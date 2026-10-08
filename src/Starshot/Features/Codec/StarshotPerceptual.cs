using System;
using System.Runtime.InteropServices;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using ComputeSharp.D2D1.Interop;
using ComputeSharp.D2D1.WinUI;
using Windows.Graphics.DirectX;

namespace Starshot.Features.Codec;

/// <summary>
/// Ordinary SDR file exports only. Input is Windows linear BT.709 scRGB (1 = 80 nits).
/// Never use this transform to build an Ultra HDR base or a gain map.
/// </summary>
public static class StarshotPerceptual
{
    public const float IdentityLimit = 0.8f;
    public const float PaperWhite = 0.94f;
    public const float WhiteSlope = 0.2f;

    public readonly record struct Statistics(float Percentile999Nits, float PeakNits, int SampleCount);

    // PQ coefficients packed once per export, not a LUT or a retained GPU allocation.
    public readonly record struct Parameters(float WhiteNits, float KneePq, float WhitePq,
        float PaperPq, float TailShape)
    {
        public static Parameters Create(float sdrWhiteNits, Statistics statistics, float displayPeakNits = 0)
        {
            float white = float.IsFinite(sdrWhiteNits) && sdrWhiteNits > 0
                ? Math.Clamp(sdrWhiteNits, 1, 10000) : 80;
            float typical = Math.Max(white, FinitePositive(statistics.Percentile999Nits));
            float peak = Math.Max(typical, FinitePositive(statistics.PeakNits));
            // A monitor's capability informs the tail, never desktop exposure. Both its
            // influence and an isolated bright pixel's influence are deliberately bounded.
            float displayRatio = Math.Clamp(FinitePositive(displayPeakNits) / typical, 1, 4);
            float peakRatio = Math.Clamp(peak / typical, 1, 4);
            float reference = typical * MathF.Sqrt(displayRatio) * MathF.Pow(peakRatio, 0.125f);
            float shape = Math.Clamp(MathF.Log2(reference / white) / 2, 0.75f, 3);
            return new(white, Pq(white * IdentityLimit), Pq(white), Pq(white * PaperWhite), shape);
        }
    }

    private static float FinitePositive(float value) => float.IsFinite(value) ? Math.Max(0, value) : 0;

    public static float Pq(float nits)
    {
        float p = MathF.Pow(Math.Max(0, nits) / 10000, 0.1593017578125f);
        return MathF.Pow((0.8359375f + 18.8515625f * p) / (1 + 18.6875f * p), 78.84375f);
    }

    /// <summary>
    /// Exact peak from per-tile GPU maxima, approximate P99.9 from three stratified
    /// samples per tile. At 4K: 240x135 RGBA32F readback (506 KiB), not a full image.
    /// Signed RGB is retained when computing luminance; only negative luminance is zeroed.
    /// </summary>
    public static Statistics Measure(CanvasBitmap source)
    {
        // CanvasBitmap also implements IDirect3DSurface. A 96-DPI view of the same
        // texture prevents Win2D inserting a resampling DPI effect. No pixel copy.
        using var pixelView = CreatePixelView(source);
        CanvasBitmap input = pixelView ?? source;
        int width = checked((int)source.SizeInPixels.Width);
        int height = checked((int)source.SizeInPixels.Height);
        int block = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / 256.0));
        int columns = (width + block - 1) / block;
        int rows = (height + block - 1) / block;
        using var summary = new CanvasRenderTarget(source.Device, columns, rows, 96,
            DirectXPixelFormat.R32G32B32A32Float, CanvasAlphaMode.Ignore);
        using var reduction = new PixelShaderEffect<PerceptualStatisticsShader>
        {
            BufferPrecision = CanvasBufferPrecision.Precision32Float,
            ConstantBuffer = new(width, height, block),
            // Describe the reduction's real bounds to D2D. Without this transform
            // it can allocate a full-resolution FP32 intermediate for a tiny summary.
            TransformMapper = D2D1DrawTransformMapper<PerceptualStatisticsShader>.Transform(
                Matrix3x2.CreateScale(1f / block)),
        };
        reduction.Sources[0] = input;
        using (var ds = summary.CreateDrawingSession())
        {
            ds.Units = CanvasUnits.Pixels;
            ds.Blend = CanvasBlend.Copy;
            ds.DrawImage(reduction);
        }
        ReadOnlySpan<float> pixels = MemoryMarshal.Cast<byte, float>(summary.GetPixelBytes());
        float[] samples = new float[columns * rows * 3];
        float peak = 0;
        int count = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            samples[count++] = pixels[i];
            peak = Math.Max(peak, pixels[i + 1]);
            samples[count++] = pixels[i + 2];
            samples[count++] = pixels[i + 3];
        }
        Array.Sort(samples);
        return new(samples[Math.Max(0, (int)Math.Ceiling(count * 0.999) - 1)] * 80,
            peak * 80, count);
    }

    public static CanvasRenderTarget Render(CanvasBitmap source, float sdrWhiteNits,
        float displayPeakNits = 0) => Render(source, Parameters.Create(sdrWhiteNits, Measure(source), displayPeakNits));

    // An explicit overload also permits deterministic mathematical/GPU validation.
    public static CanvasRenderTarget Render(CanvasBitmap source, Parameters parameters)
    {
        using var pixelView = CreatePixelView(source);
        var output = new CanvasRenderTarget(source.Device, source.SizeInPixels.Width,
            source.SizeInPixels.Height, 96, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Ignore);
        try
        {
            using var effect = new PixelShaderEffect<StarshotPerceptualShader>
            {
                // Arithmetic remains FP32 inside the shader. Its output is already
                // encoded sRGB, so an extra full-size RGBA32F effect buffer is unnecessary.
                BufferPrecision = CanvasBufferPrecision.Precision8UIntNormalized,
                ConstantBuffer = new(parameters.WhiteNits, parameters.KneePq, parameters.WhitePq,
                    parameters.PaperPq, parameters.TailShape),
            };
            effect.Sources[0] = pixelView ?? source;
            using var ds = output.CreateDrawingSession();
            ds.Units = CanvasUnits.Pixels;
            ds.Blend = CanvasBlend.Copy;
            ds.DrawImage(effect);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static CanvasBitmap? CreatePixelView(CanvasBitmap source) => source.Dpi == 96 ? null
        : CanvasBitmap.CreateFromDirect3D11Surface(source.Device, source, 96, CanvasAlphaMode.Ignore);
}
