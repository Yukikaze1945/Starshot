using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Starshot.Features.Codec;

internal sealed record HeicImage(byte[] Pixels, int Width, int Height, int Primaries, int Transfer,
    int Depth, byte[] Icc, uint MaxCll, uint MaxFall)
{
    public bool Hdr => Transfer is 16 or 18;
}

/// <summary>Bounded CPU-only codec process. No WIC extension, GPU context or temporary image is required.</summary>
internal static class HeicCodec
{
    internal static string ComponentDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "HeicCodec");
    private static readonly SemaphoreSlim Gate = new(1);
    private static string? _verifiedPath;

    internal static async Task<byte[]> EncodeAsync(byte[] pixels, int width, int height, string format,
        int quality, bool hdr, byte[]? xmp = null, float? maxCll = null, float? maxFall = null,
        int primaries = 1, CancellationToken ct = default)
    {
        int stride = format == "rgba16" ? 8 : format is "rgba8" or "bgra8" ? 4 : throw new ArgumentException("Unsupported HEIC pixels");
        if (width < 1 || height < 1 || (long)width * height > 64_000_000 || pixels.Length != checked(width * height * stride))
            throw new ArgumentException("Invalid HEIC pixel buffer");
        if (hdr && stride != 8) throw new ArgumentException("HEIC HDR input must be PQ RGB16");
        xmp ??= [];
        return await RunAsync(new[] { "--encode", N(width), N(height), format, N(Math.Clamp(quality, 1, 100)),
            hdr ? "1" : primaries == 12 ? "2" : "0", N(Light(maxCll)), N(Light(maxFall)), N(xmp.Length) }, pixels, xmp,
            async (stream, token) =>
            {
                using var result = new MemoryStream();
                var buffer = new byte[65536];
                int count;
                while ((count = await stream.ReadAsync(buffer, token)) != 0)
                {
                    if (result.Length + count > 512L * 1024 * 1024) throw new InvalidDataException("HEIC output exceeds memory limit");
                    await result.WriteAsync(buffer.AsMemory(0, count), token);
                }
                return result.ToArray();
            }, ct);
    }

    internal static Task<HeicImage> DecodeAsync(string path, int maximum = 0, CancellationToken ct = default) =>
        RunAsync(new[] { "--decode", Path.GetFullPath(path), N(maximum) }, null, null, async (stream, token) =>
        {
            byte[] header = new byte[36]; await stream.ReadExactlyAsync(header, token);
            uint U(int i) => BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(i * 4));
            int w = checked((int)U(1)), h = checked((int)U(2)), depth = checked((int)U(5)), iccLength = checked((int)U(6));
            if (U(0) != 0x31434948 || w <= 0 || h <= 0 || (long)w * h > 64_000_000 || depth < 8 || depth > 16 || iccLength > 1048576)
                throw new InvalidDataException("Invalid HEIC decoder response");
            byte[] icc = new byte[iccLength], pixels = new byte[checked(w * h * 8)];
            await stream.ReadExactlyAsync(icc, token); await stream.ReadExactlyAsync(pixels, token);
            if (await stream.ReadAsync(new byte[1], token) != 0) throw new InvalidDataException("Unexpected HEIC decoder payload");
            return new HeicImage(pixels, w, h, (int)U(3), (int)U(4), depth, icc, U(7), U(8));
        }, ct);

    internal static Task<(uint Width, uint Height)> SizeAsync(string path, CancellationToken ct = default) =>
        RunAsync(new[] { "--size", Path.GetFullPath(path) }, null, null, async (stream, token) =>
        {
            using var reader = new StreamReader(stream);
            var values = (await reader.ReadToEndAsync(token)).Split(' ');
            if (values.Length != 2 || !uint.TryParse(values[0], out uint w) || !uint.TryParse(values[1], out uint h)
                || w == 0 || h == 0 || (ulong)w * h > 64_000_000) throw new InvalidDataException("Invalid HEIC dimensions");
            return (w, h);
        }, ct);

    private static async Task<T> RunAsync<T>(string[] args, byte[]? input, byte[]? tail,
        Func<Stream, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            string executable = Path.Combine(ComponentDirectory, "StarshotHeic.exe");
            if (_verifiedPath != executable)
            {
                using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(ComponentDirectory, "manifest.json"), ct));
                string expected = manifest.RootElement.GetProperty("sha256").GetString()!;
                await using var file = File.OpenRead(executable);
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("HEIC 组件校验失败");
                _verifiedPath = executable;
            }
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in args) start.ArgumentList.Add(arg);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromMinutes(3));
            using var process = Process.Start(start) ?? throw new IOException("Cannot start HEIC codec");
            using var cancellation = linked.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            var output = read(process.StandardOutput.BaseStream, linked.Token);
            var errors = process.StandardError.ReadToEndAsync(linked.Token);
            var write = WriteAsync();
            var exit = process.WaitForExitAsync(linked.Token);
            // An early pipe failure must also stop the other side, rather than wait
            // forever with a full stdout buffer or a codec still waiting for input.
            foreach (Task task in new Task[] { output, errors, write, exit })
                _ = task.ContinueWith(_ => linked.Cancel(), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try
            {
                await Task.WhenAll(output, errors, write, exit);
                if (process.ExitCode != 0) throw new InvalidDataException("HEIC: " + (await errors).Trim());
                return await output;
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
                ct.ThrowIfCancellationRequested();
                string detail = errors.IsCompletedSuccessfully ? errors.Result.Trim() : "";
                if (detail.Length > 0) throw new InvalidDataException("HEIC: " + detail);
                throw;
            }
            async Task WriteAsync()
            {
                try
                {
                    if (input is not null) await process.StandardInput.BaseStream.WriteAsync(input, linked.Token);
                    if (tail is not null) await process.StandardInput.BaseStream.WriteAsync(tail, linked.Token);
                }
                finally { process.StandardInput.Close(); }
            }
        }
        finally { Gate.Release(); }
    }

    // Preview-only CPU conversion. Full HDR loading keeps PQ RGB16 and uses the
    // existing HDR10->scRGB viewer effect; ordinary SDR export remains Perceptual.
    internal static byte[] PreviewBgra(HeicImage image)
    {
        byte[] output = new byte[checked(image.Width * image.Height * 4)];
        for (int i = 0, d = 0; i < image.Pixels.Length; i += 8, d += 4)
        {
            float V(int c) => BinaryPrimitives.ReadUInt16LittleEndian(image.Pixels.AsSpan(i + c * 2)) / 65535f;
            var rgb = new Vector3(V(0), V(1), V(2));
            if (image.Transfer == 18) throw new NotSupportedException("HLG HEIC preview is not supported yet");
            if (image.Transfer == 16)
            {
                rgb = new(PqLinear(rgb.X), PqLinear(rgb.Y), PqLinear(rgb.Z));
            }
            else if (image.Transfer == 1) rgb = new(Bt709(rgb.X), Bt709(rgb.Y), Bt709(rgb.Z));
            else if (image.Transfer is 13 or 2) rgb = new(Linear(rgb.X), Linear(rgb.Y), Linear(rgb.Z));
            else throw new NotSupportedException("HEIC preview transfer is not supported");
            if (image.Primaries == 9) rgb = new(1.660491f * rgb.X - .587641f * rgb.Y - .07285f * rgb.Z,
                -.124550f * rgb.X + 1.132899f * rgb.Y - .008349f * rgb.Z, -.018151f * rgb.X - .100579f * rgb.Y + 1.11873f * rgb.Z);
            else if (image.Primaries == 12) rgb = new(1.224745f * rgb.X - .224904f * rgb.Y,
                -.042058f * rgb.X + 1.042081f * rgb.Y, -.019642f * rgb.X - .078655f * rgb.Y + 1.098537f * rgb.Z);
            if (image.Transfer == 16)
            {
                float y = Math.Max(0, Vector3.Dot(rgb, new(.2126f, .7152f, .0722f)));
                if (y > .8f) rgb *= (.8f + .2f * (1 - MathF.Exp(-5 * (y - .8f)))) / y;
            }
            rgb = new(Srgb(rgb.X), Srgb(rgb.Y), Srgb(rgb.Z));
            output[d] = Q(rgb.Z); output[d + 1] = Q(rgb.Y); output[d + 2] = Q(rgb.X); output[d + 3] = 255;
        }
        return output;
    }
    private static float PqLinear(float value)
    {
        double p = Math.Pow(Math.Clamp(value, 0, 1), 32d / 2523);
        return (float)(Math.Pow(Math.Max(p - 3424d / 4096, 0) / Math.Max(2413d / 128 - 2392d / 128 * p, 1e-12), 16384d / 2610) * 10000 / 203);
    }
    private static float Srgb(float x) => x <= .0031308f ? 12.92f * x : 1.055f * MathF.Pow(x, 1 / 2.4f) - .055f;
    private static float Linear(float x) => x <= .04045f ? x / 12.92f : MathF.Pow((x + .055f) / 1.055f, 2.4f);
    private static float Bt709(float x) => x < .081f ? x / 4.5f : MathF.Pow((x + .099f) / 1.099f, 1 / .45f);
    private static byte Q(float x) => (byte)Math.Clamp(MathF.Round(x * 255), 0, 255);
    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static int Light(float? value) => value is { } v && float.IsFinite(v) ? (int)Math.Clamp(MathF.Round(v), 0, 65535) : 0;
}
