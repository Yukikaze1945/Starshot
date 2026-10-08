using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Starshot.Features.Codec;

internal sealed record StaticVideoDescriptor(string? ImagePath, string? Codec, bool? Hdr);
internal static class StaticVideoMetadata
{
    internal static bool IsVideo(string path) => Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase);
    internal static void Write(string video, string image, StaticVideoCodec codec, bool hdr)
    {
        string path = video + ".starshot.json", temp = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new { version = 1, image = Path.GetFileName(image), codec = codec == StaticVideoCodec.Hevc ? "HEVC" : "AV1", hdr }));
            File.Move(temp, path, overwrite: false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static StaticVideoDescriptor Read(string video)
    {
        string? image = null, codec = null; bool? hdr = null;
        try
        {
            string path = video + ".starshot.json";
            if (File.Exists(path) && new FileInfo(path).Length <= 4096)
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                var root = json.RootElement;
                string? name = root.GetProperty("image").GetString();
                if (name is not null && Path.GetFileName(name) == name)
                {
                    string candidate = Path.Combine(Path.GetDirectoryName(video)!, name);
                    if (File.Exists(candidate)) image = candidate;
                }
                string? value = root.GetProperty("codec").GetString();
                if (value is "HEVC" or "AV1") codec = value;
                hdr = root.GetProperty("hdr").GetBoolean();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException) { }
        image ??= new[] { ".avif", ".jxl", ".png", ".jpg" }.Select(ext => Path.ChangeExtension(video, ext)).FirstOrDefault(File.Exists);
        return new(image, codec, hdr);
    }
}
