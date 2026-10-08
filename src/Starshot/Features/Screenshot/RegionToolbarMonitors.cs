using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Starshot.Features.Screenshot;

// Physical monitor coordinates and effective DPI; deliberately independent of WinUI/Win2D.
internal static class RegionToolbarMonitors
{
    private delegate bool MonitorCallback(nint monitor, nint dc, nint rect, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    { public int Left, Top, Right, Bottom; public readonly RectangleF Rect => new(Left, Top, Right - Left, Bottom - Top); }
    [StructLayout(LayoutKind.Sequential)] private struct Info
    { public int Size; public NativeRect Bounds, WorkArea; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(nint monitor, ref Info info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    public static IReadOnlyList<RegionToolbarMonitor> Read(RectangleF fallback, float fallbackScale)
    {
        var result = new List<RegionToolbarMonitor>();
        MonitorCallback callback = (monitor, _, _, _) =>
        {
            var info = new Info { Size = Marshal.SizeOf<Info>() };
            if (GetMonitorInfoW(monitor, ref info))
            {
                float scale = fallbackScale;
                try { if (GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0) scale = dpi / 96f; }
                catch (EntryPointNotFoundException) { }
                result.Add(new(info.Bounds.Rect, info.WorkArea.Rect, Math.Clamp(scale, .5f, 4)));
            }
            return true;
        };
        EnumDisplayMonitors(0, 0, callback, 0);
        return result.Count > 0 ? result : [new(fallback, fallback, fallbackScale)];
    }
}
