using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Starshot.Features.Codec;
using Starward.Codec.ICC;
using Starward.Codec.UltraHdr;
using Windows.Graphics.DirectX;

/// <summary>
/// 测试台：色块图构造、真实编解码、指标计算。数值全部来自 GPU effect graph 与 libultrahdr，不在 CPU 上复刻管线。
/// </summary>
internal static class Harness
{
    public const float MaxCll = 1000f;

    /// <summary>单个色块边长。取样点离块边界 16px，4:2:0 的色度块不会互相污染。</summary>
    public const int Patch = 32;

    private const int Columns = 5;
    private const int Rows = 2;

    private static readonly Matrix4x4 Xyz709 = ColorPrimaries.BT709.GetRGBToXYZMatrix();
    private static readonly Matrix4x4 XyzP3 = ColorPrimaries.DisplayP3.GetRGBToXYZMatrix();

    /// <summary>测试色。scRGB 线性，1.0 = 80nits；广色域色用通道负值表达，正是被抓帧数据的真实形状。</summary>
    public static List<Swatch> Swatches()
    {
        Matrix4x4 p3To709 = ColorPrimaries.GetColorTransferMatrix(
            ColorPrimaries.DisplayP3,
            ColorPrimaries.BT709
        );
        Matrix4x4 bt2020To709 = ColorPrimaries.GetColorTransferMatrix(
            ColorPrimaries.BT2020,
            ColorPrimaries.BT709
        );
        return
        [
            new("neutral gray 18%", new Vector3(0.18f, 0.18f, 0.18f)),
            new("SDR white", new Vector3(1, 1, 1)),
            new("near black", new Vector3(0.01f, 0.012f, 0.011f)),
            new("BT.709 red @120nits", new Vector3(1.5f, 0, 0)),
            new("BT.709 green @160nits", new Vector3(0, 2f, 0)),
            new("BT.709 blue @200nits", new Vector3(0, 0, 2.5f)),
            new("P3 red (out of 709)", Vector3.Transform(Vector3.UnitX, p3To709) * 2f),
            new("P3 green (out of 709)", Vector3.Transform(Vector3.UnitY, p3To709) * 2f),
            new("bright HDR red @640nits", new Vector3(8f, 0.05f, 0.02f)),
            new("BT.2020 red (out of P3)", Vector3.Transform(Vector3.UnitX, bt2020To709) * 2f),
        ];
    }

    public static List<PatchPoint> Centers()
    {
        List<Swatch> all = Swatches();
        List<PatchPoint> centers = [];
        for (int i = 0; i < all.Count; i++)
        {
            int col = i % Columns;
            int row = i / Columns;
            centers.Add(
                new PatchPoint(
                    col * Patch + Patch / 2,
                    row * Patch + Patch / 2,
                    all[i].Name,
                    all[i].ScRgb
                )
            );
        }
        return centers;
    }

    /// <summary>
    /// 一张图装满所有色块。必须混布：单色图会让全图 min/max content boost 相等，
    /// gain map 归一化出现 0/0，libultrahdr 也会以 capacityMax<=capacityMin 直接拒绝编码。
    /// </summary>
    public static CanvasBitmap MakePatchImage(CanvasDevice device)
    {
        int width = Columns * Patch;
        int height = Rows * Patch;
        // R16G16B16A16Float 的字节缓冲是 half（8 字节/像素），不是 float32。
        // 传错元素类型会让位图内容是位模式被误读的垃圾，整张测试表随之失真。
        ushort[] pixels = new ushort[width * height * 4];
        List<Swatch> all = Swatches();
        for (int i = 0; i < all.Count; i++)
        {
            Vector3 c = all[i].ScRgb;
            ushort r = BitConverter.HalfToUInt16Bits((Half)c.X);
            ushort g = BitConverter.HalfToUInt16Bits((Half)c.Y);
            ushort b = BitConverter.HalfToUInt16Bits((Half)c.Z);
            ushort a = BitConverter.HalfToUInt16Bits((Half)1f);
            int col = i % Columns;
            int row = i / Columns;
            for (int y = row * Patch; y < (row + 1) * Patch; y++)
            {
                for (int x = col * Patch; x < (col + 1) * Patch; x++)
                {
                    int o = (y * width + x) * 4;
                    pixels[o] = r;
                    pixels[o + 1] = g;
                    pixels[o + 2] = b;
                    pixels[o + 3] = a;
                }
            }
        }
        return CanvasBitmap.CreateFromBytes(
            device,
            MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray(),
            width,
            height,
            DirectXPixelFormat.R16G16B16A16Float,
            96
        );
    }

