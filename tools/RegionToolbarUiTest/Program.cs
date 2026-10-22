using System.Collections;
using System.Reflection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;
using Starshot.Features.Screenshot;
using Windows.Foundation;
using Windows.System;

internal static class Program
{
    [STAThread] internal static void Main()
    {
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string path = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "Starshot.dll"));
        Trace("WinRT init begin");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Trace("WinRT init done; Application.Start begin");
        Application.Start(callback =>
        {
            Trace("Application.Start callback");
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            try { _ = new Starshot.ToolbarValidation.ToolbarTestApp(); }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("STARSHOT_TOOLBAR_UI_REPORT")!, "xaml-interaction.log"), ex.ToString());
                Environment.Exit(1);
            }
        });
    }
    internal static void Trace(string phase) => File.AppendAllText(Path.Combine(Environment.GetEnvironmentVariable("STARSHOT_TOOLBAR_UI_REPORT")!, "xaml-runner.log"), phase + "\n");
}

// Retain the real XAML metadata/resources; override launch so no tray, hotkeys, singleton,
// configuration, WGC capture, browser, installed app or desktop input is involved.
namespace Starshot.ToolbarValidation
{
public sealed partial class ToolbarTestApp : Starshot.App
{
    private int _checks;
    private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly Type WindowType = typeof(RegionCaptureWindow);
    private static object? Field(object value, string name) => WindowType.GetField(name, Flags)!.GetValue(value);
    private static void Set(object value, string name, object? content) => WindowType.GetField(name, Flags)!.SetValue(value, content);
    private static object? Call(object value, string name, params object?[] args) => WindowType.GetMethod(name, Flags)!.Invoke(value, args);
    private static object Command(string name) => Enum.Parse(WindowType.Assembly.GetType("Starshot.Features.Screenshot.RegionToolbarCommand")!, name);
    private static void State(object value, string name) => Set(value, "_state", Enum.Parse(WindowType.GetNestedType("RegionCaptureState", BindingFlags.NonPublic)!, name));
    private void Check(bool condition, string label) { _checks++; if (!condition) throw new InvalidOperationException(label); }
    private static void Click(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        global::Program.Trace("Test launch callback");
        string report = Environment.GetEnvironmentVariable("STARSHOT_TOOLBAR_UI_REPORT") ?? throw new InvalidOperationException("Report directory is required");
        RegionCaptureWindow? window = null;
        try
        {
            ((System.Timers.Timer)typeof(Starshot.App).GetField("_gcTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!).Stop();
            typeof(Starshot.AppConfig).GetProperty("LogFile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, Path.Combine(report, "toolbar-app.log"));
            if(Environment.GetEnvironmentVariable("STARSHOT_CAPTURE_EDITOR_ONLY")=="1")
            {
                await ValidateCaptureEditorAsync();
                File.WriteAllText(Path.Combine(report,"xaml-interaction.log"),$"PASS {_checks} capture-editor ownership/bridge/draft/teardown checks; hidden HWNDs; synthetic CPU pixels only; no WebView startup, capture, model inference or desktop input.\n");
                Environment.Exit(0);return;
            }
            if(Environment.GetEnvironmentVariable("STARSHOT_SUBTITLE_PIN_ONLY")=="1")
            {
                ValidateSubtitlePin();
                File.WriteAllText(Path.Combine(report,"subtitle-pin.log"),$"PASS {_checks} subtitle pin/native topmost checks; hidden HWND; no browser, capture or models.\n");
                Environment.Exit(0);return;
            }
            if(Environment.GetEnvironmentVariable("STARSHOT_OCR_STARTUP_ONLY")=="1")
            {
                await ValidateOcrStartupAsync(report);
                Environment.Exit(0);return;
            }
            if (Environment.GetEnvironmentVariable("STARSHOT_CHROME_ONLY") == "1")
            {
                typeof(Starshot.AppConfig).GetProperty("LogFile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
                    .SetValue(null, Path.Combine(report, "native-chrome-app.log"));
                await ValidateWindowChromeAsync();
                File.WriteAllText(Path.Combine(report, "xaml-interaction.log"), $"PASS {_checks} window chrome checks; hidden HWNDs; no WebView startup, capture, textures, model inference or desktop input.\n");
                Environment.Exit(0); return;
            }
            window = new RegionCaptureWindow();
            Call(window, "MoveOffscreen"); // Never Show/Activate this synthetic test overlay.
            if(Environment.GetEnvironmentVariable("STARSHOT_FRAME_STYLE_ONLY")=="1")
            {
                await ValidateFrameStyleAsync(window,report);
                window.Close();Environment.Exit(0);return;
            }
            if(Environment.GetEnvironmentVariable("STARSHOT_REGION_TRANSLATION_ONLY")=="1")
            {
                ValidateRegionTranslation(window);
                File.WriteAllText(Path.Combine(report,"region-translation.log"),$"PASS {_checks} hidden native translation/undo/copy/subtitle selection checks; synthetic pixels, no models, desktop capture or input.");
                window.Close();Environment.Exit(0);return;
            }
            if (Environment.GetEnvironmentVariable("STARSHOT_MULTI_MONITOR_ONLY") == "1")
            {
                await ValidateMultiMonitorViewport(window, report);
                File.WriteAllText(Path.Combine(report,"xaml-interaction.log"),$"PASS {_checks} multi-monitor viewport/FP16/SDR/crop/annotation/DPI checks; hidden HWND; synthetic pixels only; no WGC, desktop capture/input or model inference.\n");
                window.Close();Environment.Exit(0);return;
            }
            if (Environment.GetEnvironmentVariable("STARSHOT_POINTER_CLOSE_ONLY") == "1")
            {
                ValidateClosedPointerEvents(window);
                File.WriteAllText(Path.Combine(report, "xaml-interaction.log"), $"PASS {_checks} pointer teardown checks; hidden HWND; no capture, textures, model inference or desktop input.\n");
                window.Close(); Environment.Exit(0); return;
            }
            if (Environment.GetEnvironmentVariable("STARSHOT_HDR_DIAGNOSTICS") == "1")
            {
                await ValidateHdrLifecycle(window, report);
                File.WriteAllText(Path.Combine(report, "xaml-interaction.log"), $"PASS {_checks} isolated HDR lifecycle diagnostics; 15 cycles; hidden HWND; no desktop screenshots/input.\n");
                window.Close(); Environment.Exit(0); return;
            }
            Set(window, "_lockedW", 1920f); Set(window, "_lockedH", 1080f); Set(window, "_isClosed", false);
            WindowType.GetProperty("SelectionRect")!.SetValue(window, new Rect(100, 100, 700, 400));
            State(window, "Selected");
            foreach (var entry in new[] { RegionCaptureAction.Copy, RegionCaptureAction.Save, RegionCaptureAction.Ocr })
            {
                Set(window, "_defaultAction", entry); Call(window, "RefreshToolbar");
                var bar = (Border)Field(window, "SelectionToolbar")!;
                Check(bar.Width < 880 && bar.Height > 0, "Compact toolbar uses measured shared geometry");
                var buttons = ((Canvas)Field(window, "ToolbarButtons")!).Children.OfType<Button>().ToArray();
                var primary = buttons.Single(button => button.Tag?.ToString() == entry.ToString());
                Check(((SolidColorBrush)primary.Background).Color == Windows.UI.Color.FromArgb(255, 221, 243, 105), "Entry-specific primary accent");
                Check(buttons.Single(button => button.Tag?.ToString() == "Undo").IsEnabled == false, "Empty history disables undo");
            }
            Call(window, "InvokeToolbar", Command("Rectangle"));
            Check(((Border)Field(window, "ToolbarParameters")!).Visibility == Visibility.Visible, "Annotation opens parameter panel");
            Check(((Canvas)Field(window, "ToolbarParameterButtons")!).Children.Count == 10, "Five colors and five stroke widths");
            var parameters = ((Canvas)Field(window, "ToolbarParameterButtons")!).Children.OfType<Button>().ToArray();
            Click(parameters[1]); await Task.Delay(40);
            Check((Windows.UI.Color)Field(window, "_annotationColor")! == Windows.UI.Color.FromArgb(255, 49, 137, 255), "Actual swatch button click applies its color");
            parameters = ((Canvas)Field(window, "ToolbarParameterButtons")!).Children.OfType<Button>().ToArray();
            Click(parameters[8]); await Task.Delay(40);
            Check((int)Field(window, "_annotationWidth")! == 8, "Actual width button click applies explicit preset");
            var split = ((Canvas)Field(window, "ToolbarButtons")!).Children.OfType<Button>().Single(button => button.Tag?.ToString() == "Shapes");
            Click(split); await Task.Delay(40);
            Check(((Border)Field(window, "ToolbarPopup")!).Visibility == Visibility.Visible, "In-overlay group popup");
            Call(window, "HandleCaptureKey", VirtualKey.Down); Call(window, "HandleCaptureKey", VirtualKey.Enter);
            Check(Field(window, "_annotationTool")!.ToString() == "Ellipse", "Keyboard group selection activates tool");
            Call(window, "InvokeToolbar", Command("Text"));
            Check(((Canvas)Field(window, "ToolbarParameterButtons")!).Children.Count == 5, "Text shows only applicable color parameters");
            Call(window, "OpenToolbarMenu", Command("More"));
            Check((bool)Call(window, "ConsumeToolbarCanvasPress", new Point(200, 200))!, "Outside press consumes popup gesture");
            Check(Field(window, "_state")!.ToString() == "Selected" && ((IList)Field(window, "_annotations")!).Count == 0, "Dismissal does not alter selection or annotations");
            Check((bool)Call(window, "ConsumeToolbarCanvasPress", new Point(200, 200))!, "Second press of dismissal double-click also consumed");
            Call(window, "OpenToolbarMenu", Command("Brushes")); Call(window, "HandleCaptureKey", VirtualKey.Escape);
            Check(Field(window, "_state")!.ToString() == "Selected", "Esc closes menu before selection");
            var editor = (TextBox)Field(window, "AnnotationTextEditor")!;
            editor.Text = "discard"; editor.Visibility = Visibility.Visible;
            Call(window, "HandleCaptureKey", VirtualKey.Escape);
            Check(editor.Visibility == Visibility.Collapsed && ((IList)Field(window, "_annotations")!).Count == 0, "Esc discards text before selection");
            Call(window, "InvokeToolbar", Command("Help"));
            Check(((Border)Field(window, "ShortcutHint")!).Visibility == Visibility.Visible, "Shortcut help is on demand");
            Call(window, "HandleCaptureKey", VirtualKey.Escape);
            Check(((Border)Field(window, "ShortcutHint")!).Visibility == Visibility.Collapsed, "Esc hides help");
            Call(window, "HandleCaptureKey", VirtualKey.Escape);
            Check(Field(window, "_state")!.ToString() == "Selecting", "Next Esc returns to selection");
            Call(window, "HandleCaptureKey", VirtualKey.Escape);
            Check(Field(window, "_state")!.ToString() == "Closed", "Final Esc closes capture");
            Check(Field(window, "_swapChain") is null && Field(window, "_canvasOriginal") is null, "No synthetic test capture/device was created");
            if (Environment.GetEnvironmentVariable("STARSHOT_TOOLBAR_EDITOR_ONLY") == "1")
            {
                State(window, "Selected");
                Call(window, "RefreshToolbar"); Call(window, "OpenToolbarMenu", Command("More"));
                var menu = ((Canvas)Field(window,"ToolbarPopupButtons")!).Children.OfType<Button>().ToArray();
                Check(menu[^2].Tag.ToString()=="Help" && menu[^1].Tag.ToString()=="EditToolbar", "Actual XAML menu has fixed help/edit footer");
                Check(Canvas.GetTop(menu[^2]) + menu[^2].Height <= Canvas.GetTop(menu[^1]), "Editor below help without overlap");
                Check(menu.Any(b=>b.Tag.ToString()=="QuickSave"), "Quick Save independently exposed");
                Check(menu.Any(b=>b.Tag.ToString()=="QrCode" && b.IsEnabled), "QR recognition is available in the actual HDR toolbar More menu");
                Check(Field(window,"_swapChain") is null && Field(window,"_canvasOriginal") is null, "Editor layout test does not initialize capture resources");
                File.WriteAllText(Path.Combine(report,"xaml-interaction.log"),$"PASS {_checks} real XAML toolbar/edit-footer/keyboard checks; hidden HWND; no textures, models, capture or desktop input.\n");
                window.Close(); Environment.Exit(0); return;
            }
            ValidateNativeHdrPreview(window);
            await ValidateHdrAnalysis(window);
            await BenchmarkHdrAnalysis(window, report);
            File.WriteAllText(Path.Combine(report, "xaml-interaction.log"), $"PASS {_checks} real XAML toolbar checks; hidden HWND; 0 screenshots; 0 desktop input; installed Starshot untouched.\n");
            window.Close(); Environment.Exit(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(report, "xaml-interaction.log"), ex.ToString()+"\n"+string.Join("\n",ex.Data.Cast<DictionaryEntry>().Select(item=>$"{item.Key}: {item.Value}")));
            window?.Close(); Environment.Exit(1);
        }
    }

    private async Task ValidateCaptureEditorAsync()
    {
        var assembly=typeof(Starshot.Features.ViewHost.MainWindow).Assembly;
        var documentType=assembly.GetType("Starshot.Features.Workspace.WorkspaceDocument")!;
        var bridgeType=assembly.GetType("Starshot.Features.WebUI.WebUiBridge")!;
        object Doc(string id)=>Activator.CreateInstance(documentType,["截图.png",id,2,2,new byte[16],null,203f,"区域截图"])!;
        bool Disposed(object doc){try{documentType.GetProperty("Bgra")!.GetValue(doc);return false;}catch(TargetInvocationException ex)when(ex.InnerException is ObjectDisposedException){return true;}}
        using var first=new Starshot.Features.ViewHost.MainWindow(captureEditor:true);
        using var second=new Starshot.Features.ViewHost.MainWindow(captureEditor:true);
        // Prevent the queued initialization before yielding to the UI dispatcher.
        first.ForceExit=second.ForceExit=true;
        var hostType=first.GetType();var pending=hostType.GetField("_pendingCapture",Flags)!;
        var take=hostType.GetMethod("TakeCaptureDocument",Flags)!;
        var a=Doc("first");var b=Doc("second");pending.SetValue(first,a);pending.SetValue(second,b);
        Check(!first.AppWindow.IsVisible&&!second.AppWindow.IsVisible,"Capture utility hosts remain hidden during test");
        Check((bool)hostType.GetProperty("IsCaptureEditor",Flags)!.GetValue(first)!,"Explicit capture-only native surface");
        Check(assembly.GetType("Starshot.Features.Workspace.WorkspaceCaptureBroker") is null,"No process-global capture broker remains");
        var bridge=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(bridgeType);
        bridgeType.GetField("_window",Flags)!.SetValue(bridge,first);
        bridgeType.GetField("_workspaceGate",Flags)!.SetValue(bridge,new SemaphoreSlim(1));
        async Task<object?> CallBridge(string method,string json="{}")
        {
            using var value=System.Text.Json.JsonDocument.Parse(json);
            return await (Task<object?>)bridgeType.GetMethod("WorkspaceAsync",Flags)!.Invoke(bridge,[method,value.RootElement,CancellationToken.None])!;
        }
        try
        {
            await CallBridge("workspace.pending");
            Check(ReferenceEquals(bridgeType.GetField("_document",Flags)!.GetValue(bridge),a),"Capture document transfers to its own bridge");
            Check(take.Invoke(first,null) is null,"Capture input is consumed exactly once");
            Check(ReferenceEquals(pending.GetValue(second),b)&&!Disposed(b),"Second capture is not stolen or disposed");
            await CallBridge("workspace.pending");
            Check(!Disposed(a),"Frontend reload reuses its current document");
            await CallBridge("workspace.draft.save","{\"draft\":{\"version\":1,\"points\":[{\"x\":2,\"y\":1}]}}");
            object? draft=await CallBridge("workspace.draft.load");
            Check(draft is System.Text.Json.JsonElement value&&value.GetProperty("points").GetArrayLength()==1,"Incomplete vertices are retained in per-window memory");
            await CallBridge("workspace.close");
            Check(Disposed(a),"Closing the capture releases its CPU pixels");
            Check(await CallBridge("workspace.draft.load") is null,"Closing capture drops temporary draft");
            Check(!Disposed(b),"Closing one capture leaves another input intact");
        }
        finally{bridgeType.GetMethod("CloseWorkspace",Flags)!.Invoke(bridge,null);}
        second.Close();Check(Disposed(b)&&take.Invoke(second,null) is null,"Closing before frontend load releases pending image");
        Check(first.AppWindow.TitleBar.ExtendsContentIntoTitleBar,"Capture editor uses integrated native chrome");
        Check(!first.AppWindow.IsVisible,"Bridge processing never activates desktop windows");
    }

    private async Task ValidateWindowChromeAsync()
    {
        foreach (bool compact in new[] { false, true })
        {
            using var host = new Starshot.Features.ViewHost.MainWindow(ocrPopup: compact);
            var bar = host.AppWindow.TitleBar;
            Check(bar.ExtendsContentIntoTitleBar, "WebUI content extends into native title bar");
            Check(bar.IconShowOptions == Microsoft.UI.Windowing.IconShowOptions.HideIconAndSystemMenu, "Duplicate system title and icon removed");
            Check(bar.PreferredHeightOption == (compact ? Microsoft.UI.Windowing.TitleBarHeightOption.Standard : Microsoft.UI.Windowing.TitleBarHeightOption.Tall), "OCR stays compact; main header matches 48 DIP caption controls");
            Check(!host.AppWindow.IsVisible, "Constructing native host does not show it");
            GetWindowRect(host.WindowHandle, out var outer); GetClientRect(host.WindowHandle, out var client);
            Check((outer.Bottom - outer.Top) - (client.Bottom - client.Top) < 16 * host.UIScale,
                "Native client extends into the old title strip; only resize borders remain");
            Check(typeof(Starshot.Features.ViewHost.MainWindow).GetField("_controller", Flags)!.GetValue(host) is null, "No browser initialized for chrome-only validation");
            using var state = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(typeof(Starshot.Features.ViewHost.MainWindow).GetMethod("ChromeState", Flags)!.Invoke(host, null)));
            Check(state.RootElement.GetProperty("enabled").GetBoolean() && state.RootElement.GetProperty("height").GetInt32() == (compact ? 32 : 48), "Bootstrap carries actual native chrome mode");
            Check(state.RootElement.GetProperty("inset").GetDouble() > 0, "Actual system caption inset is supplied in DIP");
            // Stop the queued browser initialization, then let the native title-bar
            // layout/input region changes settle on their owning dispatcher.
            typeof(Starshot.Features.ViewHost.MainWindow).GetMethod("DetachWebUiForRelease",Flags)!.Invoke(host,null);
            await Task.Delay(100);
            var nativeRegions=Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(host.AppWindow.Id)
                .GetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough);
            File.AppendAllText(Path.Combine(Environment.GetEnvironmentVariable("STARSHOT_TOOLBAR_UI_REPORT")!,"native-header-hit.log"),
                $"compact={compact}, passthrough="+string.Join(";",nativeRegions.Select(r=>$"{r.X},{r.Y},{r.Width},{r.Height}"))+"\n");
            var hitPoints = new[] { 24, 100, Math.Max(24, (int)(client.Right/host.UIScale-state.RootElement.GetProperty("inset").GetDouble()-50)) };
            foreach(int x in hitPoints)
            {
                var point = new ChromePoint { X=(int)(x*host.UIScale),Y=(int)(16*host.UIScale) };ClientToScreen(host.WindowHandle,ref point);
                nint hit=SendMessage(host.WindowHandle,0x84,0,(nint)(((long)(ushort)point.Y<<16)|(ushort)point.X));
                File.AppendAllText(Path.Combine(Environment.GetEnvironmentVariable("STARSHOT_TOOLBAR_UI_REPORT")!,"native-header-hit.log"),$"compact={compact}, scale={host.UIScale}, xDip={x}, yDip=16, hit={hit}\n");
                if(Environment.GetEnvironmentVariable("STARSHOT_VERIFY_HEADER_INPUT")=="1")
                    Check(hit==1,"Native title strip must pass header button clicks through as HTCLIENT");
            }
            if(Environment.GetEnvironmentVariable("STARSHOT_VERIFY_HEADER_INPUT")=="1")
            {
                var source=Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(host.AppWindow.Id);
                var regions=source.GetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough);
                Check(regions.Length==1 && regions[0].Y>0 && regions[0].X+regions[0].Width<=client.Right-host.AppWindow.TitleBar.RightInset,
                    "Passthrough excludes resize edge and native caption controls");
                foreach(var protectedPoint in new[]{new ChromePoint{X=client.Right-(int)(20*host.UIScale),Y=(int)(16*host.UIScale)},
                    new ChromePoint{X=client.Right/2,Y=0}})
                {
                    var screen=protectedPoint;ClientToScreen(host.WindowHandle,ref screen);
                    var protectedHit=SendMessage(host.WindowHandle,0x84,0,(nint)(((long)(ushort)screen.Y<<16)|(ushort)screen.X));
                    Check(protectedHit!=1,"System caption button strip and resize edge are not changed to client input");
                }
                host.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(850*host.UIScale),(int)(520*host.UIScale)));
                typeof(Starshot.Features.ViewHost.MainWindow).GetMethod("UpdateWebBounds",Flags)!.Invoke(host,null);
                GetClientRect(host.WindowHandle,out var resized);
                var updated=source.GetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough);
                Check(updated[0].Width==resized.Right-host.AppWindow.TitleBar.RightInset,"Native passthrough follows current client size and actual caption inset");
            }
        }
        var utility = new Window { Content = new Grid() };
        try
        {
            Type chrome = WindowType.Assembly.GetType("Starshot.Frameworks.WindowChrome")!;
            chrome.GetMethod("AddHeader", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { utility, "测试工具" });
            var shell = (Grid)utility.Content;
            Check(utility.AppWindow.TitleBar.ExtendsContentIntoTitleBar && shell.RowDefinitions[0].Height.Value == 48, "Utility content and title share one native header");
            Check(shell.Children.Count == 2 && !utility.AppWindow.IsVisible, "Utility keeps original content; remains hidden");
            var conversion = new ImageBatchConvertWindow();
            Check(conversion.FindName("ConversionDragArea") is FrameworkElement, "Batch conversion exposes a separate drag region");
        }
        finally { utility.Close(); }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ChromeRect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ChromePoint { public int X,Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd,ref ChromePoint point);
    [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="SendMessageW")] private static extern nint SendMessage(nint hwnd,uint message,nint wparam,nint lparam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out ChromeRect rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out ChromeRect rect);

    private void ValidateClosedPointerEvents(RegionCaptureWindow window)
    {
        // No event data or bitmap is available after teardown. A queued event
        // must return before querying either, including while completion runs.
        foreach (string state in new[] { "Completing", "Closed" })
        {
            State(window, state);
            foreach (string handler in new[] { "Canvas_PointerPressed", "Canvas_PointerMoved", "Canvas_PointerReleased" })
            {
                Call(window, handler, window, null);
                Check(Field(window, "_state")!.ToString() == state, $"Late {handler} cannot resurrect {state} session");
            }
        }
        State(window, "Selecting"); Set(window, "_isClosed", false);
        var dragType = WindowType.GetNestedType("DragMode", BindingFlags.NonPublic)!;
        Set(window, "_dragMode", Enum.Parse(dragType, "Creating"));
        Set(window, "_toolbarDismissedPointer", true);
        Call(window, "CancelCapture");
        Check(Field(window, "_state")!.ToString() == "Closed" && Field(window, "_dragMode")!.ToString() == "None",
            "Cancel during drag clears pending selection ownership");
        Check(!(bool)Field(window, "_toolbarDismissedPointer")! && Field(window, "_canvasOriginal") is null,
            "Close releases gesture flags without constructing a capture texture");
        Call(window, "Canvas_PointerReleased", window, null);
        Call(window, "Canvas_PointerMoved", window, null);
        Check(Field(window, "_state")!.ToString() == "Closed", "Release/move after cancellation leave closed session intact");
    }

    private void AnalysisMonitors(object window, params (Rect Bounds, bool Hdr, float White, float Peak)[] monitors)
    {
        var monitorType = WindowType.GetNestedType("AnalysisMonitor", BindingFlags.NonPublic)!;
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(monitorType))!;
        foreach (var monitor in monitors)
            list.Add(Activator.CreateInstance(monitorType, monitor.Bounds, monitor.Hdr, monitor.White, monitor.Peak)!);
        Set(window, "_analysisMonitors", list);
    }
    private async Task ValidateHdrAnalysis(RegionCaptureWindow window)
    {
        var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
        int width = 64, height = 48;
        var pixels = new Half[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        { pixels[i] = pixels[i + 1] = pixels[i + 2] = (Half)2.5f; pixels[i + 3] = (Half)1; }
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();
        using var source = Microsoft.Graphics.Canvas.CanvasBitmap.CreateFromBytes(device, bytes, width, height,
            Windows.Graphics.DirectX.DirectXPixelFormat.R16G16B16A16Float, 96, Microsoft.Graphics.Canvas.CanvasAlphaMode.Ignore);
        var previewBytes = Enumerable.Repeat((byte)127, width * height * 4).ToArray();
        using var preview = Microsoft.Graphics.Canvas.CanvasBitmap.CreateFromBytes(device, previewBytes, width, height,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, Microsoft.Graphics.Canvas.CanvasAlphaMode.Ignore);
        Set(window, "_canvasOriginal", source); Set(window, "_displayBitmap", preview);
        Set(window, "_isClosed", false); Set(window, "_scale", 2f); Set(window, "_vx", 0); Set(window, "_vy", 0);
        Set(window, "_lockedW", 32f); Set(window, "_lockedH", 24f);
        WindowType.GetProperty("SelectionRect")!.SetValue(window, new Rect(0, 0, 32, 24));
        State(window, "Selected");
        ValidateActualHdrMetadata(window);
        AnalysisMonitors(window, (new Rect(0, 0, 64, 48), false, 80, 0));
        Check(!(bool)Call(window, "CanAnalyzeHdr")!, "SDR monitor region cannot masquerade as absolute HDR in a float composite");
        AnalysisMonitors(window, (new Rect(0, 0, 32, 48), true, 203, 1000), (new Rect(32, 0, 32, 48), false, 80, 0));
        Check(!(bool)Call(window, "CanAnalyzeHdr")!, "Mixed SDR/HDR region is disabled");
        AnalysisMonitors(window, (new Rect(0, 0, 64, 48), true, 203, 1000));
        Check((bool)Call(window, "CanAnalyzeHdr")!, "Actual raw FP16 HDR region enables analysis");
        Call(window, "RefreshToolbar"); Call(window, "OpenToolbarMenu", Command("More"));
        Check(((Canvas)Field(window, "ToolbarPopupButtons")!).Children.OfType<Button>().Single(b => b.Tag?.ToString() == "HdrAnalysis").IsEnabled,
            "More menu exposes enabled HDR analyzer");
        Call(window, "DismissToolbarOutsidePress");
        using var cropBefore = (Microsoft.Graphics.Canvas.CanvasRenderTarget)Call(window, "CropDisplayToBgra")!;
        var originalCrop = cropBefore.GetPixelBytes();
        await (Task)Call(window, "OpenHdrAnalysisAsync")!;
        var analysis = (HdrLuminanceAnalysis)Field(window, "_hdrAnalysis")!;
        Check(analysis.Stats.Max == 200 && analysis.Stats.Average == 200 && analysis.Stats.P99 == 200, "GPU readback uses FP16 instead of SDR preview; no white renormalization");
        Check(((Border)Field(window, "HdrAnalysisPanel")!).Visibility == Visibility.Visible, "Closable floating analysis panel is visible");
        var panel = (Border)Field(window,"HdrAnalysisPanel")!;
        Call(window,"MoveAnalysisPanel",panel,new System.Drawing.RectangleF(-100000,-100000,360,480),false);
        double left=Canvas.GetLeft(panel),top=Canvas.GetTop(panel);Call(window,"RefreshToolbar");
        Check(Field(window,"_analysisManualBounds") is not null&&Canvas.GetLeft(panel)==left&&Canvas.GetTop(panel)==top,"Dragged HDR panel stays positioned after toolbar refresh");
        Call(window, "UpdateHdrAnalysisCursor", new Point(15.5, 10.5));
        Check(((TextBlock)Field(window, "_analysisCursor")!).Text == "200", "200% DPI cursor maps to cached source pixel");
        Check(((TextBlock)Field(window, "_analysisWhite")!).Text == "203", "SDR white reference is separate from content peak");
        var expander = (Expander)Field(window, "_analysisWaveExpander")!; expander.IsExpanded = true;
        await Task.Delay(150);
        Check(((Image)Field(window, "_analysisWaveform")!).Source is not null, "Expanded waveform generated from cached luminance");
        ((ComboBox)Field(window, "_analysisScale")!).SelectedIndex = 2;
        await Task.Delay(150);
        Check(((TextBlock)Field(window, "_analysisWaveLabels")!).Text.Contains("1000"), "Fixed nit scale and reference markers are labeled");
        ((ToggleSwitch)Field(window, "_analysisHeatmapSwitch")!).IsOn = true;
        await Task.Delay(150);
        Check(Field(window, "_analysisHeatmap") is not null, "Explicit heatmap switch prepares display-only texture");
        using var cropAfter = (Microsoft.Graphics.Canvas.CanvasRenderTarget)Call(window, "CropDisplayToBgra")!;
        Check(cropAfter.GetPixelBytes().SequenceEqual(originalCrop) && source.GetPixelBytes().SequenceEqual(bytes), "Heatmap never changes original source or output crop");
        await (Task)Call(window,"OpenGamutAnalysisAsync")!;
        var gamut=(Starshot.Features.Workspace.GamutAnalysis.Summary)Field(window,"_gamutStats")!;
        Check(gamut.Srgb==100&&gamut.Samples==width*height,"Region gamut reads original FP16 via bounded stripes");
        var gamutPanel=(Border)Field(window,"GamutAnalysisPanel")!;
        Check(gamutPanel.Visibility==Visibility.Visible,"Gamut panel visible in the capture overlay");
        Call(window,"MoveAnalysisPanel",gamutPanel,new System.Drawing.RectangleF(100000,100000,360,690),true);
        Check(Field(window,"_gamutManualBounds") is not null,"Gamut panel shares drag positioning");
        ((ToggleSwitch)Field(window,"_gamutSwitch")!).IsOn=true;
        Check(Field(window,"_gamutTexture") is not null,"Gamut map only creates a texture when explicitly enabled");
        Check(!((ToggleSwitch)Field(window,"_analysisHeatmapSwitch")!).IsOn&&Field(window,"_analysisHeatmap") is null,"Gamut and luminance pseudo-colors cannot misleadingly blend together");
        using(var gamutCrop=(Microsoft.Graphics.Canvas.CanvasRenderTarget)Call(window,"CropDisplayToBgra")!)
            Check(gamutCrop.GetPixelBytes().SequenceEqual(originalCrop)&&source.GetPixelBytes().SequenceEqual(bytes),"Gamut overlay does not modify copied/saved SDR or original HDR");
        ((ToggleSwitch)Field(window,"_gamutSwitch")!).IsOn=false;
        Check(Field(window,"_gamutTexture") is null,"Turning off gamut disposes GPU map");
        Call(window,"CloseGamutAnalysis");
        Check(Field(window,"_gamutMap") is null&&Field(window,"_gamutStats") is null&&Field(window,"_gamutCancellation") is null,"Gamut close releases data and cancellation owner");
        var oldGamut=(Task)Call(window,"OpenGamutAnalysisAsync")!;
        var newGamut=(Task)Call(window,"OpenGamutAnalysisAsync")!;
        Call(window,"CloseGamutAnalysis");await Task.WhenAll(oldGamut,newGamut);
        Check(Field(window,"_gamutMap") is null&&gamutPanel.Visibility==Visibility.Collapsed,"Canceled old gamut generation cannot publish a late map");
        ((ToggleSwitch)Field(window, "_analysisHeatmapSwitch")!).IsOn = false;
        Check(Field(window, "_analysisHeatmap") is null, "Returning to original preview disposes heatmap texture");
        Call(window, "HandleCaptureKey", VirtualKey.Escape);
        Check(Field(window, "_hdrAnalysis") is null && Field(window, "_analysisHeatmap") is null && Field(window, "_analysisCancellation") is null,
            "Esc releases CPU cache, GPU heatmap, cancellation owner");
        Check(Field(window, "_state")!.ToString() == "Selected", "Closing analysis leaves selection intact");
        await (Task)Call(window,"OpenGamutAnalysisAsync")!;
        Call(window,"HandleCaptureKey",VirtualKey.Escape);
        Check(Field(window,"_gamutCancellation") is null&&Field(window,"_state")!.ToString()=="Selected","Esc closes gamut before changing or canceling the selection");
        await (Task)Call(window, "OpenHdrAnalysisAsync")!;
        await (Task)Call(window,"OpenGamutAnalysisAsync")!;
        WindowType.GetProperty("SelectionRect")!.SetValue(window, new Rect(1, 1, 20, 16));
        Call(window, "RefreshToolbar");
        Check(Field(window, "_hdrAnalysis") is null, "Selection change invalidates analysis data");
        Check(Field(window,"_gamutMap") is null&&Field(window,"_gamutCancellation") is null,"Selection change also clears gamut data");
        var pending = (Task)Call(window, "OpenHdrAnalysisAsync")!;
        Call(window, "CloseHdrAnalysis"); await pending;
        Check(Field(window, "_hdrAnalysis") is null && ((Border)Field(window, "HdrAnalysisPanel")!).Visibility == Visibility.Collapsed,
            "Immediate cancellation cannot resurrect panel or cache");
        await (Task)Call(window, "OpenHdrAnalysisAsync")!;
        Call(window, "CancelCapture");
        Check(Field(window, "_hdrAnalysis") is null && Field(window, "_analysisHeatmap") is null && Field(window, "_canvasOriginal") is null,
            "Capture close releases all analyzer references");
    }

    private void ValidateNativeHdrPreview(RegionCaptureWindow window)
    {
        var device=Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
        var values=new Half[]{(Half)(-.25),(Half).25,(Half)3,(Half)1,(Half)2.5,(Half)2.5,(Half)2.5,(Half)1};
        byte[] raw=System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        using var source=Microsoft.Graphics.Canvas.CanvasBitmap.CreateFromBytes(device,raw,2,1,Windows.Graphics.DirectX.DirectXPixelFormat.R16G16B16A16Float,96,Microsoft.Graphics.Canvas.CanvasAlphaMode.Ignore);
        using var preview=Microsoft.Graphics.Canvas.CanvasBitmap.CreateFromBytes(device,new byte[8],2,1,Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,96,Microsoft.Graphics.Canvas.CanvasAlphaMode.Ignore);
        Set(window,"_canvasOriginal",source);Set(window,"_displayBitmap",preview);Set(window,"_scale",1f);Set(window,"_vx",0);Set(window,"_vy",0);
        Call(window,"CreateOverlaySwapChain",2f,1f,true);
        var swap=(Microsoft.Graphics.Canvas.CanvasSwapChain)Field(window,"_swapChain")!;
        Check(swap.Format==Windows.Graphics.DirectX.DirectXPixelFormat.R16G16B16A16Float&&(bool)Field(window,"_nativeHdrPreview")!,"Native HDR overlay uses an FP16 swap chain");
        using var target=new Microsoft.Graphics.Canvas.CanvasRenderTarget(device,2,1,96,Windows.Graphics.DirectX.DirectXPixelFormat.R16G16B16A16Float,Microsoft.Graphics.Canvas.CanvasAlphaMode.Ignore);
        AnalysisMonitors(window,(new Rect(0,0,2,1),true,200,1000));Set(window,"_previewSdrWhite",200f);
        using(var ds=target.CreateDrawingSession())Call(window,"DrawFrozenPreview",ds,new Rect(0,0,2,1),new Rect(0,0,2,1));
        Check(target.GetPixelBytes().SequenceEqual(raw),"Native frozen preview preserves negative scRGB and >1 highlights without gamma or white renormalization");
        AnalysisMonitors(window,(new Rect(0,0,1,1),true,200,1000),(new Rect(1,0,1,1),false,80,0));
        using(var ds=target.CreateDrawingSession())Call(window,"DrawFrozenPreview",ds,new Rect(0,0,2,1),new Rect(0,0,2,1));
        var mixed=System.Runtime.InteropServices.MemoryMarshal.Cast<byte,Half>(target.GetPixelBytes().AsSpan()).ToArray();
        Check(mixed[0]==(Half)(-.25)&&mixed[2]==(Half)3&&Math.Abs((float)mixed[4]-1)<.002,"Mixed-monitor SDR preview undoes export white boost without changing HDR pixels");
        AnalysisMonitors(window,(new Rect(0,0,2,1),true,200,1000));
        using(var scaled=new Microsoft.Graphics.Canvas.CanvasRenderTarget(device,1,.5f,192,Windows.Graphics.DirectX.DirectXPixelFormat.R16G16B16A16Float,Microsoft.Graphics.Canvas.CanvasAlphaMode.Ignore))
        {
            using(var ds=scaled.CreateDrawingSession())Call(window,"DrawFrozenPreview",ds,new Rect(0,0,1,.5),new Rect(0,0,2,1));
            Check(scaled.GetPixelBytes().SequenceEqual(raw),"200% DPI native preview preserves the physical FP16 pixel mapping");
        }
        Call(window,"CreateOverlaySwapChain",2f,1f,false);
        Check(((Microsoft.Graphics.Canvas.CanvasSwapChain)Field(window,"_swapChain")!).Format==Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized&&!(bool)Field(window,"_nativeHdrPreview")!,"Standard SDR overlay remains 8-bit without native HDR conversion");
        Call(window,"ReleaseSwapChain");Set(window,"_canvasOriginal",null);Set(window,"_displayBitmap",null);
    }

    private void ValidateActualHdrMetadata(RegionCaptureWindow window)
    {
        // Exercise production metadata discovery, including CsWinRT projections in
        // the trimmed publish. Synthetic injected monitor lists missed this path.
        // Only display metadata is queried: no WGC, screenshot or desktop input.
        var areas = Microsoft.UI.Windowing.DisplayArea.FindAll();
        Check(areas.Count > 0, "Real display metadata is available for qualification regression");
        var bounds = areas[0].OuterBounds;
        using var info = Microsoft.Graphics.Display.DisplayInformation.CreateForDisplayId(areas[0].DisplayId);
        bool expectedHdr = info.GetAdvancedColorInfo().CurrentAdvancedColorKind ==
            Microsoft.Graphics.Display.DisplayAdvancedColorKind.HighDynamicRange;
        Set(window, "_vx", bounds.X); Set(window, "_vy", bounds.Y);
        Set(window, "_analysisMonitors", null);
        bool enabled = (bool)Call(window, "CanAnalyzeHdr")!;
        File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("STARSHOT_TOOLBAR_UI_REPORT")!, "hdr-display-eligibility.log"),
            $"display_count={areas.Count}; discovered={((IList)Field(window, "_analysisMonitors")!).Count}; actual_hdr={expectedHdr}; fp16_analysis_enabled={enabled}\n");
        Check(((IList)Field(window, "_analysisMonitors")!).Count == areas.Count,
            "Production discovery reads every actual display without enumerator cast failure");
        Check(enabled == expectedHdr, "FP16 analysis eligibility matches actual display HDR state");
        Call(window, "RefreshToolbar"); Call(window, "OpenToolbarMenu", Command("More"));
        Check(((Canvas)Field(window, "ToolbarPopupButtons")!).Children.OfType<Button>()
            .Single(b => b.Tag?.ToString() == "HdrAnalysis").IsEnabled == expectedHdr,
            "Actual display metadata controls the HDR analyzer menu button");
        Call(window, "DismissToolbarOutsidePress");
        Set(window, "_vx", 0); Set(window, "_vy", 0);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct GpuSample
    {
        public ulong Resident, Shared, Committed, SharedCommitted;
        public uint Adapters, ResidentQueries, CommittedQueries, Failures, Enumerated, FirstFailure;
    }
    [System.Runtime.InteropServices.DllImport("GpuMemory.dll")]
    private static extern uint ReadGpuMemory(uint pid, out GpuSample sample);
    private string MemoryRow(string phase)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        uint error = ReadGpuMemory((uint)process.Id, out var gpu);
        return $"{phase},{DateTimeOffset.Now:O},{process.PrivateMemorySize64},{gpu.Resident},{gpu.Committed},{gpu.Shared},{error},{gpu.ResidentQueries},{gpu.CommittedQueries}";
    }
    private async Task BenchmarkHdrAnalysis(RegionCaptureWindow window, string report)
    {
        int width = 3840, height = 2160;
        var bytes = new byte[width * height * 8];
        for (int p = 0; p < bytes.Length; p += 8)
        {
            // Varying gray 0..10 scRGB in raw FP16 bytes, without extra full-frame arrays.
            ushort h = BitConverter.HalfToUInt16Bits((Half)((p / 8 % width) / (float)width * 10));
            for (int c = 0; c < 6; c += 2) { bytes[p + c] = (byte)h; bytes[p + c + 1] = (byte)(h >> 8); }
            bytes[p + 6] = 0; bytes[p + 7] = 0x3c;
        }
        using var source = Microsoft.Graphics.Canvas.CanvasBitmap.CreateFromBytes(Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice(),
            bytes, width, height, Windows.Graphics.DirectX.DirectXPixelFormat.R16G16B16A16Float, 96, Microsoft.Graphics.Canvas.CanvasAlphaMode.Ignore);
        Set(window, "_canvasOriginal", source); Set(window, "_isClosed", false); Set(window, "_scale", 2f);
        Set(window, "_lockedW", width / 2f); Set(window, "_lockedH", height / 2f);
        WindowType.GetProperty("SelectionRect")!.SetValue(window, new Rect(0, 0, width / 2, height / 2));
        State(window, "Selected"); AnalysisMonitors(window, (new Rect(0, 0, width, height), true, 203, 1000));
        Call(window, "RefreshToolbar");
        var log = new List<string> { "phase,timestamp,private_bytes,dedicated_resident,dedicated_committed,shared_resident,kmt_error,resident_queries,committed_queries" };
        log.Add(MemoryRow("source_ready"));
        var timing = new List<string>();
        for (int cycle = 1; cycle <= 5; cycle++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await (Task)Call(window, "OpenHdrAnalysisAsync")!;
            var analysis = (HdrLuminanceAnalysis?)Field(window, "_hdrAnalysis");
            Check(analysis is not null && analysis.SampleStep == 1 && analysis.SampleCount == width * height,
                "4K real GPU stripes retain all pixels within bounded analysis time");
            Check(Field(window, "_analysisHeatmap") is null, "Default numeric analysis has no heatmap GPU texture");
            timing.Add($"cycle={cycle}; elapsed_ms={watch.ElapsedMilliseconds}; samples={analysis!.SampleCount}; cached_bytes={analysis.Values.Length * 4}; max={analysis.Stats.Max}");
            log.Add(MemoryRow($"analysis_{cycle}"));
            Call(window, "CloseHdrAnalysis"); await Task.Delay(700);
            log.Add(MemoryRow($"closed_{cycle}"));
            Check(Field(window, "_hdrAnalysis") is null && Field(window, "_analysisHeatmap") is null, "4K close drops all analysis buffers/textures");
        }
        var gamutWatch=System.Diagnostics.Stopwatch.StartNew();await (Task)Call(window,"OpenGamutAnalysisAsync")!;
        var gamut=(Starshot.Features.Workspace.GamutAnalysis.Summary?)Field(window,"_gamutStats");
        var gamutMap=(Starshot.Features.Workspace.GamutAnalysis.RegionMap?)Field(window,"_gamutMap");
        Check(gamut is not null&&gamut.Approximate&&gamut.Samples<=1_000_000&&gamut.Srgb==100,"4K gamut uses bounded labeled sampling without false wide-gamut neutral classification");
        Check(gamutMap is not null&&gamutMap.Width<=1024&&gamutMap.Height<=1024&&Field(window,"_gamutTexture") is null,"4K gamut map is bounded and allocates no GPU overlay by default");
        timing.Add($"gamut_elapsed_ms={gamutWatch.ElapsedMilliseconds}; samples={gamut!.Samples}; map_bytes={gamutMap!.Pixels.Length}");log.Add(MemoryRow("gamut_ready"));
        Call(window,"CloseGamutAnalysis");Check(Field(window,"_gamutMap") is null,"4K gamut close releases map");
        File.WriteAllLines(Path.Combine(report, "4k-analysis-memory.csv"), log);
        File.WriteAllLines(Path.Combine(report, "4k-analysis-time.log"), timing);
        Call(window, "CancelCapture");
    }
}
}
