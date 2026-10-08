namespace Starshot;
internal static class AppConfig
{
    internal static string UserDataFolder { get; set; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Starshot-OcrTinyTest");
    internal static string OcrModel { get; set; } = "tiny";
}
