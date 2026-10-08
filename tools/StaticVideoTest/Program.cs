using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Starshot.Features.Codec;

var keepSamples = args.Contains("--keep-samples", StringComparer.OrdinalIgnoreCase);
var componentDirectory = args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal));
var tests = new List<(string Name, Func<Task> Run)>
{
    ("SDR RGBA packing is G-B-R and converts sRGB to BT.709", TestRgbaPacking),
    ("SDR BGRA packing is G-B-R and converts sRGB to BT.709", TestBgraPacking),
    ("PQ16 RGB packing preserves G-B-R channel bits", TestPqPacking),
    ("odd dimensions clamp-pad to even values and 64 pixel minimum", TestPadding),
    ("invalid input and cancellation are rejected", TestInvalidAndCancelledInput),
    ("mastering display formats are stable and reject invalid values", TestMasteringFormats),
};
if (componentDirectory is not null)
    tests.Add(("real FFmpeg output validates HEVC and AV1 HDR/SDR samples", () => TestRealEncodes(componentDirectory, keepSamples)));
else
    Console.WriteLine("FFmpeg integration checks skipped; pass the bundled component directory to run them.");

int failed = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} checks passed.");
return failed == 0 ? 0 : 1;

static Task TestRgbaPacking()
{
    byte[] rgba = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255];
    var frame = StaticVideoPixels.Prepare(rgba, 3, 1, pq16: false);
    Equal((64, 64, false), (frame.Width, frame.Height, frame.Hdr), "dimensions / HDR flag");
    Equal(Srgb709(0), Get16(frame.Pixels, PlaneOffset(frame, 0)), "G plane, red pixel");
    Equal(Srgb709(255), Get16(frame.Pixels, PlaneOffset(frame, 1)), "G plane, green pixel");
    Equal(Srgb709(0), Get16(frame.Pixels, PlaneOffset(frame, 2)), "G plane, blue pixel");
    Equal(Srgb709(0), Get16(frame.Pixels, PlaneOffset(frame, frame.Width * 2)), "edge-clamped row");
    Equal(Srgb709(0), Get16(frame.Pixels, PlaneOffset(frame, 63)), "edge-clamped column");
    Equal(Srgb709(255), Get16(frame.Pixels, frame.Width * frame.Height * 2 + PlaneOffset(frame, 2)), "B plane, blue pixel");
    Equal(Srgb709(255), Get16(frame.Pixels, 2 * frame.Width * frame.Height * 2 + PlaneOffset(frame, 0)), "R plane, red pixel");
    return Task.CompletedTask;
}

static Task TestBgraPacking()
{
    byte[] bgra = [0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255];
    var frame = StaticVideoPixels.Prepare(bgra, 3, 1, pq16: false, bgra: true);
    Equal(Srgb709(255), Get16(frame.Pixels, PlaneOffset(frame, 1)), "G plane, green pixel");
    Equal(Srgb709(255), Get16(frame.Pixels, frame.Width * frame.Height * 2 + PlaneOffset(frame, 2)), "B plane");
    Equal(Srgb709(255), Get16(frame.Pixels, 2 * frame.Width * frame.Height * 2 + PlaneOffset(frame, 0)), "R plane");
    return Task.CompletedTask;
}

static Task TestPqPacking()
{
    ushort[] channels = [0x1234, 0x2345, 0x3456, 0x4567, 0x5678, 0x6789, 0x789A, 0x7ABC];
    byte[] rgb = new byte[channels.Length * 2];
    for (int i = 0; i < channels.Length; i++) BitConverter.TryWriteBytes(rgb.AsSpan(i * 2, 2), channels[i]);
    var frame = StaticVideoPixels.Prepare(rgb, 2, 1, pq16: true);
    int pixels = frame.Width * frame.Height;
    Equal(channels[1], Get16(frame.Pixels, PlaneOffset(frame, 0)), "G plane, first sample");
    Equal(channels[5], Get16(frame.Pixels, PlaneOffset(frame, 1)), "G plane, second sample");
    Equal(channels[2], Get16(frame.Pixels, pixels * 2 + PlaneOffset(frame, 0)), "B plane, first sample");
    Equal(channels[6], Get16(frame.Pixels, pixels * 2 + PlaneOffset(frame, 1)), "B plane, second sample");
    Equal(channels[0], Get16(frame.Pixels, pixels * 4 + PlaneOffset(frame, 0)), "R plane, first sample");
    Equal(channels[4], Get16(frame.Pixels, pixels * 4 + PlaneOffset(frame, 1)), "R plane, second sample");
    True(frame.Hdr, "PQ16 frame flagged HDR");
    return Task.CompletedTask;
}

