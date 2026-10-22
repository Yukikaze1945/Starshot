using System;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.UI;

namespace Starshot.Features.Screenshot;

internal static class RegionFrameRenderer
{
    internal static CanvasRenderTarget Apply(CanvasBitmap source,RegionFrameStyle style,float white=80)
    {
        int w=(int)source.SizeInPixels.Width,h=(int)source.SizeInPixels.Height,pad=style.Padding;
        var target=new CanvasRenderTarget(source.Device,w+pad*2,h+pad*2,96,source.Format,CanvasAlphaMode.Premultiplied);
        try
        {
            using var ds=target.CreateDrawingSession();ds.Clear(Color.FromArgb(0,0,0,0));
            var rect=new Rect(pad,pad,w,h);float r=style.EffectiveRadius(w,h);
            DrawShadow(ds,source.Device,rect,r,style.Shadow);
            using(var geometry=CanvasGeometry.CreateRoundedRectangle(source.Device,rect,r,r))
            using(var layer=ds.CreateLayer(1,geometry))ds.DrawImage(source,pad,pad);
            DrawBorder(ds,source.Device,rect,r,style,white,source.Format==Windows.Graphics.DirectX.DirectXPixelFormat.R16G16B16A16Float);
            return target;
        }
        catch{target.Dispose();throw;}
    }
    internal static void DrawShadow(CanvasDrawingSession ds,CanvasDevice device,Rect rect,float radius,float size)
    {
        if(size<=0)return;
        using var mask=new CanvasCommandList(device);
        using(var draw=mask.CreateDrawingSession())draw.FillRoundedRectangle(rect,radius,radius,Color.FromArgb(255,255,255,255));
        using var shadow=new ShadowEffect{Source=mask,BlurAmount=size,ShadowColor=Color.FromArgb(82,0,0,0)};
        ds.DrawImage(shadow,0,size/2);
    }
    internal static void DrawBorder(CanvasDrawingSession ds,CanvasDevice device,Rect rect,float radius,RegionFrameStyle style,float white,bool hdr=false,float unit=1)
    {
        if(style.Border<=0)return;
        float width=Math.Min(style.Border*unit,(float)Math.Min(rect.Width,rect.Height)/2),half=width/2f;
        var inset=new Rect(rect.X+half,rect.Y+half,Math.Max(0,rect.Width-width),Math.Max(0,rect.Height-width));
        float Linear(uint value){float x=value/255f;return x<=.04045f?x/12.92f:MathF.Pow((x+.055f)/1.055f,2.4f);}
        using var brush=new CanvasSolidColorBrush(device,Color.FromArgb(255,(byte)(style.Color>>16),(byte)(style.Color>>8),(byte)style.Color));
        if(hdr)
            brush.ColorHdr=new Vector4(Linear((style.Color>>16)&255)*white/80,Linear((style.Color>>8)&255)*white/80,Linear(style.Color&255)*white/80,1);
        ds.DrawRoundedRectangle(inset,Math.Max(0,radius-half),Math.Max(0,radius-half),brush,width);
    }
}
