using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Starshot.Features.DocumentOcr;

namespace Starshot.Helpers;

/// <summary>Immutable multi-file revisions, published with a single atomic pointer.</summary>
internal sealed class DocumentModelStore
{
    internal sealed record Asset(string File,long Bytes,string Sha256,string Url);
    internal sealed record Package(string Id,string Name,string License,string Source,Asset[] Assets);
    internal static readonly string[] LayoutIds=["layout-doclayout-s","layout-doclayout-m","layout-doclayout-l"];
    internal static readonly string[] Ids=["formula-rapidlatex","layout-doclayout-s","table-slanet-plus","layout-doclayout-m","layout-doclayout-l"];
    internal static string SelectedLayout
    {get{string value=AppConfig.GetValue("layout-doclayout-s","DocumentLayoutModel")!;return LayoutIds.Contains(value)?value:LayoutIds[0];}}
    internal static string[] Required(bool formulaOnly,string layout)=>formulaOnly?["formula-rapidlatex"]:["formula-rapidlatex",layout,"table-slanet-plus"];
    internal static async Task SelectLayoutAsync(string id,CancellationToken ct)
    {
        if(!LayoutIds.Contains(id))throw new ArgumentException("未知版面模型。");
        await Current.RequireAsync(id,ct);
        // Wait for any executing stage before releasing its session. Selection itself stays lazy.
        await DocumentInference.UnloadAsync(ct);
        AppConfig.SetValue(id,"DocumentLayoutModel");
    }
    internal static bool Contains(string id)=>Ids.Contains(id);
    internal static DlcModelStore.Model[] Descriptors=>[
        new(Ids[0],"RapidLaTeXOCR · 公式识别","formula",178952787,"","https://github.com/RapidAI/RapidLaTeXOCR/releases/tag/v0.0.0",Ids[0],"MIT / 上游模型声明","https://github.com/RapidAI/RapidLaTeXOCR"),
        new(Ids[1],"PP-DocLayout-S · 版面检测","layout",4917852,"","https://huggingface.co/stefanj0/PP-DocLayout-S-ONNX",Ids[1],"Apache-2.0","https://huggingface.co/stefanj0/PP-DocLayout-S-ONNX"),
        new(Ids[3],"PP-DocLayout-M · 版面检测","layout",23496395,"","https://huggingface.co/x3zvawq/paddleocr-js-onnx/resolve/51c2133b5a7ea27b795fa8c400fdbfbd5337dd6a/pp_doclayout_m/PP-DocLayout-M_infer.onnx",Ids[3],"Apache-2.0 · Paddle 模型 / 社区 ONNX 导出","https://huggingface.co/PaddlePaddle/PP-DocLayout-M"),
        new(Ids[4],"PP-DocLayout-L · 版面检测","layout",129398697,"","https://huggingface.co/x3zvawq/paddleocr-js-onnx/resolve/51c2133b5a7ea27b795fa8c400fdbfbd5337dd6a/pp_doclayout_l/PP-DocLayout-L_infer.onnx",Ids[4],"Apache-2.0 · Paddle 模型 / 社区 ONNX 导出","https://huggingface.co/PaddlePaddle/PP-DocLayout-L"),
        new(Ids[2],"SLANet_plus · 表格结构","table",7784173,"","https://huggingface.co/PaddlePaddle/SLANet_plus_onnx",Ids[2],"Apache-2.0","https://huggingface.co/PaddlePaddle/SLANet_plus_onnx")];
    internal static DocumentModelStore Current {get;}=new(()=>Path.Combine(AppConfig.UserDataFolder,"Models","Library"),
        ()=>JsonSerializer.Deserialize<Package[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"ThirdParty","DocumentOcr","models.json")))!);
    private readonly Func<string> _root;
    private readonly Lazy<Package[]> _packages;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string,Entry> _entries=new();
    private sealed class Entry {public readonly SemaphoreSlim Gate=new(1);public Task? Job;public CancellationTokenSource? Cancellation;public string State="notInstalled";public string? Error,Warning,Hash,Fingerprint,ActiveSource;public long Downloaded;}
    internal DocumentModelStore(Func<string> root,Func<Package[]> packages,HttpClient? http=null){_root=root;_packages=new(packages);_http=http??new HttpClient(new SocketsHttpHandler{ConnectTimeout=TimeSpan.FromSeconds(20)}){Timeout=Timeout.InfiniteTimeSpan};}
    private Package Get(string id)=>_packages.Value.Single(p=>p.Id==id);
    private Entry E(string id)=>_entries.GetOrAdd(id,_=>new());
    private string Root(string id)=>Path.Combine(_root(),id);
    internal string? ActiveDirectory(string id)
    {
        string pointer=Path.Combine(Root(id),"active.json");
        if(!File.Exists(pointer))return null;
        try{using var file=new FileStream(pointer,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);if(file.Length>128)return null;using var reader=new StreamReader(file);string name=JsonSerializer.Deserialize<string>(reader.ReadToEnd())!;return Guid.TryParseExact(name,"N",out _)?Path.Combine(Root(id),name):null;}
        catch(JsonException){return null;}catch(IOException){return null;}
    }
    private DlcModelStore.Status Snapshot(Package p,Entry e)=>new(p.Id,p.Name,p.Id=="formula-rapidlatex"?"formula":LayoutIds.Contains(p.Id)?"layout":"table", p.Assets.Sum(a=>a.Bytes),Interlocked.Read(ref e.Downloaded),e.State,e.Error,p.License,p.Source,e.State=="installed","逐文件见模型清单",e.Warning,e.Hash,ActiveDirectory(p.Id)??Root(p.Id),e.ActiveSource,ModelDownloadSources.For(p.Id,p.Assets[0].Url));
    internal async Task<DlcModelStore.Status> StatusAsync(string id,bool force,CancellationToken ct)
    {
        var p=Get(id);var e=E(id);if(!await e.Gate.WaitAsync(0,ct))return Snapshot(p,e);
        try
        {
            if(e.Job is{IsCompleted:false})return Snapshot(p,e);
            string? path=ActiveDirectory(id);
            if(path is null){if(e.State is not("error" or "canceled"))e.State="notInstalled";return Snapshot(p,e);}
            string fingerprint=string.Join("|",p.Assets.Select(a=>{var f=new FileInfo(Path.Combine(path,a.File));return f.Exists?$"{f.Length}:{f.LastWriteTimeUtc.Ticks}":"missing";}));
            if(!force&&e.Fingerprint==fingerprint&&e.State=="installed")return Snapshot(p,e);
            var report=await InspectAsync(p,path,ct);e.Warning=report.Warning;e.Hash=report.Hash;e.Error=report.Error;e.State=report.Error is null?"installed":"corrupt";e.Fingerprint=fingerprint;
            e.Downloaded=report.Error is null?p.Assets.Sum(a=>new FileInfo(Path.Combine(path,a.File)).Length):0;
            return Snapshot(p,e);
        }
        finally{e.Gate.Release();}
    }
    internal async Task<string> RequireAsync(string id,CancellationToken ct)
    {if(!(await StatusAsync(id,false,ct)).Installed)throw new InvalidOperationException("请在模型管理库下载或导入 "+Get(id).Name);return ActiveDirectory(id)!;}
    internal DlcModelStore.Status Start(string id,string source,string? import,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var p=Get(id);var e=E(id);
        lock(e)
        {
            if(e.Job is{IsCompleted:false})return Snapshot(p,e);
            e.Cancellation?.Dispose();e.Cancellation=new();e.State=import is null?"downloading":"importing";e.Error=e.Warning=null;e.Downloaded=0;
            e.Job=Task.Run(()=>InstallAsync(p,e,source,import,e.Cancellation.Token));return Snapshot(p,e);
        }
    }
    private async Task InstallAsync(Package p,Entry e,string source,string? import,CancellationToken ct)
    {
        string? stage=null,published=null;await e.Gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Root(p.Id));stage=Path.Combine(Root(p.Id),".stage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
            foreach(var asset in p.Assets)
            {
                ct.ThrowIfCancellationRequested();string destination=Path.Combine(stage,asset.File);
                if(import is not null)
                {
                    string file=Path.Combine(import,asset.File);if(!File.Exists(file)){string alias=asset.File switch{"layout.onnx"=>p.Id=="layout-doclayout-s"?"pp_doclayout_s.onnx":$"PP-DocLayout-{p.Id[^1..].ToUpperInvariant()}_infer.onnx","table.onnx"=>"inference.onnx","table.yml"=>"inference.yml",_=>asset.File};file=Path.Combine(import,alias);}if(!File.Exists(file))throw new InvalidDataException("模型包缺少 "+asset.File);
                    await using var input=File.OpenRead(file);await CopyAsync(input,destination,asset.Bytes,e,ct);
                }
                else
                {
                    var sources=ModelDownloadSources.Resolve(p.Id,asset.Url,source).ToArray();bool done=false;
                    foreach(var candidate in sources)
                    {
                        long start=e.Downloaded;
                        try
                        {
                            e.ActiveSource=candidate.Label;using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromMinutes(15));
                            using var response=await _http.GetAsync(candidate.Url,HttpCompletionOption.ResponseHeadersRead,deadline.Token);response.EnsureSuccessStatusCode();
                            await using var input=await response.Content.ReadAsStreamAsync(deadline.Token);await CopyAsync(input,destination,asset.Bytes,e,deadline.Token);done=true;break;
                        }
                        catch(Exception)when(!ct.IsCancellationRequested&&candidate!=sources[^1]){e.Downloaded=start;File.Delete(destination);}
                    }
                    if(!done)throw new IOException("没有可用模型下载来源。");
                }
            }
            e.State="validating";var report=await InspectAsync(p,stage,ct);if(report.Error is not null)throw new InvalidDataException(report.Error);
            await DocumentInference.UnloadAsync(ct);ct.ThrowIfCancellationRequested();
            string revision=Guid.NewGuid().ToString("N");published=Path.Combine(Root(p.Id),revision);Directory.Move(stage,published);stage=null;
            string temporary=Path.Combine(Root(p.Id),"active.json.tmp");
            try{await File.WriteAllTextAsync(temporary,JsonSerializer.Serialize(revision),ct);ct.ThrowIfCancellationRequested();File.Move(temporary,Path.Combine(Root(p.Id),"active.json"),true);published=null;}
            finally{File.Delete(temporary);}
            e.Warning=report.Warning;e.Hash=report.Hash;e.State="installed";e.Error=null;e.Fingerprint=string.Join("|",p.Assets.Select(a=>{var f=new FileInfo(Path.Combine(ActiveDirectory(p.Id)!,a.File));return $"{f.Length}:{f.LastWriteTimeUtc.Ticks}";}));
            CleanupRevisions(p.Id);
        }
        catch(OperationCanceledException){e.State="canceled";e.Error=null;}
        catch(Exception ex){e.State="error";e.Error=ex.Message;}
        finally{if(stage is not null)Directory.Delete(stage,true);if(published is not null)Directory.Delete(published,true);e.Gate.Release();}
    }
    private static async Task CopyAsync(System.IO.Stream input,string target,long expected,Entry e,CancellationToken ct)
    {
        await using var output=new FileStream(target,FileMode.Create,FileAccess.Write,FileShare.None,131072,true);byte[] buffer=new byte[131072];long count=0;
        using var stall=CancellationTokenSource.CreateLinkedTokenSource(ct);
        while(true){stall.CancelAfter(TimeSpan.FromSeconds(30));int n=await input.ReadAsync(buffer,stall.Token);if(n==0)break;count+=n;if(count>Math.Max(expected*2,1_048_576))throw new InvalidDataException("模型文件超过大小上限。");await output.WriteAsync(buffer.AsMemory(0,n),ct);Interlocked.Add(ref e.Downloaded,n);}
        await output.FlushAsync(ct);
    }
    private static async Task<(string? Error,string? Warning,string Hash)> InspectAsync(Package p,string directory,CancellationToken ct)
    {
        string? warning=null;var hashes=new System.Collections.Generic.List<string>();
        foreach(var a in p.Assets)
        {
            string path=Path.Combine(directory,a.File);if(!File.Exists(path))return("模型包缺少 "+a.File,null,"");
            await using var stream=File.OpenRead(path);string hash=Convert.ToHexString(await SHA256.HashDataAsync(stream,ct));hashes.Add(a.File+"="+hash);
            if(stream.Length!=a.Bytes||!hash.Equals(a.Sha256,StringComparison.OrdinalIgnoreCase))
            {
                if(a.File.EndsWith(".onnx",StringComparison.OrdinalIgnoreCase)&&LocalModelFiles.CheckFormat(path,"onnx") is{} error)return(error,null,hash);
                warning="SHA-256 与登记版本不一致，仍可尝试使用；来源及兼容性未经验证。";
            }
            if(a.File.EndsWith(".json",StringComparison.OrdinalIgnoreCase)){using var json=JsonDocument.Parse(await File.ReadAllTextAsync(path,ct));if(!json.RootElement.TryGetProperty("model",out var model)||!model.TryGetProperty("vocab",out _))return("tokenizer.json 不兼容。",null,hash);}
            if(a.File.EndsWith(".yml",StringComparison.OrdinalIgnoreCase)&&!((await File.ReadAllTextAsync(path,ct)).Contains("SLANet_plus")))return("表格配置不兼容。",null,hash);
        }
        return(null,warning,string.Join("; ",hashes));
    }
    internal async Task<DlcModelStore.Status> CancelAsync(string id)
    {var e=E(id);Task? job;lock(e){e.Cancellation?.Cancel();job=e.Job;}if(job is not null)await job;return Snapshot(Get(id),e);}
    internal async Task<DlcModelStore.Status> DeleteAsync(string id,CancellationToken ct)
    {
        await CancelAsync(id);var e=E(id);await e.Gate.WaitAsync(ct);
        try{e.State="deleting";await DocumentInference.UnloadAsync(ct);ct.ThrowIfCancellationRequested();File.Delete(Path.Combine(Root(id),"active.json"));CleanupRevisions(id,true);e.State="notInstalled";e.Downloaded=0;e.Fingerprint=e.Warning=e.Error=e.Hash=null;return Snapshot(Get(id),e);}
        finally{e.Gate.Release();}
    }
    private void CleanupRevisions(string id,bool all=false)
    {
        string? current=ActiveDirectory(id);if(current is null&&!all)return;foreach(string dir in Directory.EnumerateDirectories(Root(id)))
            if(Guid.TryParseExact(Path.GetFileName(dir),"N",out _)&&!string.Equals(dir,current,StringComparison.OrdinalIgnoreCase))
                try{Directory.Delete(dir,true);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
}
