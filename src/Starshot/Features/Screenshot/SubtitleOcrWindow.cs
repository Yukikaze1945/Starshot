using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Starshot.Helpers;

namespace Starshot.Features.Screenshot;

/// <summary>
/// An independent, non-activating subtitle region. The transparent center never takes
/// mouse input. When locked, even the border is click-through. No full-screen overlay.
/// </summary>
internal sealed class SubtitleOcrWindow : CpuCaptureNativeWindow
{
    private const uint WsPopup = 0x80000000;
    private const uint WsThickFrame = 0x00040000;
    private const uint ExLayeredTopmostTool = 0x00080088;
    private static readonly nint TransparentHit = -1;
    private static SubtitleOcrWindow? _current;
    private static string? _lastTranscriptPath;

    private readonly SubtitleOcrCollector _collector = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly string _transcriptPath;
    private nint _font;
    private volatile bool _editing = true, _paused = true;
    private Task? _loop;
    private int _failures;

    public static bool IsRunning => _current is { _editing: false, _paused: false };
    public static string? LastTranscriptPath => _current?._transcriptPath ?? _lastTranscriptPath;

    private SubtitleOcrWindow()
    {
        int screenW = Math.Max(640, GetSystemMetrics(0));
        int screenH = Math.Max(480, GetSystemMetrics(1));
        int width = Math.Min(920, Math.Max(440, screenW * 3 / 5));
        int height = Math.Max(104, screenH / 7);
        int left = (screenW - width) / 2;
        int top = Math.Max(40, screenH * 3 / 4 - height / 2);
        _transcriptPath = Path.Combine(AppConfig.UserDataFolder, "SubtitleOCR",
            $"subtitle-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");

        try
        {
            Create("Starshot Subtitle OCR", left, top, width, height,
                noActivate: true, style: WsPopup | WsThickFrame,
                extendedStyle: ExLayeredTopmostTool, excludeFromCapture: true);
            if (!SetLayeredWindowAttributes(Hwnd, 0u, 255, 1u)) // Color-key black.
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _font = CpuCaptureGdi.Font(Math.Max(12, (int)(13 * Scale)));
            CpuCaptureNative.ShowWindow(Hwnd, 8); // SW_SHOWNA: do not steal focus.
            Redraw();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Initial display or re-edit. No screen-wide screenshot and no focus change.</summary>
    public static void Configure()
    {
        if (_current is not null)
        {
            _current.SetEditing();
            return;
        }
        _current = new SubtitleOcrWindow();
    }

    public static void LockAndStart()
    {
        if (_current is null) { Configure(); return; }
        _current._editing = false;
        _current._paused = false;
        _current.SetPassThrough(true);
        _current.Redraw();
        if (_current._loop is null)
        {
            var window = _current;
            CancellationToken token = window._cancel.Token;
            window._loop = Task.Run(() => window.RunAsync(token), token);
        }
    }

    public static void TogglePause()
    {
        if (_current is not { _editing: false } window) return;
        window._paused = !window._paused;
        window.Redraw();
    }

    public static bool CopyTranscript()
    {
        if (_current is not { } window) return false;
        string text = window._collector.CopyAll();
        if (string.IsNullOrWhiteSpace(text)) return false;
        ClipboardHelper.SetText(text);
        return true;
    }

    public static void OpenTranscript()
    {
        string? path = LastTranscriptPath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void Stop()
    {
        SubtitleOcrWindow? old = _current;
        if (old is null) return;
        _lastTranscriptPath = old._transcriptPath;
        _current = null;
        old.Dispose();
    }

    private void SetEditing()
    {
        _paused = true;
        _editing = true;
        SetPassThrough(false);
        Redraw();
    }

    private void SetPassThrough(bool enable)
    {
        if (Hwnd == 0) return;
        nint transparentStyle = 0x20; // WS_EX_TRANSPARENT
        nint old = GetWindowLongPtrW(Hwnd, -20);
        nint next = enable ? old | transparentStyle : old & ~transparentStyle;
        CpuCaptureNative.SetWindowLongPtrW(Hwnd, -20, next);
        CpuCaptureNative.SetWindowPos(Hwnd, -1, 0, 0, 0, 0, 0x0010 | 0x0002 | 0x0001 | 0x0020);
    }

    private double Scale => Hwnd == 0 ? 1 : Math.Clamp(CpuCaptureNative.GetDpiForWindow(Hwnd) / 96d, 1, 3);
    private int Header => Math.Max(25, (int)Math.Round(26 * Scale));
    private int Edge => Math.Max(4, (int)Math.Round(5 * Scale));

    private Rectangle? CaptureRect()
    {
        if (Hwnd == 0 || !CpuCaptureNative.GetWindowRect(Hwnd, out var bounds)) return null;
        int width = bounds.Right - bounds.Left - Edge * 2;
        int height = bounds.Bottom - bounds.Top - Header - Edge;
        if (width < 80 || height < 16 || (long)width * height > 8_000_000) return null;

        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77);
        int vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        var region = new Rectangle(bounds.Left + Edge, bounds.Top + Header, width, height);
        var desktop = new Rectangle(vx, vy, Math.Max(1, vw), Math.Max(1, vh));
        return desktop.Contains(region) ? region : null;
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (_editing || _paused)
            {
                try { await Task.Delay(180, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                Rectangle? capture = CaptureRect();
                if (capture is null)
                {
                    await Task.Delay(800, token).ConfigureAwait(false);
                    continue;
                }

                Rectangle rect = capture.Value;
                using var frame = await Task.Run(
                    () => GdiCaptureFrame.Capture(rect.X, rect.Y, rect.Width, rect.Height, token), token)
                    .ConfigureAwait(false);
                var lines = await OcrHelper.RecognizeAsync(
                    frame.Pixels, frame.Width, frame.Height, 1.0, token).ConfigureAwait(false);
                if (_editing || _paused || token.IsCancellationRequested) continue;

                var additions = _collector.Observe(
                    (lines ?? []).OrderBy(line => line.Rect.Top).Select(line => line.Text), DateTimeOffset.Now);
                if (additions.Count > 0)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_transcriptPath)!);
                    string data = string.Join(Environment.NewLine, additions.Select(SubtitleOcrCollector.Format))
                                  + Environment.NewLine;
                    await File.AppendAllTextAsync(_transcriptPath, data, Encoding.UTF8, token)
                        .ConfigureAwait(false);
                }
                _failures = 0;
                await Task.Delay(450, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _failures++;
                if (_failures == 1 || _failures % 10 == 0)
                    Serilog.Log.Warning(ex, "Subtitle OCR failed: consecutive={Failures}", _failures);
                try { await Task.Delay(Math.Min(10000, 1200 * _failures), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    protected override nint? HandleMessage(uint message, nint wparam, nint lparam)
    {
        switch (message)
        {
            case 0x0083: return 0; // WM_NCCALCSIZE: client covers the window.
            case 0x0021: return 3; // WM_MOUSEACTIVATE: MA_NOACTIVATE.
            case 0x0084: return HitTest(lparam); // WM_NCHITTEST.
            case 0x000F: PaintBorder(); return 0; // WM_PAINT.
            case 0x0005:
            case 0x0232: Redraw(); return null; // WM_SIZE / WM_EXITSIZEMOVE.
            case 0x0010: Dispose(); return 0; // WM_CLOSE.
            default: return null;
        }
    }

    private nint HitTest(nint param)
    {
        if (_editing == false) return TransparentHit;
        if (!CpuCaptureNative.GetWindowRect(Hwnd, out var bounds)) return TransparentHit;
        int x = (short)((long)param & 0xffff) - bounds.Left;
        int y = (short)(((long)param >> 16) & 0xffff) - bounds.Top;
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        int grip = Edge * 3;
        if (x < 0 || y < 0 || x >= width || y >= height) return TransparentHit;
        if (x >= width - grip && y >= height - grip) return 17; // HTBOTTOMRIGHT
        if (x < grip && y >= height - grip) return 16; // HTBOTTOMLEFT
        if (y >= height - Edge) return 15; // HTBOTTOM
        if (x < Edge) return 10; // HTLEFT
        if (x >= width - Edge) return 11; // HTRIGHT
        if (y < Header) return 2; // HTCAPTION: drag without activating.
        return TransparentHit;
    }

    private void PaintBorder()
    {
        nint dc = CpuCaptureNative.BeginPaint(Hwnd, out var ps);
        try
        {
            if (dc == 0 || !CpuCaptureNative.GetClientRect(Hwnd, out var bounds)) return;
            int w = bounds.Right - bounds.Left, h = bounds.Bottom - bounds.Top;
            if (w < 1 || h < 1) return;
            // Black is fully transparent through the layered-window color key.
            CpuCaptureGdi.Fill(dc, new Rectangle(0, 0, w, h), Color.Black);
            Color accent = _editing ? Color.FromArgb(49, 137, 255)
                : _paused ? Color.FromArgb(196, 150, 70) : Color.FromArgb(39, 195, 130);
            CpuCaptureGdi.Fill(dc, new Rectangle(0, 0, w, Header), Color.FromArgb(30, 37, 47));
            CpuCaptureGdi.Shape(dc, new Rectangle(0, Header - Edge, w - 1, h - Header + Edge - 1), accent, Edge);
            string label = _editing ? "字幕框 · 拖动顶栏、拖角调整 · 从托盘锁定开始"
                : _paused ? "字幕 OCR · 已暂停 · 从托盘继续"
                : "字幕 OCR · 识别中 · 鼠标穿透";
            if (_font != 0)
                CpuCaptureGdi.Text(dc, label, new Rectangle(10, 0, Math.Max(1, w - 20), Header), _font, Color.White);
        }
        finally { CpuCaptureNative.EndPaint(Hwnd, ref ps); }
    }

    protected override void OnNativeClosed()
    {
        _paused = true;
        _cancel.Cancel();
        _cancel.Dispose();
        if (_font != 0) { CpuCaptureGdi.DeleteObject(_font); _font = 0; }
        _lastTranscriptPath = _transcriptPath;
        if (ReferenceEquals(_current, this)) _current = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
