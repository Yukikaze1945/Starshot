using System;
using System.Text;

/// <summary>
/// 只读 JPEG 段头：报出有没有 ICC（APP2 + "ICC_PROFILE"）以及 SOF 里的色度抽样。
/// 用来直接证明容器标记，不靠推测。
/// </summary>
internal static class JpegProbe
{
    public static string Describe(byte[] jpeg)
    {
        bool icc = false;
        string subsampling = "n/a";
        int pos = 2;
        while (pos + 3 < jpeg.Length && jpeg[pos] == 0xFF)
        {
            byte marker = jpeg[pos + 1];
            if (marker == 0xFF)
            {
                pos++;
                continue;
            }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD9))
            {
                pos += 2;
                continue;
            }
            int segmentLength = ((jpeg[pos + 2] << 8) | jpeg[pos + 3]) + 2;
            if (segmentLength > jpeg.Length - pos)
            {
                break;
            }
            if (
                marker == 0xE2
                && segmentLength >= 16
                && jpeg.AsSpan(pos + 4, 12).SequenceEqual("ICC_PROFILE\0"u8)
            )
            {
                icc = true;
            }
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                subsampling = DescribeSof(jpeg, pos, segmentLength);
            }
            pos += segmentLength;
            if (marker == 0xDA)
            {
                break;
            }
        }
        return $"icc={(icc ? "yes" : "no")} subsampling={subsampling}";
    }

    /// <summary>
    /// 把子 JPEG 里所有 APP2 "ICC_PROFILE" 段拼回来（去掉 12 字节签名和 1 字节序号）。
    /// 这是「文件字节本身写了什么」的第一手物证，不经过任何解码器解释。
    /// </summary>
    public static byte[] ExtractIcc(byte[] jpeg)
    {
        var parts = new System.Collections.Generic.SortedDictionary<int, byte[]>();
        int pos = 2;
        while (pos + 3 < jpeg.Length && jpeg[pos] == 0xFF)
        {
            byte marker = jpeg[pos + 1];
            if (marker == 0xFF)
            {
                pos++;
                continue;
            }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD9))
            {
                pos += 2;
                continue;
            }
            int segmentLength = ((jpeg[pos + 2] << 8) | jpeg[pos + 3]) + 2;
            if (segmentLength > jpeg.Length - pos)
            {
                break;
            }
            if (
                marker == 0xE2
                && segmentLength >= 16
                && jpeg.AsSpan(pos + 4, 12).SequenceEqual("ICC_PROFILE\0"u8)
            )
            {
                int body = pos + 4 + 12;
                int seq = jpeg[body];
                body++;
                parts[seq] = jpeg.AsSpan(body, pos + segmentLength - body).ToArray();
            }
            pos += segmentLength;
            if (marker == 0xDA)
            {
                break;
            }
        }
        if (parts.Count == 0)
        {
            return [];
        }
        int total = 0;
        foreach (byte[] b in parts.Values)
        {
            total += b.Length;
        }
        byte[] icc = new byte[total];
        int at = 0;
        foreach (byte[] b in parts.Values)
        {
            b.CopyTo(icc, at);
            at += b.Length;
        }
        return icc;
    }

    private static string DescribeSof(byte[] jpeg, int pos, int segmentLength)
    {
        // SOF: FF Cx, len(2), precision(1), height(2), width(2), ncomp(1), 每分量 id/抽样/量化表
        int components = jpeg[pos + 9];
        if (components < 1 || segmentLength < 10 + components * 3)
        {
            return "?";
        }
        var sb = new StringBuilder();
        int maxH = 1;
        int maxV = 1;
        for (int i = 0; i < components; i++)
        {
            int sampling = jpeg[pos + 11 + i * 3];
            int h = sampling >> 4;
            int v = sampling & 0xF;
            maxH = Math.Max(maxH, h);
            maxV = Math.Max(maxV, v);
            sb.Append($"{h}x{v}");
            if (i + 1 < components)
            {
                sb.Append(',');
            }
        }
        string label = (maxH, maxV) switch
        {
            (1, 1) => "4:4:4",
            (2, 2) => "4:2:0",
            (2, 1) => "4:2:2",
            (4, 2) => "4:1:1",
            _ => "other",
        };
        return $"{label}({sb})";
    }
}
