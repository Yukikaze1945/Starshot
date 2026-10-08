using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Text.Json;
using Starshot.Features.Screenshot;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); _checks++; }
    private static void Pixel(Span<byte> row, int x, float r, float g, float b, float a = 1)
    {
        int p = x * HdrLuminanceAnalysis.RgbaHalfBytesPerPixel;
        BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(p, 2), BitConverter.HalfToUInt16Bits((Half)r));
        BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(p + 2, 2), BitConverter.HalfToUInt16Bits((Half)g));
        BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(p + 4, 2), BitConverter.HalfToUInt16Bits((Half)b));
        BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(p + 6, 2), BitConverter.HalfToUInt16Bits((Half)a));
    }
    private static HdrLuminanceAnalysis AnalyzeGrays(params float[] gray)
    {
        var analysis = new HdrLuminanceAnalysis(gray.Length, 1);
        byte[] row = new byte[gray.Length * 8];
        for (int x = 0; x < gray.Length; x++) Pixel(row, x, gray[x], gray[x], gray[x]);
        analysis.AppendRows(row, 0, 1, row.Length);
        analysis.Finish();
        return analysis;
    }
    private static void LuminanceAndRows()
    {
        using (var a = new HdrLuminanceAnalysis(4, 1))
        {
            int stride = 40;
            byte[] row = new byte[stride];
            Pixel(row, 0, 0, 0, 0); Pixel(row, 1, 1, 1, 1); Pixel(row, 2, 2.5f, 2.5f, 2.5f); Pixel(row, 3, 10, 10, 10);
            a.AppendRows(row, 0, 1, stride);
            var s = a.Finish();
            Check(Math.Abs(s.Min) < .001f && Math.Abs(s.Max - 800) < .1f, "Gray 0/1/2.5/10 maps to 0/80/200/800 nits");
            Check(Math.Abs(s.Average - 270) < .1 && Math.Abs(s.P99 - 800) < .1 && s.SampleCount == 4, "Summary statistics include average and nearest-rank P99");
            Check(a.TrySample(2, 0, out float cursor) && Math.Abs(cursor - 200) < .1f, "Cursor lookup returns cached source-relative sample");
            Check(!a.TrySample(-1, 0, out _) && !a.TrySample(4, 0, out _), "Cursor lookup rejects coordinates outside source bounds");
        }
        using (var a = new HdrLuminanceAnalysis(2, 2))
        {
            const int stride = 24;
            byte[] row = new byte[stride]; Pixel(row, 0, 1, 1, 1); Pixel(row, 1, 2, 2, 2);
            a.AppendRows(row, 0, 1, stride);
            row.AsSpan().Clear(); Pixel(row, 0, 3, 3, 3); Pixel(row, 1, 4, 4, 4);
            a.AppendRows(row, 1, 1, stride);
            Check(Math.Abs(a.Finish().Max - 320) < .1f, "Row chunks honor padded byte stride and source row order");
        }
        using (var a = AnalyzeGrays(1, 1))
        {
            // A negative red channel participates in the weighted sum before the final zero clamp.
            using var mixed = new HdrLuminanceAnalysis(1, 1);
            byte[] row = new byte[8]; Pixel(row, 0, -1, 2, 0); mixed.AppendRows(row, 0, 1, 8);
            Check(Math.Abs(mixed.Finish().Max - ((-0.2126f + 1.4304f) * 80f)) < .1f, "Negative scRGB channels are preserved through luminance calculation");
            row.AsSpan().Clear(); Pixel(row, 0, float.NaN, float.PositiveInfinity, float.NegativeInfinity);
            using var nonfinite = new HdrLuminanceAnalysis(1, 1);
            nonfinite.AppendRows(row, 0, 1, 8);
            Check(nonfinite.Finish().Max == 0, "NaN and infinite channel values are sanitized to zero");
        }
        using (var a = new HdrLuminanceAnalysis(100, 1))
        {
            byte[] row = new byte[100 * 8];
            for (int x = 0; x < 100; x++) Pixel(row, x, x / 80f, x / 80f, x / 80f);
            a.AppendRows(row, 0, 1, row.Length);
            Check(Math.Abs(a.Finish().P99 - 98) < .2f, "P99 uses the nearest-rank element in a 100-sample distribution");
        }
    }
    private static void ImageAndScaleChecks()
    {
        using var a = AnalyzeGrays(0, 1, 203f / 80, 5, 12.5f, 50, 125);
        var heat = a.CreateHeatmap();
        Check(heat.Width == 7 && heat.Height == 1 && heat.Pixels.Length == 28, "Heatmap returns bounded, tightly packed BGRA");
        byte[] p = heat.Pixels;
        Check(p[0] == 128 && p[1] == 0 && p[2] == 0 && p[3] == 255, "Zero nits use the navy palette stop");
        Check(p[4] == 255 && p[5] == 255 && p[6] == 0, "80 nits use the cyan palette stop");
        Check(p[8] <= 1 && p[9] == 255 && p[10] == 0, "203 nits use the green palette stop");
        Check(p[12] == 0 && p[13] == 255 && p[14] == 255, "400 nits use the yellow palette stop");
        Check(p[16] == 0 && p[17] == 128 && p[18] == 255, "1000 nits use the orange palette stop");
        Check(p[20] == 255 && p[21] == 0 && p[22] == 255, "4000 nits use the magenta palette stop");
        Check(p[24] == 255 && p[25] == 255 && p[26] == 255, "10000 nits use the white palette stop");
        var small = a.CreateHeatmap(3);
        Check(small.Width <= 3 && small.Height <= 3, "Requested heatmap dimension caps output");

        using var waveformAnalysis = AnalyzeGrays(0, 1, 203f / 80, 5);
        var waveform = waveformAnalysis.CreateWaveform(400, 4, 16);
        Check(waveform.Width == 4 && waveform.Height == 16, "Waveform uses requested dimensions");
        int bottomLeft = ((15 * waveform.Width) + 0) * 4, topRight = 3 * 4;
        Check(waveform.Pixels[bottomLeft + 3] == 255 && waveform.Pixels[topRight + 3] == 255,
            "Waveform maps source horizontal endpoints to distinct image columns and luminance vertically");
        Check(HdrLuminanceAnalysis.HeatmapColor(0) == 0xff000080 && HdrLuminanceAnalysis.HeatmapColor(80) == 0xff00ffff
            && HdrLuminanceAnalysis.HeatmapColor(203) == 0xff00ff00 && HdrLuminanceAnalysis.HeatmapColor(400) == 0xffffff00
            && HdrLuminanceAnalysis.HeatmapColor(1000) == 0xffff8000 && HdrLuminanceAnalysis.HeatmapColor(4000) == 0xffff00ff
            && HdrLuminanceAnalysis.HeatmapColor(10000) == 0xffffffff,
            "Public ARGB heatmap color API exposes each nit palette breakpoint");
        Check(HdrLuminanceAnalysis.HeatmapColor(float.NaN) == HdrLuminanceAnalysis.HeatmapColor(0)
            && HdrLuminanceAnalysis.HeatmapColor(float.PositiveInfinity) == HdrLuminanceAnalysis.HeatmapColor(10000),
            "Heatmap color API sanitizes nonfinite input");

        var snapshotOwner = AnalyzeGrays(0, 1, 2.5f, 5);
        ReadOnlyMemory<float> snapshot = snapshotOwner.Values;
        snapshotOwner.Dispose();
        var snapshotWaveform = HdrLuminanceAnalysis.CreateWaveform(snapshot, 4, 1, 400, 4, 16);
        Check(snapshotWaveform.Pixels.Any(v => v != 0), "Static waveform processes a values snapshot after its owner is disposed");

        Check(HdrLuminanceAnalysis.SelectScale(0) == 80 && HdrLuminanceAnalysis.SelectScale(80) == 80
            && HdrLuminanceAnalysis.SelectScale(81) == 203 && HdrLuminanceAnalysis.SelectScale(203) == 203
            && HdrLuminanceAnalysis.SelectScale(204) == 400 && HdrLuminanceAnalysis.SelectScale(400) == 400
            && HdrLuminanceAnalysis.SelectScale(401) == 1000 && HdrLuminanceAnalysis.SelectScale(1001) == 2000
            && HdrLuminanceAnalysis.SelectScale(2001) == 4000 && HdrLuminanceAnalysis.SelectScale(4001) == 10000,
            "Adaptive scale selects supported breakpoints");
        Check(HdrLuminanceAnalysis.SelectScale(10001) == 11000, "Scale above the presets rounds up by 1000 nits");
    }
    private static void MemoryBoundAndDispose()
    {
        const int width = 7680, height = 4320, stride = width * 8;
        using var analysis = new HdrLuminanceAnalysis(width, height);
        Check(analysis.SourceWidth == width && analysis.SourceHeight == height && analysis.SampleStep > 1
            && analysis.SampleCount <= HdrLuminanceAnalysis.MaxCachedSamples
            && analysis.Values.Length * sizeof(float) <= HdrLuminanceAnalysis.MaxCachedSamples * sizeof(float)
            && (long)analysis.SampleCount * sizeof(float) * 2 <= HdrLuminanceAnalysis.MaxAnalysisWorkingBytes,
            "8K sampling grid stays below the 16M-sample cache budget");
        byte[] row = new byte[stride];
        for (int x = 0; x < width; x++) Pixel(row, x, x / 80f, x / 80f, x / 80f);
        for (int y = 0; y < height; y++)
        {
            analysis.AppendRows(row, y, 1, stride);
            if (y == 0) row.AsSpan().Clear();
        }
        Check(analysis.Finish().SampleCount == analysis.SampleCount, "8K row ingestion finishes with the bounded grid count");
        Check(analysis.TrySample(3, 0, out float nearest) && Math.Abs(nearest - 4) < .1f,
            "Cursor coordinates map to the nearest cached grid sample");
        var heatmap = analysis.CreateHeatmap();
        Check(heatmap.Width <= 1024 && heatmap.Height <= 1024 && heatmap.Pixels.Length <= 1024 * 1024 * 4,
            "8K heatmap output is capped at 1024 by 1024");
        int valuesLength = analysis.Values.Length;
        analysis.Dispose();
        bool disposed = false;
        try { _ = analysis.Values.Length; } catch (ObjectDisposedException) { disposed = true; }
        Check(disposed && valuesLength > 0, "Dispose releases the cached grid and prevents reuse");
    }
    private static void CancellationChecks()
    {
        using var analysis = new HdrLuminanceAnalysis(1, 1);
        byte[] row = new byte[8]; Pixel(row, 0, 1, 1, 1); analysis.AppendRows(row, 0, 1, 8);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        bool finishCanceled = false;
        try { analysis.Finish(canceled.Token); } catch (OperationCanceledException) { finishCanceled = true; }
        Check(finishCanceled && Math.Abs(analysis.Finish().Max - 80) < .1f,
            "Canceled statistics can be retried without committing partial results");
        bool heatmapCanceled = false;
        try { _ = analysis.CreateHeatmap(cancellationToken: canceled.Token); } catch (OperationCanceledException) { heatmapCanceled = true; }
        Check(heatmapCanceled, "Heatmap generation observes a pre-canceled token");
        bool waveformCanceled = false;
        try { _ = HdrLuminanceAnalysis.CreateWaveform(analysis.Values, 1, 1, 100, cancellationToken: canceled.Token); }
        catch (OperationCanceledException) { waveformCanceled = true; }
        Check(waveformCanceled, "Static waveform generation observes a pre-canceled token");
    }
    private static int Benchmark()
    {
        foreach ((string label, int width, int height) in new[]
        {
            ("4K", 3840, 2160), ("8K", 7680, 4320)
        })
        {
            long allocatedBefore = GC.GetTotalAllocatedBytes(true);
            var stopwatch = Stopwatch.StartNew();
            using var analysis = new HdrLuminanceAnalysis(width, height);
            int stride = checked(width * HdrLuminanceAnalysis.RgbaHalfBytesPerPixel);
            int stripeRows = Math.Min(height, Math.Max(1, (1024 * 1024) / stride));
            byte[] stripe = new byte[checked(stripeRows * stride)];
            for (int firstRow = 0; firstRow < height; firstRow += stripeRows)
            {
                int rows = Math.Min(stripeRows, height - firstRow);
                for (int localY = 0; localY < rows; localY++)
                {
                    Span<byte> row = stripe.AsSpan(localY * stride, stride);
                    int y = firstRow + localY;
                    for (int x = 0; x < width; x++)
                    {
                        float gray = ((x * 13L + y * 29L) % 5000) / 500f;
                        Pixel(row, x, gray, gray, gray);
                    }
                }
                analysis.AppendRows(stripe.AsSpan(0, rows * stride), firstRow, rows, stride);
            }
            var stats = analysis.Finish();
            var heatmap = analysis.CreateHeatmap();
            var waveform = analysis.CreateWaveform(HdrLuminanceAnalysis.SelectScale(stats.Max));
            stopwatch.Stop();
            long allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
            long cachedBytes = (long)analysis.SampleCount * sizeof(float);
            Console.WriteLine($"{label}: {width}x{height}; {stopwatch.Elapsed.TotalSeconds:F2}s; step={analysis.SampleStep}; samples={analysis.SampleCount:N0}; cached={cachedBytes / 1048576d:F2}MiB; allocated={allocated / 1048576d:F2}MiB; stripe={stripe.Length / 1024d:F1}KiB; heatmap={heatmap.Width}x{heatmap.Height}; waveform={waveform.Width}x{waveform.Height}; max={stats.Max:F1}nit");
            GC.KeepAlive(heatmap); GC.KeepAlive(waveform);
        }
        return 0;
    }

    private sealed record LifecycleSample(string Phase, int Cycle, long PrivateBytes, long TotalMemoryBytes,
        long GcIndex, long HeapSizeBytes, long CommittedBytes, long FragmentedBytes, int Gen2Count,
        long LohSizeBytes, long LohFragmentedBytes);

    private static LifecycleSample Sample(string phase, int cycle)
    {
        using var process = Process.GetCurrentProcess();
        var info = GC.GetGCMemoryInfo();
        long loh = info.GenerationInfo.Length > 3 ? info.GenerationInfo[3].SizeAfterBytes : 0;
        long lohFragmented = info.GenerationInfo.Length > 3 ? info.GenerationInfo[3].FragmentationAfterBytes : 0;
        return new(phase, cycle, process.PrivateMemorySize64, GC.GetTotalMemory(false), info.Index,
            info.HeapSizeBytes, info.TotalCommittedBytes, info.FragmentedBytes, GC.CollectionCount(2), loh, lohFragmented);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunLifecycleCycle(int cycle, List<HdrAnalysisDiagnosticEvent> events, List<LifecycleSample> samples)
    {
        int width = 3840, height = 2160, stride = checked(width * HdrLuminanceAnalysis.RgbaHalfBytesPerPixel);
        HdrAnalysisDiagnostics.Listener = events.Add;
        var analysis = new HdrLuminanceAnalysis(width, height);
        int rowsPerStripe = Math.Max(1, 1024 * 1024 / stride);
        byte[] stripe = new byte[checked(rowsPerStripe * stride)];
        HdrAnalysisDiagnostics.Track("stripe", stripe, cycle);
        try
        {
            for (int y = 0; y < height; y += rowsPerStripe)
            {
                int rows = Math.Min(rowsPerStripe, height - y);
                for (int row = 0; row < rows; row++)
                {
                    var span = stripe.AsSpan(row * stride, stride);
                    for (int x = 0; x < width; x++) Pixel(span, x, 1, 1, 1);
                }
                analysis.AppendRows(stripe.AsSpan(0, rows * stride), y, rows, stride);
            }
            analysis.Finish();
            samples.Add(Sample("analysis.open", cycle));
            // Keep one uniform host operation duration between the open and closed samples.
            Thread.Sleep(20);
        }
        finally
        {
            analysis.Dispose();
            HdrAnalysisDiagnostics.Listener = null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunControlCycle() => Thread.Sleep(20);

    private static int Lifecycle(string reportDirectory)
    {
        Directory.CreateDirectory(reportDirectory);
        var samples = new List<LifecycleSample>();
        var events = new List<HdrAnalysisDiagnosticEvent>();
        var allWeak = new List<(string Kind, WeakReference Reference)>();
        for (int i = 0; i < 15; i++)
        {
            samples.Add(Sample("control.open", i + 1));
            RunControlCycle();
            samples.Add(Sample("control.closed", i + 1));
        }
        for (int i = 0; i < 15; i++)
        {
            RunLifecycleCycle(i + 1, events, samples);
            samples.Add(Sample("analysis.closed", i + 1));
        }
        foreach (var item in events)
            if (item.Reference is not null) allWeak.Add((item.Stage, item.Reference));
        events.Clear();
        HdrAnalysisDiagnostics.Listener = null;
        var aliveBefore = allWeak.GroupBy(x => x.Kind).ToDictionary(g => g.Key, g => g.Count(x => x.Reference.IsAlive));
        int aliveBeforeTotal = aliveBefore.Values.Sum();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        int aliveAfter = allWeak.Count(x => x.Reference.IsAlive);
        samples.Add(Sample("analysis.after_forced_gc", 15));
        var weakSummary = allWeak.GroupBy(x => x.Kind).Select(g => new
        {
            kind = g.Key, count = g.Count(), aliveBeforeForcedGc = aliveBefore.GetValueOrDefault(g.Key),
            aliveAfterForcedGc = g.Count(x => x.Reference.IsAlive)
        }).ToArray();
        File.WriteAllLines(Path.Combine(reportDirectory, "lifecycle.csv"),
            new[] { "phase,cycle,private_bytes,total_memory_bytes,last_gc_index,last_gc_heap_size_bytes,last_gc_total_committed_bytes,last_gc_fragmented_bytes,gen2_count,last_gc_loh_size_after_bytes,last_gc_loh_fragmented_after_bytes" }
                .Concat(samples.Select(s => $"{s.Phase},{s.Cycle},{s.PrivateBytes},{s.TotalMemoryBytes},{s.GcIndex},{s.HeapSizeBytes},{s.CommittedBytes},{s.FragmentedBytes},{s.Gen2Count},{s.LohSizeBytes},{s.LohFragmentedBytes}")));
        File.WriteAllText(Path.Combine(reportDirectory, "lifecycle.json"), JsonSerializer.Serialize(new
        {
            controlCycles = 15, analysisCycles = 15, width = 3840, height = 2160,
            stripeBytesMax = 1024 * 1024, weakReferenceCount = allWeak.Count,
            weakReferencesAliveBeforeForcedGc = aliveBeforeTotal, weakReferencesAliveAfterForcedGc = aliveAfter,
            weakReferencesByKind = weakSummary, samples
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Lifecycle diagnostic: 15 analysis cycles, {allWeak.Count} weak refs; alive before GC={aliveBeforeTotal}, after diagnostic GC={aliveAfter}; reports: {reportDirectory}");
        return 0;
    }
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--benchmark") return Benchmark();
            if (args.Length == 2 && args[0] == "--lifecycle") return Lifecycle(args[1]);
            LuminanceAndRows(); ImageAndScaleChecks(); MemoryBoundAndDispose(); CancellationChecks();
            Console.WriteLine($"PASS: {_checks} HDR luminance checks; pure CPU math, no GPU/UI/screen I/O.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
