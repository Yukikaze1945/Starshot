using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Starshot.Helpers;

namespace Starshot.Features.Screenshot;

internal sealed record CpuRegionCaptureResult(RegionCaptureAction Action, Rectangle PhysicalRect,
    CpuCaptureImage? Image = null, CpuCaptureImage? AnnotationLayer = null);

/// <summary>
/// Lightweight selection uses CPU DIBs and a short-lived Win32 window. There is no WinUI
/// visual tree, CanvasBitmap, CanvasSwapChain, WGC session or graphics-device dependency.
/// </summary>
internal sealed class CpuRegionCaptureWindow : CpuCaptureNativeWindow
{
    private static int NextGeneration;
    private readonly int _generation = Interlocked.Increment(ref NextGeneration);
    private nuint MoveTimerId => (nuint)_generation + 1;
    private readonly int _virtualX, _virtualY;
    private readonly RegionCaptureAction _defaultAction;
    private CpuCaptureImage? _source;
    private byte[]? _dark;
    private readonly Size _desktop;
    private readonly nint _previousForeground;
    private nint _font, _smallFont, _iconFont, _textEdit, _editOriginal;
    private CpuCaptureNative.WndProc? _editProcedure;
    private readonly float _scale;
    private readonly List<Rectangle> _windows = new();
    private readonly List<CpuAnnotation> _annotations = new();
    private readonly Stack<List<CpuAnnotation>> _undo = new(), _redo = new();
    private CpuAnnotation? _draft;
    private CpuCaptureSurface? _preview;
    private bool _previewDirty = true, _selected, _dragging, _pendingMoveIn, _closed;
    private Point _mouse, _dragStart;
    private Rectangle _selection, _dragRect, _toolbar;
    private int _handle = -1, _hoverButton = -1, _colorIndex;
    private CpuAnnotationTool _tool;
    private string _tip = "";
    private static readonly Color Paper = Color.FromArgb(249, 250, 243), Ink = Color.FromArgb(36, 43, 36),
        Border = Color.FromArgb(215, 221, 206), Accent = Color.FromArgb(221, 243, 105);
    private static readonly Color[] Colors = [Color.FromArgb(255, 64, 77), Color.FromArgb(49, 137, 255),
        Color.FromArgb(255, 225, 25), Color.White, Color.Black];
    private sealed record ToolButton(string Label, string Glyph, CpuAnnotationTool? Tool = null,
        RegionCaptureAction? Action = null, bool Icon = true);
    private static readonly ToolButton[] Buttons =
    [
        new("移动选区", "\uE762", CpuAnnotationTool.Select), new("矩形", "\uE7F8", CpuAnnotationTool.Rectangle),
        new("椭圆", "\uEA3A", CpuAnnotationTool.Ellipse), new("直线", "╱", CpuAnnotationTool.Line, Icon: false),
        new("箭头", "↗", CpuAnnotationTool.Arrow, Icon: false), new("序号", "①", CpuAnnotationTool.Number, Icon: false),
        new("画笔", "\uED63", CpuAnnotationTool.Pen), new("高亮", "\uE7E6", CpuAnnotationTool.Highlighter),
        new("马赛克", "\uE80A", CpuAnnotationTool.Mosaic), new("模糊", "\uE7A8", CpuAnnotationTool.Blur),
        new("文字", "T", CpuAnnotationTool.Text, Icon: false), new("删除标注", "\uE75C", CpuAnnotationTool.Eraser),
        new("颜色", "●", Icon: false), new("撤销 Ctrl+Z", "↶", Icon: false), new("重做 Ctrl+Y", "↷", Icon: false),
        new("贴图 Ctrl+T", "\uE718", Action: RegionCaptureAction.Pin),
        new("录制 GIF · G", "GIF", Action: RegionCaptureAction.RecordGif, Icon: false),
        new("长截图 · L", "⇵", Action: RegionCaptureAction.LongCapture, Icon: false),
        new("重新选区", "\uE8B7"), new("复制 Ctrl+C", "\uE8C8", Action: RegionCaptureAction.Copy),
        new("保存 Ctrl+S", "\uE74E", Action: RegionCaptureAction.Save),
        new("文字识别 Shift+C", "OCR", Action: RegionCaptureAction.Ocr, Icon: false),
        new("识别并翻译 Ctrl+Q", "译", Action: RegionCaptureAction.Translate, Icon: false),
        new("取消 Esc", "\uE8BB", Action: RegionCaptureAction.Cancel),
    ];
    public TaskCompletionSource<CpuRegionCaptureResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CpuRegionCaptureWindow(CpuCaptureImage source, int virtualX, int virtualY,
        RegionCaptureAction defaultAction, bool moveOnscreen = true)
    {
        _source = source; _desktop = new(source.Width, source.Height);
        _virtualX = virtualX; _virtualY = virtualY; _defaultAction = defaultAction;
        _previousForeground = CpuCaptureNative.GetForegroundWindow();
        try
        {
            Create("Starshot · 轻量选区", -32000, -32000, source.Width, source.Height);
            _scale = Math.Clamp(CpuCaptureNative.GetDpiForWindow(Hwnd) / 96f, 1f, 4f);
            _font = CpuCaptureGdi.Font(Dip(14)); _smallFont = CpuCaptureGdi.Font(Dip(12));
            _iconFont = CpuCaptureGdi.Font(Dip(18), "Segoe MDL2 Assets");
            _dark = (byte[])source.Pixels.Clone();
            for (int i = 0; i < _dark.Length; i += 4)
            { _dark[i] = (byte)(_dark[i] * 58 / 100); _dark[i + 1] = (byte)(_dark[i + 1] * 58 / 100); _dark[i + 2] = (byte)(_dark[i + 2] * 58 / 100); }
            EnumerateWindows();
            UpdateMouse();
            CpuCaptureNative.ShowWindow(Hwnd, 4);
            CpuCaptureNative.UpdateWindow(Hwnd); // First CPU paint while offscreen; never reveal a stale frame.
            Serilog.Log.Information("CPU region initial paint done: generation={Generation}, size={Width}x{Height}; no GPU upload", _generation, source.Width, source.Height);
            if (moveOnscreen)
            {
                _pendingMoveIn = true;
                if (CpuCaptureNative.SetTimer(Hwnd, MoveTimerId, 24, 0) == 0)
                    throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            }
        }
        catch { Dispose(); throw; }
    }

