using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZXing;
using ZXing.Common;

namespace Starshot.Helpers;

/// <summary>CPU-only QR decoding. Borrows immutable, top-down SDR BGRA8; no model, encoder or GPU.</summary>
internal static class QrCodeHelper
{
    internal const long MaxPixels = 40_000_000;
    static QrCodeHelper() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    internal static Uri? WebLink(string text) => Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && !string.IsNullOrEmpty(uri.Host)
        ? uri : null;

    internal static Task<string[]> DecodeAsync(byte[] bgra, int width, int height, CancellationToken ct = default,
        int stride = 0) => Task.Run(() => Decode(bgra, width, height, ct, stride), ct);

    internal static string[] Decode(byte[] bgra, int width, int height, CancellationToken ct = default, int stride = 0)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            throw new ArgumentException("二维码识别区域过大，请框选二维码附近（最多 4000 万像素）。");
        int rowBytes = checked(width * 4);
        if (stride == 0) stride = rowBytes;
        if (stride < rowBytes || (long)(height - 1) * stride + rowBytes > bgra.Length)
            throw new ArgumentException("二维码输入像素尺寸或 stride 不正确。");
        ct.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        // Luminance is the only full-size temporary buffer. Handle padded rows without a second BGRA copy.
        byte[] luma = new byte[checked(width * height)];
        for (int y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0, src = y * stride, dest = y * width; x < width; x++, src += 4, dest++)
                // Existing SDR crops/WIC buffers are premultiplied. Composite transparency on white.
                luma[dest] = (byte)Math.Min(255, ((bgra[src] + 2 * bgra[src + 1] + bgra[src + 2]) >> 2) + 255 - bgra[src + 3]);
        }
        var reader = new BarcodeReaderGeneric
        {
            Options = new DecodingOptions { PossibleFormats = [BarcodeFormat.QR_CODE], TryHarder = true },
        };
        var source = new RGBLuminanceSource(luma, width, height, RGBLuminanceSource.BitmapFormat.Gray8);
        var texts = new List<string>();
        // QRCodeMultiReader handles QR rotations itself. Explicit inversion also works for DecodeMultiple,
        // whose implementation does not apply the single-reader TryInverted hint.
        foreach (var input in new LuminanceSource[] { source, source.invert() })
        {
            ct.ThrowIfCancellationRequested();
            var results = reader.DecodeMultiple(input);
            ct.ThrowIfCancellationRequested();
            if (results is null) continue;
            texts.AddRange(results.Select(r => r.Text).Where(t => !string.IsNullOrEmpty(t)));
        }
        string[] distinct = texts.Distinct(StringComparer.Ordinal).Take(64).ToArray();
        Serilog.Log.Information("[QR] input={Width}x{Height}, results={Count}, elapsed={Elapsed} ms",
            width, height, distinct.Length, watch.ElapsedMilliseconds);
        return distinct;
    }
}
