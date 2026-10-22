using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml.Controls;
using Starshot.Features.Screenshot;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace Starshot.ToolbarValidation;

public sealed partial class ToolbarTestApp
{
    private async Task ValidateFrameStyleAsync(RegionCaptureWindow window,string report)
    {
        const int w=200,h=120;
        ValidateFrameClipboard();
        var bytes=new byte[w*h*4];for(int p=0;p<bytes.Length;p+=4){bytes[p]=90;bytes[p+1]=130;bytes[p+2]=180;bytes[p+3]=255;}
        using var source=CanvasBitmap.CreateFromBytes(CanvasDevice.GetSharedDevice(),bytes,w,h,DirectXPixelFormat.B8G8R8A8UIntNormalized);
        var renderer=WindowType.Assembly.GetType("Starshot.Features.Screenshot.RegionFrameRenderer")!;
        var apply=renderer.GetMethod("Apply",BindingFlags.Static|BindingFlags.NonPublic)!;
        using(var round=(CanvasRenderTarget)apply.Invoke(null,[source,new RegionFrameStyle(24),80f])!)
        {
            var actual=round.GetPixelBytes();
            Check(actual[3]==0&&actual[(60*w+100)*4+3]==255,"GPU rounded clip has transparent corners and opaque interior");
            Check(Math.Abs(actual[(60*w+100)*4]-90)<=1,"GPU frame does not change interior colour");
            using var encoded=new MemoryStream();
            await Starshot.Features.Codec.ImageSaver.SaveAsPngAsync(round,encoded,Starward.Codec.ICC.ColorPrimaries.BT709);
            encoded.Position=0;
            using var random=encoded.AsRandomAccessStream();
            var decoder=await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(random);
            using var bitmap=await decoder.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
            var decoded=new byte[w*h*4];bitmap.CopyToBuffer(decoded.AsBuffer());
            Check(decoded[3]==0&&decoded[(60*w+100)*4+3]==255,"PNG roundtrip preserves actual alpha");
        }
        var fp16=new byte[w*h*8];
        for(int p=0;p<fp16.Length;p+=8)
        {
            foreach(var pair in new[]{(0,10f),(2,.5f),(4,-.2f),(6,1f)})
            {ushort value=BitConverter.HalfToUInt16Bits((Half)pair.Item2);fp16[p+pair.Item1]=(byte)value;fp16[p+pair.Item1+1]=(byte)(value>>8);}
        }
        using(var hdr=CanvasBitmap.CreateFromBytes(source.Device,fp16,w,h,DirectXPixelFormat.R16G16B16A16Float,96,CanvasAlphaMode.Premultiplied))
        using(var framed=(CanvasRenderTarget)apply.Invoke(null,[hdr,new RegionFrameStyle(24,8,2),203f])!)
        {
            int pad=new RegionFrameStyle(24,8,2).Padding,width=(int)framed.SizeInPixels.Width;
            byte[] actual=framed.GetPixelBytes();int p=((pad+60)*width+pad+100)*8;
            float R=(float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(actual,p));
            float B=(float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(actual,p+4));
            Check(R==10f&&B<-.19f,"HDR styling preserves raw high values and negative scRGB channels");
            Check(width==w+pad*2&&framed.Format==DirectXPixelFormat.R16G16B16A16Float,"HDR frame keeps float format and adds shadow padding");
        }
        Set(window,"_canvasOriginal",source);Set(window,"_displayBitmap",source);Set(window,"_ownsDisplayBitmap",false);
        Set(window,"_scale",2f);Set(window,"_lockedW",100f);Set(window,"_lockedH",60f);Set(window,"_isClosed",false);Set(window,"_prevForeground",(nint)0);
        Set(window,"_vx",0);Set(window,"_vy",0);WindowType.GetProperty("SelectionRect")!.SetValue(window,new Rect(20,20,80,40));State(window,"Selected");
        Call(window,"RefreshToolbar");Call(window,"UpdateSelectionMetrics",new Rect(20,20,80,40),true);
        Call(window,"FrameStyleButton_Click",null!,new Microsoft.UI.Xaml.RoutedEventArgs());Call(window,"UpdateFrameMetrics");
        Check((bool)Field(window,"_framePopup")!,"Metrics button opens the real WinUI style panel");
        var choices=(Microsoft.UI.Xaml.Controls.Canvas)Field(window,"FrameStyleChoices")!;
        var radius=choices.Children.OfType<Button>().First(b=>b.Content?.ToString()=="24");Click(radius);
        Check(((RegionFrameStyle)Field(window,"_frameStyle")!).Radius==24,"Radius preset modifies the actual overlay state");
        Check((bool)Call(window,"HandleToolbarPopupKey",Windows.System.VirtualKey.Escape)!&&Field(window,"_state")!.ToString()=="Selected","Esc closes style panel while preserving selection");
        Call(window,"FrameStyleButton_Click",null!,new Microsoft.UI.Xaml.RoutedEventArgs());Call(window,"UpdateFrameMetrics");
        Check((bool)Call(window,"ConsumeToolbarCanvasPress",new Point(500,500))!&&!(bool)Field(window,"_framePopup")!,"Outside press consumes dismissal without selecting or completing capture");
        WindowType.GetProperty("Completion")!.SetValue(window,new TaskCompletionSource<RegionCaptureResult>());
        Call(window,"CompleteCapture",RegionCaptureAction.Copy);
        var copied=await window.Completion.Task;using(copied.SdrCrop)using(copied.AnnotationLayer)
            Check(copied.FrameStyle?.Radius==24&&copied.SdrCrop!.GetPixelBytes()[3]==0,"Copy completion carries rounded pixels and consistent style metadata");
        Check(!window.AppWindow.IsVisible,"No native test window became visible");
        File.WriteAllText(Path.Combine(report,"frame-gpu.json"),JsonSerializer.Serialize(new{passed=true,checks=_checks,pngAlpha=true,hdrFloat=true,negativeChannels=true,uiDpi=200},new JsonSerializerOptions{WriteIndented=true}));
    }

