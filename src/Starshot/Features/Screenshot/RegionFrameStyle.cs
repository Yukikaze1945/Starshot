using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.CompilerServices;

namespace Starshot.Features.Screenshot;

public sealed record RegionFrameStyle(int Radius=0,int Shadow=0,int Border=0,uint Color=0xffffffff)
{
    public bool Enabled=>Radius>0||Shadow>0||Border>0;
    public int Padding=>Shadow>0?Shadow*3+Shadow/2:0;
    public float EffectiveRadius(int width,int height)=>Math.Min(Math.Clamp(Radius,0,256),Math.Min(width,height)/2f);
    public static bool Applies(RegionCaptureAction action)=>action is RegionCaptureAction.Copy or RegionCaptureAction.Save or RegionCaptureAction.QuickSave or RegionCaptureAction.Pin;
    internal RegionFrameStyle Choose(FrameChoice c)=>c.Field switch
    {
        FrameField.Radius=>this with{Radius=Math.Clamp(c.Value,0,256)},
        FrameField.Shadow=>this with{Shadow=c.Value},FrameField.Border=>this with{Border=c.Value},
        FrameField.Color=>this with{Color=unchecked((uint)c.Value)},_=>new()
    };
}
internal enum FrameField{Radius,Shadow,Border,Color,Reset}
internal sealed record FrameChoice(FrameField Field,int Value,string Label,RectangleF Bounds,bool Active);
internal static class RegionFrameControls
{
    public static RectangleF Header(RectangleF selection,RectangleF area,float scale)
    {
        float w=Math.Min(390*scale,area.Width),h=30*scale;
        return new(Math.Clamp(selection.X,area.Left,Math.Max(area.Left,area.Right-w)),
            Math.Clamp(selection.Top-h-6*scale,area.Top,Math.Max(area.Top,area.Bottom-h)),w,h);
    }
    public static RectangleF Button(RectangleF header,float s)=>new(header.Right-98*s,header.Y,98*s,header.Height);
    public static RectangleF Popup(RectangleF header,RectangleF area,float s)
    {
        float w=Math.Min(332*s,area.Width),h=Math.Min(254*s,area.Height);
        float y=header.Top-h-6*s;
        if(y<area.Top)y=header.Bottom+6*s;
        return new(Math.Clamp(header.Right-w,area.Left,Math.Max(area.Left,area.Right-w)),Math.Clamp(y,area.Top,Math.Max(area.Top,area.Bottom-h)),w,h);
    }
    public static IReadOnlyList<FrameChoice> Choices(RectangleF p,float s,RegionFrameStyle style)
    {
        var list=new List<FrameChoice>();
        void Row(FrameField field,string[] labels,int[] values,int y,int selected)
        {
            float width=(p.Width-24*s)/labels.Length;
            for(int i=0;i<labels.Length;i++)list.Add(new(field,values[i],labels[i],new(p.X+12*s+i*width,p.Y+y*s,width-4*s,28*s),selected==values[i]&&labels[i] is not("−" or "+")));
        }
        Row(FrameField.Radius,["直角","12","24","40","60","−","+"],[0,12,24,40,60,Math.Max(0,style.Radius-2),Math.Min(256,style.Radius+2)],54,style.Radius);
        Row(FrameField.Shadow,["无","轻柔","悬浮","深邃"],[0,8,16,24],112,style.Shadow);
        Row(FrameField.Border,["无","1 px","2 px","4 px"],[0,1,2,4],170,style.Border);
        Row(FrameField.Color,["白","墨","强调色","重置"],[unchecked((int)0xffffffff),unchecked((int)0xff242b24),unchecked((int)0xffddf369),0],208,unchecked((int)style.Color));
        list[^1]=list[^1] with{Field=FrameField.Reset,Active=false};
        return list;
    }
    // Signed distance to a rounded rectangle. Used by the CPU alpha renderer;
    // negative RGB/HDR pixels never enter this geometric calculation.
    [MethodImpl(MethodImplOptions.AggressiveInlining|MethodImplOptions.AggressiveOptimization)]
    internal static float Distance(float x,float y,float w,float h,float r)
    {
        float qx=Math.Abs(x-w/2)-(w/2-r),qy=Math.Abs(y-h/2)-(h/2-r);
        float ox=Math.Max(qx,0),oy=Math.Max(qy,0);
        return MathF.Sqrt(ox*ox+oy*oy)+Math.Min(Math.Max(qx,qy),0)-r;
    }
}
