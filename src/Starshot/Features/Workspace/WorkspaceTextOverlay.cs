using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Starshot.Features.Screenshot;

namespace Starshot.Features.Workspace;

/// <summary>Explicit export only. Never changes the source or analysis buffers.</summary>
internal static class WorkspaceTextOverlay
{
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int DrawTextW(nint dc,string text,int count,ref Rect rect,uint format);
    [DllImport("gdi32.dll")]private static extern nint SelectObject(nint dc,nint obj);
    [DllImport("gdi32.dll")]private static extern int SetBkMode(nint dc,int mode);
    [DllImport("gdi32.dll")]private static extern uint SetTextColor(nint dc,uint color);
    internal static byte[] Render(byte[] source,int width,int height,JsonElement overlays,CancellationToken ct)
    {
        if(overlays.GetArrayLength()>2048)throw new ArgumentException("翻译覆盖行过多");
        using var surface=new CpuCaptureSurface(width,height);source.CopyTo(surface.Pixels);
        foreach(var item in overlays.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();string text=item.GetProperty("text").GetString() ?? "";
            if(text.Length>10000)throw new ArgumentException("覆盖文字过长");
            double Number(string name,double fallback)=>item.TryGetProperty(name,out var value)&&value.TryGetDouble(out double n)&&double.IsFinite(n) ? n : fallback;
            int x=(int)Math.Clamp(Number("x",0),0,width-1),y=(int)Math.Clamp(Number("y",0),0,height-1);
            int w=(int)Math.Clamp(Number("width",1),1,width-x),h=(int)Math.Clamp(Number("height",1),1,height-y);
            bool dark=item.TryGetProperty("dark",out var darkValue)&&darkValue.ValueKind==JsonValueKind.True;
            var rect=new Rectangle(x,y,w,h);CpuCaptureGdi.Fill(surface.Dc,rect,dark?Color.FromArgb(30,32,30):Color.White);
            nint font=CpuCaptureGdi.Font((int)Math.Clamp(Number("fontSize",h*.78),6,256));
            nint previous=SelectObject(surface.Dc,font);int mode=SetBkMode(surface.Dc,1);uint color=SetTextColor(surface.Dc,dark?0xffffffu:0x181c18u);
            try{var bounds=new Rect{Left=x+2,Top=y,Right=x+w-2,Bottom=y+h};DrawTextW(surface.Dc,text,text.Length,ref bounds,0x10|0x800|0x2000);}
            finally{SetTextColor(surface.Dc,color);SetBkMode(surface.Dc,mode);SelectObject(surface.Dc,previous);CpuCaptureGdi.DeleteObject(font);}
            CpuCaptureGdi.GdiFlush();
            for(int yy=y;yy<y+h;yy++)for(int xx=x;xx<x+w;xx++)surface.Pixels[(yy*width+xx)*4+3]=255;
        }
        CpuCaptureGdi.GdiFlush();return surface.Pixels.ToArray();
    }
}
