using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Sdcb.SimdPaddleOCR;
using Sdcb.SimdPaddleOCR.ModelProvider;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny;
using Serilog;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Starshot.Helpers;

/// <summary>识别词：文本 + 原图坐标矩形。</summary>
public sealed record OcrWord(string Text, Rect Rect);

/// <summary>识别行：原图坐标及现有词选区；Tiny 的置信度和旋转保留为附加数据。</summary>
public sealed record OcrLine(string Text, Rect Rect, List<OcrWord> Words)
{
    public float? RecognitionScore { get; init; }
    public int Rotation { get; init; }
}

/// <summary>One lazy CPU engine: bundled Tiny or optional verified Small. Windows is a final exception fallback.</summary>
public static class OcrHelper
{
    public const string EngineName = "SimdPaddleOCR-Tiny";
    private static readonly SemaphoreSlim EngineGate = new(1);
    private static PaddleOcrAll? _engine;
    private static string? _engineModel;
    private static bool _stopping;
    private static int _initializations, _fallbacks;
    internal static int ModelInitializationCount => Volatile.Read(ref _initializations);
    internal static int FallbackCount => Volatile.Read(ref _fallbacks);
    internal static long ModelInitializationMilliseconds { get; private set; }
    internal static string? LoadedModel => _engineModel;

    static OcrHelper() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();

    /// <summary>
    /// UI thread: read an existing SDR crop at its original size. Float HDR inputs
    /// must first use the application's SDR display/crop pipeline; never clamp here.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height, double Scale) PreparePixels(CanvasBitmap bitmap)
    {
        if (bitmap.Format is not (DirectXPixelFormat.B8G8R8A8UIntNormalized or DirectXPixelFormat.R8G8B8A8UIntNormalized))
            throw new ArgumentException("OCR 需要已完成 SDR 转换的截图，不能直接读取 HDR 浮点图。", nameof(bitmap));
        int width = (int)bitmap.SizeInPixels.Width, height = (int)bitmap.SizeInPixels.Height;
        if (bitmap.Format == DirectXPixelFormat.B8G8R8A8UIntNormalized)
            return (bitmap.GetPixelBytes(), width, height, 1.0);
        using var bgra = new CanvasRenderTarget(bitmap.Device, width, height, 96,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        using (var drawing = bgra.CreateDrawingSession())
        {
            drawing.Units = CanvasUnits.Pixels;
            drawing.Clear(Colors.White);
            drawing.DrawImage(bitmap);
        }
        return (bgra.GetPixelBytes(), width, height, 1.0);
    }

    public static async Task<List<OcrLine>?> RecognizeAsync(byte[] bgra, int width, int height,
        double scale, CancellationToken cancellationToken = default, int stride = 0)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0 || height <= 0 || !double.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        stride = stride == 0 ? checked(width * 4) : stride;
        if (stride < checked(width * 4) || bgra.Length < checked(stride * height))
            throw new ArgumentException("OCR BGRA buffer/stride does not match its dimensions.", nameof(bgra));
        await EngineGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, typeof(OcrHelper));
            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                string model = AppConfig.OcrModel == "small" ? "small" : "tiny";
                try
                {
                    PaddleOcrModelBundle? bundle = model == "small"
                        ? await OcrModelStore.Current.GetVerifiedBundleAsync(cancellationToken).ConfigureAwait(false) : null;
                    if (model == "small" && bundle is null)
                    {
                        Log.Warning("[OCR] Small unavailable or corrupt; falling back to built-in Tiny");
                        AppConfig.OcrModel = model = "tiny";
                    }
                    return await RunAsync(model, bundle ?? ChineseV6TinyModels.Default, bgra, width, height, stride, scale, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (model == "small" && ex is not OperationCanceledException)
                {
                    RetireEngine();
                    AppConfig.OcrModel = "tiny";
                    Log.Warning(ex, "[OCR] SimdPaddleOCR-Small failed, falling back to built-in Tiny");
                    return await RunAsync("tiny", ChineseV6TinyModels.Default, bgra, width, height, stride, scale, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _fallbacks);
                Log.Warning(ex, "[OCR] SimdPaddleOCR failed, falling back to Windows.Media.Ocr");
                // A failed inference may leave a session unusable. Retire the engine
                // before fallback; a later request can lazily create a healthy one.
                RetireEngine();
                var lines = await RecognizeLegacyAsync(bgra, width, height, stride, scale, cancellationToken).ConfigureAwait(false);
                Log.Information("[OCR] engine=Windows.Media.Ocr input={Width}x{Height} lines={Lines} elapsed={Elapsed} ms",
                    width, height, lines?.Count ?? 0, stopwatch.ElapsedMilliseconds);
                return lines;
            }
        }
        finally { EngineGate.Release(); }
    }

