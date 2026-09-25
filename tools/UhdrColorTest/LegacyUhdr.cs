using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ComputeSharp;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI;
using Starward.Codec;
using Starward.Codec.UltraHdr;
using Windows.Graphics.DirectX;
using Windows.Storage.Streams;

namespace Starshot.Features.Codec;

/// <summary>
/// 修改前的 Ultra HDR 编码实现，原样从 git HEAD 的 ImageSaver.SaveAsUhdrAsync 搬来，
/// 只给测试台当 A/B 基线用，不参与生产路径。
/// </summary>
internal static class LegacyUhdr
{
    public static async Task Encode(
        CanvasBitmap canvasImage,
        Stream stream,
        float maxCLL,
        float sdrWhiteLevel
    )
    {
        if (canvasImage.Format is DirectXPixelFormat.R16G16B16A16Float)
        {
            await Task.Delay(1).ConfigureAwait(false);
            using HdrToneMapEffect toneMapEffect = new()
            {
                Source = canvasImage,
                InputMaxLuminance = maxCLL,
                OutputMaxLuminance = sdrWhiteLevel,
                DisplayMode = HdrToneMapEffectDisplayMode.Hdr,
                BufferPrecision = CanvasBufferPrecision.Precision16Float,
            };
            using WhiteLevelAdjustmentEffect whiteLevelEffect = new()
            {
                Source = toneMapEffect,
                InputWhiteLevel = 80,
                OutputWhiteLevel = sdrWhiteLevel,
                BufferPrecision = CanvasBufferPrecision.Precision16Float,
            };
            using SrgbGammaEffect gammaEffect = new()
            {
                Source = whiteLevelEffect,
                GammaMode = SrgbGammaMode.OETF,
                BufferPrecision = CanvasBufferPrecision.Precision16Float,
            };
            using UhdrPixelGainEffect uhdrPixelGainEffect = new()
            {
                SdrSource = toneMapEffect,
                HdrSource = canvasImage,
            };

            using CanvasRenderTarget renderTarget_gain = new(
                CanvasDevice.GetSharedDevice(),
                canvasImage.SizeInPixels.Width,
                canvasImage.SizeInPixels.Height,
                96,
                DirectXPixelFormat.R32G32B32A32Float,
                CanvasAlphaMode.Premultiplied
            );
            using (CanvasDrawingSession ds = renderTarget_gain.CreateDrawingSession())
            {
                ds.Units = CanvasUnits.Pixels;
                ds.Clear(Colors.Transparent);
                ds.DrawImage(uhdrPixelGainEffect);
            }
            byte[] gainPixelBytes = renderTarget_gain.GetPixelBytes();
            float[] contentBoost = ImageSaver.GetContentMinMaxBoost(gainPixelBytes);

            using UhdrGainmapEffect uhdrGainmapEffect = new()
            {
                PixelGainSource = renderTarget_gain,
                MinContentBoost = MemoryMarshal.Cast<float, float3>(contentBoost)[0],
                MaxContentBoost = MemoryMarshal.Cast<float, float3>(contentBoost)[1],
            };
            using CanvasRenderTarget renderTarget_gainmap = new(
                CanvasDevice.GetSharedDevice(),
                canvasImage.SizeInPixels.Width,
                canvasImage.SizeInPixels.Height,
                96,
                DirectXPixelFormat.R8G8B8A8UIntNormalized,
                CanvasAlphaMode.Premultiplied
            );
            using (CanvasDrawingSession ds = renderTarget_gainmap.CreateDrawingSession())
            {
                ds.Units = CanvasUnits.Pixels;
                ds.Clear(Colors.Transparent);
                ds.DrawImage(uhdrGainmapEffect);
            }

            using CanvasRenderTarget renderTarget_sdr = new(
                CanvasDevice.GetSharedDevice(),
                canvasImage.SizeInPixels.Width,
                canvasImage.SizeInPixels.Height,
                96,
                DirectXPixelFormat.R8G8B8A8UIntNormalized,
                CanvasAlphaMode.Premultiplied
            );
            using (CanvasDrawingSession ds = renderTarget_sdr.CreateDrawingSession())
            {
                ds.Units = CanvasUnits.Pixels;
                ds.Clear(Colors.Transparent);
                ds.DrawImage(gammaEffect);
            }

            using MemoryStream ms_base = new();
            using MemoryStream ms_gainmap = new();
            await renderTarget_sdr.SaveAsync(
                ms_base.AsRandomAccessStream(),
                CanvasBitmapFileFormat.Jpeg
            );
            await renderTarget_gainmap.SaveAsync(
                ms_gainmap.AsRandomAccessStream(),
                CanvasBitmapFileFormat.Jpeg
            );

            byte[] baseArray = ms_base.ToArray();
            byte[] gainArray = ms_gainmap.ToArray();

            using var encoder = new UhdrEncoder();
            unsafe
            {
                fixed (
                    byte* b = baseArray,
                        g = gainArray
                )
                {
                    UhdrCompressedImage baseImage = new UhdrCompressedImage
                    {
                        Data = (nint)b,
                        DataSize = (uint)baseArray.Length,
                        Capacity = (uint)baseArray.Length,
                        ColorGamut = UhdrColorGamut.BT709,
                        ColorRange = UhdrColorRange.FullRange,
                        ColorTransfer = UhdrColorTransfer.SRGB,
                    };
                    UhdrCompressedImage gainmapImage = new UhdrCompressedImage
                    {
                        Data = (nint)g,
                        DataSize = (uint)gainArray.Length,
                        Capacity = (uint)gainArray.Length,
                        ColorGamut = UhdrColorGamut.BT709,
                        ColorRange = UhdrColorRange.FullRange,
                        ColorTransfer = UhdrColorTransfer.SRGB,
                    };
                    encoder.SetCompressedImage(baseImage, UhdrImageLabel.Base);
                    UhdrGainmapMetadata metadata = new UhdrGainmapMetadata
                    {
                        Gamma = new FixedArray3<float>(1),
                        OffsetSdr = new FixedArray3<float>(0.015625f),
                        OffsetHdr = new FixedArray3<float>(0.015625f),
                        HdrCapacityMin = 1,
                        HdrCapacityMax = MathF.Max(
                            MathF.Max(contentBoost[3], contentBoost[4]),
                            MathF.Max(contentBoost[5], 1)
                        ),
                        UseBaseColorSpace = 1,
                    };
                    metadata.MinContentBoost[0] = contentBoost[0];
                    metadata.MinContentBoost[1] = contentBoost[1];
                    metadata.MinContentBoost[2] = contentBoost[2];
                    metadata.MaxContentBoost[0] = contentBoost[3];
                    metadata.MaxContentBoost[1] = contentBoost[4];
                    metadata.MaxContentBoost[2] = contentBoost[5];
                    encoder.SetGainmapImage(gainmapImage, metadata);
                }
            }
            encoder.Encode();
            stream.Write(encoder.GetEncodedBytes());
        }
    }
}
