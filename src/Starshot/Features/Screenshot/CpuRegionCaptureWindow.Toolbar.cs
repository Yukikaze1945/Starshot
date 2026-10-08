using System;
using System.Drawing;
using System.Linq;

namespace Starshot.Features.Screenshot;

internal sealed partial class CpuRegionCaptureWindow
{
    private readonly RegionToolbarState _toolbarState = new();
    private RegionToolbarLayout? _toolbarLayout;
    private RegionToolbarMenu? _toolbarMenu;
    private bool _toolbarHelp, _consumeToolbarGesture, _suppressToolbarDoubleClick, _consumeRightPointer, _toolbarHover;
    private float _toolbarFontScale;
    private nint _toolbarGlyphFont, _toolbarLabelFont, _toolbarTextFont;
    private static readonly Color ToolbarBorder = Color.FromArgb(221, 243, 105);
    private static readonly Color ToolbarHover = Color.FromArgb(238, 242, 226);

    private static Rectangle ScreenRect(Rectangle local, int x, int y) => new(local.X + x, local.Y + y, local.Width, local.Height);
    private static Rectangle LocalRect(RectangleF screen, int x, int y) => Rectangle.Round(new RectangleF(screen.X - x, screen.Y - y, screen.Width, screen.Height));
    private static Rectangle LocalRect(RectangleF screen, float x, float y) => Rectangle.Round(new RectangleF(screen.X - x, screen.Y - y, screen.Width, screen.Height));
    private static Color StateColor(int argb) => Color.FromArgb(argb);
    private static int DipAt(float value, float scale) => Math.Max(1, (int)Math.Round(value * scale));

    private RegionToolbarCommand CurrentCommand => _tool switch
    {
        CpuAnnotationTool.Select => RegionToolbarCommand.Select,
        CpuAnnotationTool.Rectangle => RegionToolbarCommand.Rectangle,
        CpuAnnotationTool.Ellipse => RegionToolbarCommand.Ellipse,
        CpuAnnotationTool.Line => RegionToolbarCommand.Line,
        CpuAnnotationTool.Arrow => RegionToolbarCommand.Arrow,
        CpuAnnotationTool.Number => RegionToolbarCommand.Number,
        CpuAnnotationTool.Pen => RegionToolbarCommand.Pen,
        CpuAnnotationTool.Highlighter => RegionToolbarCommand.Highlighter,
        CpuAnnotationTool.Mosaic => RegionToolbarCommand.Mosaic,
        CpuAnnotationTool.Blur => RegionToolbarCommand.Blur,
        CpuAnnotationTool.Text => RegionToolbarCommand.Text,
        CpuAnnotationTool.Eraser => RegionToolbarCommand.Eraser,
        _ => RegionToolbarCommand.Select
    };
    private Color CurrentAnnotationColor => StateColor(_toolbarState.Color);
    private int CurrentAnnotationWidth => _toolbarState.Width;

    private RegionToolbarLayout ToolbarLayout()
    {
        if (_toolbarState.Tool != CurrentCommand) _toolbarState.Select(CurrentCommand);
        _toolbarState.Color = Colors[Math.Clamp(_colorIndex, 0, Colors.Length - 1)].ToArgb();
        var selected = ScreenRect(_selection, _virtualX, _virtualY);
        var monitors = RegionToolbarMonitors.Read(new RectangleF(_virtualX, _virtualY, _desktop.Width, _desktop.Height), _scale);
        _toolbarLayout = RegionToolbarGeometry.Arrange(selected, monitors, _toolbarState, _defaultAction);
        EnsureToolbarFonts(_toolbarLayout.Monitor.Scale);
        return _toolbarLayout;
    }

    private void EnsureToolbarFonts(float scale)
    {
        if (Math.Abs(scale - _toolbarFontScale) < .01f && _toolbarGlyphFont != 0 && _toolbarLabelFont != 0) return;
        DisposeToolbarFonts();
        _toolbarFontScale = scale;
        _toolbarGlyphFont = CpuCaptureGdi.Font(DipAt(18, scale), "Segoe MDL2 Assets");
        _toolbarLabelFont = CpuCaptureGdi.Font(DipAt(12, scale), "Segoe UI");
        _toolbarTextFont = CpuCaptureGdi.Font(DipAt(18, scale), "Segoe UI");
    }