    // All engine retirement and model-file publication shares the inference gate.
    // Run is synchronous: cancellation is observed before/after it, never by disposing
    // a live session. Selecting a model does not create an engine.
    public static async Task SelectModelAsync(string model, CancellationToken ct = default)
    {
        if (model is not ("tiny" or "small")) throw new ArgumentException("未知 OCR 模型。");
        await EngineGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, typeof(OcrHelper));
            if (model == "small" && await OcrModelStore.Current.GetVerifiedBundleAsync(ct).ConfigureAwait(false) is null)
            {
                if (_engineModel == "small") RetireEngine();
                AppConfig.OcrModel = "tiny";
                throw new InvalidOperationException("Small 尚未安装或校验失败，已回到内置 Tiny。请在 DLC 页面下载。");
            }
            if (_engineModel != model) RetireEngine();
            AppConfig.OcrModel = model;
        }
        finally { EngineGate.Release(); }
    }

    internal static async Task MutateSmallFilesAsync(Action mutation, CancellationToken ct)
    {
        await EngineGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_stopping, typeof(OcrHelper));
            if (_engineModel == "small") RetireEngine();
            if (AppConfig.OcrModel == "small") AppConfig.OcrModel = "tiny";
            mutation();
        }
        finally { EngineGate.Release(); }
    }

    private static async Task<List<OcrLine>> RunAsync(string model, PaddleOcrModelBundle bundle,
        byte[] bgra, int width, int height, int stride, double scale, CancellationToken ct)
    {
        if (_engineModel != model) RetireEngine();
        if (_engine is null)
        {
            var init = Stopwatch.StartNew();
            _engine = await PaddleOcrAll.LoadAsync(bundle, new PaddleOcrOptions
            {
                LineWorkerCount = 2, DetIntraOpThreads = 2,
                Detector = new() { MaxPooledSessions = 1, LimitSideLength = 2048 },
                Classifier = new() { MaxPooledSessions = 2 },
                Recognizer = new() { MaxPooledSessions = 2 },
            }, ct).ConfigureAwait(false);
            _engineModel = model;
            ModelInitializationMilliseconds = init.ElapsedMilliseconds;
            Interlocked.Increment(ref _initializations);
            Log.Information("[OCR] engine={Engine} model init={Elapsed} ms; workers=2; detThreads=2; detLimit=2048", EngineLabel(model), init.ElapsedMilliseconds);
        }
        ct.ThrowIfCancellationRequested();
        var run = Stopwatch.StartNew();
        var result = _engine.Run(bgra, width, height, sourceStride: stride, format: ImagePixelFormat.Bgra32);
        ct.ThrowIfCancellationRequested();
        var lines = result.Lines.Select(line => Adapt(line, 1.0 / scale)).ToList();
        Log.Information("[OCR] engine={Engine} input={Width}x{Height} lines={Lines} elapsed={Elapsed} ms; det={DetWidth}x{DetHeight}",
            EngineLabel(model), width, height, lines.Count, run.ElapsedMilliseconds, result.DetectorResizedWidth, result.DetectorResizedHeight);
        return lines;
    }

    private static string EngineLabel(string model) => model == "small" ? "SimdPaddleOCR-Small" : EngineName;
    private static void RetireEngine()
    {
        try { _engine?.Dispose(); }
        catch (Exception ex) { Log.Warning(ex, "[OCR] Engine disposal failed"); }
        finally { _engine = null; _engineModel = null; }
    }

    internal static OcrLine Adapt(PaddleOcrLine line, double restore)
    {
        var box = line.Box;
        var rect = QuadToRect(box.X1, box.Y1, box.X2, box.Y2, box.X3, box.Y3, box.X4, box.Y4, restore);
        // The pinned NuGet 1.4.2 API exposes line quads, not CTC character alignment.
        // Keep the existing result/selection model with one selectable word per line.
        var words = string.IsNullOrEmpty(line.Text) ? new List<OcrWord>() : new List<OcrWord> { new(line.Text, rect) };
        return new(line.Text, rect, words) { RecognitionScore = line.RecognitionScore, Rotation = line.AppliedRotationDegrees };
    }

    private static async Task<List<OcrLine>?> RecognizeLegacyAsync(byte[] bgra, int width, int height,
        int stride, double originalScale, CancellationToken ct)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? (OcrEngine.AvailableRecognizerLanguages.Count > 0 ? OcrEngine.TryCreateFromLanguage(OcrEngine.AvailableRecognizerLanguages[0]) : null);
        if (engine is null) { Log.Warning("[OCR] Windows.Media.Ocr has no available language engine"); return null; }
        // Only this last fallback is governed by the Windows OCR image-size limit.
        double legacyScale = Math.Min(1.0, OcrEngine.MaxImageDimension / (double)Math.Max(width, height));
        var prepared = PrepareLegacyPixels(bgra, width, height, stride, legacyScale);
        using var software = SoftwareBitmap.CreateCopyFromBuffer(prepared.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8,
            prepared.Width, prepared.Height, BitmapAlphaMode.Ignore);
        var result = await engine.RecognizeAsync(software).AsTask(ct).ConfigureAwait(false);
        double restore = 1.0 / (originalScale * legacyScale);
        return result.Lines.Select(line =>
        {
            var words = line.Words.Select(word => new OcrWord(SquashCjkSpaces(word.Text), ScaleRect(word.BoundingRect, restore))).ToList();
            return new OcrLine(SquashCjkSpaces(line.Text), UnionRects(words.Select(w => w.Rect)), words);
        }).ToList();
    }

    // Windows-only fallback packing. Never writes into the caller's pixel buffer.
    internal static (byte[] Pixels, int Width, int Height) PrepareLegacyPixels(byte[] bgra, int width, int height, int stride, double scale)
    {
        int w = Math.Max(1, (int)Math.Floor(width * scale)), h = Math.Max(1, (int)Math.Floor(height * scale));
        if (w == width && h == height && stride == width * 4) return (bgra, w, h);
        var pixels = new byte[checked(w * h * 4)];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int source = Math.Min(height - 1, (int)(y / scale)) * stride + Math.Min(width - 1, (int)(x / scale)) * 4;
            bgra.AsSpan(source, 4).CopyTo(pixels.AsSpan((y * w + x) * 4, 4));
        }
        return (pixels, w, h);
    }

    public static void Dispose()
    {
        OcrModelStore.Current.CancelDownload();
        EngineGate.Wait();
        try
        {
            _stopping = true;
            RetireEngine();
        }
        finally { EngineGate.Release(); }
    }

    private static string SquashCjkSpaces(string text)
    {
        var result = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ' && i > 0 && i < text.Length - 1
                && !IsLatinWordChar(text[i - 1]) && !IsLatinWordChar(text[i + 1])) continue;
            result.Append(text[i]);
        }
        return result.ToString().Trim();
    }
    private static bool IsLatinWordChar(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9');
    internal static string JoinWords(IEnumerable<string> words)
    {
        string previous = "";
        var text = new System.Text.StringBuilder();
        foreach (var word in words)
        {
            if (string.IsNullOrEmpty(word)) continue;
            if (previous.Length > 0 && IsLatinWordChar(previous[^1]) && IsLatinWordChar(word[0])) text.Append(' ');
            text.Append(word);
            previous = word;
        }
        return text.ToString();
    }
    private static Rect UnionRects(IEnumerable<Rect> rects)
    {
        var all = rects.ToArray();
        if (all.Length == 0) return new();
        double x = all.Min(r => r.Left), y = all.Min(r => r.Top);
        return new(x, y, all.Max(r => r.Right) - x, all.Max(r => r.Bottom) - y);
    }
    private static Rect ScaleRect(Rect rect, double factor) => new(rect.X * factor, rect.Y * factor, rect.Width * factor, rect.Height * factor);
    private static Rect QuadToRect(float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4, double scale)
    {
        double x = Math.Min(Math.Min(x1, x2), Math.Min(x3, x4)), y = Math.Min(Math.Min(y1, y2), Math.Min(y3, y4));
        double right = Math.Max(Math.Max(x1, x2), Math.Max(x3, x4)), bottom = Math.Max(Math.Max(y1, y2), Math.Max(y3, y4));
        return new(x * scale, y * scale, (right - x) * scale, (bottom - y) * scale);
    }
}
