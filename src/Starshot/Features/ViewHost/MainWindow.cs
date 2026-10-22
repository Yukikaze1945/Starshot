using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Starshot.Features.Background;
using Starshot.Features.Screenshot;
using Starshot.Features.Setting;
using Starshot.Features.Update;
using Starshot.Features.WebUI;
using Starshot.Features.Workspace;
using Starshot.Frameworks;
using Starshot.Helpers;
using Vanara.PInvoke;
using Windows.Graphics;
using Drawing = System.Drawing;

namespace Starshot.Features.ViewHost;

/// <summary>Windowed WebView2 directly in an HWND. No main-window XAML/compositor scene.</summary>
public sealed class MainWindow : CpuCaptureNativeWindow
{
    public bool ForceExit;
    public nint WindowHandle => Hwnd;
    public AppWindow AppWindow { get; }
    public DispatcherQueue DispatcherQueue { get; } = DispatcherQueue.GetForCurrentThread();
    public double UIScale => Hwnd == 0 ? 1 : CpuCaptureNative.GetDpiForWindow(Hwnd) / 96d;
    public event EventHandler? Closed;
    private readonly WindowId _windowId;
    private WebUiBridge? _bridge;
    private CoreWebView2? _core;
    private CoreWebView2Controller? _controller;
    private readonly DeferredWebFocus _webFocus=new();
    private IDisposable? _toastHost;
    private bool _initializing, _ready, _startupChecked, _webUiClosed;
    private object? _pendingOcr;
    private string? _pendingOcrId;
    private bool _ocrContentReady;
    private bool _ocrShowWhenReady;
    private bool _darkTheme;
    private bool CanShowWebUi => _ready && (!_isOcrPopup || _ocrContentReady);
    // Weak owners only: closing the last controller still releases the browser.
    private static readonly List<WeakReference<MainWindow>> WebHosts = new();
    private WorkspaceDocument? _pendingCapture;
    internal bool IsCaptureEditor { get; }
    private readonly bool _formulaEditor;
    private readonly bool _subtitlePopup;
    private static MainWindow? _subtitlePanel;
    internal static bool SubtitlePanelTopmost { get; private set; } = true;
    internal static void SetSubtitlePanelTopmost(bool value)
    {
        if(_subtitlePanel?.AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop=value;
        SubtitlePanelTopmost=value;
    }
    private bool IsUtility => _isOcrPopup || IsCaptureEditor || _subtitlePopup;
    internal static void OpenSubtitlePanel(Drawing.Rectangle? selection=null)
    {
        if(_subtitlePanel is null)
        {
            var panel=_subtitlePanel=new MainWindow(subtitlePopup:true);
            SetSubtitlePanelTopmost(SubtitlePanelTopmost);
            panel.Closed+=(_,_)=>{if(ReferenceEquals(_subtitlePanel,panel)){_subtitlePanel=null;Features.Subtitles.SubtitleController.PanelClosed();}};
        }
        if(selection is{} region)
        {
            var monitors=RegionToolbarMonitors.Read(new Drawing.RectangleF(region.X,region.Y,region.Width,region.Height),(float)_subtitlePanel.UIScale);
            if(monitors.Count>0)
            {
                var layout=RegionToolbarGeometry.Arrange(region,monitors,new RegionToolbarState(),RegionCaptureAction.Subtitles);
                float s=layout.Monitor.Scale,w=420*s,h=400*s;
                var area=layout.Monitor.WorkArea;
                float x=region.Right+12*s+w<=area.Right?region.Right+12*s:region.Left-w-12*s;
                float y=region.Bottom+8*s+h<=area.Bottom?region.Bottom+8*s:region.Top-h-8*s;
                var bounds=Drawing.Rectangle.Round(RegionToolbarGeometry.ClampPanel(new(x,y,w,h),monitors));
                _subtitlePanel.AppWindow.MoveAndResize(new(bounds.X,bounds.Y,bounds.Width,bounds.Height));
            }
        }
        _subtitlePanel.Activate();
    }
    internal static void HideSubtitlePanel()=>_subtitlePanel?.Hide();
    internal static void CloseSubtitlePanel(){var panel=_subtitlePanel;if(panel is not null){panel.ForceExit=true;panel.Close();}}
    internal WorkspaceDocument? TakeCaptureDocument() => Interlocked.Exchange(ref _pendingCapture, null);
    internal static void OpenCaptureEditor(WorkspaceDocument document)
    {
        MainWindow? editor=null;
        try
        {
            editor=new MainWindow(captureEditor:true,formulaEditor:document.InitialTool=="formula");
            editor._pendingCapture=document;
            AppConfig.GetLogger<MainWindow>().LogInformation("Capture editor opened: tool={Tool}, input={Width}x{Height}; gallery untouched",
                document.InitialTool,document.Width,document.Height);
            editor.Activate();
        }
        catch { editor?.Dispose();document.Dispose();throw; }
    }
    private string? _loadingOcrText;
    private readonly bool _isOcrPopup;
    private static int OcrCascade;
    private readonly int _ocrCascade;
    private readonly Stopwatch _uiStartup = Stopwatch.StartNew();
    private readonly Stopwatch _ocrDelivery = new();
    private CancellationTokenSource? _hiddenLifetime;
    private nint _font;
    private RectInt32 _headerInputBounds;
    private ChromeSubclassProc? _chromeInputProc;
    private const nuint ChromeInputSubclassId = 0x53534844;
    private string _loadMessage = "正在打开创作台…";
    private static JsonElement? _workspaceDraft;
    private static event Action? SettingsChanged;
    internal static void BroadcastSettingsChanged() => SettingsChanged?.Invoke();
    private void RefreshSettings() { ApplyNativeTheme(); Emit("settings.changed", new { }); }

    public MainWindow(bool ocrPopup = false, bool captureEditor = false, bool formulaEditor = false,bool subtitlePopup=false)
    {
        _formulaEditor=formulaEditor;
        _subtitlePopup=subtitlePopup;
        _isOcrPopup = ocrPopup;
        _ocrCascade=ocrPopup?(Interlocked.Increment(ref OcrCascade)-1)%6:0;
        IsCaptureEditor = captureEditor;
        if (ocrPopup) _loadMessage = "正在准备文字…";
        if (subtitlePopup) _loadMessage = "正在打开字幕摘录…";
        if (captureEditor) _loadMessage = formulaEditor?"正在准备公式…":"正在准备当前选区…";
        try
        {
            var area = DisplayArea.Primary.WorkArea;
            Create(subtitlePopup?"Starshot · 实时字幕摘录":formulaEditor?"Starshot · 公式识别":ocrPopup ? "Starshot · 文字识别" : captureEditor ? "Starshot · 选区编辑" : "Starshot · 创作台", area.X, area.Y, 1, 1,
                style: 0x02CF0000, extendedStyle: IsUtility ? 0x80u : 0u, excludeFromCapture: captureEditor||subtitlePopup);
            _windowId = Win32Interop.GetWindowIdFromWindow(Hwnd);
            AppWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(_windowId);
            bool integrated = WindowChrome.Configure(AppWindow, compact: ocrPopup||formulaEditor||subtitlePopup);
            // HTML controls need native passthrough as well as CSS no-drag.
            // Clearing drag rectangles alone leaves the system title strip hit-testable.
            if (integrated) AppWindow.TitleBar.SetDragRectangles([]);
            AppWindow.Changed += AppWindowChanged;
            if (!IsUtility) WindowEx.RegisterMainWindow(_windowId);
            var width = Math.Min(area.Width,(int)((subtitlePopup?420:formulaEditor ? 680 : ocrPopup ? 460 : captureEditor ? 1060 : 1180) * UIScale));
            var height = Math.Min(area.Height,(int)((subtitlePopup?400:formulaEditor ? 460 : ocrPopup ? 300 : captureEditor ? 700 : 760) * UIScale));
            int step=(int)(24*UIScale)*_ocrCascade;
            AppWindow.MoveAndResize(new RectInt32(
                Math.Clamp(area.X+(area.Width-width)/2+step,area.X,area.X+area.Width-width),
                Math.Clamp(area.Y+(area.Height-height)/2+step,area.Y,area.Y+area.Height-height),width,height));
            UpdateWebBounds();
            // Install after AppWindow's subclass: its caption handler otherwise
            // consumes WM_NCHITTEST before our base HWND procedure can see it.
            _chromeInputProc = HeaderInputProcedure;
            if (!SetWindowSubclass(Hwnd,_chromeInputProc,ChromeInputSubclassId,0))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            nint module = Kernel32.GetModuleHandle(null).DangerousGetHandle();
            nint icon = User32.LoadIcon(module, "#32512").DangerousGetHandle();
            AppWindow.SetIcon(Win32Interop.GetIconIdFromIcon(icon));
            _font = CpuCaptureGdi.Font((int)((_isOcrPopup?14:15) * UIScale));
            ApplyNativeTheme();
            SettingsChanged += RefreshSettings;
            if (!IsUtility)
            {
                CaptureModeController.ModeChanged += CaptureModeChanged;
                _toastHost = InAppToast.UseWebUiHost(Notify);
            }
            DispatcherQueue.TryEnqueue(async () => await InitializeWebUiAsync());
        }
        catch { Dispose(); throw; }
    }

    private void ApplyNativeTheme()
    {
        if (Hwnd == 0) return;
        uint dark = AppConfig.Theme == 2 ? 1u : 0u;
        if (AppConfig.Theme == 0)
            dark = (int?)Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0 ? 1u : 0u;
        _darkTheme=dark==1;
        CpuCaptureNative.DwmSetWindowAttribute(Hwnd, 20, ref dark, 4);
        WindowChrome.ApplyTheme(AppWindow, dark == 1);
        if (_controller is { } controller)
            controller.DefaultBackgroundColor = _isOcrPopup
                ? (_darkTheme?Windows.UI.Color.FromArgb(255,32,37,31):Windows.UI.Color.FromArgb(255,244,243,236))
                : dark == 1 ? Windows.UI.Color.FromArgb(255, 24, 27, 25) : Windows.UI.Color.FromArgb(255, 245, 246, 239);
    }

    private async Task InitializeWebUiAsync()
    {
        if (ForceExit || _webUiClosed || _initializing || _bridge is not null) return;
        _initializing = true;
        try
        {
            string assets = Path.Combine(AppContext.BaseDirectory, "WebUI");
            if (!File.Exists(Path.Combine(assets, "index.html"))) throw new FileNotFoundException("界面文件缺失，请解压完整发布包。按 F5 重试。");
            CoreWebView2Environment? environment=null;
            WebHosts.RemoveAll(reference=>!reference.TryGetTarget(out var host)||host._webUiClosed);
            foreach(var reference in WebHosts)
                if(reference.TryGetTarget(out var host)&&host._ready&&host._core is{} running)
                {environment=running.Environment;break;}
            bool reused=environment is not null;
            environment ??= await CoreWebView2Environment.CreateWithOptionsAsync(null,
                Path.Combine(AppConfig.CacheFolder, "webview2"), new CoreWebView2EnvironmentOptions());
            if (ForceExit || _webUiClosed) return;
            LogPopupStartup(reused?"environment reused":"environment ready");
            var controller = await environment.CreateCoreWebView2ControllerAsync(
                CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)Hwnd));
            if (ForceExit || _webUiClosed) { controller.Close(); return; }
            _controller = controller;
            LogPopupStartup("controller ready");
            controller.ShouldDetectMonitorScaleChanges = true;
            controller.BoundsMode = CoreWebView2BoundsMode.UseRawPixels;
            UpdateWebBounds(); ApplyNativeTheme();
            controller.IsVisible = (!IsUtility || CanShowWebUi) && AppWindow.IsVisible;
            var core = _core = controller.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = AppConfig.DevMode;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsNonClientRegionSupportEnabled = true;
            core.SetVirtualHostNameToFolderMapping("starshot.local", assets, CoreWebView2HostResourceAccessKind.Deny);
            core.NavigationStarting += (_, e) => { if (!WebUiBridge.IsTrustedSource(e.Uri)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.ProcessFailed += CoreProcessFailed;
            _bridge = new WebUiBridge(this, core);
            WebHosts.Add(new(this));
            core.Navigate("https://starshot.local/index.html" + (_subtitlePopup?"?surface=subtitles":_formulaEditor ? "?surface=formula" : _isOcrPopup ? "?surface=ocr" : IsCaptureEditor ? "?surface=capture" : ""));
        }
        catch (Exception ex)
        {
            try { CloseWebController(); }
            catch (Exception cleanup) { AppConfig.GetLogger<MainWindow>().LogWarning(cleanup, "Close partial WebUI controller"); }
            if (ForceExit || _webUiClosed) return;
            _loadMessage = ex.Message + " · 按 F5 重试"; Redraw();
            AppConfig.GetLogger<MainWindow>().LogError(ex, "Initialize native WebUI");
        }
        finally { _initializing = false; }
    }

    private void LogPopupStartup(string stage)
    {
        if (_isOcrPopup) AppConfig.GetLogger<MainWindow>().LogInformation("OCR popup {Stage}: {Elapsed} ms", stage, _uiStartup.ElapsedMilliseconds);
    }

    private void UpdateWebBounds()
    {
        if (Hwnd == 0 || !CpuCaptureNative.GetClientRect(Hwnd, out var r)) return;
        if (AppWindow.TitleBar.ExtendsContentIntoTitleBar)
        {
            // Pass pointer input through the AppWindow title strip to WebView2.
            // WebView2 still implements HTML app-region:drag for blank header areas;
            // Windows retains its real caption controls in RightInset.
            int width = Math.Max(0, r.Right - AppWindow.TitleBar.RightInset);
            int top = Math.Max(1, (int)Math.Ceiling(2 * UIScale)); // Keep the top resize edge native.
            int bottom = Math.Min(r.Bottom, (int)Math.Ceiling(((_isOcrPopup||_formulaEditor||_subtitlePopup) ? 32 : 48) * UIScale));
            _headerInputBounds = width > 0 && bottom > top ? new RectInt32(0, top, width, bottom - top) : default;
            InputNonClientPointerSource.GetForWindowId(_windowId).SetRegionRects(NonClientRegionKind.Passthrough,
                _headerInputBounds.Width > 0 ? [_headerInputBounds] : []);
        }
        if (_controller is { } controller)
            controller.Bounds = new Windows.Foundation.Rect(0, 0, Math.Max(1, r.Right), Math.Max(1, r.Bottom));
    }

    private void CoreProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (ForceExit) return;
        _webFocus.Cancel();
        _ocrContentReady=false;
        _ready = false; _loadMessage = "界面进程已停止。按 F5 重试，托盘与快捷键仍可使用。";
        if (_controller is { } controller) controller.IsVisible = false;
        Redraw(); AppConfig.GetLogger<MainWindow>().LogError("WebView2 process failed: {Kind}", e.ProcessFailedKind);
    }