    private int Dip(float value) => Math.Max(1, (int)Math.Round(value * _scale));
    private static bool Down(int key) => (CpuCaptureNative.GetKeyState(key) & 0x8000) != 0;
    private void EnumerateWindows()
    {
        CpuCaptureNative.EnumWindows((hwnd, _) =>
        {
            if (hwnd == Hwnd || !CpuCaptureNative.IsWindowVisible(hwnd) || CpuCaptureNative.IsIconic(hwnd)) return true;
            if (CpuCaptureNative.DwmGetWindowAttribute(hwnd, 14, out uint cloaked, 4) == 0 && cloaked != 0) return true;
            if (CpuCaptureNative.GetWindowRect(hwnd, out var r))
            {
                var rect = Rectangle.Intersect(new Rectangle(0, 0, _desktop.Width, _desktop.Height),
                    Rectangle.FromLTRB(r.Left - _virtualX, r.Top - _virtualY, r.Right - _virtualX, r.Bottom - _virtualY));
                if (rect.Width >= 2 && rect.Height >= 2) _windows.Add(rect);
            }
            return true;
        }, 0);
    }

    protected override nint? HandleMessage(uint message, nint wparam, nint lparam)
    {
        switch (message)
        {
            case 0x14: return 1; // erase handled by the frozen DIB
            case 0x0F: Paint(); return 0;
            case 0x10: Finish(RegionCaptureAction.Cancel); return 0;
            case 0x113 when (nuint)wparam == MoveTimerId:
                CpuCaptureNative.KillTimer(Hwnd, MoveTimerId);
                if (_pendingMoveIn && !_closed)
                {
                    _pendingMoveIn = false;
                    CpuCaptureNative.SetWindowPos(Hwnd, -1, _virtualX, _virtualY, _desktop.Width, _desktop.Height, 0x0010);
                    CpuCaptureNative.SetForegroundWindow(Hwnd); CpuCaptureNative.SetFocus(Hwnd);
                    Serilog.Log.Information("CPU region MoveOnscreen/Activate done: generation={Generation}, x={X}, y={Y}", _generation, _virtualX, _virtualY);
                }
                return 0;
            case 0x20:
                CpuCaptureNative.SetCursor(CpuCaptureNative.LoadCursorW(0, (nint)(_hoverButton >= 0 ? 32512 : 32515))); return 1;
            case 0x200:
                _mouse = new Point((short)((long)lparam & 0xffff), (short)(((long)lparam >> 16) & 0xffff));
                PointerMoved(); return 0;
            case 0x201: PointerDown(); return 0;
            case 0x202: PointerUp(); return 0;
            case 0x203: if (_selected && _selection.Contains(_mouse)) Finish(_defaultAction); return 0;
            case 0x205:
                if (_textEdit != 0) CommitText();
                if (_selected) Reselect(); else if (_dragging) { _dragging = false; _selection = default; CpuCaptureNative.ReleaseCapture(); Redraw(); }
                else Finish(RegionCaptureAction.Cancel);
                return 0;
            case 0x100: if (Key((int)wparam)) return 0; break;
            case 0x215: // unexpected capture loss must not leave an unfinished stroke
                if (_dragging) { _dragging = false; CommitDraft(); Redraw(); } return 0;
        }
        return null;
    }

