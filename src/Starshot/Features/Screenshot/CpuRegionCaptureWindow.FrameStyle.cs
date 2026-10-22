using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;

namespace Starshot.Features.Screenshot;

internal sealed partial class CpuRegionCaptureWindow
{
    private RegionFrameStyle _frameStyle=new();
    private bool _framePopup;
    private int _frameFocus;
    private float _frameScroll;
    private CpuCaptureImage? _framePreview;
    private RegionFrameStyle? _framePreviewStyle;
    private Rectangle _framePreviewRect;
    private RectangleF _frameHeader,_framePanel;
    private float _frameScale;
    private System.Collections.Generic.IReadOnlyList<FrameChoice> FrameChoices()=>RegionFrameControls.Choices(new(_framePanel.X,_framePanel.Y-_frameScroll,_framePanel.Width,_framePanel.Height),_frameScale,_frameStyle);
    private void FrameLayout()
    {
        var layout=ToolbarLayout();_frameScale=layout.Monitor.Scale;
        _frameHeader=RegionFrameControls.Header(ScreenRect(_selection,_virtualX,_virtualY),layout.Monitor.WorkArea,_frameScale);
        _framePanel=RegionFrameControls.Popup(_frameHeader,layout.Monitor.WorkArea,_frameScale);
    }
    private void ClearFramePreview(){_framePreview?.Dispose();_framePreview=null;_framePreviewStyle=null;}
    private void CloseFrameStyle(){_framePopup=false;}
    private void PaintDraggingFrame(nint dc,Rectangle rect)
    {
        // During movement/strokes, update the rounded GDI clip immediately;
        // rebuild the full feathered shadow only once after pointer release.
        int saved=SaveDC(dc),radius=(int)_frameStyle.EffectiveRadius(rect.Width,rect.Height);
        nint region=CreateRoundRectRgn(rect.Left,rect.Top,rect.Right+1,rect.Bottom+1,radius*2,radius*2);
        try
        {
            SelectClipRgn(dc,region);
            if(_annotations.Count>0||_draft is not null){EnsurePreview();if(_preview is not null)CpuCaptureGdi.BitBlt(dc,rect.X,rect.Y,rect.Width,rect.Height,_preview.Dc,0,0,CpuCaptureGdi.Copy);}
            else CpuCaptureGdi.Blit(dc,_source!.Pixels,_source.Width,_source.Height,rect,rect);
        }
        finally{RestoreDC(dc,saved);CpuCaptureGdi.DeleteObject(region);}
    }
    [DllImport("gdi32.dll")]private static extern int SaveDC(nint dc);
    [DllImport("gdi32.dll")]private static extern bool RestoreDC(nint dc,int saved);
    [DllImport("gdi32.dll")]private static extern nint CreateRoundRectRgn(int left,int top,int right,int bottom,int w,int h);
    [DllImport("gdi32.dll")]private static extern int SelectClipRgn(nint dc,nint region);
    private void PaintStyledSelection(nint dc,Rectangle rect)
    {
        bool dirty=_previewDirty||_framePreviewRect!=rect||_framePreviewStyle!=_frameStyle||_framePreview is null;
        EnsurePreview();
        if(dirty&&_preview is not null)
        {
            ClearFramePreview();using var raw=_preview.ToImage();
            _framePreview=CpuCaptureFrameStyle.Apply(raw,_frameStyle);
            CpuCaptureFrameStyle.FlattenPreview(_framePreview,new(rect.X-_frameStyle.Padding,rect.Y-_frameStyle.Padding),_dark!,_desktop.Width,_desktop.Height);
            _framePreviewRect=rect;_framePreviewStyle=_frameStyle;
        }
        if(_framePreview is not null)
            CpuCaptureGdi.Blit(dc,_framePreview.Pixels,_framePreview.Width,_framePreview.Height,
                new(rect.X-_frameStyle.Padding,rect.Y-_frameStyle.Padding,_framePreview.Width,_framePreview.Height),new(0,0,_framePreview.Width,_framePreview.Height));
    }
    private void PaintFrameMetrics(nint dc,Rectangle rect)
    {
        FrameLayout();var header=LocalRect(_frameHeader,_virtualX,_virtualY);
        PaintPanel(dc,header,_frameScale);
        var button=LocalRect(RegionFrameControls.Button(_frameHeader,_frameScale),_virtualX,_virtualY);
        CpuCaptureGdi.Text(dc,$"{rect.X+_virtualX},{rect.Y+_virtualY}   {rect.Width} × {rect.Height} px",new(header.X+DipAt(10,_frameScale),header.Y,Math.Max(1,button.X-header.X-DipAt(12,_frameScale)),header.Height),_toolbarLabelFont,Ink);
        CpuCaptureGdi.Fill(dc,button,_frameStyle.Enabled?ToolbarHover:Paper,DipAt(6,_frameScale));
        CpuCaptureGdi.Text(dc,_frameStyle.Enabled?$"样式 · {_frameStyle.Radius} ⌄":"画面样式 ⌄",button,_toolbarLabelFont,Ink,center:true);
    }
    private void PaintFramePopup(nint dc)
    {
        if(!_framePopup)return;FrameLayout();
        PaintPanel(dc,LocalRect(_framePanel,_virtualX,_virtualY),_frameScale);
        var clip=LocalRect(_framePanel,_virtualX,_virtualY);int saved=SaveDC(dc);
        nint region=CreateRoundRectRgn(clip.Left,clip.Top,clip.Right,clip.Bottom,DipAt(12,_frameScale),DipAt(12,_frameScale));
        try
        {
        SelectClipRgn(dc,region);
        void Label(string text,int y)=>CpuCaptureGdi.Text(dc,text,LocalRect(new(_framePanel.X+12*_frameScale,_framePanel.Y+y*_frameScale-_frameScroll,_framePanel.Width-24*_frameScale,18*_frameScale),_virtualX,_virtualY),_toolbarLabelFont,Ink);
        Label("画面样式",10);Label($"圆角 · {_frameStyle.Radius} px",34);Label("阴影 · 导出时自动留白",92);Label("边框",150);
        var choices=FrameChoices();
        foreach(var choice in choices)
        {
            var r=LocalRect(choice.Bounds,_virtualX,_virtualY);
            CpuCaptureGdi.Fill(dc,r,choice.Active?Accent:ToolbarHover,DipAt(5,_frameScale));
            CpuCaptureGdi.Text(dc,choice.Label,r,_toolbarLabelFont,Ink,center:true);
        }
        if(_frameFocus>=0&&_frameFocus<choices.Count)CpuCaptureGdi.Shape(dc,LocalRect(choices[_frameFocus].Bounds,_virtualX,_virtualY),Border,1);
        Label("透明以输出格式为准 · JPEG / 视频无透明通道",238);
        }
        finally{RestoreDC(dc,saved);CpuCaptureGdi.DeleteObject(region);}
    }
    private bool FramePointerDown()
    {
        if(!_selected)return false;FrameLayout();
        var point=new PointF(_mouse.X+_virtualX,_mouse.Y+_virtualY);
        if(RegionFrameControls.Button(_frameHeader,_frameScale).Contains(point))
        {_framePopup=!_framePopup;_frameFocus=0;_frameScroll=0;_toolbarState.ClosePopup();_toolbarHelp=false;}
        else if(_framePopup)
        {
            var choice=_framePanel.Contains(point)?FrameChoices().FirstOrDefault(c=>c.Bounds.Contains(point)):null;
            if(choice is not null){_frameStyle=_frameStyle.Choose(choice);ClearFramePreview();}
            else if(!_framePanel.Contains(point))CloseFrameStyle();
        }
        else return false;
        _consumeToolbarGesture=_suppressToolbarDoubleClick=true;Redraw();return true;
    }
    private bool FrameKey(int key)
    {
        if(!_framePopup)return false;
        if(key==27){CloseFrameStyle();Redraw();return true;}
        var choices=FrameChoices();
        if(key is 39 or 40 or 9)_frameFocus=(_frameFocus+1)%choices.Count;
        else if(key is 37 or 38)_frameFocus=(_frameFocus+choices.Count-1)%choices.Count;
        else if(key is 13 or 32){_frameStyle=_frameStyle.Choose(choices[_frameFocus]);ClearFramePreview();}
        var focus=choices[_frameFocus].Bounds;
        if(focus.Top<_framePanel.Top)_frameScroll=Math.Max(0,_frameScroll+focus.Top-_framePanel.Top);
        if(focus.Bottom>_framePanel.Bottom)_frameScroll=Math.Min(Math.Max(0,254*_frameScale-_framePanel.Height),_frameScroll+focus.Bottom-_framePanel.Bottom);
        Redraw();return true;
    }
}
