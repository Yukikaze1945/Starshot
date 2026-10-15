using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Starshot.Features.Codec;
using Starward.Codec.UltraHdr;
using Windows.Graphics.DirectX;

const int width = 1920, height = 1080;
int pixelCount = width * height;
byte[] source = await File.ReadAllBytesAsync(args[0]);
if (source.Length != pixelCount * 3 * sizeof(float)) throw new InvalidDataException($"Expected {pixelCount * 3 * sizeof(float)} raw RGB float bytes, received {source.Length}.");
float Read(int plane, int pixel) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan((plane * pixelCount + pixel) * sizeof(float), sizeof(float))));

static float PqToNits(float value)
{
    const double m1 = 2610.0 / 16384.0, m2 = 2523.0 / 32.0;
    const double c1 = 3424.0 / 4096.0, c2 = 2413.0 / 128.0, c3 = 2392.0 / 128.0;
    double p = Math.Pow(Math.Clamp(value, 0f, 1f), 1.0 / m2);
    double ratio = Math.Max(p - c1, 0) / (c2 - c3 * p);
    return (float)(Math.Pow(ratio, 1.0 / m1) * 10000.0);
}

var pixels = new ushort[pixelCount * 4];
for (int i = 0; i < pixelCount; i++)
{
    // FFmpeg gbrpf32le planes are G, B, R. Convert absolute-nit BT.2020 RGB to scRGB/BT.709, where 1.0 = 80 nits.
    float g2020 = PqToNits(Read(0, i));
    float b2020 = PqToNits(Read(1, i));
    float r2020 = PqToNits(Read(2, i));
    float r709 = (1.660491f * r2020 - 0.587641f * g2020 - 0.072850f * b2020) / 80f;
    float g709 = (-0.124550f * r2020 + 1.132900f * g2020 - 0.008349f * b2020) / 80f;
    float b709 = (-0.018151f * r2020 - 0.100579f * g2020 + 1.118730f * b2020) / 80f;
    int offset = i * 4;
    pixels[offset] = BitConverter.HalfToUInt16Bits((Half)r709);
    pixels[offset + 1] = BitConverter.HalfToUInt16Bits((Half)g709);
    pixels[offset + 2] = BitConverter.HalfToUInt16Bits((Half)b709);
    pixels[offset + 3] = BitConverter.HalfToUInt16Bits((Half)1f);
}

var device = CanvasDevice.GetSharedDevice();
using var frame = CanvasBitmap.CreateFromBytes(device, MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray(), width, height, DirectXPixelFormat.R16G16B16A16Float, 96);
await using (var output = File.Create(args[1])) await ImageSaver.SaveAsUhdrAsync(frame, output, 1000, 80);
byte[] encoded = await File.ReadAllBytesAsync(args[1]);
using var decoder = UhdrDecoder.Create(encoded);
var metadata = decoder.GetGainmapMetadata();
Console.WriteLine($"dimensions={width}x{height} bytes={encoded.Length} gainmapCapacityMin={metadata.HdrCapacityMin:F3} gainmapCapacityMax={metadata.HdrCapacityMax:F3}");
