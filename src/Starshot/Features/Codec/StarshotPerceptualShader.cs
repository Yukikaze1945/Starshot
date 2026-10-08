using ComputeSharp;
using ComputeSharp.D2D1;

namespace Starshot.Features.Codec;

[D2DInputCount(1)]
[D2DInputSimple(0)]
[D2DCompileOptions(D2D1CompileOptions.Default | D2D1CompileOptions.IeeeStrictness)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct StarshotPerceptualShader(
    float whiteNits, float kneePq, float whitePq, float paperPq, float tailShape) : ID2D1PixelShader
{
    private static float Clean(float v) => (Hlsl.AsUInt(v) & 0x7fffffffu) > 0x7f800000u
        ? 0 : Hlsl.Clamp(v, -65504, 65504);

    private static float Pq(float nits)
    {
        float p = Hlsl.Pow(Hlsl.Abs(nits / 10000), 0.1593017578125f);
        return Hlsl.Pow(Hlsl.Abs((0.8359375f + 18.8515625f * p) / (1 + 18.6875f * p)), 78.84375f);
    }

    private static float InversePq(float value)
    {
        float p = Hlsl.Pow(Hlsl.Abs(value), 1 / 78.84375f);
        return 10000 * Hlsl.Pow(Hlsl.Abs(Hlsl.Max(0, p - 0.8359375f) / (18.8515625f - 18.6875f * p)),
            1 / 0.1593017578125f);
    }

    private float MapLuminance(float y)
    {
        if (y <= 0.8f) return y;
        float p = Pq(y * whiteNits);
        float mapped;
        if (p <= whitePq)
        {
            float h = whitePq - kneePq;
            float t = (p - kneePq) / h;
            float t2 = t * t;
            float t3 = t2 * t;
            // Fixed C1 Hermite bridge: identity slope at 0.8, 0.2 slope at paper white.
            mapped = (2 * t3 - 3 * t2 + 1) * kneePq + (t3 - 2 * t2 + t) * h
                + (-2 * t3 + 3 * t2) * paperPq + (t3 - t2) * h * 0.2f;
        }
        else
        {
            float range = whitePq - paperPq;
            float q = (p - whitePq) * 0.2f / range;
            // Positive derivative; asymptotic white, no percentile/peak hard clipping.
            mapped = paperPq + range * (1 - Hlsl.Pow(Hlsl.Abs(1 + tailShape * q), -1 / tailShape));
        }
        return InversePq(mapped) / whiteNits;
    }

    private static float Root(float v) => Hlsl.Sign(v) * Hlsl.Pow(Hlsl.Abs(v), 1 / 3f);

    private static float3 ToLab(float3 rgb)
    {
        float l = Root(Hlsl.Dot(rgb, new float3(0.4122214708f, 0.5363325363f, 0.0514459929f)));
        float m = Root(Hlsl.Dot(rgb, new float3(0.2119034982f, 0.6806995451f, 0.1073969566f)));
        float s = Root(Hlsl.Dot(rgb, new float3(0.0883024619f, 0.2817188376f, 0.6299787005f)));
        return new float3(0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
            1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
            0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s);
    }

    private static float3 FromLab(float3 lab)
    {
        float l = lab.X + 0.3963377774f * lab.Y + 0.2158037573f * lab.Z;
        float m = lab.X - 0.1055613458f * lab.Y - 0.0638541728f * lab.Z;
        float s = lab.X - 0.0894841775f * lab.Y - 1.2914855480f * lab.Z;
        l = l * l * l; m = m * m * m; s = s * s * s;
        return new float3(4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s,
            -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s,
            -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s);
    }

    private static bool Legal(float3 rgb) => rgb.X >= 0 && rgb.Y >= 0 && rgb.Z >= 0
        && rgb.X <= 1 && rgb.Y <= 1 && rgb.Z <= 1;

    private static float3 OnRay(float3 lab, float anchor, float t) =>
        new(anchor + t * (lab.X - anchor), t * lab.Y, t * lab.Z);

    private static float Anchor(float3 lab, float chroma)
    {
        float delta = lab.X - 0.5f;
        float e = 0.5f + Hlsl.Abs(delta) + 0.2f * chroma;
        // Adaptive neutral anchor: saturated colors can lose a little lightness rather
        // than turning into white/pink at constant L. Every point on the ray keeps hue.
        return Hlsl.Clamp(0.5f * (1 + Hlsl.Sign(delta)
            * (e - Hlsl.Sqrt(Hlsl.Max(0, e * e - 2 * Hlsl.Abs(delta))))), 0.000001f, 0.999999f);
    }

    private static float CuspLightness(float3 lab, float chroma)
    {
        float2 hue = lab.YZ / chroma;
        float low = 0;
        float high = 2;
        // At L=1 find the first zero-channel saturation, then scale the ray to
        // max RGB=1. This is the hue's maximum-chroma cusp, not a per-channel clip.
        for (int i = 0; i < 12; i++)
        {
            float middle = (low + high) * 0.5f;
            float3 candidate = FromLab(new float3(1, hue.X * middle, hue.Y * middle));
            if (candidate.X >= 0 && candidate.Y >= 0 && candidate.Z >= 0) low = middle;
            else high = middle;
        }
        float3 edge = FromLab(new float3(1, hue.X * low, hue.Y * low));
        return Hlsl.Pow(Hlsl.Abs(1 / Hlsl.Max(edge.X, Hlsl.Max(edge.Y, edge.Z))), 1 / 3f);
    }

    private static float3 GamutMap(float3 rgb, float highlightWeight)
    {
        if (Legal(rgb) && highlightWeight == 0) return rgb;
        float3 lab = ToLab(rgb);
        float chroma = Hlsl.Length(lab.YZ);
        float anchor = Anchor(lab, chroma);
        float excessRgb = Hlsl.Max(Hlsl.Max(rgb.X, Hlsl.Max(rgb.Y, rgb.Z)) - 1,
            -Hlsl.Min(rgb.X, Hlsl.Min(rgb.Y, rgb.Z)));
        // The soft margin vanishes at the protected SDR gamut boundary. A fixed
        // margin would introduce a visible jump as a tiny channel crosses zero.
        float margin = 0.02f * Hlsl.Max(highlightWeight, Hlsl.Saturate(excessRgb * 8));
        float high = 1 / (1 - margin);
        if (Legal(rgb) && Legal(FromLab(OnRay(lab, anchor, high)))) return rgb;
        if (!Legal(rgb) && chroma > 0.0001f)
        {
            float saturation = Hlsl.Saturate((chroma / Hlsl.Max(lab.X, 0.0001f) - 0.15f) / 0.15f);
            saturation = saturation * saturation * (3 - 2 * saturation);
            float cusp = CuspLightness(lab, chroma);
            float excess = Hlsl.Max(lab.X / cusp - 1, 0);
            // A rational shoulder has strictly positive dLout/dLin and approaches
            // 1.25*cusp. This retains useful bright-red separation instead of
            // flattening it into the quantization/dither floor. An exponential
            // return to the cusp would reverse bright
            // saturated ramps. Low-chroma skin/neutral highlights are protected.
            float compressedL = cusp * (1 + excess / (1 + 4 * excess));
            if (lab.X > cusp) lab.X = Hlsl.Lerp(lab.X, compressedL, saturation);
            anchor = Anchor(lab, chroma);
        }
        float low = 0;
        for (int i = 0; i < 16; i++)
        {
            float middle = (low + high) * 0.5f;
            if (Legal(FromLab(OnRay(lab, anchor, middle)))) low = middle;
            else high = middle;
        }
        float knee = (1 - margin) * low;
        float range = Hlsl.Max(low - knee, 0.00000001f);
        float t = knee + range * (1 - Hlsl.Exp(-(1 - knee) / range));
        return FromLab(OnRay(lab, anchor, t));
    }

    private static float Oetf(float v) => v <= 0.0031308f ? v * 12.92f
        : 1.055f * Hlsl.Pow(Hlsl.Abs(v), 1 / 2.4f) - 0.055f;

    public float4 Execute()
    {
        float4 input = D2D.GetInput(0);
        // No max(rgb,0) here: negative scRGB channels carry wide-gamut information.
        float3 rgb = new float3(Clean(input.X), Clean(input.Y), Clean(input.Z)) * (80 / whiteNits);
        float y = Hlsl.Dot(rgb, new float3(0.2126f, 0.7152f, 0.0722f));
        if (y <= 0.00000001f) return new float4(0, 0, 0, 1);
        rgb *= MapLuminance(y) / y;
        float weight = Hlsl.Saturate((y - 0.8f) / 0.2f);
        weight = weight * weight * (3 - 2 * weight);
        rgb = GamutMap(rgb, weight);
        // Roundoff safety only: the gamut search has already placed the color inside.
        rgb = Hlsl.Saturate(rgb);
        float3 encoded = new(Oetf(rgb.X), Oetf(rgb.Y), Oetf(rgb.Z));
        if (y > 1)
        {
            float2 position = D2D.GetScenePosition().XY;
            // Sub-LSB deterministic dither only in HDR highlights; SDR text is untouched.
            float noise = Hlsl.Frac(52.9829189f * Hlsl.Frac(Hlsl.Dot(position,
                new float2(0.06711056f, 0.00583715f)))) - 0.5f;
            encoded += noise / 255;
        }
        return new float4(Hlsl.Saturate(encoded), 1);
    }
}

[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DCompileOptions(D2D1CompileOptions.Default | D2D1CompileOptions.IeeeStrictness)]
[D2DInputDescription(0, D2D1Filter.MinMagMipPoint)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct PerceptualStatisticsShader(int width, int height, int block) : ID2D1PixelShader
{
    private static float Clean(float v) => (Hlsl.AsUInt(v) & 0x7fffffffu) > 0x7f800000u
        ? 0 : Hlsl.Clamp(v, -65504, 65504);
    private float Read(int x, int y)
    {
        float4 c = D2D.SampleInputAtPosition(0, new float2(x + 0.5f, y + 0.5f));
        return Hlsl.Max(0, 0.2126f * Clean(c.X) + 0.7152f * Clean(c.Y) + 0.0722f * Clean(c.Z));
    }

    public float4 Execute()
    {
        float2 pos = D2D.GetScenePosition().XY;
        int x = (int)Hlsl.Floor(pos.X) * block;
        int y = (int)Hlsl.Floor(pos.Y) * block;
        int w = Hlsl.Min(block, width - x);
        int h = Hlsl.Min(block, height - y);
        float peak = 0;
        for (int iy = 0; iy < block; iy++)
        {
            for (int ix = 0; ix < block; ix++)
                peak = Hlsl.Max(peak, Read(Hlsl.Min(x + ix, width - 1), Hlsl.Min(y + iy, height - 1)));
        }
        return new float4(Read(x + (int)(w * 0.25f), y + (int)(h * 0.25f)), peak,
            Read(x + (int)(w * 0.5f), y + (int)(h * 0.5f)),
            Read(x + (int)(w * 0.75f), y + (int)(h * 0.75f)));
    }
}
