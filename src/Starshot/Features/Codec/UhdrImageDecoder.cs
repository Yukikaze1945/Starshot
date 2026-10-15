using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Starward.Codec.ICC;
using Starward.Codec.UltraHdr;
using Windows.Graphics.DirectX;

namespace Starshot.Features.Codec;

internal static class UhdrImageDecoder
{
    internal static bool IsUhdr(byte[] bytes) => UhdrLinearDecoder.IsUhdr(bytes);
    internal static Task<UhdrLinearDecoder.LinearImage> DecodeAsync(byte[] bytes,CancellationToken ct) => UhdrLinearDecoder.DecodeAsync(bytes,ct);

    internal static async Task<ImageInfo> LoadAsync(byte[] bytes, CancellationToken ct)
    {
        var decoded = await DecodeAsync(bytes, ct);
        using var packed = CanvasBitmap.CreateFromBytes(CanvasDevice.GetSharedDevice(), decoded.Pixels,
            decoded.Width, decoded.Height, DirectXPixelFormat.R16G16B16A16Float);
        // libultrahdr returns linear values relative to SDR white in its output gamut.
        // Convert once into the app's absolute scRGB contract (1.0 = 80 nits).
        Matrix4x4 m = ColorPrimaries.GetColorTransferMatrix(decoded.Primaries, ColorPrimaries.BT709);
        float scale = decoded.White / 80;
        using var effect = new ColorMatrixEffect { Source = packed, ClampOutput = false,
            BufferPrecision = CanvasBufferPrecision.Precision16Float,
            ColorMatrix = new Matrix5x4(m.M11 * scale,m.M12 * scale,m.M13 * scale,0,
                m.M21 * scale,m.M22 * scale,m.M23 * scale,0,m.M31 * scale,m.M32 * scale,m.M33 * scale,0,
                0,0,0,1,0,0,0,0) };
        var target = new CanvasRenderTarget(packed.Device, decoded.Width, decoded.Height, 96,
            DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied);
        try
        {
            using (var ds = target.CreateDrawingSession()) ds.DrawImage(effect);
            return new ImageInfo { CanvasBitmap = target, HDR = true, ColorPrimaries = ColorPrimaries.BT709,
                SdrReferenceWhiteNits = decoded.White, Carrier = "Ultra HDR JPEG" };
        }
        catch { target.Dispose(); throw; }
    }
}
