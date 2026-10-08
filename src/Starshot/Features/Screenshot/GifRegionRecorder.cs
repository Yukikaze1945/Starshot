using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Display;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Starshot.Features.Codec;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;

namespace Starshot.Features.Screenshot;

internal static class GifRegionRecorder
{
    private sealed record Monitor(nint Handle, int X, int Y, bool IsHdr);

    internal static async Task<byte[]> CaptureStillAsync(Rect selection, CanvasRenderTarget? annotations,
        int virtualX, int virtualY, int virtualWidth, int virtualHeight, CancellationToken token)
    {
        using var operation = CaptureModeController.BeginOperation();
        var device = CanvasDevice.GetSharedDevice();
        var displays = DisplayArea.FindAll();
        var monitors = new List<Monitor>(displays.Count);
        bool anyHdr = false;
        for (int i = 0; i < displays.Count; i++)
        {
            using var info = DisplayInformation.CreateForDisplayId(displays[i].DisplayId);
            bool hdr = CaptureModeController.UsesHdrCapture(operation.Mode) && !ScreenCaptureHelper.IsWin10
                && info.GetAdvancedColorInfo().CurrentAdvancedColorKind
                is DisplayAdvancedColorKind.HighDynamicRange;
            anyHdr |= hdr;
            var bounds = displays[i].OuterBounds;
            monitors.Add(new Monitor((nint)displays[i].DisplayId.Value,
                bounds.X - virtualX, bounds.Y - virtualY, hdr));
        }
        float sdrWhite = anyHdr ? AppConfig.GetSdrWhiteLevelFromDisplays(displays) : 80;
        DirectXPixelFormat format = CaptureModeController.PixelFormat(operation.Mode, anyHdr);
        return await CaptureFrameAsync(monitors, device, format, anyHdr, sdrWhite,
            selection, annotations, virtualX, virtualY, virtualWidth, virtualHeight, operation.Mode,
            (int)selection.Width, (int)selection.Height, token);
    }

