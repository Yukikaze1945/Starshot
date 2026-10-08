using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Display;
using Microsoft.UI;
using Starshot.Features.Screenshot;
using Windows.Graphics.DirectX;

namespace Starshot.Features.Codec;

internal sealed record StaticVideoSaveResult(string ImagePath, string? VideoPath, string? Error, bool Cancelled)
{
    public bool Complete => VideoPath is not null && Error is null;
}

internal static class StaticVideoSaver
{
    // Called on the capture dispatcher. All GPU objects die before the encoder is started.
    internal static async Task<StaticVideoSaveResult> SaveAsync(CanvasBitmap frozen, string imagePath,
        bool hdr, float maxCll, float maxFall, StaticVideoCodec codec, VideoMastering? mastering, CancellationToken ct)
    {
        string videoPath = Path.ChangeExtension(imagePath, ".mp4");
        bool committed = false;
        try
        {
            byte[] pixels;
            if (hdr)
            {
                using var pq = new CanvasRenderTarget(frozen.Device, frozen.SizeInPixels.Width,
                    frozen.SizeInPixels.Height, 96, DirectXPixelFormat.R16G16B16A16UIntNormalized, CanvasAlphaMode.Premultiplied);
                using var effect = new ScRGBToHDR10Effect { Source = frozen, BufferPrecision = CanvasBufferPrecision.Precision16Float };
                using (var drawing = pq.CreateDrawingSession()) { drawing.Clear(Colors.Black); drawing.DrawImage(effect); }
                pixels = pq.GetPixelBytes();
            }
            else pixels = frozen.GetPixelBytes();
            bool bgra = frozen.Format == DirectXPixelFormat.B8G8R8A8UIntNormalized;
            var frame = await Task.Run(() => StaticVideoPixels.Prepare(pixels, (int)frozen.SizeInPixels.Width,
                (int)frozen.SizeInPixels.Height, hdr, bgra, ct), ct);
            await StaticVideoEncoder.EncodeAsync(frame, codec, videoPath, maxCll, maxFall, mastering, ct);
            committed = true;
            // Persist only a small media descriptor, never another frame or thumbnail cache.
            StaticVideoMetadata.Write(videoPath, imagePath, codec, hdr);
            return new(imagePath, videoPath, null, false);
        }
        catch (OperationCanceledException) { return new(imagePath, null, "视频已取消，图片已保留", true); }
        catch (Exception ex) when (ct.IsCancellationRequested) { Serilog.Log.Debug(ex, "Static video cancellation"); return new(imagePath, null, "视频已取消，图片已保留", true); }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Static video encoding failed; companion image retained: {Image}", imagePath);
            return new(imagePath, committed ? videoPath : null, "视频未完成，图片已保留：" + ex.Message, false);
        }
    }

    internal static VideoMastering? ReadMastering(DisplayInformation display, bool singleDisplay)
    {
        if (!singleDisplay) return null;
        try
        {
            var info = display.GetAdvancedColorInfo();
            if (info.CurrentAdvancedColorKind != DisplayAdvancedColorKind.HighDynamicRange) return null;
            var result = new VideoMastering(info.RedPrimary.X, info.RedPrimary.Y, info.GreenPrimary.X, info.GreenPrimary.Y,
                info.BluePrimary.X, info.BluePrimary.Y, info.WhitePoint.X, info.WhitePoint.Y, info.MinLuminanceInNits, info.MaxLuminanceInNits);
            return result.IsValid ? result : null;
        }
        catch (Exception ex) { Serilog.Log.Debug(ex, "No valid video mastering metadata"); return null; }
    }
}