    private void Paint()
    {
        nint dc = CpuCaptureNative.BeginPaint(Hwnd, out var paint);
        try
        {
            if (_source is null || _dark is null || _closed) return;
            var full = new Rectangle(0, 0, _desktop.Width, _desktop.Height);
            CpuCaptureGdi.Blit(dc, _dark, _desktop.Width, _desktop.Height, full, full);
            Rectangle r = _selected || _dragging ? _selection : HoverRectangle();
            if (r.Width > 1 && r.Height > 1)
            {
                if (_annotations.Count > 0 || _draft is not null)
                {
                    EnsurePreview();
                    if (_preview is not null) CpuCaptureGdi.BitBlt(dc, r.X, r.Y, r.Width, r.Height, _preview.Dc, 0, 0, CpuCaptureGdi.Copy);
                }
                else CpuCaptureGdi.Blit(dc, _source.Pixels, _source.Width, _source.Height, r, r);
                CpuCaptureGdi.Shape(dc, r, Accent, Dip(1));
                if (_selected)
                    foreach (Point p in CpuRegionSelection.Handles(r))
                    { var handle = new Rectangle(p.X - Dip(3), p.Y - Dip(3), Dip(6), Dip(6)); CpuCaptureGdi.Fill(dc, handle, Accent); CpuCaptureGdi.Shape(dc, handle, Ink, 1); }
                int labelW = Dip(260), labelH = Dip(28);
                var label = new Rectangle(Math.Clamp(r.X, 0, Math.Max(0, _desktop.Width - labelW)), Math.Max(0, r.Top - labelH - Dip(6)), labelW, labelH);
                CpuCaptureGdi.Fill(dc, label, Paper, Dip(8));
                CpuCaptureGdi.Text(dc, $"{r.X + _virtualX},{r.Y + _virtualY}   {r.Width} × {r.Height} px", new Rectangle(label.X + Dip(10), label.Y, label.Width - Dip(20), label.Height), _smallFont, Ink);
            }
            if (_selected) PaintToolbar(dc);
            if (!_selected || _dragging) PaintMagnifier(dc);
            var hints = new Rectangle(Dip(12), Math.Max(0, _desktop.Height - Dip(82)), Math.Min(Dip(640), _desktop.Width - Dip(24)), Dip(66));
            CpuCaptureGdi.Fill(dc, hints, Paper, Dip(10));
            CpuCaptureGdi.Text(dc, "方向键 移动 · Shift 缩小 · Ctrl 扩大 · C 复制颜色", new(hints.X + Dip(12), hints.Y + Dip(7), hints.Width - Dip(24), Dip(23)), _smallFont, Ink);
            CpuCaptureGdi.Text(dc, "Ctrl+Z/Y 撤销/重做 · Ctrl+C/S 复制/保存 · Ctrl+Q 翻译 · Esc 退出", new(hints.X + Dip(12), hints.Y + Dip(32), hints.Width - Dip(24), Dip(23)), _smallFont, Ink);
        }
        finally { CpuCaptureNative.EndPaint(Hwnd, ref paint); }
    }

