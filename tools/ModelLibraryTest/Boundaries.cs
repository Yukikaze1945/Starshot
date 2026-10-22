namespace Starshot
{
    internal static class AppConfig
    {
        public static string UserDataFolder => throw new System.Exception("Tests must use isolated roots");
        private static readonly Dictionary<string,object> Settings=[];
        internal static T GetValue<T>(T fallback,string key)=>Settings.TryGetValue(key,out var value)?(T)value:fallback;
        internal static void SetValue<T>(T value,string key)=>Settings[key]=value!;
    }
}
namespace Starshot.Helpers
{
    internal static class OcrHelper { internal static Task MutateSmallFilesAsync(Action action,CancellationToken ct){ct.ThrowIfCancellationRequested();action();return Task.CompletedTask;} }
    internal static class OfflineTranslation { internal static Task UnloadAsync(CancellationToken ct)=>Task.CompletedTask; }
    internal static class BackgroundRemoval { internal static Task UnloadAsync(CancellationToken ct)=>Task.CompletedTask; }
}
namespace Serilog
{
    internal static class Log
    {
        internal static void Information(string message,params object?[] values){}
        internal static void Warning(Exception ex,string message,params object?[] values){}
    }
}
