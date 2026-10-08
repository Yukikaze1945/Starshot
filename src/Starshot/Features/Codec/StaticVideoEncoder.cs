using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Starshot.Features.Codec;

internal enum StaticVideoCodec { Hevc = 0, Av1 = 1 }
internal sealed record StaticVideoFrame(byte[] Pixels, int Width, int Height, bool Hdr);
internal sealed record VideoMastering(double Rx, double Ry, double Gx, double Gy, double Bx,
    double By, double Wx, double Wy, double MinNits, double MaxNits)
{
    public bool IsValid => new[] { Rx, Ry, Gx, Gy, Bx, By, Wx, Wy }.All(x => double.IsFinite(x) && x > 0 && x < 1)
        && double.IsFinite(MinNits) && double.IsFinite(MaxNits) && MinNits >= 0 && MaxNits > MinNits && MaxNits <= 10000;
    public string ForHevc() => FormattableString.Invariant($"G({Q(Gx)},{Q(Gy)})B({Q(Bx)},{Q(By)})R({Q(Rx)},{Q(Ry)})WP({Q(Wx)},{Q(Wy)})L({(long)Math.Round(MaxNits * 10000)},{(long)Math.Round(MinNits * 10000)})");
    public string ForAv1() => FormattableString.Invariant($"G({Gx},{Gy})B({Bx},{By})R({Rx},{Ry})WP({Wx},{Wy})L({MaxNits},{MinNits})");
    private static int Q(double x) => (int)Math.Round(x * 50000);
}

/// <summary>CPU frame packing; HDR input is already PQ RGB16 from the existing scRGB shader.</summary>
internal static class StaticVideoPixels
{
    public static StaticVideoFrame Prepare(byte[] packed, int width, int height, bool pq16, bool bgra = false,
        CancellationToken ct = default)
    {
        int stride = pq16 ? 8 : 4;
        if (width < 1 || height < 1 || packed.Length != checked(width * height * stride))
            throw new ArgumentException("Invalid frozen screenshot pixels.");
        int w = Math.Max(64, checked((width + 1) & ~1)), h = Math.Max(64, checked((height + 1) & ~1));
        int plane = checked(w * h * 2);
        byte[] output = new byte[checked(plane * 3)]; // FFmpeg gbrp16le: G, B, R.
        for (int y = 0; y < h; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                int source = (Math.Min(y, height - 1) * width + Math.Min(x, width - 1)) * stride;
                int target = (y * w + x) * 2;
                if (pq16)
                {
                    Copy16(packed, source + 2, output, target);
                    Copy16(packed, source + 4, output, plane + target);
                    Copy16(packed, source, output, 2 * plane + target);
                }
                else
                {
                    Write16(output, target, SrgbTo709(packed[source + 1]));
                    Write16(output, plane + target, SrgbTo709(packed[source + (bgra ? 0 : 2)]));
                    Write16(output, 2 * plane + target, SrgbTo709(packed[source + (bgra ? 2 : 0)]));
                }
            }
        }
        return new(output, w, h, pq16);
    }
    private static ushort SrgbTo709(byte value)
    {
        double s = value / 255d, linear = s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4);
        double result = linear < .018 ? 4.5 * linear : 1.099 * Math.Pow(linear, .45) - .099;
        return (ushort)Math.Clamp(Math.Round(result * 65535), 0, 65535);
    }
    private static void Copy16(byte[] source, int s, byte[] output, int d) { output[d] = source[s]; output[d + 1] = source[s + 1]; }
    private static void Write16(byte[] output, int d, ushort v) { output[d] = (byte)v; output[d + 1] = (byte)(v >> 8); }
}

/// <summary>One process, one input frame, one MP4 sample. Never searches PATH or uses GPU encoders.</summary>
internal static class StaticVideoEncoder
{
    private static readonly SemaphoreSlim Gate = new(1);
    private static readonly SemaphoreSlim VerificationGate = new(1);
    private static string? _verifiedDirectory;
    public static string ComponentDirectory => Path.Combine(AppContext.BaseDirectory, "VideoEncoder");