    private Rectangle HoverRectangle() => _windows.FirstOrDefault(r => r.Contains(_mouse), new Rectangle(0, 0, _desktop.Width, _desktop.Height));
    private void EnsurePreview()
    {
        if (_source is null || _selection.Width < 2 || _selection.Height < 2) return;
        if (_preview is not null && (_preview.Width != _selection.Width || _preview.Height != _selection.Height)) { _preview.Dispose(); _preview = null; }
        _preview ??= new CpuCaptureSurface(_selection.Width, _selection.Height);
        if (!_previewDirty) return;
        _preview.CopyFrom(_source, _selection);
        CpuCaptureRaster.Draw(_preview, _source, _selection, AllAnnotations(), _scale);
        _previewDirty = false;
    }
    private IEnumerable<CpuAnnotation> AllAnnotations() => _draft is null ? _annotations : _annotations.Concat([_draft]);

    private void LayoutToolbar()
    {
        int cell = Math.Min(Dip(32), Math.Max(18, (_desktop.Width - Dip(24)) / Buttons.Length));
        int w = cell * Buttons.Length + Dip(12), h = Dip(48);
        int x = Math.Clamp(_selection.Right - w, 0, Math.Max(0, _desktop.Width - w));
        int y = _selection.Bottom + Dip(10);
        if (y + h + Dip(36) > _desktop.Height) y = Math.Max(0, _selection.Top - h - Dip(10));
        _toolbar = new Rectangle(x, y, w, h);
    }
    private Rectangle ButtonRect(int index)
    {
        int cell = (_toolbar.Width - Dip(12)) / Buttons.Length;
        return new Rectangle(_toolbar.X + Dip(6) + index * cell, _toolbar.Y + Dip(6), cell, _toolbar.Height - Dip(12));
    }
    private void PaintToolbar(nint dc)
    {
        LayoutToolbar();
        CpuCaptureGdi.Fill(dc, new Rectangle(_toolbar.X - 1, _toolbar.Y - 1, _toolbar.Width + 2, _toolbar.Height + 2), Border, Dip(10));
        CpuCaptureGdi.Fill(dc, _toolbar, Paper, Dip(10));
        for (int i = 0; i < Buttons.Length; i++)
        {
            var button = Buttons[i]; Rectangle r = ButtonRect(i);
            bool active = button.Tool == _tool || button.Action == _defaultAction;
            if (active || i == _hoverButton) CpuCaptureGdi.Fill(dc, r, active ? Accent : Color.FromArgb(232, 236, 217), Dip(6));
            CpuCaptureGdi.Text(dc, button.Glyph, r, button.Icon ? _iconFont : (button.Glyph.Length > 1 ? _smallFont : _font), i == 12 ? Colors[_colorIndex] : Ink, center: true);
        }
        if (_tip.Length > 0)
        {
            int w = Math.Min(Dip(320), _desktop.Width), h = Dip(26);
            var notice = new Rectangle(Math.Clamp(_mouse.X - w / 2, 0, _desktop.Width - w),
                _toolbar.Bottom + h + Dip(8) < _desktop.Height ? _toolbar.Bottom + Dip(6) : Math.Max(0, _toolbar.Top - h - Dip(6)), w, h);
            CpuCaptureGdi.Fill(dc, notice, Paper, Dip(7)); CpuCaptureGdi.Text(dc, _tip, notice, _smallFont, Ink, center: true);
        }
    }

