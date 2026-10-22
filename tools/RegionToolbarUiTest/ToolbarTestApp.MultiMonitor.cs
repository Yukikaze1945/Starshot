using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml.Controls;
using Starshot.Features.Screenshot;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.DirectX;
using Vanara.PInvoke;

namespace Starshot.ToolbarValidation;

public sealed partial class ToolbarTestApp
{
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor,int type,out uint x,out uint y);
    private async Task ValidateMultiMonitorViewport(RegionCaptureWindow window,string report)
    {
        // Moving an unshown test HWND gives real destination DPI metadata without
        // ever placing a capture overlay or synthetic image on the user's desktop.
        window.AppWindow.MoveAndResize(new RectInt32(40,40,832,225));
        await Task.Delay(30); // Let XAML commit the hidden HWND's destination DPI.
        var root=(Microsoft.UI.Xaml.Controls.Grid)Field(window,"RootGrid")!;
        double expected=root.XamlRoot?.RasterizationScale??User32.GetDpiForWindow(window.WindowHandle)/96d;
        float actual=(float)Call(window,"GetCaptureScale")!;
        var anchor=new POINT{x=User32.GetSystemMetrics((User32.SystemMetric)76)+1,y=User32.GetSystemMetrics((User32.SystemMetric)77)+1};
        var left=User32.MonitorFromPoint(anchor,User32.MonitorFlags.MONITOR_DEFAULTTONEAREST);
        GetDpiForMonitor(left.DangerousGetHandle(),0,out uint legacyDpi,out _);
        Check(Math.Abs(actual-expected)<.001,"Capture scale follows actual XAML/host HWND rather than leftmost monitor");
        Check(!User32.IsWindowVisible(window.WindowHandle),"Test HWND remains unshown on the real desktop");
        var records=new List<object>{new{actualScale=actual,xamlScale=root.XamlRoot?.RasterizationScale,hwndDpi=User32.GetDpiForWindow(window.WindowHandle),legacyTopLeftDpi=legacyDpi}};
        const int width=832,height=225;
        // Scaled version of the reported staggered three-screen topology. The
        // top-left and top-right gaps are intentionally empty desktop, not a crop.
        var bgra=new byte[width*height*4];var half=new Half[width*height*4];
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            int p=(y*width+x)*4;
            int monitor=x<256&&y>=72&&y<216?1:x>=256&&x<640&&y<216?2:x>=640&&y>=117?3:0;
            byte level=(byte)(((x/7+y/5)%2==0)?255:180);
            bgra[p]=(byte)(monitor==3?level:0);bgra[p+1]=(byte)(monitor==2?level:0);bgra[p+2]=(byte)(monitor==1?level:0);bgra[p+3]=255;
            half[p]=(Half)(monitor==1?2.5f:monitor==3?-.125f:0);half[p+1]=(Half)(monitor==2?1.25f:0);half[p+2]=(Half)(monitor==3?4f:0);half[p+3]=(Half)1;
        }
        byte[] hdr=MemoryMarshal.AsBytes(half.AsSpan()).ToArray();var device=CanvasDevice.GetSharedDevice();
        foreach(bool native in new[]{false,true})
        {
            using var source=CanvasBitmap.CreateFromBytes(device,native?hdr:bgra,width,height,native?DirectXPixelFormat.R16G16B16A16Float:DirectXPixelFormat.B8G8R8A8UIntNormalized,96,CanvasAlphaMode.Ignore);
            using var preview=CanvasBitmap.CreateFromBytes(device,bgra,width,height,DirectXPixelFormat.B8G8R8A8UIntNormalized,96,CanvasAlphaMode.Ignore);
            Set(window,"_canvasOriginal",source);Set(window,"_displayBitmap",preview);Set(window,"_ownsDisplayBitmap",false);Set(window,"_displayPixels",bgra);
            Set(window,"_vx",-256);Set(window,"_vy",0);Set(window,"_scale",1f);Set(window,"_lockedW",(float)width);Set(window,"_lockedH",(float)height);Set(window,"_sizeLocked",true);Set(window,"_isClosed",false);
            Call(window,"CreateOverlaySwapChain",(float)width,(float)height,native);
            State(window,"Selected");WindowType.GetProperty("SelectionRect")!.SetValue(window,new Rect(250,75,430,140));Set(window,"_selectionFromDrag",false);
            AnalysisMonitors(window,(new Rect(-256,0,width,height),true,200,1000));
            Call(window,"ResetAnnotations");Set(window,"_annotationColor",Windows.UI.Color.FromArgb(255,255,255,255));
            void Tool(string name)=>Call(window,"SetAnnotationTool",Enum.Parse(WindowType.GetNestedType("AnnotationTool",BindingFlags.NonPublic)!,name));
            Tool("Rectangle");Call(window,"StartAnnotation",new Point(270,95));Call(window,"FinishAnnotation",new Point(340,130));
            Tool("Pen");Call(window,"StartAnnotation",new Point(360,110));Call(window,"UpdateAnnotation",new Point(380,125));Call(window,"FinishAnnotation",new Point(400,140));
            Call(window,"UndoAnnotation");
            byte[] before;
            using(var crop=(CanvasRenderTarget)Call(window,"CropDisplayToBgra")!)before=crop.GetPixelBytes();
            var original=(Rect)Call(window,"GetPhysicalSourceRect")!;
            foreach(float scale in new[]{1f,1.25f,1.5f,1.75f,2f,2.25f,1f})
            {
                Call(window,"ApplyViewportScale",scale);
                var swap=(CanvasSwapChain)Field(window,"_swapChain")!;
                Check(swap.SizeInPixels.Width==width&&swap.SizeInPixels.Height==height&&Math.Abs(swap.Dpi-96*scale)<.01,"DPI changes preserve backing physical extent and swap DPI");
                Check((Rect)Call(window,"GetPhysicalSourceRect")! ==original,"Physical selection stable through mixed-DPI changes");
                var point=(Point)Call(window,"ToPhysicalPoint",new Point(640/scale,117/scale))!;
                Check(point==new Point(640,117),"Pointer coordinates map to the exact physical monitor boundary");
                using(var crop=(CanvasRenderTarget)Call(window,"CropDisplayToBgra")!)
                {var after=crop.GetPixelBytes();Check(after.Length==before.Length&&after.Zip(before,(a,b)=>Math.Abs(a-b)).Max()<=1,"SDR copy plus annotation geometry/thickness survive DPI changes");}
                using(var rendered=new CanvasRenderTarget(device,width/scale,height/scale,scale*96,source.Format,CanvasAlphaMode.Ignore))
                {
                    using(var ds=rendered.CreateDrawingSession())Call(window,"DrawFrozenPreview",ds,new Rect(0,0,width/scale,height/scale),new Rect(0,0,width,height));
                    var pixels=rendered.GetPixelBytes();
                    if(native)
                    {var decoded=MemoryMarshal.Cast<byte,Half>(pixels.AsSpan());Check(decoded.Length==half.Length&&decoded.ToArray().Zip(half,(a,b)=>Math.Abs((float)a-(float)b)).Max()<.005,"All three FP16 monitors retain HDR peaks/negative RGB without scaling seams or lost rows");}
                    else Check(pixels.Length==bgra.Length&&pixels.Zip(bgra,(a,b)=>Math.Abs(a-b)).Max()<=1,"All three SDR monitors retain physical placement and valid black desktop gaps");
                }
                records.Add(new{native,scale,pixels=$"{swap.SizeInPixels.Width}x{swap.SizeInPixels.Height}",dpi=swap.Dpi,crop=original.ToString()});
            }
            var annotations=(IList)Field(window,"_annotations")!;
            Call(window,"RedoAnnotation");Check(annotations.Count==2,"Redo retains an annotation that was not visible during DPI changes");
            Tool("Eraser");Call(window,"StartAnnotation",new Point(380,125));Check(annotations.Count==1,"Eraser hit testing follows rebased annotation coordinates");
            Call(window,"UndoAnnotation");Check(annotations.Count==2,"Undo restores erased annotation after DPI rebasing");
            Tool("Pen");Call(window,"StartAnnotation",new Point(420,110));Call(window,"ApplyViewportScale",1.5f);Call(window,"FinishAnnotation",new Point(450/1.5,140/1.5));
            Check(annotations.Count==3&&Field(window,"_draftAnnotation") is null,"Active stroke completes normally when DPI changes during a drag");
            Call(window,"ResetAnnotations");
            if(native)
            {
                var completion=new TaskCompletionSource<RegionCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                WindowType.GetProperty("Completion")!.SetValue(window,completion);
                ((CanvasSwapChain)Field(window,"_swapChain")!).Dispose();
                Call(window,"Redraw");
                Check(completion.Task.IsFaulted&&(bool)Field(window,"_isClosed")!,"A viewport resize failure reports capture failure without escaping the dispatcher");
                _=completion.Task.Exception;
                Check(Field(window,"_swapChain") is null&&Field(window,"_canvasOriginal") is null,"A viewport failure clears borrowed source and swap-chain references");
            }
            Set(window,"_isClosed",true);Call(window,"ReleaseSwapChain");Set(window,"_canvasOriginal",null);Set(window,"_displayBitmap",null);Set(window,"_displayPixels",null);
        }
        File.WriteAllText(Path.Combine(report,"multiscreen.json"),JsonSerializer.Serialize(new{ok=true,checks=_checks,records},new JsonSerializerOptions{WriteIndented=true}));
    }
}
