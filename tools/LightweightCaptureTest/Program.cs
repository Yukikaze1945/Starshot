using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Starshot.Features.Screenshot;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string label)
    { if (!value) throw new InvalidOperationException(label); _checks++; }
    private static FieldInfo Field(string name) => typeof(CpuRegionCaptureWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static nint Handle(CpuRegionCaptureWindow window) => (nint)typeof(CpuCaptureNativeWindow).GetProperty("Hwnd", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void Finish(CpuRegionCaptureWindow window, RegionCaptureAction action) => typeof(CpuRegionCaptureWindow)
        .GetMethod("Finish", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [action]);
    private static CpuCaptureImage Pattern(int width, int height)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        { int i = (y * width + x) * 4; pixels[i] = (byte)x; pixels[i + 1] = (byte)y; pixels[i + 2] = (byte)(x + y); pixels[i + 3] = 255; }
        return new(width, height, pixels);
    }
    private static void RasterChecks()
    {
        using var source = Pattern(80, 60);
        var crop = new Rectangle(17, 11, 36, 28);
        using var plain = source.Crop(crop);
        Check(plain.Pixels[0] == 17 && plain.Pixels[1] == 11 && plain.Pixels[^4] == 52 && plain.Pixels[^3] == 38, "Top-down BGRA crop must retain row/channel orientation");
        var rgba = plain.RgbaPixels();
        Check(rgba[0] == 28 && rgba[1] == 11 && rgba[2] == 17 && rgba[3] == 255, "JXL conversion must swap R/B without changing alpha");
        using var copied = CpuCaptureRaster.Render(source, crop, [], 1);
        Check(copied.Pixels.SequenceEqual(plain.Pixels), "A GDI round trip must preserve every source pixel");
        var black = new CpuAnnotation { Tool = CpuAnnotationTool.Line, Start = new(19, 13), End = new(42, 13), Color = Color.Black, Width = 1 };
        using var ink = CpuCaptureRaster.RenderLayer(source, crop, [black], 1);
        int blackOffset = (2 * crop.Width + 2) * 4;
        Check(ink.Pixels[blackOffset] == 0 && ink.Pixels[blackOffset + 3] == 255, "Opaque black annotation must survive alpha repair");
        Check(ink.Pixels[^1] == 0 && ink.Pixels[^2] == 0, "Untouched annotation pixels must remain transparent black");
        var marker = new CpuAnnotation { Tool = CpuAnnotationTool.Highlighter, Start = new(29, 23), End = new(34, 23), Color = Color.Yellow, Width = 1 };
        using var marked = CpuCaptureRaster.Render(source, crop, [marker], 1);
        using var markerLayer = CpuCaptureRaster.RenderLayer(source, crop, [marker], 1);
        int m = (12 * crop.Width + 12) * 4;
        Check(markerLayer.Pixels[m + 3] == 96 && markerLayer.Pixels[m + 1] == 96 && markerLayer.Pixels[m + 2] == 96, "Highlight must be premultiplied, not an opaque overlay");
        Check(marked.Pixels[m + 3] == 255 && marked.Pixels[m + 1] == (255 * 96 + 23 * 159) / 255, "Software export must blend the marker into source pixels");
        byte[] darkPixels = Enumerable.Range(0, 64).SelectMany(_ => new byte[] { 3, 2, 1, 255 }).ToArray();
        using (var darkSource = new CpuCaptureImage(8, 8, darkPixels))
        using (var mosaicLayer = CpuCaptureRaster.RenderLayer(darkSource, new Rectangle(0, 0, 8, 8),
            [new CpuAnnotation { Tool = CpuAnnotationTool.Mosaic, Start = new(2, 2), Points = [new(2, 2)] }], 1))
            Check(mosaicLayer.Pixels[3] == 255 && mosaicLayer.Pixels[0] == 3, "A sampled near-black mosaic must not be mistaken for transparent background");
        using (var overlap = CpuCaptureRaster.RenderLayer(source, crop, [marker,
            new CpuAnnotation { Tool = CpuAnnotationTool.Line, Start = new(29, 23), End = new(35, 23), Color = Color.Black, Width = 1 }], 1))
            Check(overlap.Pixels[m] == 0 && overlap.Pixels[m + 3] == 255, "Opaque ink over a highlight must replace its alpha");
        foreach (var tool in Enum.GetValues<CpuAnnotationTool>().Where(v => v is not CpuAnnotationTool.Select and not CpuAnnotationTool.Eraser and not CpuAnnotationTool.Text))
        {
            var annotation = new CpuAnnotation { Tool = tool, Start = new(25, 20), End = new(40, 28), Text = "1测试", Points = [new(25,20),new(40,28)] };
            using var result = CpuCaptureRaster.Render(source, crop, [annotation], 2);
            Check(!result.Pixels.SequenceEqual(plain.Pixels), $"{tool} must render on the CPU at 200% scale");
        }
        using (var textSource = Pattern(200, 140))
        using (var textResult = CpuCaptureRaster.Render(textSource, new Rectangle(0, 0, 200, 140),
            [new CpuAnnotation { Tool = CpuAnnotationTool.Text, Start = new(8, 8), Text = "测试", Color = Color.White }], 2))
            Check(!textResult.Pixels.SequenceEqual(textSource.Pixels), "Text must render on a sufficiently tall crop at 200% scale");
        Check(CpuRegionSelection.Drag(new(-8, -5), new(200, 90), new(80, 60)) == new Rectangle(0, 0, 80, 60), "Drag must clamp to the virtual desktop");
        Check(CpuRegionSelection.Drag(new(30, 40), new(15, 10), new(80, 60)) == new Rectangle(15, 10, 16, 31), "Reverse drag must preserve inclusive pixel edges");
        for (int handle = 0; handle < 8; handle++)
        {
            Rectangle r = CpuRegionSelection.Resize(crop, handle, new(-1000, 1000), new(80, 60));
            Check(r.Width >= 2 && r.Height >= 2 && r.Left >= 0 && r.Top >= 0 && r.Right <= 80 && r.Bottom <= 60, $"Resize handle {handle} must remain valid");
        }
        var clone = marker.Clone(); clone.Translate(10, 20);
        Check(marker.Start == new Point(29, 23) && clone.Start == new Point(39, 43), "Undo snapshots must own their annotation coordinates");
    }
    private static void WindowChecks()
    {
        using var source = Pattern(720, 480);
        foreach (RegionCaptureAction action in Enum.GetValues<RegionCaptureAction>())
        {
            using var window = new CpuRegionCaptureWindow(source, -1920, -100, action, moveOnscreen: false);
            nint hwnd = Handle(window);
            Field("_selected").SetValue(window, true); Field("_selection").SetValue(window, new Rectangle(100, 120, 240, 180));
            var annotations = (List<CpuAnnotation>)Field("_annotations").GetValue(window)!;
            annotations.Add(new() { Tool = CpuAnnotationTool.Rectangle, Start = new(110, 130), End = new(180, 160), Color = Color.Red });
            CpuCaptureNative.InvalidateRect(hwnd, 0, false); CpuCaptureNative.UpdateWindow(hwnd);
            Finish(window, action);
            var result = window.Completion.Task.GetAwaiter().GetResult();
            try
            {
                Check(result.Action == action && Handle(window) == 0 && !IsWindow(hwnd), $"{action} must complete and destroy its HWND immediately");
                if (action != RegionCaptureAction.Cancel)
                {
                    Check(result.Image is { Width: 240, Height: 180 }, $"{action} must transfer an independently owned crop");
                    bool layerExpected = action is RegionCaptureAction.RecordGif or RegionCaptureAction.LongCapture;
                    Check((result.AnnotationLayer is not null) == layerExpected, "Only recording tools need a separate annotation layer");
                }
            }
            finally { result.Image?.Dispose(); result.AnnotationLayer?.Dispose(); }
            window.Dispose(); Check(Handle(window) == 0, "Disposal must be idempotent");
        }
        using var failed = new CpuRegionCaptureWindow(source, 0, 0, RegionCaptureAction.Save, false);
        failed.Completion.TrySetResult(new(RegionCaptureAction.Cancel, default));
        Field("_selected").SetValue(failed, true); Field("_selection").SetValue(failed, new Rectangle(50, 50, 80, 80));
        Finish(failed, RegionCaptureAction.Save);
        Check(Handle(failed) == 0, "Rejected ownership transfer must still close its window");
        Check(source.Pixels.Length == 720 * 480 * 4, "Closing a borrowed overlay must not dispose the caller's source");
        using var stale = new CpuRegionCaptureWindow(source, -100, -100, RegionCaptureAction.Save, false);
        Field("_pendingMoveIn").SetValue(stale, true);
        CpuCaptureNative.SendMessageW(Handle(stale), 0x113, -1, 0);
        CpuCaptureNative.GetWindowRect(Handle(stale), out var rect);
        Check(rect.Left == -32000 && rect.Top == -32000, "A stale timer must never move or activate a new capture");
        using var retry = new CpuRegionCaptureWindow(source, 0, 0, RegionCaptureAction.Save, false);
        nint retryHandle = Handle(retry);
        var otherThread = new Thread(retry.Dispose);
        otherThread.Start(); otherThread.Join();
        Check(IsWindow(retryHandle) && Handle(retry) == retryHandle, "A failed cross-thread DestroyWindow must keep the live callback rooted");
        retry.Dispose();
        Check(!IsWindow(retryHandle) && Handle(retry) == 0, "A failed DestroyWindow must allow the creating thread to retry disposal");
    }
    private static object Call(CpuRegionCaptureWindow window, string method, params object?[] args) =>
        typeof(CpuRegionCaptureWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args)!;
    private static RegionToolbarLayout Layout(CpuRegionCaptureWindow window) => (RegionToolbarLayout)Call(window, "ToolbarLayout");
    private static RegionToolbarState State(CpuRegionCaptureWindow window) => (RegionToolbarState)Field("_toolbarState").GetValue(window)!;
    private static Rectangle Selection => new(100, 120, 240, 180);
    private static void SetMouse(CpuRegionCaptureWindow window, PointF screen)
    {
        int x = (int)typeof(CpuRegionCaptureWindow).GetField("_virtualX", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        int y = (int)typeof(CpuRegionCaptureWindow).GetField("_virtualY", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        Field("_mouse").SetValue(window, new Point((int)screen.X - x, (int)screen.Y - y));
    }
    private static void PressToolbar(CpuRegionCaptureWindow window, PointF screen)
    {
        SetMouse(window, screen);
        Call(window, "ToolbarPointerDown");
        Call(window, "ToolbarPointerUp");
    }
    private static void Paint(CpuRegionCaptureWindow window)
    {
        var hwnd = Handle(window);
        CpuCaptureNative.InvalidateRect(hwnd, 0, false);
        CpuCaptureNative.UpdateWindow(hwnd);
    }
    private static void ToolbarInteractionChecks()
    {
        using var source = Pattern(800, 600);
        CpuRegionCaptureWindow? toolbarWindow = null;
        using (var window = new CpuRegionCaptureWindow(source, 0, 0, RegionCaptureAction.Save, moveOnscreen: false))
        {
            toolbarWindow = window;
            Field("_selected").SetValue(window, true); Field("_selection").SetValue(window, Selection);
            var layout = Layout(window);
            Check((nint)Field("_toolbarGlyphFont").GetValue(window)! != 0 && (nint)Field("_toolbarLabelFont").GetValue(window)! != 0,
                "Monitor-scaled toolbar fonts are created for rendering");
            Check(State(window).Tool == RegionToolbarCommand.Select, "Toolbar state starts in selection mode");

            var shape = layout.Slots.Single(s => s.Command == RegionToolbarCommand.Shapes);
            PressToolbar(window, new(shape.Chevron.Left + shape.Chevron.Width / 2, shape.Chevron.Top + shape.Chevron.Height / 2));
            Check(State(window).Popup == RegionToolbarCommand.Shapes, "Shape chevron opens the split group menu");
            layout = Layout(window);
            var shapeMenu = RegionToolbarGeometry.Menu(layout, State(window));
            var ellipse = shapeMenu.Slots.Single(s => s.Command == RegionToolbarCommand.Ellipse);
            PressToolbar(window, new(ellipse.Bounds.Left + 4, ellipse.Bounds.Top + 4));
            Check((CpuAnnotationTool)Field("_tool").GetValue(window)! == CpuAnnotationTool.Ellipse, "Popup member dispatch selects the exact annotation tool");
            layout = Layout(window); shape = layout.Slots.Single(s => s.Command == RegionToolbarCommand.Shapes);
            PressToolbar(window, new(shape.Bounds.Left + shape.Bounds.Width / 3, shape.Bounds.Top + shape.Bounds.Height / 2));
            Check((CpuAnnotationTool)Field("_tool").GetValue(window)! == CpuAnnotationTool.Ellipse, "Split group main click resolves its remembered member");

            var toolMap = new Dictionary<RegionToolbarCommand, CpuAnnotationTool>
            {
                [RegionToolbarCommand.Select] = CpuAnnotationTool.Select, [RegionToolbarCommand.Rectangle] = CpuAnnotationTool.Rectangle,
                [RegionToolbarCommand.Ellipse] = CpuAnnotationTool.Ellipse, [RegionToolbarCommand.Line] = CpuAnnotationTool.Line,
                [RegionToolbarCommand.Arrow] = CpuAnnotationTool.Arrow, [RegionToolbarCommand.Number] = CpuAnnotationTool.Number,
                [RegionToolbarCommand.Pen] = CpuAnnotationTool.Pen, [RegionToolbarCommand.Highlighter] = CpuAnnotationTool.Highlighter,
                [RegionToolbarCommand.Mosaic] = CpuAnnotationTool.Mosaic, [RegionToolbarCommand.Blur] = CpuAnnotationTool.Blur,
                [RegionToolbarCommand.Text] = CpuAnnotationTool.Text, [RegionToolbarCommand.Eraser] = CpuAnnotationTool.Eraser
            };
            foreach (var pair in toolMap)
            {
                Call(window, "InvokeToolbar", pair.Key, false);
                Check((CpuAnnotationTool)Field("_tool").GetValue(window)! == pair.Value, $"{pair.Key} dispatch maps to {pair.Value}");
            }
            var annotations = (List<CpuAnnotation>)Field("_annotations").GetValue(window)!;
            Call(window, "Undo", false); Call(window, "Undo", true);
            Check(annotations.Count == 0, "Disabled undo and redo leave annotation history unchanged");

            Call(window, "InvokeToolbar", RegionToolbarCommand.Pen, false);
            layout = Layout(window);
            var color = layout.Choices.Single(c => c.Color && c.Value == RegionToolbarCatalog.Colors[2]);
            PressToolbar(window, new(color.Bounds.Left + 2, color.Bounds.Top + 2));
            layout = Layout(window);
            var width = layout.Choices.Single(c => !c.Color && c.Value == 8);
            PressToolbar(window, new(width.Bounds.Left + 2, width.Bounds.Top + 2));
            Check(State(window).Color == RegionToolbarCatalog.Colors[2] && State(window).Width == 8, "Color and width presets update explicit toolbar parameters");
            SetMouse(window, new(140, 150)); Call(window, "PointerDown"); Call(window, "PointerUp");
            Check(annotations.Count == 1 && annotations[0].Color.ToArgb() == RegionToolbarCatalog.Colors[2]
                && annotations[0].Width == (int)Call(window, "Dip", 8f), "A new annotation draft uses the selected ARGB color and DPI-scaled width");

            nint beforeHdrCommandHwnd = Handle(window);
            Call(window, "InvokeToolbar", RegionToolbarCommand.HdrAnalysis, false);
            Check(Handle(window) == beforeHdrCommandHwnd && !window.Completion.Task.IsCompleted
                && (bool)Field("_selected").GetValue(window)! && annotations.Count == 1,
                "Disabled CPU HDR analysis command is a no-op and does not complete or mutate the capture");

            var beforeSelection = (Rectangle)Field("_selection").GetValue(window)!;
            int beforeCount = annotations.Count;
            Call(window, "InvokeToolbar", RegionToolbarCommand.More, false);
            layout = Layout(window); var menu = RegionToolbarGeometry.Menu(layout, State(window));
            Check(menu.Bounds.Left >= layout.Monitor.WorkArea.Left && menu.Bounds.Right <= layout.Monitor.WorkArea.Right
                && menu.Bounds.Top >= layout.Monitor.WorkArea.Top && menu.Bounds.Bottom <= layout.Monitor.WorkArea.Bottom
                && RectangleF.Intersect(menu.Bounds, layout.Main).IsEmpty
                && (layout.Parameters.IsEmpty || RectangleF.Intersect(menu.Bounds, layout.Parameters).IsEmpty), "Popup fits available monitor space without covering toolbar or presets");
            Check((bool)Call(window, "Key", (int)'A')! && State(window).Popup == RegionToolbarCommand.More,
                "Modal popup consumes unrelated keyboard input");
            Paint(window);
            PressToolbar(window, new(5, 590));
            Check(State(window).Popup is null && (bool)Call(window, "PopupConsumesPointer")!, "Outside press closes popup and consumes the following double-click message");
            Call(window, "ToolbarPointerUp");
            Check((bool)Field("_selected").GetValue(window)! && (Rectangle)Field("_selection").GetValue(window)! == beforeSelection
                && annotations.Count == beforeCount, "Outside popup gesture cannot reselect or add an annotation");

            Call(window, "InvokeToolbar", RegionToolbarCommand.Help, false);
            layout = Layout(window); var help = (RectangleF)Call(window, "HelpBounds", layout);
            string[] helpLines = RegionToolbarCatalog.Help.Split('\n');
            Check(helpLines.Length == 11 && help.Height >= (8 + helpLines.Length * 25 + 8) * layout.Monitor.Scale,
                "On-demand help panel has one visible row for all eleven shortcuts");
            Check(help.Left >= layout.Monitor.WorkArea.Left && help.Right <= layout.Monitor.WorkArea.Right
                && help.Top >= layout.Monitor.WorkArea.Top && help.Bottom <= layout.Monitor.WorkArea.Bottom, "Help panel is clamped to monitor work area");
            // Exercise the layout at 200% even when the test host's active monitor uses another scale.
            var monitor200 = layout.Monitor with { Scale = 2 };
            var layout200 = layout with { Monitor = monitor200 };
            var help200 = (RectangleF)Call(window, "HelpBounds", layout200);
            Check(help200.Height >= 2 * (8 + helpLines.Length * 25 + 8), "All help rows fit the bounded panel at 200% DPI");
            Call(window, "EnsureToolbarFonts", 2f);
            Check((float)Field("_toolbarFontScale").GetValue(window)! == 2f, "Toolbar fonts follow the monitor scale");
            Paint(window);

            Call(window, "InvokeToolbar", RegionToolbarCommand.More, false);
            Check((bool)Call(window, "Key", 27)! && State(window).Popup is null && (bool)Field("_selected").GetValue(window)!, "Escape closes a popup before the selection");
            Call(window, "InvokeToolbar", RegionToolbarCommand.Text, false);
            SetMouse(window, new(140, 150)); Call(window, "StartText");
            nint edit = (nint)Field("_textEdit").GetValue(window)!;
            Check(edit != 0, "Text editor opens in the same offscreen HWND");
            CpuCaptureNative.SetWindowTextW(edit, "discard this draft"); CpuCaptureNative.SendMessageW(edit, 0x100, 27, 0);
            Check((nint)Field("_textEdit").GetValue(window)! == 0 && (bool)Field("_selected").GetValue(window)!
                && annotations.Count == beforeCount, "Escape discards the active text editor before affecting the selection");
            Check((bool)Call(window, "Key", 27)! && !(bool)Field("_selected").GetValue(window)! && !window.Completion.Task.IsCompleted,
                "Escape reselects after popup and editor are closed");
            Check((bool)Call(window, "Key", 27)! && window.Completion.Task.GetAwaiter().GetResult().Action == RegionCaptureAction.Cancel,
                "The next Escape cancels the capture");
        }
        Check(toolbarWindow is not null && (nint)Field("_toolbarGlyphFont").GetValue(toolbarWindow)! == 0
            && (nint)Field("_toolbarLabelFont").GetValue(toolbarWindow)! == 0
            && (nint)Field("_font").GetValue(toolbarWindow)! == 0
            && (nint)Field("_smallFont").GetValue(toolbarWindow)! == 0
            && (nint)Field("_iconFont").GetValue(toolbarWindow)! == 0, "Toolbar and original UI font handles are deleted during window cleanup");

        foreach (var pair in new Dictionary<RegionToolbarCommand, RegionCaptureAction>
        {
            [RegionToolbarCommand.Ocr] = RegionCaptureAction.Ocr, [RegionToolbarCommand.Pin] = RegionCaptureAction.Pin,
            [RegionToolbarCommand.Copy] = RegionCaptureAction.Copy, [RegionToolbarCommand.Save] = RegionCaptureAction.Save,
            [RegionToolbarCommand.Cancel] = RegionCaptureAction.Cancel, [RegionToolbarCommand.Translate] = RegionCaptureAction.Translate,
            [RegionToolbarCommand.RecordGif] = RegionCaptureAction.RecordGif, [RegionToolbarCommand.LongCapture] = RegionCaptureAction.LongCapture
        })
        {
            using var window = new CpuRegionCaptureWindow(source, 0, 0, RegionCaptureAction.Save, moveOnscreen: false);
            Field("_selected").SetValue(window, true); Field("_selection").SetValue(window, new Rectangle(20, 20, 80, 60));
            Call(window, "InvokeToolbar", pair.Key, false);
            var result = window.Completion.Task.GetAwaiter().GetResult();
            Check(result.Action == pair.Value, $"{pair.Key} dispatch completes with {pair.Value}");
            result.Image?.Dispose(); result.AnnotationLayer?.Dispose();
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string? native = Environment.GetEnvironmentVariable("STARSHOT_GPU_COUNTER_DLL");
            if (native is not null) NativeLibrary.SetDllImportResolver(typeof(Program).Assembly,
                (name, _, _) => name == "GpuMemory" ? NativeLibrary.Load(native) : 0);
            if (args.Length == 2 && args[0] == "--pid") { Console.WriteLine(JsonSerializer.Serialize(Read(int.Parse(args[1])), new JsonSerializerOptions { IncludeFields = true })); return 0; }
            RasterChecks(); WindowChecks(); ToolbarInteractionChecks();
            Console.WriteLine($"PASS: {_checks} raster/geometry/window/toolbar/ownership checks; no desktop input, screenshot, clipboard or image files.");
            if (args.Length == 2 && args[0] == "--memory") MemoryChecks(args[1]);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void MemoryChecks(string csv)
    {
        // Synthetic 4K pixels and offscreen HWNDs exercise the exact production renderer.
        // This does not capture the desktop or show/move a window on screen.
        using var output = new StreamWriter(csv);
        output.WriteLine("cycle,phase,timestamp,pid,dedicatedResidentMiB,dedicatedCommittedMiB,sharedResidentMiB,sharedCommittedMiB,privateMiB,gdiHandles,userHandles,queryFailures");
        void Sample(int cycle, string phase)
        {
            var s = Read(Environment.ProcessId); using var process = Process.GetCurrentProcess();
            output.WriteLine(FormattableString.Invariant($"{cycle},{phase},{DateTimeOffset.UtcNow:O},{Environment.ProcessId},{s.DedicatedResident / 1048576d:F4},{s.DedicatedCommitted / 1048576d:F4},{s.SharedResident / 1048576d:F4},{s.SharedCommitted / 1048576d:F4},{process.PrivateMemorySize64 / 1048576d:F4},{GetGuiResources(process.Handle, 0)},{GetGuiResources(process.Handle, 1)},{s.FailedQueries}")); output.Flush();
        }
        Sample(0, "baseline");
        uint gdiBefore = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
        uint userBefore = GetGuiResources(Process.GetCurrentProcess().Handle, 1);
        for (int cycle = 1; cycle <= 20; cycle++)
        {
            using (var source = Pattern(3840, 2160))
            using (var window = new CpuRegionCaptureWindow(source, -1920, 0, RegionCaptureAction.Save, false))
            {
                Field("_selected").SetValue(window, true); Field("_selection").SetValue(window, new Rectangle(0, 0, 3840, 2160));
                var annotations = (List<CpuAnnotation>)Field("_annotations").GetValue(window)!;
                annotations.Add(new() { Tool = CpuAnnotationTool.Rectangle, Start = new(400, 400), End = new(1200, 1000), Color = Color.Red });
                CpuCaptureNative.InvalidateRect(Handle(window), 0, false); CpuCaptureNative.UpdateWindow(Handle(window));
                Sample(cycle, "overlay");
                Finish(window, RegionCaptureAction.Save);
                var result = window.Completion.Task.GetAwaiter().GetResult(); result.Image?.Dispose(); result.AnnotationLayer?.Dispose();
            }
            Thread.Sleep(5000); Sample(cycle, "after5s");
        }
        using var self = Process.GetCurrentProcess();
        Check(GetGuiResources(self.Handle, 0) <= gdiBefore + 1 && GetGuiResources(self.Handle, 1) <= userBefore + 1, "Twenty 4K cycles must not accumulate native GDI or HWND handles");
        Console.WriteLine($"PASS: 20 synthetic 4K cycles; CSV {csv}; no GC.Collect, D3D device or saved pixels.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct GpuSample
    {
        public ulong DedicatedResident, SharedResident, DedicatedCommitted, SharedCommitted;
        public uint Adapters, ResidentQueries, CommittedQueries, FailedQueries;
        public uint EnumeratedAdapters, FirstFailure;
    }
    private static GpuSample Read(int pid)
    {
        uint error = ReadGpuMemory((uint)pid, out var result);
        if (error != 0 || result.Adapters == 0 || result.ResidentQueries == 0 || result.CommittedQueries == 0)
            throw new InvalidOperationException($"GPU counters unavailable: error={error:X8}, enumAdapters={result.EnumeratedAdapters}, adapters={result.Adapters}, residentQueries={result.ResidentQueries}, committedQueries={result.CommittedQueries}, firstFailure={result.FirstFailure:X8}");
        return result;
    }
    [DllImport("GpuMemory", CallingConvention = CallingConvention.Cdecl)] private static extern uint ReadGpuMemory(uint pid, out GpuSample sample);
    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint process, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
}