    private void PaintMagnifier(nint dc)
    {
        if (_source is null) return;
        int cell = Dip(7), count = 15, size = cell * count, w = Math.Max(size, Dip(180)), h = size + Dip(64);
        int x = _mouse.X + Dip(24), y = _mouse.Y + Dip(24);
        if (x + w > _desktop.Width) x = _mouse.X - w - Dip(24);
        if (y + h > _desktop.Height) y = _mouse.Y - h - Dip(24);
        x = Math.Clamp(x, 0, Math.Max(0, _desktop.Width - w)); y = Math.Clamp(y, 0, Math.Max(0, _desktop.Height - h));
        var box = new Rectangle(x, y, w, h); CpuCaptureGdi.Fill(dc, box, Paper, Dip(8));
        int imageX = x + (w - size) / 2;
        for (int row = 0; row < count; row++) for (int col = 0; col < count; col++)
            CpuCaptureGdi.Fill(dc, new Rectangle(imageX + col * cell, y + row * cell, cell, cell), CpuCaptureRaster.Sample(_source, _mouse.X + col - 7, _mouse.Y + row - 7));
        CpuCaptureGdi.Line(dc, new(imageX + size / 2, y), new(imageX + size / 2, y + size), Accent);
        CpuCaptureGdi.Line(dc, new(imageX, y + size / 2), new(imageX + size, y + size / 2), Accent);
        var color = CpuCaptureRaster.Sample(_source, _mouse.X, _mouse.Y);
        string rgb = Down(0x10) ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : $"RGB {color.R}, {color.G}, {color.B}";
        CpuCaptureGdi.Text(dc, $"({_mouse.X + _virtualX}, {_mouse.Y + _virtualY})", new(x, y + size + Dip(3), w, Dip(20)), _smallFont, Ink, center: true);
        CpuCaptureGdi.Text(dc, rgb, new(x, y + size + Dip(23), w, Dip(20)), _smallFont, Ink, center: true);
        CpuCaptureGdi.Text(dc, "Shift 切换格式 · C 复制颜色", new(x, y + size + Dip(43), w, Dip(20)), _smallFont, Ink, center: true);
    }

