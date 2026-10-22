using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Starshot.Helpers;

namespace Starshot.Features.DocumentOcr;

/// <summary>One CPU model family at a time. Sessions are never disposed during Run.</summary>
internal static class DocumentInference
{
    private static readonly SemaphoreSlim Gate=new(1);
    private static readonly Dictionary<string,InferenceSession> Sessions=new();
    private static string? _family,_directory;
    private static long _lastUse;
    private static readonly Timer Idle=new(_=>RetireIdle(),null,10000,10000);
    static DocumentInference()=>AppDomain.CurrentDomain.ProcessExit+=(_,_)=>{if(Gate.Wait(0)){try{Retire();}finally{Gate.Release();}}};
    internal static async Task UnloadAsync(CancellationToken ct){await Gate.WaitAsync(ct);try{Retire();}finally{Gate.Release();}}
    private static void RetireIdle(){if(!Gate.Wait(0))return;try{if(Stopwatch.GetElapsedTime(_lastUse).TotalSeconds>=60)Retire();}finally{Gate.Release();}}
    private static void Retire(){foreach(var s in Sessions.Values)s.Dispose();Sessions.Clear();_family=_directory=null;}
    private static InferenceSession Session(string file)
    {
        if(Sessions.TryGetValue(file,out var current))return current;
        using var options=new SessionOptions{IntraOpNumThreads=2,InterOpNumThreads=1,ExecutionMode=ExecutionMode.ORT_SEQUENTIAL,LogSeverityLevel=OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR};
        var time=Stopwatch.StartNew();var s=new InferenceSession(Path.Combine(_directory!,file),options);Sessions.Add(file,s);Serilog.Log.Information("[DocumentOCR] family={Family} component={Component} CPU init={Elapsed} ms",_family,file,time.ElapsedMilliseconds);return s;
    }
    private static async Task<T> RunAsync<T>(string family,Func<T> run,CancellationToken ct)
    {
        // Never acquire the DLC lock while holding the inference gate.
        string directory=await DocumentModelStore.Current.RequireAsync(family,ct).ConfigureAwait(false);
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if(_family!=family||_directory!=directory){Retire();_family=family;_directory=directory;}
            ct.ThrowIfCancellationRequested();return await Task.Run(run,ct).ConfigureAwait(false);
        }
        catch(OnnxRuntimeException)when(ct.IsCancellationRequested){Retire();throw new OperationCanceledException(ct);}
        catch{Retire();throw;}
        finally{run=null!;_lastUse=Stopwatch.GetTimestamp();Gate.Release();}
    }
    private static IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Infer(InferenceSession session,CancellationToken ct,params NamedOnnxValue[] values)
    {
        using var options=new RunOptions();using var registration=ct.Register(()=>options.Terminate=true);
        ct.ThrowIfCancellationRequested();return session.Run(values,session.OutputNames,options);
    }
    internal static Task<FormulaResult> FormulaAsync(DocumentImage image,CancellationToken ct)=>RunAsync("formula-rapidlatex",()=>
    {
        var time=Stopwatch.StartNew();image.Validate();var prepared=new GrayImage(image.Gray(ct),image.Width,image.Height).TightPad();
        if(prepared is null)return new FormulaResult("",true,0,time.ElapsedMilliseconds);
        var tensor=ResizeFormula(prepared,ct);
        var encoder=Session("encoder.onnx");using var encoded=Infer(encoder,ct,NamedOnnxValue.CreateFromTensor(encoder.InputNames[0],tensor!));
        var context=encoded.First().AsTensor<float>();var decoder=Session("decoder.onnx");
        if(decoder.InputNames.Count!=3)throw new InvalidDataException("公式 decoder 模型接口不兼容。");
        var tokens=new List<long>{1};bool complete=false;
        for(int k=0;k<512;k++)
        {
            ct.ThrowIfCancellationRequested();var ids=new DenseTensor<long>(tokens.ToArray(),new[]{1,tokens.Count});
            var mask=new DenseTensor<bool>(Enumerable.Repeat(true,tokens.Count).ToArray(),new[]{1,tokens.Count});
            using var output=Infer(decoder,ct,NamedOnnxValue.CreateFromTensor(decoder.InputNames[0],ids),NamedOnnxValue.CreateFromTensor(decoder.InputNames[1],mask),NamedOnnxValue.CreateFromTensor(decoder.InputNames[2],context));
            var logits=output.First().AsTensor<float>();int size=logits.Dimensions[^1];int next;
            if(logits is DenseTensor<float> dense)next=ArgMax(dense.Buffer.Span[^size..]);else{var values=logits.ToArray();next=ArgMax(values.AsSpan(values.Length-size));}
            tokens.Add(next);if(next==2){complete=true;break;}
        }
        string latex=DecodeTokens(Path.Combine(_directory!,"tokenizer.json"),tokens.Skip(1));
        Serilog.Log.Information("[FormulaOCR] CPU input={Width}x{Height} tokens={Tokens} complete={Complete} elapsed={Elapsed} ms",image.Width,image.Height,tokens.Count-1,complete,time.ElapsedMilliseconds);
        return new FormulaResult(latex,complete,tokens.Count-1,time.ElapsedMilliseconds);
    },ct);
    internal static DenseTensor<float> ResizeFormula(GrayImage prepared,CancellationToken ct,Action<int,GrayImage,int>? trace=null)
    {
        var input=prepared.Bound();int w=input.Width,h=input.Height;double ratio=1;DenseTensor<float>? tensor=null;
        var resizer=Session("image_resizer.onnx");
        for(int k=0;k<10;k++)
        {
            ct.ThrowIfCancellationRequested();h=Math.Max(1,(int)(h*ratio));var scaled=input.Resize(w,h,ratio>1).Bound().TightPad()??input;
            tensor=GrayTensor(scaled);
            using var resized=Infer(resizer,ct,NamedOnnxValue.CreateFromTensor(resizer.InputNames[0],tensor));var logits=resized.First().AsTensor<float>().ToArray();
            w=(ArgMax(logits)+1)*32;trace?.Invoke(k,scaled,w);if(w==scaled.Width)break;ratio=w/(double)scaled.Width;
        }
        return tensor!;
    }
    internal static string DecodeTokens(string file,IEnumerable<long> tokens)
    {
        using var json=JsonDocument.Parse(File.ReadAllText(file));var vocab=json.RootElement.GetProperty("model").GetProperty("vocab").EnumerateObject().ToDictionary(p=>p.Value.GetInt64(),p=>p.Name);
        string text=string.Concat(tokens.Where(t=>t>2).Select(t=>vocab.TryGetValue(t,out var v)?v:throw new InvalidDataException("公式 token 超出字典范围。"))).Replace("Ġ"," ").Trim();
        // Preserve spaces in explicit text commands; clean only math token boundaries.
        var protectedText=new List<string>();text=Regex.Replace(text,@"\\(?:operatorname|mathrm|text|mathbf)\s?\*?\s*\{[^{}]*\}",m=>{protectedText.Add(m.Value);return "\uE000"+(protectedText.Count-1)+"\uE001";});
        text=Regex.Replace(text,@"(?<!\\)\s+(?=[^a-zA-Z])|(?<=[^a-zA-Z\\])\s+","");
        return Regex.Replace(text,"\uE000(\\d+)\uE001",m=>protectedText[int.Parse(m.Groups[1].Value)]);
    }
    private static DenseTensor<float> GrayTensor(GrayImage image)=>new(image.Pixels.Select(v=>(v/255f-.7931f)/.1738f).ToArray(),new[]{1,1,image.Height,image.Width});
    private static int ArgMax(ReadOnlySpan<float> values){int best=0;for(int i=1;i<values.Length;i++)if(values[i]>values[best])best=i;return best;}
    internal static readonly string[] Labels=["paragraph_title","image","text","number","abstract","content","figure_title","formula","table","table_title","reference","doc_title","footnote","header","algorithm","footer","seal","chart_title","chart","formula_number","header_image","footer_image","aside_text"];
    internal static Task<List<LayoutRegion>> LayoutAsync(DocumentImage image,CancellationToken ct,string? model=null)
    {
        model??=DocumentModelStore.SelectedLayout;
        if(!DocumentModelStore.LayoutIds.Contains(model))throw new ArgumentException("未知版面模型。");
        string selected=model;
        return RunAsync(selected,()=>
    {
        image.Validate();var session=Session("layout.onnx");int size=selected=="layout-doclayout-s"?480:640;
        bool large=selected=="layout-doclayout-l";
        string[] required=large?["image","scale_factor","im_shape"]:["image","scale_factor"];
        if(!session.InputNames.Order().SequenceEqual(required.Order())||session.InputMetadata["image"].ElementType!=typeof(float)
            ||session.InputMetadata["image"].Dimensions.Length!=4||session.InputMetadata["image"].Dimensions[0] is not(1 or -1)
            ||!session.InputMetadata["image"].Dimensions.Skip(1).SequenceEqual(new[]{3,size,size}))throw new InvalidDataException("版面模型接口与所选 S/M/L 型号不兼容，请导入对应模型。");
        var tensor=LayoutTensor(image,selected,ct);var factor=new DenseTensor<float>(new[]{size/(float)image.Height,size/(float)image.Width},new[]{1,2});
        var inputs=new List<NamedOnnxValue>{NamedOnnxValue.CreateFromTensor("image",tensor),NamedOnnxValue.CreateFromTensor("scale_factor",factor)};
        if(large)inputs.Add(NamedOnnxValue.CreateFromTensor("im_shape",new DenseTensor<float>(new[]{(float)size,(float)size},new[]{1,2})));
        var timer=Stopwatch.StartNew();using var output=Infer(session,ct,inputs.ToArray());
        var raw=output.First(v=>v.Value is Tensor<float>).AsTensor<float>().ToArray();int count=raw.Length/6;
        var number=output.FirstOrDefault(v=>v.Value is Tensor<int> or Tensor<long>);if(number?.Value is Tensor<int> ints)count=Math.Min(count,ints.First());else if(number?.Value is Tensor<long> longs)count=Math.Min(count,checked((int)longs.First()));
        var regions=new List<LayoutRegion>();
        for(int i=0;i<count;i++)
        {int o=i*6,c=(int)raw[o];if(c<0||c>=Labels.Length||!float.IsFinite(raw[o+1])||raw[o+1]<.3f)continue;
            float x=Math.Clamp(raw[o+2],0,image.Width),y=Math.Clamp(raw[o+3],0,image.Height),r=Math.Clamp(raw[o+4],0,image.Width),b=Math.Clamp(raw[o+5],0,image.Height);
            if(!float.IsFinite(x+y+r+b)||r<=x||b<=y)continue;regions.Add(new("region-"+i,c,Labels[c],raw[o+1],new(x,y,r-x,b-y)));}
        Serilog.Log.Information("[DocumentOCR] layout={Model} input={Width}x{Height} regions={Regions} infer={Elapsed} ms",selected,image.Width,image.Height,regions.Count,timer.ElapsedMilliseconds);
        return DocumentReadingOrder.Sort(regions);
    },ct);
    }
    internal static DenseTensor<float> LayoutTensor(DocumentImage image,string model,CancellationToken ct)
    {
        if(model=="layout-doclayout-s")return ColorTensor(image,480,480,false,ct);
        // M/L official config uses OpenCV INTER_CUBIC, RGB and a stretched 640px input.
        // L uses only /255; M additionally applies ImageNet mean/std.
        image.Validate();const int size=640;var tensor=new DenseTensor<float>(new[]{1,3,size,size});
        float[] mean=[.485f,.456f,.406f],std=[.229f,.224f,.225f];
        static float Cubic(float x){x=Math.Abs(x);return x<=1?((1.25f*x-2.25f)*x)*x+1:x<2?((-.75f*x+3.75f)*x-6)*x+3:0;}
        var xx=new int[size*4];var wx=new float[size*4];var yy=new int[size*4];var wy=new float[size*4];
        void Axis(int source,int[] indices,float[] weights)
        {for(int i=0;i<size;i++){float s=(i+.5f)*source/size-.5f;int p=(int)MathF.Floor(s);for(int k=0;k<4;k++){indices[i*4+k]=Math.Clamp(p+k-1,0,source-1);weights[i*4+k]=Cubic(s-(p+k-1));}}}
        Axis(image.Width,xx,wx);Axis(image.Height,yy,wy);
        for(int y=0;y<size;y++)
        {
            ct.ThrowIfCancellationRequested();
            for(int x=0;x<size;x++)for(int c=0;c<3;c++)
            {
                float value=0;
                for(int ky=0;ky<4;ky++)for(int kx=0;kx<4;kx++){int i=yy[y*4+ky]*image.RowBytes+xx[x*4+kx]*4;value+=Math.Min(255,image.Pixels[i+2-c]+255-image.Pixels[i+3])*wy[y*4+ky]*wx[x*4+kx];}
                value=Math.Clamp(MathF.Round(value),0,255)/255f;tensor[0,c,y,x]=model=="layout-doclayout-m"?(value-mean[c])/std[c]:value;
            }
        }
        return tensor;
    }
    internal static Task<TableResult> TableAsync(DocumentImage image,CancellationToken ct)=>RunAsync("table-slanet-plus",()=>
    {
        image.Validate();var session=Session("table.onnx");var tensor=ColorTensor(image,488,488,true,ct);
        using var output=Infer(session,ct,NamedOnnxValue.CreateFromTensor(session.InputNames[0],tensor));
        var tensors=output.Select(v=>v.AsTensor<float>()).ToArray();var probabilities=tensors.FirstOrDefault(t=>t.Dimensions[^1]>8)??throw new InvalidDataException("表格结构输出不兼容。");
        var boxes=tensors.FirstOrDefault(t=>t.Dimensions[^1]==8)??throw new InvalidDataException("表格模型缺少单元格定位输出。");
        string config=File.ReadAllText(Path.Combine(_directory!,"table.yml"));string[] dict=config[(config.IndexOf("character_dict:",StringComparison.Ordinal)+15)..].Split('\n').Where(l=>l.TrimStart().StartsWith("- ")).Select(l=>l.Trim()[2..].Trim().Trim('\'')).Where(t=>t!="<td>").Prepend("sos").Append("<td></td>").Append("eos").ToArray();
        int classes=probabilities.Dimensions[^1];if(classes!=dict.Length)throw new InvalidDataException($"表格字典不匹配：{classes}/{dict.Length}");
        var probs=probabilities.ToArray();var coords=boxes.ToArray();if(probs.Any(v=>!float.IsFinite(v))||coords.Any(v=>!float.IsFinite(v)))throw new InvalidDataException("表格模型输出包含无效数值。");var structure=new System.Text.StringBuilder("<table>");var cellBoxes=new List<PixelBox>();var scores=new List<float>();
        double scale=Math.Min(488d/image.Width,488d/image.Height);int rw=(int)(image.Width*scale),rh=(int)(image.Height*scale);
        for(int i=0;i<probs.Length/classes;i++)
        {
            int index=ArgMax(probs.AsSpan(i*classes,classes));if(index==classes-1)break;if(index==0)continue;string token=dict[index];scores.Add(probs[i*classes+index]);structure.Append(token);
            if(token is "<td>" or "<td" or "<td></td>")
            {int o=i*8;if(o+8>coords.Length)throw new InvalidDataException("表格单元格坐标不完整。");float[] xx=Enumerable.Range(0,4).Select(k=>Math.Clamp(coords[o+k*2]*Math.Max(image.Width,image.Height),0,image.Width)).ToArray();float[] yy=Enumerable.Range(0,4).Select(k=>Math.Clamp(coords[o+k*2+1]*Math.Max(image.Width,image.Height),0,image.Height)).ToArray();cellBoxes.Add(new(xx.Min(),yy.Min(),xx.Max()-xx.Min(),yy.Max()-yy.Min()));}
        }
        structure.Append("</table>");return TableStructure.Parse(structure.ToString(),cellBoxes,scores.Count==0?0:scores.Average());
    },ct);
    private static DenseTensor<float> ColorTensor(DocumentImage image,int width,int height,bool table,CancellationToken ct)
    {
        var tensor=new DenseTensor<float>(new[]{1,3,height,width});double ratio=Math.Min(width/(double)image.Width,height/(double)image.Height);
        int rw=table?Math.Max(1,(int)Math.Round(image.Width*ratio)):width,rh=table?Math.Max(1,(int)Math.Round(image.Height*ratio)):height;float[] mean=[.485f,.456f,.406f],std=[.229f,.224f,.225f];
        for(int y=0;y<rh;y++)
        {ct.ThrowIfCancellationRequested();float sy=(y+.5f)*image.Height/rh-.5f;int y0=Math.Clamp((int)Math.Floor(sy),0,image.Height-1),y1=Math.Min(y0+1,image.Height-1);float fy=Math.Clamp(sy-y0,0,1);
            for(int x=0;x<rw;x++){float sx=(x+.5f)*image.Width/rw-.5f;int x0=Math.Clamp((int)Math.Floor(sx),0,image.Width-1),x1=Math.Min(x0+1,image.Width-1);float fx=Math.Clamp(sx-x0,0,1);
                for(int c=0;c<3;c++){int channel=table?c:2-c;float V(int xx,int yy){int i=yy*image.RowBytes+xx*4;return Math.Min(255,image.Pixels[i+channel]+255-image.Pixels[i+3]);}
                    float value=(V(x0,y0)*(1-fx)+V(x1,y0)*fx)*(1-fy)+(V(x0,y1)*(1-fx)+V(x1,y1)*fx)*fy;
                    tensor[0,c,y,x]=(MathF.Round(value)/255f-mean[c])/std[c];}}}
        return tensor;
    }
}
