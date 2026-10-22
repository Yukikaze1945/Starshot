using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Starshot.Helpers;

/// <summary>Data-only packages: source selection, advisory hashes, format checks and atomic publication.</summary>
public sealed partial class DlcModelStore
{
    public sealed record Model(string Id,string Name,string Task,long Bytes,string Sha256,string Url,string File,string License,string Source);
    public static readonly Model[] Catalog =
    [
        new("index-q5km","Index-Translate-2B · Q5_K_M","translation",1454787072,
            "2A869B5E374FF6121EE5191629FBA8FF4A081951D56EEC90D89771D7971465EE",
            "https://huggingface.co/IndexTeam/Index-Translate-2B-GGUF/resolve/3a0eca6ce931d88d00614d427ab3ed8ff0743902/Index-Translate-2B.Q5_K_M.gguf",
            "Index-Translate-2B.Q5_K_M.gguf","Apache-2.0","https://huggingface.co/IndexTeam/Index-Translate-2B-GGUF/tree/3a0eca6ce931d88d00614d427ab3ed8ff0743902"),
        new("u2netp","U²-Net compact","segmentation",4574861,
            "309C8469258DDA742793DCE0EBEA8E6DD393174F89934733ECC8B14C76F4DDD8",
            "https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2netp.onnx",
            "u2netp.onnx","Apache-2.0","https://github.com/xuebinqin/U-2-Net"),
        .. DocumentModelStore.Descriptors,
    ];
    public sealed record Status(string Id,string Name,string Task,long Bytes,long Downloaded,string State,string? Error,string License,string Source,bool Installed,string Sha256,
        string? Warning,string? ActualSha256,string Directory,string? ActiveSource,ModelDownloadSource[] Sources);
    private sealed class Entry { public readonly SemaphoreSlim Gate=new(1);public Task? Job;public CancellationTokenSource? Cancellation;public long Downloaded;public long Total;public string State="notInstalled";public string? Error;public string? Fingerprint;public string? Warning,ActualSha256,ActiveSource; }
    private static readonly HttpClient Http=new(new SocketsHttpHandler{ConnectTimeout=TimeSpan.FromSeconds(20)}){Timeout=Timeout.InfiniteTimeSpan};
    public static DlcModelStore Current {get;}=new(()=>Path.Combine(AppConfig.UserDataFolder,"Models","Library"),Http);
    private readonly Func<string> _root; private readonly HttpClient _http; private readonly Dictionary<string,Entry> _entries=new();private readonly object _sync=new();
    private readonly Model[] _catalog;
    internal DlcModelStore(Func<string> root,HttpClient http,Model[]? catalog=null){_root=root;_http=http;_catalog=catalog??Catalog;foreach(var model in _catalog)_entries.Add(model.Id,new());}
    public string PathFor(string id)=>DocumentModelStore.Contains(id)?DocumentModelStore.Current.ActiveDirectory(id)??Path.Combine(Path.GetFullPath(_root()),id):Path.Combine(Path.GetFullPath(_root()),Get(id).File);
    private Model Get(string id)=>Array.Find(_catalog,m=>m.Id==id) ?? throw new ArgumentException("未知模型。");
    private Status Snapshot(Model m,Entry e)=>new(m.Id,m.Name,m.Task,e.Total>0?e.Total:m.Bytes,Interlocked.Read(ref e.Downloaded),e.State,e.Error,m.License,m.Source,e.State=="installed",m.Sha256,
        e.Warning,e.ActualSha256,PathFor(m.Id),e.ActiveSource,ModelDownloadSources.For(m.Id,m.Url));
    private static string? Fingerprint(string path){var f=new FileInfo(path);return f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : null;}

