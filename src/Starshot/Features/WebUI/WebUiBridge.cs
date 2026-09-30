using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Starshot.Features.Codec;
using Starshot.Features.Screenshot;
using Starshot.Features.Setting;
using Starshot.Features.ViewHost;
using Starshot.Features.Update;
using Starshot.Helpers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;

namespace Starshot.Features.WebUI;

/// <summary>Versioned, allowlisted RPC. No arbitrary paths, reflection, script or shell execution.</summary>
internal sealed class WebUiBridge : IDisposable
{
    private readonly MainWindow _window;
    private readonly CoreWebView2 _core;
    private readonly Dictionary<string, string> _files = new();
    private readonly HashSet<string> _imports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _requests = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _imageGate = new(2);
    private readonly List<Window> _utilityWindows = new();
    private bool _disposed;
    public const int ProtocolVersion = 1;
    public static bool IsTrustedSource(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "starshot.local" && uri.Port == 443;

    public WebUiBridge(MainWindow window, CoreWebView2 core)
    {
        _window = window; _core = core;
        _core.WebMessageReceived += OnMessage;
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed || !IsTrustedSource(e.Source)) return;
        string? id = null;
        CancellationTokenSource? cancellation = null;
        try
        {
            if (e.WebMessageAsJson.Length > 1_000_000) return;
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var request = document.RootElement;
            id = request.GetProperty("id").GetString();
            string? method = request.GetProperty("method").GetString();
            if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || method is null) return;
            if (request.GetProperty("version").GetInt32() != ProtocolVersion)
                throw new InvalidOperationException("界面协议版本不匹配，请更新完整安装包。");
            if (_requests.ContainsKey(id)) return;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _requests.Add(id, cancellation);
            object? result = await DispatchAsync(method, request.GetProperty("params"), cancellation.Token);
            Reply(new { type = "response", id, ok = true, result });
            if (method is "settings.set" or "settings.pickFolder" or "settings.addFolder" or "settings.removeFolder"
                or "translation.configure" or "translation.clearKey" or "hotkey.set" or "utility.ocrEngine")
                MainWindow.BroadcastSettingsChanged();
        }
        catch (OperationCanceledException) { if (id is not null) Reply(new { type = "response", id, ok = false, error = "操作已取消。" }); }
        catch (Exception ex)
        {
            // Never log message payloads: translation API keys and OCR text may be present.
            AppConfig.GetLogger<WebUiBridge>().LogWarning("WebUI request failed: {Type}, HRESULT={HResult}, at {Stack}",
                ex.GetType().Name, ex.HResult, ex.StackTrace);
            if (id is not null) Reply(new { type = "response", id, ok = false, error = ex.Message });
        }
        finally { if (id is not null && cancellation is not null) _requests.Remove(id); cancellation?.Dispose(); }
    }

    private void Reply(object message)
    {
        if (!_disposed) _core.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private async Task<object?> DispatchAsync(string method, JsonElement p, CancellationToken ct)
    {
        switch (method)
        {
            case "app.bootstrap":
                return new { version = AppConfig.AppVersion, protocol = ProtocolVersion,
                    settings = Settings(), hotkeys = Hotkeys(), oneOcrReady = OcrHelper.IsOneOcrReady,
                    hasApiKey = OcrTranslationClient.HasApiKey };
            case "app.ready": _window.FrontendReady(); return null;
            case "request.cancel":
                if (_requests.TryGetValue(Text(p, "requestId", 100), out var request)) request.Cancel();
                return null;
            case "window.hide": _window.Hide(); return null;
            case "window.close": _window.Close(); return null;
            case "capture.begin":
                string mode = Text(p, "mode", 16);
                if (mode is not ("region" or "screen" or "ocr" or "copy" or "long" or "gif"))
                    throw new ArgumentException("不支持的截图方式。");
                _window.Hide();
                await Task.Delay(180, ct);
                switch (mode)
                {
                    case "screen": ScreenCaptureService.Capture(); break;
                    case "ocr": ScreenCaptureService.CaptureRegionOcr(); break;
                    case "copy": ScreenCaptureService.CaptureRegionCopyOnly(); break;
                    default: ScreenCaptureService.CaptureRegion(); break;
                }
                return null;
            case "library.list": return await ListAsync(p, ct);
            case "library.thumbnail":
                return await ThumbnailAsync(ResolveFile(p), ct);
            case "library.import":
                var imported = await FileDialogHelper.PickMultipleFilesAsync(_window.WindowHandle,
                    ("图像", ".png"), ("图像", ".jpg"), ("图像", ".avif"), ("图像", ".jxl"), ("图像", ".webp"), ("图像", ".jxr"), ("动图", ".gif"));
                foreach (string path in imported) _imports.Add(path);
                return imported.Select(RegisterFile).ToArray();
            case "library.open":
                var viewer = new ImageViewWindow();
                await viewer.ShowWindowAsync(_window.AppWindow.Id, ResolveFile(p), true);
                return null;
            case "library.reveal":
                var file = await StorageFile.GetFileFromPathAsync(ResolveFile(p));
                var folder = await file.GetParentAsync();
                var options = new FolderLauncherOptions(); options.ItemsToSelect.Add(file);
                await Launcher.LaunchFolderAsync(folder, options); return null;
            case "library.copy":
                using (var image = await ImageLoader.LoadImageAsync(ResolveFile(p), ct))
                using (var sdr = ScreenCaptureService.TonemapToSdr(image.CanvasBitmap, 250))
                    if (!await ScreenCaptureService.CopyCaptureToClipboardAsync(sdr, true))
                        throw new InvalidOperationException("复制失败，请稍后重试。");
                return null;
            case "library.pin":
                using (var image = await ImageLoader.LoadImageAsync(ResolveFile(p), ct))
                using (var sdr = ScreenCaptureService.TonemapToSdr(image.CanvasBitmap, 250))
                {
                    var area = DisplayArea.GetFromWindowId(_window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
                    double zoom = Math.Min(1, Math.Min(area.Width * .6 / sdr.SizeInPixels.Width, area.Height * .6 / sdr.SizeInPixels.Height));
                    _ = new PinnedCaptureWindow(sdr, area.X + 80, area.Y + 80, zoom);
                }
                return null;
            case "library.ocr":
                using (var image = await ImageLoader.LoadImageAsync(ResolveFile(p), ct))
                using (var sdr = ScreenCaptureService.TonemapToSdr(image.CanvasBitmap, 250))
                {
                    var prepared = OcrHelper.PreparePixels(sdr);
                    var lines = await Task.Run(() => OcrHelper.RecognizeAsync(prepared.Pixels, prepared.Width, prepared.Height, prepared.Scale), ct);
                    if (lines is null) throw new InvalidOperationException("没有可用的 OCR 引擎，请在设置中配置。");
                    if (lines.Count == 0) throw new InvalidOperationException("这张图片中没有识别到文字。");
                    _window.ShowOcr(lines, false);
                }
                return null;
            case "clipboard.read": return await ReadClipboardAsync(ct);
            case "clipboard.readText":
                var clipboardText = Clipboard.GetContent();
                return clipboardText.Contains(StandardDataFormats.Text) ? await clipboardText.GetTextAsync() : "";
            case "clipboard.copyText": ClipboardHelper.SetText(Text(p, "text", 500_000)); return null;
            case "clipboard.copyRichText":
                var rich = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                rich.SetText(Text(p, "text", 500_000));
                rich.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(Text(p, "html", 700_000)));
                Clipboard.SetContent(rich);
                try { Clipboard.Flush(); } catch { }
                return null;
            case "clipboard.restore":
                var history = await Clipboard.GetHistoryItemsAsync();
                var entry = history.Items.FirstOrDefault(x => x.Id == Text(p, "id", 100));
                if (entry is null || Clipboard.SetHistoryItemAsContent(entry) != SetHistoryItemAsContentStatus.Success)
                    throw new InvalidOperationException("剪贴板条目已失效。");
                return null;
            case "clipboard.pin": PinnedCaptureWindow.PinClipboard(); return null;
            case "pin.reopen": PinnedCaptureWindow.ReopenLast(); return null;
            case "settings.get": return Settings();
            case "settings.set": ApplySetting(Text(p, "key", 80), p.GetProperty("value")); return Settings();
            case "settings.pickFolder":
                string? destination = await FileDialogHelper.PickFolderAsync(_window.WindowHandle);
                if (destination is not null) AppConfig.ScreenshotFolder = destination;
                return Settings();
            case "settings.addFolder":
                string? additional = await FileDialogHelper.PickFolderAsync(_window.WindowHandle);
                if (additional is not null)
                    AppConfig.ExtraScreenshotFolders = AppConfig.ExtraScreenshotFolders.Append(additional).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return Settings();
            case "settings.removeFolder":
                int index = p.GetProperty("index").GetInt32();
                var folders = AppConfig.ExtraScreenshotFolders;
                if (index < 0 || index >= folders.Count) throw new ArgumentException("文件夹已不存在，请刷新设置。");
                folders.RemoveAt(index); AppConfig.ExtraScreenshotFolders = folders;
                return Settings();
            case "settings.openFolder":
                Directory.CreateDirectory(AppConfig.ScreenshotFolder);
                await Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(AppConfig.ScreenshotFolder));
                return null;
            case "hotkey.set":
                int hotkeyId = p.GetProperty("hotkeyId").GetInt32();
                var info = HotkeyManager.GetHotkeyInfo(hotkeyId) ?? throw new ArgumentException("未知快捷键。");
                uint modifiers = p.GetProperty("modifiers").GetUInt32(), key = p.GetProperty("key").GetUInt32();
                if (modifiers > 15 || key > 255 || (key != 0 && modifiers == 0))
                    throw new ArgumentException("快捷键需要至少一个 Ctrl / Alt / Shift / Win 修饰键。");
                var oldModifiers = info.Modifiers; var oldKey = info.Key;
                HotkeyManager.UnregisterHotkey(_window.WindowHandle, hotkeyId);
                var error = key == 0 ? HotkeyManager.DeleteHotkey(_window.WindowHandle, hotkeyId) :
                    HotkeyManager.RegisterHotkey(_window.WindowHandle, hotkeyId, (Vanara.PInvoke.User32.HotKeyModifiers)modifiers, (Vanara.PInvoke.User32.VK)key);
                if (error.Failed)
                {
                    HotkeyManager.RegisterHotkey(_window.WindowHandle, hotkeyId, oldModifiers, oldKey);
                    throw new InvalidOperationException("快捷键已被占用或无法注册。已恢复原快捷键。");
                }
                return Hotkeys();
            case "translation.configure":
                string endpoint = Text(p, "endpoint", 2048), model = Text(p, "model", 200);
                if (!OcrTranslationClient.IsValidEndpoint(endpoint) || string.IsNullOrWhiteSpace(model))
                    throw new ArgumentException("请填写 HTTPS API 地址和模型名称。");
                string apiKey = Text(p, "apiKey", 4096);
                if (apiKey.Length > 0) OcrTranslationClient.SaveApiKey(apiKey);
                AppConfig.TranslationApiUrl = endpoint; AppConfig.TranslationModel = model;
                AppConfig.TranslationTargetLanguage = Text(p, "targetLanguage", 50);
                return new { hasApiKey = OcrTranslationClient.HasApiKey, settings = Settings() };
            case "translation.clearKey": OcrTranslationClient.ClearApiKey(); return null;
            case "translation.models": return await OcrTranslationClient.GetModelsAsync(Text(p, "endpoint", 2048), Text(p, "apiKey", 4096), ct);
            case "translation.run": return await OcrTranslationClient.TranslateAsync(Text(p, "text", 200_000), Text(p, "targetLanguage", 50), ct);
            case "translation.formatted":
                var segments = p.GetProperty("segments").EnumerateArray().Select(s =>
                    new OcrTranslationClient.Segment(Text(s, "id", 30), Text(s, "text", 200_000))).ToArray();
                return (await OcrTranslationClient.TranslateFormattedAsync(segments, Text(p, "targetLanguage", 50), ct))
                    .Select(s => new { id = s.Id, text = s.Text }).ToArray();
            case "utility.checkUpdate":
                var (update, latestTag) = await UpdateService.CheckUpdateAsync(ignoreSkipped: false);
                if (update is not null) new UpdateWindow().SetRelease(update);
                return new { available = update is not null, latestTag, currentVersion = AppConfig.AppVersion };
            case "utility.ocrEngine":
                await new OcrEngineDialog { XamlRoot = _window.Content.XamlRoot }.ShowAsync();
                return new { ready = OcrHelper.IsOneOcrReady, settings = Settings() };
            case "utility.batch":
                var utility = new Window { Title = "Starshot · 批量转换" };
                var frame = new Frame(); utility.Content = frame;
                _utilityWindows.Add(utility);
                utility.Closed += (_, _) => _utilityWindows.Remove(utility);
                var ids = p.GetProperty("ids").EnumerateArray().Select(x => x.GetString()!).ToList();
                frame.Navigate(typeof(ImageBatchConvertWindow), ids.Select(x => new ScreenshotItem(ResolveId(x))).ToList());
                utility.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(980 * _window.UIScale), (int)(680 * _window.UIScale)));
                utility.Activate(); return null;
            default: throw new ArgumentException("未知操作。");
        }
    }

    private static string Text(JsonElement p, string name, int max)
    {
        string result = p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        if (result.Length > max) throw new ArgumentException("输入内容过长。");
        return result;
    }
    private string ResolveFile(JsonElement p) => ResolveId(Text(p, "id", 100));
    private string ResolveId(string id) => _files.TryGetValue(id, out var path) && File.Exists(path) ? path :
        throw new FileNotFoundException("图片已移动或不存在，请刷新图库。");
    private object RegisterFile(string path)
    {
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))));
        _files[id] = path;
        var info = new FileInfo(path);
        return new { id, name = info.Name, format = info.Extension.TrimStart('.').ToUpperInvariant(),
            bytes = info.Length, timestamp = info.CreationTimeUtc.ToString("O") };
    }
    private async Task<object> ListAsync(JsonElement p, CancellationToken ct)
    {
        string search = Text(p, "search", 200);
        string format = Text(p, "format", 20);
        int offset = p.TryGetProperty("offset", out var o) ? Math.Clamp(o.GetInt32(), 0, 1_000_000) : 0;
        string[] roots = new[] { AppConfig.ScreenshotFolder }.Concat(AppConfig.ExtraScreenshotFolders).Distinct().ToArray();
        bool recursive = AppConfig.ScreenshotSubfolderEnabled;
        string[] imports = _imports.ToArray();
        var paths = await Task.Run(() =>
        {
            var result = imports.Where(File.Exists).Select(path => new FileInfo(path))
                .Where(f => f.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    && (format.Length == 0 || f.Extension.Equals("." + format, StringComparison.OrdinalIgnoreCase))).ToList();
            foreach (string root in roots)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(root)) continue;
                try
                {
                    result.AddRange(new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions
                        { RecurseSubdirectories = recursive, IgnoreInaccessible = true, AttributesToSkip = System.IO.FileAttributes.ReparsePoint })
                        .Where(f => (ScreenshotHelper.IsSupportedExtension(f.FullName.ToLowerInvariant()) || f.Extension.Equals(".gif", StringComparison.OrdinalIgnoreCase))
                            && f.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                            && (format.Length == 0 || f.Extension.Equals("." + format, StringComparison.OrdinalIgnoreCase))));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return result.DistinctBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).OrderByDescending(f => f.CreationTimeUtc).ToList();
        }, ct);
        return new { total = paths.Count, items = paths.Skip(offset).Take(60).Select(f => RegisterFile(f.FullName)).ToArray(), offset };
    }
    private async Task<object> ThumbnailAsync(string path, CancellationToken ct)
    {
        await _imageGate.WaitAsync(ct);
        SoftwareBitmap? thumbnail = null;
        try
        {
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            uint width, height;
            try
            {
                // WIC decodes straight to a bounded CPU thumbnail; no disk cache or full-size GPU surface.
                using var file = File.OpenRead(path);
                var decoder = await BitmapDecoder.CreateAsync(file.AsRandomAccessStream()).AsTask(ct);
                width = decoder.PixelWidth; height = decoder.PixelHeight;
                double scale = Math.Min(1, 720d / Math.Max(width, height));
                thumbnail = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied, new BitmapTransform
                    {
                        ScaledWidth = Math.Max(1, (uint)(width * scale)),
                        ScaledHeight = Math.Max(1, (uint)(height * scale)),
                        InterpolationMode = BitmapInterpolationMode.Fant
                    }, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(ct);
                // BitmapEncoder retains the input until FlushAsync completes.
                encoder.SetSoftwareBitmap(thumbnail);
            }
            catch (Exception ex) when (ex is not OperationCanceledException &&
                Path.GetExtension(path).ToLowerInvariant() is ".avif" or ".jxl" or ".jxr")
            {
                // Keep codec/HDR semantics in the existing native decoder when WIC lacks a codec.
                using var image = await ImageLoader.LoadImageAsync(path, ct);
                using var sdr = ScreenCaptureService.TonemapToSdr(image.CanvasBitmap, 250);
                width = sdr.SizeInPixels.Width; height = sdr.SizeInPixels.Height;
                double scale = Math.Min(1, 720d / Math.Max(width, height));
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    width, height, 96, 96, sdr.GetPixelBytes());
                encoder.BitmapTransform.ScaledWidth = Math.Max(1, (uint)(width * scale));
                encoder.BitmapTransform.ScaledHeight = Math.Max(1, (uint)(height * scale));
            }
            await encoder.FlushAsync().AsTask(ct);
            using var stream = output.AsStreamForRead();
            using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer, ct);
            return new { src = "data:image/png;base64," + Convert.ToBase64String(buffer.ToArray()), width, height };
        }
        finally { thumbnail?.Dispose(); _imageGate.Release(); }
    }
    private static async Task<object> ReadClipboardAsync(CancellationToken ct)
    {
        var current = Clipboard.GetContent();
        string text = current.Contains(StandardDataFormats.Text) ? await current.GetTextAsync() : "";
        string? image = null;
        if (current.Contains(StandardDataFormats.Bitmap))
        {
            var reference = await current.GetBitmapAsync();
            using var input = await reference.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(input);
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            using var bitmap = await decoder.GetSoftwareBitmapAsync();
            encoder.SetSoftwareBitmap(bitmap);
            double scale = Math.Min(1, 960d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            encoder.BitmapTransform.ScaledWidth = Math.Max(1, (uint)(decoder.PixelWidth * scale));
            encoder.BitmapTransform.ScaledHeight = Math.Max(1, (uint)(decoder.PixelHeight * scale));
            await encoder.FlushAsync();
            using var stream = output.AsStreamForRead();
            using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer, ct);
            image = "data:image/png;base64," + Convert.ToBase64String(buffer.ToArray());
        }
        var history = await Clipboard.GetHistoryItemsAsync();
        var items = new List<object>();
        foreach (var entry in history.Items.Take(30))
        {
            ct.ThrowIfCancellationRequested();
            string snippet = entry.Content.Contains(StandardDataFormats.Text) ? await entry.Content.GetTextAsync() : "";
            if (snippet.Length > 800) snippet = snippet[..800];
            items.Add(new { id = entry.Id, text = snippet, image = entry.Content.Contains(StandardDataFormats.Bitmap), timestamp = entry.Timestamp.ToString("O") });
        }
        return new { text, image, items, historyEnabled = Clipboard.IsHistoryEnabled() };
    }

    private static object[] Hotkeys() => new[] { 44446, 44445, 44447, 44448, 44449, 44450 }.Select(id =>
    {
        var info = HotkeyManager.GetHotkeyInfo(id)!;
        return (object)new { id, modifiers = (uint)info.Modifiers, key = (uint)info.Key,
            text = HotkeyInput.GetHotkeyText((uint)info.Modifiers, (uint)info.Key), registered = info.IsRegistered, error = info.Error.Failed };
    }).ToArray();

    private static object Settings() => new
    {
        screenshotFolder = AppConfig.ScreenshotFolder,
        extraFolders = AppConfig.ExtraScreenshotFolders,
        subfolders = AppConfig.ScreenshotSubfolderEnabled,
        subfolderPattern = AppConfig.ScreenshotSubfolderPattern,
        filenamePattern = AppConfig.ScreenshotFileNamePattern,
        regionFilenamePattern = AppConfig.RegionScreenshotFileNamePattern,
        autoCopy = AppConfig.AutoCopyScreenshotToClipboard,
        autoCopyOcr = AppConfig.AutoCopyOcrText,
        ultraHdr = AppConfig.AutoSaveUltraHDRJpeg,
        capacityManual = AppConfig.UhdrCapacityManual,
        capacity = AppConfig.UhdrCapacityValue,
        sdrFormat = AppConfig.ScreenCaptureSDRFormat, hdrFormat = AppConfig.ScreenCaptureHDRFormat,
        quality = AppConfig.ScreenCaptureEncodeQuality,
        colorManagement = AppConfig.EnableScreenshotColorManagement,
        deleteSdrHdr = AppConfig.DeleteHDRIfSDRContent,
        sdrWhite = AppConfig.SdrWhiteLevelOverride,
        monitorSource = AppConfig.ScreenshotCaptureMonitorSource,
        muteFullscreen = AppConfig.MuteNotificationInFullscreen,
        ocrEngine = AppConfig.OcrEngine,
        endpoint = AppConfig.TranslationApiUrl, model = AppConfig.TranslationModel,
        targetLanguage = AppConfig.TranslationTargetLanguage,
        theme = AppConfig.Theme,
        language = AppConfig.Language ?? "",
        autoUpdate = AppConfig.EnableAutoUpdateCheck,
        previewUpdates = AppConfig.EnablePreReleaseUpdateCheck,
        startupHidden = AppConfig.AutoStartMinimized,
        highPriority = AppConfig.HighPriorityProcess
    };

    private static int Range(JsonElement value, int min, int max)
    {
        int n = value.GetInt32();
        if (n < min || n > max) throw new ArgumentOutOfRangeException(nameof(value));
        return n;
    }
    private static void ApplySetting(string key, JsonElement value)
    {
        switch (key)
        {
            case "autoCopy": AppConfig.AutoCopyScreenshotToClipboard = value.GetBoolean(); break;
            case "autoCopyOcr": AppConfig.AutoCopyOcrText = value.GetBoolean(); break;
            case "ultraHdr": AppConfig.AutoSaveUltraHDRJpeg = value.GetBoolean(); break;
            case "capacityManual": AppConfig.UhdrCapacityManual = value.GetBoolean(); break;
            case "capacity": double c = value.GetDouble(); if (!double.IsFinite(c) || c < 2 || c > 32) throw new ArgumentException("容量范围为 2–32×。"); AppConfig.UhdrCapacityValue = c; break;
            case "sdrFormat": AppConfig.ScreenCaptureSDRFormat = Range(value, 0, 2); break;
            case "hdrFormat": AppConfig.ScreenCaptureHDRFormat = Range(value, 0, 1); break;
            case "quality": AppConfig.ScreenCaptureEncodeQuality = Range(value, 0, 2); break;
            case "ocrEngine": AppConfig.OcrEngine = Range(value, 0, 1); OcrHelper.ResetEngineCache(); break;
            case "colorManagement": AppConfig.EnableScreenshotColorManagement = value.GetBoolean(); break;
            case "deleteSdrHdr": AppConfig.DeleteHDRIfSDRContent = value.GetBoolean(); break;
            case "sdrWhite": AppConfig.SdrWhiteLevelOverride = Range(value, 0, 1000); break;
            case "monitorSource": AppConfig.ScreenshotCaptureMonitorSource = Range(value, 0, 1); break;
            case "muteFullscreen": AppConfig.MuteNotificationInFullscreen = value.GetBoolean(); break;
            case "subfolders": AppConfig.ScreenshotSubfolderEnabled = value.GetBoolean(); break;
            case "subfolderPattern": AppConfig.ScreenshotSubfolderPattern = value.GetString() ?? ""; break;
            case "filenamePattern": AppConfig.ScreenshotFileNamePattern = value.GetString() ?? ""; break;
            case "regionFilenamePattern": AppConfig.RegionScreenshotFileNamePattern = value.GetString() ?? ""; break;
            case "theme": AppConfig.Theme = Range(value, 0, 2); break;
            case "autoUpdate": AppConfig.EnableAutoUpdateCheck = value.GetBoolean(); break;
            case "previewUpdates": AppConfig.EnablePreReleaseUpdateCheck = value.GetBoolean(); break;
            case "startupHidden": AppConfig.AutoStartMinimized = value.GetBoolean(); break;
            case "highPriority": AppConfig.HighPriorityProcess = value.GetBoolean(); break;
            default: throw new ArgumentException("未知设置项。");
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _core.WebMessageReceived -= OnMessage; _lifetime.Cancel(); _lifetime.Dispose();
        foreach (var window in _utilityWindows.ToArray()) window.Close();
        _files.Clear();
    }
}
