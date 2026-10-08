using System;
using System.Drawing;

namespace Starshot.Features.Screenshot;

/// <summary>Owned top-down, tightly packed BGRA8 pixels. No CanvasDevice or GPU upload.</summary>
internal sealed class CpuCaptureImage : IDisposable
{
    private byte[]? _pixels;
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels => _pixels ?? throw new ObjectDisposedException(nameof(CpuCaptureImage));

    public CpuCaptureImage(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height * 4))
            throw new ArgumentException("Invalid BGRA8 image dimensions.");
        Width = width; Height = height; _pixels = pixels;
    }

    public CpuCaptureImage Crop(Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || rect.Left < 0 || rect.Top < 0
            || rect.Right > Width || rect.Bottom > Height)
            throw new ArgumentOutOfRangeException(nameof(rect));
        byte[] result = new byte[checked(rect.Width * rect.Height * 4)];
        int rowBytes = rect.Width * 4;
        for (int y = 0; y < rect.Height; y++)
            Buffer.BlockCopy(Pixels, ((rect.Y + y) * Width + rect.X) * 4, result, y * rowBytes, rowBytes);
        return new CpuCaptureImage(rect.Width, rect.Height, result);
    }

    public CpuCaptureImage Clone() => new(Width, Height, (byte[])Pixels.Clone());

    public byte[] RgbaPixels()
    {
        byte[] result = (byte[])Pixels.Clone();
        for (int i = 0; i < result.Length; i += 4)
            (result[i], result[i + 2]) = (result[i + 2], result[i]);
        return result;
    }

    public void Dispose() => _pixels = null;
}
