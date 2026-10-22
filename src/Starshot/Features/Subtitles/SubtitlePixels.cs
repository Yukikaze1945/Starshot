using System;

namespace Starshot.Features.Subtitles;

internal sealed record SubtitlePixels(byte[] Pixels,int Width,int Height)
{
    internal float[] Signature()
    {
        if(Width<=0||Height<=0||Pixels.Length!=checked(Width*Height*4))throw new ArgumentException("字幕区域 BGRA 数据无效。");
        int cols=(Width+7)/8,rows=(Height+7)/8;var values=new float[cols*rows];
        for(int cy=0;cy<rows;cy++)for(int cx=0;cx<cols;cx++)
        {
            float sum=0;int n=0;
            for(int y=cy*8;y<Math.Min(Height,cy*8+8);y++)for(int x=cx*8;x<Math.Min(Width,cx*8+8);x++)
            {int p=(y*Width+x)*4;sum+=Pixels[p]*.0722f+Pixels[p+1]*.7152f+Pixels[p+2]*.2126f;n++;}
            values[cy*cols+cx]=sum/n;
        }
        return values;
    }
    internal static bool Changed(float[]? previous,float[] current)
    {
        if(previous is null||previous.Length!=current.Length)return true;int changes=0;
        for(int i=0;i<current.Length;i++){float delta=Math.Abs(current[i]-previous[i]);if(delta>=8||delta>=2&&++changes>=2)return true;}
        return false;
    }
}
