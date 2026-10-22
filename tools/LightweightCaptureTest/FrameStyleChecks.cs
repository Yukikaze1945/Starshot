using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using Starshot.Features.Screenshot;

internal static partial class Program
{
    private static void FramePreview(string path)
    {
        const int w=1400,h=1200;
        using var surface=new CpuCaptureSurface(w,h);
        CpuCaptureGdi.Fill(surface.Dc,new(0,0,w,h),Color.FromArgb(231,232,224));
        CpuCaptureGdi.Fill(surface.Dc,new(180,750,1040,320),Color.FromArgb(36,43,36));
        nint font=CpuCaptureGdi.Font(34,"Segoe UI"),body=CpuCaptureGdi.Font(23,"Segoe UI");
        try
        {
            CpuCaptureGdi.Text(surface.Dc,"starshot   /   每一帧，都值得珍藏",new(228,794,930,60),font,Color.FromArgb(221,243,105));
            CpuCaptureGdi.Text(surface.Dc,"圆角、阴影与边框  ·  保留画面的细节",new(228,878,930,48),body,Color.FromArgb(238,242,229));
            CpuCaptureGdi.Text(surface.Dc,"选区样式在此预览，复制与保存使用同样的参数。",new(228,934,930,48),body,Color.FromArgb(170,181,160));
        }
        finally{CpuCaptureGdi.DeleteObject(font);CpuCaptureGdi.DeleteObject(body);}
        using var source=surface.ToImage();
        using var window=new CpuRegionCaptureWindow(source,0,0,RegionCaptureAction.Copy,moveOnscreen:false);
        Field("_selected").SetValue(window,true);Field("_selection").SetValue(window,new Rectangle(180,750,1040,320));
        Field("_frameStyle").SetValue(window,new RegionFrameStyle(48,16,1));Field("_framePopup").SetValue(window,true);
        CpuCaptureNative.InvalidateRect(Handle(window),0,false);CpuCaptureNative.SendMessageW(Handle(window),0x0f,0,0);
        var buffer=(CpuCaptureSurface)Field("_backBuffer").GetValue(window)!;using var rendered=buffer.ToImage();
        File.WriteAllBytes(path,rendered.Pixels);Console.WriteLine($"FRAME_PREVIEW {w} {h}");
    }
    private static void FrameStyleChecks()
    {
        using var source=Pattern(180,120);var original=(byte[])source.Pixels.Clone();
        using(var round=CpuCaptureFrameStyle.Apply(source,new(24)))
        {
            Check(round.Width==180&&round.Height==120,"Radius-only crop preserves dimensions");
            Check(round.Pixels[3]==0&&round.Pixels[(60*180+90)*4+3]==255,"Rounded corners are transparent and centre remains opaque");
            Check(round.Pixels[(60*180+90)*4]==source.Pixels[(60*180+90)*4],"Interior source colour is preserved");
        }
        var style=new RegionFrameStyle(24,8,2,0xffddf369);
        using(var framed=CpuCaptureFrameStyle.Apply(source,style))
        {
            int pad=style.Padding;
            Check(framed.Width==source.Width+pad*2&&framed.Height==source.Height+pad*2,"Shadow adds symmetric transparent padding without cropping content");
            Check(framed.Pixels[(pad/2*framed.Width+pad+90)*4+3]>0,"Shadow exists outside the original image");
            for(int p=0;p<framed.Pixels.Length;p+=4)
                Check(framed.Pixels[p]<=framed.Pixels[p+3]&&framed.Pixels[p+1]<=framed.Pixels[p+3]&&framed.Pixels[p+2]<=framed.Pixels[p+3],"Frame uses valid premultiplied BGRA");
        }
        Check(original.SequenceEqual(source.Pixels),"Frame renderer never mutates frozen source pixels");
        foreach(var action in new[]{RegionCaptureAction.Ocr,RegionCaptureAction.Subtitles,RegionCaptureAction.LongCapture,RegionCaptureAction.RecordGif,RegionCaptureAction.PageLayout})
            Check(!RegionFrameStyle.Applies(action),"Recognition/recording receive raw rectangular pixels");
        using(var window=new CpuRegionCaptureWindow(source,-1920,-200,RegionCaptureAction.Copy,moveOnscreen:false))
        {
            Field("_selected").SetValue(window,true);Field("_selection").SetValue(window,new Rectangle(20,20,130,70));
            typeof(CpuRegionCaptureWindow).GetMethod("FrameLayout",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null);
            var header=(RectangleF)Field("_frameHeader").GetValue(window)!;
            float scale=(float)Field("_frameScale").GetValue(window)!;
            var button=RegionFrameControls.Button(header,scale);
            Field("_mouse").SetValue(window,new Point((int)button.X+1921,(int)button.Y+201));
            typeof(CpuRegionCaptureWindow).GetMethod("ToolbarPointerDown",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null);
            Check((bool)Field("_framePopup").GetValue(window)!,"Metrics style button opens CPU popup");
            typeof(CpuRegionCaptureWindow).GetMethod("ToolbarEscape",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null);
            Check(!(bool)Field("_framePopup").GetValue(window)!&&(bool)Field("_selected").GetValue(window)!,"Esc closes frame popup before clearing selection");
            Field("_frameStyle").SetValue(window,style);Finish(window,RegionCaptureAction.Copy);
            using var image=window.Completion.Task.Result.Image!;
            Check(image.Width==130+style.Padding*2,"Copy receives framed CPU crop");
        }
        foreach(float scale in new[]{1f,1.25f,2f,3f})foreach(var area in new[]{new RectangleF(-1920,-200,1920,1080),new(0,0,640,480)})
        {
            var header=RegionFrameControls.Header(new(area.Left,area.Top,200,100),area,scale);
            var popup=RegionFrameControls.Popup(header,area,scale);
            Check(area.Contains(header)&&area.Contains(popup),"Style header/popup clamp to physical work area at mixed DPI");
        }
        using var large=Pattern(3840,2160);var watch=Stopwatch.StartNew();using var decorated=CpuCaptureFrameStyle.Apply(large,new(60,16,2));
        Console.WriteLine($"FRAME_STYLE_CPU_4K_MS={watch.Elapsed.TotalMilliseconds:F2}; output={decorated.Width}x{decorated.Height}; no GPU");
    }
}
