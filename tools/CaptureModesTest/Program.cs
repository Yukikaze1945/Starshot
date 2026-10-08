using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Starshot;
using Starshot.Features.Screenshot;
using Windows.Graphics.DirectX;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine("PASS " + name);
    passed++;
}
async Task Reject<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); }
    catch (T) { Check(true, name); return; }
    throw new Exception("Expected " + typeof(T).Name + ": " + name);
}

// Run against the linked production controller. The fake retirement boundary
// allows us to hold a transition open and to simulate disposal failure.
AppConfig.ScreenCaptureMode = 2;
using (var operation = CaptureModeController.BeginOperation())
{
    Check(operation.Mode == ScreenshotCaptureMode.HighQualityHdr, "default operation preserves HDR mode");
    await Reject<InvalidOperationException>(() => CaptureModeController.SetModeAsync(ScreenshotCaptureMode.Lightweight),
        "switch is rejected while a capture owns its mode");
    Check(AppConfig.ScreenCaptureMode == 2 && MonitorCaptureContext.Releases == 0,
        "rejected switch cannot change configuration or retire live resources");
}
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
MonitorCaptureContext.Retire = () => release.Task;
Task switchTask = CaptureModeController.SetModeAsync(ScreenshotCaptureMode.Lightweight);
Check(!switchTask.IsCompleted && AppConfig.ScreenCaptureMode == 2, "mode is published only after WGC retirement");
await Reject<InvalidOperationException>(() => { using var ignored = CaptureModeController.BeginOperation(); return Task.CompletedTask; },
    "new capture cannot race a mode transition");
await Reject<InvalidOperationException>(() => CaptureModeController.SetModeAsync(ScreenshotCaptureMode.Standard),
    "overlapping mode transitions are rejected");
release.SetResult();
await switchTask;
using (var operation = CaptureModeController.BeginOperation())
    Check(operation.Mode == ScreenshotCaptureMode.Lightweight, "capture resumes in lightweight mode after retirement");
MonitorCaptureContext.Retire = () => Task.FromException(new InvalidOperationException("test retirement failure"));
await Reject<InvalidOperationException>(() => CaptureModeController.SetModeAsync(ScreenshotCaptureMode.Standard),
    "retirement failure is reported");
using (var operation = CaptureModeController.BeginOperation())
    Check(operation.Mode == ScreenshotCaptureMode.Lightweight, "failed transition preserves mode and releases its switching guard");
MonitorCaptureContext.Retire = () => Task.CompletedTask;
await CaptureModeController.SetModeAsync(ScreenshotCaptureMode.Standard);
var doubleDispose = CaptureModeController.BeginOperation();
doubleDispose.Dispose();
doubleDispose.Dispose();
await CaptureModeController.SetModeAsync(ScreenshotCaptureMode.HighQualityHdr);
Check(AppConfig.ScreenCaptureMode == 2, "operation disposal is idempotent and later modes remain usable");
await CaptureModeController.SetModeAsync(ScreenshotCaptureMode.HdrVideo);
Check(AppConfig.ScreenCaptureMode == 3 && CaptureModeController.Label(ScreenshotCaptureMode.HdrVideo) == "单帧 HDR 视频",
    "fourth mode is persisted and labelled consistently");
using (var video = CaptureModeController.BeginOperation())
    await Reject<InvalidOperationException>(() => CaptureModeController.SetModeAsync(ScreenshotCaptureMode.Lightweight), "video operation holds its mode until save completes");
await Reject<ArgumentOutOfRangeException>(() => CaptureModeController.SetModeAsync((ScreenshotCaptureMode)4), "invalid persisted mode is rejected");
Check(CaptureModeController.PixelFormat(ScreenshotCaptureMode.Lightweight, true) == DirectXPixelFormat.B8G8R8A8UIntNormalized
    && CaptureModeController.PixelFormat(ScreenshotCaptureMode.Standard, true) == DirectXPixelFormat.B8G8R8A8UIntNormalized,
    "both SDR modes remain BGRA8 on an HDR monitor");
Check(CaptureModeController.PixelFormat(ScreenshotCaptureMode.HighQualityHdr, true) == DirectXPixelFormat.R16G16B16A16Float,
    "HDR mode preserves FP16 on an HDR monitor");
Check(CaptureModeController.PixelFormat(ScreenshotCaptureMode.HighQualityHdr, false) == DirectXPixelFormat.R8G8B8A8UIntNormalized,
    "HDR mode preserves the existing SDR-monitor path");
Check(CaptureModeController.PixelFormat(ScreenshotCaptureMode.HdrVideo, true) == DirectXPixelFormat.R16G16B16A16Float
    && CaptureModeController.PixelFormat(ScreenshotCaptureMode.HdrVideo, false) == DirectXPixelFormat.R8G8B8A8UIntNormalized,
    "single-frame video uses the HDR path and preserves SDR fallback");

// Actual desktop BitBlt/DIB readback; no WGC, GUI framework, or screenshot files.
int x = Native.GetSystemMetrics(76) + 80, y = Native.GetSystemMetrics(77) + 80;
if (!args.Contains("--no-desktop"))
using (var frozen = GdiCaptureFrame.Capture(x, y, 257, 133))
{
    Check(frozen.Width == 257 && frozen.Height == 133 && frozen.Pixels.Length == 257 * 133 * 4,
        "GDI returns the requested top-down BGRA8 RAM frame");
    bool opaque = true;
    for (int i = 3; i < frozen.Pixels.Length; i += 4) opaque &= frozen.Pixels[i] == 255;
    Check(opaque, "BI_RGB capture normalizes undefined alpha to opaque");
    byte[] crop = frozen.Crop(11, 17, 83, 49);
    bool same = true;
    for (int row = 0; row < 49; row++)
        same &= crop.AsSpan(row * 83 * 4, 83 * 4).SequenceEqual(frozen.Pixels.AsSpan(((row + 17) * 257 + 11) * 4, 83 * 4));
    Check(same, "CPU selection crop retains exact frozen pixels and stride");
    await Reject<ArgumentOutOfRangeException>(() => { frozen.Crop(240, 0, 83, 49); return Task.CompletedTask; },
        "selection outside frozen desktop is rejected");
    frozen.Dispose();
    await Reject<ObjectDisposedException>(() => { _ = frozen.Pixels; return Task.CompletedTask; }, "RAM frame cannot be reused after disposal");
}
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    await Reject<OperationCanceledException>(() => { using var ignored = GdiCaptureFrame.Capture(x, y, 64, 64, cancellation.Token); return Task.CompletedTask; },
        "canceled capture cannot acquire native resources");
}
using var process = Process.GetCurrentProcess();
uint before = Native.GetGuiResources(process.Handle, 0);
for (int i = 0; i < 10; i++)
{
    using var frame = GdiCaptureFrame.Capture(x, y, 320, 180);
    _ = frame.Crop(2, 3, 37, 29);
}
uint after = Native.GetGuiResources(process.Handle, 0);
Check(after == before, $"GDI handles remain flat across 10 captures: {before} -> {after}");
Console.WriteLine($"{passed} checks passed. No images written.");

internal static partial class Native
{
    [LibraryImport("user32.dll")] internal static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll")] internal static partial uint GetGuiResources(nint process, uint flag);
}
