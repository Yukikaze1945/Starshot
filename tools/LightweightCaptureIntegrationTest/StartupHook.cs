using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

// Opt-in .NET startup hook for an isolated Starshot process only. No production
// test switches, input injection, desktop capture or clipboard writes. Encoder
// smoke checks write only disposable images in an independent temp directory.
public static class StartupHook
{
    private static Assembly _app = null!;
    private static object _application = null!;
    private static string _report = null!;
    private static StreamWriter _csv = null!;
    private static StreamWriter _childrenCsv = null!;
    private static int _checks;
    private static ulong _maximumCaptureDedicated;
    private static Assembly? FindApplicationAssembly() => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Starshot");
    public static void Initialize()
    {
        _report = Environment.GetEnvironmentVariable("STARSHOT_LIGHTWEIGHT_REPORT") ?? throw new InvalidOperationException("Missing isolated report path");
        File.AppendAllText(Path.Combine(_report, "test-hook-progress.log"), "Hook initialized\n");
        string native = Environment.GetEnvironmentVariable("STARSHOT_GPU_COUNTER_DLL")!;
        NativeLibrary.SetDllImportResolver(typeof(StartupHook).Assembly, (name, _, _) => name == "GpuMemory" ? NativeLibrary.Load(native) : 0);
        var worker = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500);
                for (int attempt = 0; attempt < 150; attempt++)
                {
                    _app ??= FindApplicationAssembly()!;
                    if (_app is not null)
                    {
                        _application = _app.GetType("Starshot.App")!.GetProperty("Current")!.GetValue(null)!;
                        if (_application is not null && Field(_application, "m_SystemTrayWindow") is not null)
                        {
                            File.AppendAllText(Path.Combine(_report, "test-hook-progress.log"), "Hidden application/tray ready\n");
                            object queue = Field(_application, "_uiDispatcherQueue")!;
                            var enqueue = queue.GetType().GetMethods().Single(m => m.Name == "TryEnqueue" && m.GetParameters().Length == 1);
                            var callback = Delegate.CreateDelegate(enqueue.GetParameters()[0].ParameterType, typeof(StartupHook).GetMethod(nameof(OnUi), BindingFlags.Static | BindingFlags.NonPublic)!);
                            if (!(bool)enqueue.Invoke(queue, [callback])!) throw new InvalidOperationException("UI enqueue rejected");
                            return;
                        }
                    }
                    await Task.Delay(200);
                }
                throw new TimeoutException("Starshot did not finish hidden startup");
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(_report, "integration-error.log"), ex.ToString()); Environment.Exit(1); }
        });
        _ = worker.ContinueWith(task =>
        {
            // A JIT failure from a trimmed runtime happens before the lambda's
            // inner catch can run. Surface that harness failure instead of hanging.
            File.WriteAllText(Path.Combine(_report, "integration-error.log"), task.Exception!.ToString());
            Environment.Exit(1);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }
    private static object? Field(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value);
    private static void SetField(object value, string name, object? fieldValue) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(value, fieldValue);
    private static object? Invoke(object value, string name, params object?[] args) => value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(value, args);
    private static object Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value)!;
    private static async Task WaitUntil(Func<bool> condition, string label)
    {
        for (int i = 0; i < 100; i++) { if (condition()) { _checks++; return; } await Task.Delay(200); }
        throw new TimeoutException(label);
    }
    private static object BgraImage(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 64; pixels[i + 1] = 110; pixels[i + 2] = 150; pixels[i + 3] = 255; }
        return Activator.CreateInstance(_app.GetType("Starshot.Features.Screenshot.CpuCaptureImage")!, [width, height, pixels])!;
    }
    private static nint Handle(object window) => (nint)window.GetType().BaseType!.GetProperty("Hwnd", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void Check(bool okay, string label) { if (!okay) throw new InvalidOperationException(label); _checks++; }
    private static async void OnUi()
    {
        try
        {
            // The existing periodic GC is stopped in this test process so its timer
            // cannot mask a missed deterministic close. We never call GC.Collect.
            Invoke(Field(_application, "_gcTimer")!, "Stop");
            Check((int)_app.GetType("Starshot.AppConfig")!.GetProperty("ScreenCaptureMode")!.GetValue(null)! == 0, "Isolated profile must be lightweight");
            _csv = new StreamWriter(Path.Combine(_report, "starshot-synthetic-4k.csv"));
            _csv.WriteLine("cycle,phase,timestamp,pid,name,dedicatedResidentMiB,dedicatedCommittedMiB,sharedResidentMiB,sharedCommittedMiB,privateMiB,gdiHandles,userHandles,residentQueries,committedQueries,queryFailures");
            _childrenCsv = new StreamWriter(Path.Combine(_report, "webview2-process-tree.csv"));
            _childrenCsv.WriteLine("cycle,phase,timestamp,childCount,pid,name,dedicatedResidentMiB,dedicatedCommittedMiB,sharedResidentMiB,sharedCommittedMiB,privateMiB,statisticsAvailable");
            await Task.Delay(2000); Sample(0, "hidden_startup");
            if (Environment.GetEnvironmentVariable("STARSHOT_LIBRARY_FIXTURES") == "1")
                await CreateLibraryFixtures();
            // Load the real WebUI on an offscreen window without activating it.
            object main = Activator.CreateInstance(_app.GetType("Starshot.Features.ViewHost.MainWindow")!, [false])!;
            var retiredWindow = new WeakReference(main);
            SetField(_application, "m_MainWindow", main);
            nint mainHwnd = (nint)Property(main, "WindowHandle");
            SetWindowPos(mainHwnd, 0, -32000, -32000, 0, 0, 0x0015);
            object appWindow = Property(main, "AppWindow");
            appWindow.GetType().GetMethod("Show", [typeof(bool)])!.Invoke(appWindow, [false]);
            await WaitUntil(() => (bool)Field(main, "_ready")!, "Offscreen WebUI readiness");
            CheckWebBounds(main, mainHwnd);
            await Task.Delay(1500); Sample(0, "offscreen_webui_loaded");
            if (Environment.GetEnvironmentVariable("STARSHOT_LIBRARY_FIXTURES") == "1")
            {
                object bridge = Field(main, "_bridge")!;
                string directory = (string)_app.GetType("Starshot.AppConfig")!.GetProperty("ScreenshotFolder")!.GetValue(null)!;
                // An offscreen browser can skip lazy thumbnail requests. Exercise the
                // real native decoder explicitly, so an empty viewport cannot hide costs.
                foreach (string file in Directory.GetFiles(directory))
                {
                    object thumbnail = await (Task<object>)Invoke(bridge, "ThumbnailAsync", file, CancellationToken.None)!;
                    string src = (string)Property(thumbnail, "src");
                    Check(src.StartsWith("data:image/png;base64,"), "Native library thumbnail must return a PNG data URL");
                    byte[] png = Convert.FromBase64String(src[(src.IndexOf(',') + 1)..]);
                    Check(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)) == 720
                        && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)) == 405, "4K library decode must produce a bounded CPU thumbnail");
                }
                bridge = null!;
                Sample(0, "native_library_thumbnails_loaded");
            }
            Invoke(main, "Hide");
            await WaitUntil(() => Field(_application, "m_MainWindow") is null, "Hidden WebUI must close and relinquish the application reference");
            await Task.Delay(5000); Sample(0, "webui_released5s");
            main = null!; appWindow = null!;
            Check(!IsWindow(mainHwnd), "Retiring the main WebUI must destroy its HWND");
            Check(_app.GetType("Starshot.Helpers.InAppToast")!.GetProperty("MainWindow")!.GetValue(null) is null, "Retired native toast behavior must not retain the old visual tree");
            await EncoderChecks(); Sample(0, "cpu_encoders_complete");
            uint initialGdi = GetGuiResources(Process.GetCurrentProcess().Handle, 0), initialUser = GetGuiResources(Process.GetCurrentProcess().Handle, 1);
            Type rectangle = _app.GetType("Starshot.Features.Screenshot.CpuRegionCaptureResult")!.GetProperty("PhysicalRect")!.PropertyType;
            Type actionType = _app.GetType("Starshot.Features.Screenshot.RegionCaptureAction")!;
            object save = Enum.Parse(actionType, "Save");
            for (int cycle = 1; cycle <= 20; cycle++)
            {
                object image = BgraImage(3840, 2160);
                object? overlay = null;
                try
                {
                    overlay = Activator.CreateInstance(_app.GetType("Starshot.Features.Screenshot.CpuRegionCaptureWindow")!, [image, -1920, 0, save, false])!;
                    SetField(overlay, "_selected", true);
                    SetField(overlay, "_selection", Activator.CreateInstance(rectangle, [0, 0, 3840, 2160]));
                    nint hwnd = Handle(overlay);
                    InvalidateRect(hwnd, 0, false); UpdateWindow(hwnd);
                    Sample(cycle, "offscreen_overlay");
                    Invoke(overlay, "Finish", save);
                    Task completion = (Task)Property(Property(overlay, "Completion"), "Task");
                    await completion;
                    object result = Property(completion, "Result");
                    ((IDisposable)Property(result, "Image")).Dispose();
                    Check(Handle(overlay) == 0 && !IsWindow(hwnd), "CPU capture must destroy its HWND in the actual Starshot process");
                }
                finally { (overlay as IDisposable)?.Dispose(); ((IDisposable)image).Dispose(); }
                await Task.Delay(5000); Sample(cycle, "after5s");
                if (cycle == 1) { initialGdi = GetGuiResources(Process.GetCurrentProcess().Handle, 0); initialUser = GetGuiResources(Process.GetCurrentProcess().Handle, 1); }
            }
            using var process = Process.GetCurrentProcess();
            Check(GetGuiResources(process.Handle, 0) <= initialGdi + 1 && GetGuiResources(process.Handle, 1) <= initialUser + 1, "CPU captures must not accumulate GDI/USER handles");
            File.WriteAllText(Path.Combine(_report, "retired-window-lifetime.log"), $"Managed main window alive after normal allocation pressure: {retiredWindow.IsAlive}; no explicit GC.\n");
            if (Environment.GetEnvironmentVariable("STARSHOT_RETIRED_ROOT") == "1" && retiredWindow.IsAlive)
            {
                File.WriteAllText(Path.Combine(_report, "retired-root-ready.log"), process.Id.ToString());
                for (int i = 0; i < 300 && !File.Exists(Path.Combine(_report, "retired-root-done.log")); i++)
                    await Task.Delay(200);
            }
            // A new WebUI must attach correctly after retiring the first one. This
            // remains offscreen and does not activate, send input or start a capture.
            main = Activator.CreateInstance(_app.GetType("Starshot.Features.ViewHost.MainWindow")!, [false])!;
            SetField(_application, "m_MainWindow", main);
            mainHwnd = (nint)Property(main, "WindowHandle");
            SetWindowPos(mainHwnd, 0, -32000, -32000, 0, 0, 0x0015);
            appWindow = Property(main, "AppWindow");
            appWindow.GetType().GetMethod("Show", [typeof(bool)])!.Invoke(appWindow, [false]);
            await WaitUntil(() => (bool)Field(main, "_ready")!, "Reopened offscreen WebUI readiness");
            CheckWebBounds(main, mainHwnd);
            Sample(0, "offscreen_webui_reopened");
            Invoke(main, "Hide");
            await WaitUntil(() => Field(_application, "m_MainWindow") is null, "Reopened WebUI must release again");
            Check(!IsWindow(mainHwnd), "Reopened WebUI HWND must be destroyed");
            main = null!; appWindow = null!;
            await Task.Delay(5000); Sample(0, "reopened_webui_released5s");
            object ocr = Activator.CreateInstance(_app.GetType("Starshot.Features.ViewHost.MainWindow")!, [true])!;
            nint ocrHwnd = (nint)Property(ocr, "WindowHandle");
            SetWindowPos(ocrHwnd, 0, -32000, -32000, 0, 0, 0x0015);
            object ocrWindow = Property(ocr, "AppWindow");
            ocrWindow.GetType().GetMethod("Show", [typeof(bool)])!.Invoke(ocrWindow, [false]);
            await WaitUntil(() => (bool)Field(ocr, "_ready")!, "Compact offscreen OCR WebUI readiness");
            CheckWebBounds(ocr, ocrHwnd);
            Invoke(ocr, "Close");
            Check(!IsWindow(ocrHwnd), "Compact OCR WebUI must close rather than remain hidden");
            ocr = null!; ocrWindow = null!;
            await Task.Delay(5000); Sample(0, "ocr_webui_released5s");
            Check(_maximumCaptureDedicated < 120UL * 1048576, "Both dedicated resident and committed memory must remain below 120 MiB in the measured ordinary capture lifecycle");
            _csv.Dispose(); _childrenCsv.Dispose();
            File.WriteAllText(Path.Combine(_report, "integration-pass.log"), $"PASS {_checks} checks; actual Starshot process, 20 synthetic 4K offscreen captures; WebUI load/hide/release; no desktop input, WGC or clipboard writes; encoder temp images cleaned; periodic GC disabled in test only.");
            Invoke(_application, "Exit");
        }
        catch (Exception ex) { _csv?.Dispose(); _childrenCsv?.Dispose(); File.WriteAllText(Path.Combine(_report, "integration-error.log"), ex.ToString()); Environment.Exit(1); }
    }
    private static async Task CreateLibraryFixtures()
    {
        // Three synthetic encoder fixtures live in the harness's disposable profile.
        // Its outer finally deletes the entire isolated library, including on failure.
        Type config = _app.GetType("Starshot.AppConfig")!;
        Type service = _app.GetType("Starshot.Features.Screenshot.ScreenCaptureService")!;
        object captures = service.GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        object image = BgraImage(3840, 2160);
        try
        {
            config.GetProperty("ScreenshotSubfolderEnabled")!.SetValue(null, false);
            config.GetProperty("ScreenCaptureEncodeQuality")!.SetValue(null, 2);
            for (int format = 0; format < 3; format++)
            {
                config.GetProperty("ScreenCaptureSDRFormat")!.SetValue(null, format);
                string file = await (Task<string>)Invoke(captures, "SaveCpuCaptureAsync", image, (nint)0, true)!;
                Check(File.Exists(file), "Synthetic 4K library fixture must be saved in disposable profile");
            }
        }
        finally { ((IDisposable)image).Dispose(); }
        Sample(0, "cpu_library_fixtures_created");
    }
    private static async Task EncoderChecks()
    {
        string tempBase = Path.GetFullPath(Path.GetTempPath());
        string directory = Path.Combine(tempBase, "Starshot-cpu-encode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        object image = BgraImage(128, 96);
        Type config = _app.GetType("Starshot.AppConfig")!;
        Type service = _app.GetType("Starshot.Features.Screenshot.ScreenCaptureService")!;
        object captures = service.GetProperty("Instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        try
        {
            config.GetProperty("ScreenshotFolder")!.SetValue(null, directory);
            config.GetProperty("ScreenshotSubfolderEnabled")!.SetValue(null, false);
            config.GetProperty("ScreenCaptureEncodeQuality")!.SetValue(null, 2);
            for (int format = 0; format < 3; format++)
            {
                config.GetProperty("ScreenCaptureSDRFormat")!.SetValue(null, format);
                var saved = (Task<string>)Invoke(captures, "SaveCpuCaptureAsync", image, (nint)0, true)!;
                string path = await saved;
                byte[] bytes = await File.ReadAllBytesAsync(path);
                Check(Path.GetFullPath(path).StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "CPU encoders must write only inside their independent temp directory");
                Check(bytes.Length > 16, "CPU encoder must produce a nonempty image");
                if (format == 0)
                {
                    Check(bytes.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}), "CPU PNG signature");
                    Check(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)) == 128
                        && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20)) == 96, "CPU PNG dimensions");
                    byte[] rgba = DecodePng(bytes, 128, 96);
                    Check(rgba[0] == 150 && rgba[1] == 110 && rgba[2] == 64 && rgba[3] == 255 && rgba.AsSpan(rgba.Length - 4).SequenceEqual(rgba.AsSpan(0, 4)), "Lossless PNG preserves BGRA source channel order and alpha");
                }
                else if (format == 1) Check(System.Text.Encoding.ASCII.GetString(bytes, 4, 12).Contains("ftypavif"), "CPU AVIF container signature");
                else Check(bytes.AsSpan(0, 2).SequenceEqual(new byte[] {255,10}) || System.Text.Encoding.ASCII.GetString(bytes, 4, 4) == "JXL ", "CPU JXL signature");
            }
        }
        finally
        {
            ((IDisposable)image).Dispose();
            if (!Path.GetFullPath(directory).StartsWith(tempBase, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(directory).StartsWith("Starshot-cpu-encode-")) throw new InvalidOperationException("Unsafe temp cleanup target");
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory, false);
            File.AppendAllText(Path.Combine(_report, "encoder-cleanup.log"), "CPU PNG/AVIF/JXL temp directory cleaned in finally. No samples retained.\n");
        }
    }
    private static byte[] DecodePng(byte[] data, int width, int height)
    {
        using var compressed = new MemoryStream();
        int channels = data[25] == 6 ? 4 : data[25] == 2 ? 3 : throw new InvalidOperationException("Unexpected PNG pixel format");
        Check(data[24] == 8 && data[28] == 0, "PNG must be 8-bit and non-interlaced");
        for (int at = 8; at < data.Length;)
        {
            int count = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
            if (System.Text.Encoding.ASCII.GetString(data, at + 4, 4) == "IDAT") compressed.Write(data, at + 8, count);
            at += count + 12;
        }
        compressed.Position = 0;
        using var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionMode.Decompress);
        int stride = width * channels;
        byte[] rows = new byte[(stride + 1) * height]; zlib.ReadExactly(rows);
        byte[] decoded = new byte[stride * height], rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < stride; x++)
        {
            int a = x >= channels ? decoded[y * stride + x - channels] : 0, b = y > 0 ? decoded[(y - 1) * stride + x] : 0;
            int c = y > 0 && x >= channels ? decoded[(y - 1) * stride + x - channels] : 0;
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            int predictor = rows[y * (stride + 1)] switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, 4 => pa <= pb && pa <= pc ? a : pb <= pc ? b : c, _ => throw new InvalidOperationException("Invalid PNG filter") };
            decoded[y * stride + x] = unchecked((byte)(rows[y * (stride + 1) + x + 1] + predictor));
        }
        for (int pixel = 0; pixel < width * height; pixel++)
        { decoded.AsSpan(pixel * channels, 3).CopyTo(rgba.AsSpan(pixel * 4)); rgba[pixel * 4 + 3] = channels == 4 ? decoded[pixel * channels + 3] : (byte)255; }
        return rgba;
    }
    private static void Sample(int cycle, string phase)
    {
        using var self = Process.GetCurrentProcess();
        uint error = ReadGpuMemory((uint)self.Id, out var memory);
        // Do not disguise unavailable KMT statistics as a zero-byte sample.
        bool valid = error == 0 && memory.ResidentQueries > 0 && memory.CommittedQueries > 0;
        Check(valid && memory.FailedQueries == 0, "Dedicated GPU counters must be available without query failures");
        if (phase is "hidden_startup" or "webui_released5s" or "offscreen_overlay" or "after5s" or "reopened_webui_released5s")
            _maximumCaptureDedicated = Math.Max(_maximumCaptureDedicated, Math.Max(memory.DedicatedResident, memory.DedicatedCommitted));
        string Number(ulong bytes) => valid ? (bytes / 1048576d).ToString("F4", System.Globalization.CultureInfo.InvariantCulture) : "";
        _csv.WriteLine(FormattableString.Invariant($"{cycle},{phase},{DateTimeOffset.UtcNow:O},{self.Id},Starshot,{Number(memory.DedicatedResident)},{Number(memory.DedicatedCommitted)},{Number(memory.SharedResident)},{Number(memory.SharedCommitted)},{self.PrivateMemorySize64 / 1048576d:F4},{GetGuiResources(self.Handle, 0)},{GetGuiResources(self.Handle, 1)},{memory.ResidentQueries},{memory.CommittedQueries},{memory.FailedQueries}")); _csv.Flush();
        SampleChildren(cycle, phase, (uint)self.Id);
    }
    private static void CheckWebBounds(object main, nint hwnd)
    {
        Check(GetClientRect(hwnd, out var client), "Native WebUI client rect must be available");
        object bounds = Property(Field(main, "_controller")!, "Bounds");
        Check(Math.Abs((double)Property(bounds, "Width") - (client.Right - client.Left)) < 1
            && Math.Abs((double)Property(bounds, "Height") - (client.Bottom - client.Top)) < 1,
            "Windowed WebView2 bounds must exactly fit the native client rect at current DPI");
        File.AppendAllText(Path.Combine(_report, "native-webview-bounds.log"),
            $"DPI scale={Property(main, "UIScale")}; client={client.Right - client.Left}x{client.Bottom - client.Top}; controller={Property(bounds, "Width")}x{Property(bounds, "Height")}\n");
    }
    private static void SampleChildren(int cycle, string phase, uint root)
    {
        nint snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == -1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        var rows = new List<ProcessEntry>();
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (Process32FirstW(snapshot, ref entry)) do { rows.Add(entry); } while (Process32NextW(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        var family = new HashSet<uint> { root };
        int lastCount;
        do { lastCount = family.Count; foreach (var row in rows) if (family.Contains(row.Parent)) family.Add(row.Pid); } while (family.Count != lastCount);
        var children = rows.Where(row => row.Pid != root && family.Contains(row.Pid)).ToArray();
        if (phase is "webui_released5s" or "reopened_webui_released5s" or "ocr_webui_released5s")
            Check(children.Length == 0, "Retiring the main WebUI must also retire all of its child processes");
        if (children.Length == 0)
            _childrenCsv.WriteLine(FormattableString.Invariant($"{cycle},{phase},{DateTimeOffset.UtcNow:O},0,0,none,,,,,,true"));
        foreach (var child in children)
        {
            uint error = ReadGpuMemory(child.Pid, out var memory);
            bool valid = error == 0 && memory.ResidentQueries > 0 && memory.CommittedQueries > 0;
            string Number(ulong bytes) => valid ? (bytes / 1048576d).ToString("F4", System.Globalization.CultureInfo.InvariantCulture) : "";
            double? privateMiB = null;
            try { using var process = Process.GetProcessById((int)child.Pid); privateMiB = process.PrivateMemorySize64 / 1048576d; } catch { }
            _childrenCsv.WriteLine(FormattableString.Invariant($"{cycle},{phase},{DateTimeOffset.UtcNow:O},{children.Length},{child.Pid},{child.Name},{Number(memory.DedicatedResident)},{Number(memory.DedicatedCommitted)},{Number(memory.SharedResident)},{Number(memory.SharedCommitted)},{privateMiB:F4},{valid}"));
        }
        _childrenCsv.Flush();
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ProcessEntry
    {
        public uint Size, Usage, Pid;
        public nuint Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MemorySample
    {
        public ulong DedicatedResident, SharedResident, DedicatedCommitted, SharedCommitted;
        public uint Adapters, ResidentQueries, CommittedQueries, FailedQueries, EnumeratedAdapters, FirstFailure;
    }
    [DllImport("GpuMemory", CallingConvention = CallingConvention.Cdecl)] private static extern uint ReadGpuMemory(uint pid, out MemorySample sample);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Process32NextW(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint process, uint flags);
}