static Task TestPadding()
{
    byte[] pixels = new byte[3 * 5 * 8];
    for (int i = 0; i < 15; i++)
    {
        ushort value = (ushort)(1000 + i);
        BitConverter.TryWriteBytes(pixels.AsSpan(i * 8, 2), value);
        BitConverter.TryWriteBytes(pixels.AsSpan(i * 8 + 2, 2), (ushort)(value + 100));
        BitConverter.TryWriteBytes(pixels.AsSpan(i * 8 + 4, 2), (ushort)(value + 200));
    }
    var frame = StaticVideoPixels.Prepare(pixels, 3, 5, pq16: true);
    Equal((64, 64), (frame.Width, frame.Height), "64-pixel minimum");
    Equal((ushort)1114, Get16(frame.Pixels, PlaneOffset(frame, 63 * frame.Width + 63)), "bottom-right edge replication");

    byte[] odd = new byte[65 * 67 * 4];
    for (int i = 0; i < 65 * 67; i++) { odd[i * 4] = (byte)i; odd[i * 4 + 1] = (byte)(i + 1); odd[i * 4 + 2] = (byte)(i + 2); odd[i * 4 + 3] = 255; }
    var oddFrame = StaticVideoPixels.Prepare(odd, 65, 67, pq16: false);
    Equal((66, 68), (oddFrame.Width, oddFrame.Height), "odd dimensions rounded up to even");
    Equal(Get16(oddFrame.Pixels, 65 * 2), Get16(oddFrame.Pixels, 64 * 2), "right edge replicated");
    Equal(Get16(oddFrame.Pixels, (67 * 66 + 65) * 2), Get16(oddFrame.Pixels, (66 * 66 + 64) * 2), "bottom-right edge replicated");
    return Task.CompletedTask;
}

static Task TestInvalidAndCancelledInput()
{
    Throws<ArgumentException>(() => StaticVideoPixels.Prepare([], 1, 1, false), "missing pixel bytes");
    Throws<ArgumentException>(() => StaticVideoPixels.Prepare(new byte[4], 0, 1, false), "zero width");
    Throws<ArgumentException>(() => StaticVideoPixels.Prepare(new byte[4], 1, 1, true), "wrong pixel stride");
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    Throws<OperationCanceledException>(() => StaticVideoPixels.Prepare(new byte[4], 1, 1, false, ct: cancellation.Token), "pre-cancelled packing");
    return Task.CompletedTask;
}

static Task TestMasteringFormats()
{
    var mastering = new VideoMastering(.708, .292, .170, .797, .131, .046, .3127, .3290, .005, 1000);
    True(mastering.IsValid, "valid mastering display");
    Equal("G(8500,39850)B(6550,2300)R(35400,14600)WP(15635,16450)L(10000000,50)", mastering.ForHevc(), "HEVC mastering metadata");
    Equal("G(0.17,0.797)B(0.131,0.046)R(0.708,0.292)WP(0.3127,0.329)L(1000,0.005)", mastering.ForAv1(), "AV1 mastering metadata");
    True(!(mastering with { MaxNits = 10001 }).IsValid, "max mastering luminance range");
    True(!(mastering with { MinNits = 1000 }).IsValid, "inverted mastering luminance");
    return Task.CompletedTask;
}

