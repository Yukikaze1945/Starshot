using System;
using System.Drawing;
using System.Runtime.InteropServices;
using Starshot.Features.Screenshot;

namespace Starshot.Features.Subtitles;

/// <summary>Passive physical-pixel ROI marker, not another selection window.
/// Four narrow GDI windows avoid a full-screen backing surface. All pixels lie
/// outside the captured rectangle, even if capture exclusion is unavailable.</summary>
internal sealed class SubtitleRegionOutline : IDisposable
{
    private readonly Edge[] _edges;
    internal SubtitleRegionOutline(Rectangle region, bool visible = true)
    {
        _edges = new Edge[4];
        try
        {
            var bounds = EdgeBounds(region);
            for (int i = 0; i < _edges.Length; i++) _edges[i] = new Edge(bounds[i], visible);
        }
        catch { Dispose(); throw; }
    }

    internal static Rectangle[] EdgeBounds(Rectangle r) =>
    [
        new(r.Left - 2, r.Top - 2, r.Width + 4, 2),
        new(r.Left - 2, r.Bottom, r.Width + 4, 2),
        new(r.Left - 2, r.Top, 2, r.Height),
        new(r.Right, r.Top, 2, r.Height)
    ];

    public void Dispose() { foreach (var edge in _edges) edge?.Dispose(); }

    private sealed class Edge : CpuCaptureNativeWindow
    {
        private readonly Size _size;
        internal Edge(Rectangle bounds, bool visible)
        {
            _size = bounds.Size;
            try
            {
                // Layered + transparent passes all pointer input to other processes;
                // NOACTIVATE and SW_SHOWNA leave the player's focus untouched.
                Create("Starshot 字幕范围", bounds.X, bounds.Y, bounds.Width, bounds.Height,
                    noActivate: true, extendedStyle: 0x000800A8);
                if (!SetLayeredWindowAttributes(Hwnd, 0, 255, 2))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
                if (visible) { CpuCaptureNative.ShowWindow(Hwnd, 8); CpuCaptureNative.UpdateWindow(Hwnd); }
            }
            catch { Dispose(); throw; }
        }
        protected override nint? HandleMessage(uint message, nint wparam, nint lparam)
        {
            if (message == 0x84) return -1; // HTTRANSPARENT
            if (message == 0x21) return 3;  // MA_NOACTIVATE
            if (message == 0x14) return 1;  // WM_ERASEBKGND
            if (message != 0x0F) return null;
            nint dc = CpuCaptureNative.BeginPaint(Hwnd, out var paint);
            try { CpuCaptureGdi.Fill(dc, new Rectangle(Point.Empty, _size), Color.FromArgb(221, 243, 105)); }
            finally { CpuCaptureNative.EndPaint(Hwnd, ref paint); }
            return 0;
        }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    }
}
