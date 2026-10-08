using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace Starshot.Features.Screenshot;

public enum RegionCaptureAction { Cancel, Save, Copy, Ocr, Translate, Pin, RecordGif, LongCapture }

// UI-independent semantics shared by the GDI and XAML renderers. No capture/device objects.
internal enum RegionToolbarCommand
{
    Select, Rectangle, Ellipse, Line, Arrow, Number, Pen, Highlighter, Mosaic, Blur, Text, Eraser,
    Shapes, Brushes, Texts, Undo, Redo, Ocr, Pin, More, Copy, Save, Cancel, Translate, RecordGif, LongCapture, Reselect, Help, HdrAnalysis
}
internal sealed record RegionToolbarDescriptor(string Label, string Glyph, bool Symbol = true,
    RegionCaptureAction? Action = null, string Shortcut = "");
internal sealed record RegionToolbarSlot(RegionToolbarCommand Command, RectangleF Bounds, RectangleF Chevron);
internal sealed record RegionToolbarParameter(bool Color, int Value, RectangleF Bounds);
internal sealed record RegionToolbarMonitor(RectangleF Bounds, RectangleF WorkArea, float Scale);
internal sealed record RegionToolbarLayout(RectangleF Main, RectangleF Parameters,
    RegionToolbarMonitor Monitor, IReadOnlyList<RegionToolbarSlot> Slots,
    IReadOnlyList<RegionToolbarCommand> Overflow, IReadOnlyList<RegionToolbarParameter> Choices);
internal sealed record RegionToolbarMenu(RectangleF Bounds, IReadOnlyList<RegionToolbarSlot> Slots,
    int FirstIndex = 0, int TotalCount = 0);