    public static async Task VerifyComponentsAsync(string directory, CancellationToken ct)
    {
        await VerificationGate.WaitAsync(ct);
        try
        {
            if (_verifiedDirectory == directory) return;
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json"), ct));
            foreach (var file in manifest.RootElement.GetProperty("files").EnumerateObject())
            {
                if (Path.GetFileName(file.Name) != file.Name) throw new InvalidDataException("Invalid encoder manifest.");
                await using var stream = File.OpenRead(Path.Combine(directory, file.Name));
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                if (!actual.Equals(file.Value.GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"视频编码组件校验失败：{file.Name}");
            }
            string capabilities = await RunTextAsync(Path.Combine(directory, "ffmpeg.exe"), new[] { "-hide_banner", "-encoders" }, ct);
            if (!capabilities.Contains("libx265", StringComparison.Ordinal) || !capabilities.Contains("libsvtav1", StringComparison.Ordinal))
                throw new InvalidDataException("视频编码组件缺少 HEVC／AV1 编码器。");
            _verifiedDirectory = directory;
        }
        finally { VerificationGate.Release(); }
    }

    internal static IReadOnlyList<string> Arguments(StaticVideoFrame frame, StaticVideoCodec codec,
        string output, float maxCll, float maxFall, VideoMastering? mastering)
    {
        if (!Enum.IsDefined(codec)) throw new ArgumentOutOfRangeException(nameof(codec));
        string matrix = frame.Hdr ? "bt2020" : "bt709";
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-f", "rawvideo", "-pixel_format", "gbrp16le", "-video_size", $"{frame.Width}x{frame.Height}",
            "-framerate", "1/3", "-i", "pipe:0", "-an", "-frames:v", "1", "-fps_mode", "passthrough",
            "-vf", $"scale=in_range=full:out_range=tv:out_color_matrix={matrix},format=yuv420p10le",
            "-color_range", "tv", "-color_primaries", frame.Hdr ? "bt2020" : "bt709",
            "-color_trc", frame.Hdr ? "smpte2084" : "bt709", "-colorspace", frame.Hdr ? "bt2020nc" : "bt709" };
        int threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        string cll = $"{Light(maxCll)},{Light(maxFall)}";
        if (codec == StaticVideoCodec.Hevc)
        {
            string options = $"bframes=0:rc-lookahead=0:keyint=250:pools={threads}:frame-threads=1:repeat-headers=0:range=limited";
            options += frame.Hdr ? ":colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc" : ":colorprim=bt709:transfer=bt709:colormatrix=bt709";
            if (frame.Hdr)
            {
                options += $":hdr10=1:hdr10-opt=1:max-cll={cll}";
                if (mastering?.IsValid == true) options += ":master-display=" + mastering.ForHevc();
            }
            args.AddRange(new[] { "-c:v", "libx265", "-preset", "medium", "-crf", "18", "-profile:v", "main10", "-tag:v", "hvc1", "-x265-params", options });
        }
        else
        {
            string options = $"lp={threads}:lookahead=0:scm=2:color-range=0";
            if (!frame.Hdr) options += ":color-primaries=1:transfer-characteristics=1:matrix-coefficients=1";
            if (frame.Hdr)
            {
                options += $":enable-hdr=1:color-primaries=9:transfer-characteristics=16:matrix-coefficients=9:content-light={cll}";
                if (mastering?.IsValid == true) options += ":mastering-display=" + mastering.ForAv1();
            }
            args.AddRange(new[] { "-c:v", "libsvtav1", "-preset", "8", "-crf", "20", "-tag:v", "av01", "-svtav1-params", options });
        }
        args.AddRange(new[] { "-bsf:v", "setts=pts=0:dts=0:duration=3/TB", "-movflags", "+faststart+write_colr", "-video_track_timescale", "90000", "-f", "mp4", output });
        return args;
    }
    private static int Light(float value) => float.IsFinite(value) && value > 0 ? (int)Math.Clamp(Math.Round(value), 1, ushort.MaxValue) : 0;

