using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Starshot.Features.Screenshot;
using Starshot.Helpers;
using Starshot.Features.ViewHost;

namespace Starshot.Features.Subtitles;

internal static class SubtitleController
{
    private static readonly SemaphoreSlim Actions=new(1);
    private static SubtitleSession? _session;
    private static CancellationTokenSource? _selectionCancellation;
    private static bool _selecting;
    private static string? _operationError;
    private static bool _shuttingDown;
    private static SubtitleRegionOutline? _outline;
    private static Rectangle _outlinedRegion;
    internal static void ReportError(string? text)=>_operationError=text;
    internal static object Snapshot()=>new{hasSession=_session is not null,hasRegion=_session?.Snapshot().Region.Width>0,selecting=_selecting,status=_session?.Snapshot(),operationError=_operationError,topmost=MainWindow.SubtitlePanelTopmost};
    internal static string Text()=>_session?.Text()??"";
    internal static async Task ConfigureAsync(bool fresh=false)
    {
        EnsureAvailable();if(_selecting)throw new InvalidOperationException("请先完成或取消当前区域框选。");
        _selecting=true;
        if(_session?.Snapshot().State is "running" or "paused")_session.Pause(true);
        ClearOutline();
        using var cancellation=new CancellationTokenSource();_selectionCancellation=cancellation;
        MainWindow.HideSubtitlePanel();
        try
        {
            var region=await ScreenCaptureService.SelectSubtitleRegionAsync(cancellation.Token);
            if(region.HasValue&&!cancellation.IsCancellationRequested)await UseSelectionAsync(region.Value,fresh);
        }
        finally
        {
            _selectionCancellation=null;_selecting=false;
            if(!_shuttingDown&&!cancellation.IsCancellationRequested)
            {
                // Cancelling re-selection keeps the old ROI paused and visible.
                if(_session?.Snapshot().State=="editing")_session.Pause();
                if(_session?.Snapshot().State is "running" or "paused")ShowOutline(_session.Snapshot().Region);
                MainWindow.OpenSubtitlePanel();
            }
        }
    }
    // The existing region overlay is the only selector. This method accepts its
    // physical desktop rectangle; the persistent marker has no selection/input UI.
    internal static async Task UseSelectionAsync(Rectangle region,bool fresh=false)
    {
        region=SubtitleSelectionRegion.ToDesktop(region,0,0);
        await Actions.WaitAsync();
        try
        {
            EnsureAvailable();
            if(fresh&&_session is{} previous)
            {await previous.StopAsync();if(previous.Snapshot().Unsaved)throw new InvalidOperationException("当前文案尚未保存，请先复制或导出；当前任务会保留。");}
            EnsureAvailable();
            if(fresh||_session is null)
                _session=new(Path.Combine(AppConfig.UserDataFolder,"SubtitleOCR","subtitle-"+Guid.NewGuid().ToString("N")+".txt"),CaptureAsync,RecognizeAsync);
            _session.Pause(true);_session.SetRegion(region);ShowOutline(region);_session.Start();_operationError=null;
            MainWindow.OpenSubtitlePanel(region);
        }
        finally{Actions.Release();}
    }
    private static void EnsureAvailable(){if(_shuttingDown)throw new InvalidOperationException("应用正在退出，字幕摘录已停止。");}
    internal static void Start()
    {
        EnsureAvailable();if(_selecting)throw new InvalidOperationException("请先完成区域框选。");
        if(_session is null)throw new InvalidOperationException("请先在区域截图中框选字幕。");
        _session.Start();
        try{ShowOutline(_session.Snapshot().Region);}catch{_session.Pause();throw;}
        _operationError=null;
    }
    internal static void Pause(){EnsureAvailable();_session?.Pause();_operationError=null;}
    internal static void Interval(int value){EnsureAvailable();if(_session is null)throw new InvalidOperationException("请先创建字幕区域。");_session.SetInterval(value);_operationError=null;}
    internal static Task ShutdownAsync(){_shuttingDown=true;return StopAsync();}
    internal static async void PanelClosed()
    {
        if(_shuttingDown)return;
        try{await StopAsync();}catch(Exception ex){ReportError(ex.Message);Serilog.Log.Warning(ex,"Stop subtitle OCR after panel close");}
    }
    internal static async Task StopAsync()
    {
        _selectionCancellation?.Cancel();
        ClearOutline(); // Remove immediately, before waiting for in-flight inference.
        await Actions.WaitAsync();
        try{ClearOutline();var session=_session;if(session is null)return;await session.StopAsync();_operationError=null;}
        finally{Actions.Release();}
    }
    private static void ClearOutline(){_outline?.Dispose();_outline=null;}
    private static void ShowOutline(Rectangle region)
    {
        if(_outline is not null&&_outlinedRegion==region)return;
        ClearOutline();
        _outline=new SubtitleRegionOutline(region);
        _outlinedRegion=region;
    }
    private static async Task<SubtitlePixels> CaptureAsync(Rectangle rect,CancellationToken ct)
    {
        return await Task.Run(()=>
        {
            if(!DesktopContains(rect))throw new InvalidOperationException("字幕框已超出当前桌面，请调整后继续。");
            using var frame=GdiCaptureFrame.Capture(rect.X,rect.Y,rect.Width,rect.Height,ct);return new SubtitlePixels(frame.Pixels,frame.Width,frame.Height);
        },ct).ConfigureAwait(false);
    }
    private static async Task<SubtitleRecognition?> RecognizeAsync(SubtitlePixels pixels,CancellationToken ct)
    {
        int fallbacks=OcrHelper.FallbackCount;
        var lines=await OcrHelper.RecognizeBackgroundAsync(pixels.Pixels,pixels.Width,pixels.Height,ct).ConfigureAwait(false);
        if(lines is null)return null;
        string engine=OcrHelper.FallbackCount>fallbacks?"Windows.Media.Ocr / CPU":
            (OcrHelper.LoadedModel=="small"?"Small":"Tiny")+" / "+(OcrHelper.LoadedBackend==Sdcb.SimdPaddleOCR.OcrBackend.Cpu?"CPU":"自动后端");
        return new(lines.Where(l=>l.RecognitionScore is null or >=.45f).OrderBy(l=>l.Rect.Top).ThenBy(l=>l.Rect.Left).Select(l=>l.Text).ToArray(),engine);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern int GetSystemMetrics(int index);
    internal static bool DesktopContains(Rectangle r)=>r.Width>0&&r.Height>0&&new Rectangle(GetSystemMetrics(76),GetSystemMetrics(77),GetSystemMetrics(78),GetSystemMetrics(79)).Contains(r);
}