    private void PaintToolbar(nint dc)
    {
        var layout = ToolbarLayout();
        _toolbarMenu = _toolbarState.Popup.HasValue ? RegionToolbarGeometry.Menu(layout, _toolbarState) : null;
        var main = LocalRect(layout.Main, _virtualX, _virtualY);
        PaintPanel(dc, main, layout.Monitor.Scale);
        foreach (var slot in layout.Slots)
        {
            var bounds = LocalRect(slot.Bounds, _virtualX, _virtualY);
            var chevron = LocalRect(slot.Chevron, _virtualX, _virtualY);
            var desc = RegionToolbarCatalog.Get(RegionToolbarCatalog.IsGroup(slot.Command) ? _toolbarState.Resolve(slot.Command) : slot.Command);
            var selected = RegionToolbarCatalog.IsGroup(slot.Command)
                ? RegionToolbarCatalog.Members(slot.Command).Contains(CurrentCommand)
                : CurrentCommand == slot.Command;
            bool primary = slot.Command == RegionToolbarCatalog.Primary(_defaultAction);
            bool enabled = ToolbarCommandEnabled(slot.Command);
            if (selected || primary || slot.Bounds.Contains(_mouse.X + _virtualX, _mouse.Y + _virtualY))
                CpuCaptureGdi.Fill(dc, bounds, primary ? Accent : ToolbarHover, DipAt(6, layout.Monitor.Scale));
            if (primary) CpuCaptureGdi.Shape(dc, bounds, ToolbarBorder, DipAt(2, layout.Monitor.Scale));
            var glyphRect = bounds;
            if (!chevron.IsEmpty) glyphRect.Width -= chevron.Width;
            CpuCaptureGdi.Text(dc, desc.Glyph, glyphRect,
                desc.Symbol ? _toolbarGlyphFont : desc.Glyph.Length > 1 ? _toolbarLabelFont : _toolbarTextFont,
                !enabled ? Color.FromArgb(160, 168, 150) : primary ? Ink : (selected ? Color.FromArgb(63, 87, 37) : Ink), center: true);
            if (!chevron.IsEmpty)
                CpuCaptureGdi.Text(dc, "⌄", chevron, _toolbarLabelFont, Ink, center: true);
        }
        if (!layout.Parameters.IsEmpty) PaintParameters(dc, layout);
        if (_toolbarState.Popup.HasValue && _toolbarMenu is { } menu) PaintMenu(dc, menu, layout.Monitor.Scale);
        if (_toolbarHelp) PaintHelp(dc, layout);
        if (_tip.Length > 0 && !_toolbarHelp && !_toolbarState.Popup.HasValue)
        {
            var area = LocalRect(layout.Monitor.WorkArea, _virtualX, _virtualY);
            int w = Math.Min(DipAt(300, layout.Monitor.Scale), area.Width), h = DipAt(26, layout.Monitor.Scale);
            int bottom = layout.Parameters.IsEmpty ? main.Bottom : LocalRect(layout.Parameters, _virtualX, _virtualY).Bottom;
            var notice = new Rectangle(Math.Clamp(_mouse.X - w / 2, area.Left, Math.Max(area.Left, area.Right - w)),
                bottom + h + DipAt(8, layout.Monitor.Scale) < area.Bottom ? bottom + DipAt(6, layout.Monitor.Scale) : Math.Max(area.Top, main.Top - h - DipAt(6, layout.Monitor.Scale)), w, h);
            CpuCaptureGdi.Fill(dc, notice, Paper, DipAt(7, layout.Monitor.Scale));
            CpuCaptureGdi.Text(dc, _tip, notice, _toolbarLabelFont, Ink, center: true);
        }
    }

    private void PaintPanel(nint dc, Rectangle rect, float scale)
    {
        CpuCaptureGdi.Fill(dc, new Rectangle(rect.X - 1, rect.Y - 1, rect.Width + 2, rect.Height + 2), Border, DipAt(10, scale));
        CpuCaptureGdi.Fill(dc, rect, Paper, DipAt(10, scale));
    }