    internal object? PendingOcr => _isOcrPopup ? _pendingOcr : null;
    internal void FrontendReady(string? ocrResultId=null)
    {
        _ready = true;
        if(_isOcrPopup&&ocrResultId is not null&&ocrResultId==_pendingOcrId)
        {
            _ocrContentReady=true;_pendingOcr=null;_pendingOcrId=null;_loadingOcrText=null;
            LogPopupStartup("editor content ready");
            if(_ocrDelivery.IsRunning)
            {
                _ocrDelivery.Stop();
                AppConfig.GetLogger<MainWindow>().LogInformation("OCR popup result to editor: {Elapsed} ms",_ocrDelivery.ElapsedMilliseconds);
            }
            if(_ocrShowWhenReady){_ocrShowWhenReady=false;Activate();}
        }
        LogPopupStartup("frontend ready");
        if (_controller is { } controller) controller.IsVisible = AppWindow.IsVisible && CanShowWebUi;
        if (!IsUtility && _workspaceDraft is { } workspace) Emit("workspace.restore", workspace);
        if (!IsUtility) { InAppToast.FlushPending(); HotkeyManager.ShowRegistrationErrors(); }
        if (!IsUtility && !_startupChecked) { _startupChecked = true; _ = TryCheckUpdateOnStartupAsync(); }
        if (_pendingOcr is { } data) Emit("ocr.result", data);
        RequestWebFocus();
        PublishChrome();
    }

