using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;

namespace Starshot.Features.Screenshot;

/// <summary>A short-lived, non-activating CPU notification, also used for lightweight capture errors.</summary>
internal sealed class CpuCaptureNotice : CpuCaptureNativeWindow
{
    private static int NextId;
    private readonly nuint _timer = (nuint)Interlocked.Increment(ref NextId) + 0x10000;
    private readonly string _title, _subtitle;
    private readonly string? _file;
    private readonly Action? _action, _closedAction;
    private nint _font, _smallFont;
    private CpuCaptureImage? _thumbnail;
    private readonly float _scale;
    private readonly int _width, _height;

    internal CpuCaptureNotice(string title, CpuCaptureImage? image, Rectangle screen, string? file = null,
        string? subtitle = null, Action? action = null, Action? closedAction = null, int duration = 2200)
    {
        _title = title; _file = file; _subtitle = subtitle ?? (file is null ? "Starshot · 轻量模式" : "点击打开图片");
        _action = action; _closedAction = closedAction;
        try
        {
            Create("Starshot · 截图结果", -32000, -32000, 1, 1, noActivate: true);
            _scale = Math.Clamp(CpuCaptureNative.GetDpiForWindow(Hwnd) / 96f, 1f, 4f);
            _width = (int)(340 * _scale); _height = (int)(86 * _scale);
            _font = CpuCaptureGdi.Font((int)(15 * _scale), weight: 600);
            _smallFont = CpuCaptureGdi.Font((int)(12 * _scale));
            if (image is not null)
            {
                int size = (int)(62 * _scale);
                byte[] bytes = new byte[size * size * 4];
                int side = Math.Min(image.Width, image.Height), ox = (image.Width - side) / 2, oy = (image.Height - side) / 2;
                for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
                    image.Pixels.AsSpan(((oy + y * side / size) * image.Width + ox + x * side / size) * 4, 4)
                        .CopyTo(bytes.AsSpan((y * size + x) * 4, 4));
                _thumbnail = new(size, size, bytes);
            }
            CpuCaptureNative.SetWindowPos(Hwnd, -1, screen.Right - _width - (int)(12 * _scale),
                screen.Bottom - _height - (int)(12 * _scale), _width, _height, 0x0010);
            CpuCaptureNative.ShowWindow(Hwnd, 4); CpuCaptureNative.UpdateWindow(Hwnd);
            if (duration > 0 && CpuCaptureNative.SetTimer(Hwnd, _timer, (uint)duration, 0) == 0) Dispose();
        }
        catch { Dispose(); throw; }
    }

    protected override nint? HandleMessage(uint message, nint wparam, nint lparam)
    {
        if (message == 0x14) return 1;
        if (message == 0x113 && (nuint)wparam == _timer) { Dispose(); return 0; }
        if (message == 0x201)
        {
            int x = (short)((long)lparam & 0xffff);
            if (x < _width - (int)(28 * _scale))
            {
                if (_file is not null) Process.Start(new ProcessStartInfo(_file) { UseShellExecute = true });
                _action?.Invoke();
            }
            Dispose(); return 0;
        }
        if (message == 0x0F)
        {
            nint dc = CpuCaptureNative.BeginPaint(Hwnd, out var paint);
            try
            {
                CpuCaptureGdi.Fill(dc, new(0, 0, _width, _height), Color.FromArgb(215, 221, 206));
                CpuCaptureGdi.Fill(dc, new(1, 1, _width - 2, _height - 2), Color.FromArgb(249, 250, 243));
                int gap = (int)(12 * _scale), textX = gap;
                if (_thumbnail is not null)
                {
                    var r = new Rectangle(gap, gap, _thumbnail.Width, _thumbnail.Height);
                    CpuCaptureGdi.Blit(dc, _thumbnail.Pixels, _thumbnail.Width, _thumbnail.Height, r, new(0, 0, r.Width, r.Height));
                    textX += _thumbnail.Width + gap;
                }
                CpuCaptureGdi.Text(dc, _title, new(textX, gap, _width - textX - gap, (int)(30 * _scale)), _font, Color.FromArgb(36, 43, 36));
                CpuCaptureGdi.Text(dc, _subtitle, new(textX, gap + (int)(32 * _scale), _width - textX - gap, (int)(24 * _scale)), _smallFont, Color.FromArgb(113, 123, 101));
                CpuCaptureGdi.Text(dc, "×", new(_width - (int)(27 * _scale), 1, (int)(24 * _scale), (int)(24 * _scale)), _smallFont, Color.FromArgb(113, 123, 101), center: true);
            }
            finally { CpuCaptureNative.EndPaint(Hwnd, ref paint); }
            return 0;
        }
        return null;
    }
    protected override void OnNativeClosed()
    {
        _thumbnail?.Dispose(); _thumbnail = null;
        if (_font != 0) CpuCaptureGdi.DeleteObject(_font);
        if (_smallFont != 0) CpuCaptureGdi.DeleteObject(_smallFont);
        _font = _smallFont = 0;
        try { _closedAction?.Invoke(); } catch (Exception ex) { Serilog.Log.Warning(ex, "Native notice close callback failed"); }
    }
    public override void Dispose() { if (Hwnd != 0) CpuCaptureNative.KillTimer(Hwnd, _timer); base.Dispose(); }
}