internal static class RegionToolbarCatalog
{
    public static readonly int[] Colors = [unchecked((int)0xffff404d), unchecked((int)0xff3189ff),
        unchecked((int)0xffffe119), unchecked((int)0xffffffff), unchecked((int)0xff000000)];
    public static readonly int[] Widths = [2, 3, 5, 8, 12];
    public static readonly RegionToolbarCommand[] Main = [RegionToolbarCommand.Select, RegionToolbarCommand.Shapes,
        RegionToolbarCommand.Arrow, RegionToolbarCommand.Brushes, RegionToolbarCommand.Texts,
        RegionToolbarCommand.Undo, RegionToolbarCommand.Redo, RegionToolbarCommand.Ocr, RegionToolbarCommand.Pin,
        RegionToolbarCommand.More, RegionToolbarCommand.Copy, RegionToolbarCommand.Save, RegionToolbarCommand.Cancel];
    private static readonly Dictionary<RegionToolbarCommand, RegionToolbarDescriptor> Items = new()
    {
        [RegionToolbarCommand.Select] = new("选择 / 调整选区", "\uE762"),
        [RegionToolbarCommand.Rectangle] = new("矩形", "\uE7F8"),
        [RegionToolbarCommand.Ellipse] = new("椭圆", "\uEA3A"),
        [RegionToolbarCommand.Line] = new("直线", "╱", false),
        [RegionToolbarCommand.Arrow] = new("箭头", "↗", false),
        [RegionToolbarCommand.Number] = new("序号", "①", false),
        [RegionToolbarCommand.Pen] = new("画笔", "\uED63"),
        [RegionToolbarCommand.Highlighter] = new("荧光笔", "\uE7E6"),
        [RegionToolbarCommand.Mosaic] = new("马赛克", "\uE80A"),
        [RegionToolbarCommand.Blur] = new("模糊", "\uE7A8"),
        [RegionToolbarCommand.Text] = new("文字", "T", false),
        [RegionToolbarCommand.Eraser] = new("擦除标注", "\uE75C"),
        [RegionToolbarCommand.Shapes] = new("形状", "\uE7F8"),
        [RegionToolbarCommand.Brushes] = new("画笔", "\uED63"),
        [RegionToolbarCommand.Texts] = new("文字", "T", false),
        [RegionToolbarCommand.Undo] = new("撤销", "\uE7A7", Shortcut: "Ctrl+Z"),
        [RegionToolbarCommand.Redo] = new("重做", "\uE7A6", Shortcut: "Ctrl+Y / Ctrl+Shift+Z"),
        [RegionToolbarCommand.Ocr] = new("文字识别", "OCR", false, RegionCaptureAction.Ocr, "Shift+C"),
        [RegionToolbarCommand.Pin] = new("贴图", "\uE718", Action: RegionCaptureAction.Pin, Shortcut: "Ctrl+T"),
        [RegionToolbarCommand.More] = new("更多工具", "\uE712"),
        [RegionToolbarCommand.Copy] = new("复制", "\uE8C8", Action: RegionCaptureAction.Copy, Shortcut: "Ctrl+C"),
        [RegionToolbarCommand.Save] = new("保存", "\uE74E", Action: RegionCaptureAction.Save, Shortcut: "Ctrl+S"),
        [RegionToolbarCommand.Cancel] = new("取消截图", "\uE8BB", Action: RegionCaptureAction.Cancel, Shortcut: "Esc"),
        [RegionToolbarCommand.Translate] = new("识别并翻译", "译", false, RegionCaptureAction.Translate, "Ctrl+Q"),
        [RegionToolbarCommand.RecordGif] = new("录制 GIF", "GIF", false, RegionCaptureAction.RecordGif, "G"),
        [RegionToolbarCommand.LongCapture] = new("长截图", "⇵", false, RegionCaptureAction.LongCapture, "L"),
        [RegionToolbarCommand.Reselect] = new("重新选区", "\uE8B7"),
        [RegionToolbarCommand.Help] = new("快捷键说明", "?", false, Shortcut: "F1"),
        [RegionToolbarCommand.HdrAnalysis] = new("HDR 亮度分析", "\uE9D9"),
    };
    public static RegionToolbarDescriptor Get(RegionToolbarCommand command) => Items[command];
    public static bool IsTool(RegionToolbarCommand command) => command <= RegionToolbarCommand.Eraser;
    public static bool IsGroup(RegionToolbarCommand command) => command is RegionToolbarCommand.Shapes or RegionToolbarCommand.Brushes or RegionToolbarCommand.Texts;
    public static bool HasWidth(RegionToolbarCommand command) => IsTool(command) && command is not
        (RegionToolbarCommand.Select or RegionToolbarCommand.Text or RegionToolbarCommand.Number or RegionToolbarCommand.Eraser);
    public static RegionToolbarCommand[] Members(RegionToolbarCommand command) => command switch
    {
        RegionToolbarCommand.Shapes => [RegionToolbarCommand.Rectangle, RegionToolbarCommand.Ellipse, RegionToolbarCommand.Line],
        RegionToolbarCommand.Brushes => [RegionToolbarCommand.Pen, RegionToolbarCommand.Highlighter, RegionToolbarCommand.Mosaic, RegionToolbarCommand.Blur, RegionToolbarCommand.Eraser],
        RegionToolbarCommand.Texts => [RegionToolbarCommand.Text, RegionToolbarCommand.Number],
        _ => []
    };
    public static RegionToolbarCommand Primary(RegionCaptureAction action) => action switch
    {
        RegionCaptureAction.Copy => RegionToolbarCommand.Copy, RegionCaptureAction.Ocr => RegionToolbarCommand.Ocr,
        RegionCaptureAction.Translate => RegionToolbarCommand.Translate, RegionCaptureAction.Pin => RegionToolbarCommand.Pin,
        _ => RegionToolbarCommand.Save
    };
    public static RegionToolbarCommand? Shortcut(int key, bool control, bool shift) => (key, control, shift) switch
    {
        (0x5a, true, true) => RegionToolbarCommand.Redo, (0x5a, true, false) => RegionToolbarCommand.Undo,
        (0x59, true, _) => RegionToolbarCommand.Redo, (0x43, true, _) => RegionToolbarCommand.Copy,
        (0x43, false, true) => RegionToolbarCommand.Ocr, (0x53, true, _) => RegionToolbarCommand.Save,
        (0x51, true, _) => RegionToolbarCommand.Translate, (0x54, true, _) => RegionToolbarCommand.Pin,
        (0x47, false, false) => RegionToolbarCommand.RecordGif, (0x4c, false, false) => RegionToolbarCommand.LongCapture,
        (0x70, _, _) => RegionToolbarCommand.Help, _ => null
    };
    public static Point CursorDelta(int key) => key switch
    { 0x41 => new(-1, 0), 0x44 => new(1, 0), 0x57 => new(0, -1), 0x53 => new(0, 1), _ => Point.Empty };
    public const string Help = "方向键  移动选区\nShift + 方向键  缩小选区\nCtrl + 方向键  扩大选区\nW / A / S / D  移动取色光标\nC  复制颜色 · Shift 切换 HEX\nCtrl+C / Ctrl+S  复制 / 保存\nShift+C / Ctrl+Q  OCR / 翻译\nCtrl+T · G · L  贴图 / GIF / 长截图\nCtrl+Z / Ctrl+Y  撤销 / 重做\nEnter  当前入口操作\nEsc  关闭菜单 → 重新选区 → 退出";
}

