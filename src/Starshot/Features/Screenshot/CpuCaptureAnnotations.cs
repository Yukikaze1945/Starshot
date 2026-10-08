using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace Starshot.Features.Screenshot;

internal enum CpuAnnotationTool { Select, Rectangle, Ellipse, Line, Arrow, Number, Pen, Highlighter, Mosaic, Blur, Text, Eraser }

internal sealed class CpuAnnotation
{
    public CpuAnnotationTool Tool;
    public Point Start, End;
    public Color Color = Color.FromArgb(255, 255, 64, 77);
    public int Width = 3;
    public string Text = "";
    public List<Point> Points = new();
    public CpuAnnotation Clone() => new() { Tool = Tool, Start = Start, End = End,
        Color = Color, Width = Width, Text = Text, Points = new(Points) };
    public void Translate(int x, int y)
    {
        Start.Offset(x, y); End.Offset(x, y);
        for (int i = 0; i < Points.Count; i++) Points[i] = new Point(Points[i].X + x, Points[i].Y + y);
    }
    public Rectangle Bounds(int padding = 8)
    {
        int left = Math.Min(Start.X, End.X), top = Math.Min(Start.Y, End.Y);
        int right = Math.Max(Start.X, End.X), bottom = Math.Max(Start.Y, End.Y);
        foreach (var p in Points) { left = Math.Min(left, p.X); top = Math.Min(top, p.Y); right = Math.Max(right, p.X); bottom = Math.Max(bottom, p.Y); }
        if (Tool == CpuAnnotationTool.Text) { right = Math.Max(right, left + 300); bottom = Math.Max(bottom, top + 60); }
        return Rectangle.FromLTRB(left - padding, top - padding, right + padding + 1, bottom + padding + 1);
    }
}

internal static class CpuRegionSelection
{
    public static Rectangle Drag(Point start, Point end, Size desktop) => Rectangle.FromLTRB(
        Math.Clamp(Math.Min(start.X, end.X), 0, desktop.Width - 1),
        Math.Clamp(Math.Min(start.Y, end.Y), 0, desktop.Height - 1),
        Math.Clamp(Math.Max(start.X, end.X) + 1, 1, desktop.Width),
        Math.Clamp(Math.Max(start.Y, end.Y) + 1, 1, desktop.Height));

    public static Point[] Handles(Rectangle r) => [new(r.Left, r.Top), new(r.Left + r.Width / 2, r.Top),
        new(r.Right, r.Top), new(r.Left, r.Top + r.Height / 2), new(r.Right, r.Top + r.Height / 2),
        new(r.Left, r.Bottom), new(r.Left + r.Width / 2, r.Bottom), new(r.Right, r.Bottom)];

    public static Rectangle Resize(Rectangle start, int handle, Point delta, Size desktop)
    {
        int l = start.Left, t = start.Top, r = start.Right, b = start.Bottom;
        if (handle is 0 or 3 or 5) l = Math.Clamp(l + delta.X, 0, r - 2);
        if (handle is 2 or 4 or 7) r = Math.Clamp(r + delta.X, l + 2, desktop.Width);
        if (handle is 0 or 1 or 2) t = Math.Clamp(t + delta.Y, 0, b - 2);
        if (handle is 5 or 6 or 7) b = Math.Clamp(b + delta.Y, t + 2, desktop.Height);
        return Rectangle.FromLTRB(l, t, r, b);
    }
}

/// <summary>Software annotations shared by the on-screen DIB and exported CPU pixels.</summary>
internal static class CpuCaptureRaster
{
    public static CpuCaptureImage Render(CpuCaptureImage source, Rectangle crop,
        IEnumerable<CpuAnnotation> annotations, float scale)
    {
        using var surface = new CpuCaptureSurface(crop.Width, crop.Height);
        surface.CopyFrom(source, crop);
        Draw(surface, source, crop, annotations, scale);
        return surface.ToImage();
    }

