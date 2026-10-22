using System.Collections;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Starshot.Features.Screenshot;

namespace Starshot.ToolbarValidation;

public sealed partial class ToolbarTestApp
{
    private void ValidateRegionTranslation(RegionCaptureWindow window)
    {
        const int width=640,height=200;var bytes=new byte[width*height*4];for(int i=0;i<bytes.Length;i+=4){bytes[i]=bytes[i+1]=bytes[i+2]=40;bytes[i+3]=255;}
        using var source=CanvasBitmap.CreateFromBytes(CanvasDevice.GetSharedDevice(),bytes,width,height,DirectXPixelFormat.B8G8R8A8UIntNormalized);
        void Prepare()
        {
            Set(window,"_canvasOriginal",source);Set(window,"_displayBitmap",source);Set(window,"_ownsDisplayBitmap",false);Set(window,"_displayPixels",bytes);
            Set(window,"_vx",-1920);Set(window,"_vy",-200);Set(window,"_scale",2f);Set(window,"_lockedW",320f);Set(window,"_lockedH",100f);Set(window,"_isClosed",false);Set(window,"_prevForeground",(nint)0);
            WindowType.GetProperty("SelectionRect")!.SetValue(window,new Rect(10,5,280,80));State(window,"Selected");
            WindowType.GetProperty("Completion")!.SetValue(window,new TaskCompletionSource<RegionCaptureResult>());
            Call(window,"ResetAnnotations");
        }
        Prepare();
        var lineType=WindowType.Assembly.GetType("Starshot.Features.Screenshot.RegionTranslatedText")!;
        var lines=Array.CreateInstance(lineType,1);lines.SetValue(Activator.CreateInstance(lineType,new object[]{new System.Drawing.RectangleF(10,8,200,40),"已翻译",0xffc0d040u,0xff202420u,22f}),0);
        byte[] before;using(var crop=(CanvasRenderTarget)Call(window,"CropDisplayToBgra")!)before=crop.GetPixelBytes();
        Call(window,"ApplyRegionTranslation",lines);
        Check(((IList)Field(window,"_annotations")!).Count==1,"Translation batch remains in existing screenshot overlay");
        Check(!window.Completion.Task.IsCompleted,"Translation does not complete capture or open a workspace");
        byte[] translated;using(var crop=(CanvasRenderTarget)Call(window,"CropDisplayToBgra")!)translated=crop.GetPixelBytes();
        Check(!before.SequenceEqual(translated),"Translated text/background is included in the normal SDR crop at 200% DPI");
        Call(window,"UndoAnnotation");using(var crop=(CanvasRenderTarget)Call(window,"CropDisplayToBgra")!)Check(before.SequenceEqual(crop.GetPixelBytes()),"One Ctrl+Z restores original frozen pixels");
        Call(window,"RedoAnnotation");using(var crop=(CanvasRenderTarget)Call(window,"CropDisplayToBgra")!)Check(translated.SequenceEqual(crop.GetPixelBytes()),"Redo restores the same translation pixels");
        Call(window,"CompleteCapture",RegionCaptureAction.Copy);var copied=window.Completion.Task.GetAwaiter().GetResult();
        using(copied.SdrCrop)using(copied.AnnotationLayer)Check(copied.Action==RegionCaptureAction.Copy&&copied.SdrCrop is not null&&translated.SequenceEqual(copied.SdrCrop.GetPixelBytes()),"Copy receives translated crop with no gallery route");
        Prepare();Call(window,"InvokeToolbar",Command("Subtitles"));var selected=window.Completion.Task.GetAwaiter().GetResult();
        Check(selected.Action==RegionCaptureAction.Subtitles&&selected.SdrCrop is null,"Subtitles use existing region toolbar with coordinate-only handoff");
        Check(selected.PhysicalRect==new Rect(20,10,560,160),"Subtitle ROI is physical pixels once at 200% DPI");
        Check(Field(window,"_canvasOriginal") is null&&Field(window,"_swapChain") is null,"Closing selection releases frozen source/swapchain before live sampling");
        Check(!window.AppWindow.IsVisible,"No test overlay became visible");
    }
}
