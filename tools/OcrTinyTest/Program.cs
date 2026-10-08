using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Serilog;
using Starshot.Features.Codec;
using Starshot.Helpers;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny;
using Windows.Graphics.DirectX;
using Microsoft.UI.Text;

internal static class Program
{
    private static int _failures;
    private static long _privateBaseline;
    private static readonly string OutputDirectory = AppContext.BaseDirectory;
    private static readonly string CsvPath = Path.Combine(OutputDirectory, "ocr-tiny-validation.csv");
    private static readonly StringBuilder Csv = new("case,width,height,elapsed_ms,init_count,init_ms,fallback_count,private_baseline_bytes,private_delta_bytes,private_bytes,lines,min_score,boxes_valid,input_sha256_unchanged,recognized_text\r\n");

    private sealed record Fixture(string Name, int Width, int Height, byte[] Pixels, string Expected, string Text,
        bool HasPaddedStride = false, int Stride = 0, string? Note = null);

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Directory.CreateDirectory(OutputDirectory);
        Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.File(
            Path.Combine(OutputDirectory, "ocr-tiny-validation.log"), shared: true).CreateLogger();
        if (args.Length == 1 && args[0] == "--memory-only")
        {
            try { return await RunMemoryOnly(); }
            catch (Exception ex) { Console.Error.WriteLine($"FAIL memory-only run: {ex}"); return 1; }
            finally { Log.CloseAndFlush(); }
        }
        using var device = CanvasDevice.GetSharedDevice();
        Console.WriteLine($"Canvas device initialized: {device}");
        ReportModelResources();

        try
        {
            await RunAsync("fallback preparation, invalid inputs, cancellation, and FP16 rejection", () => PreparationChecks(device));
            var fixtures = MakeFixtures(device);
            foreach (Fixture fixture in fixtures)
                await RunOcrCase(fixture);
            try { await RunOcrCase(MakeHdrPreview(device)); }
            catch (Exception ex) { _failures++; Console.Error.WriteLine($"FAIL HDR→SDR preview fixture: {ex.GetType().Name}: {ex.Message}"); }

            await RunWarmCurve(fixtures.Single(x => x.Name == "ordinary SDR"));
            Check(OcrHelper.ModelInitializationCount == 1, $"expected exactly one engine initialization, got {OcrHelper.ModelInitializationCount}");
            Check(OcrHelper.FallbackCount == 0, $"expected no Windows OCR fallback, got {OcrHelper.FallbackCount}");
            await RunFallbackFaultInjection(fixtures.Single(x => x.Name == "English"));
        }
        catch (Exception ex)
        {
            _failures++;
            Console.Error.WriteLine($"FAIL harness: {ex}");
        }
        finally
        {
            try { OcrHelper.Dispose(); }
            catch (Exception ex) { _failures++; Console.Error.WriteLine($"FAIL dispose: {ex.Message}"); }
            Log.CloseAndFlush();
            try { await File.WriteAllTextAsync(CsvPath, Csv.ToString(), new UTF8Encoding(true)); }
            catch (Exception ex) { _failures++; Console.Error.WriteLine($"FAIL write CSV: {ex.Message}"); }
        }

