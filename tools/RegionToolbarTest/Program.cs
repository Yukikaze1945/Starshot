using System.Drawing;
using Starshot.Features.Screenshot;

int checks = 0;
void Check(bool condition, string label) { checks++; if (!condition) throw new Exception(label); }
bool Inside(RectangleF outer, RectangleF inner) => inner.IsEmpty || inner.Left >= outer.Left - .02f &&
    inner.Top >= outer.Top - .02f && inner.Right <= outer.Right + .02f && inner.Bottom <= outer.Bottom + .02f;
RegionToolbarMonitor Monitor(float width, float height, float scale, float x = 0, float y = 0) =>
    new(new(x, y, width * scale, height * scale), new(x, y, width * scale, height * scale), scale);

var state = new RegionToolbarState();
Check(CaptureSavePolicy.ChooseLocation(RegionCaptureAction.Save), "Save Image requires a picker");
Check(!CaptureSavePolicy.ChooseLocation(RegionCaptureAction.QuickSave), "Quick Save skips the picker");
Check(RegionToolbarCatalog.Shortcut(0x53, true, false) == RegionToolbarCommand.Save, "Ctrl+S selects Save As");
Check(RegionToolbarCatalog.Shortcut(0x53, true, true) == RegionToolbarCommand.QuickSave, "Ctrl+Shift+S quick saves");
string imagePath = Path.GetFullPath("sample.avif");
Check(!CaptureSavePolicy.HasCompanionConflict(imagePath, false, false, _ => true), "Picker owns primary overwrite confirmation");
Check(CaptureSavePolicy.HasCompanionConflict(imagePath, true, false, p => p.EndsWith(".mp4")), "Unconfirmed video overwrite rejected");
Check(CaptureSavePolicy.HasCompanionConflict(imagePath, true, false, p => p.EndsWith(".json")), "Unconfirmed manifest overwrite rejected");
Check(CaptureSavePolicy.HasCompanionConflict(imagePath, false, true, p => p.EndsWith(".jpg")), "Unconfirmed UHDR overwrite rejected");
Check(!CaptureSavePolicy.HasCompanionConflict(imagePath, true, true, _ => false), "New family can retain chosen name");
string? persisted = null;
RegionToolbarPreferences.Configure(null, json => persisted = json);
var defaults = RegionToolbarPreferences.Current;
Check(defaults.Secondary.Contains(RegionToolbarCommand.Subtitles), "Live subtitles appear in region More menu");
Check(RegionToolbarCatalog.Get(RegionToolbarCommand.Subtitles).Action==RegionCaptureAction.Subtitles,"CPU/XAML share subtitle action dispatch");
Check(RegionToolbarCatalog.Primary(RegionCaptureAction.Subtitles)==RegionToolbarCommand.Subtitles,"Subtitle-entry Enter highlights the matching action");
Check(RegionToolbarPreferences.Parse("{\"version\":4,\"main\":[],\"secondary\":[]}").Secondary.Contains(RegionToolbarCommand.Subtitles),"Previous settings receive region subtitles once");
Check(!RegionToolbarPreferences.Parse("{\"version\":5,\"main\":[],\"secondary\":[]}").Secondary.Contains(RegionToolbarCommand.Subtitles),"Intentionally removed subtitle action stays removed");
Check(SubtitleSelectionRegion.ToDesktop(new(100,200,640,120),-2560,-300)==new Rectangle(-2460,-100,640,120),"Existing physical selection handles negative desktop origins without a second DPI scale");
Check(defaults.Secondary.Contains(RegionToolbarCommand.GamutAnalysis), "Gamut analysis is available in More by default");
Check(RegionToolbarPreferences.Parse("{\"version\":3,\"main\":[],\"secondary\":[]}").Secondary.Contains(RegionToolbarCommand.GamutAnalysis), "V3 settings receive gamut entry once");
Check(!RegionToolbarPreferences.Parse("{\"version\":4,\"main\":[],\"secondary\":[]}").Secondary.Contains(RegionToolbarCommand.GamutAnalysis), "V4 intentionally removed gamut remains removed");
foreach(var monitor in new[]{Monitor(1920,1080,1,-1920,-120),Monitor(800,600,2,0,0)})
foreach(var proposed in new[]{new RectangleF(-10000,-5000,360,480),new RectangleF(30000,20000,360,690),new RectangleF(0,0,8000,8000)})
    Check(Inside(monitor.WorkArea,RegionToolbarGeometry.ClampPanel(proposed,[monitor])), "Dragged analysis panel clamps to negative/DPI work area");
