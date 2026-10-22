namespace Starshot
{
    internal static class AppConfig
    {
        public static string UserDataFolder{get;set;}="";
        public static bool LocalTranslationGpu{get;set;}
        public static int LocalTranslationApiPort{get;set;}=43182;
        public static string TranslationBackend{get;set;}="local";
        public static string TranslationApiUrl{get;set;}="https://example.invalid";
        public static string TranslationModel{get;set;}="test";
    }
}
namespace Starshot.Helpers
{
    internal sealed class DlcModelStore
    {
        public static readonly DlcModelStore Current=new();
        public static string ModelPath="";
        public Task<string> RequireAsync(string id,CancellationToken ct){ct.ThrowIfCancellationRequested();return Task.FromResult(ModelPath);}
    }
}
