using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI;
using Starward.Codec;
using Starward.Codec.AVIF;
using Starward.Codec.ICC;
using Starward.Codec.JpegXL;
using Starward.Codec.JpegXL.CMS;
using Starward.Codec.JpegXL.CodeStream;
using Starward.Codec.JpegXL.Encode;
using Starward.Codec.PNG;
using Starward.Codec.UltraHdr;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;

namespace Starshot.Features.Codec;

internal static class ImageSaver
{
    private static int GetSuggestedThreads()
    {
        int threads = Environment.ProcessorCount;
        if (threads >= 16)
        {
            return threads - 4;
        }
        else if (threads >= 8)
        {
            return threads - 2;
        }
        else
        {
            return threads;
        }
    }

    // WICJpegYCrCbSubsamplingOption（Windows SDK wincodec.h）
    private const byte JpegSubsampling420 = 1;
    private const byte JpegSubsampling444 = 3;

    /// <summary>libultrahdr 自带编码器的默认质量：kBaseCompressQualityDefault / kMapCompressQualityDefault。</summary>
    private const int UhdrJpegQuality = 95;

    /// <summary>
    /// 8bit 位图存普通 JPEG（WIC，quality 0-100）。输入需已 SDR（HDR 先过 tonemap）。
    /// subsampling 取 WICJpegYCrCbSubsamplingOption：1=4:2:0、2=4:2:2、3=4:4:4。
    /// </summary>
    public static async Task SaveAsJpegAsync(
        CanvasBitmap bitmap,
        Stream stream,
        int quality,
        byte subsampling = JpegSubsampling444
    )
    {
        if (
            bitmap.Format
            is not (
                DirectXPixelFormat.R8G8B8A8UIntNormalized
                or DirectXPixelFormat.B8G8R8A8UIntNormalized
            )
        )
        {
            throw new NotSupportedException(
                $"{bitmap.Format} is not supported for JPEG encoding; tone-map to 8bit first."
            );
        }
        using var ms = new MemoryStream();
        var options = new BitmapPropertySet();
        options.Add(
            "ImageQuality",
            new BitmapTypedValue(
                Math.Clamp(quality, 0, 100) / 100f,
                Windows.Foundation.PropertyType.Single
            )
        );
        options.Add(
            "JpegYCrCbSubsampling",
            new BitmapTypedValue(subsampling, Windows.Foundation.PropertyType.UInt8)
        );
        var encoder = await BitmapEncoder.CreateAsync(
            BitmapEncoder.JpegEncoderId,
            ms.AsRandomAccessStream(),
            options
        );
        encoder.SetPixelData(
            bitmap.Format is DirectXPixelFormat.B8G8R8A8UIntNormalized
                ? BitmapPixelFormat.Bgra8
                : BitmapPixelFormat.Rgba8,
            BitmapAlphaMode.Premultiplied,
            bitmap.SizeInPixels.Width,
            bitmap.SizeInPixels.Height,
            96,
            96,
            bitmap.GetPixelBytes()
        );
        await encoder.FlushAsync();
        ms.Position = 0;
        await ms.CopyToAsync(stream);
    }

    public static async Task SaveAsPngAsync(
        CanvasBitmap bitmap,
        Stream stream,
        ColorPrimaries colorPrimaries,
        byte[]? xmpData = null,
        bool writeColorProfile = true,
        float? maxCLL = null,
        float? maxFALL = null
    )
    {
        uint width = bitmap.SizeInPixels.Width;
        uint height = bitmap.SizeInPixels.Height;

        if (
            bitmap.Format
            is DirectXPixelFormat.R8G8B8A8UIntNormalized
                or DirectXPixelFormat.B8G8R8A8UIntNormalized
        )
        {
            byte[] pixelBytes = bitmap.GetPixelBytes();
            await SaveAsPngAsync(
                    stream,
                    width,
                    height,
                    bitmap.Format,
                    pixelBytes,
                    colorPrimaries,
                    xmpData,
                    writeColorProfile
                )
                .ConfigureAwait(false);
        }
        else if (
            bitmap.Format
            is DirectXPixelFormat.R16G16B16A16Float
                or DirectXPixelFormat.R32G32B32A32Float
        )
        {
            using var renderTarget = new CanvasRenderTarget(
                CanvasDevice.GetSharedDevice(),
                width,
                height,
                96,
                DirectXPixelFormat.R16G16B16A16UIntNormalized,
                CanvasAlphaMode.Premultiplied
            );
            using (var ds = renderTarget.CreateDrawingSession())
            {
                var effect = new ScRGBToHDR10Effect
                {
                    Source = bitmap,
                    BufferPrecision = CanvasBufferPrecision.Precision16Float,
                };
                ds.DrawImage(effect);
            }
            byte[] pixelBytes = renderTarget.GetPixelBytes();
            await SaveAsPngAsync(
                    stream,
                    width,
                    height,
                    DirectXPixelFormat.R16G16B16A16UIntNormalized,
                    pixelBytes,
                    ColorPrimaries.BT2020,
                    xmpData,
                    writeColorProfile,
                    maxCLL,
                    maxFALL
                )
                .ConfigureAwait(false);
        }
        else
        {
            throw new NotSupportedException($"{bitmap.Format} is not supported for PNG encoding.");
        }
    }

