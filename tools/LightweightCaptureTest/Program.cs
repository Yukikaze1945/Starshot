using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Starshot.Features.Screenshot;

internal static partial class Program
{
    private static int _checks;
    private static string? OcrReplay;
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
        using (var blit = new CpuCaptureSurface(crop.Width, crop.Height))
        {
            CpuCaptureGdi.Blit(blit.Dc, source.Pixels, source.Width, source.Height,
                new Rectangle(0, 0, crop.Width, crop.Height), crop);
            using var actual = blit.ToImage();
            Check(actual.Pixels.SequenceEqual(plain.Pixels),
                $"Partial top-down DIB must read the requested crop: first source pixel expected B={plain.Pixels[0]},G={plain.Pixels[1]}, actual B={actual.Pixels[0]},G={actual.Pixels[1]}");
        }
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
        foreach (var tool in Enum.GetValues<CpuAnnotationTool>().Where(v => v is not CpuAnnotationTool.Select and not CpuAnnotationTool.Eraser and not CpuAnnotationTool.Text and not CpuAnnotationTool.Translation))
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

    private static void PartialBlitChecks()
    {
        var mismatches = new List<string>();
        using var source = Pattern(130, 97);
        Rectangle[] crops = [new(0,0,26,19), new(79,0,51,27), new(0,65,32,32), new(91,70,39,27),
            new(17,11,36,28), new(0,0,130,97), new(0,0,1,1), new(129,96,1,1)];
        foreach (Rectangle crop in crops)
        foreach (int scale in new[] { 1, 2 })
        {
            var target = new Rectangle(7, 9, crop.Width * scale, crop.Height * scale);
            using var surface = new CpuCaptureSurface(target.Right + 7, target.Bottom + 9);
            CpuCaptureGdi.Fill(surface.Dc, new Rectangle(0, 0, surface.Width, surface.Height), Color.FromArgb(19, 23, 29));
            CpuCaptureGdi.Blit(surface.Dc, source.Pixels, source.Width, source.Height, target, crop);
            using var actual = surface.ToImage();
            bool matches = true;
            for (int y = 0; y < target.Height; y++) for (int x = 0; x < target.Width; x++)
            {
                int s = ((crop.Y + y / scale) * source.Width + crop.X + x / scale) * 4;
                int d = ((target.Y + y) * actual.Width + target.X + x) * 4;
                matches &= actual.Pixels.AsSpan(d, 3).SequenceEqual(source.Pixels.AsSpan(s, 3));
            }
            if (!matches) mismatches.Add($"{crop}, scale {scale}, first=({actual.Pixels[(target.Y * actual.Width + target.X) * 4]},{actual.Pixels[(target.Y * actual.Width + target.X) * 4 + 1]}), expected=({crop.X},{crop.Y})");
            Check(actual.Pixels[0] == 29 && actual.Pixels[1] == 23 && actual.Pixels[2] == 19,
                "Source-coordinate fix must preserve the destination origin and surrounding backdrop");
        }
        Check(mismatches.Count == 0, "Top-down partial blit matches every pixel: " + string.Join("; ", mismatches));
    }
    private static void WindowChecks()
    {
        using var source = Pattern(720, 480);
        foreach (RegionCaptureAction action in Enum.GetValues<RegionCaptureAction>().Where(a=>a is not (RegionCaptureAction.Subtitles or RegionCaptureAction.TranslateInPlace)))
        {
            using var window = new CpuRegionCaptureWindow(source, -1920, -100, action, moveOnscreen: false);
            nint hwnd = Handle(window);
            Field("_selected").SetValue(window, true); Field("_selection").SetValue(window, new Rectangle(100, 120, 240, 180));
            Paint(window);
            var plainBuffer = (CpuCaptureSurface)Field("_backBuffer").GetValue(window)!;
            CpuCaptureGdi.GdiFlush();
            int plainPixel = (220 * 720 + 220) * 4;
            Check(plainBuffer.Pixels.Slice(plainPixel, 3).SequenceEqual(source.Pixels.AsSpan(plainPixel, 3)),
                "Unannotated selection shows the exact source coordinate before any preview surface exists");
            var annotations = (List<CpuAnnotation>)Field("_annotations").GetValue(window)!;
            annotations.Add(new() { Tool = CpuAnnotationTool.Rectangle, Start = new(110, 130), End = new(180, 160), Color = Color.Red });
            CpuCaptureNative.InvalidateRect(hwnd, 0, false); CpuCaptureNative.UpdateWindow(hwnd);
            var backBuffer = (CpuCaptureSurface?)Field("_backBuffer").GetValue(window);
            Check(backBuffer is { Width: 720, Height: 480 }, "Selection must compose into a full-size CPU back buffer");
            CpuCaptureGdi.GdiFlush();
            int unchangedPixel = (220 * 720 + 220) * 4;
            Check(backBuffer!.Pixels.Slice(unchangedPixel, 3).SequenceEqual(source.Pixels.AsSpan(unchangedPixel, 3)),
                "The completed back buffer must retain undimmed selection pixels");
            Finish(window, action);
            var result = window.Completion.Task.GetAwaiter().GetResult();
            try
            {
                Check(result.Action == action && Handle(window) == 0 && !IsWindow(hwnd), $"{action} must complete and destroy its HWND immediately");
                Check(backBuffer.Dc == 0 && backBuffer.Bits == 0 && Field("_backBuffer").GetValue(window) is null,
                    "Closing must release the CPU presentation buffer and its GDI handles");
                if (action != RegionCaptureAction.Cancel)
                {
                    Check(result.Image is { Width: 240, Height: 180 }, $"{action} must transfer an independently owned crop");
                    int savedPixel = ((220 - 120) * 240 + 220 - 100) * 4;
                    Check(result.Image!.Pixels.AsSpan(savedPixel, 3).SequenceEqual(source.Pixels.AsSpan(plainPixel, 3)),
                        "Selection preview, annotated preview and exported crop retain the same physical source pixel");
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

    private static void ShortcutHintChecks()
    {
        using var source = Pattern(1024, 768);
        foreach (float scale in new[] { 1f, 2f })
        {
            using var window = new CpuRegionCaptureWindow(source, -1920, -100, RegionCaptureAction.Save, false);
            Field("_scale").SetValue(window, scale);
            Field("_selected").SetValue(window, true);
            Field("_selection").SetValue(window, new Rectangle(760, 90, 180, 130));
            Field("_mouse").SetValue(window, new Point(1000, 0));
            Rectangle hint = (Rectangle)Call(window, "ShortcutHintBounds");
            int y = hint.Bottom - (int)(5 * scale), left = hint.Left + (int)(20 * scale), right = hint.Right - (int)(20 * scale);
            Paint(window);
            var buffer = (CpuCaptureSurface)Field("_backBuffer").GetValue(window)!;
            byte[] Pixel(int x) { CpuCaptureGdi.GdiFlush(); return buffer.Pixels.Slice((y * source.Width + x) * 4, 3).ToArray(); }
            byte[] Source(int x) => source.Pixels.AsSpan((y * source.Width + x) * 4, 3).ToArray();
            Check(Pixel(left).SequenceEqual(new byte[] { 243, 250, 249 }), "Passive hint remains visible away from pointer and selection");
            Field("_mouse").SetValue(window, new Point(hint.Left - (int)(10 * scale), hint.Top + hint.Height / 2));
            Paint(window);
            Check(Pixel(left).SequenceEqual(Source(left).Select(v => (byte)(v * 58 / 100))),
                "Approaching within 24 DIP hides the entire hint, including its background");
            Field("_mouse").SetValue(window, new Point(1000, 0)); Paint(window);
            Check(Pixel(left).SequenceEqual(new byte[] { 243, 250, 249 }), "Moving away restores the passive hint without timers");
            var selection = new Rectangle(hint.Left - 8, hint.Top - 8, hint.Width / 2, hint.Height + 16);
            Field("_selection").SetValue(window, selection); Paint(window);
            Check(Pixel(left).SequenceEqual(Source(left)), "Selection restores clean source pixels over the overlapping half of the hint");
            Check(Pixel(right).SequenceEqual(new byte[] { 243, 250, 249 }), "Non-overlapping half of the hint remains visible");
            selection = new Rectangle(hint.Left - 8, hint.Top - 8, hint.Width + 16, hint.Height + 16);
            Field("_selection").SetValue(window, selection); Paint(window);
            Check(Pixel(left).SequenceEqual(Source(left)) && Pixel(right).SequenceEqual(Source(right)), "Selection covering the whole hint has no hint background or text");
            Finish(window, RegionCaptureAction.Save);
            var result = window.Completion.Task.GetAwaiter().GetResult();
            using var image = result.Image;
            using var expected = source.Crop(selection);
            Check(image is not null && image.Pixels.SequenceEqual(expected.Pixels), "Saved image never contains hint UI pixels");
        }
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
            Call(window, "InvokeToolbar", RegionToolbarCommand.GamutAnalysis, false);
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
            Check(helpLines.Length == 12 && help.Height >= (8 + helpLines.Length * 25 + 8) * layout.Monitor.Scale,
                "On-demand help panel has one visible row for all twelve shortcuts, including Quick Save");
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
            [RegionToolbarCommand.QuickSave] = RegionCaptureAction.QuickSave,
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
            if(args.Length==2&&args[0]=="--frame-preview"){FramePreview(args[1]);return 0;}
            string? native = Environment.GetEnvironmentVariable("STARSHOT_GPU_COUNTER_DLL");
            if (native is not null) NativeLibrary.SetDllImportResolver(typeof(Program).Assembly,
                (name, _, _) => name == "GpuMemory" ? NativeLibrary.Load(native) : 0);
            if (args.Length == 2 && args[0] == "--pid") { Console.WriteLine(JsonSerializer.Serialize(Read(int.Parse(args[1])), new JsonSerializerOptions { IncludeFields = true })); return 0; }
            if(args.Length==2&&args[0]=="--ocr-lines")OcrReplay=args[1];
            RasterChecks(); PartialBlitChecks(); WindowChecks(); ShortcutHintChecks(); ToolbarInteractionChecks(); ToolbarEditorChecks();TranslationChecks();SubtitleOutlineChecks();FrameStyleChecks();
            if (args.Length == 2 && args[0] == "--editor-preview")
            {
                using var editor = new CpuCaptureToolbarEditor();
                typeof(CpuCaptureToolbarEditor).GetField("_scale", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, 1f);
                nint hwnd = (nint)typeof(CpuCaptureNativeWindow).GetProperty("Hwnd", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
                CpuCaptureNative.SetWindowPos(hwnd, 0, -32000, -32000, 776, 610, 0x14);
                CpuCaptureNative.InvalidateRect(hwnd, 0, false); CpuCaptureNative.SendMessageW(hwnd, 0x0F, 0, 0);
                var surface = (CpuCaptureSurface)typeof(CpuCaptureToolbarEditor).GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
                using var image = surface.ToImage(); File.WriteAllBytes(args[1], image.Pixels);
                Console.WriteLine($"PREVIEW {image.Width} {image.Height}");
            }
            Console.WriteLine($"PASS: {_checks} raster/geometry/window/toolbar/ownership checks; no desktop input, screenshot, clipboard or image files.");
            if (args.Length == 2 && args[0] == "--memory") MemoryChecks(args[1]);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void TranslationChecks()
    {
        using var source=Pattern(640,360);
        // Reproduce Small's empty icon lines without a model or remote request.
        Starshot.Helpers.OcrHelper.Lines=[new("Appearance",new(8,6,100,20)),new("",new(120,6,8,5)),new("  ",new(130,6,8,5)),new("Mode",new(8,35,55,20))];
        var original=(byte[])source.Pixels.Clone();
        var translated=RegionTranslation.TranslateAsync(source.Pixels,source.Width,source.Height,CancellationToken.None).GetAwaiter().GetResult();
        Check(translated.Length==2&&Starshot.Helpers.OcrTranslationClient.Segments==2,"Empty icon OCR lines never reach formatted translation");
        Check(original.SequenceEqual(source.Pixels),"Translation never mutates frozen SDR input");
        if(OcrReplay is not null)
        {
            using var json=JsonDocument.Parse(File.ReadAllText(OcrReplay));var sample=json.RootElement.EnumerateArray().Single(r=>r.GetProperty("model").GetString()=="small");
            var raw=sample.GetProperty("lines").EnumerateArray().ToArray();
            Starshot.Helpers.OcrHelper.Lines=raw.Select(l=>new Starshot.Helpers.OcrLine(l.GetProperty("text").GetString()!,new((float)l.GetProperty("x").GetDouble(),(float)l.GetProperty("y").GetDouble(),(float)l.GetProperty("width").GetDouble(),(float)l.GetProperty("height").GetDouble()))).ToList();
            var pixels=new byte[1134*1047*4];Array.Fill(pixels,(byte)40);for(int a=3;a<pixels.Length;a+=4)pixels[a]=255;
            var replay=RegionTranslation.TranslateAsync(pixels,1134,1047,CancellationToken.None).GetAwaiter().GetResult();
            Check(raw.Length==25&&replay.Length==23&&Starshot.Helpers.OcrTranslationClient.Segments==23,"User image actual Small results: 25 rows minus two empty icons = 23 valid segments");
            Console.WriteLine("PASS attached image actual OCR output replay: 23 valid translated overlays; translation backend is a deterministic test double, no external API/model.");
        }
        using(var window=new CpuRegionCaptureWindow(source,-1920,-200,RegionCaptureAction.Save,moveOnscreen:false))
        {
            Field("_selected").SetValue(window,true);Field("_selection").SetValue(window,new Rectangle(20,30,300,100));
            Call(window,"ApplyRegionTranslation",(object)translated);
            Check(((List<CpuAnnotation>)Field("_annotations").GetValue(window)!).Count==1,"Translation is one undoable overlay in the current screenshot");
            Call(window,"Undo",false);Check(((List<CpuAnnotation>)Field("_annotations").GetValue(window)!).Count==0,"Ctrl+Z removes the entire translation batch");
            Call(window,"Undo",true);Check(((List<CpuAnnotation>)Field("_annotations").GetValue(window)!).Count==1,"Redo restores translation");
            Finish(window,RegionCaptureAction.Copy);var result=window.Completion.Task.GetAwaiter().GetResult();
            using var image=result.Image;using var expected=source.Crop(new(20,30,300,100));
            Check(result.Action==RegionCaptureAction.Copy&&image is not null&&!image.Pixels.SequenceEqual(expected.Pixels),"Copy result contains the translated overlay, no editor/gallery handoff");
            Check(Handle(window)==0,"Translated screenshot closes normally");
        }
        using(var window=new CpuRegionCaptureWindow(source,-1920,-200,RegionCaptureAction.Save,moveOnscreen:false))
        {
            Field("_selected").SetValue(window,true);Field("_selection").SetValue(window,new Rectangle(20,30,300,100));
            Finish(window,RegionCaptureAction.Subtitles);var result=window.Completion.Task.GetAwaiter().GetResult();
            Check(result.Action==RegionCaptureAction.Subtitles&&result.PhysicalRect==new Rectangle(20,30,300,100),"Subtitle toolbar action returns the existing physical ROI");
            Check(result.Image is null&&Handle(window)==0,"Subtitle handoff has no extra crop or independent frame");
        }
        foreach(bool close in new[]{false,true})
        {
            var pending=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);Starshot.Helpers.OcrHelper.Pending=pending.Task;Starshot.Helpers.OcrHelper.Started=false;
            using var window=new CpuRegionCaptureWindow(source,0,0,RegionCaptureAction.Save,moveOnscreen:false);
            Field("_selected").SetValue(window,true);Field("_selection").SetValue(window,new Rectangle(20,30,300,100));
            var work=(Task)Call(window,"TranslateRegionInPlaceAsync");
            Check(SpinWait.SpinUntil(()=>Starshot.Helpers.OcrHelper.Started,2000),"Translation inference started asynchronously");
            if(close)window.Dispose();else Field("_selection").SetValue(window,new Rectangle(40,50,300,100));
            pending.SetResult();work.GetAwaiter().GetResult();
            Check(((List<CpuAnnotation>)Field("_annotations").GetValue(window)!).Count==0,close?"Closing cancels inference; late translation cannot republish":"Changing selection rejects stale translated boxes");
            Starshot.Helpers.OcrHelper.Pending=null;
        }
    }

    private static void ToolbarEditorChecks()
    {
        Type type = typeof(CpuCaptureToolbarEditor);
        object? Read(object editor, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor);
        void Do(object editor, string name, params object[] args) => type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, args);
        nint H(object editor) => (nint)typeof(CpuCaptureNativeWindow).GetProperty("Hwnd", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
        string? written = null;
        RegionToolbarPreferences.Configure(null, value => written = value);
        var defaults = RegionToolbarPreferences.Current;
        using (var editor = new CpuCaptureToolbarEditor())
        {
            CpuCaptureNative.InvalidateRect(H(editor), 0, false); CpuCaptureNative.SendMessageW(H(editor), 0x0F, 0, 0);
            Check(Read(editor, "_buffer") is CpuCaptureSurface, "Hidden native editor paints through CPU GDI");
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            {
                type.GetField("_scale", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor,scale);
                Do(editor,"CreateFonts");
                CpuCaptureNative.SetWindowPos(H(editor),0,-32000,-32000,(int)(760*scale),(int)(570*scale),0x14);
                var first = (Rectangle)type.GetMethod("ListBounds",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(editor,[0])!;
                var second = (Rectangle)type.GetMethod("ListBounds",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(editor,[1])!;
                var accept = (Rectangle)type.GetMethod("Button",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(editor,["accept"])!;
                var down = (Rectangle)type.GetMethod("Button",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(editor,["down"])!;
                Check(!first.IntersectsWith(second) && first.Bottom < accept.Top && second.Bottom < accept.Top && down.Bottom < accept.Top,
                    $"Native two-column editor controls stay clear at {scale*100}% DPI");
                CpuCaptureNative.SendMessageW(H(editor),0x0F,0,0);
            }
            var model = (RegionToolbarEditModel)Read(editor, "_model")!;
            Do(editor, "Action", "right");
            Check(!model.Main.Contains(defaults.Main[0]) && model.Secondary[^1] == defaults.Main[0], "Editor right arrow transfers selected button");
            Do(editor, "Action", "up");
            Check(model.Secondary[^2] == defaults.Main[0], "Editor up arrow reorders");
            Do(editor, "Action", "remove");
            Check(model.Unused().Contains(defaults.Main[0]), "Editor delete returns item to library");
            Do(editor, "Action", "add");
            Do(editor, "AddFromPool", Array.IndexOf(model.Unused(), defaults.Main[0]));
            Check(model.Secondary[^1] == defaults.Main[0] && !(bool)Read(editor,"_pool")!, "Library adds item back without duplicates");
            Do(editor, "Action", "cancel");
            Check(written is null && RegionToolbarPreferences.Current.Main.SequenceEqual(defaults.Main), "Cancel leaves persisted and active layout unchanged");
            Check(H(editor) == 0 && Read(editor, "_buffer") is null && (nint)Read(editor, "_font")! == 0, "Editor close releases HWND, DIB and fonts");
        }
        using (var editor = new CpuCaptureToolbarEditor())
        {
            var model = (RegionToolbarEditModel)Read(editor, "_model")!;
            Do(editor, "Action", "remove"); Do(editor, "Action", "accept");
            Check(written is not null && RegionToolbarPreferences.Current.Main.SequenceEqual(model.Main), "Confirm persists and activates layout");
        }
        using (var editor = new CpuCaptureToolbarEditor())
        {
            Do(editor,"Action","reset");Do(editor,"Action","accept");
            Check(RegionToolbarPreferences.Current.Main.SequenceEqual(defaults.Main), "Restore defaults is confirmed explicitly");
        }
        using (var editor = new CpuCaptureToolbarEditor())
        {
            nint hwnd = H(editor);
            var left = (Rectangle)type.GetMethod("ListBounds", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [0])!;
            var right = (Rectangle)type.GetMethod("ListBounds", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [1])!;
            int d = (int)type.GetMethod("D", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [18f])!;
            nint Point(int x,int y)=>(nint)((y<<16)|(x&0xffff));
            CpuCaptureNative.SendMessageW(hwnd,0x201,1,Point(left.Left+d,left.Top+d));
            CpuCaptureNative.SendMessageW(hwnd,0x200,1,Point(right.Left+d,right.Top+d));
            CpuCaptureNative.SendMessageW(hwnd,0x202,0,Point(right.Left+d,right.Top+d));
            var model = (RegionToolbarEditModel)Read(editor,"_model")!;
            Check(model.Secondary[0] == defaults.Main[0] && !model.Main.Contains(defaults.Main[0]), "Actual mouse messages drag button between columns without desktop input");
            Do(editor,"Action","add");CpuCaptureNative.SendMessageW(hwnd,0x100,27,0);
            Check(H(editor)!=0 && !(bool)Read(editor,"_pool")!,"First Esc closes button library");
            CpuCaptureNative.SendMessageW(hwnd,0x100,27,0);Check(H(editor)==0,"Next Esc closes editor");
        }
        RegionToolbarPreferences.Configure(null, _=>{});
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
