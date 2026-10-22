using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Starshot;
using Starshot.Features.Codec;
using Starshot.Features.Workspace;
using Starshot.Features.ViewHost;
using Starshot.Helpers;

string temp=Path.Combine(Path.GetTempPath(),"StarshotWorkspaceCore-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
int checks=0;var rows=new List<object>();
void Check(bool condition,string text){checks++;if(!condition)throw new Exception(text);}
async Task<DlcModelStore.Status> Wait(DlcModelStore store,string id){for(int i=0;i<200;i++){var status=await store.StatusAsync(id);if(status.State is not("downloading" or "validating" or "deleting"))return status;await Task.Delay(20);}throw new TimeoutException("Model job");}
try
{
    AppConfig.UserDataFolder=Path.Combine(temp,"data");Directory.CreateDirectory(AppConfig.UserDataFolder);
    // Focus follows the native show message; controller exceptions are isolated
    // from capture publication. No WebView, HWND or model is created here.
    var focus=new DeferredWebFocus();var callbacks=new Queue<Action>();int focused=0,focusErrors=0;
    bool visible=false;bool Enqueue(Action callback){callbacks.Enqueue(callback);return true;}
    void RequestFocus()=>focus.Request(Enqueue,()=>visible,()=>focused++,ex=>focusErrors++);
    RequestFocus();RequestFocus();Check(callbacks.Count==1&&focused==0,"Native Show focus requests coalesce and never run inline");
    visible=true;callbacks.Dequeue()();Check(focused==1,"Deferred handoff observes visibility after Show finishes");
    RequestFocus();visible=false;callbacks.Dequeue()();Check(focused==1,"Hidden/minimized/unready controller is not focused");
    visible=true;RequestFocus();focus.Cancel();RequestFocus();callbacks.Dequeue()();Check(focused==1,"Old controller generation is ignored");
    callbacks.Dequeue()();Check(focused==2,"Old callback cannot clear a newer pending handoff");
    foreach(var failure in new Exception[]{new ArgumentException("MoveFocus E_INVALIDARG"),new InvalidOperationException("Controller closed"),new System.Runtime.InteropServices.COMException("Disconnected")})
    {
        focus.Request(Enqueue,()=>true,()=>throw failure,ex=>focusErrors++);callbacks.Dequeue()();
        RequestFocus();callbacks.Dequeue()();
    }
    Check(focusErrors==3&&focused==5,"Focus errors do not escape capture flow or block later activation");
    focus.Request(Enqueue,()=>throw new InvalidOperationException("Stale controller property"),()=>focused++,ex=>focusErrors++);callbacks.Dequeue()();
    Check(focusErrors==4&&focused==5,"Stale property access is isolated as well");
    focus.Request(_=>false,()=>true,()=>focused++,ex=>focusErrors++);RequestFocus();callbacks.Dequeue()();
    Check(focused==6,"Rejected enqueue does not leave focus stuck pending");
    focus.Request(Enqueue,()=>true,()=>{focused++;RequestFocus();},ex=>focusErrors++);callbacks.Dequeue()();
    Check(callbacks.Count==0&&focused==7,"Reentrant native focus notification cannot schedule a focus loop");
    // Actual workspace consumers (PNG, OCR, GDI and pixel readouts) require BGRA.
    byte[] rgba={255,0,0,255,0,255,0,128,0,0,255,200,245,244,235,255,221,243,105,255};
    byte[] before=(byte[])rgba.Clone(),bgra=new byte[rgba.Length];
    WorkspacePixels.CopyBgra8(rgba,bgra,WorkspacePixels.ByteOrder.Rgba8);
    Check(bgra.SequenceEqual(new byte[]{0,0,255,255,0,255,0,128,255,0,0,200,235,244,245,255,105,243,221,255}),"RGBA preview becomes BGRA: primaries, warm paper and Starshot accent");
    Check(rgba.SequenceEqual(before),"Preview normalization does not mutate source pixels");
    byte[] copy=new byte[bgra.Length];WorkspacePixels.CopyBgra8(bgra,copy,WorkspacePixels.ByteOrder.Bgra8);
    Check(copy.SequenceEqual(bgra),"Existing SDR BGRA must not be swapped twice");
    Check(copy[7]==128&&copy[11]==200,"Alpha survives channel normalization");
    try{WorkspacePixels.CopyBgra8(new byte[3],new byte[3],WorkspacePixels.ByteOrder.Rgba8);throw new Exception("Invalid packing accepted");}catch(ArgumentException){Check(true,"Reject incomplete pixels");}
    foreach(float white in new[]{80f,203f,320f,1000f})Check(Math.Abs(UhdrReferenceWhite.Read(UhdrReferenceWhite.CreateExif(white))-white)<.001,"UHDR reference-white EXIF roundtrip");
    Check(UhdrReferenceWhite.Read([])==203,"External UHDR reference default");
    var corrupt=UhdrReferenceWhite.CreateExif(320);corrupt[24]=255;Check(UhdrReferenceWhite.Read(corrupt)==203,"Untrusted EXIF offset must be bounded");
    var square=ImageMask.Polygon(16,12,new Vector2[]{new(2,3),new(12,3),new(12,10),new(2,10)});
    Check(square.Count(b=>b==255)==70,"Polygon exact area");Check(square[0]==0&&square[4*16+4]==255,"Polygon boundaries");
    var concave=ImageMask.Polygon(10,10,new Vector2[]{new(0,0),new(10,0),new(10,5),new(5,5),new(5,10),new(0,10)});
    Check(concave.Count(b=>b==255)==75,"Concave polygon even-odd fill");
    byte[] pixels=new byte[16*12*4];for(int i=0;i<pixels.Length;i+=4){pixels[i]=pixels[i+1]=pixels[i+2]=255;pixels[i+3]=255;}
    for(int y=3;y<10;y++)for(int x=2;x<12;x++){int p=(y*16+x)*4;pixels[p]=10;pixels[p+1]=20;pixels[p+2]=30;}
    var wand=ImageMask.MagicWand(pixels,16,12,0,0,20);Check(wand.SequenceEqual(square),"Connected background removal");
    var transparent=ImageMask.Apply(pixels,wand);Check(transparent[3]==0&&transparent[(4*16+4)*4+3]==255,"Alpha output");
    var soft=ImageMask.Apply(new byte[]{200,100,50,255},new byte[]{128});Check(soft.SequenceEqual(new byte[]{100,50,25,128}),"Premultiplied soft alpha");
    byte[] fp16=new byte[6*8];
    // neutral, sRGB red, P3 red transformed to scRGB, BT.2020 red, black, invalid.
    var colors=new[]{new Vector3(2.5f),new Vector3(1,0,0),new Vector3(1.224745f,-.042058f,-.019642f),new Vector3(1.660491f,-.12455f,-.018151f),Vector3.Zero,new Vector3(float.NaN)};
    for(int i=0;i<colors.Length;i++)for(int c=0;c<4;c++)BinaryPrimitives.WriteUInt16LittleEndian(fp16.AsSpan(i*8+c*2),BitConverter.HalfToUInt16Bits((Half)(c==3?1:c==0?colors[i].X:c==1?colors[i].Y:colors[i].Z)));
    var gamut=GamutAnalysis.Analyze(fp16,6,1);Check(gamut.Samples==4&&gamut.Black==1&&gamut.Invalid==1,"Gamut denominator excludes black/invalid");
    Check(Math.Abs(gamut.Srgb-50)<.1&&Math.Abs(gamut.DisplayP3Only-25)<.1&&Math.Abs(gamut.Bt2020Only-25)<.1,"Gamut nested regions, negative RGB preserved");
    var map=GamutAnalysis.CreateMap(fp16,6,1);Check(map.Width==6&&map.Height==1&&map.Pixels.Length==24,"Gamut region map geometry");
    Check(map.Pixels[0]==map.Pixels[4]&&map.Pixels[8]!=map.Pixels[0]&&map.Pixels[12]!=map.Pixels[8],"Gamut map nested-region colors");
    Check(map.Pixels[19]==255&&map.Pixels[23]==255,"Black and invalid pixels remain opaque diagnostic colors");
    var streaming = new GamutAnalysis.Accumulator(6,1); streaming.AppendRows(fp16,0,1,48,default);
    var streamed = streaming.Finish();
    Check(streamed.Stats.Srgb==gamut.Srgb&&streamed.Stats.DisplayP3Only==gamut.DisplayP3Only&&streamed.Stats.Bt2020Only==gamut.Bt2020Only&&streamed.Stats.Density.SequenceEqual(gamut.Density),"Streaming gamut preserves existing negative-channel statistics");
    Check(streamed.Map.Pixels.SequenceEqual(map.Pixels),"Streaming gamut map matches existing palette and coordinates");
    var diagram=GamutAnalysis.CreateDiagram(streamed.Stats);Check(diagram.Width==256&&diagram.Height==256&&diagram.Pixels.Where((_,i)=>i%4==3).All(b=>b==255),"Bounded opaque CIE diagram");
    byte[] twoRows=new byte[112];fp16.CopyTo(twoRows,0);fp16.CopyTo(twoRows,56);
    var stripes=new GamutAnalysis.Accumulator(6,2);stripes.AppendRows(twoRows.AsSpan(0,56),0,1,56,default);stripes.AppendRows(twoRows.AsSpan(56),1,1,56,default);
    Check(stripes.Finish().Stats.Samples==8,"Padded FP16 stripes retain row coordinates");
    try{new GamutAnalysis.Accumulator(6,1).AppendRows(fp16,0,1,48,new CancellationToken(true));throw new Exception("Cancellation ignored");}catch(OperationCanceledException){Check(true,"Gamut CPU calculation observes cancellation");}
    try{ImageMask.Polygon(10,10,new Vector2[]{new(0,0),new(float.MaxValue,2),new(3,4)});throw new Exception("Unbounded point accepted");}catch(ArgumentException){Check(true,"Untrusted polygon coordinates bounded");}
    byte[] data=Enumerable.Range(0,2048).Select(i=>(byte)i).ToArray();string hash=Convert.ToHexString(SHA256.HashData(data));
    var asset=new DlcModelStore.Model("test","Test","test",data.Length,hash,"https://example.invalid/model","weights.bin","MIT","test");
    using var http=new HttpClient(new Handler(data));var store=new DlcModelStore(()=>Path.Combine(temp,"ok"),http,new[]{asset});
    Check(!(await store.StatusAsync("test")).Installed,"Absent model");await store.StartAsync("test");Check((await Wait(store,"test")).Installed,"Download, hash, atomic publish");
    Check((await store.StatusAsync("test",true)).Installed,"Forced integrity verification");
    await File.WriteAllBytesAsync(store.PathFor("test"),new byte[data.Length]);Check((await store.StatusAsync("test",true)).State=="corrupt","Corruption detection");
    await store.DeleteAsync("test");Check(!File.Exists(store.PathFor("test")),"Delete model");
    using var badHttp=new HttpClient(new Handler(new byte[data.Length]));var bad=new DlcModelStore(()=>Path.Combine(temp,"bad"),badHttp,new[]{asset});await bad.StartAsync("test");Check((await Wait(bad,"test")).State=="error"&&!File.Exists(bad.PathFor("test")),"Bad hash not published");Check(!Directory.EnumerateFiles(Path.Combine(temp,"bad")).Any(),"Bad download staging cleaned");
    using var offlineHttp=new HttpClient(new Handler(data,fail:true));var offline=new DlcModelStore(()=>Path.Combine(temp,"offline"),offlineHttp,new[]{asset});await offline.StartAsync("test");Check((await Wait(offline,"test")).State=="error","Offline failure explicit");
    using var slowHttp=new HttpClient(new Handler(data,delay:5000));var slow=new DlcModelStore(()=>Path.Combine(temp,"cancel"),slowHttp,new[]{asset});await slow.StartAsync("test");await slow.CancelAsync("test");Check((await slow.StatusAsync("test")).State=="canceled","Immediate cancellation terminates job");Check(!File.Exists(slow.PathFor("test")),"Canceled download not published");
    rows.Add(new{kind="math-and-store",checks});
    if(args.Contains("--inference"))
    {
        string source=Environment.GetEnvironmentVariable("STARSHOT_TEST_MODEL_CACHE") ?? throw new Exception("STARSHOT_TEST_MODEL_CACHE required");
        string modelDirectory=Path.Combine(AppConfig.UserDataFolder,"Models","Library");Directory.CreateDirectory(modelDirectory);
        foreach(var model in DlcModelStore.Catalog){string input=Path.Combine(source,model.File);File.Copy(input,DlcModelStore.Current.PathFor(model.Id));Check((await DlcModelStore.Current.StatusAsync(model.Id,true)).Installed,"Real model SHA-256 "+model.Id);}
        long memory=Process.GetCurrentProcess().PrivateMemorySize64;
        for(int cycle=0;cycle<2;cycle++)
        {
            var watch=Stopwatch.StartNew();string translated=await OcrTranslationClient.TranslateAsync(cycle==0?"The screenshot preserves the HDR highlights.":"星光截图保留了细腻的高光层次。",cycle==0?"简体中文":"英语",CancellationToken.None);
            Check(!string.IsNullOrWhiteSpace(translated),"Actual local translation");rows.Add(new{kind="translation",cycle,elapsedMs=watch.ElapsedMilliseconds,text=translated,privateBytes=Process.GetCurrentProcess().PrivateMemorySize64,beforePrivate=memory});
        }
        var formatted=await OcrTranslationClient.TranslateFormattedAsync(new[]{new OcrTranslationClient.Segment("a","Hello world."),new OcrTranslationClient.Segment("b","Keep the highlights.")},"简体中文",CancellationToken.None);
        Check(formatted.Count==2&&formatted[0].Id=="a"&&formatted[1].Id=="b","Local formatted IDs");rows.Add(new{kind="formatted",formatted});
        await OfflineTranslation.UnloadAsync(CancellationToken.None);Check(!OfflineTranslation.Loaded,"Local worker unloaded");
        var photo=new byte[256*192*4];for(int y=0;y<192;y++)for(int x=0;x<256;x++){int i=(y*256+x)*4;bool foreground=Math.Pow((x-128)/58d,2)+Math.Pow((y-96)/72d,2)<1;photo[i]=(byte)(foreground?40:245);photo[i+1]=(byte)(foreground?80:245);photo[i+2]=(byte)(foreground?200:245);photo[i+3]=255;}
        var segmentationWatch=Stopwatch.StartNew();var mask=await BackgroundRemoval.CreateMaskAsync(photo,256,192,CancellationToken.None);Check(mask.Length==256*192&&mask.Max()>200&&mask.Min()<50,"Real ONNX foreground mask");rows.Add(new{kind="segmentation",elapsedMs=segmentationWatch.ElapsedMilliseconds,center=mask[96*256+128],corner=mask[0],privateBytes=Process.GetCurrentProcess().PrivateMemorySize64});
    }
    Console.WriteLine(JsonSerializer.Serialize(new{ok=true,checks,rows},new JsonSerializerOptions{WriteIndented=true}));
}
finally{await OfflineTranslation.UnloadAsync(CancellationToken.None);Directory.Delete(temp,true);}

sealed class Handler(byte[] data,bool fail=false,int delay=0):HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {if(delay>0)await Task.Delay(delay,ct);if(fail)throw new HttpRequestException("offline");return new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(data)};}
}
