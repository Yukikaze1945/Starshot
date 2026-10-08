using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.DirectX;

namespace Starshot.Features.Screenshot;

internal enum ScreenshotCaptureMode
{
    Lightweight = 0,
    Standard = 1,
    HighQualityHdr = 2,
    HdrVideo = 3,
}

// A capture keeps its mode through selection, recording and encoding. Changing
// the mode retires old WGC contexts before publishing the new persisted setting.
internal static class CaptureModeController
{
    private static readonly object Sync = new();
    private static int _activeOperations;
    private static bool _switching;
    public static event Action? ModeChanged;

    public static CaptureOperation BeginOperation()
    {
        lock (Sync)
        {
            if (_switching) throw new InvalidOperationException("正在切换截图模式，请稍后再截图。");
            _activeOperations++;
            return new CaptureOperation((ScreenshotCaptureMode)AppConfig.ScreenCaptureMode);
        }
    }

    public static async Task SetModeAsync(ScreenshotCaptureMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        lock (Sync)
        {
            if (AppConfig.ScreenCaptureMode == (int)mode) return;
            if (_switching || _activeOperations != 0)
                throw new InvalidOperationException("请结束当前截图或录制后，再切换截图模式。");
            _switching = true;
        }
        try
        {
            await MonitorCaptureContext.ReleaseContextsAsync();
            AppConfig.ScreenCaptureMode = (int)mode;
            Serilog.Log.Information("Screenshot mode changed: mode={Mode}; previous WGC contexts released", mode);
        }
        finally { lock (Sync) _switching = false; }
        ModeChanged?.Invoke();
    }

    public static DirectXPixelFormat PixelFormat(ScreenshotCaptureMode mode, bool hdrDisplay) =>
        UsesHdrCapture(mode)
            ? hdrDisplay && !ScreenCaptureHelper.IsWin10
                ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.R8G8B8A8UIntNormalized
            : DirectXPixelFormat.B8G8R8A8UIntNormalized;

    public static string Label(ScreenshotCaptureMode mode) => mode switch
    {
        ScreenshotCaptureMode.Lightweight => "轻量",
        ScreenshotCaptureMode.Standard => "标准",
        ScreenshotCaptureMode.HdrVideo => "单帧 HDR 视频",
        _ => "高质量 HDR",
    };

    public static bool UsesHdrCapture(ScreenshotCaptureMode mode) =>
        mode is ScreenshotCaptureMode.HighQualityHdr or ScreenshotCaptureMode.HdrVideo;

    public sealed class CaptureOperation(ScreenshotCaptureMode mode) : IDisposable
    {
        public ScreenshotCaptureMode Mode { get; } = mode;
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (Sync) _activeOperations--;
        }
    }
}