static async Task TestRealEncodes(string componentDirectory, bool keepSamples)
{
    componentDirectory = Path.GetFullPath(componentDirectory);
    string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Starshot.StaticVideoTest"));
    string workspace = Path.Combine(root, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(workspace);
    var outputs = new Dictionary<string, Dictionary<StaticVideoCodec, string>>();
    try
    {
        await StaticVideoEncoder.VerifyComponentsAsync(componentDirectory, CancellationToken.None);
        string missing = Path.Combine(workspace, "missing"); Directory.CreateDirectory(missing);
        await ThrowsAsync<FileNotFoundException>(
            () => StaticVideoEncoder.VerifyComponentsAsync(missing, CancellationToken.None), "missing manifest");

        string corrupt = Path.Combine(workspace, "corrupt"); Directory.CreateDirectory(corrupt);
        await File.WriteAllTextAsync(Path.Combine(corrupt, "sample.bin"), "tampered");
        await File.WriteAllTextAsync(Path.Combine(corrupt, "manifest.json"), "{\"files\":{\"sample.bin\":\"0000000000000000000000000000000000000000000000000000000000000000\"}}");
        await ThrowsAsync<InvalidDataException>(
            () => StaticVideoEncoder.VerifyComponentsAsync(corrupt, CancellationToken.None), "component hash corruption");

        string rejectedOutput = Path.Combine(workspace, "rejected.mp4");
        await ThrowsAsync<FileNotFoundException>(() => StaticVideoEncoder.EncodeAsync(MakeFrame(64, 64, true),
            StaticVideoCodec.Hevc, rejectedOutput, 1000, 400, null, CancellationToken.None,
            missing), "encode with missing components");
        await ThrowsAsync<InvalidDataException>(() => StaticVideoEncoder.EncodeAsync(MakeFrame(64, 64, true),
            StaticVideoCodec.Hevc, rejectedOutput, 1000, 400, null, CancellationToken.None, corrupt), "encode with corrupt component hash");
        await ThrowsAsync<ArgumentOutOfRangeException>(() => StaticVideoEncoder.EncodeAsync(MakeFrame(64, 64, true),
            (StaticVideoCodec)99, rejectedOutput, 1000, 400, null, CancellationToken.None, componentDirectory), "invalid encoder selection");
        True(!File.Exists(rejectedOutput), "failed encodes do not publish output");
        True(!Directory.EnumerateFiles(workspace, "*.partial", SearchOption.TopDirectoryOnly).Any(), "failed encode partial files removed");

        var mastering = new VideoMastering(.708, .292, .170, .797, .131, .046, .3127, .3290, .005, 1000);
        foreach (var codec in new[] { StaticVideoCodec.Hevc, StaticVideoCodec.Av1 })
        {
            foreach (var spec in new[]
            {
                (Name: "hdr-64x64", Width: 64, Height: 64, Hdr: true, VerifyLevels: true),
                (Name: "hdr-4k", Width: 3840, Height: 2160, Hdr: true, VerifyLevels: false),
                (Name: "sdr-odd", Width: 65, Height: 67, Hdr: false, VerifyLevels: false),
            })
            {
                string name = $"{spec.Name}-{codec.ToString().ToLowerInvariant()}.mp4";
                string output = Path.Combine(workspace, name);
                var frame = MakeFrame(spec.Width, spec.Height, spec.Hdr);
                await StaticVideoEncoder.EncodeAsync(frame, codec, output, 1000, 400, spec.Hdr ? mastering : null,
                    CancellationToken.None, componentDirectory);
                var expected = (spec.Width < 64 ? 64 : (spec.Width + 1) & ~1,
                    spec.Height < 64 ? 64 : (spec.Height + 1) & ~1);
                await VerifyOutput(componentDirectory, output, codec, spec.Hdr, expected.Item1, expected.Item2, spec.VerifyLevels);
                if (!outputs.TryGetValue(spec.Name, out var pair)) outputs[spec.Name] = pair = [];
                pair.Add(codec, output);
            }
        }

        string cancelOutput = Path.Combine(workspace, "cancelled.mp4");
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
            await ThrowsAsync<OperationCanceledException>(() => StaticVideoEncoder.EncodeAsync(
                MakeFrame(3840, 2160, true), StaticVideoCodec.Av1, cancelOutput, 1000, 400, mastering,
                cancellation.Token, componentDirectory), "encoder process cancellation");
        True(!File.Exists(cancelOutput), "cancelled output not committed");
        True(!Directory.EnumerateFiles(workspace, "*.partial", SearchOption.TopDirectoryOnly).Any(), "partial output removed");

        if (keepSamples)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            string sampleRoot = Path.Combine(projectRoot, "build", "hdr-video-samples");
            Directory.CreateDirectory(sampleRoot);
            foreach (string key in new[] { "hdr-64x64", "hdr-4k", "sdr-odd" })
                foreach (var (codec, path) in outputs[key])
                    File.Copy(path, Path.Combine(sampleRoot, Path.GetFileName(path)), overwrite: true);
        }
    }
    finally
    {
        string fullRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullWorkspace = Path.GetFullPath(workspace);
        if (!fullWorkspace.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ||
            Path.GetDirectoryName(fullWorkspace) != Path.GetFullPath(root))
            throw new InvalidOperationException("Refusing to clean a path outside the dedicated test temp root.");
        if (Directory.Exists(fullWorkspace)) Directory.Delete(fullWorkspace, recursive: true);
    }
}

