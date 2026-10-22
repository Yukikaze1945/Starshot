using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Starshot.Helpers;

/// <summary>Authenticated loopback-only OpenAI text API. Does not expose llama.cpp management/file routes.</summary>
internal sealed class LocalTranslationApi : IAsyncDisposable
{
    private const int MaxBody=128*1024;
    private readonly WebApplication _app;
    private readonly CancellationTokenSource _lifetime=new();
    private readonly SemaphoreSlim _requests=new(4);
    private readonly byte[] _key;
    private readonly Func<string,HttpMethod,byte[]?,HttpResponse,CancellationToken,Task> _forward;
    private int _active,_disposed;
    internal int Active=>Volatile.Read(ref _active);
    internal int Port{get;}
    internal string BaseUrl=>$"http://127.0.0.1:{Port}/v1";
    private LocalTranslationApi(WebApplication app,int port,string key,
        Func<string,HttpMethod,byte[]?,HttpResponse,CancellationToken,Task> forward)
    {_app=app;Port=port;_key=Encoding.UTF8.GetBytes(key);_forward=forward;}

    internal static async Task<LocalTranslationApi> StartAsync(int port,string key,
        Func<string,HttpMethod,byte[]?,HttpResponse,CancellationToken,Task> forward,CancellationToken ct)
    {
        if(port is <1024 or >65535)throw new ArgumentOutOfRangeException(nameof(port),"端口范围为 1024–65535。");
        if(key.Length<32)throw new ArgumentException("本地 API 密钥过短。");
        var builder=WebApplication.CreateSlimBuilder(new WebApplicationOptions{Args=[],ApplicationName=typeof(LocalTranslationApi).Assembly.GetName().Name});
        builder.Configuration.Sources.Clear(); // Environment/appsettings must not add a non-loopback listener.
        builder.Logging.ClearProviders(); // Do not retain prompts, URLs or credentials in request logs.
        builder.WebHost.ConfigureKestrel(options=>
        {
            options.AddServerHeader=false;
            options.Limits.MaxRequestBodySize=MaxBody;
            options.Limits.RequestHeadersTimeout=TimeSpan.FromSeconds(10);
            options.Limits.MaxRequestHeaderCount=32;
            options.Limits.MaxRequestHeadersTotalSize=16*1024;
            options.Limits.MaxConcurrentConnections=16;
            options.Listen(IPAddress.Loopback,port,listen=>listen.Protocols=HttpProtocols.Http1);
        });
        var app=builder.Build();var api=new LocalTranslationApi(app,port,key,forward);
        app.Run(api.HandleAsync);
        try{await app.StartAsync(ct);return api;}
        catch{await api.DisposeAsync();throw;}
    }

