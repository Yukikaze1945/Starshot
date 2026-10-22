using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Starshot.Features.Screenshot;
using Starshot.Features.Codec;
using Starshot.Features.Workspace;
using Starshot.Helpers;

namespace Starshot.Features.WebUI;

internal sealed partial class WebUiBridge
{
    private readonly SemaphoreSlim _workspaceGate=new(1);
    private WorkspaceDocument? _document,_comparison;
    private bool _workspaceClosed;
    private JsonElement? _captureDraft;
    private WorkspaceDocument Document => _document ?? throw new InvalidOperationException("请先打开图片。");
    private async Task<object?> WorkspaceAsync(string method,JsonElement p,CancellationToken ct)
    {
        if(method is "workspace.formula" or "workspace.layout" || method.StartsWith("workspace.page.",StringComparison.Ordinal) || method=="workspace.formula.copyWord")return await DocumentOcrAsync(method,p,ct);
        await _workspaceGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if(_workspaceClosed)throw new ObjectDisposedException(nameof(WebUiBridge));
            switch(method)
            {
                case "workspace.open":
                case "workspace.compare":
                {
                    string id=Text(p,"id",100);
                    if(StaticVideoMetadata.IsVideo(ResolveId(id)))throw new ArgumentException("请打开视频的伴随图片进行编辑。");
                    var next=await WorkspaceDocument.LoadAsync(ResolveId(id),ct);
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        if(method=="workspace.open"){ClosePageJob();_document?.Dispose();_comparison?.Dispose();_comparison=null;_document=next;}
                        else {_comparison?.Dispose();_comparison=next;}
                        return await WorkspaceInfoAsync(next,ct);
                    }
                    catch {if(_document!=next && _comparison!=next)next.Dispose();throw;}
                }
                case "workspace.pending":
                {
                    if(!_window.IsCaptureEditor)throw new InvalidOperationException("选区编辑只能在截图编辑层中打开。");
                    var next=_window.TakeCaptureDocument();
                    if(next is null && _document is not null)return await WorkspaceInfoAsync(_document,ct);
                    if(next is null)throw new InvalidOperationException("当前选区已关闭，请重新框选。");
                    ClosePageJob();_document?.Dispose();_comparison?.Dispose();_comparison=null;_document=next;
                    return await WorkspaceInfoAsync(next,ct);
                }
                case "workspace.close": ClosePageJob();_document?.Dispose();_comparison?.Dispose();_document=null;_comparison=null;_captureDraft=null;return null;
                case "workspace.pixel":return Document.Pixel(Range(p.GetProperty("x"),0,Document.Width-1),Range(p.GetProperty("y"),0,Document.Height-1));
                case "workspace.difference":
                {
                    var a=Document;var b=_comparison ?? throw new InvalidOperationException("请先选择第二张图");
                    if(a.Width!=b.Width||a.Height!=b.Height)return new{available=false,reason="图像尺寸不同，不计算逐像素误差。"};
                    if((a.HdrPixels is null)!=(b.HdrPixels is null))return new{available=false,reason="HDR 与 SDR 的信号量程不同，不混算误差。"};
                    return await Task.Run<object>(()=>
                    {
                        bool hdr=a.HdrPixels is not null;int step=Math.Max(1,(int)Math.Ceiling(Math.Sqrt((long)a.Width*a.Height/1_000_000d)));
                        double sum=0,squares=0,max=0;long count=0;
                        float Channel(WorkspaceDocument d,int pixel,int c)
                        {
                            if(hdr)return (float)BitConverter.UInt16BitsToHalf(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(d.HdrPixels!.AsSpan(pixel*8+c*2)))*80;
                            float v=d.Bgra[pixel*4+2-c]/255f;return v<=.04045f ? v/12.92f : MathF.Pow((v+.055f)/1.055f,2.4f);
                        }
                        for(int y=0;y<a.Height;y+=step){ct.ThrowIfCancellationRequested();for(int x=0;x<a.Width;x+=step)for(int c=0;c<3;c++)
                        {int i=y*a.Width+x;double difference=Math.Abs(Channel(a,i,c)-Channel(b,i,c));if(!double.IsFinite(difference))continue;sum+=difference;squares+=difference*difference;max=Math.Max(max,difference);count++;}}
                        return new{available=true,units=hdr?"nits / linear RGB":"relative linear RGB",mae=sum/Math.Max(1,count),rmse=Math.Sqrt(squares/Math.Max(1,count)),max,samples=count/3,approximate=step>1};
                    },ct);
                }
                case "workspace.mask":
                {
                    var d=Document;
                    string kind=Text(p,"kind",20);
                    byte[]? mask;
                    if(kind=="clear")mask=null;
                    else if(kind=="polygon")
                    {
                        var points=p.GetProperty("points").EnumerateArray().Take(20001).Select(v=>new Vector2(v.GetProperty("x").GetSingle(),v.GetProperty("y").GetSingle())).ToArray();
                        mask=await Task.Run(()=>ImageMask.Polygon(d.Width,d.Height,points,ct),ct);
                    }
                    else if(kind=="wand")mask=await Task.Run(()=>ImageMask.MagicWand(d.Bgra,d.Width,d.Height,
                        Range(p.GetProperty("x"),0,d.Width-1),Range(p.GetProperty("y"),0,d.Height-1),Range(p.GetProperty("tolerance"),0,255),true,ct),ct);
                    else if(kind=="smart")mask=await BackgroundRemoval.CreateMaskAsync(d.Bgra,d.Width,d.Height,ct);
                    else throw new ArgumentException("未知蒙版工具");
                    string preview=await WorkspaceDocument.PngAsync(mask is null ? d.Bgra : ImageMask.Apply(d.Bgra,mask),d.Width,d.Height,4096,ct);
                    ct.ThrowIfCancellationRequested();d.Mask=mask;
                    return new{src=preview,masked=mask is not null};
                }
                case "workspace.ocr":
                {
                    var d=Document;
                    var lines=await Task.Run(()=>OcrHelper.RecognizeAsync(d.Bgra,d.Width,d.Height,1,ct),ct);
                    return RegionTranslation.TextLines(lines ?? []).Select((line,i)=>new{id=i.ToString(),text=line.Text,x=line.Rect.X,y=line.Rect.Y,width=line.Rect.Width,height=line.Rect.Height}).ToArray();
                }
                case "workspace.qr":
                {
                    var d=Document;
                    await QrCodeResultWindow.ShowAsync(d.Bgra,d.Width,d.Height,ct);
                    return null;
                }
                case "workspace.analysis.close":Document.Analysis?.Dispose();Document.Analysis=null;return null;
                case "workspace.analysis":
                {
                    var d=Document; var pixels=d.HdrPixels ?? throw new InvalidOperationException("绝对亮度分析需要原始 FP16 HDR，普通 SDR 不提供 nits。");
                    HdrLuminanceAnalysis? working=null;
                    try
                    {
                        if(d.Analysis is null)
                        {
                            working=new(d.Width,d.Height);
                            var analysis=working;
                            await Task.Run(()=>{analysis.AppendRows(pixels,0,d.Height,d.Width*8);analysis.Finish(ct);},ct);
                            ct.ThrowIfCancellationRequested();d.Analysis=working;working=null;
                        }
                        float scale=p.TryGetProperty("scale",out var value) ? Range(value,0,10000) : 0;
                        var a=d.Analysis;
                        if(scale<=0)scale=HdrLuminanceAnalysis.SelectScale(a!.Stats.Max);
                        var gamut=await Task.Run(()=>GamutAnalysis.Analyze(pixels,d.Width,d.Height,ct),ct);
                        var regions=await Task.Run(()=>GamutAnalysis.CreateMap(pixels,d.Width,d.Height,ct),ct);
                        var wave=await Task.Run(()=>a!.CreateWaveform(scale,512,200,ct),ct);
                        var heat=await Task.Run(()=>a!.CreateHeatmap(1024,ct),ct);
                        return new{stats=a!.Stats,referenceWhite=d.ReferenceWhite,approximate=a.SampleStep>1,sampleStep=a.SampleStep,
                            scale,waveform=await WorkspaceDocument.PngAsync(wave.Pixels,wave.Width,wave.Height,0,ct),
                            heatmap=await WorkspaceDocument.PngAsync(heat.Pixels,heat.Width,heat.Height,0,ct),
                            gamutMap=await WorkspaceDocument.PngAsync(regions.Pixels,regions.Width,regions.Height,0,ct),gamut};
                    }
                    finally{working?.Dispose();}
                }
                case "workspace.export":
                case "workspace.copy":
                {
                    var d=Document; byte[] pixels=d.Bgra;
                    if(p.TryGetProperty("overlays",out var overlays) && overlays.ValueKind==JsonValueKind.Array)
                        pixels=await Task.Run(()=>WorkspaceTextOverlay.Render(d.Bgra,d.Width,d.Height,overlays,ct),ct);
                    if(d.Mask is not null)pixels=await Task.Run(()=>ImageMask.Apply(pixels,d.Mask),ct);
                    if(method=="workspace.copy") {ClipboardHelper.SetBitmapDib(d.Width,d.Height,pixels);return null;}
                    string? path=await FileDialogHelper.OpenSaveFileDialogAsync(_window.WindowHandle,
                        Path.GetFileNameWithoutExtension(d.Name)+"-edited",("透明 SDR PNG",".png"));
                    if(path is null)return null;
                    string data=await WorkspaceDocument.PngAsync(pixels,d.Width,d.Height,0,ct);
                    byte[] encoded=Convert.FromBase64String(data[(data.IndexOf(',')+1)..]);
                    string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                    try {await File.WriteAllBytesAsync(temporary,encoded,ct);ct.ThrowIfCancellationRequested();File.Move(temporary,path,true);}
                    finally{if(File.Exists(temporary))File.Delete(temporary);}
                    return RegisterFile(path);
                }
                case "workspace.draft.load":
                {
                    if(_window.IsCaptureEditor)return _captureDraft;
                    string path=DraftPath(Document);
                    if(!File.Exists(path))return null;
                    var info=new FileInfo(path);if(info.Length>2_000_000)throw new InvalidDataException("草稿过大");
                    using var draft=JsonDocument.Parse(await File.ReadAllTextAsync(path,ct));return draft.RootElement.Clone();
                }
                case "workspace.draft.save":
                {
                    string json=p.GetProperty("draft").GetRawText();if(json.Length>2_000_000)throw new ArgumentException("草稿过大");
                    if(_window.IsCaptureEditor){_captureDraft=p.GetProperty("draft").Clone();return null;}
                    string path=DraftPath(Document);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    string temporary=path+".tmp";try{await File.WriteAllTextAsync(temporary,json,ct);File.Move(temporary,path,true);}finally{if(File.Exists(temporary))File.Delete(temporary);}return null;
                }
                default:throw new ArgumentException("未知图像工作区操作。");
            }
        }
        finally{_workspaceGate.Release();}
    }
    private static string DraftPath(WorkspaceDocument d)=>Path.Combine(AppConfig.UserDataFolder,"ImageDrafts",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(d.Fingerprint)))+".json");
    private static async Task<object> WorkspaceInfoAsync(WorkspaceDocument d,CancellationToken ct)=>new{
        name=d.Name,width=d.Width,height=d.Height,hdr=d.HdrPixels is not null,carrier=d.Carrier,referenceWhite=d.ReferenceWhite,
        initialTool=d.InitialTool,fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(d.Fingerprint))),src=await WorkspaceDocument.PngAsync(d.Bgra,d.Width,d.Height,4096,ct),previewMax=4096};
    private void CloseWorkspace(){ClosePageJob();_workspaceClosed=true;_document?.Dispose();_comparison?.Dispose();_document=null;_comparison=null;_captureDraft=null;}
}
