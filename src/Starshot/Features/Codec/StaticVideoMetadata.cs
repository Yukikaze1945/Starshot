using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Starshot.Features.Codec;

internal sealed record StaticVideoDescriptor(string? ImagePath, string? Codec, bool? Hdr,
    int? DurationMs = null, int? FrameCount = null, string? FrameRate = null);

internal static class StaticVideoMetadata
{
    internal static bool IsVideo(string path) => Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase);

    internal static void Write(string video, string image, StaticVideoCodec codec, bool hdr,
        int frameRate = StaticVideoEncoder.DefaultFrameRate)
    {
        string path = video + ".starshot.json", temp = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            int frameCount = StaticVideoEncoder.FrameCountForRate(frameRate);
            string codecName = codec == StaticVideoCodec.Hevc ? "HEVC" : "AV1";
            File.WriteAllText(temp, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                kind = "hdr-still-video",
                videoFilename = Path.GetFileName(video),
                imageFilename = Path.GetFileName(image),
                durationMs = StaticVideoEncoder.DurationMs,
                frameCount,
                frameRate = frameRate.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/1",
                codec = codecName,
                hdr,
                // Legacy aliases remain for the existing Windows gallery reader.
                version = 1,
                image = Path.GetFileName(image)
            }));
            File.Move(temp, path, overwrite: false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static StaticVideoDescriptor Read(string video)
    {
        string? image = null, codec = null, frameRate = null; bool? hdr = null;
        int? durationMs = null, frameCount = null;
        try
        {
            string path = video + ".starshot.json";
            if (File.Exists(path) && new FileInfo(path).Length <= 4096)
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                var root = json.RootElement;
                bool modern = root.TryGetProperty("schemaVersion", out var schema) && schema.GetInt32() == 1
                    && root.TryGetProperty("kind", out var kind) && kind.GetString() == "hdr-still-video";
                string? videoName = modern && root.TryGetProperty("videoFilename", out var videoValue)
                    ? videoValue.GetString() : Path.GetFileName(video);
                string? name = modern && root.TryGetProperty("imageFilename", out var imageValue)
                    ? imageValue.GetString()
                    : root.TryGetProperty("image", out var legacyImage) ? legacyImage.GetString() : null;
                if (videoName == Path.GetFileName(video) && name is not null && Path.GetFileName(name) == name)
                {
                    string candidate = Path.Combine(Path.GetDirectoryName(video)!, name);
                    if (File.Exists(candidate)) image = candidate;
                }
                if (root.TryGetProperty("codec", out var codecValue))
                {
                    string? value = codecValue.GetString();
                    if (value is "HEVC" or "AV1") codec = value;
                }
                if (root.TryGetProperty("hdr", out var hdrValue)) hdr = hdrValue.GetBoolean();
                if (modern)
                {
                    if (root.TryGetProperty("durationMs", out var durationValue)) durationMs = durationValue.GetInt32();
                    if (root.TryGetProperty("frameCount", out var countValue)) frameCount = countValue.GetInt32();
                    if (root.TryGetProperty("frameRate", out var rateValue)) frameRate = rateValue.GetString();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException or FormatException) { }
        image ??= new[] { ".avif", ".jxl", ".heic", ".heif", ".png", ".jpg" }.Select(ext => Path.ChangeExtension(video, ext)).FirstOrDefault(File.Exists);
        return new(image, codec, hdr, durationMs, frameCount, frameRate);
    }
}
