using System.Drawing;
using Starshot.Features.Screenshot;

int checks = 0;
void Check(bool condition, string label) { checks++; if (!condition) throw new Exception(label); }
bool Inside(RectangleF outer, RectangleF inner) => inner.IsEmpty || inner.Left >= outer.Left - .02f &&
    inner.Top >= outer.Top - .02f && inner.Right <= outer.Right + .02f && inner.Bottom <= outer.Bottom + .02f;
RegionToolbarMonitor Monitor(float width, float height, float scale, float x = 0, float y = 0) =>
    new(new(x, y, width * scale, height * scale), new(x, y, width * scale, height * scale), scale);

var state = new RegionToolbarState();
foreach (var group in new[] { RegionToolbarCommand.Shapes, RegionToolbarCommand.Brushes, RegionToolbarCommand.Texts })
{
    foreach (var tool in RegionToolbarCatalog.Members(group))
    { state.Select(tool); Check(state.Resolve(group) == tool, "Group remembers its latest tool"); }
}
state.Select(RegionToolbarCommand.Select);
state.Open(RegionToolbarCommand.Shapes);
Check(state.ClosePopup() && !state.ClosePopup(), "Closing popup is idempotent");
Check(RegionToolbarCatalog.Shortcut(0x54, true, false) == RegionToolbarCommand.Pin, "Ctrl+T parity");
Check(RegionToolbarCatalog.Shortcut(0x5a, true, true) == RegionToolbarCommand.Redo, "Ctrl+Shift+Z parity");
Check(RegionToolbarCatalog.Shortcut(0x43, false, true) == RegionToolbarCommand.Ocr, "Shift+C OCR");
Check(RegionToolbarCatalog.Shortcut(0x43, false, false) == null, "C still samples color");
Check(RegionToolbarCatalog.Shortcut(0x47, false, false) == RegionToolbarCommand.RecordGif, "G recording parity");
Check(RegionToolbarCatalog.CursorDelta(0x57) == new Point(0, -1), "W cursor movement shared");
Check(RegionToolbarCatalog.CursorDelta(0x41) == new Point(-1, 0), "A cursor movement shared");

