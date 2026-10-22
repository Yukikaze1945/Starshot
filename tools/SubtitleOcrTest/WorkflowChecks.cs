using System.Drawing;
using System.Text.Json;
using Starshot.Features.Subtitles;
using Starshot.Features.Screenshot;
using Starshot.Features.ViewHost;

internal static class WorkflowChecks
{
    internal static async Task<int> RunAsync(string root)
    {
        int checks=0;Starshot.AppConfig.UserDataFolder=root;
        void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;}
        JsonElement Status()=>JsonSerializer.SerializeToElement(SubtitleController.Snapshot()).GetProperty("status");
        async Task Until(Func<bool> done){using var timeout=new CancellationTokenSource(6000);while(!done())await Task.Delay(15,timeout.Token);}
        try
        {
            Check(SubtitleSelectionRegion.ToDesktop(new(300,80,640,120),-1920,-200)==new Rectangle(-1620,-120,640,120),"Physical multi-monitor ROI adds virtual origin once, without DPI scaling");
            bool invalid=false;try{SubtitleSelectionRegion.ToDesktop(new(0,0,20,10),0,0);}catch(InvalidOperationException){invalid=true;}Check(invalid,"Invalid subtitle ROI rejected");
            await SubtitleController.UseSelectionAsync(new(0,0,160,40));
            await Until(()=>Status().GetProperty("Count").GetInt32()>0);
            Check(Status().GetProperty("State").GetString()=="running","Existing region selection begins sampling immediately");
            Check(MainWindow.Anchor==new Rectangle(0,0,160,40),"Result panel receives selected rectangle, no independent selector");
            Check(ScreenCaptureService.SelectCount==0,"Toolbar handoff does not ask for a second selection");
            Check(SubtitleRegionOutline.Live==1&&SubtitleRegionOutline.Region==MainWindow.Anchor,"Accepted ROI remains visible during recognition");
            Check(MainWindow.SubtitlePanelTopmost,"Subtitle window is pinned by default");
            MainWindow.SetSubtitlePanelTopmost(false);Check(!JsonSerializer.SerializeToElement(SubtitleController.Snapshot()).GetProperty("topmost").GetBoolean(),"Unpin state is exposed to the UI");
            MainWindow.SetSubtitlePanelTopmost(true);
            SubtitleController.Pause();await Task.Delay(60);int captures=GdiCaptureFrame.Captures;await Task.Delay(400);Check(captures==GdiCaptureFrame.Captures,"Pause stops sampling");
            string before=SubtitleController.Text();ScreenCaptureService.Selector=_=>Task.FromResult<Rectangle?>(null);await SubtitleController.ConfigureAsync(true);
            Check(SubtitleController.Text()==before&&Status().GetProperty("Region").GetProperty("Width").GetInt32()==160,"Cancel new region retains prior transcript and ROI");
            Check(SubtitleRegionOutline.Live==1&&Status().GetProperty("State").GetString()=="paused","Cancelled reselect restores old outline without resuming OCR");
            ScreenCaptureService.Selector=_=>Task.FromResult<Rectangle?>(new(180,90,160,40));await SubtitleController.ConfigureAsync();
            Check(Status().GetProperty("State").GetString()=="running"&&MainWindow.Anchor==new Rectangle(180,90,160,40),"Reselect uses existing overlay then resumes automatically");
            Check(SubtitleController.Text().Contains(before),"Reselect does not silently clear transcript");
            Check(SubtitleRegionOutline.Live==1&&SubtitleRegionOutline.Region==new Rectangle(180,90,160,40),"Reselect replaces rather than accumulates outline windows");
            ScreenCaptureService.Selector=ct=>{var done=new TaskCompletionSource<Rectangle?>(TaskCreationOptions.RunContinuationsAsynchronously);ct.Register(()=>done.TrySetResult(null));return done.Task;};
            var pending=SubtitleController.ConfigureAsync();Check(JsonSerializer.SerializeToElement(SubtitleController.Snapshot()).GetProperty("selecting").GetBoolean(),"Selecting state visible");
            Check(SubtitleRegionOutline.Live==0,"Old marker is removed before existing screenshot selector opens");
            bool startRejected=false;try{SubtitleController.Start();}catch(InvalidOperationException){startRejected=true;}Check(startRejected,"Cannot resume while a region selection is pending");
            await SubtitleController.StopAsync();await pending;Check(Status().GetProperty("State").GetString()=="stopped","Stop cancels pending region selection and joins worker");
            Check(SubtitleRegionOutline.Live==0,"Cancelled late selector cannot restore outline after stop");
            SubtitleController.Start();await Until(()=>Status().GetProperty("State").GetString()=="running");SubtitleController.PanelClosed();await Until(()=>Status().GetProperty("State").GetString()=="stopped");
            Check(SubtitleController.Text().Length>0,"Closing result panel stops recording while preserving text");
            Check(SubtitleRegionOutline.Live==0,"Closing panel removes the live ROI marker");
            await SubtitleController.UseSelectionAsync(new(0,0,160,40));await SubtitleController.ShutdownAsync();
            Check(Status().GetProperty("State").GetString()=="stopped","Exit joins subtitle worker");
            Check(SubtitleRegionOutline.Live==0,"Exit disposes all outline windows");
            bool shutdownRejected=false;try{await SubtitleController.UseSelectionAsync(new(0,0,160,40));}catch(InvalidOperationException){shutdownRejected=true;}Check(shutdownRejected,"Late selection cannot restart after exit");
            return checks;
        }
        finally{await SubtitleController.StopAsync();}
    }
}

