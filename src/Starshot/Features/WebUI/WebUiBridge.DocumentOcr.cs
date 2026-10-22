using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Starshot.Features.DocumentOcr;
using Starshot.Helpers;

namespace Starshot.Features.WebUI;

internal sealed partial class WebUiBridge
{
    private PageRecognitionJob? _pageJob;
    private PageRecognitionJob PageJob(JsonElement p)
    {var job=_pageJob;if(job is null||Text(p,"jobId",80)!=job.Id)throw new InvalidOperationException("此识别任务已关闭或图像已更换。");return job;}
    private void ClosePageJob(){var old=_pageJob;_pageJob=null;old?.Dispose();}
    private async Task<object?> DocumentOcrAsync(string method,JsonElement p,CancellationToken ct)
    {
        // These paths must never queue behind inference or a workspace mutation.
        if(method=="workspace.page.status")return PageJob(p).Snapshot();
        if(method=="workspace.page.cancel"){var job=PageJob(p);job.Cancel();return job.Snapshot();}
        if(method=="workspace.formula.copyWord"){FormulaClipboard.Copy(_window.WindowHandle,Text(p,"mathml",500_000),Text(p,"latex",100_000));return null;}
        if(method=="workspace.page.models")
        {string layout=_pageJob?.LayoutModel??DocumentModelStore.SelectedLayout;return new{layoutModel=layout,items=await Task.WhenAll(DocumentModelStore.Required(false,layout).Select(id=>DocumentModelStore.Current.StatusAsync(id,false,ct)))};}
        await _workspaceGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();if(_workspaceClosed)throw new ObjectDisposedException(nameof(WebUiBridge));var d=Document;
            if(method is "workspace.formula" or "workspace.layout")
            {
                ClosePageJob();_pageJob=new(new DocumentImage(d.Bgra,d.Width,d.Height),method=="workspace.formula",_lifetime.Token);return new{jobId=_pageJob.Id};
            }
            if(method=="workspace.page.retry"){var job=PageJob(p);await job.RetryAsync(Text(p,"regionId",80),new(d.Bgra,d.Width,d.Height),ct);return job.Snapshot();}
            if(method=="workspace.page.edit")
            {
                string text=p.TryGetProperty("text",out _)?Text(p,"text",100_000):"";string[]? cells=null;
                if(p.TryGetProperty("cells",out var values)){if(values.GetArrayLength()>10000)throw new ArgumentException("单元格过多。");cells=values.EnumerateArray().Select(v=>v.GetString()??"").ToArray();if(cells.Sum(v=>v.Length)>500_000)throw new ArgumentException("表格文字过多。");}
                var job=PageJob(p);job.Edit(Text(p,"regionId",80),text,cells);return job.Snapshot();
            }
            if(method=="workspace.page.crop")
            {
                var region=PageJob(p).Snapshot().Regions.Single(r=>r.Id==Text(p,"regionId",80));var crop=new DocumentImage(d.Bgra,d.Width,d.Height).Crop(region.Box);
                return new{src=await Workspace.WorkspaceDocument.PngAsync(crop.Pixels,crop.Width,crop.Height,1024,ct)};
            }
            if(method=="workspace.page.export")
            {
                var snapshot=PageJob(p).Snapshot();if(snapshot.State=="running")throw new InvalidOperationException("请先等待提取完成或取消任务。");
                string format=Text(p,"format",12);TableResult? table=null;
                if(format is "xlsx" or "html" or "tsv")table=snapshot.Regions.Single(r=>r.Id==Text(p,"regionId",80)).Table??throw new InvalidOperationException("没有可导出的表格。");
                string extension=format switch{"xlsx"=>".xlsx","html"=>".html","tsv"=>".tsv","markdown"=>".md",_=>throw new ArgumentException("不支持的导出格式。")};
                string? path=await FileDialogHelper.OpenSaveFileDialogAsync(_window.WindowHandle,Path.GetFileNameWithoutExtension(d.Name)+"-recognized",("识别结果",extension));if(path is null)return null;
                string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                try
                {
                    if(format=="xlsx")await Task.Run(()=>DocumentExport.Excel(temporary,table!),ct);
                    else await File.WriteAllTextAsync(temporary,format=="markdown"?DocumentExport.Markdown(snapshot):format=="html"?"<!doctype html><meta charset=\"utf-8\">"+table!.Html():table!.Tsv(),ct);
                    ct.ThrowIfCancellationRequested();File.Move(temporary,path,true);
                }
                finally{File.Delete(temporary);}
                return new{saved=true};
            }
            throw new ArgumentException("未知文档识别操作。");
        }
        finally{_workspaceGate.Release();}
    }
}