        Console.WriteLine($"OCR Tiny harness: {(_failures == 0 ? "PASS" : "FAIL")} ({_failures} failures)");
        Console.WriteLine($"Metrics: {CsvPath}");
        Console.WriteLine($"Log: {Path.Combine(OutputDirectory, "ocr-tiny-validation.log")}");
        return _failures == 0 ? 0 : 1;
    }

    private static void ReportModelResources()
    {
        try { Assembly.Load("Sdcb.SimdPaddleOCR"); } catch { }
        foreach (string path in Directory.EnumerateFiles(AppContext.BaseDirectory, "Sdcb.SimdPaddleOCR*.dll"))
            try { Assembly.LoadFrom(path); } catch { }
        Console.WriteLine($"Engine={OcrHelper.EngineName}; Tiny package assembly={typeof(ChineseV6TinyModels).Assembly.GetName().Name}; version={typeof(ChineseV6TinyModels).Assembly.GetName().Version}");
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()
                     .Where(a => a.GetName().Name?.StartsWith("Sdcb.SimdPaddleOCR", StringComparison.OrdinalIgnoreCase) == true)
                     .OrderBy(a => a.GetName().Name))
        {
            string? location = null;
            try { location = assembly.Location; } catch { }
            long diskBytes = location is { Length: > 0 } && File.Exists(location) ? new FileInfo(location).Length : 0;
            Console.WriteLine($"assembly={assembly.GetName().Name}; version={assembly.GetName().Version}; bytes={diskBytes}; path={location}");
            foreach (string resourceName in assembly.GetManifestResourceNames().OrderBy(n => n))
            {
                using Stream? resource = assembly.GetManifestResourceStream(resourceName);
                Console.WriteLine($"  resource={resourceName}; bytes={resource?.Length ?? 0}");
            }
        }
    }

    private static async Task PreparationChecks(CanvasDevice device)
    {
        byte[] padded = new byte[40];
        for (int i = 0; i < padded.Length; i++) padded[i] = (byte)(i + 1);
        byte[] original = (byte[])padded.Clone();
        var direct = OcrHelper.PrepareLegacyPixels(padded, 2, 2, 8, 1.0);
        Check(ReferenceEquals(padded, direct.Pixels), "contiguous unchanged legacy pixels should reuse input");
        var resized = OcrHelper.PrepareLegacyPixels(padded, 2, 2, 20, .5);
        Check(resized.Width == 1 && resized.Height == 1 && resized.Pixels.Length == 4, "legacy resize dimensions/length");
        Check(resized.Pixels.SequenceEqual(padded.AsSpan(0, 4).ToArray()), "legacy resize did not honor padded stride");
        Check(padded.SequenceEqual(original), "legacy preparation modified caller buffer");

        byte[] invalidHashInput = [1, 2, 3];
        await ExpectAsync<ArgumentException>(() => OcrHelper.RecognizeAsync(invalidHashInput, 2, 2, 1));
        Check(OcrHelper.ModelInitializationCount == 0 && OcrHelper.FallbackCount == 0, "invalid buffer entered OCR engine");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await ExpectAsync<OperationCanceledException>(() => OcrHelper.RecognizeAsync(new byte[16], 2, 2, 1, cancelled.Token));
        }
        Check(OcrHelper.ModelInitializationCount == 0 && OcrHelper.FallbackCount == 0, "pre-cancelled request initialized OCR or fallback");

        using var floatBitmap = new CanvasRenderTarget(device, 32, 24, 96,
            DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied);
        ExpectSync<ArgumentException>(() => OcrHelper.PreparePixels(floatBitmap));
        using var large = new CanvasRenderTarget(device, 3840, 2160, 96,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        var prepared = OcrHelper.PreparePixels(large);
        Check(prepared.Width == 3840 && prepared.Height == 2160 && prepared.Scale == 1.0,
            $"4K preparation changed dimensions/scale: {prepared.Width}x{prepared.Height} scale={prepared.Scale}");
        Check(OcrHelper.ModelInitializationCount == 0 && OcrHelper.FallbackCount == 0, "preparation-only checks initialized OCR");
    }

    private static List<Fixture> MakeFixtures(CanvasDevice device)
    {
        var fixtures = new List<Fixture>
        {
            MakeTextFixture(device, "Chinese", 1080, 600, "星光截图 文字识别", "星光截图", 64, Colors.Black, Colors.White, "Microsoft YaHei UI"),
            MakeTextFixture(device, "English", 1280, 720, "GAME SETTINGS", "GAMESETTINGS", 64, Colors.Black, Colors.White, "Arial"),
            MakeTextFixture(device, "mixed", 1280, 720, "FPS 120 帧", "FPS120", 60, Colors.Black, Colors.White, "Microsoft YaHei UI"),
            MakeTextFixture(device, "dark white", 960, 540, "DARK MODE 2026", "DARKMODE", 54, Windows.UI.Color.FromArgb(255, 24, 28, 36), Colors.White, "Arial"),
            MakeTextFixture(device, "light black", 960, 540, "LIGHT MODE 2026", "LIGHTMODE", 54, Colors.White, Windows.UI.Color.FromArgb(255, 20, 20, 20), "Arial"),
            MakeTextFixture(device, "game UI smallfont", 1280, 720, "HP 100 / MP 60", "HP100", 26, Windows.UI.Color.FromArgb(255, 18, 24, 30), Windows.UI.Color.FromArgb(255, 245, 225, 132), "Arial", padded: true),
            MakeTextFixture(device, "ordinary SDR", 1280, 720, "ORDINARY SDR 80 NITS", "ORDINARYSDR", 56, Windows.UI.Color.FromArgb(255, 35, 44, 52), Colors.White, "Arial"),
            MakeTextFixture(device, "small crop", 520, 160, "Small crop: OCR 42", "OCR42", 36, Colors.Black, Colors.White, "Arial"),
            MakeTextFixture(device, "4K small text", 3840, 2160, "4K HUD 60 FPS", "HUD", 22, Windows.UI.Color.FromArgb(255, 20, 25, 30), Colors.White, "Arial"),
        };
        return fixtures;
    }

    private static Fixture MakeTextFixture(CanvasDevice device, string name, int width, int height, string text, string expected,
        float fontSize, Windows.UI.Color background, Windows.UI.Color foreground, string font, bool padded = false)
    {
        using var bitmap = new CanvasRenderTarget(device, width, height, 96,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        using (var ds = bitmap.CreateDrawingSession())
        using (var format = new CanvasTextFormat { FontFamily = font, FontSize = fontSize,
                   WordWrapping = CanvasWordWrapping.NoWrap })
        {
            ds.Clear(background);
            float x = name == "4K small text" ? 100 : Math.Max(28, width * .06f);
            float y = name == "4K small text" ? 110 : Math.Max(22, height * .18f);
            ds.DrawText(text, x, y, foreground, format);
        }
        byte[] pixels = bitmap.GetPixelBytes();
        if (!padded) return new(name, width, height, pixels, expected, text);
        int stride = width * 4 + 32;
        byte[] paddedPixels = new byte[stride * height];
        Array.Fill(paddedPixels, (byte)0xA5);
        for (int y = 0; y < height; y++) pixels.AsSpan(y * width * 4, width * 4).CopyTo(paddedPixels.AsSpan(y * stride, width * 4));
        return new(name, width, height, paddedPixels, expected, text, true, stride);
    }

    private static Fixture MakeHdrPreview(CanvasDevice device)
    {
        const int width = 1000, height = 420;
        const string text = "HDR PREVIEW 203 NITS";
        var (hdr, max, foreground, background) = MakeSyntheticHdr(device, width, height, text);
        using var hdrBitmap = hdr;
        using var preview = new CanvasRenderTarget(device, width, height, 96,
            DirectXPixelFormat.R8G8B8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        using (var ds = preview.CreateDrawingSession())
        using (var white = new WhiteLevelAdjustmentEffect { Source = hdrBitmap, InputWhiteLevel = 80, OutputWhiteLevel = 203,
                   BufferPrecision = CanvasBufferPrecision.Precision16Float })
        using (var gamma = new SrgbGammaEffect { Source = white, GammaMode = SrgbGammaMode.OETF,
                   BufferPrecision = CanvasBufferPrecision.Precision16Float })
        {
            ds.DrawImage(gamma);
        }
        return PreviewFixture(device, preview, text, max, foreground, background);
    }

    private static (CanvasBitmap Bitmap, float Maximum, float Foreground, float Background) MakeSyntheticHdr(
        CanvasDevice device, int width, int height, string text)
    {
        using var source = new CanvasRenderTarget(device, width, height, 96,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        using (var ds = source.CreateDrawingSession())
        using (var format = new CanvasTextFormat { FontFamily = "Arial", FontSize = 56,
                   WordWrapping = CanvasWordWrapping.NoWrap })
        {
            ds.Clear(Windows.UI.Color.FromArgb(255, 30, 38, 44));
            ds.DrawText(text, 60, 90, Colors.White, format);
        }

        // Test-only CPU sRGB EOTF creates synthetic scRGB values above 1.0 from the SDR fixture.
        byte[] sdr = source.GetPixelBytes();
        byte[] rgbaHalf = new byte[checked(width * height * 8)];
        float max = 0;
        for (int i = 0; i < width * height; i++)
        {
            float b = SrgbToLinear(sdr[i * 4]) * 2.5f;
            float g = SrgbToLinear(sdr[i * 4 + 1]) * 2.5f;
            float r = SrgbToLinear(sdr[i * 4 + 2]) * 2.5f;
            max = Math.Max(max, Math.Max(r, Math.Max(g, b)));
            WriteHalf(rgbaHalf, i * 8, r); WriteHalf(rgbaHalf, i * 8 + 2, g);
            WriteHalf(rgbaHalf, i * 8 + 4, b); WriteHalf(rgbaHalf, i * 8 + 6, 1);
        }
        float background = Math.Max(ReadHalf(rgbaHalf, (20 * width + 20) * 8),
            Math.Max(ReadHalf(rgbaHalf, (20 * width + 20) * 8 + 2), ReadHalf(rgbaHalf, (20 * width + 20) * 8 + 4)));
        float foreground = 0;
        for (int y = 88; y < 190; y++)
        for (int x = 55; x < 720; x++)
        {
            int i = (y * width + x) * 8;
            foreground = Math.Max(foreground, Math.Max(ReadHalf(rgbaHalf, i), Math.Max(ReadHalf(rgbaHalf, i + 2), ReadHalf(rgbaHalf, i + 4))));
        }
        Check(max > 1 && foreground > 1 && background < 1 && foreground > background + .5f,
            $"synthetic HDR text/background lack >1 contrast (max={max:F2}, foreground={foreground:F2}, background={background:F2})");
        var hdr = CanvasBitmap.CreateFromBytes(device, rgbaHalf, width, height, DirectXPixelFormat.R16G16B16A16Float);
        return (hdr, max, foreground, background);
    }

    private static Fixture PreviewFixture(CanvasDevice device, CanvasBitmap preview, string text,
        float max, float foreground, float background)
    {
        int width = (int)preview.SizeInPixels.Width, height = (int)preview.SizeInPixels.Height;
        var prepared = OcrHelper.PreparePixels(preview);
        Check(prepared.Scale == 1.0 && prepared.Width == width && prepared.Height == height,
            "HDR→SDR PreparePixels changed scale/dimensions");
        byte[] result = prepared.Pixels;
        int min = 255, maxByte = 0;
        for (int i = 0; i < result.Length; i += 4)
            for (int c = 0; c < 3; c++) { min = Math.Min(min, result[i + c]); maxByte = Math.Max(maxByte, result[i + c]); }
        Check(maxByte - min > 100, $"HDR→SDR preview lost contrast (range {min}..{maxByte})");
        return new("HDR→SDR preview", width, height, result, "HDRPREVIEW", text, Note: $"synthetic scRGB max={max:F2} foreground={foreground:F2} background={background:F2}, SDR channel range={min}..{maxByte}; PreparePixels R8→BGRA");
    }

    private static async Task<int> RunMemoryOnly()
    {
        using var device = CanvasDevice.GetSharedDevice();
        try
        {
            Fixture fixture = MakeTextFixture(device, "ordinary SDR", 1280, 720, "ORDINARY SDR 80 NITS",
                "ORDINARYSDR", 56, Windows.UI.Color.FromArgb(255, 35, 44, 52), Colors.White, "Arial");
            await RunOcrCase(fixture); // cold first request and private-memory baseline
            await RunOcrCase(fixture with { Name = "second request" });
            await RunWarmCurve(fixture);
            Check(OcrHelper.ModelInitializationCount == 1 && OcrHelper.FallbackCount == 0,
                $"memory run expected one init/no fallback; init={OcrHelper.ModelInitializationCount}; fallback={OcrHelper.FallbackCount}");
            return _failures == 0 ? 0 : 1;
        }
        finally
        {
            OcrHelper.Dispose();
            await File.WriteAllTextAsync(CsvPath, Csv.ToString(), new UTF8Encoding(true));
        }
    }

    private static async Task RunOcrCase(Fixture fixture)
    {
        byte[] before = (byte[])fixture.Pixels.Clone();
        string hash = Convert.ToHexString(SHA256.HashData(before));
        long privateBefore = Process.GetCurrentProcess().PrivateMemorySize64;
        if (_privateBaseline == 0 && OcrHelper.ModelInitializationCount == 0) _privateBaseline = privateBefore;
        var stopwatch = Stopwatch.StartNew();
        List<OcrLine>? lines = null;
        Exception? failure = null;
        try
        {
            lines = await OcrHelper.RecognizeAsync(fixture.Pixels, fixture.Width, fixture.Height, 1,
                stride: fixture.HasPaddedStride ? fixture.Stride : 0);
        }
        catch (Exception ex) { failure = ex; }
        stopwatch.Stop();
        string recognized = string.Join(" | ", lines?.Select(x => x.Text) ?? []);
        string formatted = lines is null ? "" : OcrTextFormatter.Paragraphs(lines);
        string joinedNormalized = Normalize(recognized);
        bool contains = joinedNormalized.Contains(Normalize(fixture.Expected), StringComparison.OrdinalIgnoreCase);
        bool boxesValid = lines is not null && lines.All(line =>
            line.Rect.X >= 0 && line.Rect.Y >= 0 && line.Rect.Width > 0 && line.Rect.Height > 0 &&
            line.Rect.Right <= fixture.Width + 4 && line.Rect.Bottom <= fixture.Height + 4 &&
            line.RecognitionScore is > 0 and <= 100 && line.Words.Count > 0);
        bool unchanged = fixture.Pixels.AsSpan().SequenceEqual(before) &&
            Convert.ToHexString(SHA256.HashData(fixture.Pixels)) == hash;
        double? minScore = lines is { Count: > 0 } ? lines.Min(x => x.RecognitionScore ?? 0) : null;
        AddMetric(fixture.Name, fixture.Width, fixture.Height, stopwatch.ElapsedMilliseconds, lines?.Count ?? 0,
            minScore, boxesValid, unchanged, recognized);
        bool passed = failure is null && contains && Normalize(formatted).Contains(Normalize(fixture.Expected), StringComparison.OrdinalIgnoreCase) &&
            boxesValid && unchanged && OcrHelper.FallbackCount == 0;
        if (!passed) _failures++;
        long privateNow = Process.GetCurrentProcess().PrivateMemorySize64;
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {fixture.Name} {fixture.Width}x{fixture.Height} {stopwatch.ElapsedMilliseconds}ms init={OcrHelper.ModelInitializationCount}/{OcrHelper.ModelInitializationMilliseconds}ms private={privateNow / (1024 * 1024)}MiB baseline={_privateBaseline / (1024 * 1024)}MiB delta={(privateNow - _privateBaseline) / (1024 * 1024)}MiB fallback={OcrHelper.FallbackCount} lines={lines?.Count ?? 0} score={Fmt(minScore)} boxes={boxesValid} unchanged={unchanged}; expected='{fixture.Expected}' text='{recognized}' formatted='{formatted}'{(fixture.Note is null ? "" : "; " + fixture.Note)}{(failure is null ? "" : "; error=" + failure.Message)}");
    }

    private static async Task RunWarmCurve(Fixture fixture)
    {
        Console.WriteLine("Warm steady-state sequence (500 ms between calls; no forced GC):");
        for (int i = 1; i <= 6; i++)
        {
            var timer = Stopwatch.StartNew();
            var lines = await OcrHelper.RecognizeAsync(fixture.Pixels, fixture.Width, fixture.Height, 1);
            timer.Stop();
            bool passed = OcrHelper.ModelInitializationCount == 1 && OcrHelper.FallbackCount == 0 &&
                Normalize(string.Join(" ", lines?.Select(x => x.Text) ?? [])).Contains(Normalize(fixture.Expected), StringComparison.OrdinalIgnoreCase);
            if (!passed) _failures++;
            long privateBytes = Process.GetCurrentProcess().PrivateMemorySize64;
            Console.WriteLine($"  {(passed ? "PASS" : "FAIL")} warm-{i} {timer.ElapsedMilliseconds}ms init={OcrHelper.ModelInitializationCount} private={privateBytes / (1024 * 1024)}MiB fallback={OcrHelper.FallbackCount}");
            AddMetric($"warm-{i}", fixture.Width, fixture.Height, timer.ElapsedMilliseconds, lines?.Count ?? 0,
                lines is { Count: > 0 } ? lines.Min(x => x.RecognitionScore ?? 0) : null, lines is not null, true,
                string.Join(" | ", lines?.Select(x => x.Text) ?? []));
            if (i < 6) await Task.Delay(500);
        }
    }

    private static async Task RunFallbackFaultInjection(Fixture fixture)
    {
        var engineField = typeof(OcrHelper).GetField("_engine", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(OcrHelper).FullName, "_engine");
        if (engineField.GetValue(null) is not IDisposable engine)
            throw new InvalidOperationException("Tiny engine was not initialized before fault injection.");
        int fallbackBefore = OcrHelper.FallbackCount;
        byte[] before = (byte[])fixture.Pixels.Clone();
        string hash = Convert.ToHexString(SHA256.HashData(before));
        engine.Dispose();
        List<OcrLine>? lines = null;
        string? fallbackError = null;
        var timer = Stopwatch.StartNew();
        try { lines = await OcrHelper.RecognizeAsync(fixture.Pixels, fixture.Width, fixture.Height, 1); }
        catch (Exception ex) { fallbackError = ex.GetType().Name + ": " + ex.Message; }
        timer.Stop();
        bool unchanged = fixture.Pixels.AsSpan().SequenceEqual(before) &&
            Convert.ToHexString(SHA256.HashData(fixture.Pixels)) == hash;
        bool retired = engineField.GetValue(null) is null;
        bool incremented = OcrHelper.FallbackCount == fallbackBefore + 1;
        bool passed = unchanged && retired && incremented;
        if (!passed) _failures++;
        string output = string.Join(" | ", lines?.Select(x => x.Text) ?? []);
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} fault-injected disposed Tiny engine; fallback={(lines is null ? "unavailable" : "available")} retired={retired} fallbackCount={OcrHelper.FallbackCount} unchanged={unchanged} elapsed={timer.ElapsedMilliseconds}ms result='{output}'{(fallbackError is null ? "" : " error=" + fallbackError)}; see log for expected SimdPaddleOCR failure warning");
        AddMetric("fault-injected Windows fallback", fixture.Width, fixture.Height, timer.ElapsedMilliseconds,
            lines?.Count ?? 0, lines is { Count: > 0 } ? lines.Min(x => x.RecognitionScore ?? 0) : null,
            lines is not null, unchanged, output + (fallbackError is null ? "" : " [" + fallbackError + "]"));
    }

    private static void AddMetric(string name, int width, int height, long elapsed, int count, double? minScore,
        bool boxesValid, bool unchanged, string text)
    {
        long privateBytes = Process.GetCurrentProcess().PrivateMemorySize64;
        Csv.Append(CsvEscape(name)).Append(',').Append(width).Append(',').Append(height).Append(',').Append(elapsed).Append(',')
            .Append(OcrHelper.ModelInitializationCount).Append(',').Append(OcrHelper.ModelInitializationMilliseconds).Append(',')
            .Append(OcrHelper.FallbackCount).Append(',').Append(_privateBaseline).Append(',').Append(privateBytes - _privateBaseline).Append(',').Append(privateBytes).Append(',')
            .Append(count).Append(',').Append(minScore?.ToString("0.000", CultureInfo.InvariantCulture) ?? "").Append(',')
            .Append(boxesValid).Append(',').Append(unchanged).Append(',').Append(CsvEscape(text)).Append("\r\n");
    }

    private static string Normalize(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string Fmt(double? value) => value?.ToString("0.000", CultureInfo.InvariantCulture) ?? "n/a";
    private static string CsvEscape(string value) => "\"" + value.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    private static float SrgbToLinear(byte value)
    {
        float s = value / 255f;
        return s <= .04045f ? s / 12.92f : MathF.Pow((s + .055f) / 1.055f, 2.4f);
    }
    private static void WriteHalf(byte[] target, int offset, float value) =>
        BitConverter.TryWriteBytes(target.AsSpan(offset, 2), BitConverter.HalfToUInt16Bits((Half)value));
    private static float ReadHalf(byte[] source, int offset) => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(source, offset));
    private static void Check(bool condition, string message)
    {
        if (!condition) { _failures++; Console.Error.WriteLine($"FAIL assertion: {message}"); }
    }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); Check(false, $"expected {typeof(T).Name}"); }
        catch (T) { }
        catch (Exception ex) { Check(false, $"expected {typeof(T).Name}, got {ex.GetType().Name}: {ex.Message}"); }
    }
    private static void ExpectSync<T>(Action action) where T : Exception
    {
        try { action(); Check(false, $"expected {typeof(T).Name}"); }
        catch (T) { }
        catch (Exception ex) { Check(false, $"expected {typeof(T).Name}, got {ex.GetType().Name}: {ex.Message}"); }
    }
    private static void Run(string name, Action action)
    {
        try { action(); Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { _failures++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
    }
    private static async Task RunAsync(string name, Func<Task> action)
    {
        try { await action(); Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { _failures++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
    }
}