    public static async Task SaveAsPngAsync(
        Stream stream,
        uint width,
        uint height,
        DirectXPixelFormat pixelFormat,
        byte[] pixelBytes,
        ColorPrimaries colorPrimaries,
        byte[]? xmpData = null,
        bool writeColorProfile = true,
        float? maxCLL = null,
        float? maxFALL = null
    )
    {
        BitmapPixelFormat format = pixelFormat switch
        {
            DirectXPixelFormat.R8G8B8A8UIntNormalized => BitmapPixelFormat.Rgba8,
            DirectXPixelFormat.B8G8R8A8UIntNormalized => BitmapPixelFormat.Bgra8,
            DirectXPixelFormat.R16G16B16A16UIntNormalized => BitmapPixelFormat.Rgba16,
            _ => throw new NotSupportedException(
                $"{pixelFormat} is not supported for PNG encoding."
            ),
        };

        PngChunk? cicpChunk = null;
        PngChunk? iccpChunk = null;
        PngChunk? srgbChunk = null;
        PngChunk? chrmChunk = null;
        PngChunk? itxtChunk = null;
        byte[]? clliBytes = null;

        if (writeColorProfile)
        {
            if (colorPrimaries.TryGetDefinedPrimaries(out int id))
            {
                if (id == 1)
                {
                    srgbChunk = new PngChunk(1, PngChunkType.sRGB);
                }
                cicpChunk = new PngChunk(4, PngChunkType.cICP);
                ref PngcICPChunk cicp = ref cicpChunk.GetcICPChunk();
                cicp.ColorPrimaries = (byte)id;
                cicp.TransferFunction = (byte)(id == 9 ? 16 : 13);
                cicp.MatrixCoefficients = 0;
                cicp.FullRangeFlag = 1;
                cicpChunk.UpdateCrc32();
                // cLLi（PNGv3 内容亮度元数据，与 AVIF 的 clli 同动机）：无此元数据时浏览器做保守
                // tone-map 压高光；maxFALL 是直方图加权平均（零额外管线开销）。PngChunkType 不接受强转，整块手拼
                if (id == 9 && maxCLL is > 0)
                {
                    Span<byte> chunk = new byte[20];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk, 8);
                    "cLLi"u8.CopyTo(chunk[4..]);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                        chunk[8..],
                        (uint)Math.Clamp(maxCLL.Value, 0, uint.MaxValue)
                    );
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                        chunk[12..],
                        (uint)Math.Clamp(maxFALL ?? 0, 0, uint.MaxValue)
                    );
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                        chunk[16..],
                        ComputeCrc32(chunk[4..16])
                    );
                    clliBytes = chunk.ToArray();
                }
            }
            else
            {
                chrmChunk = new PngChunk(32, PngChunkType.cHRM);
                ref PngcHRMChunk chrm = ref chrmChunk.GetcHRMChunk();
                chrm.WhitePointX = colorPrimaries.White.X;
                chrm.WhitePointY = colorPrimaries.White.Y;
                chrm.RedX = colorPrimaries.Red.X;
                chrm.RedY = colorPrimaries.Red.Y;
                chrm.GreenX = colorPrimaries.Green.X;
                chrm.GreenY = colorPrimaries.Green.Y;
                chrm.BlueX = colorPrimaries.Blue.X;
                chrm.BlueY = colorPrimaries.Blue.Y;
                chrmChunk.UpdateCrc32();

                using var iccdata = new MemoryStream();
                using var zlib = new ZLibStream(iccdata, CompressionMode.Compress);
                zlib.Write(ICCHelper.CreateIccData(colorPrimaries));
                zlib.Flush();
                Span<byte> chunkContent = new byte[13 + iccdata.Length];
                "ICC Profile"u8.CopyTo(chunkContent);
                iccdata.Position = 0;
                iccdata.ReadExactly(chunkContent[13..]);
                iccpChunk = new PngChunk(PngChunkType.iCCP, chunkContent);
            }
        }
        if (xmpData is not null)
        {
            Span<byte> chunkContent = new byte[22 + xmpData.Length];
            "XML:com.adobe.xmp"u8.CopyTo(chunkContent);
            xmpData.CopyTo(chunkContent[22..]);
            itxtChunk = new PngChunk(PngChunkType.iTXt, chunkContent);
        }

        using var ms = new MemoryStream();
        var encoder = await BitmapEncoder.CreateAsync(
            BitmapEncoder.PngEncoderId,
            ms.AsRandomAccessStream()
        );
        encoder.SetPixelData(
            format,
            BitmapAlphaMode.Premultiplied,
            width,
            height,
            96,
            96,
            pixelBytes
        );
        await encoder.FlushAsync();

        stream.Write(PngReader.PngSignature);

        ms.Position = 0;
        using var reader = new PngReader(ms);
        PngChunk currentChunk;
        bool write = false;
        while ((currentChunk = reader.GetNextChunk()).Type != PngChunkType.IEND)
        {
            if (
                !write
                && (
                    currentChunk.Type == PngChunkType.sRGB
                    || (currentChunk.Type == PngChunkType.gAMA)
                    || currentChunk.Type == PngChunkType.PLTE
                    || currentChunk.Type == PngChunkType.IDAT
                )
            )
            {
                if (cicpChunk is not null)
                {
                    stream.Write(cicpChunk.ChunkData.Span);
                }
                if (iccpChunk is not null)
                {
                    stream.Write(iccpChunk.ChunkData.Span);
                }
                if (srgbChunk is not null)
                {
                    stream.Write(srgbChunk.ChunkData.Span);
                }
                if (chrmChunk is not null)
                {
                    stream.Write(chrmChunk.ChunkData.Span);
                }
                if (clliBytes is not null)
                {
                    stream.Write(clliBytes);
                }
                if (itxtChunk is not null)
                {
                    stream.Write(itxtChunk.ChunkData.Span);
                }
                write = true;
            }
            if (
                currentChunk.Type != PngChunkType.sRGB
                && currentChunk.Type != PngChunkType.gAMA
                && currentChunk.Type != PngChunkType.cICP
                && currentChunk.Type != PngChunkType.iCCP
                && currentChunk.Type != PngChunkType.cHRM
            )
            {
                stream.Write(currentChunk.ChunkData.Span);
            }
        }

        stream.Write(PngReader.IENDSignature);
    }

    /// <summary>
    /// PNG chunk CRC32（反射多项式 0xEDB88320，覆盖 type + data）。
    /// </summary>
    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1)));
            }
        }
        return crc ^ 0xFFFFFFFF;
    }

    public static async Task SaveAsAvifAsync(
        CanvasBitmap bitmap,
        Stream stream,
        ColorPrimaries colorPrimaries,
        int quality,
        byte[]? xmpData = null,
        bool writeColorProfile = true,
        float? maxCLL = null,
        float? maxFALL = null
    )
    {
        uint width = bitmap.SizeInPixels.Width;
        uint height = bitmap.SizeInPixels.Height;

        if (
            bitmap.Format
            is DirectXPixelFormat.R8G8B8A8UIntNormalized
                or DirectXPixelFormat.B8G8R8A8UIntNormalized
        )
        {
            byte[] pixelBytes = bitmap.GetPixelBytes();
            await SaveAsAvifAsync(
                    stream,
                    width,
                    height,
                    bitmap.Format,
                    pixelBytes,
                    colorPrimaries,
                    quality,
                    xmpData,
                    writeColorProfile
                )
                .ConfigureAwait(false);
        }
        else if (
            bitmap.Format
            is DirectXPixelFormat.R16G16B16A16Float
                or DirectXPixelFormat.R32G32B32A32Float
        )
        {
            using var renderTarget = new CanvasRenderTarget(
                CanvasDevice.GetSharedDevice(),
                width,
                height,
                96,
                DirectXPixelFormat.R16G16B16A16UIntNormalized,
                CanvasAlphaMode.Premultiplied
            );
            using (var ds = renderTarget.CreateDrawingSession())
            {
                var effect = new ScRGBToHDR10Effect
                {
                    Source = bitmap,
                    BufferPrecision = CanvasBufferPrecision.Precision16Float,
                };
                ds.DrawImage(effect);
            }
            byte[] pixelBytes = renderTarget.GetPixelBytes();
            await SaveAsAvifAsync(
                    stream,
                    width,
                    height,
                    DirectXPixelFormat.R16G16B16A16UIntNormalized,
                    pixelBytes,
                    ColorPrimaries.BT2020,
                    quality,
                    xmpData,
                    writeColorProfile,
                    maxCLL,
                    maxFALL
                )
                .ConfigureAwait(false);
        }
        else
        {
            throw new NotSupportedException($"{bitmap.Format} is not supported for AVIF encoding.");
        }
    }

    public static async Task SaveAsAvifAsync(
        Stream stream,
        uint width,
        uint height,
        DirectXPixelFormat pixelFormat,
        byte[] pixelBytes,
        ColorPrimaries colorPrimaries,
        int quality,
        byte[]? xmpData = null,
        bool writeColorProfile = true,
        float? maxCLL = null,
        float? maxFALL = null
    )
    {
        quality = Math.Clamp(quality, 0, 100);
        bool floatPixel =
            pixelFormat
            is DirectXPixelFormat.R16G16B16A16Float
                or DirectXPixelFormat.R32G32B32A32Float;
        avifRGBFormat rgbFormat = pixelFormat switch
        {
            DirectXPixelFormat.R8G8B8A8UIntNormalized => avifRGBFormat.RGBA,
            DirectXPixelFormat.B8G8R8A8UIntNormalized => avifRGBFormat.BGRA,
            DirectXPixelFormat.R16G16B16A16UIntNormalized => avifRGBFormat.RGBA,
            //DirectXPixelFormat.R16G16B16A16Float => avifRGBFormat.RGBA,
            _ => throw new NotSupportedException(
                $"{pixelFormat} is not supported for AVIF encoding."
            ),
        };
        uint depth = pixelFormat switch
        {
            DirectXPixelFormat.R8G8B8A8UIntNormalized => 8,
            DirectXPixelFormat.B8G8R8A8UIntNormalized => 8,
            DirectXPixelFormat.R16G16B16A16UIntNormalized => 16,
            //DirectXPixelFormat.R16G16B16A16Float => 16,
            _ => throw new NotSupportedException(
                $"{pixelFormat} is not supported for AVIF encoding."
            ),
        };

        await Task.Run(() =>
            {
                int maxThreads = GetSuggestedThreads();
                using var encoder = new avifEncoderLite();
                encoder.Quality = quality;
                encoder.QualityAlpha = quality;
                encoder.MaxThreads = maxThreads;
                using var rgb = new avifRGBImageWrapper(width, height, depth, rgbFormat);
                rgb.MaxThreads = maxThreads;
                rgb.IsFloat = floatPixel;
                rgb.SetPixelBytes(pixelBytes);
                using var image = new avifImageWrapper(
                    width,
                    height,
                    Math.Clamp(depth, 8, 12),
                    avifPixelFormat.YUV444
                );

                if (writeColorProfile && colorPrimaries.TryGetDefinedPrimaries(out int id))
                {
                    if (id == 9)
                    {
                        image.ColorPrimaries = avifColorPrimaries.BT2020;
                        image.TransferCharacteristics = avifTransferCharacteristics.SMPTE2084;
                        image.MatrixCoefficients = avifMatrixCoefficients.BT2020_NCL;
                        // clli（maxCLL + maxFALL）：无 maxCLL 元数据时 Chrome 做保守 tone-map 压高光（屏幕越暗越明显，issue #4）；
                        // Chrome 决策只看 maxCLL，maxFALL 属顺手补全（直方图加权平均，零额外管线开销）
                        if (maxCLL is > 0)
                        {
                            image.SetMaxCLL(
                                (ushort)Math.Clamp(maxCLL.Value, 0, 65535),
                                (ushort)Math.Clamp(maxFALL ?? 0, 0, 65535)
                            );
                        }
                    }
                    else
                    {
                        image.ColorPrimaries = (avifColorPrimaries)id;
                        image.TransferCharacteristics = avifTransferCharacteristics.SRGB;
                        image.MatrixCoefficients = avifMatrixCoefficients.BT709;
                    }
                }
                else if (writeColorProfile)
                {
                    image.ColorPrimaries = avifColorPrimaries.Unspecified;
                    image.TransferCharacteristics = avifTransferCharacteristics.Unspecified;
                    image.MatrixCoefficients = avifMatrixCoefficients.Unspecified;
                    image.SetProfileICC(ICCHelper.CreateIccData(colorPrimaries));
                }
                else
                {
                    image.ColorPrimaries = avifColorPrimaries.Unspecified;
                    image.TransferCharacteristics = avifTransferCharacteristics.Unspecified;
                    image.MatrixCoefficients = avifMatrixCoefficients.Unspecified;
                }

                if (xmpData is not null)
                {
                    image.SetXMPMetadata(xmpData);
                }
                image.FromRGBImage(rgb);
                encoder.AddImage(image, 1, avifAddImageFlag.Single);
                stream.Write(encoder.Encode());
            })
            .ConfigureAwait(false);
    }

    public static async Task SaveAsJxlAsync(
        CanvasBitmap bitmap,
        Stream stream,
        ColorPrimaries colorPrimaries,
        float distance,
        byte[]? xmpData = null,
        bool writeColorProfile = true
    )
    {
        uint width = bitmap.SizeInPixels.Width;
        uint height = bitmap.SizeInPixels.Height;

        if (
            bitmap.Format
            is DirectXPixelFormat.R8G8B8A8UIntNormalized
                or DirectXPixelFormat.B8G8R8A8UIntNormalized
        )
        {
            byte[] pixelBytes;
            if (bitmap.Format is DirectXPixelFormat.B8G8R8A8UIntNormalized)
            {
                using var renderTarget = new CanvasRenderTarget(
                    CanvasDevice.GetSharedDevice(),
                    width,
                    height,
                    96,
                    DirectXPixelFormat.R8G8B8A8UIntNormalized,
                    CanvasAlphaMode.Premultiplied
                );
                using (var ds = renderTarget.CreateDrawingSession())
                {
                    ds.DrawImage(bitmap);
                }
                pixelBytes = renderTarget.GetPixelBytes();
            }
            else
            {
                pixelBytes = bitmap.GetPixelBytes();
            }
            await SaveAsJxlAsync(
                    stream,
                    width,
                    height,
                    DirectXPixelFormat.R8G8B8A8UIntNormalized,
                    pixelBytes,
                    colorPrimaries,
                    distance,
                    xmpData,
                    writeColorProfile
                )
                .ConfigureAwait(false);
        }
        else if (
            bitmap.Format
            is DirectXPixelFormat.R16G16B16A16Float
                or DirectXPixelFormat.R32G32B32A32Float
        )
        {
            using var renderTarget = new CanvasRenderTarget(
                CanvasDevice.GetSharedDevice(),
                width,
                height,
                96,
                DirectXPixelFormat.R16G16B16A16UIntNormalized,
                CanvasAlphaMode.Premultiplied
            );
            using (var ds = renderTarget.CreateDrawingSession())
            {
                var effect = new ScRGBToHDR10Effect
                {
                    Source = bitmap,
                    BufferPrecision = CanvasBufferPrecision.Precision16Float,
                };
                ds.DrawImage(effect);
            }
            byte[] pixelBytes = renderTarget.GetPixelBytes();
            await SaveAsJxlAsync(
                    stream,
                    width,
                    height,
                    DirectXPixelFormat.R16G16B16A16UIntNormalized,
                    pixelBytes,
                    ColorPrimaries.BT2020,
                    distance,
                    xmpData,
                    writeColorProfile
                )
                .ConfigureAwait(false);
        }
        else
        {
            throw new NotSupportedException(
                $"{bitmap.Format} is not supported for JPEG XL encoding."
            );
        }
    }

    public static async Task SaveAsJxlAsync(
        Stream stream,
        uint width,
        uint height,
        DirectXPixelFormat pixelFormat,
        byte[] pixelBytes,
        ColorPrimaries colorPrimaries,
        float distance,
        byte[]? xmpData = null,
        bool writeColorProfile = true
    )
    {
        distance = Math.Clamp(distance, 0, 25);
        bool lossless = distance == 0;
        bool floatPixel =
            pixelFormat
            is DirectXPixelFormat.R16G16B16A16Float
                or DirectXPixelFormat.R32G32B32A32Float;
        JxlPixelFormat format = pixelFormat switch
        {
            DirectXPixelFormat.R8G8B8A8UIntNormalized => JxlPixelFormat.R8G8B8A8UInt,
            DirectXPixelFormat.R16G16B16A16UIntNormalized => JxlPixelFormat.R16G16B16A16UInt,
            DirectXPixelFormat.R16G16B16A16Float => JxlPixelFormat.R16G16B16A16Float,
            DirectXPixelFormat.R32G32B32A32Float => JxlPixelFormat.R32G32B32A32Float,
            _ => throw new NotSupportedException(
                $"{pixelFormat} is not supported for JXL encoding."
            ),
        };
        uint depth = pixelFormat switch
        {
            DirectXPixelFormat.R8G8B8A8UIntNormalized => 8,
            DirectXPixelFormat.R16G16B16A16UIntNormalized => 16,
            DirectXPixelFormat.R16G16B16A16Float => 16,
            DirectXPixelFormat.R32G32B32A32Float => 32,
            _ => throw new NotSupportedException(
                $"{pixelFormat} is not supported for AVIF encoding."
            ),
        };

        JxlColorEncoding colorEncoding = default;
        if (writeColorProfile)
        {
            if (colorPrimaries.TryGetDefinedPrimaries(out int id))
            {
                if (id == 9)
                {
                    colorEncoding = JxlColorEncoding.HDR10;
                }
                else
                {
                    colorEncoding.Primaries = (JxlPrimaries)id;
                    colorEncoding.TransferFunction = JxlTransferFunction.sRGB;
                }
                colorEncoding.WhitePoint = JxlWhitePoint.D65;
            }
            else
            {
                colorEncoding.Primaries = JxlPrimaries.Custom;
                colorEncoding.PrimariesRedXY = new JxlPoint(
                    colorPrimaries.Red.X,
                    colorPrimaries.Red.Y
                );
                colorEncoding.PrimariesGreenXY = new JxlPoint(
                    colorPrimaries.Green.X,
                    colorPrimaries.Green.Y
                );
                colorEncoding.PrimariesBlueXY = new JxlPoint(
                    colorPrimaries.Blue.X,
                    colorPrimaries.Blue.Y
                );
                colorEncoding.WhitePoint = JxlWhitePoint.Custom;
                colorEncoding.WhitePointXY = new JxlPoint(
                    colorPrimaries.White.X,
                    colorPrimaries.White.Y
                );
                colorEncoding.TransferFunction = JxlTransferFunction.sRGB;
            }
            if (floatPixel)
            {
                colorEncoding.TransferFunction = JxlTransferFunction.Linear;
            }
        }

        await Task.Run(() =>
            {
                using var encoder = new JxlEncoder();
                encoder.SetBasicInfo(
                    new JxlBasicInfo(width, height, format, true) { UsesOriginalProfile = lossless }
                );
                if (writeColorProfile)
                {
                    encoder.SetColorEncoding(colorEncoding);
                }
                if (xmpData is not null)
                {
                    encoder.AddBox(JxlBoxType.XMP, xmpData, false);
                }
                encoder.RunnerThreads = (uint)GetSuggestedThreads();
                var frameSettings = encoder.CreateFrameSettings();
                frameSettings.Distance = distance;
                frameSettings.Lossless = lossless;
                frameSettings.AddImageFrame(format, pixelBytes);
                encoder.Encode(stream);
            })
            .ConfigureAwait(false);
    }

    public static async Task SaveAsUhdrAsync(
        CanvasBitmap canvasImage,
        Stream stream,
        float maxCLL,
        float sdrWhiteLevel,
        float hdrCapacityMaxOverride = 0
    )
    {
        if (!float.IsFinite(hdrCapacityMaxOverride)
            || (hdrCapacityMaxOverride != 0 && (hdrCapacityMaxOverride < 2 || hdrCapacityMaxOverride > 32)))
        {
            throw new ArgumentOutOfRangeException(nameof(hdrCapacityMaxOverride));
        }
        if (canvasImage.Format is DirectXPixelFormat.R16G16B16A16Float)
        {
            await Task.Delay(1).ConfigureAwait(false);
            // 先换色域并把超域颜色压回工作色域，再做任何截断：scRGB 的广色域颜色本来就靠负通道表达，
            // 负通道一旦被截成 0，逐通道增益再也乘不回来（增益只能放大，不能翻负）。
            using UhdrWorkingGamutEffect workingEffect = new()
            {
                Source = canvasImage,
                BufferPrecision = CanvasBufferPrecision.Precision16Float,
            };
            using HdrToneMapEffect toneMapEffect = new()
            {
                Source = workingEffect,
                InputMaxLuminance = maxCLL,
                OutputMaxLuminance = sdrWhiteLevel,
                DisplayMode = HdrToneMapEffectDisplayMode.Hdr,
                BufferPrecision = CanvasBufferPrecision.Precision16Float,
            };
            using WhiteLevelAdjustmentEffect sdrLinearEffect = new()
            {
                Source = toneMapEffect,
                InputWhiteLevel = 80,
                OutputWhiteLevel = sdrWhiteLevel,
                BufferPrecision = CanvasBufferPrecision.Precision16Float,
            };
            // base 走的是 toneMap → 白电平 → OETF → 8bit → JPEG，libultrahdr 解码后对它做 srgbInvOetf
            // 才拿来乘增益。所以增益的分母只能是"已经落到 8bit 的那个值"，不能是上面任何一级浮点：
            // 分母比实际 base 小的通道（被 255 截断的高光）算出来的增益 ≈ 1，base 丢掉的余量就永久丢了。
            using SrgbGammaEffect gammaEffect = new()
            {
                Source = sdrLinearEffect,
                GammaMode = SrgbGammaMode.OETF,
                BufferPrecision = CanvasBufferPrecision.Precision16Float,
            };

            // 分子必须和分母用同一个参考白：base 存的是「1.0 = 该显示器的 SDR 白」，
            // 而 scRGB 的 1.0 恒等于 80nits。少这一步，SDR 白不是 80nits 时整张图会被重建得
            // 亮 sdrWhiteLevel/80 倍（320nits 下实测 4 倍，灰阶误差 300%）。
            using WhiteLevelAdjustmentEffect hdrNormalizedEffect = new()
            {
                Source = workingEffect,
                InputWhiteLevel = 80,
                OutputWhiteLevel = sdrWhiteLevel,
                BufferPrecision = CanvasBufferPrecision.Precision16Float,
            };

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

            using SrgbGammaEffect storedLinearEffect = new()
            {
                Source = renderTarget_sdr,
                GammaMode = SrgbGammaMode.EOTF,
                BufferPrecision = CanvasBufferPrecision.Precision16Float,
            };
            using UhdrPixelGainEffect uhdrPixelGainEffect = new()
            {
                SdrSource = storedLinearEffect,
                HdrSource = hdrNormalizedEffect,
                BufferPrecision = CanvasBufferPrecision.Precision16Float,
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
            float[] contentBoost = GetContentMinMaxBoost(gainPixelBytes);
            // XMP 只能带一个量程，三通道不公共就不能写 XMP（jpegr.cpp 直接报 unsupported）。
            // 取公共 min/max 只影响量化区间，逐通道的增益差别仍然留在 gain map 像素里。
            float commonMinBoost = MathF.Min(
                MathF.Min(contentBoost[0], contentBoost[1]),
                contentBoost[2]
            );
            float commonMaxBoost = MathF.Max(
                MathF.Max(contentBoost[3], contentBoost[4]),
                contentBoost[5]
            );

            using UhdrGainmapEffect uhdrGainmapEffect = new()
            {
                PixelGainSource = renderTarget_gain,
                MinContentBoost = new float3(commonMinBoost, commonMinBoost, commonMinBoost),
                MaxContentBoost = new float3(commonMaxBoost, commonMaxBoost, commonMaxBoost),
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

            using MemoryStream ms_base = new();
            using MemoryStream ms_gainmap = new();
            // base 走 4:2:0（Ultra HDR 基图惯例，与 libultrahdr 自带编码器一致）；
            // gain map 走 4:4:4，因为它是逐通道增益，4:2:0 会让增益在色边互相渗透。
            await SaveAsJpegAsync(
                renderTarget_sdr,
                ms_base,
                UhdrJpegQuality,
                JpegSubsampling420
            );
            await SaveAsJpegAsync(
                renderTarget_gainmap,
                ms_gainmap,
                UhdrJpegQuality,
                JpegSubsampling444
            );

            // base 只有在没有 ICC 时才会被 libultrahdr 按声明色域补一份 ICC；
            // gain map 带 ICC 会被当成增益自身所在色域，use_base_cg=1 下再乘一道转换就把增益压灰。
            byte[] baseArray = RemoveJpegIccSegments(ms_base.ToArray());
            byte[] gainArray = RemoveJpegIccSegments(ms_gainmap.ToArray());

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
                        ColorGamut = UhdrColor.WorkingColorGamut,
                        ColorRange = UhdrColorRange.FullRange,
                        ColorTransfer = UhdrColorTransfer.SRGB,
                    };
                    UhdrCompressedImage gainmapImage = new UhdrCompressedImage
                    {
                        Data = (nint)g,
                        DataSize = (uint)gainArray.Length,
                        Capacity = (uint)gainArray.Length,
                        // 增益本身没有色域，声明成 base 色域是为了让 libultrahdr 走恒等转换
                        ColorGamut = UhdrColor.WorkingColorGamut,
                        ColorRange = UhdrColorRange.FullRange,
                        ColorTransfer = UhdrColorTransfer.SRGB,
                    };
                    encoder.SetCompressedImage(baseImage, UhdrImageLabel.Base);
                    UhdrGainmapMetadata metadata = new UhdrGainmapMetadata
                    {
                        Gamma = new FixedArray3<float>(1),
                        OffsetSdr = new FixedArray3<float>(UhdrColor.GainOffset),
                        OffsetHdr = new FixedArray3<float>(UhdrColor.GainOffset),
                        HdrCapacityMin = 1,
                        // API uses linear boost; native XMP/ISO writers convert to log2.
                        // Do not use this override in gain-map pixel generation or gain bounds.
                        HdrCapacityMax = hdrCapacityMaxOverride > 0
                            ? hdrCapacityMaxOverride : MathF.Max(commonMaxBoost, 1),
                        // use_base_cg≠0：base 不做色域转换，增益按 base 色域表达（jpegr.cpp applyGainMap）。
                        // 我们的 gain 正是拿 base 的线性信号当分母算的，故为 1。
                        UseBaseColorSpace = 1,
                    };
                    // 三通道必须写同一个 float 值（不是三个算出来相等的值），
                    // libultrahdr 用 are_all_channels_identical() 比的是浮点相等。
                    metadata.MinContentBoost[0] = commonMinBoost;
                    metadata.MinContentBoost[1] = commonMinBoost;
                    metadata.MinContentBoost[2] = commonMinBoost;
                    metadata.MaxContentBoost[0] = commonMaxBoost;
                    metadata.MaxContentBoost[1] = commonMaxBoost;
                    metadata.MaxContentBoost[2] = commonMaxBoost;
                    encoder.SetGainmapImage(gainmapImage, metadata);
                }
            }
            encoder.Encode();
            stream.Write(encoder.GetEncodedBytes());
        }
    }

    /// <summary>
    /// 剥掉 JPEG 头里的 ICC 段（APP2 + "ICC_PROFILE\0"，可分多段）。SOS 之后的熵编码数据原样拷走。
    /// 没找到 ICC 时返回等价字节流。
    /// </summary>
    private static byte[] RemoveJpegIccSegments(byte[] jpeg)
    {
        using var result = new MemoryStream(jpeg.Length);
        if (jpeg.Length < 2 || jpeg[0] is not 0xFF || jpeg[1] is not 0xD8)
        {
            return jpeg;
        }
        result.Write(jpeg, 0, 2);
        int pos = 2;
        while (pos + 3 < jpeg.Length && jpeg[pos] is 0xFF)
        {
            byte marker = jpeg[pos + 1];
            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2)) + 2;
            if (marker is 0xFF)
            {
                pos++;
                continue;
            }
            if (segmentLength > jpeg.Length - pos)
            {
                break;
            }
            bool isIcc =
                marker is 0xE2
                && segmentLength >= 16
                && jpeg.AsSpan(pos + 4, 12).SequenceEqual("ICC_PROFILE\0"u8);
            if (!isIcc)
            {
                result.Write(jpeg, pos, segmentLength);
            }
            pos += segmentLength;
            if (marker is 0xDA)
            {
                result.Write(jpeg, pos, jpeg.Length - pos);
                return result.ToArray();
            }
        }
        result.Write(jpeg, pos, jpeg.Length - pos);
        return result.ToArray();
    }

    /// <summary>
    /// return min rgb, max rgb
    /// </summary>
    /// <param name="pixelBytes"></param>
    /// <returns></returns>
    public static float[] GetContentMinMaxBoost(byte[] pixelBytes)
    {
        const float PQ_MAX = 10000f / 203;
        float[] contentBoost = [PQ_MAX, PQ_MAX, PQ_MAX, 0, 0, 0];
        var span = MemoryMarshal.Cast<byte, float>(pixelBytes);
        if (Vector.IsHardwareAccelerated && Vector<float>.Count % 4 == 0)
        {
            Vector<float> minBoost = new Vector<float>(PQ_MAX);
            Vector<float> maxBoost = new Vector<float>(0);
            int remaining = span.Length % Vector<float>.Count;
            for (int i = 0; i < span.Length - remaining; i += Vector<float>.Count)
            {
                var value = new Vector<float>(span.Slice(i, Vector<float>.Count));
                minBoost = Vector.Min(minBoost, value);
                maxBoost = Vector.Max(maxBoost, value);
            }
            for (int i = 0; i < Vector<float>.Count; i += 4)
            {
                contentBoost[0] = MathF.Min(contentBoost[0], minBoost[i]);
                contentBoost[1] = MathF.Min(contentBoost[1], minBoost[i + 1]);
                contentBoost[2] = MathF.Min(contentBoost[2], minBoost[i + 2]);
                contentBoost[3] = MathF.Max(contentBoost[3], maxBoost[i]);
                contentBoost[4] = MathF.Max(contentBoost[4], maxBoost[i + 1]);
                contentBoost[5] = MathF.Max(contentBoost[5], maxBoost[i + 2]);
            }
            for (int i = span.Length - remaining; i < span.Length; i += 4)
            {
                contentBoost[0] = MathF.Min(contentBoost[0], span[i]);
                contentBoost[1] = MathF.Min(contentBoost[1], span[i + 1]);
                contentBoost[2] = MathF.Min(contentBoost[2], span[i + 2]);
                contentBoost[3] = MathF.Max(contentBoost[3], span[i]);
                contentBoost[4] = MathF.Max(contentBoost[4], span[i + 1]);
                contentBoost[5] = MathF.Max(contentBoost[5], span[i + 2]);
            }
        }
        else
        {
            for (int i = 0; i < span.Length; i += 4)
            {
                contentBoost[0] = MathF.Min(contentBoost[0], span[i]);
                contentBoost[1] = MathF.Min(contentBoost[1], span[i + 1]);
                contentBoost[2] = MathF.Min(contentBoost[2], span[i + 2]);
                contentBoost[3] = MathF.Max(contentBoost[3], span[i]);
                contentBoost[4] = MathF.Max(contentBoost[4], span[i + 1]);
                contentBoost[5] = MathF.Max(contentBoost[5], span[i + 2]);
            }
        }
        return contentBoost;
    }
}