    private void PaintParameters(nint dc, RegionToolbarLayout layout)
    {
        var bounds = LocalRect(layout.Parameters, _virtualX, _virtualY);
        PaintPanel(dc, bounds, layout.Monitor.Scale);
        foreach (var choice in layout.Choices)
        {
            var r = LocalRect(choice.Bounds, _virtualX, _virtualY);
            if (choice.Color)
            {
                var color = StateColor(choice.Value);
                CpuCaptureGdi.Fill(dc, r, color, r.Width / 2);
                CpuCaptureGdi.Shape(dc, r, _toolbarState.Color == choice.Value ? Ink : Border, _toolbarState.Color == choice.Value ? DipAt(2, layout.Monitor.Scale) : 1, ellipse: true);
            }
            else
            {
                CpuCaptureGdi.Fill(dc, r, _toolbarState.Width == choice.Value ? Color.FromArgb(240, 246, 218) : Paper, DipAt(5, layout.Monitor.Scale));
                int thickness = DipAt(choice.Value, layout.Monitor.Scale);
                CpuCaptureGdi.Line(dc, new(r.Left + DipAt(5, layout.Monitor.Scale), r.Top + r.Height / 2),
                    new(r.Right - DipAt(5, layout.Monitor.Scale), r.Top + r.Height / 2), Ink, thickness);
                if (_toolbarState.Width == choice.Value) CpuCaptureGdi.Shape(dc, r, Border, 1);
            }
        }
    }

    private void PaintMenu(nint dc, RegionToolbarMenu menu, float scale)
    {
        var bounds = LocalRect(menu.Bounds, _virtualX, _virtualY);
        PaintPanel(dc, bounds, scale);
        for (int i = 0; i < menu.Slots.Count; i++)
        {
            var slot = menu.Slots[i];
            var r = LocalRect(slot.Bounds, _virtualX, _virtualY);
            if (menu.FirstIndex + i == _toolbarState.MenuFocus || slot.Bounds.Contains(_mouse.X + _virtualX, _mouse.Y + _virtualY))
                CpuCaptureGdi.Fill(dc, r, ToolbarHover, DipAt(5, scale));
            var desc = RegionToolbarCatalog.Get(slot.Command);
            string label = desc.Label + (desc.Shortcut.Length == 0 ? "" : "   " + desc.Shortcut);
            if (slot.Command == RegionToolbarCommand.HdrAnalysis) label = "HDR 亮度分析（需 HDR 捕获）";
            CpuCaptureGdi.Text(dc, label, new(r.X + DipAt(8, scale), r.Y, r.Width - DipAt(16, scale), r.Height), _toolbarLabelFont,
                ToolbarCommandEnabled(slot.Command) ? Ink : Color.FromArgb(160, 168, 150));
        }
    }

    private void PaintHelp(nint dc, RegionToolbarLayout layout)
    {
        float s = layout.Monitor.Scale;
        var screen = HelpBounds(layout);
        if (screen.Bottom > layout.Monitor.WorkArea.Bottom) screen.Y = layout.Main.Top - screen.Height - DipAt(6, s);
        screen.X = Math.Clamp(screen.X, layout.Monitor.WorkArea.Left, Math.Max(layout.Monitor.WorkArea.Left, layout.Monitor.WorkArea.Right - screen.Width));
        var r = LocalRect(screen, _virtualX, _virtualY);
        PaintPanel(dc, r, s);
        int y = r.Y + DipAt(8, s), line = DipAt(25, s);
        foreach (string text in RegionToolbarCatalog.Help.Split('\n'))
        {
            CpuCaptureGdi.Text(dc, text, new(r.X + DipAt(12, s), y, r.Width - DipAt(24, s), line), _toolbarLabelFont, Ink);
            y += line;
        }
    }

    private void UpdateToolbarHover()
    {
        if (!_selected) { _tip = ""; _toolbarHover = false; return; }
        var layout = ToolbarLayout();
        PointF screen = new(_mouse.X + _virtualX, _mouse.Y + _virtualY);
        var slot = layout.Slots.FirstOrDefault(s => s.Bounds.Contains(screen));
        if (slot is not null)
        {
            _toolbarHover = true;
            var descriptor = RegionToolbarCatalog.Get(RegionToolbarCatalog.IsGroup(slot.Command) ? _toolbarState.Resolve(slot.Command) : slot.Command);
            _tip = descriptor.Label + (descriptor.Shortcut.Length == 0 ? "" : "  " + descriptor.Shortcut);
            return;
        }
        _toolbarMenu = _toolbarState.Popup.HasValue ? RegionToolbarGeometry.Menu(layout, _toolbarState) : null;
        var menuSlot = _toolbarMenu?.Slots.FirstOrDefault(s => s.Bounds.Contains(screen));
        if (menuSlot is not null) { _toolbarState.MenuFocus = _toolbarMenu!.FirstIndex + _toolbarMenu.Slots.ToList().IndexOf(menuSlot); _toolbarHover = true; _tip = RegionToolbarCatalog.Get(menuSlot.Command).Label; return; }
        var choice = layout.Choices.FirstOrDefault(c => c.Bounds.Contains(screen));
        if (choice is not null) { _toolbarHover = true; _tip = choice.Color ? "标注颜色" : $"线宽 {choice.Value}"; return; }
        if (_toolbarHelp && HelpBounds(layout).Contains(screen)) { _toolbarHover = true; _tip = "快捷键说明"; return; }
        _toolbarHover = false;
        _tip = "";
    }