    /// <summary>改后管线里真正进 gain 分子与 base 的节点：scRGB→工作色域 + 色域压缩后的线性值。</summary>
    public static Dictionary<string, Vector3> MeasureWorkings(
        CanvasDevice device,
        CanvasBitmap src
    )
    {
        using UhdrWorkingGamutEffect effect = new()
        {
            Source = src,
            BufferPrecision = CanvasBufferPrecision.Precision16Float,
        };
        return SampleFloatTarget(device, effect, src.SizeInPixels.Width, src.SizeInPixels.Height);
    }

    /// <summary>旧管线的分子只是 Max(scRGB,0)（负通道被截掉），这就是它丢饱和的方式。</summary>
    public static Vector3 LegacyNumerator(Vector3 scRgb) =>
        new(Math.Max(scRgb.X, 0), Math.Max(scRgb.Y, 0), Math.Max(scRgb.Z, 0));

    /// <summary>
    /// 逐级量出生产管线里各节点的真实量程（1.0 到底等于多少 nits）。
    /// 旧管线的增益分母取 toneMapEffect、base 却走 toneMap→WhiteLevel→OETF，
    /// 两者是否同量程只能量出来，不能靠读代码猜。
    /// </summary>
    public static (
        Dictionary<string, Vector3> Tone,
        Dictionary<string, Vector3> SdrLinear,
        Dictionary<string, Vector3> HdrNormalized
    ) MeasureChain(
        CanvasDevice device,
        CanvasBitmap src,
        float sdrWhiteLevel,
        bool workingGamut
    )
    {
        uint width = src.SizeInPixels.Width;
        uint height = src.SizeInPixels.Height;
        UhdrWorkingGamutEffect? widened = workingGamut
            ? new UhdrWorkingGamutEffect
            {
                Source = src,
                BufferPrecision = CanvasBufferPrecision.Precision16Float,
            }
            : null;
        using var widenedGuard = widened;
        Windows.Graphics.Effects.IGraphicsEffectSource head =
            widened is null ? src : widened;
        using HdrToneMapEffect tone = new()
        {
            Source = head,
            InputMaxLuminance = MaxCll,
            OutputMaxLuminance = sdrWhiteLevel,
            DisplayMode = HdrToneMapEffectDisplayMode.Hdr,
            BufferPrecision = CanvasBufferPrecision.Precision16Float,
        };
        using WhiteLevelAdjustmentEffect white = new()
        {
            Source = tone,
            InputWhiteLevel = 80,
            OutputWhiteLevel = sdrWhiteLevel,
            BufferPrecision = CanvasBufferPrecision.Precision16Float,
        };
        // 改后管线里增益的分子：同一个白电平归一化，但不过 tone map。
        using WhiteLevelAdjustmentEffect hdr = new()
        {
            Source = head,
            InputWhiteLevel = 80,
            OutputWhiteLevel = sdrWhiteLevel,
            BufferPrecision = CanvasBufferPrecision.Precision16Float,
        };
        return (
            SampleFloatTarget(device, tone, width, height),
            SampleFloatTarget(device, white, width, height),
            SampleFloatTarget(device, hdr, width, height)
        );
    }

    /// <summary>把解出来的 xy 归到已知色域；认不出返回 0。Id 用 Starward 的定义，便于和声明对照。</summary>
    public static int PrimariesId((float X, float Y)[] pts)
    {
        const float tol = 0.006f;
        if (IccProbe.MaxDeviation(pts, IccProbe.Primaries("P3")) <= tol)
        {
            return ColorPrimaries.DisplayP3.Id;
        }
        if (IccProbe.MaxDeviation(pts, IccProbe.Primaries("BT709")) <= tol)
        {
            return ColorPrimaries.BT709.Id;
        }
        if (IccProbe.MaxDeviation(pts, IccProbe.Primaries("BT2020")) <= tol)
        {
            return ColorPrimaries.BT2020.Id;
        }
        return 0;
    }

    /// <summary>sRGB 逆 OETF：把文件里真正存着的 8bit base 还原成线性值，用来对齐各节点量程。</summary>
    public static Vector3 InvOetf(Vector3 v)
    {
        static float F(float c) =>
            c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        return new Vector3(F(v.X), F(v.Y), F(v.Z));
    }

    /// <summary>与 InvOetf 配对的正向 OETF，用来把偏差换回 8bit 编码口径。</summary>
    public static Vector3 Oetf(Vector3 v)
    {
        static float F(float c) =>
            c <= 0.0031308f ? 12.92f * c : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
        return new Vector3(F(v.X), F(v.Y), F(v.Z));
    }

    public static byte[] EncodeLegacy(CanvasBitmap src, float sdrWhiteLevel)
    {
        using var ms = new MemoryStream();
        LegacyUhdr
            .Encode(src, ms, MaxCll, sdrWhiteLevel)
            .GetAwaiter()
            .GetResult();
        return ms.ToArray();
    }

