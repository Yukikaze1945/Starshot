using System;
using System.Linq;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using D=System.Drawing;

namespace Starshot.Features.Screenshot;

public sealed partial class RegionCaptureWindow
{
    private RegionFrameStyle _frameStyle=new();
    private bool _framePopup;
    private int _frameFocus;
    private D.RectangleF _frameHeader,_framePanel;
    private string _frameMenuKey="";
    private float _frameScale=1;
    private void CloseFrameStyle(){_framePopup=false;FrameStylePanel.Visibility=Visibility.Collapsed;_frameMenuKey="";}
    private void FrameStyleButton_Click(object sender,RoutedEventArgs e)
    {
        if(_state!=RegionCaptureState.Selected)return;
        _toolbarState.ClosePopup();ToolbarPopup.Visibility=Visibility.Collapsed;
        _toolbarHelpVisible=false;ShortcutHint.Visibility=Visibility.Collapsed;
        _framePopup=!_framePopup;_frameFocus=0;_frameMenuKey="";RequestRedraw();
    }
    private void UpdateFrameMetrics()
    {
        var monitor=_toolbarLayout?.Monitor;
        var area=monitor?.WorkArea??new D.RectangleF(_vx,_vy,(float)(_lockedW*_scale),(float)(_lockedH*_scale));
        _frameScale=monitor?.Scale??(float)_scale;
        _frameHeader=RegionFrameControls.Header(ToolbarPhysicalSelection(),area,_frameScale);
        PlaceToolbarPanel(SelectionMetrics,_frameHeader);
        SelectionMetrics.Padding=new Thickness(ToolbarDip(10*_frameScale),0,ToolbarDip(4*_frameScale),0);
        SelectionMetricsText.MaxWidth=ToolbarDip(Math.Max(50,_frameHeader.Width-128*_frameScale));
        SelectionMetricsText.FontSize=ToolbarDip(12*_frameScale);
        FrameStyleButton.FontSize=ToolbarDip(12*_frameScale);
        FrameStyleButton.Content=_frameStyle.Enabled?$"样式 · {_frameStyle.Radius} px ⌄":"画面样式 ⌄";
        FrameStyleButton.Background=ToolbarBrush(_frameStyle.Enabled?0xffe8ecd9:0x00ffffff);
        FrameStylePanel.Visibility=_framePopup?Visibility.Visible:Visibility.Collapsed;
        if(!_framePopup)return;
        _framePanel=RegionFrameControls.Popup(_frameHeader,area,_frameScale);
        PlaceToolbarPanel(FrameStylePanel,_framePanel);
        FrameStyleChoices.Height=ToolbarDip(254*_frameScale);
        string key=$"{_frameStyle},{_framePanel.Width},{_frameScale}";
        if(key==_frameMenuKey)return;_frameMenuKey=key;
        FrameStyleChoices.Children.Clear();
        void Label(string text,int y,int size=11)
        {
            var label=new TextBlock{Text=text,FontSize=ToolbarDip(size*_frameScale),Foreground=ToolbarBrush(size==11?0xff828679:0xff242b24)};
            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(label,ToolbarDip(12*_frameScale));Microsoft.UI.Xaml.Controls.Canvas.SetTop(label,ToolbarDip(y*_frameScale));FrameStyleChoices.Children.Add(label);
        }
        Label("画面样式",10,13);Label($"圆角 · {_frameStyle.Radius} px",34);Label("阴影 · 导出时自动留白",92);Label("边框",150);
        foreach(var choice in RegionFrameControls.Choices(_framePanel,_frameScale,_frameStyle))
        {
            var button=new Button{Content=choice.Label,Padding=new Thickness(0),MinWidth=0,MinHeight=0,FontSize=ToolbarDip(11*_frameScale),CornerRadius=new(5),
                Background=ToolbarBrush(choice.Active?0xffddf369:0xffeef0e7),Foreground=ToolbarBrush(0xff242b24),BorderThickness=new(0)};
            button.Click+=(_,_)=>{_frameStyle=_frameStyle.Choose(choice);_frameMenuKey="";RequestRedraw();};
            AddToolbarChild(FrameStyleChoices,button,choice.Bounds,_framePanel);
        }
        Label("透明以输出格式为准 · JPEG / 视频无透明通道",240,9);
    }
    private bool FrameKey(VirtualKey key)
    {
        if(!_framePopup)return false;
        if(key==VirtualKey.Escape){CloseFrameStyle();RequestRedraw();return true;}
        var choices=RegionFrameControls.Choices(_framePanel,_frameScale,_frameStyle);
        if(key is VirtualKey.Tab or VirtualKey.Right or VirtualKey.Down)_frameFocus=(_frameFocus+1)%choices.Count;
        else if(key is VirtualKey.Left or VirtualKey.Up)_frameFocus=(_frameFocus+choices.Count-1)%choices.Count;
        else if(key is VirtualKey.Space or VirtualKey.Enter){_frameStyle=_frameStyle.Choose(choices[_frameFocus]);_frameMenuKey="";RequestRedraw();}
        if(_frameFocus<FrameStyleChoices.Children.OfType<Button>().Count())FrameStyleChoices.Children.OfType<Button>().ElementAt(_frameFocus).Focus(FocusState.Keyboard);
        return true;
    }
    private void DrawFrameShadow(CanvasDrawingSession ds,Rect rect)
    {
        if(_state==RegionCaptureState.Selected&&_frameStyle.Shadow>0)
            RegionFrameRenderer.DrawShadow(ds,ds.Device,rect,_frameStyle.EffectiveRadius((int)(rect.Width*_scale),(int)(rect.Height*_scale))/(float)_scale,_frameStyle.Shadow/(float)_scale);
    }
    private CanvasActiveLayer? FrameClip(CanvasDrawingSession ds,Rect rect)
    {
        if(_state!=RegionCaptureState.Selected||!_frameStyle.Enabled)return null;
        float radius=_frameStyle.EffectiveRadius((int)(rect.Width*_scale),(int)(rect.Height*_scale))/(float)_scale;
        using var geometry=CanvasGeometry.CreateRoundedRectangle(ds.Device,rect,radius,radius);
        return ds.CreateLayer(1,geometry);
    }
}
