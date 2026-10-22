using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Extensions.Logging;
using Starshot.Features.Codec;
using Starshot.Helpers;
using Starward.Codec.ICC;
using Vanara.PInvoke;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace Starshot.Features.Screenshot;

internal partial class ScreenCaptureService
{
    private void ReleaseGpuWindowsForLightweight()
    {
        if (AppConfig.ScreenCaptureMode != (int)ScreenshotCaptureMode.Lightweight) return;
        var region = _regionWindow; _regionWindow = null;
        var info = _infoWindow; _infoWindow = null;
        try { region?.Close(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to close retired GPU region window"); }
        try { info?.ReleaseForLightweight(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to close retired GPU capture notice"); }
        if (region is not null || info is not null)
        {
            // Trim cached allocations only when the GPU path has actually been used.
            // The shared device may still belong to a pinned image; never dispose it.
            try { CanvasDevice.GetSharedDevice().Trim(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Unable to trim retired capture allocations"); }
        }
        _logger.LogInformation("Lightweight GPU capture windows retired: region={Region}, info={Info}", region is not null, info is not null);
    }

    private async Task CaptureLightweightScreenAsync(nint monitor, nint sourceWindow, Action releaseGuard)
    {
        var info = new User32.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<User32.MONITORINFOEX>() };
        if (!User32.GetMonitorInfo(new HMONITOR(monitor), ref info)) throw new InvalidOperationException("显示器已不可用。");
        using var frame = await Task.Run(() => GdiCaptureFrame.Capture(info.rcMonitor.left, info.rcMonitor.top,
            info.rcMonitor.right - info.rcMonitor.left, info.rcMonitor.bottom - info.rcMonitor.top));
        using var image = new CpuCaptureImage(frame.Width, frame.Height, frame.Pixels);
        _logger.LogInformation("Lightweight full-screen CPU capture: {Width}x{Height}; no CanvasDevice", image.Width, image.Height);
        releaseGuard();
        string? path = await SaveCpuCaptureAsync(image, sourceWindow, isRegion: false);
        if (path is null) return;
        if (AppConfig.AutoCopyScreenshotToClipboard) ClipboardHelper.SetFiles(path);
        ShowCpuNotice("截图已保存", image, sourceWindow, path);
    }

    private async Task CaptureLightweightRegionAsync(int vx, int vy, int vw, int vh,
        bool copyOnly, bool ocrEditor, Action releaseGuard,
        TaskCompletionSource<Rectangle?>? subtitleSelection=null,CancellationToken selectionToken=default)
    {
        nint sourceWindow = (nint)User32.GetForegroundWindow();
        using var frame = await Task.Run(() => GdiCaptureFrame.Capture(vx, vy, vw, vh));
        using var desktop = new CpuCaptureImage(vw, vh, frame.Pixels);
        ReleaseGpuWindowsForLightweight();
        selectionToken.ThrowIfCancellationRequested();
        var action = subtitleSelection is not null ? RegionCaptureAction.Subtitles : ocrEditor ? RegionCaptureAction.Ocr : copyOnly ? RegionCaptureAction.Copy : RegionCaptureAction.Save;
        using var overlay = new CpuRegionCaptureWindow(desktop, vx, vy, action);
        var dispatcher=Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        using var cancelSelection=selectionToken.Register(()=>dispatcher.TryEnqueue(()=>overlay.Dispose()));
        CpuRegionCaptureResult result = await overlay.Completion.Task;
        using var image = result.Image;
        using var layer = result.AnnotationLayer;
        // The overlay borrowed these buffers. After its HWND and pins are gone the
        // desktop is no longer needed, including throughout long/GIF recording.
        overlay.Dispose(); desktop.Dispose(); frame.Dispose();
        if (result.Action == RegionCaptureAction.Cancel) return;

        if(result.Action==RegionCaptureAction.Subtitles)
        {
            var region=SubtitleSelectionRegion.ToDesktop(result.PhysicalRect,vx,vy);
            image?.Dispose();layer?.Dispose();releaseGuard();
            if(subtitleSelection is not null)subtitleSelection.TrySetResult(region);
            else await Features.Subtitles.SubtitleController.UseSelectionAsync(region);
            return;
        }
        if(image is null)return;

        if (result.Action is RegionCaptureAction.Pin or RegionCaptureAction.RecordGif or RegionCaptureAction.LongCapture)
        {
            // Explicit image tools still use their native renderer. Upload only the
            // selected image at this boundary; ordinary capture/copy/save/OCR never does.
            var device = CanvasDevice.GetSharedDevice();
            using var selected = GdiCaptureBackend.Upload(image.Pixels, image.Width, image.Height, device);
            using var annotations = layer is null ? null : GdiCaptureBackend.Upload(layer.Pixels, layer.Width, layer.Height, device);
            var rect = new Rect(result.PhysicalRect.X, result.PhysicalRect.Y, result.PhysicalRect.Width, result.PhysicalRect.Height);
            if (result.Action == RegionCaptureAction.Pin)
                _ = new PinnedCaptureWindow(selected, vx + result.PhysicalRect.X-(result.FrameStyle?.Padding??0), vy + result.PhysicalRect.Y-(result.FrameStyle?.Padding??0));
            else if (result.Action == RegionCaptureAction.RecordGif)
                await GifRegionRecorder.RecordAsync(rect, selected, annotations, vx, vy, vw, vh);
            else await ScrollingRegionCapture.CaptureAsync(rect, selected, annotations, vx, vy, vw, vh);
            return;
        }

        releaseGuard();
        if(result.Action == RegionCaptureAction.QrCode)
        {
            await QrCodeResultWindow.ShowAsync(image.Pixels,image.Width,image.Height);
            return;
        }
        if(result.Action is RegionCaptureAction.Cutout or RegionCaptureAction.Formula or RegionCaptureAction.PageLayout)
        {
            var document=new Starshot.Features.Workspace.WorkspaceDocument("截图.png",Guid.NewGuid().ToString("N"),image.Width,image.Height,(byte[])image.Pixels.Clone())
                {InitialTool=result.Action switch{RegionCaptureAction.Formula=>"formula",RegionCaptureAction.PageLayout=>"layout",_=>"polygon"}};
            Starshot.Features.ViewHost.MainWindow.OpenCaptureEditor(document);
            return;
        }
        if (result.Action is RegionCaptureAction.Ocr or RegionCaptureAction.Translate)
        {
            using var editorPreparation=OcrResultWindow.Prepare();
            var lines = await Task.Run(() => OcrHelper.RecognizeAsync(image.Pixels, image.Width, image.Height, 1.0));
            if (lines is null) ShowCpuNotice(Lang.Ocr_EngineUnavailable, image, sourceWindow);
            else if (lines.Count == 0) ShowCpuNotice(Lang.Ocr_NoneFound, image, sourceWindow);
            else editorPreparation.Show(lines, result.Action == RegionCaptureAction.Translate);
            return;
        }
        if (result.Action == RegionCaptureAction.Copy)
        {
            bool copied = await Task.Run(() => ClipboardHelper.SetBitmapDib(image.Width, image.Height, image.Pixels));
            ShowCpuNotice(copied ? "已复制截图" : "剪贴板正忙，请重试", image, sourceWindow);
            return;
        }
        string? saved = await SaveCpuCaptureAsync(image, sourceWindow, isRegion: true,
            chooseLocation: CaptureSavePolicy.ChooseLocation(result.Action));
        if (saved is null) return;
        if (AppConfig.AutoCopyScreenshotToClipboard)
            await Task.Run(() => ClipboardHelper.SetBitmapDib(image.Width, image.Height, image.Pixels));
        ShowCpuNotice("截图已保存", image, sourceWindow, saved);
        _logger.LogInformation("Lightweight region screenshot saved through CPU encoder: {Width}x{Height}", image.Width, image.Height);
    }

    private async Task<string?> SaveCpuCaptureAsync(CpuCaptureImage image, nint sourceWindow, bool isRegion, bool chooseLocation = false)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        string process = GetProcessNameFromWindowHandle(sourceWindow), exe = GetProcessExeNameFromWindowHandle(sourceWindow);
        var title = new StringBuilder(Math.Clamp(User32.GetWindowTextLength(sourceWindow) + 1, 1, 32768));
        User32.GetWindowText(sourceWindow, title, title.Capacity);
        string root = string.IsNullOrWhiteSpace(AppConfig.ScreenshotFolder)
            ? Path.Combine(AppConfig.UserDataFolder, "Screenshots") : AppConfig.ScreenshotFolder;
        if (AppConfig.ScreenshotSubfolderEnabled)
        {
            string subfolder = BuildFileName(process, exe, title.ToString(), now, (uint)image.Width,
                (uint)image.Height, AppConfig.ScreenshotSubfolderPattern);
            if (!string.IsNullOrWhiteSpace(subfolder)) root = Path.Combine(root, subfolder);
        }
        root = Path.GetFullPath(root);
        string extension = AppConfig.ScreenCaptureSDRFormat switch { 1 => "avif", 2 => "jxl", 3 => "heic", _ => "png" };
        string path = EnsureUniquePath(Path.Combine(root, BuildFileName(process, exe, title.ToString(), now,
            (uint)image.Width, (uint)image.Height, isRegion ? AppConfig.RegionScreenshotFileNamePattern : null) + "." + extension));
        if (chooseLocation)
        {
            string? selected = await ChooseCaptureSavePathAsync(sourceWindow, path, extension);
            if (selected is null) { _logger.LogInformation("Region save cancelled; no file or clipboard change"); return null; }
            path = selected;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] xmp = BuildXMPMetadata(now);
        int quality = AppConfig.ScreenCaptureEncodeQuality switch { 0 => 80, 2 => 100, _ => 90 };
        float distance = AppConfig.ScreenCaptureEncodeQuality switch { 0 => 2, 2 => 0, _ => 1 };
        await _encodeSlim.WaitAsync();
        try
        {
            using var memory = new MemoryStream();
            if (extension == "png")
                await ImageSaver.SaveAsPngAsync(memory, (uint)image.Width, (uint)image.Height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, image.Pixels, ColorPrimaries.BT709, xmp, true);
            else if (extension == "avif")
                await ImageSaver.SaveAsAvifAsync(memory, (uint)image.Width, (uint)image.Height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, image.Pixels, ColorPrimaries.BT709, quality, xmp, true);
            else if (extension == "heic")
                await ImageSaver.SaveAsHeicAsync(memory, (uint)image.Width, (uint)image.Height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, image.Pixels, ColorPrimaries.BT709, quality, xmp);
            else
                await ImageSaver.SaveAsJxlAsync(memory, (uint)image.Width, (uint)image.Height,
                    DirectXPixelFormat.R8G8B8A8UIntNormalized, image.RgbaPixels(), ColorPrimaries.BT709, distance, xmp, true);
            memory.Position = 0;
            await using var output = File.Create(path);
            await memory.CopyToAsync(output);
            return path;
        }
        finally { _encodeSlim.Release(); }
    }

    private static void ShowCpuNotice(string title, CpuCaptureImage? image, nint window,
        string? file = null, string? subtitle = null)
    {
        if (!ShouldShowInfoWindow()) return;
        try
        {
            var monitor = User32.MonitorFromWindow(window, User32.MonitorFlags.MONITOR_DEFAULTTONEAREST);
            var info = new User32.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<User32.MONITORINFOEX>() };
            if (!User32.GetMonitorInfo(monitor, ref info)) return;
            _ = new CpuCaptureNotice(title, image, Rectangle.FromLTRB(info.rcMonitor.left, info.rcMonitor.top,
                info.rcMonitor.right, info.rcMonitor.bottom), file, subtitle);
        }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Lightweight capture notification failed"); }
    }
}
