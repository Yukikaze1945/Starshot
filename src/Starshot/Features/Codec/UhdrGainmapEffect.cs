using System;
using ComputeSharp;
using ComputeSharp.D2D1;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Graphics.Effects;

namespace Starshot.Features.Codec;

/// <summary>
/// 线性 scRGB → 线性工作色域（Display-P3），并把超出工作色域的颜色沿亮度线回拉进色域。
/// base 和 gain 都必须吃同一个节点的输出，否则两边像素定义不一致。
/// </summary>
public partial class UhdrWorkingGamutEffect : CanvasEffect
{
    public IGraphicsEffectSource Source { get; set; }

    public CanvasBufferPrecision? BufferPrecision { get; set; }

    protected override void BuildEffectGraph(CanvasEffectGraph effectGraph)
    {
        ColorMatrixEffect colorEffect = new()
        {
            Source = Source,
            ColorMatrix = UhdrColor.ScRgbToWorkingMatrix,
            ClampOutput = false,
            BufferPrecision = BufferPrecision,
        };
        PixelShaderEffect<UhdrGamutMapShader> effect = new()
        {
            BufferPrecision = BufferPrecision,
            ConstantBuffer = new UhdrGamutMapShader(UhdrColor.WorkingLuma),
        };
        effect.Sources[0] = colorEffect;
        effectGraph.RegisterOutputNode(effect);
    }

    protected override void ConfigureEffectGraph(CanvasEffectGraph effectGraph) { }
}

public partial class UhdrPixelGainEffect : CanvasEffect
{
    public IGraphicsEffectSource SdrSource { get; set; }

    public IGraphicsEffectSource HdrSource { get; set; }

    public CanvasBufferPrecision? BufferPrecision { get; set; }

    protected override void BuildEffectGraph(CanvasEffectGraph effectGraph)
    {
        PixelShaderEffect<UhdrPixelGainShader> effect = new()
        {
            BufferPrecision = BufferPrecision,
            ConstantBuffer = new UhdrPixelGainShader(UhdrColor.GainOffset),
        };
        effect.Sources[0] = SdrSource;
        effect.Sources[1] = HdrSource;
        effectGraph.RegisterOutputNode(effect);
    }

    protected override void ConfigureEffectGraph(CanvasEffectGraph effectGraph) { }
}

public partial class UhdrGainmapEffect : CanvasEffect
{
    public float3 MinContentBoost { get; set; }

    public float3 MaxContentBoost { get; set; }

    public IGraphicsEffectSource PixelGainSource { get; set; }

    public CanvasBufferPrecision? BufferPrecision { get; set; }

    protected override void BuildEffectGraph(CanvasEffectGraph effectGraph)
    {
        PixelShaderEffect<UhdrGainmapShader> effect = new() { BufferPrecision = BufferPrecision };
        effect.Sources[0] = PixelGainSource;
        effect.ConstantBuffer = new UhdrGainmapShader(Log2(MinContentBoost), Log2(MaxContentBoost));
        effectGraph.RegisterOutputNode(effect);
    }

    protected override void ConfigureEffectGraph(CanvasEffectGraph effectGraph) { }

    private static float3 Log2(float3 value)
    {
        return new float3(MathF.Log2(value.R), MathF.Log2(value.G), MathF.Log2(value.B));
    }
}

[D2DInputCount(1)]
[D2DInputSimple(0)]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct UhdrGamutMapShader(float3 luma) : ID2D1PixelShader
{
    public float4 Execute()
    {
        float4 color = D2D.GetInput(0);
        float3 rgb = color.RGB;
        float y = Hlsl.Dot(luma, rgb);
        // 沿亮度线向中性点回拉：Y 不变、色相角不变，t 取刚好让所有通道非负。
        // 逐通道直接截断会把红色推向黄/白，这条线只会把它变浓回不了的程度限死。
        float3 pull = Hlsl.Saturate(Hlsl.Max(0 - rgb, 0) / Hlsl.Max(y - rgb, 1e-6f));
        rgb = Hlsl.Lerp(rgb, Hlsl.Max(new float3(y, y, y), 0), pull);
        return new float4(Hlsl.Max(rgb, 0), color.A);
    }
}

[D2DInputCount(2)]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct UhdrPixelGainShader(float offset) : ID2D1PixelShader
{
    public float4 Execute()
    {
        float4 sdr = D2D.GetInput(0);
        float4 hdr = D2D.GetInput(1);
        float3 gain = (Hlsl.Max(hdr.RGB, 0) + offset) / (Hlsl.Max(sdr.RGB, 0) + offset);
        return new float4(gain, sdr.A);
    }
}

[D2DInputCount(1)]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct UhdrGainmapShader(float3 minBoostLog, float3 maxBoostLog)
    : ID2D1PixelShader
{
    public float4 Execute()
    {
        float4 gain = D2D.GetInput(0);
        float3 logGain = (Hlsl.Log2(gain.RGB) - minBoostLog) / (maxBoostLog - minBoostLog);
        return new float4(Hlsl.Clamp(logGain, 0, 1), gain.A);
    }
}
