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
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Starshot.Features.Background;
using Starshot.Features.Screenshot;
using Starshot.Features.Setting;
using Starshot.Features.Update;
using Starshot.Features.WebUI;
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
    private bool _pendingWorkspace;
    internal void ShowWorkspaceCapture()
    {
        _pendingWorkspace=true;Activate();
        AppConfig.GetLogger<MainWindow>().LogInformation("Workspace capture queued: frontendReady={Ready}",_ready);
        if(_ready){_pendingWorkspace=false;Emit("workspace.capture",new{});}
    }
    private string? _loadingOcrText;
    private readonly bool _isOcrPopup;
    private readonly Stopwatch _uiStartup = Stopwatch.StartNew();
    private CancellationTokenSource? _hiddenLifetime;
    private nint _font;
    private string _loadMessage = "正在打开创作台…";
    private static JsonElement? _workspaceDraft;
    private static event Action? SettingsChanged;
    internal static void BroadcastSettingsChanged() => SettingsChanged?.Invoke();
    private void RefreshSettings() { ApplyNativeTheme(); Emit("settings.changed", new { }); }

    public MainWindow(bool ocrPopup = false)
    {
        _isOcrPopup = ocrPopup;
        if (ocrPopup) _loadMessage = "正在准备文字…";
        try
        {
            var area = DisplayArea.Primary.WorkArea;
            Create(ocrPopup ? "Starshot · 文字识别" : "Starshot · 创作台", area.X, area.Y, 1, 1,
                style: 0x02CF0000, extendedStyle: ocrPopup ? 0x80u : 0u, excludeFromCapture: false);
            _windowId = Win32Interop.GetWindowIdFromWindow(Hwnd);
            AppWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(_windowId);
            if (!ocrPopup) WindowEx.RegisterMainWindow(_windowId);
            var width = (int)((ocrPopup ? 460 : 1180) * UIScale);
            var height = (int)((ocrPopup ? 300 : 760) * UIScale);
            AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2,
                area.Y + (area.Height - height) / 2, width, height));
            nint module = Kernel32.GetModuleHandle(null).DangerousGetHandle();
            nint icon = User32.LoadIcon(module, "#32512").DangerousGetHandle();
            AppWindow.SetIcon(Win32Interop.GetIconIdFromIcon(icon));
            _font = CpuCaptureGdi.Font((int)(15 * UIScale));
            ApplyNativeTheme();
            SettingsChanged += RefreshSettings;
            if (!ocrPopup)
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
        CpuCaptureNative.DwmSetWindowAttribute(Hwnd, 20, ref dark, 4);
        if (_controller is { } controller)
            controller.DefaultBackgroundColor = dark == 1 ? Windows.UI.Color.FromArgb(255, 24, 27, 25)
                : Windows.UI.Color.FromArgb(255, 245, 246, 239);
    }

    private async Task InitializeWebUiAsync()
    {
        if (ForceExit || _webUiClosed || _initializing || _bridge is not null) return;
        _initializing = true;
        try
        {
            string assets = Path.Combine(AppContext.BaseDirectory, "WebUI");
            if (!File.Exists(Path.Combine(assets, "index.html"))) throw new FileNotFoundException("界面文件缺失，请解压完整发布包。按 F5 重试。");
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null,
                Path.Combine(AppConfig.CacheFolder, "webview2"), new CoreWebView2EnvironmentOptions());
            if (ForceExit || _webUiClosed) return;
            LogPopupStartup("environment ready");
            var controller = await environment.CreateCoreWebView2ControllerAsync(
                CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)Hwnd));
            if (ForceExit || _webUiClosed) { controller.Close(); return; }
            _controller = controller;
            LogPopupStartup("controller ready");
            controller.ShouldDetectMonitorScaleChanges = true;
            controller.BoundsMode = CoreWebView2BoundsMode.UseRawPixels;
            UpdateWebBounds(); ApplyNativeTheme();
            controller.IsVisible = (!_isOcrPopup || _ready) && AppWindow.IsVisible;
            var core = _core = controller.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = AppConfig.DevMode;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.SetVirtualHostNameToFolderMapping("starshot.local", assets, CoreWebView2HostResourceAccessKind.Deny);
            core.NavigationStarting += (_, e) => { if (!WebUiBridge.IsTrustedSource(e.Uri)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.ProcessFailed += CoreProcessFailed;
            _bridge = new WebUiBridge(this, core);
            core.Navigate("https://starshot.local/index.html" + (_isOcrPopup ? "?surface=ocr" : ""));
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
        if (Hwnd != 0 && _controller is { } controller && CpuCaptureNative.GetClientRect(Hwnd, out var r))
            controller.Bounds = new Windows.Foundation.Rect(0, 0, Math.Max(1, r.Right), Math.Max(1, r.Bottom));
    }

    private void CoreProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (ForceExit) return;
        _webFocus.Cancel();
        _ready = false; _loadMessage = "界面进程已停止。按 F5 重试，托盘与快捷键仍可使用。";
        if (_controller is { } controller) controller.IsVisible = false;
        Redraw(); AppConfig.GetLogger<MainWindow>().LogError("WebView2 process failed: {Kind}", e.ProcessFailedKind);
    }

    internal void FrontendReady()
    {
        _ready = true;
        _loadingOcrText = null;
        LogPopupStartup("frontend ready");
        if (_controller is { } controller) controller.IsVisible = AppWindow.IsVisible;
        if (!_isOcrPopup && _workspaceDraft is { } workspace) Emit("workspace.restore", workspace);
        if (!_isOcrPopup) { InAppToast.FlushPending(); HotkeyManager.ShowRegistrationErrors(); }
        if (!_isOcrPopup && !_startupChecked) { _startupChecked = true; _ = TryCheckUpdateOnStartupAsync(); }
        if (_pendingOcr is { } data) { _pendingOcr = null; Emit("ocr.result", data); }
        if (_pendingWorkspace) { _pendingWorkspace=false; Emit("workspace.capture",new{}); }
        RequestWebFocus();
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

    internal void ShowOcr(IReadOnlyList<OcrLine> lines, bool translate)
    {
        if (!_isOcrPopup) { _ = new OcrResultWindow(lines, translate); return; }
        string text = OcrTextFormatter.Paragraphs(lines);
        if (!AppWindow.IsVisible)
        {
            // Short snippets should not open a document-sized window. Only size a
            // newly shown popup, preserving the user's size while it stays open.
            int wraps = 0;
            foreach (string line in text.Split('\n')) wraps += Math.Max(1, (line.Length + 27) / 28);
            var area = DisplayArea.GetFromWindowId(_windowId, DisplayAreaFallback.Primary).WorkArea;
            int width = Math.Min((int)(460 * UIScale), area.Width);
            int height = Math.Min((int)(Math.Clamp(180 + wraps * 23, 250, 430) * UIScale), area.Height);
            AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2,
                area.Y + (area.Height - height) / 2, width, height));
        }
        if (!_ready) _loadingOcrText = text.Length > 16384 ? text[..16384] : text;
        bool autoCopied = false; string? copyError = null;
        if (AppConfig.AutoCopyOcrText)
        {
            try { ClipboardHelper.SetText(text); autoCopied = true; }
            catch { copyError = "自动复制失败，可以点击复制按钮重试。"; }
        }
        object data = new { text, raw = OcrTextFormatter.Lines(lines), lineCount = lines.Count,
            translate, autoCopied, copyError, id = Guid.NewGuid().ToString() };
        if (_ready) Emit("ocr.result", data); else _pendingOcr = data;
        Activate();
        if (!_ready) Redraw();
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
        AppWindow.Show(true);
        if (_controller is { } controller) { UpdateWebBounds(); controller.IsVisible = !_isOcrPopup || _ready; }
    }
    public void Hide()
    {
        if (ForceExit) return;
        _webFocus.Cancel();
        if (_controller is { } controller) controller.IsVisible = false;
        AppWindow.Hide();
        WeakReferenceMessenger.Default.Send(new MainWindowStateChangedMessage { Hide = true });
        CaptureModeChanged();
    }
    public void Close() { if (!ForceExit && !_isOcrPopup) Hide(); else Dispose(); }
    private void CancelHiddenRelease()
    {
        var previous = _hiddenLifetime; _hiddenLifetime = null;
        previous?.Cancel(); previous?.Dispose();
    }
    private void CaptureModeChanged()
    {
        if (_isOcrPopup || ForceExit || AppWindow.IsVisible || AppConfig.ScreenCaptureMode != 0) return;
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
        _pendingOcr = null; _loadingOcrText = null;
        try { DetachWebUiForRelease(); }
        finally { base.Dispose(); }
    }
    protected override void OnNativeClosed()
    {
        ForceExit = true;
        if (!_isOcrPopup)
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
            case 0x10: Close(); return 0;
            case 0x14: return 1;
            case 0x05:
                UpdateWebBounds(); if (_controller is { } controller) controller.IsVisible = wparam != 1 && AppWindow.IsVisible && (!_isOcrPopup || _ready);
                return 0;
            case 0x03: _controller?.NotifyParentWindowPositionChanged(); break;
            case 0x07: RequestWebFocus(); break; // WM_SETFOCUS: never call WebView2 reentrantly.
            case 0x06: if (wparam != 0 && _ready) Emit("library.changed", new { }); break;
            case 0x24:
                var info = (User32.MINMAXINFO*)lparam;
                info->minTrackSize = new SIZE((int)((_isOcrPopup ? 360 : 760) * UIScale), (int)((_isOcrPopup ? 230 : 540) * UIScale));
                return 0;
            case 0x2E0:
                var rect = Marshal.PtrToStructure<CpuCaptureGdi.NativeRect>(lparam);
                CpuCaptureNative.SetWindowPos(Hwnd, 0, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, 0x14);
                if (_font != 0) CpuCaptureGdi.DeleteObject(_font);
                _font = CpuCaptureGdi.Font((int)(15 * UIScale));
                UpdateWebBounds(); return 0;
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
                    CpuCaptureGdi.Fill(dc, r, AppConfig.Theme == 2 ? Drawing.Color.FromArgb(24, 27, 25) : Drawing.Color.FromArgb(245, 246, 239));
                    if (!_ready && _isOcrPopup && _loadingOcrText is { } text) DrawLoadingOcr(dc, r, text);
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
        int padding = (int)(22 * UIScale), header = (int)(42 * UIScale);
        var foreground = AppConfig.Theme == 2 ? Drawing.Color.White : Drawing.Color.FromArgb(36, 43, 36);
        CpuCaptureGdi.Text(dc, "原文 · 正在准备编辑器…", new Drawing.Rectangle(padding, padding, Math.Max(1, bounds.Width - padding * 2), header), _font, foreground);
        nint oldFont = CpuCaptureGdi.SelectObject(dc, _font);
        int oldMode = SetLoadingBkMode(dc, 1);
        uint oldColor = SetLoadingTextColor(dc, (uint)(foreground.R | foreground.G << 8 | foreground.B << 16));
        try
        {
            var rect = new CpuCaptureGdi.NativeRect(Drawing.Rectangle.FromLTRB(padding, padding + header, Math.Max(padding + 1, bounds.Right - padding), Math.Max(padding + header + 1, bounds.Bottom - padding)));
            DrawLoadingText(dc, text, text.Length, ref rect, 0x10 | 0x800); // DT_WORDBREAK | DT_NOPREFIX
        }
        finally { SetLoadingTextColor(dc, oldColor); SetLoadingBkMode(dc, oldMode); CpuCaptureGdi.SelectObject(dc, oldFont); }
    }

    [DllImport("gdi32.dll", EntryPoint = "SetBkMode")] private static extern int SetLoadingBkMode(nint dc, int mode);
    [DllImport("gdi32.dll", EntryPoint = "SetTextColor")] private static extern uint SetLoadingTextColor(nint dc, uint color);
    [DllImport("user32.dll", EntryPoint = "DrawTextW", CharSet = CharSet.Unicode)] private static extern int DrawLoadingText(nint dc, string text, int count, ref CpuCaptureGdi.NativeRect rect, uint format);
}
