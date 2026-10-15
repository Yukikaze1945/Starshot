using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Starshot.Features.Workspace;

namespace Starshot.Helpers;

/// <summary>Optional U²-Net compact foreground segmentation, CPU-only, scoped native session.</summary>
internal static class BackgroundRemoval
{
    private static readonly SemaphoreSlim Gate=new(1);
    internal static async Task<byte[]> CreateMaskAsync(byte[] bgra,int width,int height,CancellationToken ct)
    {
        ImageMask.Validate(width,height);await Gate.WaitAsync(ct);
        try
        {
            string path=await DlcModelStore.Current.RequireAsync("u2netp",ct);
            return await Task.Run(()=>
            {
                ct.ThrowIfCancellationRequested();
                using var options=new SessionOptions{IntraOpNumThreads=2,InterOpNumThreads=1,ExecutionMode=ExecutionMode.ORT_SEQUENTIAL};
                using var session=new InferenceSession(path,options);
                const int size=320;
                var tensor=new DenseTensor<float>(new[]{1,3,size,size});
                var means=new[]{.485f,.456f,.406f};var deviations=new[]{.229f,.224f,.225f};
                float maximum=1;
                for(int i=0;i<bgra.Length;i+=4)maximum=Math.Max(maximum,Math.Max(bgra[i],Math.Max(bgra[i+1],bgra[i+2])));
                for(int y=0;y<size;y++)
                {
                    ct.ThrowIfCancellationRequested();
                    float yy=(y+.5f)*height/size-.5f;int top=Math.Clamp((int)Math.Floor(yy),0,height-1),bottom=Math.Min(top+1,height-1);float fy=Math.Clamp(yy-top,0,1);
                    for(int x=0;x<size;x++)
                    {
                        float xx=(x+.5f)*width/size-.5f;int left=Math.Clamp((int)Math.Floor(xx),0,width-1),right=Math.Min(left+1,width-1);float fx=Math.Clamp(xx-left,0,1);
                        for(int c=0;c<3;c++)
                        {
                            int channel=2-c;
                            float a=bgra[(top*width+left)*4+channel]*(1-fx)+bgra[(top*width+right)*4+channel]*fx;
                            float b=bgra[(bottom*width+left)*4+channel]*(1-fx)+bgra[(bottom*width+right)*4+channel]*fx;
                            tensor[0,c,y,x]=((a*(1-fy)+b*fy)/maximum-means[c])/deviations[c];
                        }
                    }
                }
                using var runOptions=new RunOptions();
                using var cancel=ct.Register(()=>runOptions.Terminate=true);
                using var result=session.Run(new[]{NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(),tensor)},session.OutputMetadata.Keys.ToArray(),runOptions);
                ct.ThrowIfCancellationRequested();
                float[] output=result.First().AsTensor<float>().ToArray();float min=output.Min(),max=output.Max();
                if(!float.IsFinite(min)||!float.IsFinite(max)||max-min<1e-6f)throw new InvalidOperationException("没有检测到可靠前景，请使用套索或多边形修正。");
                var mask=new byte[width*height];
                for(int y=0;y<height;y++)
                {
                    ct.ThrowIfCancellationRequested();float yy=(y+.5f)*size/height-.5f;int top=Math.Clamp((int)Math.Floor(yy),0,size-1),bottom=Math.Min(top+1,size-1);float fy=Math.Clamp(yy-top,0,1);
                    for(int x=0;x<width;x++)
                    {
                        float xx=(x+.5f)*size/width-.5f;int left=Math.Clamp((int)Math.Floor(xx),0,size-1),right=Math.Min(left+1,size-1);float fx=Math.Clamp(xx-left,0,1);
                        float a=output[top*size+left]*(1-fx)+output[top*size+right]*fx,b=output[bottom*size+left]*(1-fx)+output[bottom*size+right]*fx;
                        mask[y*width+x]=(byte)Math.Clamp(MathF.Round(((a*(1-fy)+b*fy)-min)/(max-min)*255),0,255);
                    }
                }
                return mask;
            },ct);
        }
        catch(OnnxRuntimeException)when(ct.IsCancellationRequested){throw new OperationCanceledException(ct);}
        finally{Gate.Release();}
    }
    internal static async Task UnloadAsync(CancellationToken ct){await Gate.WaitAsync(ct);Gate.Release();}
}
