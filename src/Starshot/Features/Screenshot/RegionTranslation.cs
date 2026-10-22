using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Starshot.Helpers;

namespace Starshot.Features.Screenshot;

internal sealed record RegionTranslatedText(RectangleF Bounds,string Text,uint Background,uint Foreground,float FontSize);

internal static class RegionTranslation
{
    // Icons can produce an empty REC result (the reported settings screenshot
    // produces two with Small). They are not translation segments.
    internal static IEnumerable<OcrLine> TextLines(IEnumerable<OcrLine> lines)=>lines.Where(l=>
        !string.IsNullOrWhiteSpace(l.Text)&&l.Rect.Width>0&&l.Rect.Height>0);

    internal static async Task<RegionTranslatedText[]> TranslateAsync(byte[] pixels,int width,int height,CancellationToken ct)
    {
        var lines=TextLines(await OcrHelper.RecognizeAsync(pixels,width,height,1,ct).ConfigureAwait(false)??[]).ToArray();
        if(lines.Length==0)throw new InvalidOperationException("没有识别到可翻译的文字，请重新框选清晰文字。");
        var segments=lines.Select((l,i)=>new OcrTranslationClient.Segment(i.ToString(CultureInfo.InvariantCulture),l.Text)).ToArray();
        var translated=await OcrTranslationClient.TranslateFormattedAsync(segments,AppConfig.TranslationTargetLanguage,ct).ConfigureAwait(false);
        var values=translated.ToDictionary(s=>s.Id,s=>s.Text);
        return lines.Select((l,i)=>
        {
            string text=values[i.ToString(CultureInfo.InvariantCulture)];
            var b=RectangleF.Intersect(new((float)l.Rect.X-1,(float)l.Rect.Y-1,(float)l.Rect.Width+2,(float)l.Rect.Height+2),new(0,0,width,height));
            int r=0,g=0,blue=0;
            foreach(var p in new[]{new PointF(b.Left,b.Top),new PointF(b.Right-1,b.Top),new PointF(b.Left,b.Bottom-1),new PointF(b.Right-1,b.Bottom-1)})
            {int offset=(Math.Clamp((int)p.Y,0,height-1)*width+Math.Clamp((int)p.X,0,width-1))*4;blue+=pixels[offset];g+=pixels[offset+1];r+=pixels[offset+2];}
            r/=4;g/=4;blue/=4;uint bg=0xff000000u|(uint)(r<<16|g<<8|blue);
            uint fg=(r*.2126+g*.7152+blue*.0722)>140?0xff202420u:0xfff5f6f0u;
            double units=text.Sum(c=>c>0x2e7f?1d:.55d);
            float size=Math.Clamp(Math.Min(b.Height*.78f,(b.Width-2)/(float)Math.Max(1,units)),6,128);
            return new RegionTranslatedText(b,text,bg,fg,size);
        }).ToArray();
    }
}
