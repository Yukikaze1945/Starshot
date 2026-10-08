using System;
using System.Buffers.Binary;
using System.Linq;
using System.Threading;

namespace Starshot.Features.Screenshot;

/// <summary>Immutable summary of the sampled source luminance, in nits.</summary>
public readonly record struct HdrLuminanceStats(float Min, float Max, double Average, float P99, int SampleCount);

/// <summary>A tightly packed, top-down BGRA analysis image.</summary>
public sealed record HdrBgraImage(int Width, int Height, byte[] Pixels);

/// <summary>
/// Bounded CPU analysis of an FP16 RGBA scRGB readback. Input bytes are consumed by row and never retained.
/// scRGB linear RGB is converted to nits using max(0, luma * 80).
/// </summary>
public sealed class HdrLuminanceAnalysis : IDisposable
{
    public const int MaxCachedSamples = 16_000_000;
    public const int MaxImageDimension = 1024;
    public const int RgbaHalfBytesPerPixel = 8;
    // Values plus an exact-percentile working copy; rendered outputs have separate 4 MiB limits.
    public const long MaxAnalysisWorkingBytes = (long)MaxCachedSamples * sizeof(float) * 2;
    private static readonly float[] ScalePresets = [80, 203, 400, 1000, 2000, 4000, 10000];
    private static readonly float[] PaletteNits = [0, 80, 203, 400, 1000, 4000, 10000];
    // Colors are BGRA: navy, cyan, green, yellow, orange, magenta, white.
    private static readonly byte[,] Palette =
    {
        { 128, 0, 0, 255 }, { 255, 255, 0, 255 }, { 0, 255, 0, 255 },
        { 0, 255, 255, 255 }, { 0, 128, 255, 255 }, { 255, 0, 255, 255 }, { 255, 255, 255, 255 }
    };

    private float[]? _values;
    private int _nextRow;
    private HdrLuminanceStats? _stats;
    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public int SampleStep { get; }
    public int Width { get; }
    public int Height { get; }
    public int SampleCount => checked(Width * Height);
    public bool IsFinished => _stats.HasValue;
    public HdrLuminanceStats Stats => _stats ?? throw new InvalidOperationException("Call Finish after all source rows have been ingested.");
    public ReadOnlyMemory<float> Values => _values ?? throw new ObjectDisposedException(nameof(HdrLuminanceAnalysis));

    public HdrLuminanceAnalysis(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        long pixels = checked((long)sourceWidth * sourceHeight);
        SampleStep = pixels <= MaxCachedSamples ? 1 : Math.Max(1, (int)Math.Ceiling(Math.Sqrt(pixels / (double)MaxCachedSamples)));
        Width = DivideRoundUp(sourceWidth, SampleStep);
        Height = DivideRoundUp(sourceHeight, SampleStep);
        while ((long)Width * Height > MaxCachedSamples)
        {
            // Correct rounding at the cap boundary without risking an oversized allocation.
            SampleStep++;
            Width = DivideRoundUp(sourceWidth, SampleStep);
            Height = DivideRoundUp(sourceHeight, SampleStep);
        }
        _values = new float[checked(Width * Height)];
        HdrAnalysisDiagnostics.Track("luminance", _values);
    }

    /// <summary>Ingests consecutive top-down rows from an FP16 RGBA buffer using its raw byte stride.</summary>
    public void AppendRows(ReadOnlySpan<byte> fp16Rgba, int firstRow, int rowCount, int rowStrideBytes)
    {
        using var timing = HdrAnalysisDiagnostics.Measure("append_cpu");
        ThrowIfDisposed();
        if (_stats.HasValue) throw new InvalidOperationException("Analysis is already finished.");
        if (firstRow != _nextRow || rowCount <= 0 || firstRow < 0 || firstRow + rowCount > SourceHeight)
            throw new ArgumentOutOfRangeException(nameof(firstRow), "Rows must be appended once, in source order.");
        int rowBytes = checked(SourceWidth * RgbaHalfBytesPerPixel);
        if (rowStrideBytes < rowBytes || fp16Rgba.Length < checked(rowCount * rowStrideBytes))
            throw new ArgumentOutOfRangeException(nameof(rowStrideBytes), "The input span must contain every row at the supplied stride.");

        float[] values = _values!;
        for (int localY = 0; localY < rowCount; localY++)
        {
            int sourceY = firstRow + localY;
            if (sourceY % SampleStep != 0) continue;
            int gridY = sourceY / SampleStep;
            int rowOffset = localY * rowStrideBytes;
            for (int gridX = 0, sourceX = 0; sourceX < SourceWidth; gridX++, sourceX += SampleStep)
            {
                int pixel = rowOffset + sourceX * RgbaHalfBytesPerPixel;
                float r = ReadHalf(fp16Rgba, pixel), g = ReadHalf(fp16Rgba, pixel + 2), b = ReadHalf(fp16Rgba, pixel + 4);
                if (!float.IsFinite(r)) r = 0;
                if (!float.IsFinite(g)) g = 0;
                if (!float.IsFinite(b)) b = 0;
                float luma = (0.2126f * r + 0.7152f * g + 0.0722f * b) * 80f;
                values[gridY * Width + gridX] = float.IsFinite(luma) ? Math.Max(0, luma) : 0;
            }
        }
        _nextRow += rowCount;
    }

