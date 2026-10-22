namespace Starshot {
internal static class AppConfig
{
    internal static bool OcrGpuAcceleration { get; set; }
    internal static string UserDataFolder { get; set; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Starshot-OcrTinyTest");
    internal static string OcrModel { get; set; } = "tiny";
    private static readonly System.Collections.Generic.Dictionary<string,object> Settings=[];
    internal static T GetValue<T>(T fallback,string key)=>Settings.TryGetValue(key,out var value)?(T)value:fallback;
    internal static void SetValue<T>(T value,string key)=>Settings[key]=value!;
}
}
namespace Starshot.Helpers {
public static class DlcModelStore {
 public sealed record Model(string Id,string Name,string Task,long Bytes,string Sha256,string Url,string File,string License,string Source);
 public sealed record Status(string Id,string Name,string Task,long Bytes,long Downloaded,string State,string? Error,string License,string Source,bool Installed,string Sha256,string? Warning,string? ActualSha256,string Directory,string? ActiveSource,ModelDownloadSource[] Sources);
}}
