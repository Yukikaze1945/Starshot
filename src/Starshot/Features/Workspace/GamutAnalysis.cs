using System;
using System.Buffers.Binary;
using System.Threading;

namespace Starshot.Features.Workspace;

/// <summary>Chromaticity membership from linear scRGB, preserving negative channels.</summary>
public static class GamutAnalysis
{
    public sealed record RegionMap(int Width,int Height,byte[] Pixels);
    public sealed record Summary(int Samples, int Invalid, int Black, double Srgb, double DisplayP3Only,
        double Bt2020Only, double Outside2020, int[] Density, int Grid, bool Approximate);
    public static Summary Analyze(byte[] fp16, int width, int height, CancellationToken ct = default)
    {
        ImageMask.Validate(width,height);
        if(fp16.Length != width*height*8) throw new ArgumentException("需要原始 FP16 scRGB");
        const int grid=128;
        int[] density=new int[grid*grid];
        int step=Math.Max(1,(int)Math.Ceiling(Math.Sqrt((long)width*height/1_000_000d)));
        int valid=0, invalid=0, black=0, srgb=0,p3=0,bt2020=0,outside=0;
        for(int y=0;y<height;y+=step)
        {
            ct.ThrowIfCancellationRequested();
            for(int x=0;x<width;x+=step)
            {
                int i=(y*width+x)*8;
                float r=H(fp16,i),g=H(fp16,i+2),b=H(fp16,i+4);
                if(!float.IsFinite(r)||!float.IsFinite(g)||!float.IsFinite(b)){invalid++;continue;}
                double X=.4123908*r+.3575843*g+.1804808*b;
                double Y=.212639*r+.7151687*g+.0721923*b;
                double Z=.0193308*r+.1191948*g+.9505322*b, sum=X+Y+Z;
                if(Y<=1e-7 || sum<=1e-7){black++;continue;}
                valid++;
                switch(Classify(r,g,b)){case 0:srgb++;break;case 1:p3++;break;case 2:bt2020++;break;default:outside++;break;}
                int xx=(int)Math.Clamp(X/sum/.8*(grid-1),0,grid-1), yy=(int)Math.Clamp((1-Y/sum/.9)*(grid-1),0,grid-1);
                density[yy*grid+xx]++;
            }
        }
        double denominator=Math.Max(1,valid);
        return new(valid,invalid,black,100*srgb/denominator,100*p3/denominator,100*bt2020/denominator,100*outside/denominator,density,grid,step>1);
    }
    public static RegionMap CreateMap(byte[] fp16,int width,int height,CancellationToken ct=default)
    {
        ImageMask.Validate(width,height);if(fp16.Length!=width*height*8)throw new ArgumentException("需要 FP16 scRGB");
        double scale=Math.Min(1,1024d/Math.Max(width,height));int w=Math.Max(1,(int)(width*scale)),h=Math.Max(1,(int)(height*scale));
        byte[] pixels=new byte[w*h*4];
        byte[,] palette={{90,160,95},{235,135,80},{45,170,245},{80,70,240},{24,29,25},{200,60,220}};
        for(int y=0;y<h;y++)
        {
            ct.ThrowIfCancellationRequested();int sy=Math.Min(height-1,(int)((y+.5)*height/h));
            for(int x=0;x<w;x++)
            {
                int sx=Math.Min(width-1,(int)((x+.5)*width/w)),source=(sy*width+sx)*8,dest=(y*w+x)*4;
                float r=H(fp16,source),g=H(fp16,source+2),b=H(fp16,source+4);
                int region=!float.IsFinite(r)||!float.IsFinite(g)||!float.IsFinite(b)?5:(.2126*r+.7152*g+.0722*b<=1e-7)?4:Classify(r,g,b);
                pixels[dest]=palette[region,0];pixels[dest+1]=palette[region,1];pixels[dest+2]=palette[region,2];pixels[dest+3]=255;
            }
        }
        return new(w,h,pixels);
    }
    private static int Classify(float r,float g,float b)=>Inside(r,g,b)?0:
        Inside(.8225929*r+.177534*g,.0331995*r+.9667835*g,.0170853*r+.0723957*g+.9103015*b)?1:
        Inside(.627404*r+.329282*g+.0433136*b,.069097*r+.91954*g+.0113612*b,.0163916*r+.0880132*g+.895595*b)?2:3;
    // Boundary RGB recovered from FP16 can have cancellation noise after a gamut
    // transform. Use a relative 0.08% tolerance, independent of content exposure.
    private static bool Inside(double r,double g,double b) => Math.Min(r,Math.Min(g,b))>=-Math.Max(1e-10,Math.Max(Math.Abs(r),Math.Max(Math.Abs(g),Math.Abs(b)))*.0008);
    private static float H(byte[] p,int i)=>(float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(i)));
}
