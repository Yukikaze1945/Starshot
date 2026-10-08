using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Vanara.PInvoke;
using Windows.Graphics.DirectX;

namespace Starshot.Features.Screenshot;

internal interface IScreenCaptureBackend
{
    Task<CanvasRenderTarget> CaptureMonitorAsync(nint monitor, DirectXPixelFormat format,
        CanvasDevice device, bool isHdr, CancellationToken token);
}

internal sealed class WgcCaptureBackend : IScreenCaptureBackend
{
    public Task<CanvasRenderTarget> CaptureMonitorAsync(nint monitor, DirectXPixelFormat format,
        CanvasDevice device, bool isHdr, CancellationToken token) =>
        MonitorCaptureContext.CaptureAsync(monitor, format, device, token, isHdr);
}

internal sealed class GdiCaptureBackend : IScreenCaptureBackend
{
    public async Task<CanvasRenderTarget> CaptureMonitorAsync(nint monitor, DirectXPixelFormat format,
        CanvasDevice device, bool isHdr, CancellationToken token)
    {
        var info = new User32.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<User32.MONITORINFOEX>() };
        if (!User32.GetMonitorInfo(new HMONITOR(monitor), ref info))
            throw new InvalidOperationException("The capture monitor is no longer available.");
        using var frame = await Task.Run(() => GdiCaptureFrame.Capture(info.rcMonitor.left,
            info.rcMonitor.top, info.rcMonitor.right - info.rcMonitor.left,
            info.rcMonitor.bottom - info.rcMonitor.top, token), token).ConfigureAwait(false);
        Serilog.Log.Information("GDI capture: size={Width}x{Height}; BGRA8 RAM snapshot", frame.Width, frame.Height);
        return Upload(frame.Pixels, frame.Width, frame.Height, device);
    }

    internal static CanvasRenderTarget Upload(byte[] pixels, int width, int height, CanvasDevice device)
    {
        var bitmap = new CanvasRenderTarget(device, width, height, 96,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        try { bitmap.SetPixelBytes(pixels); return bitmap; }
        catch { bitmap.Dispose(); throw; }
    }
}
