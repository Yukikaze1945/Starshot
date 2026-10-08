using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Starshot.Features.Codec;
using Starward.Codec.ICC;
using Windows.Graphics.DirectX;

internal static class Program
{
    private static async Task<int> Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            CanvasDevice device = CanvasDevice.GetSharedDevice();
            Console.WriteLine($"GPU device initialized: {device}");
            await Run("native GPU saver HEVC and AV1", () => SaveBothCodecs(device));
            await Run("cancelled saver keeps companion image and removes video", () => CheckCancellation(device));
            await Run("MP4 basename collision preserves existing bytes and fails", () => CheckCollision(device));
            return failures == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int failures;
    private static async Task Run(string name, Func<Task> test)
    {
        try { await test(); Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
    }

    private static async Task SaveBothCodecs(CanvasDevice device)
    {
        using var temp = new TempWorkspace();
        string media = Path.Combine(temp.Path, "media"); Directory.CreateDirectory(media);
        using CanvasBitmap fixture = MakeFixture(device, 240, 160);
        foreach (var codec in new[] { StaticVideoCodec.Hevc, StaticVideoCodec.Av1 })
        {
            string label = codec == StaticVideoCodec.Hevc ? "hevc" : "av1";
            string image = Path.Combine(media, label + ".png");
            string video = Path.ChangeExtension(image, ".mp4");
            await SaveCompanion(fixture, image);
            var result = await StaticVideoSaver.SaveAsync(fixture, image, true, 1000, 350, codec, null, CancellationToken.None);
            Check(result.Complete && result.Error is null, $"{label} saver did not complete: {result.Error}");
            Check(result.ImagePath == image && result.VideoPath == video && File.Exists(image), $"{label} output paths/companion");
            Check(File.Exists(video) && new FileInfo(video).Length > 0, $"{label} MP4 missing");
            Check(StaticVideoMetadata.IsVideo(video), $"{label} MP4 not recognized");
            var descriptor = StaticVideoMetadata.Read(video);
            Check(descriptor.ImagePath == image && descriptor.Hdr == true && descriptor.Codec == label.ToUpperInvariant(), $"{label} descriptor values");
            await VerifyVideo(video, codec);
            await VerifyDecodedHighlights(video, device, codec);

            string victim = Path.Combine(temp.Path, "victim.png");
            await File.WriteAllBytesAsync(victim, [1, 2, 3, 4]);
            await File.WriteAllTextAsync(video + ".starshot.json", JsonSerializer.Serialize(new { version = 1, image = "../victim.png", codec = label.ToUpperInvariant(), hdr = true }));
            descriptor = StaticVideoMetadata.Read(video);
            Check(descriptor.ImagePath == image, $"{label} descriptor accepted an escaping companion path");
            Check((await File.ReadAllBytesAsync(victim)).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "descriptor path check changed outside file");
        }
    }

    private static async Task CheckCancellation(CanvasDevice device)
    {
        using var temp = new TempWorkspace();
        string image = Path.Combine(temp.Path, "cancel.png");
        using CanvasBitmap fixture = MakeFixture(device, 3840, 2160);
        await SaveCompanion(fixture, image);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        var result = await StaticVideoSaver.SaveAsync(fixture, image, true, 1000, 350, StaticVideoCodec.Hevc, null, cancellation.Token);
        Check(result.Cancelled && !result.Complete && result.VideoPath is null, $"cancel result not reported: {result}");
        Check(File.Exists(image), "cancellation removed the already-saved companion image");
        Check(!File.Exists(Path.ChangeExtension(image, ".mp4")), "cancelled save left a committed MP4");
        Check(!Directory.EnumerateFiles(temp.Path, "*.partial", SearchOption.AllDirectories).Any(), "cancellation left partial output");
    }

    private static async Task CheckCollision(CanvasDevice device)
    {
        using var temp = new TempWorkspace();
        string image = Path.Combine(temp.Path, "collision.png");
        string video = Path.ChangeExtension(image, ".mp4");
        using CanvasBitmap fixture = MakeFixture(device, 240, 160);
        await SaveCompanion(fixture, image);
        byte[] original = [0x53, 0x54, 0x41, 0x52, 0x53, 0x48, 0x4F, 0x54];
        await File.WriteAllBytesAsync(video, original);
        var result = await StaticVideoSaver.SaveAsync(fixture, image, true, 1000, 350, StaticVideoCodec.Hevc, null, CancellationToken.None);
        Check(!result.Complete && result.Error is not null, $"collision reported success: {result}");
        Check((await File.ReadAllBytesAsync(video)).SequenceEqual(original), "basename collision overwrote existing MP4");
        Check(File.Exists(image), "basename collision removed companion image");
    }

    private static async Task SaveCompanion(CanvasBitmap bitmap, string path)
    {
        await using var output = File.Create(path);
        await ImageSaver.SaveAsPngAsync(bitmap, output, ColorPrimaries.BT2020, maxCLL: 1000, maxFALL: 350);
    }

    private static CanvasBitmap MakeFixture(CanvasDevice device, int width, int height)
    {
        ushort[] samples = new ushort[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            Vector3 rgb = y < height / 2
                ? x < width / 3 ? new Vector3(1f) : x < width * 2 / 3 ? new Vector3(12.5f) : new Vector3(.18f)
                : x < width / 3 ? new Vector3(3f, .02f, .02f) : x < width * 2 / 3 ? new Vector3(.02f, 3f, .02f) : new Vector3(.02f, .02f, 3f);
            // A bright white annotation stroke over the 1000-nit patch.
            if ((y >= 18 && y < 24 && x >= width / 3 + 8 && x < width / 3 + width / 12) ||
                (x >= width / 3 + 8 && x < width / 3 + 14 && y >= 18 && y < 52))
                rgb = new Vector3(12.5f);
            int index = (y * width + x) * 4;
            samples[index] = BitConverter.HalfToUInt16Bits((Half)rgb.X);
            samples[index + 1] = BitConverter.HalfToUInt16Bits((Half)rgb.Y);
            samples[index + 2] = BitConverter.HalfToUInt16Bits((Half)rgb.Z);
            samples[index + 3] = BitConverter.HalfToUInt16Bits((Half)1f);
        }
        return CanvasBitmap.CreateFromBytes(device, MemoryMarshal.AsBytes(samples.AsSpan()).ToArray(), width, height,
            DirectXPixelFormat.R16G16B16A16Float, 96);
    }

    private static async Task VerifyVideo(string path, StaticVideoCodec codec)
    {
        string ffprobe = Path.Combine(StaticVideoEncoder.ComponentDirectory, "ffprobe.exe");
        string json = await StaticVideoEncoder.RunTextAsync(ffprobe,
            ["-v", "error", "-show_streams", "-show_format", "-show_frames", "-show_packets", "-of", "json", path], CancellationToken.None);
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        var stream = root.GetProperty("streams").EnumerateArray().Single();
        Check(stream.GetProperty("codec_name").GetString() == (codec == StaticVideoCodec.Hevc ? "hevc" : "av1"), "codec mismatch");
        Check(stream.GetProperty("pix_fmt").GetString() == "yuv420p10le", "output is not 10-bit");
        Check(stream.GetProperty("color_transfer").GetString() == "smpte2084", "PQ transfer missing");
        Check(stream.GetProperty("color_primaries").GetString() == "bt2020", "BT.2020 primaries missing");
        Check(stream.GetProperty("color_space").GetString() == "bt2020nc", "BT.2020 matrix missing");
        Check(Math.Abs(double.Parse(stream.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture) - 3) < .001, "stream duration is not 3s");
        JsonElement[] records = root.GetProperty("packets_and_frames").EnumerateArray().ToArray();
        JsonElement[] frames = records.Where(x => x.GetProperty("type").GetString() == "frame").ToArray();
        JsonElement[] packets = records.Where(x => x.GetProperty("type").GetString() == "packet").ToArray();
        Check(frames.Length == 1, $"expected 1 decoded frame, got {frames.Length}");
        Check(packets.Length == 1, $"expected 1 packet, got {packets.Length}");
        Check(Math.Abs(double.Parse(packets[0].GetProperty("duration_time").GetString()!, CultureInfo.InvariantCulture) - 3) < .001, "packet duration is not 3s");
        var sideData = frames[0].GetProperty("side_data_list").EnumerateArray().ToArray();
        JsonElement light = sideData.Single(x => x.GetProperty("side_data_type").GetString() == "Content light level metadata");
        Check(light.GetProperty("max_content").GetInt32() == 1000 && light.GetProperty("max_average").GetInt32() == 350, "CLL metadata mismatch");
    }

    private static async Task VerifyDecodedHighlights(string path, CanvasDevice device, StaticVideoCodec codec)
    {
        string ffmpeg = Path.Combine(StaticVideoEncoder.ComponentDirectory, "ffmpeg.exe");
        using var process = StaticVideoEncoder.NewProcess(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-i", path, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gbrp16le", "pipe:1"]);
        process.Start();
        using var output = new MemoryStream();
        Task copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); await copy;
        string errors = await error;
        Check(process.ExitCode == 0, "ffmpeg decode failed: " + errors);
        byte[] decoded = output.ToArray();
        JsonElement stream = await ProbeStream(path);
        int width = stream.GetProperty("width").GetInt32(), height = stream.GetProperty("height").GetInt32();
        int planeBytes = checked(width * height * 2);
        Check(decoded.Length >= planeBytes * 3, "decoded pixel payload missing");
        ushort white = Mean16(decoded, planeBytes * 2, width, height, width / 6, height / 4, width / 6, height / 4);
        ushort hdrWhite = Mean16(decoded, planeBytes * 2, width, height, width / 2, height / 4, width / 6, height / 4);
        double whiteNits = DecodePq(white);
        double highlightNits = DecodePq(hdrWhite);
        Console.WriteLine($"{codec}: scRGB white={whiteNits:F2} nit, highlight={highlightNits:F2} nit");
        Check(whiteNits is > 72 and < 88, $"scRGB 1.0 decoded as {whiteNits:F1} nits, expected ~80");
        Check(highlightNits is > 900 and < 1100, $"scRGB 12.5 clipped/tone-mapped to {highlightNits:F1} nits");
        _ = device; _ = codec;
    }

    private static async Task<JsonElement> ProbeStream(string path)
    {
        string json = await StaticVideoEncoder.RunTextAsync(Path.Combine(StaticVideoEncoder.ComponentDirectory, "ffprobe.exe"),
            ["-v", "error", "-show_streams", "-of", "json", path], CancellationToken.None);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("streams")[0].Clone();
    }

    private static ushort Mean16(byte[] bytes, int plane, int width, int height, int x, int y, int sampleWidth, int sampleHeight)
    {
        ulong sum = 0; int count = 0;
        for (int yy = y; yy < y + sampleHeight; yy += Math.Max(1, sampleHeight / 8))
        for (int xx = x; xx < x + sampleWidth; xx += Math.Max(1, sampleWidth / 8))
        { sum += BitConverter.ToUInt16(bytes, plane + (yy * width + xx) * 2); count++; }
        return (ushort)(sum / (ulong)count);
    }

    private static double DecodePq(ushort encoded)
    {
        const double m1 = 2610d / 16384, m2 = 2523d / 32, c1 = 3424d / 4096, c2 = 2413d / 128, c3 = 2392d / 128;
        double p = Math.Pow(encoded / (double)ushort.MaxValue, 1 / m2);
        return 10000 * Math.Pow(Math.Max(p - c1, 0) / (c2 - c3 * p), 1 / m1);
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class TempWorkspace : IDisposable
    {
        private static readonly string Root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Starshot.StaticVideoNativeTest"));
        public string Path { get; } = System.IO.Path.Combine(Root, Guid.NewGuid().ToString("N"));
        public TempWorkspace() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            string fullRoot = Root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            string fullPath = System.IO.Path.GetFullPath(Path);
            if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ||
                System.IO.Path.GetDirectoryName(fullPath) != Root)
                throw new InvalidOperationException("Refusing to clean a path outside the dedicated native-test temp root.");
            if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
        }
    }
}