    public static CpuCaptureImage RenderLayer(CpuCaptureImage source, Rectangle crop,
        IEnumerable<CpuAnnotation> annotations, float scale)
    {
        using var surface = new CpuCaptureSurface(crop.Width, crop.Height);
        // GDI writes RGB without alpha. A non-palette sentinel makes opaque black ink
        // distinguishable from untouched transparent pixels when fixing alpha.
        Span<byte> pixels = surface.Pixels;
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 3; pixels[i + 1] = 2; pixels[i + 2] = 1; pixels[i + 3] = 0; }
        foreach (var stroke in annotations)
        {
            DrawStroke(surface, source, crop, stroke, scale, transparent: true);
            CpuCaptureGdi.GdiFlush();
            if (stroke.Tool != CpuAnnotationTool.Highlighter)
                for (int i = 0; i < pixels.Length; i += 4)
                    if (pixels[i + 3] == 0 && (pixels[i] != 3 || pixels[i + 1] != 2 || pixels[i + 2] != 1)) pixels[i + 3] = 255;
        }
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i + 3] == 0) pixels.Slice(i, 4).Clear();
        return surface.ToImage(opaque: false);
    }

    public static void Draw(CpuCaptureSurface surface, CpuCaptureImage source, Rectangle crop,
        IEnumerable<CpuAnnotation> annotations, float scale)
    {
        foreach (var stroke in annotations) DrawStroke(surface, source, crop, stroke, scale, transparent: false);
    }

    private static void DrawStroke(CpuCaptureSurface surface, CpuCaptureImage source, Rectangle crop,
        CpuAnnotation stroke, float scale, bool transparent)
    {
        Point P(Point p) => new(p.X - crop.X, p.Y - crop.Y);
        Point start = P(stroke.Start), end = P(stroke.End);
        Rectangle r = Rectangle.FromLTRB(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
            Math.Max(start.X, end.X) + 1, Math.Max(start.Y, end.Y) + 1);
        nint dc = surface.Dc;
        switch (stroke.Tool)
        {
            case CpuAnnotationTool.Rectangle: CpuCaptureGdi.Shape(dc, r, stroke.Color, stroke.Width); break;
            case CpuAnnotationTool.Ellipse: CpuCaptureGdi.Shape(dc, r, stroke.Color, stroke.Width, ellipse: true); break;
            case CpuAnnotationTool.Line: CpuCaptureGdi.Line(dc, start, end, stroke.Color, stroke.Width); break;
            case CpuAnnotationTool.Arrow:
                CpuCaptureGdi.Line(dc, start, end, stroke.Color, stroke.Width);
                double angle = Math.Atan2(end.Y - start.Y, end.X - start.X), length = 15 * scale;
                foreach (double offset in new[] { -.45, .45 })
                    CpuCaptureGdi.Line(dc, end, new Point(end.X - (int)(Math.Cos(angle + offset) * length),
                        end.Y - (int)(Math.Sin(angle + offset) * length)), stroke.Color, stroke.Width);
                break;
            case CpuAnnotationTool.Number:
                int radius = (int)(13 * scale);
                var circle = new Rectangle(start.X - radius, start.Y - radius, radius * 2, radius * 2);
                CpuCaptureGdi.Fill(dc, circle, stroke.Color, radius * 2);
                DrawText(dc, stroke.Text, circle, (int)(16 * scale), Color.White, center: true);
                break;
            case CpuAnnotationTool.Text:
                DrawText(dc, stroke.Text, new Rectangle(start.X, start.Y, Math.Max(1, surface.Width - start.X),
                    (int)(40 * scale)), (int)(24 * scale), stroke.Color);
                break;
            case CpuAnnotationTool.Pen:
                if (stroke.Points.Count < 2) CpuCaptureGdi.Line(dc, start, new Point(start.X + 1, start.Y), stroke.Color, stroke.Width);
                for (int i = 1; i < stroke.Points.Count; i++) CpuCaptureGdi.Line(dc, P(stroke.Points[i - 1]), P(stroke.Points[i]), stroke.Color, stroke.Width);
                break;
            case CpuAnnotationTool.Highlighter:
                CpuCaptureGdi.GdiFlush();
                Marker(surface, stroke, crop.Location, transparent);
                break;
            case CpuAnnotationTool.Mosaic:
            case CpuAnnotationTool.Blur:
                int cell = Math.Max(2, (int)((stroke.Tool == CpuAnnotationTool.Mosaic ? 10 : 6) * scale));
                var visited = new HashSet<(int, int)>();
                foreach (Point p in stroke.Points)
                for (int y = p.Y / cell - 1; y <= p.Y / cell + 1; y++)
                for (int x = p.X / cell - 1; x <= p.X / cell + 1; x++)
                {
                    if (!visited.Add((x, y))) continue;
                    var tile = Rectangle.Intersect(crop, new Rectangle(x * cell, y * cell, cell, cell));
                    if (tile.IsEmpty) continue;
                    Color color = Sample(source, tile.X + tile.Width / 2, tile.Y + tile.Height / 2);
                    if (stroke.Tool == CpuAnnotationTool.Blur)
                    {
                        int rr = 0, gg = 0, bb = 0;
                        for (int yy = -1; yy <= 1; yy++) for (int xx = -1; xx <= 1; xx++)
                        { var c = Sample(source, tile.X + xx * cell, tile.Y + yy * cell); rr += c.R; gg += c.G; bb += c.B; }
                        color = Color.FromArgb(rr / 9, gg / 9, bb / 9);
                    }
                    tile.Offset(-crop.X, -crop.Y);
                    // Sampled colors can equal the transparent layer's sentinel.
                    // Write their coverage explicitly instead of inferring it from RGB.
                    CpuCaptureGdi.GdiFlush();
                    Span<byte> data = surface.Pixels;
                    for (int yy = tile.Top; yy < tile.Bottom; yy++)
                    for (int xx = tile.Left; xx < tile.Right; xx++)
                    { int offset = (yy * surface.Width + xx) * 4; data[offset] = color.B; data[offset + 1] = color.G; data[offset + 2] = color.R; data[offset + 3] = 255; }
                }
                break;
        }
    }

    private static void DrawText(nint dc, string text, Rectangle r, int size, Color color, bool center = false)
    {
        nint font = CpuCaptureGdi.Font(size);
        try { CpuCaptureGdi.Text(dc, text, r, font, color, center); }
        finally { CpuCaptureGdi.DeleteObject(font); }
    }

    internal static Color Sample(CpuCaptureImage source, int x, int y)
    {
        int i = (Math.Clamp(y, 0, source.Height - 1) * source.Width + Math.Clamp(x, 0, source.Width - 1)) * 4;
        byte[] p = source.Pixels;
        return Color.FromArgb(p[i + 2], p[i + 1], p[i]);
    }

    private static void Marker(CpuCaptureSurface surface, CpuAnnotation stroke, Point origin, bool transparent)
    {
        var points = stroke.Points.Count > 0 ? stroke.Points : [stroke.Start, stroke.End];
        var painted = new HashSet<int>();
        int radius = Math.Max(2, stroke.Width * 3);
        Span<byte> pixels = surface.Pixels;
        for (int segment = 0; segment < Math.Max(1, points.Count - 1); segment++)
        {
            Point a = points[segment], b = points[Math.Min(points.Count - 1, segment + 1)];
            int steps = Math.Max(1, Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)));
            for (int s = 0; s <= steps; s++)
            {
                int cx = a.X + (b.X - a.X) * s / steps - origin.X;
                int cy = a.Y + (b.Y - a.Y) * s / steps - origin.Y;
                for (int y = Math.Max(0, cy - radius); y <= Math.Min(surface.Height - 1, cy + radius); y++)
                for (int x = Math.Max(0, cx - radius); x <= Math.Min(surface.Width - 1, cx + radius); x++)
                {
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > radius * radius) continue;
                    int i = (y * surface.Width + x) * 4;
                    if (!painted.Add(i)) continue;
                    int alpha = 96, inv = 255 - alpha;
                    bool blank = transparent && pixels[i + 3] == 0;
                    pixels[i] = (byte)((stroke.Color.B * alpha + (blank ? 0 : pixels[i] * inv)) / 255);
                    pixels[i + 1] = (byte)((stroke.Color.G * alpha + (blank ? 0 : pixels[i + 1] * inv)) / 255);
                    pixels[i + 2] = (byte)((stroke.Color.R * alpha + (blank ? 0 : pixels[i + 2] * inv)) / 255);
                    pixels[i + 3] = transparent ? (byte)(alpha + (blank ? 0 : pixels[i + 3] * inv / 255)) : (byte)255;
                }
            }
        }
    }
}
