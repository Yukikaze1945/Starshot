using System.Net;
using Starshot.Helpers;

internal static class PackageTests
{
    internal static async Task<int> RunAsync(string root,DocumentModelStore.Package original,string models)
    {
        int checks=0;void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);checks++;}
        byte[] bytes=await File.ReadAllBytesAsync(Path.Combine(models,"layout.onnx"));
        var second=original.Assets[0] with{File="second.onnx",Url="https://huggingface.co/test/resolve/pinned/second.onnx"};
        var package=original with{Assets=[original.Assets[0],second]};
        using var stall=new AssetsHttp(bytes,true,false);var canceled=new DocumentModelStore(()=>Path.Combine(root,"interrupted-download"),()=>[package],new HttpClient(stall));
        canceled.Start(package.Id,"auto",null,default);await stall.SecondRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(canceled.ActiveDirectory(package.Id) is null,"A partial multi-file download is not published");
        var watch=System.Diagnostics.Stopwatch.StartNew();await canceled.CancelAsync(package.Id);Check(watch.ElapsedMilliseconds<2000,"Download cancellation terminates promptly");
        Check(canceled.ActiveDirectory(package.Id) is null&&!Directory.EnumerateDirectories(Path.Combine(root,"interrupted-download",package.Id),".stage-*").Any(),"Canceled package staging cleaned");
        using var online=new AssetsHttp(bytes,false,false);string onlineRoot=Path.Combine(root,"complete-download");var complete=new DocumentModelStore(()=>onlineRoot,()=>[package],new HttpClient(online));
        complete.Start(package.Id,"auto",null,default);var installed=await Wait(complete,package.Id);Check(installed.Installed&&installed.Warning is null,"Multi-file download, hash and atomic publication");
        var restarted=new DocumentModelStore(()=>onlineRoot,()=>[package]);Check((await restarted.StatusAsync(package.Id,false,default)).Installed,"Installed package survives restart");
        using var offline=new AssetsHttp(bytes,false,true);var failed=new DocumentModelStore(()=>Path.Combine(root,"offline-download"),()=>[package],new HttpClient(offline));
        failed.Start(package.Id,"auto",null,default);Check((await Wait(failed,package.Id)).State=="error"&&failed.ActiveDirectory(package.Id) is null,"Offline failure cannot create an installed package");
        string modified=Path.Combine(root,"modified");Directory.CreateDirectory(modified);await File.WriteAllBytesAsync(Path.Combine(modified,"layout.onnx"),bytes.Concat(new byte[]{0xba,0x3e,0}).ToArray());
        var advisory=new DocumentModelStore(()=>Path.Combine(root,"advisory"),()=>[original]);advisory.Start(original.Id,"auto",modified,default);var warning=await Wait(advisory,original.Id);
        Check(warning.Installed&&warning.Warning is not null,"Valid ONNX with a different SHA remains usable with a warning");
        using(var session=new Microsoft.ML.OnnxRuntime.InferenceSession(Path.Combine(advisory.ActiveDirectory(original.Id)!,"layout.onnx"),new Microsoft.ML.OnnxRuntime.SessionOptions{LogSeverityLevel=Microsoft.ML.OnnxRuntime.OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,IntraOpNumThreads=2}))
            Check(session.InputMetadata.ContainsKey("image"),"Advisory hash model remains loadable");
        await advisory.DeleteAsync(original.Id,default);Check(!(await advisory.StatusAsync(original.Id,true,default)).Installed,"Advisory package deletion");return checks;
    }
    private static async Task<DlcModelStore.Status> Wait(DocumentModelStore store,string id)
    {for(int i=0;i<1000;i++){await Task.Delay(10);var s=await store.StatusAsync(id,false,default);if(s.State is not("importing" or "downloading" or "validating"))return s;}throw new TimeoutException("Package job did not finish");}
    private sealed class AssetsHttp(byte[] bytes,bool stall,bool offline):HttpMessageHandler
    {
        internal readonly TaskCompletionSource<bool> SecondRequested=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(offline)return new(HttpStatusCode.ServiceUnavailable){Content=new StringContent("offline")};
            if(request.RequestUri!.AbsolutePath.EndsWith("second.onnx")){SecondRequested.TrySetResult(true);if(stall)await Task.Delay(Timeout.Infinite,ct);}
            return new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};
        }
    }
}