    private void UpdateMouse()
    {
        if (CpuCaptureNative.GetCursorPos(out var p)) _mouse = new Point(Math.Clamp(p.X - _virtualX, 0, _desktop.Width - 1), Math.Clamp(p.Y - _virtualY, 0, _desktop.Height - 1));
    }
    private void PointerMoved()
    {
        _hoverButton = _selected ? Enumerable.Range(0, Buttons.Length).FirstOrDefault(i => ButtonRect(i).Contains(_mouse), -1) : -1;
        _tip = _hoverButton < 0 ? "" : Buttons[_hoverButton].Label;
        if (_dragging)
        {
            Point delta = new(_mouse.X - _dragStart.X, _mouse.Y - _dragStart.Y);
            if (_draft is not null)
            { _draft.End = _mouse; _draft.Points.Add(_mouse); }
            else if (_selected)
            {
                Rectangle next = _handle >= 0 ? CpuRegionSelection.Resize(_dragRect, _handle, delta, _desktop)
                    : new Rectangle(Math.Clamp(_dragRect.X + delta.X, 0, _desktop.Width - _dragRect.Width), Math.Clamp(_dragRect.Y + delta.Y, 0, _desktop.Height - _dragRect.Height), _dragRect.Width, _dragRect.Height);
                TranslateSelection(next);
            }
            else _selection = CpuRegionSelection.Drag(_dragStart, _mouse, _desktop);
            _previewDirty = true;
        }
        Redraw();
    }
    private int HitHandle() => Array.FindIndex(CpuRegionSelection.Handles(_selection), p => Math.Abs(p.X - _mouse.X) <= Dip(6) && Math.Abs(p.Y - _mouse.Y) <= Dip(6));
    private void PointerDown()
    {
        if (_textEdit != 0) CommitText();
        if (_selected && _toolbar.Contains(_mouse))
        {
            int index = Enumerable.Range(0, Buttons.Length).FirstOrDefault(i => ButtonRect(i).Contains(_mouse), -1);
            if (index >= 0) ClickButton(index);
            return;
        }
        if (_selected && _tool != CpuAnnotationTool.Select && _selection.Contains(_mouse))
        {
            if (_tool == CpuAnnotationTool.Text) { StartText(); return; }
            PushUndo();
            if (_tool == CpuAnnotationTool.Eraser)
            { int i = _annotations.FindLastIndex(a => a.Bounds(Dip(8)).Contains(_mouse)); if (i >= 0) _annotations.RemoveAt(i); _previewDirty = true; Redraw(); return; }
            _draft = new CpuAnnotation { Tool = _tool, Start = _mouse, End = _mouse, Color = Colors[_colorIndex], Width = Dip(3) };
            _draft.Points.Add(_mouse);
            if (_tool == CpuAnnotationTool.Number) { _draft.Text = (_annotations.Count(a => a.Tool == CpuAnnotationTool.Number) + 1).ToString(); CommitDraft(); Redraw(); return; }
        }
        else if (_selected && (_handle = HitHandle()) >= 0) { }
        else if (_selected && _selection.Contains(_mouse)) { _handle = -1; }
        else { Reselect(); _selection = new Rectangle(_mouse.X, _mouse.Y, 0, 0); }
        _dragging = true; _dragStart = _mouse; _dragRect = _selection;
        CpuCaptureNative.SetCapture(Hwnd);
    }
    private void PointerUp()
    {
        if (!_dragging) return;
        _dragging = false; CpuCaptureNative.ReleaseCapture();
        if (_draft is not null) CommitDraft();
        else if (!_selected)
        {
            if (_selection.Width <= 2 && _selection.Height <= 2) _selection = HoverRectangle();
            _selected = _selection.Width >= 2 && _selection.Height >= 2;
            _tool = CpuAnnotationTool.Select;
            Serilog.Log.Information("CPU region selected: generation={Generation}, width={Width}, height={Height}", _generation, _selection.Width, _selection.Height);
        }
        _previewDirty = true; LayoutToolbar(); Redraw();
    }
    private void ClickButton(int index)
    {
        var button = Buttons[index];
        if (button.Tool is { } tool) _tool = tool;
        else if (button.Action is { } action) { Finish(action); return; }
        else if (index == 12) _colorIndex = (_colorIndex + 1) % Colors.Length;
        else if (index == 13) Undo(); else if (index == 14) Undo(redo: true); else if (index == 18) Reselect();
        Redraw();
    }
    private void CommitDraft() { if (_draft is null) return; _annotations.Add(_draft); _draft = null; _previewDirty = true; }
    private void PushUndo()
    {
        if (_undo.Count >= 100) _undo.Clear();
        _undo.Push(_annotations.Select(a => a.Clone()).ToList()); _redo.Clear();
    }
    private void Undo(bool redo = false)
    {
        var from = redo ? _redo : _undo; var to = redo ? _undo : _redo;
        if (from.Count == 0) return;
        to.Push(_annotations.Select(a => a.Clone()).ToList());
        _annotations.Clear(); _annotations.AddRange(from.Pop()); _previewDirty = true; Redraw();
    }
    private void TranslateSelection(Rectangle next)
    {
        if (_selection.Size == next.Size)
        {
            int dx = next.X - _selection.X, dy = next.Y - _selection.Y;
            foreach (var a in _annotations) a.Translate(dx, dy);
            foreach (var snapshot in _undo.Concat(_redo)) foreach (var a in snapshot) a.Translate(dx, dy);
        }
        _selection = next; _previewDirty = true;
    }
    private void Reselect()
    {
        _selected = false; _dragging = false; _selection = default; _draft = null;
        _annotations.Clear(); _undo.Clear(); _redo.Clear(); _tool = CpuAnnotationTool.Select;
        _preview?.Dispose(); _preview = null; _previewDirty = true; _hoverButton = -1; _tip = ""; Redraw();
    }