    public async Task<Status> StatusAsync(string id,bool force=false,CancellationToken ct=default)
    {
        if(DocumentModelStore.Contains(id))return await DocumentModelStore.Current.StatusAsync(id,force,ct);
        var m=Get(id);var e=_entries[id];
        if(!await e.Gate.WaitAsync(0,ct))return Snapshot(m,e);
        try
        {
            if(e.Job is {IsCompleted:false})return Snapshot(m,e);
            string path=PathFor(id);string? fingerprint=Fingerprint(path);
            if(!force && fingerprint is not null && e.Fingerprint==fingerprint && e.State=="installed")return Snapshot(m,e);
            var report=await Task.Run(()=>InspectAsync(path,m,ct),ct).ConfigureAwait(false);
            bool valid=report.Valid;
            if(e.Job is {IsCompleted:false})return Snapshot(m,e);
            e.Fingerprint=valid ? fingerprint : null;e.Downloaded=valid ? new FileInfo(path).Length : 0;
            e.Total=valid?e.Downloaded:m.Bytes;e.Warning=report.Warning;e.ActualSha256=report.Hash;
            if(valid){e.State="installed";e.Error=null;}
            else if(fingerprint is not null){e.State="corrupt";e.Error=report.Error??"文件无法读取，请重新下载。";}
            else if(e.State is not("error" or "canceled")){e.State="notInstalled";e.Error=null;}
            return Snapshot(m,e);
        }
        finally{e.Gate.Release();}
    }
    public async Task<string> RequireAsync(string id,CancellationToken ct)
    {if(DocumentModelStore.Contains(id))return await DocumentModelStore.Current.RequireAsync(id,ct);if(!(await StatusAsync(id,false,ct)).Installed)throw new InvalidOperationException("请先在模型管理库下载或导入可读取的 "+Get(id).Name);return PathFor(id);}
    internal static async Task<bool> VerifyAsync(string path,Model model,CancellationToken ct)
    {
        if(!File.Exists(path)||new FileInfo(path).Length!=model.Bytes)return false;
        await using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,true);
        string sha=Convert.ToHexString(await SHA256.HashDataAsync(file,ct));return sha.Equals(model.Sha256,StringComparison.OrdinalIgnoreCase);
    }
    public Task<Status> StartAsync(string id,CancellationToken ct=default,string source="auto")
    {
        if(DocumentModelStore.Contains(id))return Task.FromResult(DocumentModelStore.Current.Start(id,source,null,ct));
        ct.ThrowIfCancellationRequested();var m=Get(id);var e=_entries[id];
        var sources=System.Linq.Enumerable.ToArray(ModelDownloadSources.Resolve(m.Id,m.Url,source));
        lock(_sync)
        {
            if(e.Job is {IsCompleted:false})return Task.FromResult(Snapshot(m,e));
            e.Cancellation?.Dispose();e.Cancellation=new();e.State="downloading";e.Error=null;e.Warning=null;e.Downloaded=0;e.Total=m.Bytes;
            var token=e.Cancellation.Token;e.Job=Task.Run(()=>DownloadAsync(m,e,sources,token));
            return Task.FromResult(Snapshot(m,e));
        }
    }
    private async Task DownloadAsync(Model m,Entry e,ModelDownloadSource[] sources,CancellationToken ct)
    {
        bool acquired=false;string? partial=null;
        try
        {
            await e.Gate.WaitAsync(ct);acquired=true;
            string target=PathFor(m.Id);Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            foreach(var sourceOption in sources)
            {
            partial=target+"."+Guid.NewGuid().ToString("N")+".partial";e.ActiveSource=sourceOption.Label;e.Downloaded=0;
            try
            {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response=await _http.GetAsync(sourceOption.Url,HttpCompletionOption.ResponseHeadersRead,timeout.Token);response.EnsureSuccessStatusCode();
            timeout.CancelAfter(TimeSpan.FromMinutes(60));
            e.Total=response.Content.Headers.ContentLength??m.Bytes;
            await using(var source=await response.Content.ReadAsStreamAsync(timeout.Token))
            await using(var file=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true))
            using(var stall=CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
            {
                byte[] buffer=new byte[128*1024];int n;
                while(true)
                {
                    stall.CancelAfter(TimeSpan.FromSeconds(30));n=await source.ReadAsync(buffer,stall.Token);if(n==0)break;
                    if(Interlocked.Read(ref e.Downloaded)+n>Math.Max(m.Bytes*4,64*1024*1024))throw new InvalidDataException("模型超出支持的大小范围。");
                    await file.WriteAsync(buffer.AsMemory(0,n),timeout.Token);Interlocked.Add(ref e.Downloaded,n);
                }
                e.State="validating";
                await file.FlushAsync(timeout.Token);
            }
            var report=await InspectAsync(partial,m,ct);
            if(!report.Valid)throw new InvalidDataException(report.Error);
            ct.ThrowIfCancellationRequested();
            await ReleaseRuntimeAsync(m.Id,ct);
            File.Move(partial,target,true);partial=null;e.Fingerprint=Fingerprint(target);e.State="installed";e.Error=null;
            e.Warning=report.Warning;e.ActualSha256=report.Hash;e.Total=e.Downloaded;return;
            }
            catch(Exception ex) when(!ct.IsCancellationRequested && sourceOption!=sources[^1])
            { if(partial is not null && File.Exists(partial))File.Delete(partial);partial=null;e.Error="正在尝试下一个来源："+ex.Message; }
            }
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){e.State="canceled";e.Error=null;}
        catch(Exception ex){e.State="error";e.Error=ex.Message;}
        finally{if(partial is not null && File.Exists(partial))File.Delete(partial);if(acquired)e.Gate.Release();}
    }
    public async Task<Status> CancelAsync(string id)
    {
        if(DocumentModelStore.Contains(id))return await DocumentModelStore.Current.CancelAsync(id);
        var e=_entries[Get(id).Id];Task? job;lock(_sync){e.Cancellation?.Cancel();job=e.Job;}
        if(job is not null)await job;return Snapshot(Get(id),e);
    }
    public async Task<Status> DeleteAsync(string id,CancellationToken ct=default)
    {
        if(DocumentModelStore.Contains(id))return await DocumentModelStore.Current.DeleteAsync(id,ct);
        await CancelAsync(id);var m=Get(id);var e=_entries[id];await e.Gate.WaitAsync(ct);
        try{e.State="deleting";await ReleaseRuntimeAsync(id,ct);File.Delete(PathFor(id));e.Fingerprint=null;e.Downloaded=0;e.State="notInstalled";e.Error=e.Warning=e.ActualSha256=e.ActiveSource=null;return Snapshot(m,e);}
        finally{e.Gate.Release();}
    }
    private static Task ReleaseRuntimeAsync(string id,CancellationToken ct)=>id=="index-q5km" ? OfflineTranslation.UnloadAsync(ct) : BackgroundRemoval.UnloadAsync(ct);
}