    internal object ChromeState() => new {
        enabled = AppWindow.TitleBar.ExtendsContentIntoTitleBar,
        inset = AppWindow.TitleBar.RightInset / UIScale,
        height = (_isOcrPopup||_formulaEditor||_subtitlePopup) ? 32 : 48,
        active = CpuCaptureNative.GetForegroundWindow() == Hwnd,
        maximized = AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Maximized
    };
    private void PublishChrome() => Emit("window.chrome", ChromeState());
    private void AppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (ForceExit || _webUiClosed) return;
        UpdateWebBounds(); PublishChrome();
    }

    private static async Task TryCheckUpdateOnStartupAsync()
    {
#if !DEBUG
        try
        {
            if (!AppConfig.EnableAutoUpdateCheck) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now - AppConfig.LastUpdateCheckTime < 86400) return;
            var (release, _) = await UpdateService.CheckUpdateAsync(); AppConfig.LastUpdateCheckTime = now;
            if (release is not null) new UpdateWindow().SetRelease(release);
        }
        catch { }
#endif
    }

    internal void ShowOcr(IReadOnlyList<OcrLine> lines, bool translate,bool autoCopyAllowed=true)
    {
        if (!_isOcrPopup) { _ = new OcrResultWindow(lines, translate); return; }
        _ocrDelivery.Restart();
        string text = OcrTextFormatter.Paragraphs(lines);
        if(!AppWindow.IsVisible||CpuCaptureNative.IsIconic(Hwnd))_ocrContentReady=false;
        if (!_ocrContentReady) _loadingOcrText = text.Length > 16384 ? text[..16384] : text;
        EnsureOcrBounds(text);
        bool autoCopied = false; string? copyError = null;
        if (autoCopyAllowed&&AppConfig.AutoCopyOcrText)
        {
            try { ClipboardHelper.SetText(text); autoCopied = true; }
            catch { copyError = "自动复制失败，可以点击复制按钮重试。"; }
        }
        _pendingOcrId=Guid.NewGuid().ToString();
        object data = new { text, raw = OcrTextFormatter.Lines(lines), lineCount = lines.Count,
            translate, autoCopied, copyError, id = _pendingOcrId };
        _pendingOcr=data;
        if (_ready) Emit("ocr.result", data);
        LogPopupStartup("result queued");
        // A prepared browser normally commits in one message turn. Show that
        // complete editor once, rather than flashing a native preview for 20ms.
        _ocrShowWhenReady=_ready&&!AppWindow.IsVisible;
        if(_ocrShowWhenReady)_=ShowOcrPreviewIfSlowAsync(_pendingOcrId);
        else Activate();
        if (!_ocrContentReady)
        {
            Redraw();
            // WM_PAINT is otherwise lower priority than queued browser startup.
            // Paint the CPU text now, before WebView2 initialization can run.
            if(AppWindow.IsVisible)CpuCaptureNative.UpdateWindow(Hwnd);
        }
    }

    private async Task ShowOcrPreviewIfSlowAsync(string id)
    {
        await Task.Delay(120);
        if(ForceExit||_webUiClosed||!_ocrShowWhenReady||_pendingOcrId!=id)return;
        _ocrShowWhenReady=false;Activate();Redraw();CpuCaptureNative.UpdateWindow(Hwnd);
    }

    private void EnsureOcrBounds(string text)
    {
        bool minimized=AppWindow.Presenter is OverlappedPresenter {State:OverlappedPresenterState.Minimized};
        if(minimized)
        {
            ((OverlappedPresenter)AppWindow.Presenter).Restore(false);
            AppConfig.GetLogger<MainWindow>().LogInformation("OCR popup restored from minimized state");
        }
        var area=DisplayArea.GetFromWindowId(_windowId,DisplayAreaFallback.Primary).WorkArea;
        var bounds=new Drawing.Rectangle(AppWindow.Position.X,AppWindow.Position.Y,AppWindow.Size.Width,AppWindow.Size.Height);
        var visible=Drawing.Rectangle.Intersect(bounds,new(area.X,area.Y,area.Width,area.Height));
        if (!AppWindow.IsVisible||bounds.Width<360*UIScale||bounds.Height<230*UIScale||visible.Width<80*UIScale||visible.Height<50*UIScale)
        {
            // Short snippets should not open a document-sized window. Only size a
            // newly shown popup, preserving the user's size while it stays open.
            int wraps = 0;
            foreach (string line in text.Split('\n')) wraps += Math.Max(1, (line.Length + 27) / 28);
            int width = Math.Min((int)(460 * UIScale), area.Width);
            int height = Math.Min((int)(Math.Clamp(180 + wraps * 23, 250, 430) * UIScale), area.Height);
            int step=(int)(24*UIScale)*_ocrCascade;
            AppWindow.MoveAndResize(new RectInt32(
                Math.Clamp(area.X+(area.Width-width)/2+step,area.X,area.X+area.Width-width),
                Math.Clamp(area.Y+(area.Height-height)/2+step,area.Y,area.Y+area.Height-height),width,height));
            AppConfig.GetLogger<MainWindow>().LogInformation("OCR popup bounds normalized: {Width}x{Height}, scale={Scale}; previous={Previous}",width,height,UIScale,bounds);
        }
    }

    internal void Emit(string name, object data)
    {
        if (!_ready || ForceExit) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_ready && !ForceExit && _core is { } core)
                core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "event", name, data }));
        });
    }

    private void Notify(InfoBarSeverity severity, string? title, string? message, int duration,
        string? button, Action? action, Action? closed)
    {
        if (ForceExit) return;
        if (action is null) Emit("notice", new { text = string.Join(" ", new[] { title, message }),
            error = severity is InfoBarSeverity.Error or InfoBarSeverity.Warning, duration });
        else
        {
            var r = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            _ = new CpuCaptureNotice(title ?? message ?? "Starshot", null,
                new Drawing.Rectangle(r.X, r.Y, r.Width, r.Height), subtitle: button,
                action: action, closedAction: closed, duration: duration);
        }
    }

    public void ApplyTheme() => RefreshSettings();
    public void ApplyBackdrop() { }
    public void Activate()
    {
        if(ForceExit || _webUiClosed || Hwnd==0)return;
        Show();CpuCaptureNative.SetForegroundWindow(Hwnd);RequestWebFocus();
    }
    private void RequestWebFocus()
    {
        if(ForceExit || _webUiClosed || !_ready || _controller is not { } controller)return;
        // Show/activation can synchronously deliver WM_SETFOCUS before the hidden
        // controller is made visible. Leave that native call stack first, then
        // check the current window/controller rather than focusing a stale one.
        _webFocus.Request(
            callback=>DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,()=>callback()),
            ()=>!ForceExit && !_webUiClosed && _ready && ReferenceEquals(controller,_controller) && Hwnd!=0
                && AppWindow.IsVisible && !CpuCaptureNative.IsIconic(Hwnd)
                && CpuCaptureNative.GetForegroundWindow()==Hwnd && controller.IsVisible
                && controller.Bounds.Width>0 && controller.Bounds.Height>0,
            ()=>controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic),
            ex=>AppConfig.GetLogger<MainWindow>().LogWarning(ex,
                "WebUI focus handoff skipped: HRESULT={HResult:X8}; capture/workspace delivery continues",ex.HResult));
    }
    public void Show()
    {
        if (ForceExit) return;
        CancelHiddenRelease(); _core?.Resume();
        if(_isOcrPopup&&AppWindow.Presenter is OverlappedPresenter {State:OverlappedPresenterState.Minimized} presenter)presenter.Restore(false);
        AppWindow.Show(true);
        if (_controller is { } controller) { UpdateWebBounds(); controller.IsVisible = !IsUtility || CanShowWebUi; }
    }
    public void Hide()
    {
        if (ForceExit) return;
        if (IsCaptureEditor) { Dispose(); return; }
        _webFocus.Cancel();
        if (_controller is { } controller) controller.IsVisible = false;
        AppWindow.Hide();
        WeakReferenceMessenger.Default.Send(new MainWindowStateChangedMessage { Hide = true });
        CaptureModeChanged();
    }
    public void Close() { if (!ForceExit && !IsUtility) Hide(); else Dispose(); }
    private void CancelHiddenRelease()
    {
        var previous = _hiddenLifetime; _hiddenLifetime = null;
        previous?.Cancel(); previous?.Dispose();
    }
    private void CaptureModeChanged()
    {
        if (IsUtility || ForceExit || AppWindow.IsVisible || AppConfig.ScreenCaptureMode != 0) return;
        CancelHiddenRelease(); _hiddenLifetime = new CancellationTokenSource();
        _ = ReleaseHiddenWebUiAsync(_hiddenLifetime.Token);
    }
    private async Task ReleaseHiddenWebUiAsync(CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(500, cancellation);
            while (!ForceExit && !AppWindow.IsVisible && AppConfig.ScreenCaptureMode == 0)
            {
                cancellation.ThrowIfCancellationRequested();
                if (_initializing || _bridge?.HasPendingRequests == true || _bridge?.HasUtilityWindows == true)
                { await Task.Delay(1000, cancellation); continue; }
                if (_ready && _core is { } core)
                {
                    string json = await core.ExecuteScriptAsync("window.starshotWorkspaceSnapshot?.() ?? null")
                        .AsTask(cancellation).WaitAsync(TimeSpan.FromSeconds(2), cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    if (json == "null") { await Task.Delay(1000, cancellation); continue; }
                    if (json.Length > 4_000_000) throw new InvalidOperationException("Workspace draft exceeds in-memory limit");
                    using var draft = JsonDocument.Parse(json);
                    if (draft.RootElement.GetProperty("version").GetInt32() != 1) throw new InvalidOperationException("Unsupported workspace draft");
                    _workspaceDraft = draft.RootElement.Clone();
                }
                if (ForceExit || AppWindow.IsVisible || AppConfig.ScreenCaptureMode != 0) return;
                AppConfig.GetLogger<MainWindow>().LogInformation("Lightweight native WebUI released; workspace retained only in RAM");
                App.Current.ReleaseMainWindow(this); return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ForceExit || cancellation.IsCancellationRequested || AppWindow.IsVisible) return;
            AppConfig.GetLogger<MainWindow>().LogWarning("WebUI release deferred: {Type}", ex.GetType().Name);
            try { if (_core is { } core) await core.TrySuspendAsync(); } catch { }
        }
    }
    internal void DetachWebUiForRelease()
    {
        _ready = false; CancelHiddenRelease();
        SettingsChanged -= RefreshSettings; CaptureModeController.ModeChanged -= CaptureModeChanged;
        _toastHost?.Dispose(); _toastHost = null;
        if (_webUiClosed) return;
        _webUiClosed = true;
        CloseWebController();
    }
    private void CloseWebController()
    {
        WebHosts.RemoveAll(reference=>!reference.TryGetTarget(out var host)||ReferenceEquals(host,this));
        _webFocus.Cancel();
        _ready = false;
        if (_core is { } core) core.ProcessFailed -= CoreProcessFailed;
        try { _bridge?.Dispose(); }
        finally
        {
            _bridge = null;
            try { _controller?.Close(); }
            finally { _controller = null; _core = null; }
        }
    }
    public override void Dispose()
    {
        ForceExit = true;
        _pendingOcr = null; _pendingOcrId=null; _loadingOcrText = null;
        TakeCaptureDocument()?.Dispose();
        try { DetachWebUiForRelease(); }
        finally { base.Dispose(); }
    }
    protected override void OnNativeClosed()
    {
        ForceExit = true;
        AppWindow.Changed -= AppWindowChanged;
        TakeCaptureDocument()?.Dispose();
        if (!IsUtility)
        {
            WindowEx.UnregisterMainWindow(_windowId);
            App.Current.ForgetMainWindow(this);
        }
        try { DetachWebUiForRelease(); }
        finally
        {
            if (_font != 0) CpuCaptureGdi.DeleteObject(_font); _font = 0;
            Closed?.Invoke(this, EventArgs.Empty); Closed = null;
        }
    }
    protected override unsafe nint? HandleMessage(uint message, nint wparam, nint lparam)
    {
        switch (message)
        {
            case 0x10:
                if(IsCaptureEditor && _ready) Emit("capture.closeRequested",new{});
                else Close();
                return 0;
            case 0x14: return 1;
            case 0x05:
                UpdateWebBounds(); if (_controller is { } controller) controller.IsVisible = wparam != 1 && AppWindow.IsVisible && (!IsUtility || CanShowWebUi);
                return 0;
            case 0x03: _controller?.NotifyParentWindowPositionChanged(); break;
            case 0x07: RequestWebFocus(); break; // WM_SETFOCUS: never call WebView2 reentrantly.
            case 0x06:
                PublishChrome();
                if (wparam != 0 && _ready) Emit("library.changed", new { });
                break;
            case 0x24:
                var info = (User32.MINMAXINFO*)lparam;
                info->minTrackSize = new SIZE((int)((_isOcrPopup ? 360 : 760) * UIScale), (int)((_isOcrPopup ? 230 : 540) * UIScale));
                return 0;
            case 0x2E0:
                var rect = Marshal.PtrToStructure<CpuCaptureGdi.NativeRect>(lparam);
                CpuCaptureNative.SetWindowPos(Hwnd, 0, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, 0x14);
                if (_font != 0) CpuCaptureGdi.DeleteObject(_font);
                _font = CpuCaptureGdi.Font((int)((_isOcrPopup?14:15) * UIScale));
                UpdateWebBounds(); PublishChrome(); return 0;
            case 0x100 when wparam == 0x74:
                if (_initializing) return 0;
                if (_ready && _core is { } core) core.Reload();
                else { CloseWebController(); _ = InitializeWebUiAsync(); }
                return 0;
            case 0x0F:
                nint dc = CpuCaptureNative.BeginPaint(Hwnd, out var paint);
                try
                {
                    CpuCaptureNative.GetClientRect(Hwnd, out var client);
                    var r = Drawing.Rectangle.FromLTRB(0, 0, client.Right, client.Bottom);
                    CpuCaptureGdi.Fill(dc, r, _isOcrPopup?(_darkTheme?Drawing.Color.FromArgb(32,37,31):Drawing.Color.FromArgb(244,243,236)):
                        _darkTheme ? Drawing.Color.FromArgb(24, 27, 25) : Drawing.Color.FromArgb(245, 246, 239));
                    if (!CanShowWebUi && _isOcrPopup && _loadingOcrText is { } text) DrawLoadingOcr(dc, r, text);
                    else if (!_ready) CpuCaptureGdi.Text(dc, _loadMessage, new Drawing.Rectangle(0, r.Height / 2 - 20, r.Width, 40), _font,
                        AppConfig.Theme == 2 ? Drawing.Color.White : Drawing.Color.FromArgb(36, 43, 36), center: true);
                }
                finally { CpuCaptureNative.EndPaint(Hwnd, ref paint); }
                return 0;
        }
        return null;
    }

    // Show recognized text immediately while the browser/editor boots. No extra
    // GPU surface or retained WebView is needed to hide its cold-start latency.
    private void DrawLoadingOcr(nint dc, Drawing.Rectangle bounds, string text)
    {
        int padding = (int)(16 * UIScale), header = (int)(80 * UIScale);
        var foreground = _darkTheme ? Drawing.Color.FromArgb(231,236,220) : Drawing.Color.FromArgb(36, 43, 36);
        CpuCaptureGdi.Fill(dc,new Drawing.Rectangle((int)(4*UIScale),(int)(36*UIScale),Math.Max(1,bounds.Width-(int)(8*UIScale)),Math.Max(1,bounds.Height-(int)(72*UIScale))),
            _darkTheme?Drawing.Color.FromArgb(40,46,37):Drawing.Color.FromArgb(250,250,246));
        CpuCaptureGdi.Text(dc, "原文", new Drawing.Rectangle(padding,(int)(36*UIScale), Math.Max(1, bounds.Width-padding*2),(int)(36*UIScale)), _font, foreground);
        nint oldFont = CpuCaptureGdi.SelectObject(dc, _font);
        int oldMode = SetLoadingBkMode(dc, 1);
        uint oldColor = SetLoadingTextColor(dc, (uint)(foreground.R | foreground.G << 8 | foreground.B << 16));
        try
        {
            var rect = new CpuCaptureGdi.NativeRect(Drawing.Rectangle.FromLTRB(padding, header, Math.Max(padding + 1, bounds.Right - padding), Math.Max(header + 1, bounds.Bottom-(int)(76*UIScale))));
            DrawLoadingText(dc, text, text.Length, ref rect, 0x10 | 0x800); // DT_WORDBREAK | DT_NOPREFIX
        }
        finally { SetLoadingTextColor(dc, oldColor); SetLoadingBkMode(dc, oldMode); CpuCaptureGdi.SelectObject(dc, oldFont); }
    }

    [DllImport("gdi32.dll", EntryPoint = "SetBkMode")] private static extern int SetLoadingBkMode(nint dc, int mode);
    [DllImport("user32.dll", EntryPoint = "ScreenToClient")] private static extern bool HeaderScreenToClient(nint hwnd,ref CpuCaptureNative.NativePoint point);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint ChromeSubclassProc(nint hwnd,uint message,nint wparam,nint lparam,nuint id,nuint data);
    [DllImport("comctl32.dll",SetLastError=true)] private static extern bool SetWindowSubclass(nint hwnd,ChromeSubclassProc proc,nuint id,nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd,ChromeSubclassProc proc,nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd,uint message,nint wparam,nint lparam);
    private nint HeaderInputProcedure(nint hwnd,uint message,nint wparam,nint lparam,nuint id,nuint data)
    {
        if(message==0x84)
        {
            var hit=new CpuCaptureNative.NativePoint{X=(short)((long)lparam&0xffff),Y=(short)(((long)lparam>>16)&0xffff)};
            if(HeaderScreenToClient(hwnd,ref hit) && hit.X>=_headerInputBounds.X && hit.Y>=_headerInputBounds.Y
                && hit.X<_headerInputBounds.X+_headerInputBounds.Width && hit.Y<_headerInputBounds.Y+_headerInputBounds.Height)
                return 1; // HTCLIENT. WebView2 retains app-region:drag for the noninteractive HTML surface.
        }
        if(message==0x82 && _chromeInputProc is {} proc)RemoveWindowSubclass(hwnd,proc,id);
        return DefSubclassProc(hwnd,message,wparam,lparam);
    }
    [DllImport("gdi32.dll", EntryPoint = "SetTextColor")] private static extern uint SetLoadingTextColor(nint dc, uint color);
    [DllImport("user32.dll", EntryPoint = "DrawTextW", CharSet = CharSet.Unicode)] private static extern int DrawLoadingText(nint dc, string text, int count, ref CpuCaptureGdi.NativeRect rect, uint format);
}
