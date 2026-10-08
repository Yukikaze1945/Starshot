namespace Starshot.Features.Screenshot
{
    public enum RegionCaptureAction { Cancel, Save, Copy, Ocr, Translate, Pin, RecordGif, LongCapture }
}
namespace Starshot.Helpers
{
    internal static class ClipboardHelper { internal static void SetText(string text) => throw new InvalidOperationException("Tests must not touch the user's clipboard"); }
}
namespace Serilog
{
    internal static class Log
    {
        internal static void Information(string template, params object[] values) { }
        internal static void Warning(string template, params object[] values) => Console.Error.WriteLine(template);
        internal static void Warning(Exception ex, string template, params object[] values) => Console.Error.WriteLine($"{template}: {ex}");
        internal static void Error(Exception ex, string template, params object[] values) => Console.Error.WriteLine($"{template}: {ex}");
    }
}
