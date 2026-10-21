using Starshot.Features.Screenshot;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var baseTime = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
var collector = new SubtitleOcrCollector();

Check(collector.Observe(["第一句话", "第二句话"], baseTime).Count == 2, "First two lines");
Check(collector.Observe(["第一句话", "第二句话"], baseTime.AddSeconds(1)).Count == 0, "No duplicate frames");
Check(collector.Observe([" 第二 句话 ", "第三句话"], baseTime.AddSeconds(2)).Count == 1, "Overlapping rolling subtitle");
Check(collector.Observe([], baseTime.AddSeconds(3)).Count == 0, "Blank frame");
Check(collector.Observe(["第一句话"], baseTime.AddSeconds(4)).Count == 0, "Brief disappearance");
Check(collector.Observe([], baseTime.AddSeconds(8)).Count == 0, "Blank between repetitions");
Check(collector.Observe(["第一句话"], baseTime.AddSeconds(16)).Count == 1, "Real repeat allowed later");
Check(collector.Observe(["ＡＢＣ １２３"], baseTime.AddSeconds(18)).Count == 1, "Unicode compatibility text");
Check(collector.Observe(["abc123"], baseTime.AddSeconds(19)).Count == 0, "Unicode and spacing normalized");
Check(collector.Observe([""], baseTime.AddSeconds(20)).Count == 0, "Empty strings");
Check(collector.CopyAll().Split(Environment.NewLine).Length == 5, "Five captured cues");
Check(collector.CopyAll().Contains("[10:00:00] 第一句话"), "Wall clock timestamp");

Console.WriteLine("Subtitle OCR collector: PASS");