var edits = new RegionToolbarEditModel(defaults);
Check(edits.Move(0, 0, 1, 0) && edits.Secondary[0] == defaults.Main[0], "Move across columns");
Check(RegionToolbarPreferences.Current.Main.SequenceEqual(defaults.Main), "Draft does not change active layout");
Check(edits.Move(1, 0, 1, 2) && edits.Secondary[1] == defaults.Main[0], "Downward drag insertion correct");
edits.Secondary.RemoveAt(1);
Check(edits.Unused().Contains(defaults.Main[0]), "Removed button returns to library");
RegionToolbarPreferences.Save(edits.Snapshot());
Check(persisted is not null && RegionToolbarPreferences.Parse(persisted).Main.SequenceEqual(edits.Main), "Versioned settings round trip");
var cleaned = RegionToolbarPreferences.Normalize(new([RegionToolbarCommand.Save, RegionToolbarCommand.Save, RegionToolbarCommand.More],
    [RegionToolbarCommand.Save, RegionToolbarCommand.QuickSave, RegionToolbarCommand.Help, RegionToolbarCommand.EditToolbar, (RegionToolbarCommand)987]));
Check(cleaned.Main.SequenceEqual(new[] { RegionToolbarCommand.Save }) && cleaned.Secondary.SequenceEqual(new[] { RegionToolbarCommand.QuickSave }), "Invalid/duplicate/protected buttons removed");
foreach (string bad in new[] { "", "{}", "[]", "not json", "{\"version\":2}" })
    Check(RegionToolbarPreferences.Parse(bad).Main.SequenceEqual(defaults.Main), "Invalid/old settings restore default");
RegionToolbarPreferences.Configure(persisted, _ => throw new IOException("test save failure"));
var prior = RegionToolbarPreferences.Current;
try { RegionToolbarPreferences.Save(defaults); throw new Exception("Expected write failure"); }
catch (IOException) { Check(RegionToolbarPreferences.Current.Main.SequenceEqual(prior.Main), "Failed persistence preserves active layout"); }
// Empty/custom layouts must still expose fixed footer recovery on small displays.
foreach (var custom in new[] { new RegionToolbarCustomization([], []), new(RegionToolbarPreferences.Available, []), cleaned })
{
    RegionToolbarPreferences.Configure(RegionToolbarPreferences.Serialize(custom), _ => { });
    foreach (float scale in new[] { 1f, 2f, 3f })
    {
        var m = Monitor(320, 720, scale, -1000, -80);
        var layout = RegionToolbarGeometry.Arrange(new(-900, 0, 90, 140), [m], state, RegionCaptureAction.Save);
        state.Open(RegionToolbarCommand.More);
        var commands = state.MenuItems(layout);
        for (int focus = 0; focus < commands.Length; focus++)
        {
            state.MenuFocus = focus; var menu = RegionToolbarGeometry.Menu(layout, state);
            Check(menu.Slots.Any(s => s.ItemIndex == focus && s.Command == commands[focus]), "Custom paged focused item reachable");
            Check(menu.Slots[^2].Command == RegionToolbarCommand.Help && menu.Slots[^1].Command == RegionToolbarCommand.EditToolbar,
                "Help/edit always anchored at bottom");
            Check(menu.Slots[^2].Bounds.Bottom <= menu.Slots[^1].Bounds.Top, "Edit remains below help");
            foreach (var slot in menu.Slots) Check(Inside(menu.Bounds, slot.Bounds), "Custom footer/body stays in popup");
        }
    }
}
RegionToolbarPreferences.Configure(null, _ => { });
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