foreach (float dpi in new[] { 1f, 1.25f, 1.5f, 2f, 3f, 4f })
foreach (float width in new[] { 160f, 240f, 320f, 480f, 640f, 960f, 1920f })
foreach (float height in new[] { 360f, 720f, 1080f })
foreach (var action in new[] { RegionCaptureAction.Save, RegionCaptureAction.Copy, RegionCaptureAction.Ocr, RegionCaptureAction.Translate })
foreach (var tool in new[] { RegionToolbarCommand.Select, RegionToolbarCommand.Rectangle, RegionToolbarCommand.Text })
foreach (var location in new[] { 0f, .45f, .9f })
{
    var monitor = Monitor(width, height, dpi, -width * dpi, -80 * dpi);
    var selection = new RectangleF(monitor.Bounds.Left + width * dpi * location, monitor.Bounds.Top + height * dpi * location,
        Math.Min(220 * dpi, width * dpi), Math.Min(160 * dpi, height * dpi));
    state.Select(tool);
    var layout = RegionToolbarGeometry.Arrange(selection, [monitor], state, action);
    Check(Inside(monitor.WorkArea, layout.Main), "Main toolbar remains on monitor");
    Check(Inside(monitor.WorkArea, layout.Parameters), "Parameters remain on monitor");
    Check(layout.Slots.Any(slot => slot.Command == RegionToolbarCatalog.Primary(action)), "Entry primary remains directly accessible");
    foreach (var slot in layout.Slots)
    {
        Check(slot.Bounds.Width >= 36 * dpi - .02f && slot.Bounds.Height >= 36 * dpi - .02f, "Never shrink click targets");
        Check(Inside(layout.Main, slot.Bounds), "Main button fits panel");
    }
    Check(layout.Parameters.IsEmpty == (tool == RegionToolbarCommand.Select), "Only annotation tools show parameters");
    foreach (var choice in layout.Choices) Check(Inside(layout.Parameters, choice.Bounds), "Parameter fits panel");
    var reachable = new HashSet<RegionToolbarCommand>();
    void Expand(RegionToolbarCommand command)
    {
        reachable.Add(command);
        foreach (var member in RegionToolbarCatalog.Members(command)) reachable.Add(member);
    }
    foreach (var slot in layout.Slots) Expand(slot.Command);
    state.Open(RegionToolbarCommand.More);
    foreach (var command in state.MenuItems(layout)) Expand(command);
    foreach (var command in Enum.GetValues<RegionToolbarCommand>())
        Check(reachable.Contains(command), "Every action/tool stays reachable: " + command);
    var menuItems = state.MenuItems(layout);
    for (int focus = 0; focus < menuItems.Length; focus++)
    {
        state.MenuFocus = focus;
        var menu = RegionToolbarGeometry.Menu(layout, state);
        Check(Inside(monitor.WorkArea, menu.Bounds), "Popup remains on monitor");
        Check(!menu.Bounds.IntersectsWith(layout.Main) && (layout.Parameters.IsEmpty || !menu.Bounds.IntersectsWith(layout.Parameters)), "Popup does not cover toolbar/parameters");
        Check(menu.Slots.Any(slot => slot.Command == menuItems[focus]), "Paged menu exposes focused action");
        foreach (var slot in menu.Slots)
        { Check(Inside(menu.Bounds, slot.Bounds), "Menu entry fits panel"); Check(slot.Bounds.Width >= 36 * dpi, "Menu click target stays usable"); }
    }
    state.ClosePopup();
}
foreach (float dpi in new[] { 1f, 1.25f, 1.5f, 2f, 3f })
foreach (float fx in new[] { .08f, .5f, .92f })
foreach (bool expanded in new[] { false, true })
{
    var monitor = Monitor(1920, 1080, dpi, -1920 * dpi, -120 * dpi);
    var selection = new RectangleF(monitor.Bounds.Left + monitor.Bounds.Width * fx,
        monitor.Bounds.Top + monitor.Bounds.Height * .42f, 180 * dpi, 120 * dpi);
    state.Select(RegionToolbarCommand.Pen);
    var layout = RegionToolbarGeometry.Arrange(selection, [monitor], state, RegionCaptureAction.Save);
    var panel = RegionToolbarGeometry.AnalysisBounds(layout, selection, expanded);
    Check(Inside(monitor.WorkArea, panel), "Analysis panel remains in negative-origin work area at DPI " + dpi);
    Check(Math.Abs(panel.Width - 360 * dpi) < .02f, "Analysis panel width follows monitor DPI");
    float expectedHeight = Math.Min((expanded ? 690 : 480) * dpi, monitor.WorkArea.Height);
    Check(panel.Height > 0 && panel.Height <= expectedHeight + .02f, "Compact/expanded panel stays within its mode-specific height budget");
}
foreach (float dpi in new[] { 1f, 1.5f, 2f, 3f })
{
    var monitor = Monitor(2560, 1600, dpi, -2560 * dpi, -160 * dpi);
    var selection = new RectangleF(monitor.Bounds.Left + 1040 * dpi, monitor.Bounds.Top + 600 * dpi, 220 * dpi, 160 * dpi);
    state.Select(RegionToolbarCommand.Pen);
    var layout = RegionToolbarGeometry.Arrange(selection, [monitor], state, RegionCaptureAction.Save);
    var compact = RegionToolbarGeometry.AnalysisBounds(layout, selection, false);
    var expanded = RegionToolbarGeometry.AnalysisBounds(layout, selection, true);
    Check(Math.Abs(compact.Height - 480 * dpi) < .02f && Math.Abs(expanded.Height - 690 * dpi) < .02f,
        "Available center placement preserves compact and expanded heights at DPI " + dpi);
    Check(!compact.IntersectsWith(layout.Main) && !compact.IntersectsWith(layout.Parameters)
        && !expanded.IntersectsWith(layout.Main) && !expanded.IntersectsWith(layout.Parameters),
        "Panels avoid main toolbar and parameters when sufficient space is available");
}
var left = Monitor(1280, 720, 1.25f, -1600);
var right = Monitor(1920, 1080, 2);
var mixed = RegionToolbarGeometry.Arrange(new(-300, 40, 1200, 400), [left, right], state, RegionCaptureAction.Copy);
Check(mixed.Monitor == right, "Cross-monitor toolbar uses greatest selection overlap");
Check(mixed.Monitor.Scale == 2, "Uses destination monitor DPI");
var crossSelection = new RectangleF(-300, 40, 1200, 400);
foreach (bool expanded in new[] { false, true })
{
    var panel = RegionToolbarGeometry.AnalysisBounds(mixed, crossSelection, expanded);
    Check(Inside(right.WorkArea, panel), "Mixed-monitor analysis panel uses selected monitor work area");
    Check(!panel.IntersectsWith(mixed.Main) && !panel.IntersectsWith(mixed.Parameters),
        "Mixed-monitor panel avoids the toolbar when room is available");
}
var native = RegionToolbarMonitors.Read(new(0, 0, 640, 480), 1);
Check(native.Count > 0 && native.All(m => m.Scale > 0), "Native monitor inventory valid without graphics APIs");
Console.WriteLine($"PASS {checks} checks; shared actions, groups, parameters, analysis panels, 126 DPI/monitor combinations, 0 screenshots, 0 GPU dependencies.");