    public static byte[] EncodeNew(CanvasBitmap src, float sdrWhiteLevel)
    {
        using var ms = new MemoryStream();
        ImageSaver
            .SaveAsUhdrAsync(src, ms, MaxCll, sdrWhiteLevel)
            .GetAwaiter()
            .GetResult();
        return ms.ToArray();
    }

    public static List<Row> Measure(
        CanvasDevice device,
        string arm,
        byte[] file,
        float sdrWhiteLevel,
        Dictionary<string, Vector3> workings,
        Dictionary<string, Vector3> denominatorNode,
        Dictionary<string, Vector3>? numeratorNode,
        List<string> diag
    )
    {
        Matrix4x4 toXyz = arm == "new" ? XyzP3 : Xyz709;
        List<PatchPoint> centers = Centers();

        (Dictionary<string, Vector3> values, Dictionary<string, float> spread8) = Decode8(
            file,
            centers,
            diag
        );
        (Dictionary<string, Vector3> reconRaw, Dictionary<string, float> spreadH) = DecodeHalf(
            file,
            centers,
            diag
        );

        string iccState;
        int iccId;
        (float X, float Y)[] iccXY = [];
        float minBoost;
        float maxBoost;
        byte[] baseBytes = SplitJpeg(file, 0);
        byte[] gainBytes = SplitJpeg(file, 1);
        using (var probe = UhdrDecoder.Create(file))
        {
            ReadOnlySpan<byte> icc = probe.GetIccData();
            UhdrGainmapMetadata gm = probe.GetGainmapMetadata();
            minBoost = MathF.Min(gm.MinContentBoost[0], MathF.Min(gm.MinContentBoost[1], gm.MinContentBoost[2]));
            maxBoost = gm.HdrCapacityMax;

            // 两处独立取证：文件字节里的 APP2 ICC，和解码器给出的 ICC。
            // 只看字节能排除「解码器自己脑补色域」的可能；两者一致才算容器真的标了这个色域。
            byte[] rawIcc = JpegProbe.ExtractIcc(baseBytes);
            (int rawStart, var rawPts, string rawText) = IccProbe.Parse(rawIcc);
            (int decStart, var decPts, string decText) = IccProbe.Parse(icc);
            // ColorPrimaries 没有值相等语义，只有 Id 可比（BT709=1 / BT2020=9 / DisplayP3=12）。
            iccId = rawPts is null ? 0 : PrimariesId(rawPts);
            iccState =
                $"文件字节(len={rawIcc.Length},@{rawStart}): {rawText} | 解码器(len={icc.Length},@{decStart}): {decText}";
            if (rawPts is not null)
            {
                iccXY = rawPts;
                iccState += rawPts.SequenceEqual(decPts ?? [])
                    ? " | 两处一致"
                    : " | 两处不一致";
                // 量化偏差：标签到底是哪组原色，不靠「看起来像」。
                iccState +=
                    $" | 与 P3(D50 适配后)最大偏差={IccProbe.MaxDeviation(rawPts, IccProbe.Primaries("P3")):F4}"
                    + $" 与 BT.709(D50 适配后)={IccProbe.MaxDeviation(rawPts, IccProbe.Primaries("BT709")):F4}";
            }
        }

        string baseJpeg = JpegProbe.Describe(baseBytes);
        string gainmapJpeg = JpegProbe.Describe(gainBytes);

        List<Row> rows = [];
        foreach (PatchPoint p in centers)
        {
            Vector3 working = arm == "new" ? workings[p.Name] : LegacyNumerator(p.ScRgb);
            Vector3 base8 = values[p.Name];
            Vector3 recon = reconRaw[p.Name] * sdrWhiteLevel;
            Vector3 refNits = working * 80f;
            float worst = 0;
            for (int c = 0; c < 3; c++)
            {
                float expected = Get(working, c) * 80f;
                if (expected < 1f)
                {
                    continue; // 1nits 以下不统计相对误差
                }
                worst = Math.Max(worst, Math.Abs(Get(recon, c) - expected) / expected);
            }
            rows.Add(
                new Row
                {
                    Name = p.Name,
                    Arm = arm,
                    SdrWhiteLevel = sdrWhiteLevel,
                    Input = p.ScRgb,
                    Working = working,
                    Base8 = base8,
                    ReconNits = recon,
                    RefNits = refNits,
                    MaxRelError = worst,
                    RefChroma = Chroma(working, toXyz),
                    RecChroma = Chroma(recon / sdrWhiteLevel, toXyz),
                    Denominator = denominatorNode[p.Name],
                    Numerator = arm == "old" ? working : numeratorNode![p.Name],
                    StoredLinear = InvOetf(base8),
                    Finite = !Bad(p.ScRgb) && !Bad(working) && !Bad(recon) && !Bad(base8),
                    Spread = Math.Max(spread8[p.Name], spreadH[p.Name]),
                    MinBoost = minBoost,
                    MaxBoost = maxBoost,
                    IccId = iccId,
                    IccText = iccState,
                    BaseJpeg = baseJpeg,
                    GainmapJpeg = gainmapJpeg,
                }
            );
        }
        return rows;
    }

