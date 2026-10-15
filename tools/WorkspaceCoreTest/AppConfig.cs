namespace Starshot;
internal static class AppConfig
{
    public static string UserDataFolder {get;set;}="";
    public static string TranslationBackend {get;set;}="local";
    public static string TranslationApiUrl {get;set;}="https://example.invalid/v1/chat/completions";
    public static string TranslationModel {get;set;}="test";
}