    public static async Task<string?> RecordAsync(Rect selection, CanvasRenderTarget firstFrame,
        CanvasRenderTarget? annotations, int virtualX, int virtualY, int virtualWidth, int virtualHeight)
    {
        using var operation = CaptureModeController.BeginOperation();
        int sourceWidth = (int)selection.Width, sourceHeight = (int)selection.Height;
        double resize = Math.Min(1.0, Math.Min(1280.0 / sourceWidth, 720.0 / sourceHeight));
        int width = Math.Max(1, (int)Math.Round(sourceWidth * resize));
        int height = Math.Max(1, (int)Math.Round(sourceHeight * resize));
        var device = CanvasDevice.GetSharedDevice();
        var displays = DisplayArea.FindAll();
        var monitors = new List<Monitor>(displays.Count);
        bool anyHdr = false;
        for (int i = 0; i < displays.Count; i++)
        {
            using var info = DisplayInformation.CreateForDisplayId(displays[i].DisplayId);
            bool hdr = CaptureModeController.UsesHdrCapture(operation.Mode) && !ScreenCaptureHelper.IsWin10
                && info.GetAdvancedColorInfo().CurrentAdvancedColorKind
                is DisplayAdvancedColorKind.HighDynamicRange;
            anyHdr |= hdr;
            var bounds = displays[i].OuterBounds;
            monitors.Add(new Monitor((nint)displays[i].DisplayId.Value,
                bounds.X - virtualX, bounds.Y - virtualY, hdr));
        }
        float sdrWhite = anyHdr ? AppConfig.GetSdrWhiteLevelFromDisplays(displays) : 80;
        DirectXPixelFormat format = CaptureModeController.PixelFormat(operation.Mode, anyHdr);

        string root = string.IsNullOrWhiteSpace(AppConfig.ScreenshotFolder)
            ? Path.Combine(AppConfig.UserDataFolder, "Screenshots") : AppConfig.ScreenshotFolder;
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        string stem = $"Starshot_GIF_{DateTime.Now:yyyyMMdd_HHmmss}_{width}x{height}";
        string path = Path.Combine(root, stem + ".gif");
        for (int suffix = 2; File.Exists(path); suffix++)
            path = Path.Combine(root, $"{stem}_{suffix}.gif");

        int controlX = Math.Clamp(virtualX + (int)selection.Right - 280,
            virtualX, virtualX + Math.Max(0, virtualWidth - 280));
        int below = virtualY + (int)selection.Bottom + 8;
        int controlY = below + 66 <= virtualY + virtualHeight ? below
            : Math.Max(virtualY, virtualY + (int)selection.Top - 74);
        var control = new GifRecordingControlWindow(controlX, controlY);
        bool completed = false;
        try
        {
            using var resized = new CanvasRenderTarget(device, width, height, 96,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
            using (var ds = resized.CreateDrawingSession())
                ds.DrawImage(firstFrame, new Rect(0, 0, width, height),
                    new Rect(0, 0, sourceWidth, sourceHeight));
            byte[] previous = resized.GetPixelBytes();
            DateTimeOffset previousTime = DateTimeOffset.UtcNow;

            File.WriteAllBytes(path, []);
            StorageFile output = await StorageFile.GetFileFromPathAsync(path);
            using IRandomAccessStream stream = await output.OpenAsync(FileAccessMode.ReadWrite);
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.GifEncoderId, stream);
            int frames = 0;
            while (!control.Completion.IsCompleted && frames < 150)
            {
                Task delay = Task.Delay(200);
                if (await Task.WhenAny(delay, control.Completion) != delay) break;
                using var frameTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                byte[] next = await CaptureFrameAsync(monitors, device, format, anyHdr, sdrWhite,
                    selection, annotations, virtualX, virtualY, virtualWidth, virtualHeight, operation.Mode, width, height,
                    frameTimeout.Token);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                ushort centiseconds = (ushort)Math.Clamp(
                    (int)Math.Round((now - previousTime).TotalMilliseconds / 10), 2, 65535);
                await WriteFrameAsync(encoder, previous, width, height, centiseconds);
                await encoder.GoToNextFrameAsync();
                previous = next;
                previousTime = now;
                frames++;
            }
            bool save = control.Completion.IsCompleted && await control.Completion;
            if (!save && frames < 150) return null;
            await WriteFrameAsync(encoder, previous, width, height, 20);
            await encoder.FlushAsync();
            completed = true;
            Serilog.Log.Information("Region GIF saved: {Path}, frames={Frames}", path, frames + 1);
            return path;
        }
        finally
        {
            control.Close();
            if (!completed)
            {
                try { File.Delete(path); }
                catch (Exception ex) { Serilog.Log.Warning(ex, "Failed to delete incomplete GIF: {Path}", path); }
            }
        }
    }