// Narrow synthetic boundaries: the production controller/session are linked;
// no test reads pixels from the desktop or opens a real window/model.
namespace Starshot {internal static class AppConfig{internal static string UserDataFolder="";}}
namespace Starshot.Features.Screenshot
{
    internal static class ScreenCaptureService
    {
        internal static int SelectCount;
        internal static Func<CancellationToken,Task<Rectangle?>> Selector=_=>Task.FromResult<Rectangle?>(null);
        internal static Task<Rectangle?> SelectSubtitleRegionAsync(CancellationToken ct){SelectCount++;return Selector(ct);}
    }
    internal sealed class GdiCaptureFrame:IDisposable
    {
        internal static int Captures;internal byte[] Pixels=[];internal int Width,Height;
        internal static GdiCaptureFrame Capture(int x,int y,int w,int h,CancellationToken ct){ct.ThrowIfCancellationRequested();Interlocked.Increment(ref Captures);return new(){Width=w,Height=h,Pixels=new byte[w*h*4]};}
        public void Dispose(){Pixels=[];}
    }
}
namespace Starshot.Features.ViewHost
{
    internal static class MainWindow
    {
        internal static Rectangle? Anchor;internal static int Opens,Hides;
        internal static bool SubtitlePanelTopmost=true;
        internal static void SetSubtitlePanelTopmost(bool value)=>SubtitlePanelTopmost=value;
        internal static void OpenSubtitlePanel(Rectangle? anchor=null){Opens++;if(anchor.HasValue)Anchor=anchor;}
        internal static void HideSubtitlePanel(){Hides++;}
    }
}
namespace Starshot.Features.Subtitles
{
    internal sealed class SubtitleRegionOutline:IDisposable
    {
        internal static int Live;internal static Rectangle Region;private bool _disposed;
        internal SubtitleRegionOutline(Rectangle region){Region=region;Live++;}
        public void Dispose(){if(_disposed)return;_disposed=true;Live--;}
    }
}
namespace Sdcb.SimdPaddleOCR {internal enum OcrBackend{Cpu,Auto}}
namespace Starshot.Helpers
{
    internal sealed record OcrLine(string Text,RectangleF Rect,float? RecognitionScore=null);
    internal static class OcrHelper
    {
        internal static int FallbackCount=>0;internal static string LoadedModel=>"tiny";
        internal static Sdcb.SimdPaddleOCR.OcrBackend LoadedBackend=>Sdcb.SimdPaddleOCR.OcrBackend.Cpu;
        internal static Task<List<OcrLine>?> RecognizeBackgroundAsync(byte[] p,int w,int h,CancellationToken ct)=>Task.FromResult<List<OcrLine>?>([new("字幕 English",new(0,0,100,20),.99f)]);
    }
}
namespace Serilog {internal static class Log{internal static void Warning(Exception ex,string message)=>Console.Error.WriteLine(message+ex.Message);}}
