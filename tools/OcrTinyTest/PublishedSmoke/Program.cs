using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.Graphics.Canvas.Text;
using Windows.Graphics.DirectX;

internal static class Program
{
    private const string Expected = "HDRPREVIEW";
    private const string FixtureText = "HDR PREVIEW 203 NITS";

    private static async Task<int> Main(string[] args)
    {
        if ((args.Length != 2 && args.Length != 4) || args[0] is not ("--published-app" or "--model-info" or "--small-dlc"))
        {
            Console.Error.WriteLine("Usage: OcrTinyPublishedSmoke.exe --published-app <published Starshot.dll>");
            return 2;
        }
        string appPath = Path.GetFullPath(args[1]);
        string appDirectory = Path.GetDirectoryName(appPath)!;
        if (!File.Exists(appPath)) throw new FileNotFoundException("Published app assembly not found.", appPath);

        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string candidate = Path.Combine(appDirectory, name.Name + ".dll");
            return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
        };
        // Keep this harness's Win2D host/runtime together. Only the published OCR
        // model assemblies need to be loaded from the final app output.
        string[] modelPaths = Directory.GetFiles(appDirectory, "Sdcb.SimdPaddleOCR*.dll");
        if (modelPaths.Length < 3) throw new FileNotFoundException("Published app is missing SimdPaddleOCR managed assemblies.");
        foreach (string dependency in modelPaths) AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency);
        if (args[0] == "--model-info")
        {
            long total = 0;
            foreach (Assembly model in AssemblyLoadContext.Default.Assemblies.Where(a => a.GetName().Name?.StartsWith("Sdcb.SimdPaddleOCR") == true))
            foreach (string name in model.GetManifestResourceNames())
            {
                using Stream? resource = model.GetManifestResourceStream(name);
                long size = resource?.Length ?? 0;
                total += size;
                Console.WriteLine($"resource={name}; bytes={size}");
            }
            Console.WriteLine($"total embedded resources={total} bytes ({total / 1048576.0:F3} MiB)");
            return 0;
        }
        Assembly app = AssemblyLoadContext.Default.LoadFromAssemblyPath(appPath);
        Console.WriteLine($"Published assembly={app.Location}; version={app.GetName().Version}");

        if (args[0] == "--small-dlc") return await SmallDlcAsync(app, appDirectory, args[2], args[3]);

        using var device = CanvasDevice.GetSharedDevice();
        using var hdr = CreateSyntheticHdr(device, 1000, 420);
        Type service = app.GetType("Starshot.Features.Screenshot.ScreenCaptureService", throwOnError: true)!;
        MethodInfo tonemap = service.GetMethod("TonemapToSdr", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(service.FullName, "TonemapToSdr");
        using var sdr = (IDisposable)(tonemap.Invoke(null, [hdr, 203f])
            ?? throw new InvalidOperationException("Published TonemapToSdr returned null."));

        Type helper = app.GetType("Starshot.Helpers.OcrHelper", throwOnError: true)!;
        MethodInfo prepare = helper.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method => method.Name == "PreparePixels" && method.GetParameters().Length == 1);
        var prepared = ((byte[] Pixels, int Width, int Height, double Scale))(prepare.Invoke(null, [sdr])
            ?? throw new InvalidOperationException("Published PreparePixels returned null."));
        byte[] original = (byte[])prepared.Pixels.Clone();
        string hash = Convert.ToHexString(SHA256.HashData(original));
        long before = Process.GetCurrentProcess().PrivateMemorySize64;
        MethodInfo recognize = helper.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method => method.Name == "RecognizeAsync" && method.GetParameters().Length == 6);
        var watch = Stopwatch.StartNew();
        Task task = (Task)(recognize.Invoke(null, [prepared.Pixels, prepared.Width, prepared.Height,
            prepared.Scale, CancellationToken.None, 0]) ?? throw new InvalidOperationException("Published RecognizeAsync returned null."));
        await task;
        watch.Stop();
        object? lineResult = task.GetType().GetProperty("Result")?.GetValue(task);
        var lines = (lineResult as System.Collections.IEnumerable)?.Cast<object>().ToList() ?? [];
        string text = string.Join(" | ", lines.Select(line => line.GetType().GetProperty("Text")?.GetValue(line)?.ToString() ?? ""));
        Type formatter = app.GetType("Starshot.Helpers.OcrTextFormatter", throwOnError: true)!;
        MethodInfo paragraphs = formatter.GetMethod("Paragraphs", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(formatter.FullName, "Paragraphs");
        string formatted = (string)(paragraphs.Invoke(null, [lineResult]) ?? "");

        var loadedModels = AssemblyLoadContext.Default.Assemblies.Where(assembly =>
            assembly.GetName().Name?.StartsWith("Sdcb.SimdPaddleOCR", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        foreach (Assembly model in loadedModels)
            Console.WriteLine($"Model assembly={model.GetName().Name}; version={model.GetName().Version}; path={model.Location}");
        bool modelsFromPublish = loadedModels.Length >= 3 && loadedModels.All(assembly =>
            Path.GetFullPath(assembly.Location).StartsWith(appDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        bool unchanged = prepared.Pixels.AsSpan().SequenceEqual(original) &&
            Convert.ToHexString(SHA256.HashData(prepared.Pixels)) == hash;
        int min = 255, max = 0;
        for (int i = 0; i < prepared.Pixels.Length; i += 4)
            for (int c = 0; c < 3; c++) { min = Math.Min(min, prepared.Pixels[i + c]); max = Math.Max(max, prepared.Pixels[i + c]); }
        int init = ReadInt(helper, "ModelInitializationCount");
        int fallback = ReadInt(helper, "FallbackCount");
        bool passed = modelsFromPublish && lines.Count > 0 &&
            Normalize(formatted).Contains(Expected, StringComparison.OrdinalIgnoreCase) && unchanged && max - min > 100 &&
            (init < 0 || init == 1) && (fallback < 0 || fallback == 0);
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} published HDR pipeline: TonemapToSdr → PreparePixels (R8→BGRA) → RecognizeAsync; init={init} fallback={fallback} elapsed={watch.ElapsedMilliseconds}ms privateDelta={(Process.GetCurrentProcess().PrivateMemorySize64 - before) / (1024 * 1024)}MiB lines={lines.Count} unchanged={unchanged} SDRrange={min}..{max} text='{text}' formatted='{formatted}'");
        helper.GetMethod("Dispose", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(null, null);
        return passed ? 0 : 1;
    }

    private static async Task<int> SmallDlcAsync(Assembly app, string appDirectory, string sources, string output)
    {
        string root = Path.Combine(Path.GetTempPath(), "Starshot-PublishedSmall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); Directory.CreateDirectory(output);
        Type config = app.GetType("Starshot.AppConfig", true)!;
        Type helper = app.GetType("Starshot.Helpers.OcrHelper", true)!;
        Type storeType = app.GetType("Starshot.Helpers.OcrModelStore", true)!;
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        config.GetProperty("UserDataFolder")!.SetValue(null, root);
        var choice = config.GetProperty("OcrModel")!;
        var clear = config.GetMethod("ClearCache")!;
        var select = helper.GetMethod("SelectModelAsync")!;
        var recognize = helper.GetMethod("RecognizeAsync")!;
        var dispose = helper.GetMethod("Dispose")!;
        var rows = new List<string> { "case,elapsed_ms,private_bytes,lines" };
        try
        {
            Assert(choice.GetValue(null)?.ToString() == "tiny", "existing/empty config defaults Tiny");
            object store = storeType.GetProperty("Current")!.GetValue(null)!;
            string models = (string)storeType.GetProperty("DirectoryPath")!.GetValue(store)!;
            Assert(models.StartsWith(root, StringComparison.OrdinalIgnoreCase), "Small uses isolated app-data root");
            Directory.CreateDirectory(models);
            foreach (string name in new[] { "det.onnx", "rec.onnx", "dict.txt" }) File.Copy(Path.Combine(sources, name), Path.Combine(models, name));
            Assert(ReadInt(helper, "ModelInitializationCount") == 0, "provider construction remains lazy");
            Assert(!Directory.EnumerateFiles(appDirectory, "*ChineseV6Small*.dll").Any(), "publish does not contain downloaded Small assembly");
            using var device = CanvasDevice.GetSharedDevice();
            using var bitmap = new CanvasRenderTarget(device, 1280, 420, 96, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
            using (var drawing = bitmap.CreateDrawingSession())
            using (var format = new CanvasTextFormat { FontFamily = "Microsoft YaHei UI", FontSize = 40, WordWrapping = CanvasWordWrapping.NoWrap })
            using (var japanese = new CanvasTextFormat { FontFamily = "Yu Gothic UI", FontSize = 40, WordWrapping = CanvasWordWrapping.NoWrap })
            {
                drawing.Clear(Colors.White);
                drawing.DrawText("截图文字识别 星光 2026", 40, 35, Colors.Black, format);
                drawing.DrawText("SCREENSHOT OCR / English 123", 40, 145, Colors.Black, format);
                drawing.DrawText("日本語の文字認識 テスト", 40, 255, Colors.Black, japanese);
            }
            var pixels = bitmap.GetPixelBytes();
            foreach (string model in new[] { "tiny", "small" })
            {
                await (Task)select.Invoke(null, [model, CancellationToken.None])!;
                if (model == "small")
                {
                    Assert(helper.GetProperty("LoadedModel", flags)!.GetValue(null) is null, "switch disposes old engine, new remains lazy");
                    clear.Invoke(null, null);
                    Assert(choice.GetValue(null)?.ToString() == "small", "production config persists/reloads Small");
                    using var setting = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config.sjson")));
                    Assert(setting.RootElement.GetProperty("OcrModel").GetString() == "small", "production config serialized correctly");
                }
                var sw = Stopwatch.StartNew();
                Task result = (Task)recognize.Invoke(null, [pixels, 1280, 420, 1d, CancellationToken.None, 0])!;
                await result;
                var lines = ((System.Collections.IEnumerable)result.GetType().GetProperty("Result")!.GetValue(result)!).Cast<object>().ToArray();
                string text = string.Join(" | ", lines.Select(l => l.GetType().GetProperty("Text")!.GetValue(l)?.ToString()));
                Assert(text.Contains("截图文字识别") && text.Contains("SCREENSHOT"), "published " + model + " Chinese/English OCR");
                if (model == "small") Assert(text.Contains("日本語") && text.Contains("認識"), "published Small Japanese OCR");
                Assert(ReadInt(helper, "FallbackCount") == 0, "published provider does not fall back to Windows OCR");
                long memory = Process.GetCurrentProcess().PrivateMemorySize64;
                rows.Add($"publish-{model},{sw.ElapsedMilliseconds},{memory},{lines.Length}");
                Console.WriteLine($"publish {model}: {sw.ElapsedMilliseconds} ms; private={memory}; text={text}");
            }
            await (Task)storeType.GetMethod("DeleteAsync")!.Invoke(store, [CancellationToken.None])!;
            Assert(!Directory.Exists(models) && choice.GetValue(null)?.ToString() == "tiny", "published deletion reverts to Tiny");
            Assert(helper.GetProperty("LoadedModel", flags)!.GetValue(null) is null, "delete disposes loaded Small");
            var setValue = config.GetMethod("SetValue")!.MakeGenericMethod(typeof(string));
            await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
            {
                for (int i = 0; i < 10; i++) setValue.Invoke(null, [$"{worker}-{i}", "Concurrency" + worker]);
            })));
            clear.Invoke(null, null);
            var getValue = config.GetMethod("GetValue")!.MakeGenericMethod(typeof(string));
            foreach (int i in Enumerable.Range(0, 4)) Assert(getValue.Invoke(null, ["", "Concurrency" + i])?.ToString() == $"{i}-9", "background preference writes persist without races");
            Assert(File.Exists(Path.Combine(appDirectory, "ThirdParty", "SimdPaddleOCR", "small-manifest.json")), "publish contains DLC source/hash/license manifest");
            Console.WriteLine("PASS publish: actual local provider, Tiny/Small OCR, persistence, delete, concurrent config, no NuGet cache/Small DLL dependency");
            return 0;
        }
        finally
        {
            dispose.Invoke(null, null);
            File.WriteAllLines(Path.Combine(output, "publish-memory.csv"), rows);
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(temp + "Starshot-PublishedSmall-", StringComparison.OrdinalIgnoreCase)) throw new Exception("unsafe cleanup");
            Directory.Delete(root, true);
            Console.WriteLine("published test temporary data removed; no screenshots written");
        }
    }

    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static int ReadInt(Type type, string name) => Convert.ToInt32(
        type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) ?? -1);

    private static string Normalize(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static CanvasBitmap CreateSyntheticHdr(CanvasDevice device, int width, int height)
    {
        using var sdr = new CanvasRenderTarget(device, width, height, 96,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        using (var ds = sdr.CreateDrawingSession())
        using (var format = new CanvasTextFormat { FontFamily = "Arial", FontSize = 56,
                   WordWrapping = CanvasWordWrapping.NoWrap })
        {
            ds.Clear(Windows.UI.Color.FromArgb(255, 30, 38, 44));
            ds.DrawText(FixtureText, 60, 90, Colors.White, format);
        }
        byte[] bgra = sdr.GetPixelBytes();
        byte[] fp16 = new byte[checked(width * height * 8)];
        float max = 0;
        for (int i = 0; i < width * height; i++)
        {
            float b = Linear(bgra[i * 4]) * 2.5f;
            float g = Linear(bgra[i * 4 + 1]) * 2.5f;
            float r = Linear(bgra[i * 4 + 2]) * 2.5f;
            max = Math.Max(max, Math.Max(r, Math.Max(g, b)));
            HalfValue(fp16, i * 8, r); HalfValue(fp16, i * 8 + 2, g);
            HalfValue(fp16, i * 8 + 4, b); HalfValue(fp16, i * 8 + 6, 1);
        }
        float background = Math.Max(ReadHalf(fp16, (20 * width + 20) * 8),
            Math.Max(ReadHalf(fp16, (20 * width + 20) * 8 + 2), ReadHalf(fp16, (20 * width + 20) * 8 + 4)));
        float foreground = 0;
        for (int y = 88; y < 190; y++)
        for (int x = 55; x < 720; x++)
        {
            int i = (y * width + x) * 8;
            foreground = Math.Max(foreground, Math.Max(ReadHalf(fp16, i), Math.Max(ReadHalf(fp16, i + 2), ReadHalf(fp16, i + 4))));
        }
        if (!(max > 1 && foreground > 1 && background < 1 && foreground > background + .5f))
            throw new InvalidOperationException($"Synthetic HDR foreground/background invalid: max={max}; foreground={foreground}; background={background}");
        Console.WriteLine($"Synthetic scRGB max={max:F2}, foreground={foreground:F2}, background={background:F2}");
        return CanvasBitmap.CreateFromBytes(device, fp16, width, height, DirectXPixelFormat.R16G16B16A16Float);
    }

    private static float Linear(byte value)
    {
        float s = value / 255f;
        return s <= .04045f ? s / 12.92f : MathF.Pow((s + .055f) / 1.055f, 2.4f);
    }

    private static void HalfValue(byte[] target, int offset, float value) =>
        BitConverter.TryWriteBytes(target.AsSpan(offset, 2), BitConverter.HalfToUInt16Bits((Half)value));

    private static float ReadHalf(byte[] source, int offset) => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(source, offset));
}