    private async Task HandleAsync(HttpContext context)
    {
        var r=context.Request;var response=context.Response;
        response.Headers.CacheControl="no-store";
        if(context.Connection.RemoteIpAddress is not {} ip || !IPAddress.IsLoopback(ip)
            || r.Host.Port!=Port || (r.Host.Host!="127.0.0.1" && r.Host.Host!="localhost") || r.Headers.ContainsKey("Origin"))
        {await ErrorAsync(context,403,"仅接受本机应用调用，不开放浏览器跨域访问。");return;}
        string authorization=r.Headers.Authorization.ToString();
        string supplied=authorization.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase)?authorization[7..]:"";
        if(supplied.Length>128 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied),_key))
        {response.Headers.WWWAuthenticate="Bearer";await ErrorAsync(context,401,"需要有效的 API Key。");return;}
        string path=r.Path.Value??"";
        bool get=r.Method=="GET"&&path=="/v1/models";
        if(!get && !(r.Method=="POST"&&(path=="/v1/chat/completions"||path=="/v1/completions")))
        {await ErrorAsync(context,404,"不支持的 API 路径或方法。");return;}
        if(!await _requests.WaitAsync(0))
        {response.Headers.RetryAfter="1";await ErrorAsync(context,429,"请求过多，请稍后重试。");return;}
        Interlocked.Increment(ref _active);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token,context.RequestAborted);
        linked.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            byte[]? body=null;
            if(!get)
            {
                if(r.ContentLength>MaxBody){await ErrorAsync(context,413,"请求超过 128 KiB。");return;}
                if(r.ContentType is null || !r.ContentType.StartsWith("application/json",StringComparison.OrdinalIgnoreCase))
                {await ErrorAsync(context,415,"请使用 application/json。");return;}
                using var buffer=new MemoryStream();await r.Body.CopyToAsync(buffer,linked.Token);
                if(buffer.Length>MaxBody){await ErrorAsync(context,413,"请求超过 128 KiB。");return;}
                body=PreparePayload(buffer.ToArray(),path);
            }
            await _forward(path,get?HttpMethod.Get:HttpMethod.Post,body,response,linked.Token);
        }
        catch(OperationCanceledException)
        {if(!response.HasStarted && !context.RequestAborted.IsCancellationRequested)await ErrorAsync(context,503,"请求已取消或模型正在卸载。");else context.Abort();}
        catch(Microsoft.AspNetCore.Http.BadHttpRequestException ex){if(!response.HasStarted)await ErrorAsync(context,ex.StatusCode,"请求体无效或过大。");else context.Abort();}
        catch(Exception ex)when(ex is JsonException or ArgumentException)
        {if(!response.HasStarted)await ErrorAsync(context,400,ex is ArgumentException?ex.Message:"JSON 格式不正确。");else context.Abort();}
        catch(Exception)
        {if(!response.HasStarted)await ErrorAsync(context,503,"本地模型暂不可用，请在 Starshot 中检查加载状态。");else context.Abort();}
        finally{Interlocked.Decrement(ref _active);_requests.Release();}
    }

    internal static byte[] PreparePayload(byte[] data,string path)
    {
        using var parsed=JsonDocument.Parse(data,new JsonDocumentOptions{MaxDepth=32});
        var root=parsed.RootElement;
        if(root.ValueKind!=JsonValueKind.Object)throw new ArgumentException("请求必须是 JSON 对象。");
        if(root.TryGetProperty("model",out var model) && (model.ValueKind!=JsonValueKind.String||model.GetString()!="Index-Translate-2B"))
            throw new ArgumentException("模型名应为 Index-Translate-2B。");
        var output=new JsonObject{["model"]="Index-Translate-2B",["temperature"]=0};
        if(path=="/v1/chat/completions")
        {
            if(!root.TryGetProperty("messages",out var messages)||messages.ValueKind!=JsonValueKind.Array||messages.GetArrayLength() is <1 or >256)
                throw new ArgumentException("messages 需要 1–256 条文本消息。");
            foreach(var message in messages.EnumerateArray())
            {
                if(message.ValueKind!=JsonValueKind.Object||!message.TryGetProperty("role",out var role)||role.ValueKind!=JsonValueKind.String
                    ||role.GetString() is not("system" or "user" or "assistant" or "developer")
                    ||!message.TryGetProperty("content",out var content)||content.ValueKind!=JsonValueKind.String)
                    throw new ArgumentException("仅支持纯文本消息，不支持图像、远程资源或工具调用。");
            }
            output["messages"]=JsonNode.Parse(messages.GetRawText());
        }
        else
        {
            if(!root.TryGetProperty("prompt",out var prompt)||prompt.ValueKind!=JsonValueKind.String)throw new ArgumentException("prompt 必须是文本。");
            output["prompt"]=prompt.GetString();
        }
        // An allowlist prevents model-management, file/URL loading and backend configuration through payloads.
        foreach(string field in new[]{"temperature","top_p","seed","stop","frequency_penalty","presence_penalty","stream_options"})
            if(root.TryGetProperty(field,out var value))output[field]=JsonNode.Parse(value.GetRawText());
        int limit=2048;
        if(root.TryGetProperty("max_tokens",out var tokens)||root.TryGetProperty("max_completion_tokens",out tokens))
            if(tokens.ValueKind!=JsonValueKind.Number||!tokens.TryGetInt32(out limit)||limit is <1 or >2048)throw new ArgumentException("max_tokens 范围为 1–2048。");
        bool stream=false;
        if(root.TryGetProperty("stream",out var streaming))
        {if(streaming.ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw new ArgumentException("stream 必须为布尔值。");stream=streaming.GetBoolean();}
        if(root.TryGetProperty("n",out var n)&&(n.ValueKind!=JsonValueKind.Number||!n.TryGetInt32(out int count)||count!=1))throw new ArgumentException("一次只生成一份结果（n=1）。");
        output["max_tokens"]=limit;output["stream"]=stream;
        output["chat_template_kwargs"]=new JsonObject{["enable_thinking"]=false};
        return Encoding.UTF8.GetBytes(output.ToJsonString());
    }
    private static async Task ErrorAsync(HttpContext context,int status,string message)
    {
        context.Response.StatusCode=status;context.Response.ContentType="application/json; charset=utf-8";
        if(status==413)context.Response.Headers.Connection="close";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new{error=new{message,type="starshot_local_api",code=status}}));
    }
    internal void Abort(){if(Volatile.Read(ref _disposed)==0)_lifetime.Cancel();}
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref _disposed,1)!=0)return;
        _lifetime.Cancel();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try{await _app.StopAsync(timeout.Token);}catch(OperationCanceledException){}
        await _app.DisposeAsync();
        _lifetime.Dispose();CryptographicOperations.ZeroMemory(_key);
        // Pending request finally blocks may still release their semaphore after a forced disconnect.
    }
}
