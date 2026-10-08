using System.Collections;
using System.Reflection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;
using Starshot.Features.Screenshot;
using Windows.Foundation;
using Windows.System;

internal static class Program
{
    [STAThread] internal static void Main()
    {
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string path = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "Starshot.dll"));
        Trace("WinRT init begin");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Trace("WinRT init done; Application.Start begin");
        Application.Start(callback =>
        {
            Trace("Application.Start callback");
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            try { _ = new Starshot.ToolbarValidation.ToolbarTestApp(); }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("STARSHOT_TOOLBAR_UI_REPORT")!, "xaml-interaction.log"), ex.ToString());
                Environment.Exit(1);
            }
        });
    }
    internal static void Trace(string phase) => File.AppendAllText(Path.Combine(Environment.GetEnvironmentVariable("STARSHOT_TOOLBAR_UI_REPORT")!, "xaml-runner.log"), phase + "\n");
}

// Retain the real XAML metadata/resources; override launch so no tray, hotkeys, singleton,
// configuration, WGC capture, browser, installed app or desktop input is involved.
namespace Starshot.ToolbarValidation
{
public sealed partial class ToolbarTestApp : Starshot.App
{
    private int _checks;
    private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly Type WindowType = typeof(RegionCaptureWindow);
    private static object? Field(object value, string name) => WindowType.GetField(name, Flags)!.GetValue(value);
    private static void Set(object value, string name, object? content) => WindowType.GetField(name, Flags)!.SetValue(value, content);
    private static object? Call(object value, string name, params object?[] args) => WindowType.GetMethod(name, Flags)!.Invoke(value, args);
    private static object Command(string name) => Enum.Parse(WindowType.Assembly.GetType("Starshot.Features.Screenshot.RegionToolbarCommand")!, name);
    private static void State(object value, string name) => Set(value, "_state", Enum.Parse(WindowType.GetNestedType("RegionCaptureState", BindingFlags.NonPublic)!, name));
    private void Check(bool condition, string label) { _checks++; if (!condition) throw new InvalidOperationException(label); }
    private static void Click(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        global::Program.Trace("Test launch callback");
        string report = Environment.GetEnvironmentVariable("STARSHOT_TOOLBAR_UI_REPORT") ?? throw new InvalidOperationException("Report directory is required");
        RegionCaptureWindow? window = null;
        try
        {
            ((System.Timers.Timer)typeof(Starshot.App).GetField("_gcTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!).Stop();
            window = new RegionCaptureWindow();
            Call(window, "MoveOffscreen"); // Never Show/Activate this synthetic test overlay.
            if (Environment.GetEnvironmentVariable("STARSHOT_HDR_DIAGNOSTICS") == "1")
            {
                await ValidateHdrLifecycle(window, report);
                File.WriteAllText(Path.Combine(report, "xaml-interaction.log"), $"PASS {_checks} isolated HDR lifecycle diagnostics; 15 cycles; hidden HWND; no desktop screenshots/input.\n");
                window.Close(); Environment.Exit(0); return;
            }
            Set(window, "_lockedW", 1920f); Set(window, "_lockedH", 1080f); Set(window, "_isClosed", false);
            WindowType.GetProperty("SelectionRect")!.SetValue(window, new Rect(100, 100, 700, 400));
            State(window, "Selected");
            foreach (var entry in new[] { RegionCaptureAction.Copy, RegionCaptureAction.Save, RegionCaptureAction.Ocr })
            {
                Set(window, "_defaultAction", entry); Call(window, "RefreshToolbar");
                var bar = (Border)Field(window, "SelectionToolbar")!;
                Check(bar.Width < 880 && bar.Height > 0, "Compact toolbar uses measured shared geometry");
                var buttons = ((Canvas)Field(window, "ToolbarButtons")!).Children.OfType<Button>().ToArray();
                var primary = buttons.Single(button => button.Tag?.ToString() == entry.ToString());
                Check(((SolidColorBrush)primary.Background).Color == Windows.UI.Color.FromArgb(255, 221, 243, 105), "Entry-specific primary accent");
                Check(buttons.Single(button => button.Tag?.ToString() == "Undo").IsEnabled == false, "Empty history disables undo");
            }
            Call(window, "InvokeToolbar", Command("Rectangle"));
            Check(((Border)Field(window, "ToolbarParameters")!).Visibility == Visibility.Visible, "Annotation opens parameter panel");
            Check(((Canvas)Field(window, "ToolbarParameterButtons")!).Children.Count == 10, "Five colors and five stroke widths");
            var parameters = ((Canvas)Field(window, "ToolbarParameterButtons")!).Children.OfType<Button>().ToArray();
            Click(parameters[1]); await Task.Delay(40);
            Check((Windows.UI.Color)Field(window, "_annotationColor")! == Windows.UI.Color.FromArgb(255, 49, 137, 255), "Actual swatch button click applies its color");
            parameters = ((Canvas)Field(window, "ToolbarParameterButtons")!).Children.OfType<Button>().ToArray();
            Click(parameters[8]); await Task.Delay(40);
            Check((int)Field(window, "_annotationWidth")! == 8, "Actual width button click applies explicit preset");
            var split = ((Canvas)Field(window, "ToolbarButtons")!).Children.OfType<Button>().Single(button => button.Tag?.ToString() == "Shapes");
            Click(split); await Task.Delay(40);
            Check(((Border)Field(window, "ToolbarPopup")!).Visibility == Visibility.Visible, "In-overlay group popup");
            Call(window, "HandleCaptureKey", VirtualKey.Down); Call(window, "HandleCaptureKey", VirtualKey.Enter);
            Check(Field(window, "_annotationTool")!.ToString() == "Ellipse", "Keyboard group selection activates tool");
            Call(window, "InvokeToolbar", Command("Text"));
            Check(((Canvas)Field(window, "ToolbarParameterButtons")!).Children.Count == 5, "Text shows only applicable color parameters");
            Call(window, "OpenToolbarMenu", Command("More"));
            Check((bool)Call(window, "ConsumeToolbarCanvasPress", new Point(200, 200))!, "Outside press consumes popup gesture");
            Check(Field(window, "_state")!.ToString() == "Selected" && ((IList)Field(window, "_annotations")!).Count == 0, "Dismissal does not alter selection or annotations");
            Check((bool)Call(window, "ConsumeToolbarCanvasPress", new Point(200, 200))!, "Second press of dismissal double-click also consumed");
            Call(window, "OpenToolbarMenu", Command("Brushes")); Call(window, "HandleCaptureKey", VirtualKey.Escape);
            Check(Field(window, "_state")!.ToString() == "Selected", "Esc closes menu before selection");
            var editor = (TextBox)Field(window, "AnnotationTextEditor")!;
            editor.Text = "discard"; editor.Visibility = Visibility.Visible;
            Call(window, "HandleCaptureKey", VirtualKey.Escape);
            Check(editor.Visibility == Visibility.Collapsed && ((IList)Field(window, "_annotations")!).Count == 0, "Esc discards text before selection");
            Call(window, "InvokeToolbar", Command("Help"));
            Check(((Border)Field(window, "ShortcutHint")!).Visibility == Visibility.Visible, "Shortcut help is on demand");
            Call(window, "HandleCaptureKey", VirtualKey.Escape);
            Check(((Border)Field(window, "ShortcutHint")!).Visibility == Visibility.Collapsed, "Esc hides help");
            Call(window, "HandleCaptureKey", VirtualKey.Escape);
            Check(Field(window, "_state")!.ToString() == "Selecting", "Next Esc returns to selection");
            Call(window, "HandleCaptureKey", VirtualKey.Escape);
            Check(Field(window, "_state")!.ToString() == "Closed", "Final Esc closes capture");
            Check(Field(window, "_swapChain") is null && Field(window, "_canvasOriginal") is null, "No synthetic test capture/device was created");
            await ValidateHdrAnalysis(window);
            await BenchmarkHdrAnalysis(window, report);
            File.WriteAllText(Path.Combine(report, "xaml-interaction.log"), $"PASS {_checks} real XAML toolbar checks; hidden HWND; 0 screenshots; 0 desktop input; installed Starshot untouched.\n");
            window.Close(); Environment.Exit(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(report, "xaml-interaction.log"), ex.ToString());
            window?.Close(); Environment.Exit(1);
        }
    }

    private void AnalysisMonitors(object window, params (Rect Bounds, bool Hdr, float White, float Peak)[] monitors)
    {
        var monitorType = WindowType.GetNestedType("AnalysisMonitor", BindingFlags.NonPublic)!;
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(monitorType))!;
        foreach (var monitor in monitors)
            list.Add(Activator.CreateInstance(monitorType, monitor.Bounds, monitor.Hdr, monitor.White, monitor.Peak)!);
        Set(window, "_analysisMonitors", list);
    }
    private async Task ValidateHdrAnalysis(RegionCaptureWindow window)
    {
        var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
        int width = 64, height = 48;
        var pixels = new Half[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        { pixels[i] = pixels[i + 1] = pixels[i + 2] = (Half)2.5f; pixels[i + 3] = (Half)1; }
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();
        using var source = Microsoft.Graphics.Canvas.CanvasBitmap.CreateFromBytes(device, bytes, width, height,
            Windows.Graphics.DirectX.DirectXPixelFormat.R16G16B16A16Float, 96, Microsoft.Graphics.Canvas.CanvasAlphaMode.Ignore);
        var previewBytes = Enumerable.Repeat((byte)127, width * height * 4).ToArray();
        using var preview = Microsoft.Graphics.Canvas.CanvasBitmap.CreateFromBytes(device, previewBytes, width, height,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, Microsoft.Graphics.Canvas.CanvasAlphaMode.Ignore);
        Set(window, "_canvasOriginal", source); Set(window, "_displayBitmap", preview);
        Set(window, "_isClosed", false); Set(window, "_scale", 2f); Set(window, "_vx", 0); Set(window, "_vy", 0);
        Set(window, "_lockedW", 32f); Set(window, "_lockedH", 24f);
        WindowType.GetProperty("SelectionRect")!.SetValue(window, new Rect(0, 0, 32, 24));
        State(window, "Selected");
        AnalysisMonitors(window, (new Rect(0, 0, 64, 48), false, 80, 0));
        Check(!(bool)Call(window, "CanAnalyzeHdr")!, "SDR monitor region cannot masquerade as absolute HDR in a float composite");
        AnalysisMonitors(window, (new Rect(0, 0, 32, 48), true, 203, 1000), (new Rect(32, 0, 32, 48), false, 80, 0));
        Check(!(bool)Call(window, "CanAnalyzeHdr")!, "Mixed SDR/HDR region is disabled");
        AnalysisMonitors(window, (new Rect(0, 0, 64, 48), true, 203, 1000));
        Check((bool)Call(window, "CanAnalyzeHdr")!, "Actual raw FP16 HDR region enables analysis");
        Call(window, "RefreshToolbar"); Call(window, "OpenToolbarMenu", Command("More"));
        Check(((Canvas)Field(window, "ToolbarPopupButtons")!).Children.OfType<Button>().Single(b => b.Tag?.ToString() == "HdrAnalysis").IsEnabled,
            "More menu exposes enabled HDR analyzer");
        Call(window, "DismissToolbarOutsidePress");
        using var cropBefore = (Microsoft.Graphics.Canvas.CanvasRenderTarget)Call(window, "CropDisplayToBgra")!;
        var originalCrop = cropBefore.GetPixelBytes();
        await (Task)Call(window, "OpenHdrAnalysisAsync")!;
        var analysis = (HdrLuminanceAnalysis)Field(window, "_hdrAnalysis")!;
        Check(analysis.Stats.Max == 200 && analysis.Stats.Average == 200 && analysis.Stats.P99 == 200, "GPU readback uses FP16 instead of SDR preview; no white renormalization");
        Check(((Border)Field(window, "HdrAnalysisPanel")!).Visibility == Visibility.Visible, "Closable floating analysis panel is visible");
        Call(window, "UpdateHdrAnalysisCursor", new Point(15.5, 10.5));
        Check(((TextBlock)Field(window, "_analysisCursor")!).Text == "200", "200% DPI cursor maps to cached source pixel");
        Check(((TextBlock)Field(window, "_analysisWhite")!).Text == "203", "SDR white reference is separate from content peak");
        var expander = (Expander)Field(window, "_analysisWaveExpander")!; expander.IsExpanded = true;
        await Task.Delay(150);
        Check(((Image)Field(window, "_analysisWaveform")!).Source is not null, "Expanded waveform generated from cached luminance");
        ((ComboBox)Field(window, "_analysisScale")!).SelectedIndex = 2;
        await Task.Delay(150);
        Check(((TextBlock)Field(window, "_analysisWaveLabels")!).Text.Contains("1000"), "Fixed nit scale and reference markers are labeled");
        ((ToggleSwitch)Field(window, "_analysisHeatmapSwitch")!).IsOn = true;
        await Task.Delay(150);
        Check(Field(window, "_analysisHeatmap") is not null, "Explicit heatmap switch prepares display-only texture");
        using var cropAfter = (Microsoft.Graphics.Canvas.CanvasRenderTarget)Call(window, "CropDisplayToBgra")!;
        Check(cropAfter.GetPixelBytes().SequenceEqual(originalCrop) && source.GetPixelBytes().SequenceEqual(bytes), "Heatmap never changes original source or output crop");
        ((ToggleSwitch)Field(window, "_analysisHeatmapSwitch")!).IsOn = false;
        Check(Field(window, "_analysisHeatmap") is null, "Returning to original preview disposes heatmap texture");
        Call(window, "HandleCaptureKey", VirtualKey.Escape);
        Check(Field(window, "_hdrAnalysis") is null && Field(window, "_analysisHeatmap") is null && Field(window, "_analysisCancellation") is null,
            "Esc releases CPU cache, GPU heatmap, cancellation owner");
        Check(Field(window, "_state")!.ToString() == "Selected", "Closing analysis leaves selection intact");
        await (Task)Call(window, "OpenHdrAnalysisAsync")!;
        WindowType.GetProperty("SelectionRect")!.SetValue(window, new Rect(1, 1, 20, 16));
        Call(window, "RefreshToolbar");
        Check(Field(window, "_hdrAnalysis") is null, "Selection change invalidates analysis data");
        var pending = (Task)Call(window, "OpenHdrAnalysisAsync")!;
        Call(window, "CloseHdrAnalysis"); await pending;
        Check(Field(window, "_hdrAnalysis") is null && ((Border)Field(window, "HdrAnalysisPanel")!).Visibility == Visibility.Collapsed,
            "Immediate cancellation cannot resurrect panel or cache");
        await (Task)Call(window, "OpenHdrAnalysisAsync")!;
        Call(window, "CancelCapture");
        Check(Field(window, "_hdrAnalysis") is null && Field(window, "_analysisHeatmap") is null && Field(window, "_canvasOriginal") is null,
            "Capture close releases all analyzer references");
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct GpuSample
    {
        public ulong Resident, Shared, Committed, SharedCommitted;
        public uint Adapters, ResidentQueries, CommittedQueries, Failures, Enumerated, FirstFailure;
    }
    [System.Runtime.InteropServices.DllImport("GpuMemory.dll")]
    private static extern uint ReadGpuMemory(uint pid, out GpuSample sample);
    private string MemoryRow(string phase)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        uint error = ReadGpuMemory((uint)process.Id, out var gpu);
        return $"{phase},{DateTimeOffset.Now:O},{process.PrivateMemorySize64},{gpu.Resident},{gpu.Committed},{gpu.Shared},{error},{gpu.ResidentQueries},{gpu.CommittedQueries}";
    }
    private async Task BenchmarkHdrAnalysis(RegionCaptureWindow window, string report)
    {
        int width = 3840, height = 2160;
        var bytes = new byte[width * height * 8];
        for (int p = 0; p < bytes.Length; p += 8)
        {
            // Varying gray 0..10 scRGB in raw FP16 bytes, without extra full-frame arrays.
            ushort h = BitConverter.HalfToUInt16Bits((Half)((p / 8 % width) / (float)width * 10));
            for (int c = 0; c < 6; c += 2) { bytes[p + c] = (byte)h; bytes[p + c + 1] = (byte)(h >> 8); }
            bytes[p + 6] = 0; bytes[p + 7] = 0x3c;
        }
        using var source = Microsoft.Graphics.Canvas.CanvasBitmap.CreateFromBytes(Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice(),
            bytes, width, height, Windows.Graphics.DirectX.DirectXPixelFormat.R16G16B16A16Float, 96, Microsoft.Graphics.Canvas.CanvasAlphaMode.Ignore);
        Set(window, "_canvasOriginal", source); Set(window, "_isClosed", false); Set(window, "_scale", 2f);
        Set(window, "_lockedW", width / 2f); Set(window, "_lockedH", height / 2f);
        WindowType.GetProperty("SelectionRect")!.SetValue(window, new Rect(0, 0, width / 2, height / 2));
        State(window, "Selected"); AnalysisMonitors(window, (new Rect(0, 0, width, height), true, 203, 1000));
        Call(window, "RefreshToolbar");
        var log = new List<string> { "phase,timestamp,private_bytes,dedicated_resident,dedicated_committed,shared_resident,kmt_error,resident_queries,committed_queries" };
        log.Add(MemoryRow("source_ready"));
        var timing = new List<string>();
        for (int cycle = 1; cycle <= 5; cycle++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await (Task)Call(window, "OpenHdrAnalysisAsync")!;
            var analysis = (HdrLuminanceAnalysis?)Field(window, "_hdrAnalysis");
            Check(analysis is not null && analysis.SampleStep == 1 && analysis.SampleCount == width * height,
                "4K real GPU stripes retain all pixels within bounded analysis time");
            Check(Field(window, "_analysisHeatmap") is null, "Default numeric analysis has no heatmap GPU texture");
            timing.Add($"cycle={cycle}; elapsed_ms={watch.ElapsedMilliseconds}; samples={analysis!.SampleCount}; cached_bytes={analysis.Values.Length * 4}; max={analysis.Stats.Max}");
            log.Add(MemoryRow($"analysis_{cycle}"));
            Call(window, "CloseHdrAnalysis"); await Task.Delay(700);
            log.Add(MemoryRow($"closed_{cycle}"));
            Check(Field(window, "_hdrAnalysis") is null && Field(window, "_analysisHeatmap") is null, "4K close drops all analysis buffers/textures");
        }
        File.WriteAllLines(Path.Combine(report, "4k-analysis-memory.csv"), log);
        File.WriteAllLines(Path.Combine(report, "4k-analysis-time.log"), timing);
        Call(window, "CancelCapture");
    }
}
}
