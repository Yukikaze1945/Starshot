using System;
using System.Collections.Generic;
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
    private IDisposable? _toastHost;
    private bool _initializing, _ready, _startupChecked, _webUiClosed;
    private object? _pendingOcr;
    private readonly bool _isOcrPopup;
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
        try
        {
            var area = DisplayArea.Primary.WorkArea;
            Create(ocrPopup ? "Starshot · 文字识别" : "Starshot · 创作台", area.X, area.Y, 1, 1,
                style: 0x02CF0000, extendedStyle: ocrPopup ? 0x80u : 0u, excludeFromCapture: false);
            _windowId = Win32Interop.GetWindowIdFromWindow(Hwnd);
            AppWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(_windowId);
            if (!ocrPopup) WindowEx.RegisterMainWindow(_windowId);
            var width = (int)((ocrPopup ? 720 : 1180) * UIScale);
            var height = (int)((ocrPopup ? 560 : 760) * UIScale);
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
            var controller = await environment.CreateCoreWebView2ControllerAsync(
                CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)Hwnd));
            if (ForceExit || _webUiClosed) { controller.Close(); return; }
            _controller = controller;
            controller.ShouldDetectMonitorScaleChanges = true;
            controller.BoundsMode = CoreWebView2BoundsMode.UseRawPixels;
            UpdateWebBounds(); ApplyNativeTheme();
            controller.IsVisible = AppWindow.IsVisible;
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

    private void UpdateWebBounds()
    {
        if (Hwnd != 0 && _controller is { } controller && CpuCaptureNative.GetClientRect(Hwnd, out var r))
            controller.Bounds = new Windows.Foundation.Rect(0, 0, Math.Max(1, r.Right), Math.Max(1, r.Bottom));
    }

    private void CoreProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (ForceExit) return;
        _ready = false; _loadMessage = "界面进程已停止。按 F5 重试，托盘与快捷键仍可使用。";
        if (_controller is { } controller) controller.IsVisible = false;
        Redraw(); AppConfig.GetLogger<MainWindow>().LogError("WebView2 process failed: {Kind}", e.ProcessFailedKind);
    }

    internal void FrontendReady()
    {
        _ready = true;
        if (_controller is { } controller) controller.IsVisible = AppWindow.IsVisible;
        if (!_isOcrPopup && _workspaceDraft is { } workspace) Emit("workspace.restore", workspace);
        if (!_isOcrPopup) { InAppToast.FlushPending(); HotkeyManager.ShowRegistrationErrors(); }
        if (!_isOcrPopup && !_startupChecked) { _startupChecked = true; _ = TryCheckUpdateOnStartupAsync(); }
        if (_pendingOcr is { } data) { _pendingOcr = null; Emit("ocr.result", data); }
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
    public void Activate() { Show(); CpuCaptureNative.SetForegroundWindow(Hwnd); _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); }
    public void Show()
    {
        if (ForceExit) return;
        CancelHiddenRelease(); _core?.Resume();
        AppWindow.Show(true);
        if (_controller is { } controller) { UpdateWebBounds(); controller.IsVisible = true; }
    }
    public void Hide()
    {
        if (ForceExit) return;
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
                UpdateWebBounds(); if (_controller is { } controller) controller.IsVisible = wparam != 1 && AppWindow.IsVisible;
                return 0;
            case 0x03: _controller?.NotifyParentWindowPositionChanged(); break;
            case 0x07: if (_ready) _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); break;
            case 0x06: if (wparam != 0 && _ready) Emit("library.changed", new { }); break;
            case 0x24:
                var info = (User32.MINMAXINFO*)lparam;
                info->minTrackSize = new SIZE((int)((_isOcrPopup ? 560 : 760) * UIScale), (int)((_isOcrPopup ? 420 : 540) * UIScale));
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
                    if (!_ready) CpuCaptureGdi.Text(dc, _loadMessage, new Drawing.Rectangle(0, r.Height / 2 - 20, r.Width, 40), _font,
                        AppConfig.Theme == 2 ? Drawing.Color.White : Drawing.Color.FromArgb(36, 43, 36), center: true);
                }
                finally { CpuCaptureNative.EndPaint(Hwnd, ref paint); }
                return 0;
        }
        return null;
    }
}
