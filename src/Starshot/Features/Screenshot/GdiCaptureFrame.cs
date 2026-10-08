using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace Starshot.Features.Screenshot;

// The native DIB lives only for BitBlt/readback. The frozen desktop and its crop
// remain BGRA8 in RAM; no WGC item, D3D device or capture session is created here.
internal sealed partial class GdiCaptureFrame : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    private byte[]? _pixels;

    private GdiCaptureFrame(int width, int height, byte[] pixels)
    {
        Width = width; Height = height; _pixels = pixels;
    }

    public byte[] Pixels => _pixels ?? throw new ObjectDisposedException(nameof(GdiCaptureFrame));

    public static GdiCaptureFrame Capture(int x, int y, int width, int height,
        CancellationToken cancellationToken = default)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        int length = checked(checked(width * height) * 4);
        cancellationToken.ThrowIfCancellationRequested();
        nint screen = GetDC(0), memory = 0, dib = 0, previous = 0;
        if (screen == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot access the desktop.");
        try
        {
            memory = CreateCompatibleDC(screen);
            if (memory == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            dib = CreateDIBSection(screen, ref info, 0, out nint bits, 0, 0);
            if (dib == 0 || bits == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            previous = SelectObject(memory, dib);
            if (previous == 0 || previous == -1) throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (!BitBlt(memory, 0, 0, width, height, screen, x, y, 0x40CC0020))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Desktop capture failed.");
            if (!GdiFlush()) throw new Win32Exception(Marshal.GetLastPInvokeError());
            cancellationToken.ThrowIfCancellationRequested();
            byte[] pixels = new byte[length];
            Marshal.Copy(bits, pixels, 0, length);
            // BI_RGB has no alpha contract. Make every captured pixel opaque.
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return new GdiCaptureFrame(width, height, pixels);
        }
        finally
        {
            if (previous != 0 && previous != -1) SelectObject(memory, previous);
            if (dib != 0) DeleteObject(dib);
            if (memory != 0) DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    public byte[] Crop(int x, int y, int width, int height)
    {
        byte[] source = Pixels;
        if (x < 0 || y < 0 || width <= 0 || height <= 0
            || (long)x + width > Width || (long)y + height > Height)
            throw new ArgumentOutOfRangeException(nameof(width), "Crop must be inside the frozen desktop.");
        int stride = checked(width * 4);
        byte[] result = new byte[checked(stride * height)];
        for (int row = 0; row < height; row++)
            Buffer.BlockCopy(source, checked(((y + row) * Width + x) * 4), result, row * stride, stride);
        return result;
    }

    public void Dispose() => _pixels = null;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant, Color;
    }

    [LibraryImport("user32.dll", SetLastError = true)] private static partial nint GetDC(nint hwnd);
    [LibraryImport("user32.dll")] private static partial int ReleaseDC(nint hwnd, nint dc);
    [LibraryImport("gdi32.dll", SetLastError = true)] private static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll", SetLastError = true)] private static partial nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [LibraryImport("gdi32.dll", SetLastError = true)] private static partial nint SelectObject(nint dc, nint obj);
    [LibraryImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint rop);
    [LibraryImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GdiFlush();
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(nint dc);
}
