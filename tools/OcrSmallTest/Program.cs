using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Serilog;
using Starshot;
using Starshot.Helpers;
using Windows.Graphics.DirectX;

internal static class Program
{
    private static string Root = "", Sources = "", Output = "";
    private static int Failures, OcrRuns;
    private static readonly List<object> Results = [];
    private static readonly List<string> Memory = ["case,elapsed_ms,model_init_ms,init_count,loaded_model,private_bytes,private_delta_bytes,lines"];
    private static long Baseline;
    private static SemaphoreSlim Gate => (SemaphoreSlim)typeof(OcrHelper).GetField("EngineGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    private static long PrivateBytes { get { using var p = Process.GetCurrentProcess(); p.Refresh(); return p.PrivateMemorySize64; } }

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        bool child = args.Length > 0 && args[0] == "--restart";
        Root = child ? Path.GetFullPath(args[1]) : Path.Combine(Path.GetTempPath(), "Starshot-OcrSmall-" + Guid.NewGuid().ToString("N"));
        Sources = child ? "" : Path.GetFullPath(args[0]);
        Output = child ? Root : Path.GetFullPath(args[1]);
        Directory.CreateDirectory(Root); Directory.CreateDirectory(Output);
        AppConfig.UserDataFolder = Root;
        Log.Logger = new LoggerConfiguration().WriteTo.File(Path.Combine(Output, child ? "restart.log" : "ocr-small-validation.log")).CreateLogger();
        try
        {
            if (args.Length > 2 && args[2] == "--resources-only")
            {
                await Check("official NuGet 1.0.0 embedded resource identity", () => { CompareNuGetResources(); return Task.CompletedTask; });
                return Failures == 0 ? 0 : 1;
            }
            if (child)
            {
                Require(AppConfig.OcrModel == "small", "restart selected Small");
                Require(OcrHelper.ModelInitializationCount == 0, "restart remains lazy");
                using var childDevice = CanvasDevice.GetSharedDevice();
                Baseline = PrivateBytes;
                await Recognize("restart-small", Fixture(childDevice));
                Require(OcrHelper.LoadedModel == "small", "restart loaded Small");
                File.WriteAllLines(Path.Combine(Root, "restart-memory.csv"), Memory);
                return 0;
            }
            await Check("official NuGet 1.0.0 embedded resource identity", () => { CompareNuGetResources(); return Task.CompletedTask; });
            await Check("actual official download / progress / exact hashes / atomic publication / lazy", async () =>
            {
                Require(!(await OcrModelStore.Current.GetStatusAsync()).Installed, "clean model directory");
                var begin = OcrModelStore.Current.StartDownload();
                Require(!begin.Installed && begin.State == "downloading", "download starts asynchronously");
                var progress = new List<long>();
                while (!OcrModelStore.Current.WaitForDownloadAsync().IsCompleted)
                {
                    var s = await OcrModelStore.Current.GetStatusAsync(); progress.Add(s.DownloadedBytes);
                    Require(!Directory.Exists(s.Directory), "no partial bundle visible");
                    await Task.Delay(100);
                }
                await OcrModelStore.Current.WaitForDownloadAsync();
                var end = await OcrModelStore.Current.GetStatusAsync(force: true);
                Require(end.Installed && end.DownloadedBytes == OcrModelStore.TotalBytes, end.Error ?? "download installed");
                Require(progress.Count > 0 && progress.Zip(progress.Skip(1)).All(pair => pair.First <= pair.Second), "monotonic progress");
                Require(!Directory.EnumerateFiles(Root, "*.dll", SearchOption.AllDirectories).Any(), "no downloaded DLL");
                Require(OcrHelper.ModelInitializationCount == 0, "download/status never load engine");
                Console.WriteLine($"download bytes={end.DownloadedBytes}; progress samples={progress.Count}");
            });
            await Check("offline failure, invalid length/hash, cancellation and stage cleanup", FailurePaths);
            await Check("selection is lazy, queued cancellation, publish gate and status deadlock protection", async () =>
            {
                await OcrHelper.SelectModelAsync("small");
                Require(OcrHelper.ModelInitializationCount == 0 && OcrHelper.LoadedModel is null, "selection remains lazy");
                await Gate.WaitAsync();
                try
                {
                    using var cts = new CancellationTokenSource();
                    var switchTask = OcrHelper.SelectModelAsync("tiny", cts.Token);
                    Require(!switchTask.IsCompleted, "switch waits on inference gate");
                    cts.Cancel();
                    try { await switchTask; throw new Exception("queued switch must cancel"); } catch (OperationCanceledException) { }
                    Require(AppConfig.OcrModel == "small", "canceled switch preserves choice");
                    // Publish waits for gate while status is polled: no reverse lock deadlock.
                    var store = Store("gate", new FixtureHandler(Sources));
                    store.StartDownload();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    while ((await store.GetStatusAsync()).State != "validating") await Task.Delay(10, timeout.Token);
                    Require(await store.GetVerifiedBundleAsync(timeout.Token) is null, "pending bundle not selectable");
                    Gate.Release();
                    await store.WaitForDownloadAsync().WaitAsync(timeout.Token);
                    Require((await store.GetStatusAsync()).Installed, "publication completed after inference gate release");
                    await Gate.WaitAsync();
                }
                finally { Gate.Release(); }
            });

            using var device = CanvasDevice.GetSharedDevice();
            var pixels = Fixture(device);
            Baseline = PrivateBytes;
            Console.WriteLine($"baseline private bytes={Baseline}");
            await Check("Small Chinese / English / Japanese, BGRA unchanged; warm reuse", async () =>
            {
                await OcrHelper.SelectModelAsync("small");
                var first = await Recognize("small-cold", pixels);
                Require(OcrHelper.LoadedModel == "small", "Small is active");
                string text = string.Concat(first.Select(l => l.Text)).Replace(" ", "");
                Require(text.Contains("截图文字识别"), "Chinese recognized");
                Require(text.Contains("SCREENSHOT"), "English recognized");
                Require(text.Contains("日本語") && text.Contains("認識"), "Japanese recognized");
                int init = OcrHelper.ModelInitializationCount;
                await Recognize("small-warm-1", pixels); await Recognize("small-warm-2", pixels);
                Require(OcrHelper.ModelInitializationCount == init, "warm OCR never reloads");
                Require(OcrHelper.FallbackCount == 0, "no Windows fallback");
            });
            await Check("restart keeps Small and lazy loads on first OCR", async () =>
            {
                using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                { ArgumentList = { "--restart", Root }, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
                var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync(); Console.Write(await stdout); Console.Write(await stderr);
                Require(p.ExitCode == 0, "restart child exited successfully"); OcrRuns++;
                File.Copy(Path.Combine(Root, "restart-memory.csv"), Path.Combine(Output, "restart-memory.csv"));
            });
            await Check("real concurrent OCR and switch retire only when inference finishes", async () =>
            {
                var running = Task.Run(() => Recognize("small-concurrent", pixels));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (Gate.CurrentCount != 0) await Task.Delay(1, timeout.Token);
                var switching = OcrHelper.SelectModelAsync("tiny");
                await running; await switching;
                Require(AppConfig.OcrModel == "tiny" && OcrHelper.LoadedModel is null, "switch disposes old without eager new engine");
                await Recognize("tiny-after-switch", pixels);
                Require(OcrHelper.LoadedModel == "tiny", "only Tiny active");
            });
            await Check("on-disk corruption (unchanged size/timestamp) safely falls back to Tiny", async () =>
            {
                await OcrHelper.SelectModelAsync("small");
                string path = Path.Combine(OcrModelStore.Current.DirectoryPath, "det.onnx");
                var original = File.GetLastWriteTimeUtc(path);
                using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) { int value = file.ReadByte(); file.Position = 0; file.WriteByte((byte)(value ^ 1)); }
                File.SetLastWriteTimeUtc(path, original);
                await Recognize("corrupt-small-tiny-fallback", pixels);
                Require(AppConfig.OcrModel == "tiny" && OcrHelper.LoadedModel == "tiny", "safe fallback to Tiny");
                Require(!(await OcrModelStore.Current.GetStatusAsync(force: true)).Installed, "damaged bundle rejected");
                Require(OcrHelper.FallbackCount == 0, "Small damage does not invoke Windows OCR");
            });
            await Check("delete cancels operations, removes only Small, missing model safe fallback", async () =>
            {
                using (var cts = new CancellationTokenSource())
                {
                    cts.Cancel(); try { await OcrModelStore.Current.DeleteAsync(cts.Token); } catch (OperationCanceledException) { }
                }
                await OcrModelStore.Current.DeleteAsync();
                Require(!(await OcrModelStore.Current.GetStatusAsync()).Installed && !Directory.Exists(OcrModelStore.Current.DirectoryPath), "Small deleted");
                Require(OcrHelper.LoadedModel == "tiny", "delete does not dispose unrelated Tiny engine");
                AppConfig.OcrModel = "small"; // Missing DLC in a restored config.
                await Recognize("missing-small-tiny-fallback", pixels);
                Require(AppConfig.OcrModel == "tiny" && OcrHelper.LoadedModel == "tiny", "missing installed files normalize choice");
            });
            Require(OcrRuns <= 10, "OCR run budget <=10");
            Console.WriteLine($"OCR runs={OcrRuns}; failed groups={Failures}");
            return Failures == 0 ? 0 : 1;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
        finally
        {
            OcrHelper.Dispose(); Log.CloseAndFlush();
            if (!child)
            {
                File.WriteAllText(Path.Combine(Output, "results.json"), JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
                File.WriteAllLines(Path.Combine(Output, "memory.csv"), Memory);
                string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!Root.StartsWith(temp + "Starshot-OcrSmall-", StringComparison.OrdinalIgnoreCase)) throw new Exception("unsafe test cleanup");
                Directory.Delete(Root, true); Console.WriteLine("isolated temporary model data removed; no screenshots written");
            }
        }
    }

    private static async Task Check(string name, Func<Task> action)
    {
        var sw = Stopwatch.StartNew();
        try { await action(); Results.Add(new { name, ok = true, elapsedMs = sw.ElapsedMilliseconds }); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { Failures++; Results.Add(new { name, ok = false, error = ex.ToString() }); Console.WriteLine("FAIL " + name + ": " + ex); }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static OcrModelStore Store(string name, HttpMessageHandler handler) => new(() => Path.Combine(Root, name), new HttpClient(handler), OcrHelper.MutateSmallFilesAsync);
    private static async Task FailurePaths()
    {
        foreach (var mode in new[] { "offline", "length", "hash", "cancel" })
        {
            var store = Store(mode, new FixtureHandler(Sources, mode)); store.StartDownload();
            if (mode == "cancel") { await Task.Delay(40); store.CancelDownload(); }
            await store.WaitForDownloadAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var status = await store.GetStatusAsync(force: true);
            Require(!status.Installed && status.State == (mode == "cancel" ? "canceled" : "error"), mode + " failure rejected");
            Require(!Directory.Exists(store.DirectoryPath), mode + " no partial install");
            Require(!Directory.Exists(Path.Combine(Root, mode)) || !Directory.EnumerateDirectories(Path.Combine(Root, mode), ".install-*").Any(), mode + " stage cleanup");
        }
    }
    private static byte[] Fixture(CanvasDevice device)
    {
        using var bitmap = new CanvasRenderTarget(device, 1280, 420, 96, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        using (var draw = bitmap.CreateDrawingSession())
        using (var chinese = new CanvasTextFormat { FontFamily = "Microsoft YaHei UI", FontSize = 40, WordWrapping = CanvasWordWrapping.NoWrap })
        using (var japanese = new CanvasTextFormat { FontFamily = "Yu Gothic UI", FontSize = 40, WordWrapping = CanvasWordWrapping.NoWrap })
        {
            draw.Clear(Colors.White);
            draw.DrawText("截图文字识别 星光 2026", 40, 35, Colors.Black, chinese);
            draw.DrawText("SCREENSHOT OCR / English 123", 40, 145, Colors.Black, chinese);
            draw.DrawText("日本語の文字認識 テスト", 40, 255, Colors.Black, japanese);
        }
        return bitmap.GetPixelBytes();
    }
    private static async Task<List<OcrLine>> Recognize(string name, byte[] pixels)
    {
        OcrRuns++; Require(OcrRuns <= 10, "OCR limit");
        var hash = SHA256.HashData(pixels); var sw = Stopwatch.StartNew();
        var result = await OcrHelper.RecognizeAsync(pixels, 1280, 420, 1) ?? throw new Exception("missing OCR result");
        Require(SHA256.HashData(pixels).SequenceEqual(hash), "input BGRA remains unchanged");
        long memory = PrivateBytes;
        Memory.Add($"{name},{sw.ElapsedMilliseconds},{OcrHelper.ModelInitializationMilliseconds},{OcrHelper.ModelInitializationCount},{OcrHelper.LoadedModel},{memory},{memory-Baseline},{result.Count}");
        Console.WriteLine($"OCR {name}: {sw.ElapsedMilliseconds} ms; init={OcrHelper.ModelInitializationMilliseconds}; private={memory}; lines={result.Count}; text={string.Join(" | ",result.Select(l=>l.Text))}");
        return result;
    }
    private static void CompareNuGetResources()
    {
        using var zip = ZipFile.OpenRead(Path.Combine(Sources, "official-small-1.0.0.nupkg"));
        var dll = zip.Entries.Single(e => e.FullName.StartsWith("lib/") && e.Name.EndsWith(".dll"));
        using var bytes = new MemoryStream(); using (var stream = dll.Open()) stream.CopyTo(bytes); bytes.Position = 0;
        // Read PE resources as data; never load or execute this downloaded assembly.
        using var pe = new PEReader(bytes); var md = pe.GetMetadataReader();
        var resources = pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress);
        int count = 0;
        foreach (var handle in md.ManifestResources)
        {
            var resource = md.GetManifestResource(handle); string name = md.GetString(resource.Name);
            var asset = OcrModelStore.Assets.SingleOrDefault(a => name.EndsWith("." + a.File)); if (asset is null) continue;
            var reader = resources.GetReader((int)resource.Offset, resources.Length - (int)resource.Offset);
            var data = reader.ReadBytes(reader.ReadInt32());
            Console.WriteLine($"official resource={name}; bytes={data.Length}; sha256={Convert.ToHexString(SHA256.HashData(data))}");
            if (asset.File == "dict.txt")
            {
                string embedded = Encoding.UTF8.GetString(data).Replace("\r\n", "\n");
                string source = File.ReadAllText(Path.Combine(Sources, asset.File)).Replace("\r\n", "\n");
                Require(embedded == source, "official dictionary entries identical; NuGet build uses CRLF, pinned Git source uses LF");
                count++; continue;
            }
            Require(data.Length == asset.Bytes && Convert.ToHexString(SHA256.HashData(data)) == asset.Sha256, "NuGet resource hash " + name);
            Require(File.ReadAllBytes(Path.Combine(Sources, asset.File)).SequenceEqual(data), "source matches NuGet " + name);
            count++;
        }
        Require(count == 3, "three official DET/REC/dictionary resources verified");
    }

    private sealed class FixtureHandler(string source, string mode = "ok") : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Require(OcrModelStore.Assets.Any(a => OcrModelStore.SourceUrl(a) == request.RequestUri!.AbsoluteUri), "only pinned official sources requested");
            if (mode == "offline") throw new HttpRequestException("simulated offline");
            if (mode == "cancel") await Task.Delay(Timeout.Infinite, ct);
            var asset = OcrModelStore.Assets.Single(a => OcrModelStore.SourceUrl(a) == request.RequestUri!.AbsoluteUri);
            HttpContent content = mode is "hash" or "length" ? new ByteArrayContent(new byte[mode == "length" ? 1 : asset.Bytes]) : new StreamContent(File.OpenRead(Path.Combine(source, asset.File)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
