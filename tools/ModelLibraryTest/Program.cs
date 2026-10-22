using System.Net;
using System.Security.Cryptography;
using System.Text;
using Starshot.Helpers;

int checks=0;void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
string root=Path.Combine(Path.GetTempPath(),"Starshot-ModelLibrary-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
byte[] Gguf(string name="Index-Translate-2B",string arch="qwen35")
{
    using var stream=new MemoryStream();using var w=new BinaryWriter(stream,Encoding.UTF8,true);
    void Text(string value){byte[] bytes=Encoding.UTF8.GetBytes(value);w.Write((ulong)bytes.Length);w.Write(bytes);}
    w.Write(0x46554747u);w.Write(3u);w.Write(1ul);w.Write(2ul);
    foreach(var (key,value) in new[]{("general.architecture",arch),("general.name",name)}){Text(key);w.Write(8u);Text(value);}
    Text("weight");w.Write(1u);w.Write(1ul);w.Write(0u);w.Write(0ul);while(stream.Position%32!=0)w.Write((byte)0);w.Write(1f);return stream.ToArray();
}
byte[] onnx=[8,10,18,8,83,116,97,114,115,104,111,116,58,4,18,2,120,121];
async Task<DlcModelStore.Status> Done(DlcModelStore store,string id)
{for(int i=0;i<1000;i++){var s=await store.StatusAsync(id);if(s.State is not("downloading" or "validating" or "importing" or "deleting"))return s;await Task.Delay(5);}throw new TimeoutException();}
try
{
    var known=DlcModelStore.Catalog[0];
    Check(ModelDownloadSources.Resolve(known.Id,known.Url,"auto").First().Id=="modelscope","Automatic domestic-first route");
    Check(ModelDownloadSources.Resolve(known.Id,known.Url,"hf-mirror").Single().Url!.StartsWith("https://hf-mirror.com/"),"Pinned HF mirror URL");
    Check(ModelDownloadSources.For("u2netp","https://example.invalid").Single(s=>s.Id=="modelscope").Url is null,"Unavailable source never fabricated");
    string local=Path.Combine(root,"downloads");Directory.CreateDirectory(local);string input=Path.Combine(local,known.File);await File.WriteAllBytesAsync(input,Gguf());
    Check(LocalModelFiles.CheckFormat(input,known.Id) is null,"Data-only GGUF metadata and tensor directory inspection");
    byte[] exactData=Gguf();var exact=known with{Bytes=exactData.Length,Sha256=Convert.ToHexString(SHA256.HashData(exactData))};
    using var http=new HttpClient(new Fake(_=>Gguf()));
    var verified=new DlcModelStore(()=>Path.Combine(root,"exact"),http,[exact]);await verified.StartAsync(known.Id);
    var verifiedStatus=await Done(verified,known.Id);Check(verifiedStatus.Installed&&verifiedStatus.Warning is null,"Exact registered hash still recognized separately");
    var store=new DlcModelStore(()=>Path.Combine(root,"managed"),http);
    await store.ImportAsync(known.Id,input);var result=await Done(store,known.Id);
    Check(result.Installed&&result.Warning is not null&&result.ActualSha256 is not null,"Different hash remains usable with warning and actual digest");
    Check(await store.RequireAsync(known.Id,CancellationToken.None)==store.PathFor(known.Id),"Warning does not prevent runtime path resolution");
    var restarted=new DlcModelStore(()=>Path.Combine(root,"managed"),http);
    Check((await restarted.StatusAsync(known.Id)).Warning is not null,"Warning survives fresh store/restart");
    await store.DeleteAsync(known.Id);Check(File.Exists(input)&&!File.Exists(store.PathFor(known.Id)),"Delete only managed copy, preserve original");
    await File.WriteAllBytesAsync(input,Gguf("Other model"));await store.ImportAsync(known.Id,input);Check(!(await Done(store,known.Id)).Installed,"Wrong model family rejected without inference");
    await File.WriteAllBytesAsync(input,Gguf("Index-Translate-9B"));Check(LocalModelFiles.CheckFormat(input,known.Id) is not null,"Larger 9B is not silently substituted for 2B");
    await File.WriteAllTextAsync(input,"<html>403 forbidden</html>");await store.ImportAsync(known.Id,input);Check(!(await Done(store,known.Id)).Installed,"HTML error page is not a model");
    await File.WriteAllBytesAsync(input,Gguf()[..^4]);Check(LocalModelFiles.CheckFormat(input,known.Id) is not null,"Truncated tensor data rejected");
    await File.WriteAllBytesAsync(input,Gguf());
    int attempts=0;using var fallbackHttp=new HttpClient(new Fake(_=>{if(++attempts==1)throw new HttpRequestException("unreachable");return Gguf();}));
    var fallback=new DlcModelStore(()=>Path.Combine(root,"fallback"),fallbackHttp);
    await fallback.StartAsync(known.Id);var fallbackStatus=await Done(fallback,known.Id);
    Check(attempts==2&&fallbackStatus.Installed&&fallbackStatus.ActiveSource!.Contains("镜像"),"Automatic network fallback and warning install");
    await fallback.DeleteAsync(known.Id);await fallback.StartAsync(known.Id,source:"huggingface");Check((await Done(fallback,known.Id)).ActiveSource=="Hugging Face","Explicit HF choice used");
    string onnxPath=Path.Combine(local,"u2netp.onnx");await File.WriteAllBytesAsync(onnxPath,onnx);
    Check(LocalModelFiles.CheckFormat(onnxPath,"u2netp") is null,"ONNX protobuf inspected without model session");
    await store.ImportAsync("u2netp",onnxPath);Check((await Done(store,"u2netp")).Installed,"ONNX different hash is usable");
    string ocrFolder=Path.Combine(local,"small");Directory.CreateDirectory(ocrFolder);
    await File.WriteAllBytesAsync(Path.Combine(ocrFolder,"small_det.onnx"),onnx);await File.WriteAllBytesAsync(Path.Combine(ocrFolder,"small_rec.onnx"),onnx);
    await File.WriteAllTextAsync(Path.Combine(ocrFolder,"rec_keys.txt"),"中\n文\na\nb\n");
    int publications=0;Task Publish(Action action,CancellationToken ct){ct.ThrowIfCancellationRequested();action();publications++;return Task.CompletedTask;}
    var ocr=new OcrModelStore(()=>Path.Combine(root,"ocr"),http,Publish);
    ocr.StartImport(ocrFolder);await ocr.WaitForDownloadAsync();var ocrStatus=await ocr.GetStatusAsync(force:true);
    Check(ocrStatus.Installed&&ocrStatus.Warning is not null&&ocrStatus.ActualHashes!.Contains("det.onnx"),"OCR bundle aliases, mismatch warning and digests");
    Check(publications==1,"OCR publication uses existing runtime mutation gate");
    var newOcr=new OcrModelStore(()=>Path.Combine(root,"ocr"),http,Publish);
    Check((await newOcr.GetStatusAsync()).Installed,"Imported OCR bundle persists without eager loading");
    await ocr.DeleteAsync();Check(Directory.Exists(ocrFolder)&&!Directory.Exists(ocr.DirectoryPath),"OCR deletion preserves source directory");
    using var ocrHttp=new HttpClient(new Fake(url=>url.EndsWith("rec_keys.txt")?Encoding.UTF8.GetBytes("中\n文\n"):onnx));
    var downloadOcr=new OcrModelStore(()=>Path.Combine(root,"download-ocr"),ocrHttp,Publish);
    downloadOcr.StartDownload();await downloadOcr.WaitForDownloadAsync();Check((await downloadOcr.GetStatusAsync()).Installed,"Downloaded OCR hash mismatch warns instead of blocking");
    var scan=await LocalModelFiles.ScanAsync([local],CancellationToken.None);
    Check(scan.Items.Select(i=>i.Id).Distinct().Count()==3,"Local scan finds GGUF, ONNX and OCR folders");
    await File.WriteAllBytesAsync(Path.Combine(local,"Index-Translate-2B.mmproj-Q8_0.gguf"),Gguf());
    Check((await LocalModelFiles.ScanAsync([local],CancellationToken.None)).Items.Count(i=>i.Id==known.Id)==1,"Visual projector excluded from text-model discovery");
    using var cancel=new CancellationTokenSource();cancel.Cancel();try{await LocalModelFiles.ScanAsync([local],cancel.Token);throw new Exception("Expected cancellation");}catch(OperationCanceledException){Check(true,"Scan cancellation");}
    Check(!Directory.EnumerateFiles(root,"*.partial",SearchOption.AllDirectories).Any(),"No abandoned partial files");
    using var slowHttp=new HttpClient(new Slow());var canceled=new DlcModelStore(()=>Path.Combine(root,"cancel"),slowHttp);
    await canceled.StartAsync(known.Id);await canceled.CancelAsync(known.Id);
    Check((await canceled.StatusAsync(known.Id)).State=="canceled"&&!File.Exists(canceled.PathFor(known.Id)),"Download cancellation never publishes incomplete data");
    Console.WriteLine($"PASS {checks} model-source/import/format/warning/restart/cleanup checks; synthetic bytes, 0 inference sessions, 0 GPU usage.");
}
finally
{
    string absolute=Path.GetFullPath(root),temp=Path.GetFullPath(Path.GetTempPath());
    if(!absolute.StartsWith(temp,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(absolute).StartsWith("Starshot-ModelLibrary-"))throw new Exception("Invalid test cleanup root");
    Directory.Delete(absolute,true);
}
sealed class Fake(Func<string,byte[]> data):HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message,CancellationToken ct)
    {ct.ThrowIfCancellationRequested();return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(data(message.RequestUri!.ToString()))});}
}
sealed class Slow:HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {await Task.Delay(30000,ct);return new HttpResponseMessage(HttpStatusCode.OK);}
}