    private static (Dictionary<string, Vector3>, Dictionary<string, float>) Decode8(
        byte[] file,
        List<PatchPoint> centers,
        List<string> diag
    )
    {
        using var decoder = new UhdrDecoder();
        decoder.SetImage(file);
        decoder.SetOutImagePixelFormat(UhdrPixelFormat._32bppRGBA8888);
        decoder.SetOutColorTransfer(UhdrColorTransfer.SRGB);
        decoder.Decode();
        UhdrRawImage image = decoder.GetDecodedImage();
        ReportGeometry(image, diag);
        Dictionary<string, Vector3> values = [];
        Dictionary<string, float> spread = [];
        foreach (PatchPoint p in centers)
        {
            List<Vector3> samples = [];
            foreach ((int x, int y) in Offsets(p))
            {
                IntPtr origin = image.Plane[0] + (y * (int)image.Stride[0] + x) * 4;
                byte[] buffer = new byte[4];
                Marshal.Copy(origin, buffer, 0, 4);
                samples.Add(new Vector3(buffer[0], buffer[1], buffer[2]) / 255f);
            }
            values[p.Name] = samples[0];
            spread[p.Name] = MaxDeviation(samples);
        }
        return (values, spread);
    }

    private static (Dictionary<string, Vector3>, Dictionary<string, float>) DecodeHalf(
        byte[] file,
        List<PatchPoint> centers,
        List<string> diag
    )
    {
        using var decoder = new UhdrDecoder();
        decoder.SetImage(file);
        decoder.SetOutImagePixelFormat(UhdrPixelFormat._64bppRGBAHalfFloat);
        decoder.SetOutColorTransfer(UhdrColorTransfer.Linear);
        // 不解封顶的话所有高光的重建值都被压在 1.0（=SDR 白）以下，误差列会全部退化成「亮度=SDR 白」。
        decoder.SetOutMaxDisplayBoost(100f);
        decoder.Decode();
        UhdrRawImage image = decoder.GetDecodedImage();
        ReportGeometry(image, diag);
        Dictionary<string, Vector3> values = [];
        Dictionary<string, float> spread = [];
        foreach (PatchPoint p in centers)
        {
            List<Vector3> samples = [];
            foreach ((int x, int y) in Offsets(p))
            {
                IntPtr origin = image.Plane[0] + (y * (int)image.Stride[0] + x) * 8;
                float[] v = new float[3];
                for (int i = 0; i < 3; i++)
                {
                    v[i] = (float)BitConverter.UInt16BitsToHalf(
                        unchecked((ushort)Marshal.ReadInt16(origin, i * 2))
                    );
                }
                samples.Add(new Vector3(v[0], v[1], v[2]));
            }
            values[p.Name] = samples[0];
            spread[p.Name] = MaxDeviation(samples);
        }
        return (values, spread);
    }

    /// <summary>同一色块内离中心 10px 的 5 个点。取样口径错了（stride 单位弄反）时这些点会散到别的色块上。</summary>
    private static IEnumerable<(int x, int y)> Offsets(PatchPoint p)
    {
        yield return (p.X, p.Y);
        yield return (p.X - 10, p.Y);
        yield return (p.X + 10, p.Y);
        yield return (p.X, p.Y - 10);
        yield return (p.X, p.Y + 10);
    }

    private static float MaxDeviation(List<Vector3> samples)
    {
        float worst = 0;
        foreach (Vector3 s in samples)
        {
            for (int c = 0; c < 3; c++)
            {
                // 相对偏差：half 在 8 附近的最小间隔就有 0.0078，绝对阈值会误报。
                float scale = Math.Max(1f, Math.Abs(Get(samples[0], c)));
                worst = Math.Max(worst, Math.Abs(Get(s, c) - Get(samples[0], c)) / scale);
            }
        }
        return worst;
    }

    private static void ReportGeometry(UhdrRawImage image, List<string> diag)
    {
        string line =
            $"decoded {image.PixelFormat} {image.Width}x{image.Height} stride={image.Stride[0]} gamut={image.ColorGamut} transfer={image.ColorTransfer}";
        if (!diag.Contains(line))
        {
            diag.Add(line);
        }
    }

