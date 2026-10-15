using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace Starshot.Features.Workspace;

/// <summary>CPU alpha masks shared by capture and library editing. No graphics/device dependency.</summary>
public static class ImageMask
{
    public const int MaxPixels = 40_000_000;
    public static void Validate(int width, int height)
    { if (width < 1 || height < 1 || (long)width * height > MaxPixels) throw new ArgumentException("编辑上限为 4000 万像素。"); }

    public static byte[] Polygon(int width, int height, IReadOnlyList<Vector2> points, CancellationToken ct = default)
    {
        Validate(width, height);
        if (points.Count is < 3 or > 20000) throw new ArgumentException("多边形需要至少 3 个顶点。");
        foreach (var point in points) if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || point.X < 0 || point.Y < 0 || point.X > width || point.Y > height) throw new ArgumentException("无效顶点");
        var result = new byte[width * height];
        var crossings = new List<float>();
        for (int y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested(); crossings.Clear(); float scan = y + .5f;
            for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
            {
                var a = points[j]; var b = points[i];
                if ((a.Y > scan) != (b.Y > scan)) crossings.Add(a.X + (scan - a.Y) * (b.X - a.X) / (b.Y - a.Y));
            }
            crossings.Sort();
            for (int i = 0; i + 1 < crossings.Count; i += 2)
            {
                int left = (int)Math.Clamp(Math.Ceiling(crossings[i] - .5), 0, width);
                int right = (int)Math.Clamp(Math.Ceiling(crossings[i + 1] - .5), 0, width);
                if (right > left) result.AsSpan(y * width + left, right - left).Fill(255);
            }
        }
        return result;
    }

    public static byte[] MagicWand(byte[] bgra, int width, int height, int x, int y, int tolerance,
        bool removeBackground = true, CancellationToken ct = default)
    {
        Validate(width,height);
        if (bgra.Length != width * height * 4 || (uint)x >= width || (uint)y >= height) throw new ArgumentException("无效像素或坐标");
        tolerance = Math.Clamp(tolerance, 0, 255);
        var visited = new byte[width * height];
        // Scanline flood fill avoids an image-sized int queue and is cancellable.
        var stack = new Stack<int>(); stack.Push(y * width + x);
        int origin = (y * width + x) * 4;
        int r = bgra[origin+2], g = bgra[origin+1], b = bgra[origin];
        bool Matches(int p) => visited[p] == 0 && Math.Max(Math.Abs(bgra[p*4+2]-r),Math.Max(Math.Abs(bgra[p*4+1]-g),Math.Abs(bgra[p*4]-b))) <= tolerance;
        int operations = 0;
        while (stack.TryPop(out int seed))
        {
            if ((operations++ & 1023) == 0) ct.ThrowIfCancellationRequested();
            if (!Matches(seed)) continue;
            int row = seed / width, left = seed % width, right = left;
            while (left > 0 && Matches(row * width + left-1)) left--;
            while (right+1 < width && Matches(row*width+right+1)) right++;
            bool above = false, below = false;
            for (int column = left; column <= right; column++)
            {
                int p = row*width+column; visited[p] = 255;
                bool a = row > 0 && Matches(p-width), c = row+1 < height && Matches(p+width);
                if (a && !above) stack.Push(p-width);
                if (c && !below) stack.Push(p+width);
                above = a; below = c;
            }
        }
        if (removeBackground) for (int i=0;i<visited.Length;i++) visited[i]=(byte)(255-visited[i]);
        return visited;
    }

    public static byte[] Apply(byte[] bgra, ReadOnlySpan<byte> alpha)
    {
        if (bgra.Length != alpha.Length * 4) throw new ArgumentException("蒙版尺寸不符");
        var output=(byte[])bgra.Clone();
        for(int i=0;i<alpha.Length;i++)
        {
            int a=alpha[i];
            // BGRA source and WIC output use premultiplied alpha.
            for(int c=0;c<4;c++) output[i*4+c]=(byte)((output[i*4+c]*a+127)/255);
        }
        return output;
    }
}
