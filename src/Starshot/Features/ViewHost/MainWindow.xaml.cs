using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Starshot.Features.Background;
using Starshot.Features.WebUI;
using Starshot.Features.Update;
using Starshot.Frameworks;
using Starshot.Helpers;
using Vanara.PInvoke;
using Windows.Graphics;

namespace Starshot.Features.ViewHost;

/// <summary>Native window lifetime and WebView2 container; product UI lives in React.</summary>
public sealed partial class MainWindow : WindowEx
{
    public bool ForceExit;
    private WebUiBridge? _bridge;
    private bool _initializing, _ready;
    private bool _startupChecked;
    private object? _pendingOcr;
    private readonly bool _isOcrPopup;
    private static event Action? SettingsChanged;
    private void RefreshSettings() => Emit("settings.changed", new { });
    internal static void BroadcastSettingsChanged() => SettingsChanged?.Invoke();

    public MainWindow(bool ocrPopup = false)
    {
        _isOcrPopup = ocrPopup;
        SettingsChanged += RefreshSettings;
        InitializeComponent();
        if (!ocrPopup) Microsoft.Xaml.Interactivity.Interaction.GetBehaviors(NativeToastHost).Add(new InAppToast { Tag = "MainWindow" });
        if (!ocrPopup) MainWindowId = AppWindow.Id;
        Title = ocrPopup ? "Starshot · 文字识别" : "Starshot · 创作台";
        SetIcon();
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        AdaptTitleBarButtonColorToActuallTheme();
        SetDragRectangles(new RectInt32(0, 0, 100000, (int)(32 * UIScale)));
        CenterInScreen(ocrPopup ? 720 : 1180, ocrPopup ? 560 : 760);
        AppWindow.Closing += (_, args) => { if (!ForceExit && !ocrPopup) { args.Cancel = true; Hide(); } };
        Closed += (_, _) => { ForceExit = true; SettingsChanged -= RefreshSettings; _bridge?.Dispose(); WebHost.Close(); };
        RootGrid.Loaded += async (_, _) => await InitializeWebUiAsync();
        Activated += (_, _) => { if (_ready) Emit("library.changed", new { }); };
    }

    private async System.Threading.Tasks.Task InitializeWebUiAsync()
    {
        if (_initializing || _bridge is not null) return;
        _initializing = true;
        try
        {
            string assets = Path.Combine(AppContext.BaseDirectory, "WebUI");
            if (!File.Exists(Path.Combine(assets, "index.html")))
                throw new FileNotFoundException("界面文件缺失。请重新构建或解压完整发布包（包含 WebUI 目录）。");
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null,
                Path.Combine(AppConfig.CacheFolder, "webview2"), new CoreWebView2EnvironmentOptions());
            await WebHost.EnsureCoreWebView2Async(environment);
            var core = WebHost.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = AppConfig.DevMode;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.SetVirtualHostNameToFolderMapping("starshot.local", assets, CoreWebView2HostResourceAccessKind.Deny);
            core.NavigationStarting += (_, e) =>
            {
                if (!WebUiBridge.IsTrustedSource(e.Uri)) e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.ProcessFailed += (_, e) =>
            {
                _ready = false;
                LoadStatus.Visibility = Visibility.Visible;
                LoadStatusText.Text = "界面进程已停止。可以重新加载，原生托盘与快捷键仍可使用。";
                RetryButton.Visibility = Visibility.Visible;
                AppConfig.GetLogger<MainWindow>().LogError("WebView2 process failed: {Kind}", e.ProcessFailedKind);
            };
            _bridge = new WebUiBridge(this, core);
            core.Navigate("https://starshot.local/index.html" + (_isOcrPopup ? "?surface=ocr" : ""));
        }
        catch (Exception ex)
        {
            LoadStatusText.Text = ex.Message;
            RetryButton.Visibility = Visibility.Visible;
            AppConfig.GetLogger<MainWindow>().LogError(ex, "Initialize WebUI");
        }
        finally { _initializing = false; }
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        RetryButton.Visibility = Visibility.Collapsed;
        if (_bridge is null) await InitializeWebUiAsync();
        else WebHost.CoreWebView2.Reload();
    }

    internal void FrontendReady()
    {
        _ready = true;
        LoadStatus.Visibility = Visibility.Collapsed;
        if (!_isOcrPopup) InAppToast.FlushPending();
        if (!_isOcrPopup && !_startupChecked) { _startupChecked = true; _ = TryCheckUpdateOnStartupAsync(); }
        if (_pendingOcr is { } data) { _pendingOcr = null; Emit("ocr.result", data); }
    }

    private static async System.Threading.Tasks.Task TryCheckUpdateOnStartupAsync()
    {
#if !DEBUG
        try
        {
            if (!AppConfig.EnableAutoUpdateCheck) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now - AppConfig.LastUpdateCheckTime < 86400) return;
            var (release, _) = await UpdateService.CheckUpdateAsync();
            AppConfig.LastUpdateCheckTime = now;
            if (release is not null) new UpdateWindow().SetRelease(release);
        }
        catch { }
#endif
    }

    internal void ShowOcr(IReadOnlyList<OcrLine> lines, bool translate)
    {
        if (!_isOcrPopup) { _ = new Starshot.Features.Screenshot.OcrResultWindow(lines, translate); return; }
        string text = OcrTextFormatter.Paragraphs(lines);
        bool autoCopied = false;
        string? copyError = null;
        if (AppConfig.AutoCopyOcrText)
        {
            try { ClipboardHelper.SetText(text); autoCopied = true; }
            catch { copyError = "自动复制失败，可以点击复制按钮重试。"; }
        }
        object data = new { text, raw = OcrTextFormatter.Lines(lines),
            lineCount = lines.Count, translate, autoCopied, copyError, id = Guid.NewGuid().ToString() };
        if (_ready) Emit("ocr.result", data); else _pendingOcr = data;
        Activate(); Show();
    }

    internal void Emit(string name, object data)
    {
        if (!_ready) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_ready && !ForceExit)
                WebHost.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "event", name, data }));
        });
    }

    public void ApplyTheme() => Emit("settings.changed", new { });
    public void ApplyBackdrop() { }
    public override void Hide()
    {
        base.Hide();
        WeakReferenceMessenger.Default.Send(new MainWindowStateChangedMessage { Hide = true });
    }

    protected override unsafe nint WindowSubclassProc(HWND hWnd, uint uMsg, nint wParam,
        nint lParam, nuint uIdSubclass, nint dwRefData)
    {
        if (uMsg == (uint)User32.WindowMessage.WM_GETMINMAXINFO)
        {
            var info = (User32.MINMAXINFO*)lParam;
            info->minTrackSize = new SIZE((int)((_isOcrPopup ? 560 : 760) * UIScale), (int)((_isOcrPopup ? 420 : 540) * UIScale));
            return 0;
        }
        return base.WindowSubclassProc(hWnd, uMsg, wParam, lParam, uIdSubclass, dwRefData);
    }
}