    private bool ToolbarPointerDown()
    {
        _suppressToolbarDoubleClick = false;
        if (!_selected && !_toolbarHelp) return false;
        var layout = ToolbarLayout();
        var screen = new PointF(_mouse.X + _virtualX, _mouse.Y + _virtualY);
        _toolbarMenu = _toolbarState.Popup.HasValue ? RegionToolbarGeometry.Menu(layout, _toolbarState) : null;
        if (_toolbarHelp)
        {
            _consumeToolbarGesture = _suppressToolbarDoubleClick = true;
            if (!HelpBounds(layout).Contains(screen)) { _toolbarHelp = false; Redraw(); }
            return true;
        }
        if (_toolbarMenu is { } menu && menu.Bounds.Contains(screen))
        {
            var item = menu.Slots.FirstOrDefault(s => s.Bounds.Contains(screen));
            _consumeToolbarGesture = _suppressToolbarDoubleClick = true;
            if (item is not null) { _toolbarState.MenuFocus = menu.FirstIndex + menu.Slots.ToList().IndexOf(item); InvokeToolbar(item.Command, fromPopup: true); }
            Redraw(); return true;
        }
        if (_toolbarState.Popup.HasValue)
        {
            _toolbarState.ClosePopup(); _toolbarMenu = null; _toolbarHelp = false;
            _consumeToolbarGesture = _suppressToolbarDoubleClick = true; Redraw(); return true;
        }
        var parameter = layout.Choices.FirstOrDefault(c => c.Bounds.Contains(screen));
        if (parameter is not null)
        {
            _consumeToolbarGesture = _suppressToolbarDoubleClick = true;
            if (parameter.Color)
            {
                _colorIndex = Array.FindIndex(Colors, c => c.ToArgb() == parameter.Value);
                if (_colorIndex < 0) _colorIndex = 0;
                _toolbarState.Color = Colors[_colorIndex].ToArgb();
            }
            else _toolbarState.Width = parameter.Value;
            Redraw(); return true;
        }
        var slot = layout.Slots.FirstOrDefault(s => s.Bounds.Contains(screen));
        if (slot is null) return false;
        _consumeToolbarGesture = _suppressToolbarDoubleClick = true;
        var command = slot.Command;
        if (RegionToolbarCatalog.IsGroup(command))
        {
            if (!slot.Chevron.IsEmpty && slot.Chevron.Contains(screen)) _toolbarState.Open(command);
            else InvokeToolbar(_toolbarState.Resolve(command));
        }
        else InvokeToolbar(command);
        Redraw(); return true;
    }

    private RectangleF HelpBounds(RegionToolbarLayout layout)
        => RegionToolbarGeometry.HelpBounds(layout);