    /// <summary>Computes min, max, mean and exact nearest-rank P99 after all rows have arrived.</summary>
    public HdrLuminanceStats Finish(CancellationToken cancellationToken = default)
    {
        using var timing = HdrAnalysisDiagnostics.Measure("statistics_cpu");
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_nextRow != SourceHeight) throw new InvalidOperationException($"Expected {SourceHeight} rows; received {_nextRow}.");
        if (_stats.HasValue) return _stats.Value;
        float[] values = _values!;
        float min = float.PositiveInfinity, max = 0;
        double sum = 0;
        for (int i = 0; i < values.Length; i++)
        {
            if ((i & 0x3fff) == 0) cancellationToken.ThrowIfCancellationRequested();
            float value = values[i]; min = Math.Min(min, value); max = Math.Max(max, value); sum += value;
        }
        float[] ordered;
        using (HdrAnalysisDiagnostics.Measure("p99_clone")) ordered = (float[])values.Clone();
        HdrAnalysisDiagnostics.Track("sort", ordered);
        // Array.Sort is not cancellable; token is checked immediately before and after this bounded sort.
        cancellationToken.ThrowIfCancellationRequested();
        using (HdrAnalysisDiagnostics.Measure("p99_sort")) Array.Sort(ordered);
        cancellationToken.ThrowIfCancellationRequested();
        int rank = Math.Max(1, (int)Math.Ceiling(ordered.Length * 0.99d));
        _stats = new HdrLuminanceStats(min, max, sum / values.Length, ordered[rank - 1], values.Length);
        return _stats.Value;
    }

    /// <summary>Maps source-relative cursor coordinates to the cached nearest sample.</summary>
    public bool TrySample(int sourceX, int sourceY, out float nits)
    {
        ThrowIfDisposed();
        if ((uint)sourceX >= (uint)SourceWidth || (uint)sourceY >= (uint)SourceHeight)
        { nits = 0; return false; }
        int x = Math.Min(Width - 1, (int)Math.Round(sourceX / (double)SampleStep));
        int y = Math.Min(Height - 1, (int)Math.Round(sourceY / (double)SampleStep));
        nits = _values![y * Width + x];
        return true;
    }

    /// <summary>Creates a palette heatmap, preserving aspect ratio and limiting either dimension to 1024.</summary>
    public HdrBgraImage CreateHeatmap(int maxDimension = MaxImageDimension, CancellationToken cancellationToken = default)
    {
        using var timing = HdrAnalysisDiagnostics.Measure("heatmap_cpu");
        ThrowIfDisposed();
        float[] values = _values!; // An in-flight optional render may finish after the owner closes.
        cancellationToken.ThrowIfCancellationRequested();
        if (maxDimension <= 0) throw new ArgumentOutOfRangeException(nameof(maxDimension));
        float down = Math.Max(1f, Math.Max(Width, Height) / (float)Math.Min(maxDimension, MaxImageDimension));
        int outWidth = Math.Max(1, (int)Math.Ceiling(Width / down)), outHeight = Math.Max(1, (int)Math.Ceiling(Height / down));
        byte[] pixels = new byte[checked(outWidth * outHeight * 4)];
        HdrAnalysisDiagnostics.Track("heatmap_pixels", pixels);
        for (int y = 0; y < outHeight; y++)
        for (int x = 0; x < outWidth; x++)
        {
            if (x == 0) cancellationToken.ThrowIfCancellationRequested();
            int sx = Math.Min(Width - 1, (int)(x * down)), sy = Math.Min(Height - 1, (int)(y * down));
            WritePalette(values[sy * Width + sx], pixels, (y * outWidth + x) * 4);
        }
        return new(outWidth, outHeight, pixels);
    }

    /// <summary>Creates a horizontal-position versus luminance density plot for the selected linear nit scale.</summary>
    public HdrBgraImage CreateWaveform(float selectedScaleNits, int width = 256, int height = 128,
        CancellationToken cancellationToken = default)
        => CreateWaveform(Values, Width, Height, selectedScaleNits, width, height, cancellationToken);

    /// <summary>Creates a waveform from a captured grid snapshot, independent of the analysis object's lifetime.</summary>
    public static HdrBgraImage CreateWaveform(ReadOnlyMemory<float> values, int sampleWidth, int sampleHeight,
        float selectedScaleNits, int width = 256, int height = 128, CancellationToken cancellationToken = default)
    {
        using var timing = HdrAnalysisDiagnostics.Measure("waveform_cpu");
        if (sampleWidth <= 0 || sampleHeight <= 0 || values.Length < checked(sampleWidth * sampleHeight))
            throw new ArgumentOutOfRangeException(nameof(values));
        if (!float.IsFinite(selectedScaleNits) || selectedScaleNits <= 0) throw new ArgumentOutOfRangeException(nameof(selectedScaleNits));
        if (width <= 0 || height <= 0 || width > MaxImageDimension || height > MaxImageDimension)
            throw new ArgumentOutOfRangeException(nameof(width));
        cancellationToken.ThrowIfCancellationRequested();
        int[] density = new int[checked(width * height)];
        HdrAnalysisDiagnostics.Track("waveform_density", density);
        ReadOnlySpan<float> samples = values.Span;
        for (int gy = 0; gy < sampleHeight; gy++)
        {
        cancellationToken.ThrowIfCancellationRequested();
        for (int gx = 0; gx < sampleWidth; gx++)
        {
            int x = sampleWidth == 1 ? 0 : (int)Math.Round(gx * (width - 1d) / (sampleWidth - 1));
            float value = samples[gy * sampleWidth + gx];
            float normalized = float.IsNaN(value) ? 0 : Math.Clamp(value / selectedScaleNits, 0, 1);
            int y = height - 1 - (int)Math.Round(normalized * (height - 1));
            int index = y * width + x;
            if (density[index] < int.MaxValue) density[index]++;
        }
        }
        int peak = 0;
        for (int i = 0; i < density.Length; i++)
        { if ((i & 0x3fff) == 0) cancellationToken.ThrowIfCancellationRequested(); peak = Math.Max(peak, density[i]); }
        byte[] pixels = new byte[checked(width * height * 4)];
        HdrAnalysisDiagnostics.Track("waveform_pixels", pixels);
        if (peak == 0) return new(width, height, pixels);
        for (int i = 0; i < density.Length; i++)
        {
            if ((i & 0x3fff) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (density[i] == 0) continue;
            byte value = (byte)Math.Clamp((int)Math.Round(255 * Math.Sqrt(density[i] / (double)peak)), 1, 255);
            int p = i * 4;
            pixels[p] = value; pixels[p + 1] = value; pixels[p + 2] = value; pixels[p + 3] = 255;
        }
        return new(width, height, pixels);
    }

    /// <summary>Selects the first supported scale at or above the source maximum.</summary>
    public static float SelectScale(float maxNits)
    {
        if (!float.IsFinite(maxNits) || maxNits < 0) maxNits = 0;
        foreach (float scale in ScalePresets) if (maxNits <= scale) return scale;
        return MathF.Ceiling(maxNits / 1000f) * 1000f;
    }

    /// <summary>Returns an opaque ARGB color interpolated across the fixed nit-based heatmap palette.</summary>
    public static uint HeatmapColor(float nits)
    {
        if (float.IsNaN(nits) || nits < 0) nits = 0;
        int upper = -1;
        for (int i = 0; i < PaletteNits.Length; i++)
            if (PaletteNits[i] >= nits) { upper = i; break; }
        if (upper < 0) upper = PaletteNits.Length - 1;
        if (upper == 0) return PackArgb(0);
        int lower = upper - 1;
        float t = Math.Clamp((nits - PaletteNits[lower]) / (PaletteNits[upper] - PaletteNits[lower]), 0, 1);
        int b = Lerp(Palette[lower, 0], Palette[upper, 0], t);
        int g = Lerp(Palette[lower, 1], Palette[upper, 1], t);
        int r = Lerp(Palette[lower, 2], Palette[upper, 2], t);
        return 0xff000000u | (uint)(r << 16 | g << 8 | b);
    }

    private static int DivideRoundUp(int value, int divisor) => (int)(((long)value + divisor - 1) / divisor);
    private static float ReadHalf(ReadOnlySpan<byte> input, int offset) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(input.Slice(offset, 2)));
    private static void WritePalette(float nits, byte[] pixels, int offset)
    {
        uint argb = HeatmapColor(nits);
        pixels[offset] = (byte)argb; pixels[offset + 1] = (byte)(argb >> 8);
        pixels[offset + 2] = (byte)(argb >> 16); pixels[offset + 3] = (byte)(argb >> 24);
    }
    private static int Lerp(byte a, byte b, float t) => (int)Math.Round(a + (b - a) * t);
    private static uint PackArgb(int index) => 0xff000000u | (uint)(Palette[index, 2] << 16 | Palette[index, 1] << 8 | Palette[index, 0]);
    private void ThrowIfDisposed() { if (_values is null) throw new ObjectDisposedException(nameof(HdrLuminanceAnalysis)); }
    public void Dispose() { _values = null; _stats = null; _nextRow = 0; }
}
