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
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string? native = Environment.GetEnvironmentVariable("STARSHOT_GPU_COUNTER_DLL");
            if (native is not null) NativeLibrary.SetDllImportResolver(typeof(Program).Assembly,
                (name, _, _) => name == "GpuMemory" ? NativeLibrary.Load(native) : 0);
            if (args.Length == 2 && args[0] == "--pid") { Console.WriteLine(JsonSerializer.Serialize(Read(int.Parse(args[1])), new JsonSerializerOptions { IncludeFields = true })); return 0; }
            RasterChecks(); WindowChecks();
            Console.WriteLine($"PASS: {_checks} raster/geometry/window/ownership checks; no desktop input, screenshot, clipboard or image files.");
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
