using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Starshot.Helpers;

namespace Starshot.Features.DocumentOcr;

/// <summary>Owns only results and a running task. The source buffer is retained only until the task finishes.</summary>
internal sealed class PageRecognitionJob : IDisposable
{
    private readonly object _sync=new();
    private int _disposed;
    private readonly CancellationTokenSource _cancel;
    private readonly CancellationToken _ownerLifetime;
    private CancellationTokenSource? _retry;
    private DocumentImage? _image;
    private readonly string _layoutModel;
    private readonly PageRecognitionResult _result;
    private readonly TaskCompletionSource<bool> _finished=new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Completion=>_finished.Task;
    internal string Id=>_result.Id;
    internal string? LayoutModel=>_result.LayoutModel;
    internal PageRecognitionJob(DocumentImage image,bool formulaOnly,CancellationToken lifetime)
    {
        image.Validate();_ownerLifetime=lifetime;_cancel=CancellationTokenSource.CreateLinkedTokenSource(lifetime);_result=new(Guid.NewGuid().ToString("N"),image.Width,image.Height);
        _layoutModel=DocumentModelStore.SelectedLayout;_result.LayoutModel=formulaOnly?null:_layoutModel;
        _image=image;_formulaOnly=formulaOnly;_ = Task.Run(async()=>{try{await RunAsync();}finally{_finished.TrySetResult(true);}});
    }
    internal PageRecognitionResult Snapshot(){lock(_sync)return _result with{TimingsMs=new(_result.TimingsMs),Regions=_result.Regions.Select(r=>r with{Table=r.Table is{} t?t with{Cells=[..t.Cells]}:null}).ToList()};}
    internal void Cancel(){lock(_sync){if(_disposed==0){_cancel.Cancel();_retry?.Cancel();}}}
    private readonly bool _formulaOnly;
    private async Task RunAsync()
    {
        DocumentImage image=_image!;bool formulaOnly=_formulaOnly;CancellationToken ct=_cancel.Token;
        var time=Stopwatch.StartNew();
        try
        {
            var needed=DocumentModelStore.Required(formulaOnly,_layoutModel);
            var missing=new System.Collections.Generic.List<string>();foreach(string id in needed)if(!(await DocumentModelStore.Current.StatusAsync(id,false,ct)).Installed)missing.Add(DocumentModelStore.Descriptors.First(d=>d.Id==id).Name);
            if(missing.Count>0)throw new InvalidOperationException("请先在模型管理库下载："+string.Join("、",missing));
            lock(_sync)_result.TimingsMs["prepare"]=time.ElapsedMilliseconds;var phase=Stopwatch.StartNew();
            System.Collections.Generic.List<LayoutRegion> regions=formulaOnly?[new LayoutRegion("formula",7,"formula",1,new(0,0,image.Width,image.Height)){Order=1}]:await DocumentInference.LayoutAsync(image,ct,_layoutModel);
            foreach(var child in regions.Where(r=>!r.IsTable&&!r.IsFormula&&!r.IsImage))
                child.ParentId=regions.Where(p=>(p.IsTable||p.IsFormula)&&p.Box.Area>child.Box.Area&&p.Box.Intersection(child.Box)/Math.Max(1,child.Box.Area)>.9f).OrderBy(p=>p.Box.Area).FirstOrDefault()?.Id;
            ct.ThrowIfCancellationRequested();lock(_sync){_result.Regions=regions;_result.Stage="识别正文";}
            lock(_sync)_result.TimingsMs["layout"]=phase.ElapsedMilliseconds;phase.Restart();
            // Stage by model family to avoid repeated reloads and concurrent model heaps.
            foreach(var r in regions.Where(r=>!r.IsFormula&&!r.IsTable))
            {
                if(r.IsImage||r.ParentId is not null){lock(_sync)r.State="done";continue;}
                await RegionAsync(r,async crop=>{var lines=await OcrHelper.RecognizeAsync(crop.Pixels,crop.Width,crop.Height,1,ct);lock(_sync)r.Text=string.Join("\n",(lines??[]).Select(l=>l.Text));},image,ct);
            }
            lock(_sync){_result.TimingsMs["text"]=phase.ElapsedMilliseconds;_result.Stage="识别公式";}phase.Restart();
            foreach(var r in regions.Where(r=>r.IsFormula))await RegionAsync(r,async crop=>{var f=await DocumentInference.FormulaAsync(crop,ct);lock(_sync){r.Formula=f;r.Text=f.Latex;}},image,ct);
            lock(_sync){_result.TimingsMs["formula"]=phase.ElapsedMilliseconds;_result.Stage="识别表格与单元格";}phase.Restart();
            foreach(var r in regions.Where(r=>r.IsTable))await RegionAsync(r,async crop=>
            {
                var table=await DocumentInference.TableAsync(crop,ct);var lines=await OcrHelper.RecognizeAsync(crop.Pixels,crop.Width,crop.Height,1,ct);
                table=TableStructure.Associate(table,(lines??[]).Select(l=>(l.Text,new PixelBox((float)l.Rect.X,(float)l.Rect.Y,(float)l.Rect.Width,(float)l.Rect.Height))));
                lock(_sync){r.Table=TableStructure.InDocument(table,r.Box);r.Text=table.Tsv();}
            },image,ct);
            lock(_sync)_result.TimingsMs["table"]=phase.ElapsedMilliseconds;
            ct.ThrowIfCancellationRequested();lock(_sync){_result.State=regions.Any(r=>r.State=="error")?"partial":"done";_result.Stage=regions.Count==0?"没有发现版面区域":_result.State=="partial"?"部分区域识别失败，可单独重试":"提取完成";}
        }
        catch(OperationCanceledException){lock(_sync){_result.State="canceled";_result.Stage="已取消";}}
        catch(Exception ex){lock(_sync){_result.State="error";_result.Error=ex.Message;_result.Stage="未能完成识别";}}
        finally{_image=null;image=null!;lock(_sync){_result.ElapsedMs=time.ElapsedMilliseconds;Serilog.Log.Information("[DocumentOCR] mode={Mode} regions={Regions} state={State} elapsed={Elapsed} ms",formulaOnly?"formula":"page",_result.Regions.Count,_result.State,_result.ElapsedMs);}}
    }
    private async Task RegionAsync(LayoutRegion region,Func<DocumentImage,Task> recognize,DocumentImage image,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();lock(_sync)region.State="running";
        try{await recognize(image.Crop(region.Box));ct.ThrowIfCancellationRequested();lock(_sync)region.State="done";}
        catch(OperationCanceledException){lock(_sync)region.State="canceled";throw;}
        catch(Exception ex){lock(_sync){region.State="error";region.Error=ex.Message;}}
    }
    internal async Task RetryAsync(string id,DocumentImage image,CancellationToken ct)
    {
        if(!Completion.IsCompleted)throw new InvalidOperationException("请等待整页任务完成后再重试区域。");
        using var retry=CancellationTokenSource.CreateLinkedTokenSource(ct,_ownerLifetime);
        lock(_sync){ObjectDisposedException.ThrowIf(_disposed!=0,this);if(_retry is not null)throw new InvalidOperationException("已有区域正在识别。");_retry=retry;}
        ct=retry.Token;
        try{
        var r=_result.Regions.Single(r=>r.Id==id);lock(_sync)r.Error=null;
        if(r.IsFormula)await RegionAsync(r,async crop=>{var f=await DocumentInference.FormulaAsync(crop,ct);lock(_sync){r.Formula=f;r.Text=f.Latex;}},image,ct);
        else if(r.IsTable)await RegionAsync(r,async crop=>{var t=await DocumentInference.TableAsync(crop,ct);var lines=await OcrHelper.RecognizeAsync(crop.Pixels,crop.Width,crop.Height,1,ct);lock(_sync)r.Table=TableStructure.InDocument(TableStructure.Associate(t,(lines??[]).Select(l=>(l.Text,new PixelBox((float)l.Rect.X,(float)l.Rect.Y,(float)l.Rect.Width,(float)l.Rect.Height)))),r.Box);},image,ct);
        else await RegionAsync(r,async crop=>{var lines=await OcrHelper.RecognizeAsync(crop.Pixels,crop.Width,crop.Height,1,ct);lock(_sync)r.Text=string.Join("\n",(lines??[]).Select(l=>l.Text));},image,ct);
        }finally{image=null!;lock(_sync)_retry=null;}
    }
    internal void Edit(string id,string text,string[]? cells)
    {
        lock(_sync)
        {
            if(!Completion.IsCompleted||_retry is not null)throw new InvalidOperationException("请等待提取结束后编辑。");var r=_result.Regions.Single(r=>r.Id==id);
            if(r.Table is{} t){if(cells is null||cells.Length!=t.Cells.Count)throw new ArgumentException("单元格数量不符。");r.Table=t with{Cells=t.Cells.Select((c,i)=>c with{Text=cells[i]}).ToList()};}
            else if(r.Formula is{} f){r.Formula=f with{Latex=text};r.Text=text;}
            else r.Text=text;
        }
    }
    public void Dispose(){lock(_sync){if(Interlocked.Exchange(ref _disposed,1)!=0)return;_cancel.Cancel();_retry?.Cancel();}_ = Completion.ContinueWith(_=>_cancel.Dispose(),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);}
}
