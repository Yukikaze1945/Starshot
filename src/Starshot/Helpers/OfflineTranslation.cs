using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Starshot.Helpers;

/// <summary>One lazy CPU/Vulkan worker, shared by internal translation and an optional loopback API.</summary>
internal static class OfflineTranslation
{
    private static readonly SemaphoreSlim Gate=new(1),Control=new(1);
    private static readonly HttpClient Http=new(new SocketsHttpHandler{UseProxy=false}){Timeout=TimeSpan.FromMinutes(3)};
    private static Process? _process;
    private static Uri? _endpoint;
    private static string? _key,_modelPath;
    private static readonly object StopSync=new();
    private static Task _shutdown=Task.CompletedTask;
    private static DateTime _lastUsed;
    private static volatile bool _resident,_gpuActive;
    private static volatile string _phase="idle";
    private static string? _lastError;
    private static LocalTranslationApi? _api;
    private static readonly Timer Idle=new(_=>IdleCheck(),null,TimeSpan.FromSeconds(15),TimeSpan.FromSeconds(15));
    static OfflineTranslation()=>AppDomain.CurrentDomain.ProcessExit+=(_,_)=>{try{_api?.Abort();}finally{Stop();}};
    private static bool ProcessAlive{get{try{return _process is {HasExited:false};}catch(InvalidOperationException){return false;}}}
    public static bool Loaded=>_phase=="ready"&&ProcessAlive;
    private static string RuntimeDirectory=>Path.Combine(AppContext.BaseDirectory,AppConfig.LocalTranslationGpu?"LocalInferenceVulkan":"LocalInference");
    internal static object Status()=>new{
        loaded=Loaded,resident=_resident&&Loaded,gpuEnabled=AppConfig.LocalTranslationGpu,
        activeBackend=Loaded?(_gpuActive?"Vulkan0":"CPU"):null,
        state=_phase=="ready"&&!ProcessAlive?"error":_phase,
        error=_phase=="ready"&&!ProcessAlive?"模型进程已退出，请重新加载。":_lastError,
        gpuRuntimeAvailable=File.Exists(Path.Combine(AppContext.BaseDirectory,"LocalInferenceVulkan","manifest.json")),
        apiEnabled=_api is not null,apiPort=AppConfig.LocalTranslationApiPort,
        apiUrl=_api?.BaseUrl??$"http://127.0.0.1:{AppConfig.LocalTranslationApiPort}/v1",
        apiActiveRequests=_api?.Active??0,idleSeconds=60
    };
    internal static string ApiKeyForCopy()=>_api is null?throw new InvalidOperationException("请先开启本地 API。"):LocalTranslationApiKey.ReadOrCreate();