    private void ValidateFrameClipboard()
    {
        // Inspect the allocation directly. Never replace the user's clipboard.
        var helper=WindowType.Assembly.GetType("Starshot.Helpers.ClipboardHelper")!;
        const BindingFlags flags=BindingFlags.Static|BindingFlags.NonPublic;
        var create=helper.GetMethod("CreateAlphaDib",flags)!;
        var pixels=new byte[]{10,20,30,128,0,0,0,0,60,70,80,255,5,10,15,32};
        nint memory=(nint)create.Invoke(null,[2,2,pixels])!;
        Check(memory!=0,"Transparent clipboard DIB allocation succeeds");
        nint pointer=(nint)helper.GetMethod("GlobalLock",flags)!.Invoke(null,[memory])!;
        try
        {
            var data=new byte[140];Marshal.Copy(pointer,data,0,data.Length);
            Check(BitConverter.ToInt32(data,0)==124&&BitConverter.ToInt32(data,16)==3,"Clipboard uses explicit BITMAPV5 bitfields");
            Check(BitConverter.ToUInt32(data,52)==0xff000000&&BitConverter.ToUInt32(data,56)==0x73524742,"Clipboard declares alpha and sRGB");
            Check(data.AsSpan(124,8).SequenceEqual(pixels.AsSpan(8,8))&&data.AsSpan(132,8).SequenceEqual(pixels.AsSpan(0,8)),"Clipboard keeps premultiplied alpha and bottom-up row order");
        }
        finally
        {
            helper.GetMethod("GlobalUnlock",flags)!.Invoke(null,[memory]);
            helper.GetMethod("GlobalFree",flags)!.Invoke(null,[memory]);
        }
    }
}
