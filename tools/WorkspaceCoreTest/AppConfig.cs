namespace Starshot;
internal static class AppConfig
{
    public static bool LocalTranslationGpu {get;set;}
    public static int LocalTranslationApiPort {get;set;}=43182;
    public static string UserDataFolder {get;set;}="";
    public static string TranslationBackend {get;set;}="local";
    public static string TranslationApiUrl {get;set;}="https://example.invalid/v1/chat/completions";
    public static string TranslationModel {get;set;}="test";
    private static readonly System.Collections.Generic.Dictionary<string,object> Settings=[];
    internal static T GetValue<T>(T fallback,string key)=>Settings.TryGetValue(key,out var value)?(T)value:fallback;
    internal static void SetValue<T>(T value,string key)=>Settings[key]=value!;
}