    private static Dictionary<string, Vector3> SampleFloatTarget(
        CanvasDevice device,
        ICanvasImage source,
        uint width,
        uint height
    )
    {
        using CanvasRenderTarget rt = new(
            device,
            width,
            height,
            96,
            DirectXPixelFormat.R32G32B32A32Float,
            CanvasAlphaMode.Premultiplied
        );
        using (var ds = rt.CreateDrawingSession())
        {
            ds.Clear(Microsoft.UI.Colors.Transparent);
            ds.DrawImage(source);
        }
        var span = MemoryMarshal.Cast<byte, float>(rt.GetPixelBytes());
        int expected = (int)(width * height * 16);
        if (span.Length * 4 < expected)
        {
            throw new InvalidOperationException(
                $"render target 回读 {span.Length * 4} 字节，{width}x{height} R32G32B32A32Float 需要 {expected} 字节"
            );
        }
        Dictionary<string, Vector3> values = [];
        foreach (PatchPoint p in Centers())
        {
            int i = (p.Y * (int)width + p.X) * 4;
            values[p.Name] = new Vector3(span[i], span[i + 1], span[i + 2]);
        }
        return values;
    }

    /// <summary>u'v' 里离白点的距离：饱和色被拉灰时这个值掉得最快。</summary>
    public static float Chroma(Vector3 rgb, Matrix4x4 toXyz)
    {
        (float u, float v) uv = Uv(Vector3.Transform(rgb, toXyz));
        (float u, float v) white = Uv(Vector3.Transform(Vector3.One, toXyz));
        return MathF.Sqrt(
            (uv.u - white.u) * (uv.u - white.u) + (uv.v - white.v) * (uv.v - white.v)
        );
    }

    private static (float u, float v) Uv(Vector3 xyz)
    {
        float d = -2f * xyz.X + 12f * xyz.Y + 3f * xyz.Z;
        return d == 0 ? (0, 0) : (9f * xyz.X / d, 4f * xyz.Y / d);
    }

    /// <summary>Ultra HDR 文件是两段 JPEG 串起来的：取第 n 段（0=base，1=gain map）。</summary>
    public static byte[] SplitJpeg(byte[] bytes, int index)
    {
        List<int> starts = [];
        for (int i = 0; i + 1 < bytes.Length; i++)
        {
            if (bytes[i] == 0xFF && bytes[i + 1] == 0xD8)
            {
                starts.Add(i);
            }
        }
        if (index >= starts.Count)
        {
            throw new InvalidOperationException($"only {starts.Count} jpeg segments found");
        }
        int start = starts[index];
        int end = index + 1 < starts.Count ? starts[index + 1] : bytes.Length;
        return bytes[start..end];
    }

    public static float Get(Vector3 v, int i) => i switch
    {
        0 => v.X,
        1 => v.Y,
        _ => v.Z,
    };

    /// <summary>取最大通道：饱和色里只有最大通道不会被 0/0 或极小值干扰，比值口径统一用它。</summary>
    public static float MaxChannel(Vector3 v) => MathF.Max(v.X, MathF.Max(v.Y, v.Z));

    /// <summary>增益统一口径：看分子最大的那个通道（饱和色的信息就在它上面），三个比值都用同一个通道号。</summary>
    public static int GainChannel(Vector3 numerator) =>
        numerator.X >= numerator.Y && numerator.X >= numerator.Z
            ? 0
            : (numerator.Y >= numerator.Z ? 1 : 2);

    public static float ExpectedGain(Vector3 numerator, Vector3 storedBase)
    {
        int i = GainChannel(numerator);
        float d = Get(storedBase, i);
        return d < 1e-4f ? float.NaN : Get(numerator, i) / d;
    }

    /// <summary>解码重建值相对文件里存着的 base，实际乘上了多少。</summary>
    public static float AppliedGain(Vector3 numerator, Vector3 reconNormalized, Vector3 storedBase)
    {
        int i = GainChannel(numerator);
        float d = Get(storedBase, i);
        return d < 1e-4f ? float.NaN : Get(reconNormalized, i) / d;
    }

    private static bool Bad(Vector3 v) =>
        float.IsNaN(v.X)
        || float.IsNaN(v.Y)
        || float.IsNaN(v.Z)
        || !float.IsFinite(v.X + v.Y + v.Z);

    public static string Fmt(Vector3 v) => $"({v.X,7:F3},{v.Y,7:F3},{v.Z,7:F3})";

    /// <summary>
    /// 分母节点相对「文件里的 base」的偏差，口径是 8bit 编码值（单位 1/255）。
    /// 用线性口径会在暗部被量化步长放大，看不出偏差是不是只剩量化+压缩。
    /// </summary>
    public static float DenominatorQuantSteps(Row row)
    {
        Vector3 enc = Oetf(row.Denominator);
        float worst = 0;
        for (int c = 0; c < 3; c++)
        {
            worst = Math.Max(worst, Math.Abs(Get(enc, c) - Get(row.Base8, c)));
        }
        return worst;
    }

