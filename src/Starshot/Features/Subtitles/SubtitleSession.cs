using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Starshot.Features.Subtitles;

internal sealed record SubtitleRecognition(string[] Lines,string Engine);
internal sealed record SubtitleStatus(string State,string? Error,Rectangle Region,int Count,int Revision,
    SubtitleCue[] Recent,string Engine,double OcrMs,double CycleMs,int Captures,int Recognitions,int Skipped,
    bool Unsaved,string Path,int IntervalMs);

/// <summary>Single bounded worker. No HWND, GPU resources, or wall-clock timestamps.</summary>
internal sealed class SubtitleSession
{
    private readonly object _sync=new();
    private readonly SubtitleCollector _collector=new();
    private readonly Func<Rectangle,CancellationToken,Task<SubtitlePixels>> _capture;
    private readonly Func<SubtitlePixels,CancellationToken,Task<SubtitleRecognition?>> _recognize;
    private readonly Func<string,CancellationToken,Task> _save;
    private CancellationTokenSource? _cancel;
    private Task? _loop,_stop;
    private Rectangle _region;
    private long _generation;
    private string _state="editing",_engine="按需加载",_captureError="",_saveError="";
    private int _savedRevision,_captures,_recognitions,_skipped,_interval=350;
    private double _ocrMs,_cycleMs;
    internal string Path {get;}
    internal SubtitleSession(string path,Func<Rectangle,CancellationToken,Task<SubtitlePixels>> capture,
        Func<SubtitlePixels,CancellationToken,Task<SubtitleRecognition?>> recognize,Func<string,CancellationToken,Task>? save=null)
    {Path=path;_capture=capture;_recognize=recognize;_save=save??SaveFileAsync;}
    internal void SetRegion(Rectangle region)
    {lock(_sync){if(_region==region)return;_region=region;_generation++;_collector.EndCaption();_state="editing";}}
    internal void Pause(bool editing=false)
    {lock(_sync){_generation++;_state=editing?"editing":"paused";_collector.EndCaption();}}
    internal void SetInterval(int milliseconds)
    {if(milliseconds is not(200 or 350 or 600))throw new ArgumentException("未知采样间隔。");lock(_sync)_interval=milliseconds;}
    internal void Start()
    {
        lock(_sync)
        {
            if(_stop is{IsCompleted:false})throw new InvalidOperationException("正在结束上一轮识别，请稍候。");
            if(_region.Width<80||_region.Height<16||(long)_region.Width*_region.Height>4_194_304)throw new InvalidOperationException("请将字幕框调整为有效的小区域（至少 80×16，最多约 400 万像素）。");
            _captureError="";_generation++;_state="running";
            if(_loop is null||_loop.IsCompleted){_cancel?.Dispose();_cancel=new();var token=_cancel.Token;_stop=null;_loop=Task.Run(()=>RunAsync(token));}
        }
    }
    internal Task StopAsync()
    {
        lock(_sync)
        {
            if(_stop is not null)
            {
                if(!_stop.IsCompleted||_savedRevision>=_collector.Revision)return _stop;
                return _stop=FinishAsync(null,null); // Retry a failed final save without discarding the transcript.
            }
            _state="stopping";_generation++;_collector.EndCaption();_cancel?.Cancel();
            return _stop=FinishAsync(_loop,_cancel);
        }
    }
    private async Task FinishAsync(Task? loop,CancellationTokenSource? cancel)
    {
        try
        {if(loop is not null)await loop.ConfigureAwait(false);else await PersistAsync().ConfigureAwait(false);}
        finally{cancel?.Dispose();lock(_sync)_state="stopped";}
    }
    internal SubtitleStatus Snapshot()
    {
        lock(_sync){var transcript=_collector.Snapshot(false);return new(_state,(_captureError+(_captureError.Length>0&&_saveError.Length>0?"；":"")+_saveError) is{Length:>0} error?error:null,_region,transcript.Count,transcript.Revision,transcript.Recent,_engine,_ocrMs,_cycleMs,_captures,_recognitions,_skipped,_savedRevision<transcript.Revision,Path,_interval);}
    }
    internal string Text(){lock(_sync){_collector.Flush();return _collector.Snapshot().Text;}}
    private async Task RunAsync(CancellationToken ct)
    {
        float[]? signature=null;string[] cached=[];long cachedGeneration=-1,lastRecognition=-2000;var elapsed=Stopwatch.StartNew();
        try
        {
            while(!ct.IsCancellationRequested)
            {
                long generation;Rectangle region;bool running;int interval;
                lock(_sync){generation=_generation;region=_region;running=_state=="running";interval=_interval;}
                var cycle=Stopwatch.StartNew();
                try
                {
                    if(running)
                    {
                        var pixels=await _capture(region,ct).ConfigureAwait(false);ct.ThrowIfCancellationRequested();
                        var current=pixels.Signature();lock(_sync)_captures++;
                        if(generation==cachedGeneration&&elapsed.ElapsedMilliseconds-lastRecognition<2000&&!SubtitlePixels.Changed(signature,current))
                        {lock(_sync){if(generation==_generation&&_state=="running"){_skipped++;_collector.Observe(cached,elapsed.ElapsedMilliseconds);}}}
                        else
                        {
                            var watch=Stopwatch.StartNew();var result=await _recognize(pixels,ct).ConfigureAwait(false);ct.ThrowIfCancellationRequested();
                            if(result is not null)
                            {
                                lock(_sync)if(generation==_generation&&_state=="running")
                                {_ocrMs=watch.Elapsed.TotalMilliseconds;_recognitions++;_engine=result.Engine;_captureError="";_collector.Observe(result.Lines,elapsed.ElapsedMilliseconds);signature=current;cached=result.Lines;cachedGeneration=generation;lastRecognition=elapsed.ElapsedMilliseconds;}
                            }
                        }
                        pixels=null!;
                    }
                    await PersistAsync().ConfigureAwait(false);
                }
                catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
                catch(Exception ex)
                {lock(_sync){if(generation==_generation){_captureError=ex.Message;_state="paused";_generation++;}}}
                lock(_sync)_cycleMs=cycle.Elapsed.TotalMilliseconds;
                await Task.Delay(Math.Max(10,interval-(int)cycle.ElapsedMilliseconds),ct).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){}
        finally
        {
            signature=null;cached=[];
            lock(_sync)_collector.Flush();
            await PersistAsync().ConfigureAwait(false);
        }
    }
    private async Task PersistAsync()
    {
        SubtitleTranscript snapshot;lock(_sync){if(_collector.Revision<=_savedRevision)return;snapshot=_collector.Snapshot();}
        try
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));await _save(snapshot.Text,deadline.Token).ConfigureAwait(false);
            lock(_sync){_savedRevision=Math.Max(_savedRevision,snapshot.Revision);_saveError="";}
        }
        catch(Exception ex){lock(_sync)_saveError="文本尚未写入文件，文案仍可复制或导出；运行期间自动重试，新建任务前也会重试："+ex.Message;}
    }
    private async Task SaveFileAsync(string text,CancellationToken ct)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);string temporary=Path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{await File.WriteAllTextAsync(temporary,text,new System.Text.UTF8Encoding(false),ct).ConfigureAwait(false);ct.ThrowIfCancellationRequested();File.Move(temporary,Path,true);}
        finally{File.Delete(temporary);}
    }
}