    private static async Task WriteFrameAsync(BitmapEncoder encoder, byte[] pixels,
        int width, int height, ushort delay)
    {
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            (uint)width, (uint)height, 96, 96, pixels);
        var properties = new BitmapPropertySet
        {
            ["/grctlext/Delay"] = new BitmapTypedValue(delay, Windows.Foundation.PropertyType.UInt16),
            ["/grctlext/Disposal"] = new BitmapTypedValue((byte)2, Windows.Foundation.PropertyType.UInt8),
        };
        await encoder.BitmapProperties.SetPropertiesAsync(properties);
    }

    private static async Task<byte[]> CaptureFrameAsync(IReadOnlyList<Monitor> monitors,
        CanvasDevice device, DirectXPixelFormat format, bool anyHdr, float sdrWhite,
        Rect selection, CanvasRenderTarget? annotations, int virtualX, int virtualY,
        int virtualWidth, int virtualHeight, ScreenshotCaptureMode mode,
        int outputWidth, int outputHeight, CancellationToken token)
    {
        if (mode == ScreenshotCaptureMode.Lightweight)
        {
            // Read only the live selection into RAM. A temporary GPU upload is
            // needed only for resizing/annotations; no full-desktop WGC cache.
            using var frame = await Task.Run(() => GdiCaptureFrame.Capture(
                virtualX + (int)selection.X, virtualY + (int)selection.Y,
                (int)selection.Width, (int)selection.Height, token), token);
            if (annotations is null && outputWidth == frame.Width && outputHeight == frame.Height)
                return frame.Pixels;
            using var source = GdiCaptureBackend.Upload(frame.Pixels, frame.Width, frame.Height, device);
            using var output = new CanvasRenderTarget(device, outputWidth, outputHeight, 96,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
            using (var ds = output.CreateDrawingSession())
            {
                ds.DrawImage(source, new Rect(0, 0, outputWidth, outputHeight),
                    new Rect(0, 0, frame.Width, frame.Height));
                if (annotations is not null)
                    ds.DrawImage(annotations, new Rect(0, 0, outputWidth, outputHeight),
                        new Rect(0, 0, selection.Width, selection.Height));
            }
            return output.GetPixelBytes();
        }

        var tasks = new Task<CanvasRenderTarget>[monitors.Count];
        for (int i = 0; i < monitors.Count; i++)
        {
            Monitor monitor = monitors[i];
            tasks[i] = ScreenCaptureHelper.CaptureMonitorBitmapAsync(monitor.Handle,
                format, device, monitor.IsHdr, token);
        }
        try { await Task.WhenAll(tasks); }
        catch
        {
            foreach (var task in tasks)
                if (task is { Status: TaskStatus.RanToCompletion }) task.Result.Dispose();
            throw;
        }

        try
        {
            using var composite = new CanvasRenderTarget(device, virtualWidth, virtualHeight, 96,
                format, CanvasAlphaMode.Premultiplied);
            using (var ds = composite.CreateDrawingSession())
            {
                ds.Clear(Colors.Black);
                for (int i = 0; i < monitors.Count; i++)
                {
                    Monitor monitor = monitors[i];
                    CanvasRenderTarget source = tasks[i].Result;
                    if (anyHdr && !monitor.IsHdr && sdrWhite != 80)
                    {
                        var whiteLevel = new WhiteLevelAdjustmentEffect
                        {
                            Source = source, InputWhiteLevel = sdrWhite, OutputWhiteLevel = 80,
                            BufferPrecision = CanvasBufferPrecision.Precision16Float,
                        };
                        ds.DrawImage(whiteLevel, monitor.X, monitor.Y);
                    }
                    else ds.DrawImage(source, monitor.X, monitor.Y);
                }
            }
            using var output = new CanvasRenderTarget(device, outputWidth, outputHeight, 96,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
            using (var ds = output.CreateDrawingSession())
            {
                if (anyHdr)
                {
                    var whiteLevel = new WhiteLevelAdjustmentEffect
                    {
                        Source = composite, InputWhiteLevel = 80, OutputWhiteLevel = sdrWhite,
                        BufferPrecision = CanvasBufferPrecision.Precision16Float,
                    };
                    var gamma = new SrgbGammaEffect
                    {
                        Source = whiteLevel, GammaMode = SrgbGammaMode.OETF,
                        BufferPrecision = CanvasBufferPrecision.Precision16Float,
                    };
                    ds.DrawImage(gamma, new Rect(0, 0, outputWidth, outputHeight), selection);
                }
                else ds.DrawImage(composite, new Rect(0, 0, outputWidth, outputHeight), selection);
                if (annotations is not null)
                    ds.DrawImage(annotations, new Rect(0, 0, outputWidth, outputHeight),
                        new Rect(0, 0, selection.Width, selection.Height));
            }
            return output.GetPixelBytes();
        }
        finally
        {
            for (int i = 0; i < tasks.Length; i++) tasks[i].Result.Dispose();
        }
    }
}
