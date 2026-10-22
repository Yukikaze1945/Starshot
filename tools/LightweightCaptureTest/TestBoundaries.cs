namespace Starshot.Helpers
{
    internal static class ClipboardHelper { internal static void SetText(string text) => throw new InvalidOperationException("Tests must not touch the user's clipboard"); }
    internal sealed record OcrLine(string Text,System.Drawing.RectangleF Rect);
    internal static class OcrHelper
    {
        internal static System.Collections.Generic.List<OcrLine> Lines=[];
        internal static Task? Pending;internal static volatile bool Started;
        internal static async Task<System.Collections.Generic.List<OcrLine>?> RecognizeAsync(byte[] p,int w,int h,double scale,CancellationToken ct)
        {Started=true;if(Pending is{} pending)await pending.WaitAsync(ct);ct.ThrowIfCancellationRequested();return Lines;}
    }
    internal static class OcrTranslationClient
    {
        internal sealed record Segment(string Id,string Text);
        internal static int Segments;
        internal static Task<System.Collections.Generic.IReadOnlyList<Segment>> TranslateFormattedAsync(System.Collections.Generic.IReadOnlyList<Segment> rows,string language,CancellationToken ct)
        {ct.ThrowIfCancellationRequested();if(rows.Any(r=>string.IsNullOrWhiteSpace(r.Text)))throw new Exception("Empty OCR reached translation");Segments=rows.Count;return Task.FromResult<System.Collections.Generic.IReadOnlyList<Segment>>(rows.Select(r=>new Segment(r.Id,"译："+r.Text)).ToArray());}
    }
}
namespace Starshot {internal static class AppConfig{internal static string TranslationTargetLanguage=>"简体中文";}}
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
