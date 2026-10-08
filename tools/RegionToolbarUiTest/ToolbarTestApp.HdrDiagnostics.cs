using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starshot.Features.Screenshot;
using Windows.Foundation;

namespace Starshot.ToolbarValidation;

public sealed partial class ToolbarTestApp
{
    private readonly object _diagnosticLock = new();
    private readonly List<(string Kind, WeakReference Reference)> _weakObjects = new();
    private readonly List<object> _performanceRows = new();
    private readonly Dictionary<string, List<double>> _performance = new();
    private readonly HashSet<int> _activeOperations = new();
    private readonly List<string> _memoryRows = new();
    private string _probePhase = "setup";
    private int _diagnosticCycle;
    private Action<HdrAnalysisDiagnosticEvent>? _stageHook;
    private readonly Dictionary<int, int> _readbackGcStarts = new();

    private void ObserveHdr(HdrAnalysisDiagnosticEvent e)
    {
        lock (_diagnosticLock)
        {
            if (e.Reference is not null) _weakObjects.Add((e.Stage[7..], e.Reference));
            if (e.Stage == "readback.begin") _readbackGcStarts[e.Operation] = GC.CollectionCount(2);
            if (e.IsTask)
            {
                if (e.Stage.EndsWith(".begin")) _activeOperations.Add(e.Operation);
                else if (e.Stage.EndsWith(".end")) _activeOperations.Remove(e.Operation);
            }
            if (e.Stage.EndsWith(".end"))
            {
                string stage = e.Stage[..^4];
                RecordPerformanceUnsafe(stage, e.Milliseconds);
                _performanceRows.Add(new { cycle = _diagnosticCycle, phase = _probePhase, stage,
                    generation = e.Generation, timestamp = e.Timestamp, milliseconds = e.Milliseconds,
                    gen2_before = stage == "readback" ? _readbackGcStarts[e.Operation] : -1,
                    gen2_after = stage == "readback" ? GC.CollectionCount(2) : -1 });
                if (stage == "readback") _readbackGcStarts.Remove(e.Operation);
            }
        }
        _stageHook?.Invoke(e);
    }
    private void RecordPerformanceUnsafe(string stage, double milliseconds)
    {
        if (!_performance.TryGetValue(stage, out var values)) _performance[stage] = values = new();
        values.Add(milliseconds);
    }
    private int ActiveOperations { get { lock (_diagnosticLock) return _activeOperations.Count; } }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsTaskRunning(WeakReference reference) => reference.Target is Task task && !task.IsCompleted;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Dictionary<string, int> AliveObjects()
    {
        lock (_diagnosticLock)
            return _weakObjects.GroupBy(o => o.Kind).ToDictionary(g => g.Key, g => g.Count(o => o.Reference.IsAlive));
    }
    private void DiagnosticMemory(string phase, RegionCaptureWindow window)
    {
        _probePhase = "memory_sampling"; // KMT/process enumeration is test overhead, kept separate from analysis.
        using var process = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        var loh = gc.GenerationInfo.Length > 3 ? gc.GenerationInfo[3] : default;
        var alive = AliveObjects();
        uint error = ReadGpuMemory((uint)process.Id, out var gpu);
        int running;
        lock (_diagnosticLock) running = _weakObjects.Count(o => o.Kind == "task" && IsTaskRunning(o.Reference));
        int Count(string kind) => alive.GetValueOrDefault(kind);
        _memoryRows.Add(string.Join(',', phase, DateTimeOffset.Now.ToString("O"), process.PrivateMemorySize64, process.WorkingSet64,
            GC.GetTotalMemory(false), gc.Index, gc.Generation, gc.HeapSizeBytes, gc.TotalCommittedBytes, gc.FragmentedBytes,
            loh.SizeBeforeBytes, loh.SizeAfterBytes, loh.FragmentationAfterBytes, GC.CollectionCount(2), ActiveOperations, running,
            Count("luminance"), Count("sort"), Count("stripe"), Count("model"), Count("cts"), Count("panel"),
            Count("waveform_density"), Count("waveform_pixels"), Count("waveform_bitmap"), Count("heatmap_pixels"), Count("heatmap_texture"),
            Field(window, "_hdrAnalysis") is not null, Field(window, "_analysisCancellation") is not null,
            ((Task)Field(window, "_analysisPending")!).IsCompleted, gpu.Resident, gpu.Committed, gpu.Shared, error));
    }
    private async Task DrainHdrWorkers(RegionCaptureWindow window)
    {
        await ((Task)Field(window, "_analysisPending")!);
        var watch = Stopwatch.StartNew();
        while (ActiveOperations != 0)
        {
            if (watch.Elapsed.TotalSeconds > 5) throw new InvalidOperationException("HDR task failed to stop");
            await Task.Delay(2);
        }
        await Task.Delay(10); // Drain queued dispatcher continuations without any forced collection.
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task DiagnosticCycle(RegionCaptureWindow window, int cycle)
    {
        _diagnosticCycle = cycle; _probePhase = "analysis";
        var task = (Task)Call(window, "OpenHdrAnalysisAsync")!;
        HdrAnalysisDiagnostics.Track("task", task);
        await task;
        Check(Field(window, "_hdrAnalysis") is not null, "4K analysis completes");
        DiagnosticMemory($"open_{cycle}", window);
        _probePhase = "close";
        Call(window, "CloseHdrAnalysis");
        await DrainHdrWorkers(window);
        Check(Field(window, "_hdrAnalysis") is null && Field(window, "_analysisHeatmap") is null && Field(window, "_analysisCancellation") is null,
            "Closed analysis has no owned cache/texture/CTS");
        DiagnosticMemory($"closed_{cycle}", window);
    }

    private async Task ValidateHdrLifecycle(RegionCaptureWindow window, string report)
    {
        _memoryRows.Add("phase,timestamp,private_bytes,working_set_bytes,gc_total_memory,gc_snapshot_index,gc_snapshot_generation,heap_snapshot_bytes,gc_committed_snapshot_bytes,fragmented_snapshot_bytes,loh_before_snapshot_bytes,loh_after_snapshot_bytes,loh_fragmented_after_snapshot_bytes,gen2_collections,active_operations,running_tasks_weak,alive_luminance,alive_sort,alive_stripe,alive_model,alive_cts,alive_panel,alive_waveform_density,alive_waveform_pixels,alive_waveform_bitmap,alive_heatmap_pixels,alive_heatmap_texture,field_cache,field_cts,pending_complete,dedicated_resident,dedicated_committed,shared_resident,kmt_error");
        HdrAnalysisDiagnostics.Listener = ObserveHdr;
        using var gcEvents = new HdrGcEventListener();
        using var probeCancellation = new CancellationTokenSource();
        var dispatcher = window.DispatcherQueue;
        var timer = dispatcher.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(5); timer.IsRepeating = true;
        long heartbeat = Stopwatch.GetTimestamp(); string heartbeatPhase = _probePhase;
        timer.Tick += (_, _) =>
        {
            var now = Stopwatch.GetTimestamp(); var gap = Stopwatch.GetElapsedTime(heartbeat, now).TotalMilliseconds; heartbeat = now;
            string phase = _probePhase;
            if (phase == heartbeatPhase)
                lock (_diagnosticLock) RecordPerformanceUnsafe("heartbeat_gap:" + phase, gap);
            heartbeatPhase = phase;
        };
        timer.Start();
        var producer = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(5, probeCancellation.Token);
                    long posted = Stopwatch.GetTimestamp(); string phase = _probePhase;
                    dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, () =>
                    {
                        lock (_diagnosticLock)
                        {
                            double delay = Stopwatch.GetElapsedTime(posted).TotalMilliseconds;
                            RecordPerformanceUnsafe("dispatcher_delay:" + phase, delay);
                            _performanceRows.Add(new { cycle = _diagnosticCycle, phase, stage = "dispatcher_probe", posted,
                                timestamp = Stopwatch.GetTimestamp(), callbackPhase = _probePhase, milliseconds = delay });
                        }
                    });
                }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            // Warm UI runtime with no analysis buffers, then same-process host-only control.
            _probePhase = "host_control";
            await Task.Delay(150);
            DiagnosticMemory("host_start", window);
            for (int i = 1; i <= 15; i++)
            { _probePhase = "host_control"; await Task.Delay(50); DiagnosticMemory($"host_{i}", window); }
            _probePhase = "fixture_setup";
            using var source = CreateHdrFixture(window);
            Call(window, "RefreshToolbar"); await Task.Delay(50);
            DiagnosticMemory("source_ready", window);
            for (int cycle = 1; cycle <= 15; cycle++) await DiagnosticCycle(window, cycle);
            await DrainHdrWorkers(window);
            DiagnosticMemory("after_15_cycles", window);
            await ValidateHdrCancelPaths(window, report);
            Call(window, "CancelCapture"); await DrainHdrWorkers(window);
            DiagnosticMemory("after_capture_close", window);
            DiagnosticMemory("before_forced_gc", window);
            // The only explicit forced collection is here, in this isolated diagnostic executable.
            _probePhase = "diagnostic_gc";
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            DiagnosticMemory("after_forced_gc", window);
            var afterGc = AliveObjects();
            Check(afterGc.GetValueOrDefault("luminance") == 0 && afterGc.GetValueOrDefault("sort") == 0 && afterGc.GetValueOrDefault("stripe") == 0,
                "All completed analysis buffers are unreachable after diagnostic GC");
            Check(ActiveOperations == 0, "No unfinished analysis workers");
            source.Dispose(); await Task.Delay(100);
            DiagnosticMemory("after_source_dispose", window);
            File.WriteAllText(Path.Combine(report, "weak-references-after-gc.json"), JsonSerializer.Serialize(afterGc, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            timer.Stop(); probeCancellation.Cancel(); await producer;
            HdrAnalysisDiagnostics.Listener = null;
            File.WriteAllText(Path.Combine(report, "gc-events.json"), JsonSerializer.Serialize(gcEvents.Snapshot(), new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllLines(Path.Combine(report, "lifecycle-memory.csv"), _memoryRows);
            lock (_diagnosticLock)
            {
                File.WriteAllText(Path.Combine(report, "performance-events.json"), JsonSerializer.Serialize(_performanceRows));
                File.WriteAllText(Path.Combine(report, "performance-summary.json"), JsonSerializer.Serialize(_performance.ToDictionary(p => p.Key, p => Summary(p.Value)), new JsonSerializerOptions { WriteIndented = true }));
            }
            File.WriteAllText(Path.Combine(report, "runtime.json"), JsonSerializer.Serialize(new { runtime = Environment.Version.ToString(),
                serverGc = System.Runtime.GCSettings.IsServerGC, processors = Environment.ProcessorCount, os = Environment.OSVersion.ToString(),
                note = "Heap/LOH/committed/fragmentation columns reflect the most recent GC (index/generation recorded), not live heap telemetry. WeakReference counters never own buffers. No forced GC until all 15 cycles finish." }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    private static object Summary(List<double> values)
    {
        var sorted = values.Order().ToArray();
        double P(double percentile) => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];
        return new { count = sorted.Length, sum_ms = values.Sum(), p50_ms = P(.5), p95_ms = P(.95), p99_ms = P(.99), max_ms = sorted[^1] };
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Microsoft.Graphics.Canvas.CanvasBitmap CreateHdrFixture(RegionCaptureWindow window)
    {
        int width = 3840, height = 2160;
        var bytes = new byte[width * height * 8];
        for (int p = 0; p < bytes.Length; p += 8)
        {
            ushort h = BitConverter.HalfToUInt16Bits((Half)((p / 8 % width) / (float)width * 10));
            for (int c = 0; c < 6; c += 2) { bytes[p + c] = (byte)h; bytes[p + c + 1] = (byte)(h >> 8); }
            bytes[p + 7] = 0x3c;
        }
        var source = Microsoft.Graphics.Canvas.CanvasBitmap.CreateFromBytes(Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice(), bytes,
            width, height, Windows.Graphics.DirectX.DirectXPixelFormat.R16G16B16A16Float, 96, Microsoft.Graphics.Canvas.CanvasAlphaMode.Ignore);
        Set(window, "_canvasOriginal", source); Set(window, "_isClosed", false); Set(window, "_scale", 2f);
        Set(window, "_lockedW", width / 2f); Set(window, "_lockedH", height / 2f);
        WindowType.GetProperty("SelectionRect")!.SetValue(window, new Rect(0, 0, width / 2, height / 2));
        State(window, "Selected"); AnalysisMonitors(window, (new Rect(0, 0, width, height), true, 203, 1000));
        return source; // The full source CPU byte[] is not held by the test after fixture creation.
    }
    private async Task ValidateHdrCancelPaths(RegionCaptureWindow window, string report)
    {
        var cases = new List<object>();
        for (int i = 0; i < 3; i++)
        {
            _probePhase = "cancel";
            var job = (Task)Call(window, "OpenHdrAnalysisAsync")!;
            await Task.Delay(i == 0 ? 0 : 30);
            var watch = Stopwatch.StartNew();
            Call(window, "CloseHdrAnalysis"); await job; await DrainHdrWorkers(window);
            cases.Add(new { scenario = "cancel_" + i, stop_ms = watch.Elapsed.TotalMilliseconds, active = ActiveOperations });
            Check(Field(window, "_hdrAnalysis") is null, "Cancel cannot publish stale cache");
        }
        _probePhase = "rapid_reopen";
        var first = (Task)Call(window, "OpenHdrAnalysisAsync")!;
        await Task.Delay(10);
        var second = (Task)Call(window, "OpenHdrAnalysisAsync")!;
        await Task.WhenAll(first, second); await DrainHdrWorkers(window);
        Check(Field(window, "_hdrAnalysis") is not null, "Rapid reopen only publishes current generation");
        Call(window, "CloseHdrAnalysis"); await DrainHdrWorkers(window);
        _probePhase = "cancel_sort";
        var stopped = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        int requested = 0;
        _stageHook = e =>
        {
            if (e.Stage == "p99_sort.begin" && Interlocked.Exchange(ref requested, 1) == 0)
                window.DispatcherQueue.TryEnqueue(() =>
                {
                    long request = Stopwatch.GetTimestamp();
                    Call(window, "CloseHdrAnalysis"); stopped.TrySetResult(request);
                });
        };
        try
        {
            var sorting = (Task)Call(window, "OpenHdrAnalysisAsync")!;
            long request = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await sorting; await DrainHdrWorkers(window);
            cases.Add(new { scenario = "cancel_during_sort", stop_ms = Stopwatch.GetElapsedTime(request).TotalMilliseconds, active = ActiveOperations });
            Check(Field(window, "_hdrAnalysis") is null, "Sort cancellation cannot publish stale cache");
        }
        finally { _stageHook = null; }
        _probePhase = "selection_change";
        var selectionJob = (Task)Call(window, "OpenHdrAnalysisAsync")!;
        await Task.Delay(15);
        WindowType.GetProperty("SelectionRect")!.SetValue(window, new Rect(30, 30, 1000, 500));
        Call(window, "RefreshToolbar");
        await selectionJob; await DrainHdrWorkers(window);
        Check(Field(window, "_hdrAnalysis") is null && Field(window, "_analysisCancellation") is null, "Selection change cancels pending analysis");
        WindowType.GetProperty("SelectionRect")!.SetValue(window, new Rect(0, 0, 1920, 1080));
        Call(window, "RefreshToolbar");
        _probePhase = "readback_exception";
        var realSource = Field(window, "_canvasOriginal");
        using (var invalidSource = CreateHdrFixture(window))
        {
            var failing = (Task)Call(window, "OpenHdrAnalysisAsync")!;
            invalidSource.Dispose();
            await failing; await DrainHdrWorkers(window);
            Check(Field(window, "_hdrAnalysis") is null && ((TextBlock)Field(window, "_analysisStatus")!).Text.StartsWith("分析未完成"),
                "Native readback exception is reported and cache disposed");
            Call(window, "CloseHdrAnalysis"); await DrainHdrWorkers(window);
        }
        Set(window, "_canvasOriginal", realSource);
        _probePhase = "optional_views";
        await (Task)Call(window, "OpenHdrAnalysisAsync")!;
        ((Expander)Field(window, "_analysisWaveExpander")!).IsExpanded = true;
        ((ToggleSwitch)Field(window, "_analysisHeatmapSwitch")!).IsOn = true;
        await DrainHdrWorkers(window);
        Check(Field(window, "_analysisHeatmap") is not null && ((Image)Field(window, "_analysisWaveform")!).Source is not null,
            "Waveform and heatmap jobs complete");
        ((ToggleSwitch)Field(window, "_analysisHeatmapSwitch")!).IsOn = false;
        Check(Field(window, "_analysisHeatmap") is null, "Heatmap toggle disposes texture");
        Call(window, "CloseHdrAnalysis"); await DrainHdrWorkers(window);
        _probePhase = "close_optional_workers";
        await (Task)Call(window, "OpenHdrAnalysisAsync")!;
        ((Expander)Field(window, "_analysisWaveExpander")!).IsExpanded = true;
        ((ToggleSwitch)Field(window, "_analysisHeatmapSwitch")!).IsOn = true;
        Call(window, "CloseHdrAnalysis"); await DrainHdrWorkers(window);
        Check(Field(window, "_hdrAnalysis") is null && Field(window, "_analysisHeatmap") is null, "Closing optional jobs cannot republish textures");
        _probePhase = "capture_close_pending";
        var closing = (Task)Call(window, "OpenHdrAnalysisAsync")!;
        await Task.Delay(15); Call(window, "CancelCapture");
        await closing; await DrainHdrWorkers(window);
        Check(Field(window, "_hdrAnalysis") is null && Field(window, "_analysisCancellation") is null && (bool)Field(window, "_isClosed")!,
            "Capture close cancels analysis and clears buffers");
        File.WriteAllText(Path.Combine(report, "cancellation.json"), JsonSerializer.Serialize(cases, new JsonSerializerOptions { WriteIndented = true }));
    }
}
