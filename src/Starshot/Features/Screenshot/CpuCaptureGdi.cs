using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Starshot.Features.Screenshot;

/// <summary>A CPU DIB and DC with deterministic GDI ownership. This never creates a D3D device.</summary>
internal sealed unsafe class CpuCaptureSurface : IDisposable
{
    internal nint Dc { get; private set; }
    internal nint Bits { get; private set; }
    private nint _bitmap, _previous;
    public int Width { get; }
    public int Height { get; }
    internal Span<byte> Pixels => new((void*)Bits, checked(Width * Height * 4));

    public CpuCaptureSurface(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        _ = checked(width * height * 4);
        Width = width; Height = height;
        try
        {
            Dc = CpuCaptureGdi.CreateCompatibleDC(0);
            if (Dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var info = CpuCaptureGdi.BitmapInfo.For(width, height);
            _bitmap = CpuCaptureGdi.CreateDIBSection(Dc, ref info, 0, out nint bits, 0, 0);
            Bits = bits;
            if (_bitmap == 0 || Bits == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _previous = CpuCaptureGdi.SelectObject(Dc, _bitmap);
            if (_previous == 0 || _previous == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { Dispose(); throw; }
    }

    public void CopyFrom(CpuCaptureImage image, Rectangle source)
    {
        if (source.Size != new Size(Width, Height) || source.Left < 0 || source.Top < 0
            || source.Right > image.Width || source.Bottom > image.Height)
            throw new ArgumentOutOfRangeException(nameof(source));
        CpuCaptureGdi.GdiFlush();
        Span<byte> destination = Pixels;
        int stride = Width * 4;
        for (int y = 0; y < Height; y++)
            image.Pixels.AsSpan(((source.Y + y) * image.Width + source.X) * 4, stride)
                .CopyTo(destination.Slice(y * stride, stride));
    }

    public CpuCaptureImage ToImage(bool opaque = true)
    {
        CpuCaptureGdi.GdiFlush();
        byte[] result = Pixels.ToArray();
        if (opaque) for (int i = 3; i < result.Length; i += 4) result[i] = 255;
        return new CpuCaptureImage(Width, Height, result);
    }

    public void Dispose()
    {
        CpuCaptureGdi.GdiFlush();
        if (Dc != 0 && _previous != 0 && _previous != -1) CpuCaptureGdi.SelectObject(Dc, _previous);
        _previous = 0;
        if (_bitmap != 0) CpuCaptureGdi.DeleteObject(_bitmap);
        _bitmap = 0; Bits = 0;
        if (Dc != 0) CpuCaptureGdi.DeleteDC(Dc);
        Dc = 0;
    }
}

internal static class CpuCaptureGdi
{
    internal const uint Copy = 0x00CC0020;
    internal static uint Rgb(Color c) => (uint)(c.R | c.G << 8 | c.B << 16);

    internal static void Blit(nint dc, byte[] pixels, int width, int height, Rectangle destination, Rectangle source)
    {
        var info = BitmapInfo.For(width, height);
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            if (StretchDIBits(dc, destination.X, destination.Y, destination.Width, destination.Height,
                source.X, source.Y, source.Width, source.Height, pin.AddrOfPinnedObject(), ref info, 0, Copy) == -1)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { pin.Free(); }
    }

    internal static void Fill(nint dc, Rectangle rect, Color color, int radius = 0)
    {
        nint brush = CreateSolidBrush(Rgb(color));
        if (brush == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint oldBrush = SelectObject(dc, brush), oldPen = SelectObject(dc, GetStockObject(8));
        try
        {
            if (radius > 0) RoundRect(dc, rect.Left, rect.Top, rect.Right, rect.Bottom, radius, radius);
            else { var r = new NativeRect(rect); FillRect(dc, ref r, brush); }
        }
        finally { SelectObject(dc, oldPen); SelectObject(dc, oldBrush); DeleteObject(brush); }
    }

    internal static void Shape(nint dc, Rectangle rect, Color color, int width, bool ellipse = false)
    {
        nint pen = CreatePen(0, Math.Max(1, width), Rgb(color));
        if (pen == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint oldPen = SelectObject(dc, pen), oldBrush = SelectObject(dc, GetStockObject(5));
        try
        {
            if (ellipse) Ellipse(dc, rect.Left, rect.Top, rect.Right, rect.Bottom);
            else Rectangle(dc, rect.Left, rect.Top, rect.Right, rect.Bottom);
        }
        finally { SelectObject(dc, oldBrush); SelectObject(dc, oldPen); DeleteObject(pen); }
    }

    internal static void Line(nint dc, Point a, Point b, Color color, int width = 1)
    {
        nint pen = CreatePen(0, Math.Max(1, width), Rgb(color));
        if (pen == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint old = SelectObject(dc, pen);
        try { MoveToEx(dc, a.X, a.Y, 0); LineTo(dc, b.X, b.Y); }
        finally { SelectObject(dc, old); DeleteObject(pen); }
    }

    internal static void Text(nint dc, string text, Rectangle rect, nint font, Color color, bool center = false)
    {
        nint oldFont = SelectObject(dc, font);
        int oldMode = SetBkMode(dc, 1);
        uint oldColor = SetTextColor(dc, Rgb(color));
        try
        {
            var native = new NativeRect(rect);
            DrawTextW(dc, text, text.Length, ref native, 0x20 | 0x4 | 0x800 | (center ? 0x1u : 0));
        }
        finally { SetTextColor(dc, oldColor); SetBkMode(dc, oldMode); SelectObject(dc, oldFont); }
    }

    internal static nint Font(int pixels, string name = "Segoe UI", int weight = 400)
    {
        nint font = CreateFontW(-Math.Max(1, pixels), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, name);
        return font != 0 ? font : throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant, Color;
        internal static BitmapInfo For(int w, int h) => new() { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32 };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        internal NativeRect(Rectangle r) { Left = r.Left; Top = r.Top; Right = r.Right; Bottom = r.Bottom; }
    }

    [DllImport("gdi32.dll", SetLastError = true)] internal static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] internal static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] internal static extern bool GdiFlush();
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int StretchDIBits(nint dc, int x, int y, int w, int h, int sx, int sy, int sw, int sh, nint bits, ref BitmapInfo info, uint usage, uint operation);
    [DllImport("gdi32.dll")] internal static extern bool BitBlt(nint dc, int x, int y, int w, int h, nint source, int sx, int sy, uint operation);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern nint CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref NativeRect rect, nint brush);
    [DllImport("gdi32.dll")] private static extern bool RoundRect(nint dc, int l, int t, int r, int b, int ew, int eh);
    [DllImport("gdi32.dll")] private static extern bool Rectangle(nint dc, int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern bool Ellipse(nint dc, int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern bool MoveToEx(nint dc, int x, int y, nint previous);
    [DllImport("gdi32.dll")] private static extern bool LineTo(nint dc, int x, int y);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawTextW(nint dc, string text, int count, ref NativeRect rect, uint format);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateFontW(int h, int w, int escape, int orientation, int weight, uint italic, uint underline, uint strikeout, uint charset, uint precision, uint clip, uint quality, uint pitch, string name);
}
