using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Starward.Codec.ICC;
using Starward.Codec.UltraHdr;

namespace Starshot.Features.Codec;

/// <summary>CPU-only complete gain-map reconstruction; no graphics device is created.</summary>
internal static class UhdrLinearDecoder
{
    [DllImport("uhdr.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int is_uhdr_image(nint data, int size);
    internal static unsafe bool IsUhdr(byte[] bytes)
    { fixed (byte* data = bytes) return is_uhdr_image((nint)data, bytes.Length) == 1; }

    internal sealed record LinearImage(byte[] Pixels, int Width, int Height, ColorPrimaries Primaries, float White);
    internal static Task<LinearImage> DecodeAsync(byte[] bytes, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        // The pinned v2.0.2 native library locks output configuration at Probe,
        // even though its error text mentions Decode. Probe in a separate short-
        // lived context, then configure the reconstruction context before probing.
        UhdrGainmapMetadata metadata;
        float white;
        using(var probe = new UhdrDecoder())
        {
            probe.SetImage(bytes);probe.Probe();
            if(probe.ImageWidth<=0 || probe.ImageHeight<=0 || (long)probe.ImageWidth*probe.ImageHeight>64_000_000)
                throw new InvalidDataException("UHDR image exceeds 64 MP decode limit");
            metadata=probe.GetGainmapMetadata();
            white=UhdrReferenceWhite.Read(probe.GetExifData());
        }
        if (!float.IsFinite(metadata.HdrCapacityMax) || metadata.HdrCapacityMax < 1) throw new InvalidDataException("UHDR capacity is invalid");
        ct.ThrowIfCancellationRequested();
        using var decoder = new UhdrDecoder();
        decoder.SetImage(bytes);
        decoder.SetOutImagePixelFormat(UhdrPixelFormat._64bppRGBAHalfFloat);
        decoder.SetOutColorTransfer(UhdrColorTransfer.Linear);
        decoder.SetOutMaxDisplayBoost(Math.Max(1, metadata.HdrCapacityMax));
        decoder.Decode();
        ct.ThrowIfCancellationRequested();
        var raw = decoder.GetDecodedImage();
        if (raw.Width == 0 || raw.Height == 0 || (long)raw.Width * raw.Height > 64_000_000 || raw.Stride[0] < raw.Width || raw.Plane[0] == 0)
            throw new InvalidDataException("UHDR decoded geometry exceeds limits");
        int w = checked((int)raw.Width), h = checked((int)raw.Height), rowBytes = checked(w * 8);
        byte[] pixels = new byte[checked(rowBytes * h)];
        for (int y = 0; y < h; y++)
        {
            ct.ThrowIfCancellationRequested();
            Marshal.Copy(raw.Plane[0] + checked(y * (int)raw.Stride[0] * 8), pixels, y * rowBytes, rowBytes);
        }
        ColorPrimaries primaries = raw.ColorGamut switch
        {
            UhdrColorGamut.BT709 => ColorPrimaries.BT709,
            UhdrColorGamut.DisplayP3 => ColorPrimaries.DisplayP3,
            UhdrColorGamut.BT2100 => ColorPrimaries.BT2020,
            _ => throw new NotSupportedException("Unknown UHDR output gamut")
        };
        return new LinearImage(pixels, w, h, primaries, white);
    }, ct);

}
