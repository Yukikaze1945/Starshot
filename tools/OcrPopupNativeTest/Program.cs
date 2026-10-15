using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Starshot.Features.ViewHost;

internal static class Program
{
    [STAThread] static void Main()
    {
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string path = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(args =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new PopupTestApp();
        });
    }
}

// Actual native WebView2/bridge, hidden throughout. No singleton, tray or capture.
internal sealed class PopupTestApp : Starshot.App
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        string report = Environment.GetEnvironmentVariable("STARSHOT_OCR_POPUP_TEST_REPORT")!;
        string temp = Environment.GetEnvironmentVariable("STARSHOT_OCR_POPUP_TEST_TEMP")!;
        var rows = new List<object>();
        MainWindow? window = null;
        try
        {
            ((System.Timers.Timer)typeof(Starshot.App).GetField("_gcTimer", Flags)!.GetValue(this)!).Stop();
            foreach (var (name, value) in new[] { ("UserDataFolder", temp), ("CacheFolder", temp), ("LogFile", Path.Combine(report, "native-popup.log")), ("AppVersion", "popup-test") })
                typeof(Starshot.AppConfig).GetProperty(name)!.SetValue(null, value);
            for (int cycle = 1; cycle <= 2; cycle++)
            {
                long before = Process.GetCurrentProcess().PrivateMemorySize64;
                var timer = Stopwatch.StartNew();
                window = new MainWindow(ocrPopup: true);
                typeof(MainWindow).GetField("_pendingOcr", Flags)!.SetValue(window, new { id = "synthetic-" + cycle, text = "星光截图 SCREENSHOT 2026", raw = "星光截图\nSCREENSHOT 2026", lineCount = 2, translate = false, autoCopied = false, copyError = (string?)null });
                double? immediateTextMs = null;
                var loadingText = typeof(MainWindow).GetField("_loadingOcrText", Flags);
                if (loadingText is not null)
                {
                    loadingText.SetValue(window, "星光截图 SCREENSHOT 2026");
                    var surfaceType = typeof(MainWindow).Assembly.GetType("Starshot.Features.Screenshot.CpuCaptureSurface")!;
                    using var surface = (IDisposable)Activator.CreateInstance(surfaceType, 1200, 800)!;
                    var dc = (nint)surfaceType.GetProperty("Dc", Flags | BindingFlags.Public)!.GetValue(surface)!;
                    var drawing = Stopwatch.StartNew();
                    typeof(MainWindow).GetMethod("DrawLoadingOcr", Flags)!.Invoke(window, [dc, new System.Drawing.Rectangle(0, 0, 1200, 800), "星光截图 SCREENSHOT 2026"]);
                    immediateTextMs = drawing.Elapsed.TotalMilliseconds;
                }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (!(bool)typeof(MainWindow).GetField("_ready", Flags)!.GetValue(window)!) await Task.Delay(20, timeout.Token);
                long readyMs = timer.ElapsedMilliseconds;
                var core = (CoreWebView2)typeof(MainWindow).GetField("_core", Flags)!.GetValue(window)!;
                string result;
                do
                {
                    result = await core.ExecuteScriptAsync("Boolean(document.querySelector('.tiptap')?.textContent.includes('SCREENSHOT'))");
                    if (result != "true") await Task.Delay(20, timeout.Token);
                } while (result != "true");
                long resultMs = timer.ElapsedMilliseconds;
                string metrics = await core.ExecuteScriptAsync("JSON.stringify({width:innerWidth,height:innerHeight,overflow:document.documentElement.scrollWidth>innerWidth,editorBorder:getComputedStyle(document.querySelector('.editor-shell')).borderWidth,js:performance.getEntriesByType('resource').filter(x=>x.name.includes('.js')).map(x=>({name:x.name,bytes:x.decodedBodySize}))})");
                if (window.AppWindow.IsVisible) throw new Exception("Test popup must remain hidden");
                if (loadingText?.GetValue(window) is not null) throw new Exception("Loading text must be released after ready");
                rows.Add(new { cycle, readyMs, resultMs, immediateTextMs, beforePrivateBytes = before, privateBytes = Process.GetCurrentProcess().PrivateMemorySize64, hidden = true, metrics });
                window.Dispose(); window = null;
                await Task.Delay(500);
            }
            File.WriteAllText(Path.Combine(report, "native-popup.json"), JsonSerializer.Serialize(new { ok = true, rows }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(report, "native-popup-error.txt"), ex.ToString());
            Environment.ExitCode = 1;
        }
        finally { window?.Dispose(); Exit(); }
    }
}
