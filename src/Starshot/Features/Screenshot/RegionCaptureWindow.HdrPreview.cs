using System;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace Starshot.Features.Screenshot;

public sealed partial class RegionCaptureWindow
{
    private bool _nativeHdrPreview;
    private float _previewSdrWhite = 80;

    private void CreateOverlaySwapChain(float width, float height, bool hdr)
    {
        var format = hdr ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized;
        if (_swapChain is not null && _swapChain.Format == format && Math.Abs(_swapChain.Dpi - _scale * 96) < .05 && Math.Abs(_swapChain.Size.Width - width) < .5 && Math.Abs(_swapChain.Size.Height - height) < .5)
        { _nativeHdrPreview = hdr; return; }
        ReleaseSwapChain();
        try
        {
            // FP16 swap chains use linear BT.709 scRGB by default (DWM's own space).
            // Submit raw captured values: do not normalize white or apply OETF here.
            _swapChain = new(CanvasDevice.GetSharedDevice(), width, height, _scale * 96, format, 2, CanvasAlphaMode.Premultiplied);
            _nativeHdrPreview = hdr;
        }
        catch (Exception ex) when (hdr)
        {
            Serilog.Log.Warning(ex, "Native HDR overlay unavailable; using SDR preview");
            _nativeHdrPreview = false;
            _swapChain = new(CanvasDevice.GetSharedDevice(), width, height, _scale * 96,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, CanvasAlphaMode.Premultiplied);
        }
        Canvas.SwapChain = _swapChain;
        Serilog.Log.Information("Region overlay preview: nativeHDR={Hdr}, format={Format}, buffers=2", _nativeHdrPreview, _swapChain.Format);
    }
    private void DrawFrozenPreview(CanvasDrawingSession ds, Rect destination, Rect source)
    {
        var bitmap = _nativeHdrPreview ? _canvasOriginal : _displayBitmap;
        ds.DrawImage(bitmap, destination, source, 1, CanvasImageInterpolation.Linear);
        if (!_nativeHdrPreview || _analysisMonitors is null || _previewSdrWhite == 80) return;
        // Capture's mixed-monitor composite boosts SDR-only monitors to the shared
        // white for export. Undo that boost only when showing those monitors on SDR.
        foreach (var monitor in _analysisMonitors)
        {
            if (monitor.Hdr) continue;
            var region = new Rect(monitor.Bounds.X - _vx, monitor.Bounds.Y - _vy, monitor.Bounds.Width, monitor.Bounds.Height);
            region.Intersect(source); if (region.Width <= 0 || region.Height <= 0) continue;
            var target = new Rect(destination.X + (region.X - source.X) / source.Width * destination.Width,
                destination.Y + (region.Y - source.Y) / source.Height * destination.Height,
                region.Width / source.Width * destination.Width, region.Height / source.Height * destination.Height);
            using var white = new WhiteLevelAdjustmentEffect { Source = bitmap, InputWhiteLevel = 80, OutputWhiteLevel = _previewSdrWhite, BufferPrecision = CanvasBufferPrecision.Precision16Float };
            ds.DrawImage(white, target, region, 1, CanvasImageInterpolation.Linear);
        }
    }
}