    private void StartText()
    {
        int x = Math.Clamp(_mouse.X, _selection.Left, _selection.Right - 1), y = Math.Clamp(_mouse.Y, _selection.Top, _selection.Bottom - 1);
        _draft = new CpuAnnotation { Tool = CpuAnnotationTool.Text, Start = new(x, y), End = new(x, y), Color = Colors[_colorIndex], Width = Dip(3) };
        _textEdit = CpuCaptureNative.CreateWindowExW(0x200, "EDIT", "", 0x50000080, x, y,
            Math.Min(Dip(300), _desktop.Width - x), Dip(38), Hwnd, 0, CpuCaptureNative.GetModuleHandleW(null), 0);
        if (_textEdit == 0) { _draft = null; return; }
        CpuCaptureNative.SendMessageW(_textEdit, 0x30, _font, 1);
        _editProcedure = (hwnd, message, wparam, lparam) =>
        {
            try
            {
                if (message == 0x100 && (int)wparam is 13 or 27)
                { if ((int)wparam == 27) CpuCaptureNative.SetWindowTextW(hwnd, ""); CommitText(); return 0; }
                return CpuCaptureNative.CallWindowProcW(_editOriginal, hwnd, message, wparam, lparam);
            }
            catch (Exception ex) { OnFailure(ex); return 0; }
        };
        _editOriginal = CpuCaptureNative.SetWindowLongPtrW(_textEdit, -4, System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_editProcedure));
        CpuCaptureNative.SetFocus(_textEdit);
    }
    private void CommitText()
    {
        if (_textEdit == 0) return;
        var text = new StringBuilder(4096); CpuCaptureNative.GetWindowTextW(_textEdit, text, text.Capacity);
        nint edit = _textEdit; _textEdit = 0;
        CpuCaptureNative.SetWindowLongPtrW(edit, -4, _editOriginal);
        CpuCaptureNative.DestroyWindow(edit); _editProcedure = null; _editOriginal = 0;
        if (_draft is not null && text.Length > 0) { PushUndo(); _draft.Text = text.ToString(); CommitDraft(); }
        else _draft = null;
        CpuCaptureNative.SetFocus(Hwnd); _previewDirty = true; Redraw();
    }

    private bool Key(int key)
    {
        bool control = Down(0x11), shift = Down(0x10);
        if (key == 27) { Finish(RegionCaptureAction.Cancel); return true; }
        if (key == 0x10) { Redraw(); return false; }
        if (control && key == 'Z') { Undo(shift); return true; }
        if (control && key == 'Y') { Undo(true); return true; }
        if (key == 'C')
        {
            if (control && _selected) Finish(RegionCaptureAction.Copy);
            else if (shift && _selected) Finish(RegionCaptureAction.Ocr);
            else if (_source is not null)
            { Color c = CpuCaptureRaster.Sample(_source, _mouse.X, _mouse.Y); ClipboardHelper.SetText(shift ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"RGB: {c.R},{c.G},{c.B}"); }
            return true;
        }
        if (_selected)
        {
            if (key == 13) { Finish(_defaultAction); return true; }
            if (control && key == 'S') { Finish(RegionCaptureAction.Save); return true; }
            if (control && key == 'Q') { Finish(RegionCaptureAction.Translate); return true; }
            if (control && key == 'T') { Finish(RegionCaptureAction.Pin); return true; }
            if (!control && key == 'G') { Finish(RegionCaptureAction.RecordGif); return true; }
            if (!control && key == 'L') { Finish(RegionCaptureAction.LongCapture); return true; }
            int dx = key == 37 ? -1 : key == 39 ? 1 : 0, dy = key == 38 ? -1 : key == 40 ? 1 : 0;
            if (dx != 0 || dy != 0)
            {
                var r = _selection;
                if (control || shift)
                {
                    int sign = shift ? -1 : 1;
                    int l = r.Left, t = r.Top, right = r.Right, bottom = r.Bottom;
                    if (dx < 0) l = Math.Clamp(l - sign, 0, right - 2);
                    if (dx > 0) right = Math.Clamp(right + sign, l + 2, _desktop.Width);
                    if (dy < 0) t = Math.Clamp(t - sign, 0, bottom - 2);
                    if (dy > 0) bottom = Math.Clamp(bottom + sign, t + 2, _desktop.Height);
                    r = Rectangle.FromLTRB(l, t, right, bottom);
                }
                else r.Location = new Point(Math.Clamp(r.X + dx, 0, _desktop.Width - r.Width), Math.Clamp(r.Y + dy, 0, _desktop.Height - r.Height));
                TranslateSelection(r); LayoutToolbar(); Redraw(); return true;
            }
        }
        int mx = key == 'A' ? -1 : key == 'D' ? 1 : 0, my = key == 'W' ? -1 : key == 'S' ? 1 : 0;
        if (mx != 0 || my != 0)
        { CpuCaptureNative.SetCursorPos(_mouse.X + _virtualX + mx, _mouse.Y + _virtualY + my); UpdateMouse(); Redraw(); return true; }
        return false;
    }

    private void Finish(RegionCaptureAction action)
    {
        if (_closed) return;
        if (action != RegionCaptureAction.Cancel && (!_selected || _source is null)) return;
        if (_textEdit != 0) CommitText();
        CpuCaptureImage? image = null, layer = null;
        try
        {
            if (action != RegionCaptureAction.Cancel)
            {
                image = _annotations.Count == 0 ? _source!.Crop(_selection) : CpuCaptureRaster.Render(_source!, _selection, _annotations, _scale);
                if (_annotations.Count > 0 && action is RegionCaptureAction.RecordGif or RegionCaptureAction.LongCapture)
                    layer = CpuCaptureRaster.RenderLayer(_source!, _selection, _annotations, _scale);
            }
            var result = new CpuRegionCaptureResult(action, _selection, image, layer);
            if (Completion.TrySetResult(result)) { image = null; layer = null; }
            Serilog.Log.Information("CPU region completed: generation={Generation}, action={Action}; destroying native overlay", _generation, action);
        }
        catch (Exception ex) { Completion.TrySetException(ex); }
        finally { image?.Dispose(); layer?.Dispose(); Dispose(); }
    }

    protected override void OnFailure(Exception ex) { Completion.TrySetException(ex); Dispose(); }
    protected override void OnNativeClosed()
    {
        _closed = true; _pendingMoveIn = false;
        _preview?.Dispose(); _preview = null; _dark = null; _source = null;
        if (_font != 0) CpuCaptureGdi.DeleteObject(_font);
        if (_smallFont != 0) CpuCaptureGdi.DeleteObject(_smallFont);
        if (_iconFont != 0) CpuCaptureGdi.DeleteObject(_iconFont);
        _font = _smallFont = _iconFont = 0;
        _editProcedure = null; _textEdit = 0;
        _annotations.Clear(); _draft = null; _undo.Clear(); _redo.Clear();
        Completion.TrySetResult(new(RegionCaptureAction.Cancel, default));
    }
    public override void Dispose()
    {
        if (_closed) return;
        bool restoreFocus = Hwnd != 0 && CpuCaptureNative.GetForegroundWindow() == Hwnd;
        if (Hwnd != 0)
        {
            CpuCaptureNative.KillTimer(Hwnd, MoveTimerId); _pendingMoveIn = false;
            if (CpuCaptureNative.GetCapture() == Hwnd) CpuCaptureNative.ReleaseCapture();
            CpuCaptureNative.ShowWindow(Hwnd, 0);
        }
        base.Dispose();
        if (restoreFocus && _previousForeground != 0) CpuCaptureNative.SetForegroundWindow(_previousForeground);
    }
}
