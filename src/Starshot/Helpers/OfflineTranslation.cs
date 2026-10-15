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

namespace Starshot.Helpers;

/// <summary>Lazy, private loopback llama.cpp CPU worker. No external service or user API key.</summary>
internal static class OfflineTranslation
{
    private static readonly SemaphoreSlim Gate=new(1);
    private static readonly HttpClient Http=new(){Timeout=TimeSpan.FromMinutes(3)};
    private static Process? _process;private static Uri? _endpoint;private static string? _key;
    private static readonly object StopSync=new();private static Task _shutdown=Task.CompletedTask;
    private static DateTime _lastUsed;
    private static readonly Timer Idle=new(_=>IdleCheck(),null,TimeSpan.FromSeconds(15),TimeSpan.FromSeconds(15));
    static OfflineTranslation()=>AppDomain.CurrentDomain.ProcessExit+=(_,_)=>Stop();
    public static bool Loaded{get{try{return _process is {HasExited:false};}catch(InvalidOperationException){return false;}}}
    private static string RuntimeDirectory=>Path.Combine(AppContext.BaseDirectory,"LocalInference");

    internal static async Task<string> SendAsync(string instruction,string text,CancellationToken ct)
    {
        if(text.Length>9000)throw new ArgumentException("离线翻译单段过长，请分段后重试。");
        await Gate.WaitAsync(ct);
        try
        {
            await EnsureStartedAsync(ct);
            using var cancellation=ct.Register(Stop);
            var payload=new{model="Index-Translate-2B",temperature=0,max_tokens=2048,
                chat_template_kwargs=new{enable_thinking=false},
                messages=new[]{new{role="system",content=instruction},new{role="user",content=text}}};
            using var request=new HttpRequestMessage(HttpMethod.Post,_endpoint){Content=new StringContent(JsonSerializer.Serialize(payload),Encoding.UTF8,"application/json")};
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",_key);
            using var response=await Http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
            if(!response.IsSuccessStatusCode)throw new InvalidOperationException($"本地翻译返回 {(int)response.StatusCode}；文本可能超过上下文长度，请缩短后重试。");
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var choice=json.RootElement.GetProperty("choices")[0];
            if(choice.TryGetProperty("finish_reason",out var reason)&&reason.GetString()=="length")throw new InvalidDataException("本地译文达到长度限制，未替换原文；请分段翻译。");
            string result=choice.GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "";
            // Some templates expose thinking even with enable_thinking=false.
            int end=result.IndexOf("</think>",StringComparison.Ordinal);if(end>=0)result=result[(end+8)..].Trim();
            if(result.Length==0)throw new InvalidDataException("本地模型未返回译文。");
            _lastUsed=DateTime.UtcNow;return result;
        }
        catch(Exception)when(ct.IsCancellationRequested){throw new OperationCanceledException(ct);}
        finally{_lastUsed=DateTime.UtcNow;Gate.Release();}
    }
    private static async Task EnsureStartedAsync(CancellationToken ct)
    {
        if(Loaded)return;
        Stop();await _shutdown.WaitAsync(ct);string model=await DlcModelStore.Current.RequireAsync("index-q5km",ct);
        string manifestPath=Path.Combine(RuntimeDirectory,"manifest.json");
        if(!File.Exists(manifestPath))throw new FileNotFoundException("离线推理组件缺失，请重新运行源码启动脚本。");
        using var manifest=JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath,ct));
        foreach(var entry in manifest.RootElement.GetProperty("files").EnumerateObject())
        {
            if(Path.GetFileName(entry.Name)!=entry.Name)throw new InvalidDataException("无效推理组件清单");
            await using var file=File.OpenRead(Path.Combine(RuntimeDirectory,entry.Name));
            if(!Convert.ToHexString(await SHA256.HashDataAsync(file,ct)).Equals(entry.Value.GetString(),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("离线推理组件校验失败。");
        }
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        _key=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));_endpoint=new Uri($"http://127.0.0.1:{port}/v1/chat/completions");
        var start=new ProcessStartInfo(Path.Combine(RuntimeDirectory,"llama-server.exe")){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=RuntimeDirectory,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in new[]{"--model",model,"--alias","Index-Translate-2B","--host","127.0.0.1","--port",port.ToString(),"--api-key",_key,
            "--ctx-size","4096","--parallel","1","--threads","4","--n-gpu-layers","0","--no-webui","--jinja","--chat-template-kwargs","{\"enable_thinking\":false}","--log-disable"})start.ArgumentList.Add(arg);
        var process=Process.Start(start) ?? throw new IOException("无法启动离线翻译进程");_process=process;
        // Drain redirected pipes; never retain source text or an unbounded inference log.
        process.OutputDataReceived+=(_,_)=>{};process.ErrorDataReceived+=(_,_)=>{};process.BeginOutputReadLine();process.BeginErrorReadLine();
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(120));
            while(true)
            {
                timeout.Token.ThrowIfCancellationRequested();if(process.HasExited)throw new InvalidOperationException($"本地翻译进程启动失败（{process.ExitCode}）；请检查模型与 CPU 支持。");
                try
                {
                    using var health=new HttpRequestMessage(HttpMethod.Get,$"http://127.0.0.1:{port}/health");health.Headers.Authorization=new("Bearer",_key);
                    using var response=await Http.SendAsync(health,timeout.Token);if(response.IsSuccessStatusCode)break;
                }
                catch(HttpRequestException){}
                await Task.Delay(150,timeout.Token);
            }
        }
        catch{Stop();throw;}
        _lastUsed=DateTime.UtcNow;
    }
    private static void Stop()
    {
        lock(StopSync)
        {
            var process=Interlocked.Exchange(ref _process,null);_endpoint=null;
            if(process is null)return;
            try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}
            _shutdown=DrainAsync(process);
        }
    }
    private static async Task DrainAsync(Process process){try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}catch(InvalidOperationException){}catch(TimeoutException){}finally{process.Dispose();}}
    private static void IdleCheck(){if(DateTime.UtcNow-_lastUsed<TimeSpan.FromSeconds(60)||!Gate.Wait(0))return;try{Stop();}finally{Gate.Release();}}
    internal static async Task UnloadAsync(CancellationToken ct){await Gate.WaitAsync(ct);try{Stop();await _shutdown.WaitAsync(ct);}finally{Gate.Release();}}
}