static StaticVideoFrame MakeFrame(int width, int height, bool hdr)
{
    int stride = hdr ? 8 : 4;
    byte[] pixels = new byte[checked(width * height * stride)];
    for (int y = 0; y < height; y++)
    for (int x = 0; x < width; x++)
    {
        double nits = x < width / 2 ? 80 : 1000;
        ushort component = hdr ? Pq16(nits) : (byte)(x < width / 2 ? 128 : 255);
        int i = (y * width + x) * stride;
        if (hdr)
        {
            BitConverter.TryWriteBytes(pixels.AsSpan(i, 2), component);
            BitConverter.TryWriteBytes(pixels.AsSpan(i + 2, 2), component);
            BitConverter.TryWriteBytes(pixels.AsSpan(i + 4, 2), component);
            BitConverter.TryWriteBytes(pixels.AsSpan(i + 6, 2), ushort.MaxValue);
        }
        else pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)component;
    }
    return StaticVideoPixels.Prepare(pixels, width, height, hdr);
}

static async Task VerifyOutput(string directory, string path, StaticVideoCodec codec, bool hdr, int width, int height, bool verifyLevels)
{
    string json = await StaticVideoEncoder.RunTextAsync(Path.Combine(directory, "ffprobe.exe"),
        ["-v", "error", "-show_streams", "-show_format", "-show_frames", "-show_packets", "-of", "json", path], CancellationToken.None);
    using var doc = JsonDocument.Parse(json);
    var streams = doc.RootElement.GetProperty("streams");
    Equal(1, streams.GetArrayLength(), "exactly one stream (no audio)");
    var stream = streams[0];
    Equal(codec == StaticVideoCodec.Hevc ? "hevc" : "av1", stream.GetProperty("codec_name").GetString() ?? "<missing>", "codec");
    Equal(width, stream.GetProperty("width").GetInt32(), "encoded width");
    Equal(height, stream.GetProperty("height").GetInt32(), "encoded height");
    Equal("yuv420p10le", stream.GetProperty("pix_fmt").GetString() ?? "<missing>", "10-bit pixel format");
    if (codec == StaticVideoCodec.Hevc) Equal("Main 10", stream.GetProperty("profile").GetString() ?? "<missing>", "phone-compatible HEVC Main 10 profile");
    Near(3, double.Parse(stream.GetProperty("duration").GetString() ?? "NaN", CultureInfo.InvariantCulture), .001, "stream duration");
    JsonElement[] frames;
    JsonElement[] packets;
    if (doc.RootElement.TryGetProperty("packets_and_frames", out var combined))
    {
        var records = combined.EnumerateArray().ToArray();
        frames = records.Where(x => x.GetProperty("type").GetString() == "frame").ToArray();
        packets = records.Where(x => x.GetProperty("type").GetString() == "packet").ToArray();
    }
    else
    {
        frames = doc.RootElement.GetProperty("frames").EnumerateArray().ToArray();
        packets = doc.RootElement.GetProperty("packets").EnumerateArray().ToArray();
    }
    Equal(1, frames.Length, "exactly one decoded frame");
    Equal(1, packets.Length, "exactly one encoded packet");
    Near(3, double.Parse(packets[0].GetProperty("duration_time").GetString() ?? "NaN", CultureInfo.InvariantCulture), .001, "single packet duration");

    if (!hdr) return;
    Equal("smpte2084", stream.GetProperty("color_transfer").GetString() ?? "<missing>", "PQ transfer");
    Equal("bt2020", stream.GetProperty("color_primaries").GetString() ?? "<missing>", "BT.2020 primaries");
    Equal("bt2020nc", stream.GetProperty("color_space").GetString() ?? "<missing>", "BT.2020 matrix");
    var sideData = frames[0].GetProperty("side_data_list").EnumerateArray().ToArray();
    var light = sideData.Single(x => x.GetProperty("side_data_type").GetString() == "Content light level metadata");
    Equal(1000, light.GetProperty("max_content").GetInt32(), "MaxCLL");
    Equal(400, light.GetProperty("max_average").GetInt32(), "MaxFALL");
    var display = sideData.Single(x => x.GetProperty("side_data_type").GetString() == "Mastering display metadata");
    Near(1000, ParseNumberOrRatio(display.GetProperty("max_luminance")), .1, "mastering max luminance");
    Near(.005, ParseNumberOrRatio(display.GetProperty("min_luminance")), .0001, "mastering min luminance");
    if (verifyLevels)
    {
        byte[] planar = await DecodeFrame(directory, path);
        int planeBytes = checked(width * height * 2);
        True(planar.Length >= planeBytes * 3, "decoded planar RGB16 payload");
        double low = DecodePq16(Average16(planar, planeBytes * 2, width, height, 4, width / 4));
        double high = DecodePq16(Average16(planar, planeBytes * 2, width, height, width * 3 / 4, width / 4));
        Near(80, low, 22, "decoded 80-nit sample");
        Near(1000, high, 140, "decoded 1000-nit highlight");
    }
}