    public static async Task EncodeAsync(StaticVideoFrame frame, StaticVideoCodec codec, string output,
        float maxCll, float maxFall, VideoMastering? mastering, CancellationToken ct, string? directory = null)
    {
        await Gate.WaitAsync(ct);
        string partial = output + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            directory ??= ComponentDirectory;
            await VerifyComponentsAsync(directory, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            using var process = NewProcess(Path.Combine(directory, "ffmpeg.exe"), Arguments(frame, codec, partial, maxCll, maxFall, mastering), true);
            process.Start();
            using var registration = timeout.Token.Register(() => Kill(process));
            Task<string> errors = process.StandardError.ReadToEndAsync();
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(frame.Pixels, timeout.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                string error = await errors;
                if (process.ExitCode != 0) throw new InvalidOperationException("视频编码失败：" + error[^Math.Min(2000, error.Length)..]);
                timeout.Token.ThrowIfCancellationRequested();
                await ValidateAsync(partial, frame, codec, maxCll, maxFall, mastering, directory, timeout.Token);
                File.Move(partial, output, overwrite: false);
            }
            finally
            {
                Kill(process);
                await process.WaitForExitAsync();
                await errors;
            }
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); }
            finally { Gate.Release(); }
        }
    }

    private static async Task ValidateAsync(string path, StaticVideoFrame frame, StaticVideoCodec codec,
        float maxCll, float maxFall, VideoMastering? mastering, string directory, CancellationToken ct)
    {
        string json = await RunTextAsync(Path.Combine(directory, "ffprobe.exe"), new[] { "-v", "error", "-count_frames", "-show_streams", "-show_frames", "-show_format", "-of", "json", path }, ct);
        using var document = JsonDocument.Parse(json);
        var streams = document.RootElement.GetProperty("streams");
        if (streams.GetArrayLength() != 1) throw new InvalidDataException("单帧视频包含额外轨道。");
        var video = streams[0];
        bool valid = video.GetProperty("codec_name").GetString() == (codec == StaticVideoCodec.Hevc ? "hevc" : "av1")
            && video.GetProperty("nb_read_frames").GetString() == "1"
            && video.GetProperty("width").GetInt32() == frame.Width && video.GetProperty("height").GetInt32() == frame.Height
            && video.GetProperty("pix_fmt").GetString() == "yuv420p10le"
            && video.GetProperty("color_transfer").GetString() == (frame.Hdr ? "smpte2084" : "bt709")
            && video.GetProperty("color_primaries").GetString() == (frame.Hdr ? "bt2020" : "bt709")
            && video.GetProperty("color_space").GetString() == (frame.Hdr ? "bt2020nc" : "bt709")
            && video.GetProperty("color_range").GetString() == "tv"
            && Math.Abs(double.Parse(video.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture) - 3) < .001;
        if (codec == StaticVideoCodec.Hevc) valid &= video.GetProperty("profile").GetString() == "Main 10";
        var frames = document.RootElement.GetProperty("frames");
        valid &= frames.GetArrayLength() == 1;
        if (valid && frame.Hdr)
        {
            var data = frames[0].TryGetProperty("side_data_list", out var sideData) ? sideData.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
            if (Light(maxCll) > 0 || Light(maxFall) > 0)
                valid &= data.Any(d => d.GetProperty("side_data_type").GetString() == "Content light level metadata"
                    && d.GetProperty("max_content").GetInt32() == Light(maxCll) && d.GetProperty("max_average").GetInt32() == Light(maxFall));
            if (mastering?.IsValid == true)
                valid &= data.Any(d => d.GetProperty("side_data_type").GetString() == "Mastering display metadata");
        }
        if (!valid) throw new InvalidDataException("单帧视频的帧数、时长或 HDR／色彩数据验证失败。");
    }

    internal static Process NewProcess(string executable, IEnumerable<string> arguments, bool input = false)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = input, RedirectStandardError = true, RedirectStandardOutput = !input,
            WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return new Process { StartInfo = start };
    }
    internal static async Task<string> RunTextAsync(string executable, IEnumerable<string> arguments, CancellationToken ct)
    {
        using var process = NewProcess(executable, arguments);
        process.Start();
        using var registration = ct.Register(() => Kill(process));
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(ct);
            string result = await output, errors = await error;
            if (process.ExitCode != 0) throw new InvalidOperationException(errors);
            return result;
        }
        finally { Kill(process); await process.WaitForExitAsync(); await Task.WhenAll(output, error); }
    }
    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