internal sealed class RegionToolbarState
{
    // Remember group choices across captures and across the two renderers, in this process only.
    private static readonly Dictionary<RegionToolbarCommand, RegionToolbarCommand> Remembered = new()
    { [RegionToolbarCommand.Shapes] = RegionToolbarCommand.Rectangle, [RegionToolbarCommand.Brushes] = RegionToolbarCommand.Pen, [RegionToolbarCommand.Texts] = RegionToolbarCommand.Text };
    public RegionToolbarCommand Tool { get; private set; } = RegionToolbarCommand.Select;
    public RegionToolbarCommand? Popup { get; private set; }
    public int MenuFocus { get; set; }
    public int Color { get; set; } = RegionToolbarCatalog.Colors[0];
    public int Width { get; set; } = 3;
    public RegionToolbarCommand Resolve(RegionToolbarCommand command)
    { lock (Remembered) return Remembered.TryGetValue(command, out var selected) ? selected : command; }
    public void Select(RegionToolbarCommand command)
    {
        Tool = Resolve(command);
        lock (Remembered)
            foreach (var group in Remembered.Keys.ToArray())
                if (RegionToolbarCatalog.Members(group).Contains(Tool)) Remembered[group] = Tool;
        ClosePopup();
    }
    public void Open(RegionToolbarCommand command) { Popup = command; MenuFocus = 0; }
    public bool ClosePopup() { bool open = Popup.HasValue; Popup = null; return open; }
    public RegionToolbarCommand[] MenuItems(RegionToolbarLayout layout) => Popup == RegionToolbarCommand.More
        ? layout.Overflow.Concat(new[] { RegionToolbarCommand.Translate, RegionToolbarCommand.RecordGif,
            RegionToolbarCommand.LongCapture, RegionToolbarCommand.HdrAnalysis, RegionToolbarCommand.Reselect, RegionToolbarCommand.Help }).Distinct()
            .Where(command => !layout.Slots.Any(slot => slot.Command == command)).ToArray()
        : RegionToolbarCatalog.Members(Popup ?? RegionToolbarCommand.More);
}