static async Task<byte[]> DecodeFrame(string directory, string path)
{
    using var process = StaticVideoEncoder.NewProcess(Path.Combine(directory, "ffmpeg.exe"),
        ["-hide_banner", "-loglevel", "error", "-i", path, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "gbrp16le", "pipe:1"]);
    process.Start();
    using var output = new MemoryStream();
    Task copy = process.StandardOutput.BaseStream.CopyToAsync(output);
    Task<string> error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    await copy;
    string errors = await error;
    if (process.ExitCode != 0) throw new InvalidOperationException("ffmpeg decode failed: " + errors);
    return output.ToArray();
}

static ushort Average16(byte[] bytes, int plane, int width, int height, int startX, int countX)
{
    ulong sum = 0; int n = 0;
    for (int y = height / 4; y < height * 3 / 4; y += Math.Max(1, height / 16))
    for (int x = startX; x < Math.Min(width, startX + countX); x += Math.Max(1, countX / 16))
    { sum += Get16(bytes, plane + (y * width + x) * 2); n++; }
    return (ushort)(sum / (ulong)n);
}

static double ParseNumberOrRatio(JsonElement element)
{
    string value = element.ToString();
    string[] parts = value.Split('/');
    return parts.Length == 1
        ? double.Parse(parts[0], CultureInfo.InvariantCulture)
        : double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture);
}

static ushort Pq16(double nits)
{
    const double m1 = 2610d / 16384, m2 = 2523d / 32, c1 = 3424d / 4096, c2 = 2413d / 128, c3 = 2392d / 128;
    double l = nits / 10000, p = Math.Pow(l, m1);
    return (ushort)Math.Round(Math.Pow((c1 + c2 * p) / (1 + c3 * p), m2) * ushort.MaxValue);
}

static double DecodePq16(ushort code)
{
    const double m1 = 2610d / 16384, m2 = 2523d / 32, c1 = 3424d / 4096, c2 = 2413d / 128, c3 = 2392d / 128;
    double p = Math.Pow(code / (double)ushort.MaxValue, 1 / m2);
    return 10000 * Math.Pow(Math.Max(p - c1, 0) / (c2 - c3 * p), 1 / m1);
}

static int PlaneOffset(StaticVideoFrame frame, int pixel) => pixel * 2;
static int Srgb709(byte value) { double s = value / 255d, linear = s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); double y = linear < .018 ? 4.5 * linear : 1.099 * Math.Pow(linear, .45) - .099; return (int)Math.Clamp(Math.Round(y * 65535), 0, 65535); }
static ushort Get16(byte[] data, int offset) => BitConverter.ToUInt16(data, offset);

static void Equal<T>(T expected, T actual, string label) where T : notnull
{ if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{label}: expected {expected}, got {actual}."); }
static void True(bool value, string label) { if (!value) throw new InvalidOperationException(label); }
static void Near(double expected, double actual, double tolerance, string label)
{ if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance) throw new InvalidOperationException($"{label}: expected {expected} ± {tolerance}, got {actual:F2}."); }
static void Throws<T>(Action action, string label) where T : Exception
{ try { action(); } catch (T) { return; } throw new InvalidOperationException($"{label}: expected {typeof(T).Name}."); }
static async Task ThrowsAsync<T>(Func<Task> action, string label) where T : Exception
{ try { await action(); } catch (T) { return; } throw new InvalidOperationException($"{label}: expected {typeof(T).Name}."); }
