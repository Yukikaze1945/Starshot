using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

/// <summary>
/// 直接读文件里那份 ICC 的 rXYZ/gXYZ/bXYZ 标签，算出它声称的三原色色度。
/// 不依赖 Starward 的解析函数（它对 libultrahdr 合成的 profile 返回 id=0），
/// 也不依赖任何声明：这就是「ICC 到底标了什么」的物证。
/// </summary>
internal static class IccProbe
{
    /// <summary>ICC 的标签名/类型都是 4 字节 ASCII，直接按字符拼，不走 Encoding（避免任何规范化）。</summary>
    private static string Ascii(ReadOnlySpan<byte> b, int at, int len)
    {
        if (at < 0 || at + len > b.Length)
        {
            return "?";
        }
        var chars = new char[len];
        for (int i = 0; i < len; i++)
        {
            chars[i] = (char)b[at + i];
        }
        return new string(chars);
    }

    /// <summary>
    /// profile 头部 0..4 是整条 profile 的长度，而 GetIccData 的返回值前面还挂着 APP2 的段头。
    /// 用「长度字段 == 剩余字节数」定位本体起点，比按固定偏移猜可靠。
    /// </summary>
    private static int FindProfileStart(ReadOnlySpan<byte> icc)
    {
        for (int start = 0; start + 132 <= icc.Length; start++)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(icc[start..]) == (uint)(icc.Length - start))
            {
                return start;
            }
        }
        return -1;
    }

    /// <summary>返回 (定位到的起点, 三原色与白点的 xy, 说明)。定位失败时 xy 为 null。</summary>
    public static (int Start, (float X, float Y)[]? Primaries, string Detail) Parse(
        ReadOnlySpan<byte> icc
    )
    {
        int start = FindProfileStart(icc);
        if (start < 0)
        {
            return (-1, null, "找不到 profile 本体（长度字段不匹配）");
        }
        ReadOnlySpan<byte> p = icc[start..];
        string head40 = Convert.ToHexString(p[..Math.Min(40, p.Length)]);
        // ICC 头：0 尺寸 / 4 首选 CMM / 8 版本 / 12 设备类型 / 16 数据色空间 / 20 PCS
        if (p.Length < 132 || Ascii(p, 16, 3) != "RGB")
        {
            return (
                start,
                null,
                $"起点 {start}，设备类型={Ascii(p, 12, 4)} 数据色空间={Ascii(p, 16, 4)}，不是 RGB: {head40}"
            );
        }
        uint tagCount = Read(p, 128);
        if (tagCount is 0 or > 200)
        {
            return (start, null, $"起点 {start}，tag 数量 {tagCount} 不合理");
        }
        var found = new System.Collections.Generic.Dictionary<string, uint>();
        for (int i = 0; i < tagCount; i++)
        {
            int entry = 132 + i * 12;
            if (entry + 12 > p.Length)
            {
                break;
            }
            found[Ascii(p, entry, 4)] = Read(p, entry + 4);
        }
        var pts = new (float X, float Y)[4];
        var text = new StringBuilder(
            $"起点 {start}, class={Ascii(p, 12, 4)}, pcs={Ascii(p, 20, 4)}, tagCount {tagCount}:"
        );
        string[] tags = ["wtpt", "rXYZ", "gXYZ", "bXYZ"];
        for (int t = 0; t < tags.Length; t++)
        {
            if (!found.TryGetValue(tags[t], out uint off) || off + 20 > p.Length)
            {
                return (start, null, $"{text} 缺 {tags[t]}");
            }
            if (Ascii(p, (int)off, 4) != "XYZ ")
            {
                return (start, null, $"{text} {tags[t]} 类型不是 XYZ ");
            }
            // XYZtype: 4 字节类型 + 4 字节保留 + 3 个 s15Fixed16
            float X = Fixed(p, (int)off + 8);
            float Y = Fixed(p, (int)off + 12);
            float Z = Fixed(p, (int)off + 16);
            float s = X + Y + Z;
            pts[t] = s == 0 ? (0, 0) : (X / s, Y / s);
            text.Append($" {tags[t]}=({pts[t].X:F4},{pts[t].Y:F4})");
        }
        return (start, pts, text.ToString());
    }

    private static uint Read(ReadOnlySpan<byte> b, int at) =>
        BinaryPrimitives.ReadUInt32BigEndian(b[at..]);

    private static float Fixed(ReadOnlySpan<byte> b, int at) =>
        BinaryPrimitives.ReadInt32BigEndian(b[at..]) / 65536f;

    /// <summary>解出来的色度与某组已知三原色的最大偏差（绝对坐标差）。profile 里的定点数精度约 1e-4。</summary>
    public static float MaxDeviation((float X, float Y)[] pts, (float X, float Y)[] want)
    {
        float worst = 0;
        for (int i = 0; i < 3; i++)
        {
            worst = Math.Max(
                worst,
                Math.Max(Math.Abs(pts[i + 1].X - want[i].X), Math.Abs(pts[i + 1].Y - want[i].Y))
            );
        }
        return worst;
    }

    /// <summary>
    /// BT.709 / Display-P3 / BT.2020 三原色在 ICC 里的形态：公布的 D65 色度经 Bradford 适配到 D50。
    /// profile 的 PCS 白点是 D50（wtpt 就是这个值），rXYZ/gXYZ/bXYZ 也跟着是 D50 口径，
    /// 拿公布的 D65 色度直接比必然对不上——先适配再比，才能证明「标签就是这组原色」。
    /// </summary>
    public static (float X, float Y)[] Primaries(string which)
    {
        (float X, float Y)[] d65 =
            which switch
            {
                "BT709" =>
                [
                    (0.640f, 0.330f),
                    (0.300f, 0.600f),
                    (0.150f, 0.060f),
                ],
                "P3" =>
                [
                    (0.680f, 0.320f),
                    (0.265f, 0.690f),
                    (0.150f, 0.060f),
                ],
                _ =>
                [
                    (0.708f, 0.295f),
                    (0.170f, 0.797f),
                    (0.131f, 0.046f),
                ],
            };
        return Array.ConvertAll(d65, p => AdaptToD50(p.X, p.Y));
    }

    /// <summary>
    /// ICC 通用的 D65→D50 Bradford 适配矩阵（PCS 白点固定 D50，所以 profile 里的原色都是这个口径）。
    /// 用它把公布的 D65 色度换算一次，就能和文件里的 rXYZ/gXYZ/bXYZ 逐值对齐；
    /// 换完能对上，说明标签写的确实是那组原色，而不是「数值凑巧看着像」。
    /// </summary>
    private static readonly float[] D65ToD50 =
    [
        1.04792982f, 0.02294679f, -0.05019223f,
        0.02962781f, 0.99043449f, -0.01707382f,
        -0.00921128f, 0.01504015f, 0.75187428f,
    ];

    private static (float X, float Y) AdaptToD50(float x, float y)
    {
        // 同一色度方向的任意 XYZ 缩放都行：适配是线性映射，方向不变。
        float[] v = [x / y, 1f, (1f - x - y) / y];
        float[] o =
        [
            D65ToD50[0] * v[0] + D65ToD50[1] * v[1] + D65ToD50[2] * v[2],
            D65ToD50[3] * v[0] + D65ToD50[4] * v[1] + D65ToD50[5] * v[2],
            D65ToD50[6] * v[0] + D65ToD50[7] * v[1] + D65ToD50[8] * v[2],
        ];
        float s = o[0] + o[1] + o[2];
        return (o[0] / s, o[1] / s);
    }

    public static string Fmt((float X, float Y)[]? pts) =>
        pts is null
            ? "n/a"
            : string.Join(
                " ",
                Array.ConvertAll(pts, p => $"({p.X.ToString("F4", CultureInfo.InvariantCulture)},{p.Y.ToString("F4", CultureInfo.InvariantCulture)})")
            );
}
