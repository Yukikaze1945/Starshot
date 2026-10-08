namespace Starshot;
internal static class AppConfig
{
    internal static string UserDataFolder { get; set; } = "";
    internal static string OcrModel
    {
        get => File.Exists(Path.Combine(UserDataFolder, "choice.txt")) ? File.ReadAllText(Path.Combine(UserDataFolder, "choice.txt")) : "tiny";
        set => File.WriteAllText(Path.Combine(UserDataFolder, "choice.txt"), value);
    }
}
