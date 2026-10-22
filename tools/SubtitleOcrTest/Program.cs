using System.Drawing;
using Starshot.Features.Subtitles;
int checks=0;void Check(bool value,string name){if(!value)throw new Exception(name);checks++;}
var c=new SubtitleCollector();
void See(SubtitleCollector collector,string[] lines,long ms){collector.Observe(lines,ms);collector.Observe(lines,ms+200);}
See(c,["Yes."],0);c.Observe([],400);c.Observe([],800);See(c,["Yes."],1000);Check(c.Snapshot().Count==2,"Separate repeated dialogue is preserved");
var words=new SubtitleCollector();See(words,["now here"],0);See(words,["nowhere"],1000);Check(words.Snapshot().Count==2,"English word boundaries preserved");
var progressive=new SubtitleCollector();See(progressive,["我们要去"],0);See(progressive,["我们要去北京"],400);Check(progressive.Snapshot().Text=="我们要去北京","Progressive caption replaces its prefix");
var wrap=new SubtitleCollector();See(wrap,["Here is a sentence"],0);See(wrap,["Here is a","sentence"],500);Check(wrap.Snapshot().Count==1,"English rewrap does not duplicate");
var chinese=new SubtitleCollector();See(chinese,["这是完整的一句话"],0);See(chinese,["这是完整的","一句话"],500);Check(chinese.Snapshot().Count==1,"Chinese rewrap does not duplicate");
var rolling=new SubtitleCollector();See(rolling,["Line A","Line B"],0);See(rolling,["Line B","Line C"],500);Check(rolling.Snapshot().Text=="Line A"+Environment.NewLine+"Line B"+Environment.NewLine+"Line C","Rolling line overlap");
See(rolling,["Line B Line C grows"],1000);Check(rolling.Snapshot().Text=="Line A"+Environment.NewLine+"Line B"+Environment.NewLine+"Line C grows","Rewrapped progressive rolling text does not duplicate its prefix");
var shortCaption=new SubtitleCollector();shortCaption.Observe(["短句"],0);shortCaption.Observe(["下一句"],250);shortCaption.Flush();Check(shortCaption.Snapshot().Count==2,"One-sample short captions survive transitions and stop");
Check(!shortCaption.Snapshot().Text.Contains('['),"No timestamps");
var whites=new SubtitleCollector();See(whites,["ＡＢＣ １２３"],0);See(whites,["ABC 123"],700);Check(whites.Snapshot().Count==1,"NFKC compatibility");
SubtitlePixels Frame(int value=0){var bytes=new byte[160*40*4];Array.Fill(bytes,(byte)value);for(int i=3;i<bytes.Length;i+=4)bytes[i]=255;return new(bytes,160,40);}
var baseline=Frame().Signature();Check(!SubtitlePixels.Changed(baseline,Frame().Signature()),"Static frame unchanged");var thin=Frame();for(int y=0;y<thin.Height;y++){int i=(y*thin.Width+3)*4;thin.Pixels[i]=thin.Pixels[i+1]=thin.Pixels[i+2]=255;}Check(SubtitlePixels.Changed(baseline,thin.Signature()),"One-pixel odd-column glyph changes detected");
string root=Path.Combine(Path.GetTempPath(),"starshot-subtitles-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
async Task Until(Func<bool> condition){using var limit=new CancellationTokenSource(10000);while(!condition())await Task.Delay(15,limit.Token);}
try
{
    int recognizes=0,writes=0;bool failWrite=true;
    var session=new SubtitleSession(Path.Combine(root,"copy.txt"),(_,_)=>Task.FromResult(Frame()),(_,_)=>{Interlocked.Increment(ref recognizes);return Task.FromResult<SubtitleRecognition?>(new(["Static caption"],"Tiny / CPU"));},(text,ct)=>{Interlocked.Increment(ref writes);if(failWrite)throw new IOException("locked");return File.WriteAllTextAsync(Path.Combine(root,"copy.txt"),text,ct);});
    session.SetInterval(200);session.SetRegion(new(0,0,160,40));session.Start();
    await Until(()=>session.Snapshot().Unsaved&&session.Snapshot().Error is not null);Check(session.Text()=="Static caption","Failed save retains copy text");
    failWrite=false;await Until(()=>!session.Snapshot().Unsaved);Check(File.ReadAllText(Path.Combine(root,"copy.txt"))=="Static caption","Unchanged caption retries failed writes");
    Check(writes>=2,"Persistence retry actually ran");Check(session.Snapshot().Skipped>0,"Unchanged frames bypass OCR");Check(recognizes<=2,"Static OCR is bounded");
    session.Pause();int before=recognizes;await Task.Delay(430);Check(recognizes==before,"Pause stops inference");
    session.Start();await Until(()=>recognizes>before);await session.StopAsync();Check(session.Text()=="Static caption"+Environment.NewLine+"Static caption","Stopped session retains transcript and resumes as a new episode");
    Check(session.Snapshot().State=="stopped","Stop joins worker");Check(!Directory.EnumerateFiles(root,"*.tmp").Any(),"No incomplete saves");
    int calls=0;var arrived=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var generation=new SubtitleSession(Path.Combine(root,"generation.txt"),(_,_)=>Task.FromResult(Frame()),async(_,ct)=>{if(Interlocked.Increment(ref calls)==1){arrived.SetResult();await release.Task;}return new([calls==1?"OLD REGION":"NEW REGION"],"fake");});
    generation.SetInterval(200);generation.SetRegion(new(0,0,160,40));generation.Start();await arrived.Task;
    generation.Pause(true);generation.SetRegion(new(200,0,160,40));generation.Start();release.SetResult();await Until(()=>generation.Text().Contains("NEW REGION"));await generation.StopAsync();
    Check(!generation.Text().Contains("OLD REGION"),"Late result after adjust/resume discarded");Check(File.ReadAllText(generation.Path)==generation.Text(),"Final stop persists latest revision");
    var cancelEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);bool canceled=false;
    var cancellation=new SubtitleSession(Path.Combine(root,"cancel.txt"),(_,_)=>Task.FromResult(Frame()),async(_,ct)=>{cancelEntered.SetResult();try{await Task.Delay(Timeout.Infinite,ct);}catch(OperationCanceledException){canceled=true;throw;}return null;});
    cancellation.SetRegion(new(0,0,160,40));cancellation.Start();await cancelEntered.Task;await cancellation.StopAsync();Check(canceled&&cancellation.Snapshot().State=="stopped","Cancel joins executing OCR before disposal");
    var broken=new SubtitleSession(Path.Combine(root,"broken.txt"),(_,_)=>throw new IOException("capture denied"),(_,_)=>Task.FromResult<SubtitleRecognition?>(null));broken.SetRegion(new(0,0,160,40));broken.Start();await Until(()=>broken.Snapshot().State=="paused");Check(broken.Snapshot().Error!.Contains("capture denied"),"Capture failure visible");await broken.StopAsync();
    var atomic=new SubtitleSession(Path.Combine(root,"atomic.txt"),(_,_)=>Task.FromResult(Frame()),(_,_)=>Task.FromResult<SubtitleRecognition?>(new(["中文 English"],"fake")));atomic.SetRegion(new(0,0,160,40));atomic.Start();await Until(()=>atomic.Snapshot().Count>0);await atomic.StopAsync();
    Check(File.ReadAllText(atomic.Path)=="中文 English","UTF8 plain output without headers or timestamps");Check(!Directory.EnumerateFiles(root,"*.tmp").Any(),"Atomic save temp cleanup");
    for(int i=0;i<5;i++){atomic.Start();await Task.Delay(20);await atomic.StopAsync();}Check(atomic.Snapshot().State=="stopped","Repeated start/stop finishes");
    bool finalFail=true;var retryStop=new SubtitleSession(Path.Combine(root,"stop-retry.txt"),(_,_)=>Task.FromResult(Frame()),(_,_)=>Task.FromResult<SubtitleRecognition?>(new(["Keep after stop"],"fake")),(text,ct)=>finalFail?Task.FromException(new IOException("locked on exit")):File.WriteAllTextAsync(Path.Combine(root,"stop-retry.txt"),text,ct));
    retryStop.SetRegion(new(0,0,160,40));retryStop.Start();await Until(()=>retryStop.Snapshot().Count>0);await retryStop.StopAsync();Check(retryStop.Snapshot().Unsaved,"Failed final save remains visible");finalFail=false;await retryStop.StopAsync();Check(!retryStop.Snapshot().Unsaved&&File.ReadAllText(retryStop.Path)=="Keep after stop","Stopped failed save can be retried before a new task");
    checks+=await WorkflowChecks.RunAsync(root);
    Console.WriteLine($"PASS {checks} subtitle collector/lifecycle/persistence/generation/region-controller checks; synthetic boundaries, no desktop capture, no models, no persistent images.");
}
finally{Directory.Delete(root,true);}