internal static class RegionToolbarGeometry
{
    public static RectangleF AnalysisBounds(RegionToolbarLayout layout, RectangleF selection, bool expanded)
    {
        float scale = layout.Monitor.Scale;
        var area = layout.Monitor.WorkArea;
        float width = Math.Min(360 * scale, area.Width);
        float height = Math.Min((expanded ? 690 : 480) * scale, area.Height);
        float x = selection.Right + 12 * scale;
        if (x + width > area.Right) x = selection.Left - width - 12 * scale;
        x = Clamp(x, area.Left, area.Right - width);
        float y = Clamp(selection.Top, area.Top, area.Bottom - height);
        var rect = new RectangleF(x, y, width, height);
        var toolbar = layout.Parameters.IsEmpty ? layout.Main : RectangleF.Union(layout.Main, layout.Parameters);
        if (rect.IntersectsWith(toolbar))
        {
            float above = toolbar.Top - area.Top - 8 * scale, below = area.Bottom - toolbar.Bottom - 8 * scale;
            if (Math.Max(above, below) > 100 * scale)
            {
                height = Math.Min(height, Math.Max(above, below));
                y = above >= below ? toolbar.Top - height - 8 * scale : toolbar.Bottom + 8 * scale;
                rect = new(x, y, width, height);
            }
        }
        return rect;
    }
    public static RegionToolbarLayout Arrange(RectangleF selection, IReadOnlyList<RegionToolbarMonitor> monitors,
        RegionToolbarState state, RegionCaptureAction action)
    {
        if (monitors.Count == 0) throw new ArgumentException("A monitor is required", nameof(monitors));
        var center = new PointF(selection.Left + selection.Width / 2, selection.Top + selection.Height / 2);
        var monitor = monitors.OrderByDescending(m => Area(RectangleF.Intersect(selection, m.Bounds)))
            .ThenBy(m => Distance(center, m.Bounds)).First();
        float s = Math.Clamp(monitor.Scale, .5f, 4), pad = 8 * s, gap = 4 * s, cell = 36 * s, height = 48 * s;
        var available = monitor.WorkArea;
        var items = RegionToolbarCatalog.Main.ToList();
        var primary = RegionToolbarCatalog.Primary(action);
        if (!items.Contains(primary)) items.Insert(items.IndexOf(RegionToolbarCommand.More), primary);
        var overflow = new List<RegionToolbarCommand>();
        float Width() => pad * 2 + items.Sum(c => RegionToolbarCatalog.IsGroup(c) ? cell + 16 * s : cell) + gap * (items.Count - 1);
        foreach (var command in new[] { RegionToolbarCommand.Redo, RegionToolbarCommand.Pin, RegionToolbarCommand.Undo,
            RegionToolbarCommand.Arrow, RegionToolbarCommand.Ocr, RegionToolbarCommand.Texts, RegionToolbarCommand.Brushes,
            RegionToolbarCommand.Shapes, RegionToolbarCommand.Save, RegionToolbarCommand.Copy, RegionToolbarCommand.Select })
        {
            if (Width() <= available.Width) break;
            if (command == primary || !items.Contains(command)) continue;
            items.Remove(command); overflow.Add(command);
        }
        // Only extremely narrow displays need a second row; targets remain full-sized.
        float w = Math.Min(Width(), available.Width), x = Clamp(selection.Right - w, available.Left, available.Right - w);
        var slots = new List<RegionToolbarSlot>();
        float cursor = pad, row = 0;
        foreach (var command in items)
        {
            float itemW = cell + (RegionToolbarCatalog.IsGroup(command) ? 16 * s : 0);
            if (cursor + itemW + pad > w && cursor > pad) { cursor = pad; row += height; }
            slots.Add(new(command, new(cursor, row + 6 * s, itemW, cell), RegionToolbarCatalog.IsGroup(command)
                ? new(cursor + cell, row + 6 * s, 16 * s, cell) : RectangleF.Empty));
            cursor += itemW + gap;
        }
        float h = row + height;
        bool parameters = state.Tool is not (RegionToolbarCommand.Select or RegionToolbarCommand.Eraser);
        int count = parameters ? RegionToolbarCatalog.Colors.Length + (RegionToolbarCatalog.HasWidth(state.Tool) ? RegionToolbarCatalog.Widths.Length : 0) : 0;
        float paramW = Math.Min(available.Width, count * 32 * s + pad * 2);
        int columns = Math.Max(1, (int)((paramW - 2 * pad) / (32 * s)));
        float paramH = count == 0 ? 0 : (float)Math.Ceiling(count / (double)columns) * 36 * s + pad * 2;
        float totalH = h + (parameters ? paramH + gap : 0);
        float y = selection.Bottom + 8 * s;
        if (y + totalH > available.Bottom) y = selection.Top - totalH - 8 * s;
        y = Clamp(y, available.Top, available.Bottom - totalH);
        var main = new RectangleF(x, y, w, h);
        slots = slots.Select(slot => slot with { Bounds = Offset(slot.Bounds, x, y), Chevron = slot.Chevron.IsEmpty ? RectangleF.Empty : Offset(slot.Chevron, x, y) }).ToList();
        var param = parameters ? new RectangleF(Clamp(x + w - paramW, available.Left, available.Right - paramW), y + h + gap, paramW, paramH) : RectangleF.Empty;
        var choices = new List<RegionToolbarParameter>();
        for (int i = 0; i < count; i++)
            choices.Add(new(i < RegionToolbarCatalog.Colors.Length,
                i < RegionToolbarCatalog.Colors.Length ? RegionToolbarCatalog.Colors[i] : RegionToolbarCatalog.Widths[i - RegionToolbarCatalog.Colors.Length],
                new(param.X + pad + i % columns * 32 * s, param.Y + pad + i / columns * 36 * s, 28 * s, 28 * s)));
        return new(main, param, monitor, slots, overflow, choices);
    }
    public static RegionToolbarMenu Menu(RegionToolbarLayout layout, RegionToolbarState state)
    {
        var commands = state.MenuItems(layout);
        if (commands.Length == 0) return new(RectangleF.Empty, []);
        float s = layout.Monitor.Scale, pad = 6 * s, row = 34 * s;
        var area = layout.Monitor.WorkArea;
        float above = Math.Max(0, layout.Main.Top - area.Top - 6 * s);
        float bottom = layout.Parameters.IsEmpty ? layout.Main.Bottom : layout.Parameters.Bottom;
        float below = Math.Max(0, area.Bottom - bottom - 6 * s);
        bool onTop = above >= below;
        float freeHeight = Math.Max(above, below);
        int rows = Math.Max(1, (int)((freeHeight - 2 * pad) / row));
        int columns = Math.Min(Math.Max(1, (int)(area.Width / (160 * s))), (int)Math.Ceiling(commands.Length / (double)rows));
        float columnW = Math.Min(230 * s, area.Width / columns);
        rows = Math.Min(rows, commands.Length);
        float w = columns * columnW, h = rows * row + 2 * pad;
        var anchor = layout.Slots.FirstOrDefault(slot => slot.Command == state.Popup)?.Bounds ??
            layout.Slots.First(slot => slot.Command == RegionToolbarCommand.More).Bounds;
        float x = Clamp(anchor.Left, area.Left, area.Right - w);
        float y = onTop ? layout.Main.Top - h - 6 * s : bottom + 6 * s;
        y = Clamp(y, area.Top, area.Bottom - h);
        var bounds = new RectangleF(x, y, w, h);
        int visible = rows * columns;
        state.MenuFocus = Math.Clamp(state.MenuFocus, 0, commands.Length - 1);
        int first = state.MenuFocus / visible * visible;
        var slots = commands.Skip(first).Take(visible).Select((c, i) => new RegionToolbarSlot(c,
            new(x + i / rows * columnW + pad, y + pad + i % rows * row, columnW - 2 * pad, row), RectangleF.Empty)).ToArray();
        return new(bounds, slots, first, commands.Length);
    }
    public static RectangleF Offset(RectangleF r, float x, float y) => new(r.X + x, r.Y + y, r.Width, r.Height);
    public static RectangleF HelpBounds(RegionToolbarLayout layout)
    {
        float s = layout.Monitor.Scale;
        var area = layout.Monitor.WorkArea;
        float w = Math.Min(360 * s, area.Width), h = Math.Min(300 * s, area.Height);
        float x = Clamp(layout.Main.Left, area.Left, area.Right - w);
        float y = layout.Main.Bottom + 6 * s;
        if (y + h > area.Bottom) y = layout.Main.Top - h - 6 * s;
        return new(x, Clamp(y, area.Top, area.Bottom - h), w, h);
    }
    private static float Clamp(float v, float low, float high) => Math.Clamp(v, low, Math.Max(low, high));
    private static double Area(RectangleF r) => Math.Max(0, r.Width) * (double)Math.Max(0, r.Height);
    private static double Distance(PointF p, RectangleF r) => Math.Pow(p.X - Clamp(p.X, r.Left, r.Right), 2) + Math.Pow(p.Y - Clamp(p.Y, r.Top, r.Bottom), 2);
}
