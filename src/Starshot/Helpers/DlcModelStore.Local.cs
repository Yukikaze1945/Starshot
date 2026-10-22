using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Starshot.Helpers;

public sealed partial class DlcModelStore
{
    private sealed record Inspection(bool Valid,string? Hash,string? Warning,string? Error);
    private static async Task<Inspection> InspectAsync(string path,Model model,CancellationToken ct)
    {
        if(!File.Exists(path))return new(false,null,null,null);
        try
        {
            await using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,131072,true);
            string hash=Convert.ToHexString(await SHA256.HashDataAsync(file,ct));
            if(file.Length==model.Bytes && hash.Equals(model.Sha256,StringComparison.OrdinalIgnoreCase))return new(true,hash,null,null);
            file.Close();
            string? error=LocalModelFiles.CheckFormat(path,model.Id);
            return error is not null?new(false,hash,null,error):new(true,hash,"SHA-256 与登记版本不一致，仍可启用；来源及兼容性未经验证。",null);
        }
        catch(IOException ex){return new(false,null,null,ex.Message);}
    }
    public Task<Status> ImportAsync(string id,string path,CancellationToken ct=default)
    {
        if(DocumentModelStore.Contains(id))return Task.FromResult(DocumentModelStore.Current.Start(id,"auto",path,ct));
        ct.ThrowIfCancellationRequested();var m=Get(id);var e=_entries[id];path=Path.GetFullPath(path);
        if(!File.Exists(path))throw new FileNotFoundException("请选择本地模型文件。",path);
        if(string.Equals(path,PathFor(id),StringComparison.OrdinalIgnoreCase))return StatusAsync(id,true,ct);
        lock(_sync)
        {
            if(e.Job is{IsCompleted:false})throw new InvalidOperationException("请先取消当前模型操作。");
            e.Cancellation?.Dispose();e.Cancellation=new();var token=e.Cancellation.Token;
            e.State="importing";e.Error=e.Warning=null;e.Downloaded=0;e.Total=new FileInfo(path).Length;e.ActiveSource="本地导入";
            e.Job=Task.Run(()=>InstallLocalAsync(m,e,path,token));return Task.FromResult(Snapshot(m,e));
        }
    }
    private async Task InstallLocalAsync(Model m,Entry e,string input,CancellationToken ct)
    {
        string? stage=null;bool acquired=false;
        try
        {
            await e.Gate.WaitAsync(ct);acquired=true;string target=PathFor(m.Id);Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            stage=target+"."+Guid.NewGuid().ToString("N")+".partial";
            await using(var from=new FileStream(input,FileMode.Open,FileAccess.Read,FileShare.Read,131072,true))
            await using(var to=new FileStream(stage,FileMode.CreateNew,FileAccess.Write,FileShare.None,131072,true))
            {
                byte[] buffer=new byte[131072];int n;
                while((n=await from.ReadAsync(buffer,ct))>0){await to.WriteAsync(buffer.AsMemory(0,n),ct);Interlocked.Add(ref e.Downloaded,n);}
                await to.FlushAsync(ct);
            }
            e.State="validating";var report=await InspectAsync(stage,m,ct);
            if(!report.Valid)throw new InvalidDataException(report.Error);
            await ReleaseRuntimeAsync(m.Id,ct);ct.ThrowIfCancellationRequested();File.Move(stage,target,true);stage=null;
            e.Fingerprint=Fingerprint(target);e.Warning=report.Warning;e.ActualSha256=report.Hash;e.State="installed";e.Error=null;
        }
        catch(OperationCanceledException){e.State="canceled";e.Error=null;}
        catch(Exception ex){e.State="error";e.Error=ex.Message;}
        finally{if(stage is not null&&File.Exists(stage))File.Delete(stage);if(acquired)e.Gate.Release();}
    }
}
