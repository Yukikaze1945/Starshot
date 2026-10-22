using System;
using System.Linq;
using System.Threading;

namespace Starshot.Features.DocumentOcr;

internal sealed record DocumentImage(byte[] Pixels,int Width,int Height,int Stride=0)
{
    internal int RowBytes=>Stride==0?checked(Width*4):Stride;
    internal void Validate()
    {
        if(Width<1||Height<1||(long)Width*Height>40_000_000||RowBytes<checked(Width*4)||Pixels.LongLength<(long)RowBytes*Height)
            throw new ArgumentException("图像尺寸或 BGRA stride 不兼容。");
    }
    internal DocumentImage Crop(PixelBox box)
    {
        int x=Math.Clamp((int)Math.Floor(box.X),0,Width-1),y=Math.Clamp((int)Math.Floor(box.Y),0,Height-1);
        int w=Math.Clamp((int)Math.Ceiling(box.Right)-x,1,Width-x),h=Math.Clamp((int)Math.Ceiling(box.Bottom)-y,1,Height-y);
        var data=new byte[checked(w*h*4)];for(int r=0;r<h;r++)Pixels.AsSpan((y+r)*RowBytes+x*4,w*4).CopyTo(data.AsSpan(r*w*4));
        return new(data,w,h);
    }
    internal byte[] Gray(CancellationToken ct)
    {
        Validate();var gray=new byte[checked(Width*Height)];
        for(int y=0;y<Height;y++){ct.ThrowIfCancellationRequested();for(int x=0;x<Width;x++)
        {int i=y*RowBytes+x*4,a=Pixels[i+3];
            // Composite premultiplied SDR BGRA on white, then use BT.601 luminance.
            int r=Math.Min(255,Pixels[i+2]+255-a),g=Math.Min(255,Pixels[i+1]+255-a),b=Math.Min(255,Pixels[i]+255-a);
            gray[y*Width+x]=(byte)((19595*r+38470*g+7471*b+32768)>>16);}}
        return gray;
    }
}
internal sealed record GrayImage(byte[] Pixels,int Width,int Height)
{
    internal GrayImage Resize(int w,int h,bool? bilinear=null)
    {
        if(w==Width&&h==Height)return this;
        var result=new byte[checked(w*h)];
        // Separable Pillow-style antialiased bilinear / Lanczos-3 resampling.
        bool up=bilinear??(w>Width||h>Height);double radius=up?1:3;
        (int Start,double[] Weights)[] Weights(int input,int output)
        {
            double scale=input/(double)output,filter=Math.Max(1,scale),support=radius*filter;
            return Enumerable.Range(0,output).Select(x=>{double center=(x+.5)*scale;int left=Math.Max(0,(int)(center-support+.5)),right=Math.Min(input,(int)(center+support+.5));
                var weights=new double[Math.Max(1,right-left)];double sum=0;
                for(int k=0;k<weights.Length;k++){double z=(k+left-center+.5)/filter,abs=Math.Abs(z);double value=up?Math.Max(0,1-abs):abs<1e-12?1:abs<3?Math.Sin(Math.PI*z)*Math.Sin(Math.PI*z/3)/(Math.PI*Math.PI*z*z/3):0;weights[k]=value;sum+=value;}
                for(int k=0;k<weights.Length;k++)weights[k]/=sum;return(left,weights);}).ToArray();
        }
        var wx=Weights(Width,w);var wy=Weights(Height,h);var temp=new byte[w*Height];
        for(int y=0;y<Height;y++)for(int x=0;x<w;x++){double sum=0;var v=wx[x];for(int k=0;k<v.Weights.Length;k++)sum+=Pixels[y*Width+Math.Min(Width-1,v.Start+k)]*v.Weights[k];temp[y*w+x]=(byte)Math.Clamp((int)Math.Round(sum),0,255);}
        for(int y=0;y<h;y++)for(int x=0;x<w;x++){double sum=0;var v=wy[y];for(int k=0;k<v.Weights.Length;k++)sum+=temp[Math.Min(Height-1,v.Start+k)*w+x]*v.Weights[k];result[y*w+x]=(byte)Math.Clamp((int)Math.Round(sum),0,255);}
        return new(result,w,h);
    }
    internal GrayImage Bound()
    {
        double ratio=Math.Max(Width/672d,Height/192d);var image=ratio>1?Resize(Math.Max(1,(int)Math.Floor(Width/ratio)),Math.Max(1,(int)Math.Floor(Height/ratio)),true):this;
        return image.Pad(Math.Max(32,image.Width),Math.Max(32,image.Height));
    }
    internal GrayImage Pad(int width,int height)
    {var p=new byte[width*height];Array.Fill(p,(byte)255);for(int y=0;y<Height;y++)Pixels.AsSpan(y*Width,Width).CopyTo(p.AsSpan(y*width));return new(p,width,height);}
    internal GrayImage? TightPad()
    {
        int min=Pixels.Min(v=>(int)v),max=Pixels.Max(v=>(int)v);if(max-min<2)return null;
        var normalized=Pixels.Select(v=>(byte)((v-min)*255d/(max-min))).ToArray();bool invert=normalized.Average(v=>(double)v)<=128;
        int left=Width,top=Height,right=-1,bottom=-1;
        for(int y=0;y<Height;y++)for(int x=0;x<Width;x++){int i=y*Width+x;if(invert)normalized[i]=(byte)(255-normalized[i]);if(normalized[i]<128){left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);}}
        if(right<left||bottom<top)return null;
        int w=right-left+1,h=bottom-top+1;var p=new byte[w*h];for(int y=0;y<h;y++)normalized.AsSpan((y+top)*Width+left,w).CopyTo(p.AsSpan(y*w));
        return new GrayImage(p,w,h).Pad((w+31)/32*32,(h+31)/32*32);
    }
}
