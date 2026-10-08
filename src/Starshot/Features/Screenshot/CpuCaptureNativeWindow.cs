using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Starshot.Features.Screenshot;

/// <summary>Small Win32 lifetime wrapper. Destroying a window releases its compositor surface.</summary>
public abstract class CpuCaptureNativeWindow : IDisposable
{
    private static readonly Dictionary<nint, CpuCaptureNativeWindow> Windows = new();
    private static readonly CpuCaptureNative.WndProc Procedure = Dispatch;
    private static readonly string ClassName = "Starshot.CpuCapture." + Guid.NewGuid().ToString("N");
    private static bool _registered;
    protected nint Hwnd { get; private set; }
    private bool _disposed;

    protected void Create(string title, int x, int y, int width, int height, bool noActivate = false,
        uint style = 0x80000000, uint extendedStyle = 0x00000088, bool excludeFromCapture = true)
    {
        if (!_registered)
        {
            var cls = new CpuCaptureNative.WindowClass
            {
                Size = (uint)Marshal.SizeOf<CpuCaptureNative.WindowClass>(),
                Style = 8, // CS_DBLCLKS
                Instance = CpuCaptureNative.GetModuleHandleW(null),
                Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
                ClassName = ClassName,
                Cursor = CpuCaptureNative.LoadCursorW(0, (nint)32512),
            };
            if (CpuCaptureNative.RegisterClassExW(ref cls) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _registered = true;
        }
        Hwnd = CpuCaptureNative.CreateWindowExW(extendedStyle | (noActivate ? 0x08000000u : 0),
            ClassName, title, style, x, y, width, height, 0, 0, CpuCaptureNative.GetModuleHandleW(null), 0);
        if (Hwnd == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        Windows.Add(Hwnd, this);
        if (excludeFromCapture) CpuCaptureNative.SetWindowDisplayAffinity(Hwnd, 0x11);
    }

    protected abstract nint? HandleMessage(uint message, nint wparam, nint lparam);
    protected virtual void OnNativeClosed() { }
    protected virtual void OnFailure(Exception ex) { Dispose(); }
    protected void Redraw() { if (Hwnd != 0) CpuCaptureNative.InvalidateRect(Hwnd, 0, false); }

    private static nint Dispatch(nint hwnd, uint message, nint wparam, nint lparam)
    {
        if (!Windows.TryGetValue(hwnd, out var window)) return CpuCaptureNative.DefWindowProcW(hwnd, message, wparam, lparam);
        try
        {
            if (message == 0x82) // WM_NCDESTROY
            {
                Windows.Remove(hwnd); window.Hwnd = 0; window._disposed = true;
                window.OnNativeClosed();
                return CpuCaptureNative.DefWindowProcW(hwnd, message, wparam, lparam);
            }
            return window.HandleMessage(message, wparam, lparam) ?? CpuCaptureNative.DefWindowProcW(hwnd, message, wparam, lparam);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "CPU capture window failed: message={Message}", message);
            try { window.OnFailure(ex); } catch { }
            return 0;
        }
    }

    public virtual void Dispose()
    {
        if (_disposed) return;
        nint hwnd = Hwnd;
        if (hwnd != 0 && !CpuCaptureNative.DestroyWindow(hwnd))
        {
            Serilog.Log.Warning("Failed to destroy CPU capture window: error={Error}", Marshal.GetLastWin32Error());
            // The HWND still owns its callback and resources. Keep it rooted and
            // permit disposal to be retried on the creating thread.
            return;
        }
        _disposed = true;
    }
}

internal static class CpuCaptureNative
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate nint WndProc(nint hwnd, uint message, nint wparam, nint lparam);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate bool EnumProc(nint hwnd, nint data);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        public uint Size, Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName, ClassName;
        public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Paint
    {
        public nint Dc;
        public int Erase;
        public CpuCaptureGdi.NativeRect Rect;
        public int Restore, IncUpdate;
        public long Reserved1, Reserved2, Reserved3, Reserved4;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandleW(string? module);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClassExW(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] internal static extern nint DefWindowProcW(nint hwnd, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] internal static extern bool UpdateWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);
    [DllImport("user32.dll")] internal static extern nint BeginPaint(nint hwnd, out Paint paint);
    [DllImport("user32.dll")] internal static extern bool EndPaint(nint hwnd, ref Paint paint);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
    [DllImport("user32.dll")] internal static extern nint GetCapture();
    [DllImport("user32.dll")] internal static extern short GetKeyState(int key);
    [DllImport("user32.dll")] internal static extern nuint SetTimer(nint hwnd, nuint id, uint ms, nint callback);
    [DllImport("user32.dll")] internal static extern bool KillTimer(nint hwnd, nuint id);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint LoadCursorW(nint instance, nint name);
    [DllImport("user32.dll")] internal static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, nint data);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out CpuCaptureGdi.NativeRect rect);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint hwnd, out CpuCaptureGdi.NativeRect rect);
    [DllImport("user32.dll")] internal static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint SendMessageW(nint hwnd, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool SetWindowTextW(nint hwnd, string text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextW(nint hwnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] internal static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] internal static extern nint CallWindowProcW(nint proc, nint hwnd, uint message, nint wparam, nint lparam);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out uint value, int size);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, ref uint value, int size);
}