    internal static async Task<string> SendAsync(string instruction,string text,CancellationToken ct)
    {
        if(text.Length>9000)throw new ArgumentException("离线翻译单段过长，请分段后重试。");
        // Model-store locks always precede worker locks; import/delete wait for worker cleanup.
        string model=await DlcModelStore.Current.RequireAsync("index-q5km",ct);
        await Gate.WaitAsync(ct);
        try
        {
            await EnsureStartedAsync(model,ct);
            var payload=new{model="Index-Translate-2B",temperature=0,max_tokens=2048,
                chat_template_kwargs=new{enable_thinking=false},
                messages=new[]{new{role="system",content=instruction},new{role="user",content=text}}};
            using var request=BackendRequest("/v1/chat/completions",HttpMethod.Post,JsonSerializer.SerializeToUtf8Bytes(payload));
            using var response=await Http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
            if(!response.IsSuccessStatusCode)throw new InvalidOperationException($"本地翻译返回 {(int)response.StatusCode}；请检查上下文长度与模型状态。");
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var choice=json.RootElement.GetProperty("choices")[0];
            if(choice.TryGetProperty("finish_reason",out var reason)&&reason.GetString()=="length")throw new InvalidDataException("本地译文达到长度限制，未替换原文；请分段翻译。");
            string result=choice.GetProperty("message").GetProperty("content").GetString()?.Trim()??"";
            int end=result.IndexOf("</think>",StringComparison.Ordinal);if(end>=0)result=result[(end+8)..].Trim();
            if(result.Length==0)throw new InvalidDataException("本地模型未返回译文。");
            return result;
        }
        catch(Exception)when(ct.IsCancellationRequested){throw new OperationCanceledException(ct);}
        finally{_lastUsed=DateTime.UtcNow;Gate.Release();}
    }
    private static HttpRequestMessage BackendRequest(string route,HttpMethod method,byte[]? body)
    {
        var endpoint=_endpoint??throw new InvalidOperationException("模型未加载。");
        var request=new HttpRequestMessage(method,new Uri(endpoint,route));
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",_key);
        if(body is not null){request.Content=new ByteArrayContent(body);request.Content.Headers.ContentType=new("application/json");}
        return request;
    }
    private static async Task ForwardAsync(string route,HttpMethod method,byte[]? body,HttpResponse output,CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if(!Loaded)throw new InvalidOperationException("模型未就绪。");
            using var request=BackendRequest(route,method,body);
            using var response=await Http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
            output.StatusCode=(int)response.StatusCode;
            output.ContentType=response.Content.Headers.ContentType?.ToString()??"application/json";
            await response.Content.CopyToAsync(output.Body,ct);
            await output.Body.FlushAsync(ct);
        }
        finally{_lastUsed=DateTime.UtcNow;Gate.Release();}
    }

    internal static async Task LoadResidentAsync(CancellationToken ct)
    {
        string model=await DlcModelStore.Current.RequireAsync("index-q5km",ct);
        await Control.WaitAsync(ct);
        try{await Gate.WaitAsync(ct);try{await EnsureStartedAsync(model,ct);_resident=true;}catch{_resident=false;throw;}finally{Gate.Release();}}
        finally{Control.Release();}
    }
    internal static async Task SetGpuAsync(bool enabled,CancellationToken ct)
    {
        string? model=Loaded?await DlcModelStore.Current.RequireAsync("index-q5km",ct):null;
        await Control.WaitAsync(ct);
        bool changed=false;
        try
        {
            if(AppConfig.LocalTranslationGpu==enabled)return;
            await Gate.WaitAsync(ct);
            try
            {
                bool reload=Loaded;model??=_modelPath;
                changed=true;
                Stop();await _shutdown;
                ct.ThrowIfCancellationRequested();
                AppConfig.LocalTranslationGpu=enabled;
                if(reload&&model is not null)await EnsureStartedAsync(model,ct);
                else {_phase="idle";_lastError=null;}
            }
            finally{Gate.Release();}
        }
        catch(Exception ex)
        {
            if(changed){_resident=false;if(!Loaded){_phase="error";_lastError=ex is OperationCanceledException?"模式切换已取消，请重新加载模型。":ex.Message;}await StopApiAsync();}
            throw;
        }
        finally{Control.Release();}
    }
    internal static async Task SetApiAsync(bool enabled,int port,CancellationToken ct)
    {
        if(port is <1024 or >65535)throw new ArgumentException("本地 API 端口范围为 1024–65535。");
        string? model=enabled?await DlcModelStore.Current.RequireAsync("index-q5km",ct):null;
        await Control.WaitAsync(ct);
        bool hadModel=Loaded,wasResident=_resident;
        try
        {
            if(!enabled){await StopApiAsync();return;}
            if(_api?.Port==port)return;
            await StopApiAsync();
            await Gate.WaitAsync(ct);
            try{await EnsureStartedAsync(model!,ct);_resident=true;}
            finally{Gate.Release();}
            try
            {
                _api=await LocalTranslationApi.StartAsync(port,LocalTranslationApiKey.ReadOrCreate(),ForwardAsync,ct);
                AppConfig.LocalTranslationApiPort=port;
            }
            catch
            {
                _resident=wasResident;
                if(!hadModel&&!wasResident){await Gate.WaitAsync(CancellationToken.None);try{Stop();await _shutdown;_phase="idle";}finally{Gate.Release();}}
                ct.ThrowIfCancellationRequested();
                throw new InvalidOperationException("本地 API 启动失败，请检查端口是否被占用或密钥文件是否可读。");
            }
        }
        finally{Control.Release();}
    }
    private static async Task StopApiAsync(){var api=Interlocked.Exchange(ref _api,null);if(api is not null)await api.DisposeAsync();}
    internal static async Task UnloadAsync(CancellationToken ct)
    {
        await Control.WaitAsync(ct);
        try
        {
            await StopApiAsync();
            await Gate.WaitAsync(ct);
            try{_phase="unloading";_resident=false;Stop();await _shutdown;_phase="idle";_lastError=null;}
            finally{Gate.Release();}
        }
        finally{Control.Release();}
    }
    private static async Task EnsureStartedAsync(string model,CancellationToken ct)
    {
        if(Loaded)return;
        _phase="loading";_lastError=null;
        try
        {
            Stop();await _shutdown.WaitAsync(ct);
            string directory=RuntimeDirectory,manifestPath=Path.Combine(directory,"manifest.json");
            if(!File.Exists(manifestPath))throw new FileNotFoundException("离线推理组件缺失，请重新运行源码启动脚本。");
            using var manifest=JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath,ct));
            foreach(var entry in manifest.RootElement.GetProperty("files").EnumerateObject())
            {
                if(Path.GetFileName(entry.Name)!=entry.Name)throw new InvalidDataException("无效推理组件清单。");
                await using var file=File.OpenRead(Path.Combine(directory,entry.Name));
                if(!Convert.ToHexString(await SHA256.HashDataAsync(file,ct)).Equals(entry.Value.GetString(),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("离线推理组件校验失败。");
            }
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
            _key=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));_endpoint=new Uri($"http://127.0.0.1:{port}/");
            var start=new ProcessStartInfo(Path.Combine(directory,"llama-server.exe")){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=directory,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string arg in new[]{"--model",model,"--alias","Index-Translate-2B","--host","127.0.0.1","--port",port.ToString(),"--api-key",_key,
                "--ctx-size","4096","--parallel","1","--threads","4","--n-gpu-layers",AppConfig.LocalTranslationGpu?"all":"0","--no-webui","--jinja","--chat-template-kwargs","{\"enable_thinking\":false}","--log-disable"})start.ArgumentList.Add(arg);
            if(AppConfig.LocalTranslationGpu)foreach(string arg in new[]{"--device","Vulkan0","--split-mode","none","--fit","off"})start.ArgumentList.Add(arg);
            var process=Process.Start(start)??throw new IOException("无法启动离线翻译进程。");_process=process;
            process.OutputDataReceived+=(_,_)=>{};process.ErrorDataReceived+=(_,_)=>{};process.BeginOutputReadLine();process.BeginErrorReadLine();
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(120));
            while(true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if(process.HasExited)throw new InvalidOperationException(AppConfig.LocalTranslationGpu?"GPU 模型启动失败，请释放显存、更新 Vulkan 驱动或切回 CPU。":$"本地模型启动失败（{process.ExitCode}）。");
                try
                {
                    using var health=new HttpRequestMessage(HttpMethod.Get,$"http://127.0.0.1:{port}/health");health.Headers.Authorization=new("Bearer",_key);
                    using var response=await Http.SendAsync(health,timeout.Token);if(response.IsSuccessStatusCode)break;
                }
                catch(HttpRequestException){}
                await Task.Delay(150,timeout.Token);
            }
            _gpuActive=AppConfig.LocalTranslationGpu;_modelPath=model;_phase="ready";_lastUsed=DateTime.UtcNow;
        }
        catch(Exception ex){Stop();await _shutdown;_phase="error";_lastError=ex is OperationCanceledException?"模型加载已取消或超时。":ex.Message;throw;}
    }
    private static void Stop()
    {
        lock(StopSync)
        {
            var process=Interlocked.Exchange(ref _process,null);_endpoint=null;_key=null;_gpuActive=false;_modelPath=null;
            if(process is null)return;
            try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}
            _shutdown=DrainAsync(process);
        }
    }
    private static async Task DrainAsync(Process process){try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}catch(InvalidOperationException){}catch(TimeoutException){}finally{process.Dispose();}}
    private static void IdleCheck()
    {
        if(_resident||_api is not null||DateTime.UtcNow-_lastUsed<TimeSpan.FromSeconds(60)||!Gate.Wait(0))return;
        try{if(!_resident&&_api is null){Stop();_phase="idle";}}finally{Gate.Release();}
    }
}
