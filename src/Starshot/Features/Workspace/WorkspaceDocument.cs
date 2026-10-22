using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Starshot.Features.Codec;
using Starshot.Features.Screenshot;
using Starward.Codec.ICC;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Starshot.Features.Workspace;

/// <summary>Owned CPU snapshot; no WGC frame, CanvasBitmap, HWND or decoder remains resident.</summary>
internal sealed class WorkspaceDocument : IDisposable
{
    private byte[]? _bgra, _hdr;
    public byte[] Bgra => _bgra ?? throw new ObjectDisposedException(nameof(WorkspaceDocument));
    public byte[]? HdrPixels => _hdr;
    public int Width { get; }
    public int Height { get; }
    public string Name { get; }
    public string Fingerprint { get; }
    public float ReferenceWhite { get; }
    public string Carrier { get; }
    public string InitialTool {get;set;}="hand";
    public HdrLuminanceAnalysis? Analysis { get; set; }
    public byte[]? Mask { get; set; }
    public WorkspaceDocument(string name, string fingerprint, int width, int height, byte[] bgra,
        byte[]? hdr = null, float white = 203, string carrier = "SDR")
    {
        ImageMask.Validate(width,height);
        if(bgra.Length != width*height*4 || (hdr is not null && hdr.Length != width*height*8)) throw new ArgumentException("图像尺寸不符");
        Name=name; Fingerprint=fingerprint; Width=width; Height=height; _bgra=bgra; _hdr=hdr; ReferenceWhite=white; Carrier=carrier;
    }

    public static async Task<WorkspaceDocument> LoadAsync(string path, CancellationToken ct)
    {
        var file = new FileInfo(path);
        if (file.Length > 512L*1024*1024) throw new ArgumentException("图像文件超过 512 MiB 编辑上限。");
        using var image = await ImageLoader.LoadImageAsync(path,ct);
        return await FromCanvasAsync(image.CanvasBitmap,file.Name,$"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}",
            image.SdrReferenceWhiteNits ?? 203,image.ColorPrimaries,image.Carrier ?? file.Extension,ct);
    }

    public static async Task<WorkspaceDocument> FromCanvasAsync(CanvasBitmap source,string name,string fingerprint,
        float white,ColorPrimaries primaries,string carrier,CancellationToken ct=default,bool allowHdrAnalysis=true)
    {
        int w=checked((int)source.SizeInPixels.Width),h=checked((int)source.SizeInPixels.Height); ImageMask.Validate(w,h);
        bool hdr=source.Format is DirectXPixelFormat.R16G16B16A16Float or DirectXPixelFormat.R32G32B32A32Float;
        using var normalized = new CanvasRenderTarget(source.Device,w,h,96,
            hdr ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized,CanvasAlphaMode.Premultiplied);
        Matrix4x4 m=ColorPrimaries.GetColorTransferMatrix(primaries,ColorPrimaries.BT709);
        using var linear = new SrgbGammaEffect { Source=source,GammaMode=SrgbGammaMode.EOTF,BufferPrecision=CanvasBufferPrecision.Precision16Float };
        using var matrix = new ColorMatrixEffect { Source=hdr ? source : linear,ClampOutput=false,BufferPrecision=CanvasBufferPrecision.Precision16Float,
            ColorMatrix=new Matrix5x4(m.M11,m.M12,m.M13,0,m.M21,m.M22,m.M23,0,m.M31,m.M32,m.M33,0,0,0,0,1,0,0,0,0) };
        using var gamma = new SrgbGammaEffect {Source=matrix,GammaMode=SrgbGammaMode.OETF,BufferPrecision=CanvasBufferPrecision.Precision16Float};
        using(var ds=normalized.CreateDrawingSession()) ds.DrawImage(hdr ? matrix : gamma);
        byte[]? original=hdr && allowHdrAnalysis ? await ReadStripesAsync(normalized,8,ct) : null;
        using var display = hdr ? ScreenCaptureService.TonemapToSdr(normalized,white) : null;
        byte[] preview=await ReadStripesAsync(display ?? normalized,4,ct);
        return new(name,fingerprint,w,h,preview,original,white,carrier);
    }

    private static async Task<byte[]> ReadStripesAsync(CanvasBitmap source,int stride,CancellationToken ct)
    {
        // TonemapToSdr returns RGBA8; WIC, OCR, pixel readouts and workspace
        // exports all consume BGRA8. Normalize bytes without another GPU target.
        WorkspacePixels.ByteOrder? order=source.Format switch
        {
            DirectXPixelFormat.B8G8R8A8UIntNormalized when stride==4=>WorkspacePixels.ByteOrder.Bgra8,
            DirectXPixelFormat.R8G8B8A8UIntNormalized when stride==4=>WorkspacePixels.ByteOrder.Rgba8,
            DirectXPixelFormat.R16G16B16A16Float when stride==8=>null,
            _=>throw new ArgumentException("工作区像素格式与 stride 不匹配")
        };
        int w=(int)source.SizeInPixels.Width,h=(int)source.SizeInPixels.Height,row=w*stride;
        byte[] output=new byte[checked(row*h)];
        int rows=Math.Max(1,1024*1024/row);
        for(int y=0;y<h;y+=rows)
        {
            ct.ThrowIfCancellationRequested(); int count=Math.Min(rows,h-y);
            byte[] stripe=source.GetPixelBytes(0,y,w,count);
            if(order.HasValue)WorkspacePixels.CopyBgra8(stripe,output.AsSpan(y*row,stripe.Length),order.Value);
            else System.Buffer.BlockCopy(stripe,0,output,y*row,stripe.Length); // Raw FP16 RGBA stays untouched.
            await Task.Delay(1,ct);
        }
        return output;
    }

    internal static async Task<string> PngAsync(byte[] pixels,int w,int h,int maximum=2048,CancellationToken ct=default)
    {
        using var memory=new InMemoryRandomAccessStream();
        var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,memory).AsTask(ct);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)w,(uint)h,96,96,pixels);
        if(maximum>0 && Math.Max(w,h)>maximum)
        {
            double scale=maximum/(double)Math.Max(w,h);
            encoder.BitmapTransform.ScaledWidth=(uint)Math.Max(1,(int)(w*scale)); encoder.BitmapTransform.ScaledHeight=(uint)Math.Max(1,(int)(h*scale));
        }
        await encoder.FlushAsync().AsTask(ct);
        byte[] png=new byte[checked((int)memory.Size)];
        memory.Seek(0); using var input=memory.AsStreamForRead();await input.ReadExactlyAsync(png,ct);
        return "data:image/png;base64,"+Convert.ToBase64String(png);
    }

    internal object Pixel(int x,int y)
    {
        if((uint)x>=Width || (uint)y>=Height) throw new ArgumentException("像素坐标无效");
        int i=(y*Width+x)*4;
        float? nits=null;
        if(_hdr is not null)
        {
            int j=(y*Width+x)*8;
            float H(int c)=>(float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(_hdr.AsSpan(j+c*2)));
            float r=H(0),g=H(1),b=H(2); float value=(.2126f*r+.7152f*g+.0722f*b)*80;
            if(float.IsFinite(value))nits=Math.Max(0,value);
        }
        return new{x,y,r=Bgra[i+2],g=Bgra[i+1],b=Bgra[i],nits};
    }
    public void Dispose(){ Analysis?.Dispose(); Analysis=null; Mask=null; _bgra=null; _hdr=null; }
}
