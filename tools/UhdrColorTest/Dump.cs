using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Starshot.Features.Codec;
using Starward.Codec.UltraHdr;
using Windows.Graphics.DirectX;

/// <summary>
/// 定位用：把「源位图 → 工作色域渲染 → 解码回读」三级在同一批坐标上打成文本，
/// 哪一级开始串位就一目了然。Program 传 dump 参数时调用。
/// </summary>
internal static class Dump
{
    public static void Run(CanvasDevice device, string path)
    {
        var lines = new List<string>();
        void L(string s)
        {
            lines.Add(s);
            Console.WriteLine(s);
        }

        using CanvasBitmap src = Harness.MakePatchImage(device);
        L($"src {src.SizeInPixels.Width}x{src.SizeInPixels.Height} fmt={src.Format} dpi={src.Dpi}");

        byte[] srcBytes = src.GetPixelBytes();
        var srcHalf = MemoryMarshal.Cast<byte, Half>(srcBytes);
        L($"src bytes={srcBytes.Length} halfs={srcHalf.Length}");

        var workings = Harness.MeasureWorkings(device, src);

        byte[] file = Harness.EncodeNew(src, 80f);
        using var decoder = new UhdrDecoder();
        decoder.SetImage(file);
        decoder.SetOutImagePixelFormat(UhdrPixelFormat._32bppRGBA8888);
        decoder.SetOutColorTransfer(UhdrColorTransfer.SRGB);
        decoder.Decode();
        UhdrRawImage img = decoder.GetDecodedImage();
        L(
            $"decoded {img.PixelFormat} {img.Width}x{img.Height} stride={img.Stride[0]} plane={img.Plane[0]:X}"
        );

        L("");
        L(
            "patch                          want(scRGB)            srcPx(float)             working                decoded8"
        );
        foreach (PatchPoint p in Harness.Centers())
        {
            int o = (p.Y * (int)src.SizeInPixels.Width + p.X) * 4;
            Vector3 s = o + 2 < srcHalf.Length
                ? new Vector3(
                    (float)srcHalf[o],
                    (float)srcHalf[o + 1],
                    (float)srcHalf[o + 2]
                )
                : new Vector3(float.NaN, float.NaN, float.NaN);
            IntPtr origin = img.Plane[0] + (p.Y * (int)img.Stride[0] + p.X) * 4;
            byte[] b = new byte[4];
            Marshal.Copy(origin, b, 0, 4);
            L(
                $"{p.Name,-32} {Harness.Fmt(p.ScRgb),-24} {Harness.Fmt(s),-24} {Harness.Fmt(workings[p.Name]),-24} ({b[0],3},{b[1],3},{b[2],3})"
            );
        }

        L("");
        L("first 3 rows x first 12 cols of src (R channel):");
        int w = (int)src.SizeInPixels.Width;
        for (int y = 0; y < 3; y++)
        {
            var cells = new List<string>();
            for (int x = 0; x < 12; x++)
            {
                cells.Add(((float)srcHalf[(y * w + x) * 4]).ToString("F3"));
            }
            L($"  y={y}: " + string.Join(" ", cells));
        }
        File.WriteAllLines(path, lines);
        Console.WriteLine(string.Join(Environment.NewLine, lines));
    }
}