    /// <summary>分母节点相对「文件里的 base」的最大线性相对偏差（暗部按 0.05 兜底）。</summary>
    public static float DenominatorDeviation(Row row)
    {
        float worst = 0;
        for (int c = 0; c < 3; c++)
        {
            float a = Get(row.Denominator, c);
            float b = Get(row.StoredLinear, c);
            float scale = Math.Max(0.05f, Math.Max(Math.Abs(a), Math.Abs(b)));
            worst = Math.Max(worst, Math.Abs(a - b) / scale);
        }
        return worst;
    }

    public static string FmtBase(Vector3 v) => $"({v.X * 255:F0},{v.Y * 255:F0},{v.Z * 255:F0})";

    public static int RunAssertions(List<Row> rows, List<string> diag, Action<string> log)
    {
        int failures = 0;
        void Check(string name, bool ok, string detail)
        {
            if (!ok)
            {
                failures++;
            }
            log($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}");
        }

        float lumaSum = UhdrColor.WorkingLuma.X + UhdrColor.WorkingLuma.Y + UhdrColor.WorkingLuma.Z;
        Check(
            "亮度系数取到的是 XYZ 的 Y 行",
            Math.Abs(lumaSum - 1f) < 1e-3f && UhdrColor.WorkingLuma.Y > UhdrColor.WorkingLuma.X,
            $"luma=({UhdrColor.WorkingLuma.X:F6},{UhdrColor.WorkingLuma.Y:F6},{UhdrColor.WorkingLuma.Z:F6}) sum={lumaSum:F6}"
        );

        // 取样口径自检：同一色块内 5 个点必须一致，否则后面所有数字都不作数。
        Check(
            "解码取样自洽",
            rows.TrueForAll(r => r.Spread < 0.02f),
            $"最大块内偏差={rows.Max(r => r.Spread):F4}  {string.Join(" | ", diag)}"
        );

        foreach (float sdrWhiteLevel in new[] { 80f, 320f })
        {
            List<Row> at = [.. rows.Where(r => r.SdrWhiteLevel == sdrWhiteLevel)];
            Row? One(string arm, string name) =>
                at.Find(r => r.Arm == arm && r.Name == name);
            Row Need(string arm, string name) =>
                One(arm, name)
                ?? throw new InvalidOperationException(
                    $"缺少 {arm}/{name} @ {sdrWhiteLevel}nits 的数据行，测试台没有可比数据"
                );
            bool hasOld = at.Exists(r => r.Arm == "old");
            void Skip(string name, string detail) => log($"SKIP  {name}  {detail}");
            if (!hasOld)
            {
                Skip(
                    $"[{sdrWhiteLevel:F0}] 依赖「改前」基线的对比项",
                    "改前实现在该白电平下无法编码出文件（见上方 !! 行），无基线可比"
                );
            }

            // 1. 中性色不许偏色
            foreach (string neutral in new[] { "neutral gray 18%", "SDR white", "near black" })
            {
                Row n = Need("new", neutral);
                Check(
                    $"[{sdrWhiteLevel:F0}] 灰阶无偏色 {neutral}",
                    n.MaxRelError < 0.05f,
                    $"maxRelErr={n.MaxRelError:P2} ref={Fmt(n.RefNits)} rec={Fmt(n.ReconNits)}"
                );
            }

            // 1b. 当作增益分母的那个浮点节点，必须等于文件里真正存着的 base。
            //     只看没被 8bit 截断的像素：被截断的那些正是改后要靠增益补回来的对象。
            {
                List<Row> n = [.. at.Where(r => r.Arm == "new" && MaxChannel(r.StoredLinear) < 0.985f)];
                float worstNew = n.Max(DenominatorQuantSteps);
                Check(
                    $"[{sdrWhiteLevel:F0}] 增益分母=文件里的 base（未截断像素）",
                    n.Count >= 2 && worstNew <= 4f / 255f,
                    $"参与比对 {n.Count} 块，最大偏差={worstNew * 255f:F1}/255（8bit 量化+JPEG 级别）"
                );
                if (hasOld)
                {
                    List<Row> o = [.. at.Where(r => r.Arm == "old")];
                    float worstOld = o.Max(DenominatorDeviation);
                    float worstNewRel = n.Max(DenominatorDeviation);
                    Check(
                        $"[{sdrWhiteLevel:F0}] 改前分母确实取错了节点",
                        worstOld > worstNewRel,
                        $"old 线性最大偏差={worstOld:P1} vs new {worstNewRel:P1}"
                    );
                }
            }

            // 1c. 端到端：文件实际乘出来的 gain 必须等于「分子节点 / 文件里的 base」。
            //     分母取错节点时这条必然不成立（高光通道被截断后 gain 停在 1，余量丢失）。
            {
                List<Row> n = [.. at.Where(r => r.Arm == "new" && MaxChannel(r.StoredLinear) > 0.02f)];
                float worst = 0;
                string worstName = "";
                foreach (Row r in n)
                {
                    float expected = ExpectedGain(r.Numerator, r.StoredLinear);
                    float applied = AppliedGain(
                        r.Numerator,
                        r.ReconNits / sdrWhiteLevel,
                        r.StoredLinear
                    );
                    float dev = float.IsNaN(expected) || expected < 1e-4f
                        ? 0
                        : Math.Abs(applied / expected - 1f);
                    if (dev > worst)
                    {
                        worst = dev;
                        worstName = r.Name;
                    }
                }
                Check(
                    $"[{sdrWhiteLevel:F0}] 实测 gain = 分子/文件里的 base",
                    worst < 0.05f,
                    $"最大偏离={worst:P2}（{worstName}），参与 {n.Count} 块"
                );
            }

            // 2. 域内颜色保持不变
            foreach (
                string inGamut in new[]
                {
                    "BT.709 red @120nits",
                    "BT.709 green @160nits",
                    "BT.709 blue @200nits",
                }
            )
            {
                Row n = Need("new", inGamut);
                if (!hasOld)
                {
                    Skip($"[{sdrWhiteLevel:F0}] 域内色不被改动 {inGamut}", "无 old 基线");
                    continue;
                }
                Row o = Need("old", inGamut);
                Check(
                    $"[{sdrWhiteLevel:F0}] 域内色不被改动 {inGamut}",
                    n.MaxRelError < 0.08f && n.MaxRelError <= o.MaxRelError + 0.02f,
                    $"maxRelErr old={o.MaxRelError:P2} new={n.MaxRelError:P2}"
                );
            }

            // 3. 广色域红色的饱和度保留必须优于旧实现
            foreach (
                string red in new[]
                {
                    "P3 red (out of 709)",
                    "bright HDR red @640nits",
                    "BT.2020 red (out of P3)",
                }
            )
            {
                Row n = Need("new", red);
                if (!hasOld)
                {
                    Skip($"[{sdrWhiteLevel:F0}] 红色饱和度保留 {red}", "无 old 基线");
                    continue;
                }
                Row o = Need("old", red);
                Check(
                    $"[{sdrWhiteLevel:F0}] 红色饱和度保留 {red}",
                    n.RecChroma > o.RecChroma + 1e-4f,
                    $"重建 chroma old={o.RecChroma:F4} new={n.RecChroma:F4}（目标 {n.RefChroma:F4}）"
                );
            }

            // 3b. 绝对判据：改后重建出来的饱和度必须贴住改前工作色域的目标值，
            //     这条不依赖 old 基线，old 编不出文件时照样能判红。
            foreach (
                string red in new[]
                {
                    "P3 red (out of 709)",
                    "bright HDR red @640nits",
                    "BT.2020 red (out of P3)",
                }
            )
            {
                Row n = Need("new", red);
                Check(
                    $"[{sdrWhiteLevel:F0}] 重建 chroma 达标 {red}",
                    n.RecChroma > 0.97f * n.RefChroma,
                    $"new={n.RecChroma:F4} 目标={n.RefChroma:F4}"
                );
            }

            // 4. 绿/蓝不劣化
            foreach (
                string gb in new[]
                {
                    "P3 green (out of 709)",
                    "BT.709 green @160nits",
                    "BT.709 blue @200nits",
                }
            )
            {
                Row n = Need("new", gb);
                if (!hasOld)
                {
                    Skip($"[{sdrWhiteLevel:F0}] 绿/蓝不劣化 {gb}", "无 old 基线");
                    continue;
                }
                Row o = Need("old", gb);
                Check(
                    $"[{sdrWhiteLevel:F0}] 绿/蓝不劣化 {gb}",
                    n.MaxRelError <= o.MaxRelError + 0.02f,
                    $"maxRelErr old={o.MaxRelError:P2} new={n.MaxRelError:P2}"
                );
            }

            // 5. 高光不被压暗
            {
                Row n = Need("new", "bright HDR red @640nits");
                Check(
                    $"[{sdrWhiteLevel:F0}] 高光未压暗",
                    n.ReconNits.X >= 0.9f * n.RefNits.X,
                    $"R rec={n.ReconNits.X:F1}nits ref={n.RefNits.X:F1}nits"
                );
            }

            // 6. 数值健康：无 NaN/Inf；逐通道增益允许 <1（tone map 把饱和色推开时要把别的通道乘回来），
            //    但必须是有限正数，且 capacity max 不能爆到不合理的量级。
            Check(
                $"[{sdrWhiteLevel:F0}] 数值健康",
                at.TrueForAll(r => r.Finite)
                && at.Min(r => r.MinBoost) > 0f
                && at.Max(r => r.MaxBoost) is > 1f and < 1e4f,
                $"minBoost={at.Min(r => r.MinBoost):F4} capacityMax={at.Max(r => r.MaxBoost):F2}"
            );

            // 7. SDR base 仍是一张正常的 SDR 图：数值在 [0,1]、中性保持中性、亮度单调
            {
                Row white = Need("new", "SDR white");
                Row gray = Need("new", "neutral gray 18%");
                Row black = Need("new", "near black");
                bool inRange = at
                    .Where(r => r.Arm == "new")
                    .All(r =>
                        r.Base8.X is >= -0.004f and <= 1.004f
                        && r.Base8.Y is >= -0.004f and <= 1.004f
                        && r.Base8.Z is >= -0.004f and <= 1.004f
                    );
                bool neutralOk =
                    MathF.Max(
                        MathF.Abs(white.Base8.X - white.Base8.Y),
                        MathF.Abs(white.Base8.Y - white.Base8.Z)
                    )
                    < 0.02f
                    && MathF.Abs(gray.Base8.X - gray.Base8.Y) < 0.02f;
                bool monotonic = white.Base8.X > gray.Base8.X && gray.Base8.X > black.Base8.X;
                Check(
                    $"[{sdrWhiteLevel:F0}] SDR base 合理",
                    inRange && neutralOk && monotonic && white.Base8.X > 0.5f,
                    $"base(white)={FmtBase(white.Base8)} base(gray)={FmtBase(gray.Base8)} 数值在[0,1]={inRange} 中性={neutralOk} 单调={monotonic}"
                );
            }

            // 8. 容器标记与像素一致：base 由 libultrahdr 合成 P3 ICC，两张子 JPEG 自身不带 ICC
            {
                Row n = Need("new", "SDR white");
                string oldState = hasOld
                    ? $"old {Need("old", "SDR white").IccText}"
                    : "old 无文件";
                Check(
                    $"[{sdrWhiteLevel:F0}] base 的 ICC 标签就是工作色域 P3（读文件字节）",
                    n.IccId == ColorPrimaries.DisplayP3.Id && n.BaseJpeg.Contains("icc=yes"),
                    $"new {n.IccText} base[{n.BaseJpeg}]"
                );
                if (hasOld)
                {
                    // 解析器自检：改前那份是标准 sRGB profile，必须被认成 BT.709。
                    // 认对了，上面那条「认成 P3」才有意义。
                    Row o = Need("old", "SDR white");
                    Check(
                        $"[{sdrWhiteLevel:F0}] 同一解析器读改前的 ICC = BT.709",
                        o.IccId == ColorPrimaries.BT709.Id,
                        $"old {o.IccText}"
                    );
                }
                else
                {
                    Skip($"[{sdrWhiteLevel:F0}] ICC 解析器自检（old=BT.709）", "无 old 文件");
                }
                Check(
                    $"[{sdrWhiteLevel:F0}] gain map 无 ICC 且 4:4:4",
                    !n.GainmapJpeg.Contains("icc=yes") && n.GainmapJpeg.Contains("4:4:4"),
                    n.GainmapJpeg
                );
                Check(
                    $"[{sdrWhiteLevel:F0}] base 为 4:2:0（Ultra HDR 惯例，显式指定）",
                    n.BaseJpeg.Contains("4:2:0"),
                    $"new base[{n.BaseJpeg}]（改前用 WIC 默认参数，未显式指定）  {oldState}"
                );
            }
        }
        return failures;
    }
}

sealed record Swatch(string Name, Vector3 ScRgb);

sealed record PatchPoint(int X, int Y, string Name, Vector3 ScRgb);

sealed class Row
{
    public required string Name;
    public required string Arm;
    public required float SdrWhiteLevel;
    public required Vector3 Input;
    public required Vector3 Working;
    public required Vector3 Base8;
    public required Vector3 ReconNits;
    public required Vector3 RefNits;
    public required float MaxRelError;
    public required float RefChroma;
    public required float RecChroma;
    public required Vector3 Denominator;
    public required Vector3 Numerator;
    public required Vector3 StoredLinear;
    public required bool Finite;
    public required float Spread;
    public required float MinBoost;
    public required float MaxBoost;
    public required int IccId;
    public required string IccText;
    public required string BaseJpeg;
    public required string GainmapJpeg;
}
