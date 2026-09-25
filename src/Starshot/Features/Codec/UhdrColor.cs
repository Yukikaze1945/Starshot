using System;
using System.Numerics;
using ComputeSharp;
using Microsoft.Graphics.Canvas.Effects;
using Starward.Codec.ICC;
using Starward.Codec.UltraHdr;

namespace Starshot.Features.Codec;

/// <summary>
/// Ultra HDR 通道（base JPEG + gain map）的色彩定义。
/// 截图帧是线性 scRGB（BT.709 原色、1.0=80nits、允许负通道），而 base 是 8bit sRGB JPEG、
/// gain map 只能表达逐通道非负倍数：负通道必须在截断前靠换色域解决，否则广色域红色只能被拉灰。
/// </summary>
internal static class UhdrColor
{
    /// <summary>
    /// CTA.2048 默认增益偏移。gain 着色器和 gainmap metadata 必须同源，
    /// 否则分子分母各自加在不同量程上，重建值逐通道偏移。
    /// </summary>
    public const float GainOffset = 0.015625f;

    /// <summary>base 与 gain 共用的工作色域。</summary>
    public static ColorPrimaries WorkingPrimaries { get; } = ColorPrimaries.DisplayP3;

    /// <summary>base JPEG 声明的色域，必须与 <see cref="WorkingPrimaries"/> 同步（libultrahdr 据此合成 ICC）。</summary>
    public static UhdrColorGamut WorkingColorGamut { get; } = UhdrColorGamut.DisplayP3;

    /// <summary>
    /// 线性 scRGB(BT.709) → 线性工作色域。D2D 颜色矩阵按行向量乘（out_j = Σ v_i·M[i][j]），
    /// 与 <see cref="Vector3.Transform(Vector3, Matrix4x4)"/> 同式，故可直接转交 ColorMatrixEffect。
    /// </summary>
    public static Matrix5x4 ScRgbToWorkingMatrix { get; } = ToMatrix5x4(
        ColorPrimaries.GetColorTransferMatrix(ColorPrimaries.BT709, WorkingPrimaries)
    );

    /// <summary>
    /// 工作色域的线性亮度系数（RGB→XYZ 的 Y 行，和为 1）。色域压缩沿亮度线回拉时用，
    /// 保证压缩后的 Y 与 <see cref="Screenshot.ScreenCaptureService.GetContentLightLevels"/> 算出的 maxCLL 同值。
    /// </summary>
    public static float3 WorkingLuma { get; } = GetWorkingLuma();

    private static float3 GetWorkingLuma()
    {
        Matrix4x4 xyz = WorkingPrimaries.GetRGBToXYZMatrix();
        return new float3(xyz.M12, xyz.M22, xyz.M32);
    }

    private static Matrix5x4 ToMatrix5x4(Matrix4x4 matrix4x4)
    {
        return new Matrix5x4(
            matrix4x4.M11,
            matrix4x4.M12,
            matrix4x4.M13,
            matrix4x4.M14,
            matrix4x4.M21,
            matrix4x4.M22,
            matrix4x4.M23,
            matrix4x4.M24,
            matrix4x4.M31,
            matrix4x4.M32,
            matrix4x4.M33,
            matrix4x4.M34,
            matrix4x4.M41,
            matrix4x4.M42,
            matrix4x4.M43,
            matrix4x4.M44,
            0,
            0,
            0,
            0
        );
    }
}