    private bool PopupConsumesPointer()
    {
        if (_suppressToolbarDoubleClick) { _suppressToolbarDoubleClick = false; _consumeToolbarGesture = true; return true; }
        if (_toolbarState.Popup.HasValue || _toolbarHelp) { _consumeToolbarGesture = true; return true; }
        return false;
    }
    private bool RightPointerDown()
    {
        if (!_toolbarState.Popup.HasValue && !_toolbarHelp) return false;
        _toolbarState.ClosePopup(); _toolbarHelp = false; _toolbarMenu = null;
        _consumeRightPointer = true; _consumeToolbarGesture = _suppressToolbarDoubleClick = true; Redraw(); return true;
    }
    private bool ToolbarPointerUp()
    {
        if (!_consumeToolbarGesture) return false;
        _consumeToolbarGesture = false; return true;
    }
    private bool ToolbarEscape()
    {
        if (!_toolbarState.Popup.HasValue && !_toolbarHelp) return false;
        CloseToolbarPopup(); Redraw(); return true;
    }
    private bool ToolbarKey(int key)
    {
        if (key == 0x70 && (_toolbarHelp || _toolbarState.Popup.HasValue)) { CloseToolbarPopup(); Redraw(); return true; }
        if (_toolbarHelp) return true;
        if (!_toolbarState.Popup.HasValue) return false;
        var items = _toolbarState.MenuItems(ToolbarLayout());
        if (items.Length == 0) return true;
        if (key is 37 or 38) _toolbarState.MenuFocus = (_toolbarState.MenuFocus + items.Length - 1) % items.Length;
        else if (key is 39 or 40 or 9) _toolbarState.MenuFocus = (_toolbarState.MenuFocus + 1) % items.Length;
        else if (key is 13 or 32) { InvokeToolbar(items[Math.Clamp(_toolbarState.MenuFocus, 0, items.Length - 1)], fromPopup: true); return true; }
        else return true;
        _toolbarMenu = RegionToolbarGeometry.Menu(ToolbarLayout(), _toolbarState); Redraw(); return true;
    }
    private bool ToolbarWheel(int delta)
    {
        if (!_toolbarState.Popup.HasValue || delta == 0) return false;
        var items = _toolbarState.MenuItems(ToolbarLayout());
        if (items.Length == 0) return false;
        _toolbarState.MenuFocus = (_toolbarState.MenuFocus + (delta > 0 ? items.Length - 1 : 1)) % items.Length;
        _toolbarMenu = RegionToolbarGeometry.Menu(ToolbarLayout(), _toolbarState); Redraw(); return true;
    }
    private void CloseToolbarPopup() { _toolbarState.ClosePopup(); _toolbarHelp = false; _toolbarMenu = null; _consumeToolbarGesture = false; }
    private void DisposeToolbarFonts()
    {
        if (_toolbarGlyphFont != 0) CpuCaptureGdi.DeleteObject(_toolbarGlyphFont);
        if (_toolbarLabelFont != 0) CpuCaptureGdi.DeleteObject(_toolbarLabelFont);
        if (_toolbarTextFont != 0) CpuCaptureGdi.DeleteObject(_toolbarTextFont);
        _toolbarGlyphFont = _toolbarLabelFont = _toolbarTextFont = 0; _toolbarFontScale = 0;
    }

    private bool InvokeToolbar(RegionToolbarCommand command, bool fromPopup = false)
    {
        if (!ToolbarCommandEnabled(command)) return true;
        if (RegionToolbarCatalog.IsTool(command))
        {
            _toolbarState.Select(command);
            _tool = command switch
            {
                RegionToolbarCommand.Select => CpuAnnotationTool.Select,
                RegionToolbarCommand.Rectangle => CpuAnnotationTool.Rectangle,
                RegionToolbarCommand.Ellipse => CpuAnnotationTool.Ellipse,
                RegionToolbarCommand.Line => CpuAnnotationTool.Line,
                RegionToolbarCommand.Arrow => CpuAnnotationTool.Arrow,
                RegionToolbarCommand.Number => CpuAnnotationTool.Number,
                RegionToolbarCommand.Pen => CpuAnnotationTool.Pen,
                RegionToolbarCommand.Highlighter => CpuAnnotationTool.Highlighter,
                RegionToolbarCommand.Mosaic => CpuAnnotationTool.Mosaic,
                RegionToolbarCommand.Blur => CpuAnnotationTool.Blur,
                RegionToolbarCommand.Text => CpuAnnotationTool.Text,
                RegionToolbarCommand.Eraser => CpuAnnotationTool.Eraser,
                _ => CpuAnnotationTool.Select
            };
        }
        else if (RegionToolbarCatalog.Get(command).Action is { } action) Finish(action);
        else if (RegionToolbarCatalog.IsGroup(command))
        { if (fromPopup) _toolbarState.Open(command); else _toolbarState.Select(_toolbarState.Resolve(command)); }
        else switch (command)
        {
            case RegionToolbarCommand.Undo: Undo(); break;
            case RegionToolbarCommand.Redo: Undo(redo: true); break;
            case RegionToolbarCommand.More: _toolbarState.Open(command); break;
            case RegionToolbarCommand.Reselect: CloseToolbarPopup(); Reselect(); break;
            case RegionToolbarCommand.Help: _toolbarState.ClosePopup(); _toolbarHelp = true; break;

        }
        Redraw(); return true;
    }
    private bool ToolbarCommandEnabled(RegionToolbarCommand command) => command switch
    { RegionToolbarCommand.Undo => _undo.Count > 0, RegionToolbarCommand.Redo => _redo.Count > 0,
      RegionToolbarCommand.HdrAnalysis => false, _ => true };
}
