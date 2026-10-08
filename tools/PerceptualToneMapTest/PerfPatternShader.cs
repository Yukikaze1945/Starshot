using ComputeSharp;
using ComputeSharp.D2D1;

[D2DInputCount(0)]
[D2DCompileOptions(D2D1CompileOptions.Default | D2D1CompileOptions.IeeeStrictness)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
internal readonly partial struct PerfPatternShader : ID2D1PixelShader
{
    public float4 Execute()
    {
        float2 p = D2D.GetScenePosition().XY;
        float x = p.X, y = p.Y;
        float3 c = new float3(0.035f, 0.045f, 0.06f); // dark SDR UI
        if (y < 240)
        {
            int band = (int)(x / 384);
            if (band > 4) band = 4;
            float v = band == 0 ? 1 : band == 1 ? 2 : band == 2 ? 4 : band == 3 ? 8 : 16;
            c = new float3(v, v, v);
        }
        else if (y < 480)
        {
            int band = (int)(x / 480);
            if (band == 0) c = new float3(4, 0, 0);
            else if (band == 1) c = new float3(16, 0, 0);
            else if (band == 2) c = new float3(0, 4, 0);
            else if (band == 3) c = new float3(0, 0, 4);
            else if (band == 4) c = new float3(3, 0.1f, 2);
            else if (band == 5) c = new float3(0.2f, 4, 0.3f);
            else if (band == 6) c = new float3(0.1f, 0.2f, 4);
            else c = new float3(1.5f, 0.7f, 0.3f);
        }
        else if (y < 720)
        {
            int band = (int)(x / 480);
            if (band > 3) band = 3;
            if (band == 0) c = new float3(1.25f, 0.12f, -0.04f);
            else if (band == 1) c = new float3(-0.06f, 0.92f, 0.16f);
            else if (band == 2) c = new float3(0.08f, 0.14f, 1.3f);
            else c = new float3(1.5f, 0.7f, 0.3f);
        }
        else
        {
            // Synthetic game UI: panels, buttons, text-like antialiased bars and SDR gradients.
            if (y < 800 || x < 300 || (x > 3020 && y < 1900)) c = new float3(0.075f, 0.085f, 0.105f);
            else if (x > 420 && x < 2900 && y > 960 && y < 1660) c = new float3(0.12f, 0.14f, 0.17f);
            if (x > 460 && x < 1760 && y > 1020 && y < 1060) c = new float3(0.62f, 0.31f, 0.12f);
            if (x > 480 && x < 1820 && y > 1150 && y < 1164 && y - Hlsl.Floor(y / 3) * 3 != 0) c = new float3(0.64f, 0.68f, 0.72f);
            if (x > 2380 && x < 2770 && y > 1510 && y < 1600) c = new float3(0.75f, 0.28f, 0.12f);
            float gradient = 0.08f + 0.55f * (x / 3840f);
            if (y > 1850 && y < 2080) c = new float3(gradient, gradient * 0.8f, gradient * 0.65f);
        }
        return new float4(c, 1);
    }
}
