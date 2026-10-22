using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Starshot;
using Starshot.Helpers;

int checks=0;
void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
int Port(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int p=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return p;}
var output=new List<object>();
string temporary=Path.Combine(Path.GetTempPath(),"Starshot-runtime-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temporary);AppConfig.UserDataFolder=temporary;
using var http=new HttpClient(new SocketsHttpHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(30)};
async Task<HttpResponseMessage> Call(int port,string route,HttpMethod? method=null,string? body=null,string? key=null,string? origin=null,string? host=null)
{
    using var req=new HttpRequestMessage(method??HttpMethod.Get,$"http://127.0.0.1:{port}"+route);
    if(body is not null)req.Content=new StringContent(body,Encoding.UTF8,"application/json");
    if(key is not null)req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
    if(origin is not null)req.Headers.Add("Origin",origin);
    if(host is not null)req.Headers.Host=host;
    return await http.SendAsync(req,HttpCompletionOption.ResponseHeadersRead);
}
try
{
    string key=LocalTranslationApiKey.ReadOrCreate();
    Check(key.Length==64&&key==LocalTranslationApiKey.ReadOrCreate(),"DPAPI key is stable within the user-data directory");
    Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(temporary,"local-translation-api.dpapi"))).Contains(key),"API credential is not stored as plaintext");
    int port=Port(),forwarded=0;bool canceled=false;byte[]? observed=null;
    var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using(var api=await LocalTranslationApi.StartAsync(port,key,async(route,method,body,response,ct)=>
    {
        Interlocked.Increment(ref forwarded);observed=body;response.ContentType="application/json";
        if(body is not null&&Encoding.UTF8.GetString(body).Contains("WAIT_FOR_CANCEL"))
        {entered.TrySetResult();try{await Task.Delay(30000,ct);}catch(OperationCanceledException){canceled=true;throw;}return;}
        if(body is not null&&Encoding.UTF8.GetString(body).Contains("\"stream\":true"))
        {response.ContentType="text/event-stream";await response.WriteAsync("data: {\"token\":\"hello\"}\n\n",ct);await response.Body.FlushAsync(ct);await Task.Delay(350,ct);await response.WriteAsync("data: [DONE]\n\n",ct);return;}
        await response.WriteAsync(route=="/v1/models"?"{\"data\":[{\"id\":\"Index-Translate-2B\"}]}":"{\"choices\":[{\"message\":{\"content\":\"你好\"}}]}",ct);
    },CancellationToken.None))
    {
        using(var r=await Call(port,"/v1/models"))Check(r.StatusCode==HttpStatusCode.Unauthorized,"Missing bearer key rejected");
        using(var r=await Call(port,"/v1/models",key:new string('A',64)))Check(r.StatusCode==HttpStatusCode.Unauthorized,"Wrong bearer key rejected");
        using(var r=await Call(port,"/v1/models",key:key,origin:"https://example.com"))Check(r.StatusCode==HttpStatusCode.Forbidden,"Browser-origin requests rejected");
        using(var r=await Call(port,"/v1/models",key:key,host:$"attacker.invalid:{port}"))Check(r.StatusCode==HttpStatusCode.Forbidden,"DNS-rebinding Host rejected");
        foreach(string route in new[]{"/props","/slots","/load","/completion","/v1/models/../../props"})
        {using var r=await Call(port,route,key:key);Check(r.StatusCode==HttpStatusCode.NotFound,"Private backend route blocked: "+route);}
        Check(forwarded==0,"Unauthorized or management requests never reach model");
        using(var r=await Call(port,"/v1/models",key:key))Check(r.IsSuccessStatusCode&&(await r.Content.ReadAsStringAsync()).Contains("Index-Translate-2B"),"Authenticated models endpoint");
        const string request="{\"model\":\"Index-Translate-2B\",\"messages\":[{\"role\":\"user\",\"content\":\"Hello\"}],\"max_tokens\":32,\"lora\":\"file:///x\",\"chat_template_kwargs\":{\"enable_thinking\":true}}";
        using(var r=await Call(port,"/v1/chat/completions",HttpMethod.Post,request,key))Check(r.IsSuccessStatusCode,"Ordinary OpenAI chat request forwarded");
        using(var body=JsonDocument.Parse(observed!))
        {Check(!body.RootElement.TryGetProperty("lora",out _),"Unsafe backend parameters removed");Check(!body.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean(),"Translation template disables thinking");}
        foreach(string invalid in new[]{"[]","{\"messages\":[]}","{\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"max_tokens\":\"bad\"}","{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"image_url\",\"image_url\":{\"url\":\"file:///x\"}}]}]}","{\"model\":\"other\",\"messages\":[]}"})
        {using var r=await Call(port,"/v1/chat/completions",HttpMethod.Post,invalid,key);Check(r.StatusCode==HttpStatusCode.BadRequest,"Invalid/remote payload rejected");}
        using(var r=await Call(port,"/v1/chat/completions",HttpMethod.Post,new string(' ',130*1024),key))Check((int)r.StatusCode==413,"Oversized requests bounded before inference");
        var clock=Stopwatch.StartNew();
        using(var r=await Call(port,"/v1/completions",HttpMethod.Post,"{\"prompt\":\"Hello\",\"stream\":true}",key))
        {
            using var stream=await r.Content.ReadAsStreamAsync();using var reader=new StreamReader(stream);
            Check((await reader.ReadLineAsync())!.Contains("hello")&&clock.ElapsedMilliseconds<300,"SSE first chunk is delivered without waiting for completion");
            Check((await reader.ReadToEndAsync()).Contains("[DONE]"),"SSE completion preserved");
        }
        var pending=Call(port,"/v1/completions",HttpMethod.Post,"{\"prompt\":\"WAIT_FOR_CANCEL\"}",key);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(api.Active==1,"Active API request reported");
        var more=Enumerable.Range(0,3).Select(_=>Call(port,"/v1/completions",HttpMethod.Post,"{\"prompt\":\"WAIT_FOR_CANCEL\"}",key)).ToArray();
        for(int i=0;i<40&&api.Active<4;i++)await Task.Delay(10);
        Check(api.Active==4,"Pending API requests are bounded");
        using(var saturated=await Call(port,"/v1/completions",HttpMethod.Post,"{\"prompt\":\"Hello\"}",key))Check((int)saturated.StatusCode==429,"Fifth concurrent request is rejected with retry status");
        // Disposal cancels forwarding even when a client is waiting for headers.
        await api.DisposeAsync();
        try{using var result=await pending;}catch(HttpRequestException){}
        Check(canceled,"Disabling API cancels in-flight request");
        foreach(var task in more){try{using var result=await task;}catch(HttpRequestException){}}
    }
    bool closed=false;try{using var r=await Call(port,"/v1/models",key:key);}catch(HttpRequestException){closed=true;}
    Check(closed,"Disabled API port is closed");

    await OfflineTranslation.SetGpuAsync(true,CancellationToken.None);
    Check(!OfflineTranslation.Loaded,"Selecting GPU while unloaded does not start a model");
    await OfflineTranslation.SetGpuAsync(false,CancellationToken.None);
    Check(!OfflineTranslation.Loaded,"Selecting CPU while unloaded does not start a model");
    using(var preCanceled=new CancellationTokenSource())
    {preCanceled.Cancel();bool aborted=false;try{await OfflineTranslation.LoadResidentAsync(preCanceled.Token);}catch(OperationCanceledException){aborted=true;}Check(aborted&&!OfflineTranslation.Loaded,"Pre-canceled loading does not spawn a worker");}
    if(args.Contains("--gpu-smoke"))
    {
        string? model=Environment.GetEnvironmentVariable("STARSHOT_GPU_TEST_MODEL");
        if(model is null||!File.Exists(model))throw new Exception("STARSHOT_GPU_TEST_MODEL missing");
        DlcModelStore.ModelPath=Path.GetFullPath(model);
        await OfflineTranslation.SetGpuAsync(true,CancellationToken.None);
        Console.Error.WriteLine("GPU smoke: loading one resident Vulkan worker...");
        var clock=Stopwatch.StartNew();await OfflineTranslation.LoadResidentAsync(CancellationToken.None);long loadMs=clock.ElapsedMilliseconds;
        JsonElement Status()=>JsonSerializer.SerializeToElement(OfflineTranslation.Status());
        Check(Status().GetProperty("resident").GetBoolean()&&Status().GetProperty("activeBackend").GetString()=="Vulkan0","GPU model explicitly resident");
        var worker=(Process)typeof(OfflineTranslation).GetField("_process",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
        int pid=worker.Id;Console.Error.WriteLine($"GPU smoke: worker ready, pid={pid}, load={loadMs} ms");
        Check(worker.Modules.Cast<ProcessModule>().Any(m=>m.ModuleName.Equals("ggml-vulkan.dll",StringComparison.OrdinalIgnoreCase)),"Real Vulkan backend module loaded");
        typeof(OfflineTranslation).GetField("_lastUsed",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,DateTime.UtcNow.AddMinutes(-5));
        typeof(OfflineTranslation).GetMethod("IdleCheck",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,null);
        Check(OfflineTranslation.Loaded,"Resident worker survives the idle-unload condition");
        int realPort=Port();await OfflineTranslation.SetApiAsync(true,realPort,CancellationToken.None);
        Check(ReferenceEquals(worker,typeof(OfflineTranslation).GetField("_process",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)),"API shares existing GPU worker without loading another model");
        string realKey=OfflineTranslation.ApiKeyForCopy();
        using(var r=await Call(realPort,"/v1/models",key:realKey))Check(r.IsSuccessStatusCode,"Real authenticated model discovery");
        clock.Restart();
        using(var r=await Call(realPort,"/v1/chat/completions",HttpMethod.Post,"{\"model\":\"Index-Translate-2B\",\"messages\":[{\"role\":\"system\",\"content\":\"Translate the user's English text to Simplified Chinese. Output the translation only.\"},{\"role\":\"user\",\"content\":\"The screenshot preserves bright highlights and accurate colors.\"}],\"max_tokens\":96}",realKey))
        {
            string raw=await r.Content.ReadAsStringAsync();Check(r.IsSuccessStatusCode,"Exactly one actual GPU generation request succeeds");
            using var answer=JsonDocument.Parse(raw);string value=answer.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
            Check(!string.IsNullOrWhiteSpace(value),"GPU translation returns text");
            output.Add(new{kind="gpu-smoke",pid,loadMs,inferenceMs=clock.ElapsedMilliseconds,answer=value,
                timings=answer.RootElement.TryGetProperty("timings",out var timings)?timings.Clone():(JsonElement?)null,
                gpuMemory=GpuCounter.Read(pid),privateMiB=worker.PrivateMemorySize64/1048576d,workingSetMiB=worker.WorkingSet64/1048576d});
        }
        Console.Error.WriteLine(JsonSerializer.Serialize(output[^1]));
        await OfflineTranslation.UnloadAsync(CancellationToken.None);
        Check(!OfflineTranslation.Loaded&&!Status().GetProperty("apiEnabled").GetBoolean(),"Unload clears resident model and API");
        bool alive=true;
        for(int retry=0;retry<40;retry++)
        {
            try{using var probe=Process.GetProcessById(pid);alive=!probe.HasExited;}catch(ArgumentException){alive=false;}
            if(!alive)break;await Task.Delay(50);
        }
        Check(!alive,"Owned GPU worker has exited (allow OS enumeration to settle)");
        closed=false;try{using var r=await Call(realPort,"/v1/models",key:realKey);}catch(HttpRequestException){closed=true;}
        Check(closed,"Unload closes external API listener");
    }
    Console.WriteLine(JsonSerializer.Serialize(new{ok=true,checks,actualGpuInference=args.Contains("--gpu-smoke"),rows=output},new JsonSerializerOptions{WriteIndented=true}));
}
finally{await OfflineTranslation.UnloadAsync(CancellationToken.None);Directory.Delete(temporary,true);}

internal static class GpuCounter
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Sample{public ulong DedicatedResident,SharedResident,DedicatedCommitted,SharedCommitted;public uint Adapters,ResidentQueries,CommittedQueries,FailedQueries,Enumerated,FirstFailure;}
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]private delegate uint Query(uint pid,out Sample value);
    internal static object? Read(int pid)
    {
        string? path=Environment.GetEnvironmentVariable("STARSHOT_GPU_MEMORY_DLL");if(path is null)return null;
        nint library=System.Runtime.InteropServices.NativeLibrary.Load(path);
        try{var read=System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<Query>(System.Runtime.InteropServices.NativeLibrary.GetExport(library,"ReadGpuMemory"));uint status=read((uint)pid,out var value);return new{status,dedicatedResidentMiB=value.DedicatedResident/1048576d,sharedResidentMiB=value.SharedResident/1048576d,dedicatedCommittedMiB=value.DedicatedCommitted/1048576d,failedQueries=value.FailedQueries};}
        finally{System.Runtime.InteropServices.NativeLibrary.Free(library);}
    }
}
