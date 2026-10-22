using System;
using System.Drawing;
using System.Runtime.CompilerServices;

namespace Starshot.Features.Screenshot;

internal static class CpuCaptureFrameStyle
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static CpuCaptureImage Apply(CpuCaptureImage source,RegionFrameStyle style)
    {
        int pad=style.Padding,w=checked(source.Width+2*pad),h=checked(source.Height+2*pad);
        byte[] pixels=new byte[checked(w*h*4)];
        float radius=style.EffectiveRadius(source.Width,source.Height),sigma=style.Shadow;
        // Direct interior copies; geometry/exp work stays in edge strips, not
        // every pixel of a 4K frame. Output is premultiplied BGRA.
        for(int y=0;y<h;y++)
        {
            float sy=y-pad+.5f;
            if(style.Shadow>0)
                for(int x=0;x<w;x++)
                {
                    if(x>=pad+radius&&x<pad+source.Width-radius&&sy>=0&&sy<source.Height)
                    {x=(int)Math.Ceiling(pad+source.Width-radius)-1;continue;}
                    float d=RegionFrameControls.Distance(x-pad+.5f,sy-sigma/2,source.Width,source.Height,radius);
                    float alpha=.32f*MathF.Exp(-Math.Max(0,d)*Math.Max(0,d)/(2*sigma*sigma));
                    pixels[(y*w+x)*4+3]=(byte)Math.Clamp((int)Math.Round(alpha*255),0,255);
                }
            int iy=y-pad;if(iy<0||iy>=source.Height)continue;
            Buffer.BlockCopy(source.Pixels,iy*source.Width*4,pixels,(y*w+pad)*4,source.Width*4);
            int edge=(int)Math.Ceiling(iy<radius||iy>=source.Height-radius?Math.Max(radius,style.Border+1):style.Border+1);
            for(int x=0;x<source.Width;x++)
            {
                if(x>=edge&&x<source.Width-edge&&iy>=style.Border+1&&iy<source.Height-style.Border-1)
                {x=source.Width-edge-1;continue;}
                int index=(y*w+pad+x)*4,src=(iy*source.Width+x)*4;
                float d=RegionFrameControls.Distance(x+.5f,iy+.5f,source.Width,source.Height,radius);
                float coverage=Math.Clamp(.5f-d,0,1),a=source.Pixels[src+3]/255f*coverage;
                float stroke=style.Border>0?coverage*(1-Math.Clamp(-d-style.Border+.5f,0,1)):0;
                float shadow=0;
                if(style.Shadow>0){float sd=RegionFrameControls.Distance(x+.5f,sy-sigma/2,source.Width,source.Height,radius);shadow=.32f*MathF.Exp(-Math.Max(0,sd)*Math.Max(0,sd)/(2*sigma*sigma));}
                for(int c=0;c<3;c++)
                {
                    float color=(style.Color>>(8*c))&255;
                    pixels[index+c]=(byte)Math.Clamp((int)Math.Round(source.Pixels[src+c]*coverage*(1-stroke)+color*stroke),0,255);
                }
                pixels[index+3]=(byte)Math.Clamp((int)Math.Round((stroke+(1-stroke)*(a+(1-a)*shadow))*255),0,255);
            }
        }
        return new(w,h,pixels);
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void FlattenPreview(CpuCaptureImage image,Point origin,byte[] dark,int desktopWidth,int desktopHeight)
    {
        // This is a preview-only copy. Flatten once against the frozen dimmed
        // desktop, then use a GDI blit on pointer/toolbar redraws.
        for(int y=0;y<image.Height;y++)for(int x=0;x<image.Width;x++)
        {
            int p=(y*image.Width+x)*4,dx=origin.X+x,dy=origin.Y+y;
            int inv=255-image.Pixels[p+3];
            if(inv==0)continue;
            int dest=dx>=0&&dx<desktopWidth&&dy>=0&&dy<desktopHeight?(dy*desktopWidth+dx)*4:-1;
            for(int c=0;c<3;c++)image.Pixels[p+c]=(byte)Math.Min(255,image.Pixels[p+c]+(dest<0?0:(dark[dest+c]*inv+127)/255));
            image.Pixels[p+3]=255;
        }
    }
}
