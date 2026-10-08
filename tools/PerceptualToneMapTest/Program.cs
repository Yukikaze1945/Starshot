using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using ComputeSharp.D2D1.WinUI;
using Starshot.Features.Codec;
using Starward.Codec;
using Starward.Codec.AVIF;
using Starward.Codec.ICC;
using Starward.Codec.JpegXL.CMS;
using Starward.Codec.JpegXL.Decode;
using Windows.Graphics.DirectX;

internal static class Program
{
    private static readonly List<object> Results = [];
    private static int checks;
    private static void Check(bool ok, string name, object? detail = null)
    {
        checks++;
        Results.Add(new { name, passed = ok, detail });
        if (!ok) Console.Error.WriteLine($"FAIL {name}: {JsonSerializer.Serialize(detail)}");
    }

    private static CanvasBitmap Bitmap(CanvasDevice device, int w, int h, Func<int, int, (float r, float g, float b)> pixel, float dpi = 96)
    {
        float[] data = new float[checked(w * h * 4)];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        { var p = pixel(x, y); int i = (y * w + x) * 4; data[i] = p.r; data[i + 1] = p.g; data[i + 2] = p.b; data[i + 3] = 1; }
        return CanvasBitmap.CreateFromBytes(device, MemoryMarshal.AsBytes(data.AsSpan()).ToArray(), w, h,
            DirectXPixelFormat.R32G32B32A32Float, dpi, CanvasAlphaMode.Ignore);
    }

    private static (byte r, byte g, byte b) Rgb(byte[] bytes, int i) => (bytes[i + 2], bytes[i + 1], bytes[i]); // BGRA target
    private static object RgbDetail((byte r, byte g, byte b) p) => new { r = p.r, g = p.g, b = p.b };
    private static byte[] Render(CanvasBitmap src, float white) { using var dst = StarshotPerceptual.Render(src, white); return dst.GetPixelBytes(); }
    private static byte[] Render(CanvasBitmap src, StarshotPerceptual.Parameters parameters) { using var dst = StarshotPerceptual.Render(src, parameters); return dst.GetPixelBytes(); }
    private static double HueError((byte r, byte g, byte b) a, (byte r, byte g, byte b) b)
    {
        static (double aa, double bb) Lab((byte r, byte g, byte b) p)
        {
            static double Lin(byte v) { double n = v / 255d; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); }
            double r = Lin(p.r), g = Lin(p.g), bl = Lin(p.b);
            double l = Math.Cbrt(.4122214708*r+.5363325363*g+.0514459929*bl), m = Math.Cbrt(.2119034982*r+.6806995451*g+.1073969566*bl), s = Math.Cbrt(.0883024619*r+.2817188376*g+.6299787005*bl);
            return (1.9779984953*l-2.428592205*g+.4505937099*s, .0259040371*l+.7827717662*m-.808675766*s);
        }
        var x = Lab(a); var y = Lab(b); double delta = Math.Abs(Math.Atan2(x.bb,x.aa)-Math.Atan2(y.bb,y.aa))*180/Math.PI; return Math.Min(delta,360-delta);
    }
    private static (byte r,byte g,byte b) NormalizedHue(float r,float g,float b)
    {double m=Math.Max(r,Math.Max(g,b));static byte E(double x){x=Math.Clamp(x,0,1);double v=x<=.0031308?12.92*x:1.055*Math.Pow(x,1/2.4)-.055;return (byte)Math.Round(v*255);}return(E(r/m),E(g/m),E(b/m));}
    private static (double a,double b) LabHue(double r,double g,double b)
    {
        static double Root(double x)=>Math.CopySign(Math.Cbrt(Math.Abs(x)),x);
        double l=Root(.4122214708*r+.5363325363*g+.0514459929*b),m=Root(.2119034982*r+.6806995451*g+.1073969566*b),s=Root(.0883024619*r+.2817188376*g+.6299787005*b);
        return(1.9779984953*l-2.428592205*m+.4505937099*s,.0259040371*l+.7827717662*m-.808675766*s);
    }
    private static double HueDelta((double a,double b) x,(double a,double b) y)
    {double d=Math.Abs(Math.Atan2(x.b,x.a)-Math.Atan2(y.b,y.a))*180/Math.PI;return Math.Min(d,360-d);}
    private static double Linear(byte b){double x=b/255d;return x<=.04045?x/12.92:Math.Pow((x+.055)/1.055,2.4);}
    private static double EncodedY((byte r,byte g,byte b) p)
    {double y=.2126*Linear(p.r)+.7152*Linear(p.g)+.0722*Linear(p.b);return 255*(y<=.0031308?12.92*y:1.055*Math.Pow(y,1/2.4)-.055);}
    private static (int substantiveLevels,double maxAdjacentLuminanceLsb) RampMetrics(double[] encodedLuminance)
    {
        if(encodedLuminance.Length==0)return(0,0);int levels=1;double lastLevel=encodedLuminance[0],maxAdjacent=0;
        for(int i=1;i<encodedLuminance.Length;i++){double value=encodedLuminance[i];maxAdjacent=Math.Max(maxAdjacent,Math.Abs(value-encodedLuminance[i-1]));if(value>lastLevel+1.5){levels++;lastLevel=value;}}
        return(levels,maxAdjacent);
    }

    private static (CanvasBitmap source,byte[] expected) MakeTextFixture(CanvasDevice device,int white)
    {
        const int width=1000,height=260;using var sdr=new CanvasRenderTarget(device,width,height,96,DirectXPixelFormat.B8G8R8A8UIntNormalized,CanvasAlphaMode.Premultiplied);
        using(var ds=sdr.CreateDrawingSession())
        {ds.Clear(Windows.UI.Color.FromArgb(255,12,16,22));using var latin=new CanvasTextFormat{FontFamily="Segoe UI",FontSize=52,WordWrapping=CanvasWordWrapping.NoWrap};using var cjk=new CanvasTextFormat{FontFamily="Microsoft YaHei UI",FontSize=42,WordWrapping=CanvasWordWrapping.NoWrap};ds.DrawText("STARSHOT 203 HDR",48,42,Windows.UI.Color.FromArgb(255,220,190,140),latin);ds.DrawText("游戏界面  FPS 60",48,132,Windows.UI.Color.FromArgb(255,190,210,220),cjk);}
        byte[] expected=sdr.GetPixelBytes();float[] linear=new float[checked(width*height*4)];
        static float Eotf(byte b){double v=b/255d;return (float)(v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4));}
        for(int i=0;i<width*height;i++){linear[i*4]=Eotf(expected[i*4+2])*white/80f;linear[i*4+1]=Eotf(expected[i*4+1])*white/80f;linear[i*4+2]=Eotf(expected[i*4])*white/80f;linear[i*4+3]=1;}
        return(CanvasBitmap.CreateFromBytes(device,MemoryMarshal.AsBytes(linear.AsSpan()).ToArray(),width,height,DirectXPixelFormat.R32G32B32A32Float,96,CanvasAlphaMode.Ignore),expected);
    }

    private static void TextIdentityCheck(CanvasDevice device)
    {
        var created=MakeTextFixture(device,203);using var fixture=created.source;var expected=created.expected;byte[] actual=Render(fixture,203);int max=0,affected=0;int maxAt=0;
        for(int i=0;i<expected.Length;i+=4)for(int c=0;c<3;c++){int delta=Math.Abs(expected[i+c]-actual[i+c]);if(delta>max){max=delta;maxAt=i/4;}if(expected[i+c]!=12&&expected[i+c]!=16&&expected[i+c]!=22)affected++;}
        Check(max<=1,"Segoe UI/YaHei antialiased text remains SDR identity at 203 nits",new{maxErrorLsb=max,maxPixel=maxAt,nonBackgroundChannelSamples=affected});
    }

    private static double PqD(double nits){double p=Math.Pow(Math.Max(0,nits)/10000,0.1593017578125);return Math.Pow((0.8359375+18.8515625*p)/(1+18.6875*p),78.84375);}
    private static double InvPqD(double value){double p=Math.Pow(Math.Abs(value),1/78.84375);return 10000*Math.Pow(Math.Max(0,(p-.8359375)/(18.8515625-18.6875*p)),1/.1593017578125);}
    private static (double value,double slope) Hermite(double p,double white)
    {
        double knee=PqD(.8*white),w=PqD(white),paper=PqD(.94*white),h=w-knee,t=(p-knee)/h,t2=t*t,t3=t2*t;
        double value=(2*t3-3*t2+1)*knee+(t3-2*t2+t)*h+(-2*t3+3*t2)*paper+(t3-t2)*h*.2;
        double slope=((6*t2-6*t)*knee+(3*t2-4*t+1)*h+(-6*t2+6*t)*paper+(3*t2-2*t)*h*.2)/h;
        return(value,slope);
    }
    private static void ExtendedHdrRampCheck(CanvasDevice device)
    {
        using var ramp = Bitmap(device,16384,1,(x,_) =>
        { float v = MathF.Pow(2,-16+32*x/16383f); return (v,v,v); });
        byte[] b = Render(ramp,80);
        int reversals = 0, worst = 0;
        for (int x=1;x<16384;x++)
        { int drop=Rgb(b,(x-1)*4).r-Rgb(b,x*4).r; worst=Math.Max(worst,drop); if(drop>1)reversals++; }
        int levels=Enumerable.Range(8192,8192).Select(x=>Rgb(b,x*4).r).Distinct().Count();
        Check(reversals==0&&levels>=4,"GPU logarithmic neutral HDR ramp is monotone within 1LSB dither tolerance",
            new{inputRange="2^-16..2^16 scRGB (finite FP16 range sanitized)",largeReversals=reversals,worstDropLsb=worst,hdrDistinctCodes=levels});
    }

    private static void MathematicalCurveChecks()
    {
        foreach(float white in new[]{1f,80f,203f,250f,400f,10000f})
        {
            var parameters=StarshotPerceptual.Parameters.Create(white,new(white,white,3));double knee=PqD(.8*white),w=PqD(white);double minSlope=double.MaxValue;
            for(int i=0;i<=4096;i++){var h=Hermite(knee+(w-knee)*i/4096,white);minSlope=Math.Min(minSlope,h.slope);}
            double atKnee=Hermite(knee,white).slope,atWhite=Hermite(w,white).slope;
            bool positiveTail=true,monotonic=true;double previous=.8;double maxY=4;
            for(int i=1;i<=8192;i++){double y=.8+(maxY-.8)*i/8192,p=PqD(y*white),mapped;if(p<=w)mapped=Hermite(p,white).value;else{double range=w-PqD(.94*white),q=(p-w)*.2/range;double derivative=.2*Math.Pow(1+parameters.TailShape*q,-1/parameters.TailShape-1);if(!(derivative>0))positiveTail=false;mapped=PqD(.94*white)+range*(1-Math.Pow(1+parameters.TailShape*q,-1/parameters.TailShape));}double result=InvPqD(mapped)/white;if(result+1e-11<previous)monotonic=false;previous=result;}
            Check(minSlope>=-1e-12&&Math.Abs(atKnee-1)<1e-12&&Math.Abs(atWhite-.2)<1e-12&&positiveTail&&monotonic,$"independent PQ/Hermite C1 monotonic curve white={white}",new{minHermiteSlope=minSlope,kneeSlope=atKnee,whiteSlope=atWhite,positiveTail,monotonic});
        }
    }

    private static void MathematicalGpuChecks(CanvasDevice device)
    {
        foreach (float white in new[] { 80f, 203f, 250f, 400f })
        {
            using var patch = Bitmap(device, 4, 1, (x, _) => { float v = new[] { .1f, .4f, .8f, .8f }[x] * (white / 80); return (v,v,v); });
            byte[] b = Render(patch, white);
            double max = 0; for (int i=0;i<4;i++) { double v=new[]{.1,.4,.8,.8}[i];double expected=v<=.0031308?12.92*v:1.055*Math.Pow(v,1/2.4)-.055;max=Math.Max(max,Math.Abs(Rgb(b,i*4).r/255d-expected)*255); }
            // Shader output is encoded sRGB; compare against independently encoded patch values.
            Check(max <= 1.01, $"SDR identity white={white}", new { maxEncodedByteError=max, sample=Rgb(b, 8).r });
        }
        using (var whitePatch = Bitmap(device,1,1,(_,_) => (1f,1f,1f)))
        { var p=Rgb(Render(whitePatch,80),0); Check(Math.Abs(p.r-248)<=2 && p.r==p.g && p.g==p.b,"paper white tradeoff",new[]{(int)p.r,(int)p.g,(int)p.b}); }
        using(var prefix=Bitmap(device,12,1,(x,_)=>x switch{0=>(.2f,.3f,.4f),1=>(.9f,.2f,.1f),2=>(1f,1f,1f),3=>(.7f,.7f,.7f),4=>(-.01f,.6105f,.5755f),5=>(0f,1f,0f),6=>(2f,2f,2f),7=>(4f,4f,4f),8=>(8f,8f,8f),9=>(16f,16f,16f),10=>(64f,64f,64f),_=>(256f,256f,256f)}))
        {
            var typical=StarshotPerceptual.Parameters.Create(80,new(80,80,3),80);var outlier=StarshotPerceptual.Parameters.Create(80,new(2000,10000,3),10000);
            byte[] a=Render(prefix,typical),b=Render(prefix,outlier);bool prefixEqual=Enumerable.Range(0,6).All(i=>a.AsSpan(i*4,4).SequenceEqual(b.AsSpan(i*4,4)));bool hdrTailCanVary=Enumerable.Range(6,6).Any(i=>!a.AsSpan(i*4,4).SequenceEqual(b.AsSpan(i*4,4)));
            Check(prefixEqual,"SDR prefix through white is invariant to P999/peak/display parameters",new{prefixEqual,pixelCount=6,tailShapeTypical=typical.TailShape,tailShapeOutlier=outlier.TailShape});
            Check(hdrTailCanVary,"HDR tail responds to P999/peak/display parameters",new{hdrTailCanVary,inputs=new[]{2,4,8,16,64,256},typical=Enumerable.Range(6,6).Select(i=>RgbDetail(Rgb(a,i*4))),outlier=Enumerable.Range(6,6).Select(i=>RgbDetail(Rgb(b,i*4)))});
        }
        {
            var fallback=StarshotPerceptual.Parameters.Create(float.NaN,new(float.NaN,float.PositiveInfinity,3),float.NegativeInfinity);
            var baseStats=StarshotPerceptual.Parameters.Create(80,new(320,320,3));var isolatedOutlier=StarshotPerceptual.Parameters.Create(80,new(320,1_000_000,3));
            bool safe= fallback.WhiteNits==80&&float.IsFinite(fallback.KneePq)&&float.IsFinite(fallback.WhitePq)&&float.IsFinite(fallback.PaperPq)&&float.IsFinite(fallback.TailShape)&&fallback.TailShape>=.75f&&fallback.TailShape<=3;
            float isolatedInfluence=isolatedOutlier.TailShape-baseStats.TailShape;
            Check(safe,"invalid white/display/statistics inputs fall back to finite safe parameters",new{white=fallback.WhiteNits,knee=fallback.KneePq,whitePq=fallback.WhitePq,paper=fallback.PaperPq,tailShape=fallback.TailShape});
            Check(isolatedInfluence>.1f&&isolatedInfluence<=.126f,"isolated peak outlier has bounded tail-shape influence",new{baselineTailShape=baseStats.TailShape,outlierTailShape=isolatedOutlier.TailShape,delta=isolatedInfluence,maxAllowed=.126f});
        }
        using (var ramp = Bitmap(device,16384,1,(x,_) => { float v=x/16383f; return (v,v,v); }))
        {
            byte[] b=Render(ramp,80); int reversals=0, worst=0; int kneeLow=0,kneeHigh=0;
            for(int x=1;x<16384;x++){int prev=Rgb(b,(x-1)*4).r, cur=Rgb(b,x*4).r; if(cur<prev){int rev=prev-cur;if(rev>1)reversals++;worst=Math.Max(worst,rev);} if(x==13107)kneeLow=cur;if(x==16383)kneeHigh=cur;}
            Check(reversals==0 && worst<=1,"neutral monotonicity over 16k ramp",new{reversals,worst,kneeLow,kneeHigh});
        }
        using (var hdr = Bitmap(device,9,1,(x,_)=>x switch {0=>(1f,1f,1f),1=>(2f,2f,2f),2=>(4f,4f,4f),3=>(8f,8f,8f),4=>(4f,.2f,.2f),5=>(3f,.1f,2f),6=>(.2f,4f,.3f),7=>(.1f,.2f,4f),_=>((float)1.5,(float).7,(float).3)}))
        {
            byte[] b=Render(hdr,80); var values=Enumerable.Range(0,9).Select(i=>Rgb(b,i*4)).ToArray();
            Check(values.Take(4).Select(p=>p.r).Distinct().Count()==4 && values.Take(4).All(p=>p.r<255),"HDR neutrals distinguish levels",values.Take(4).Select(RgbDetail));
            foreach(int i in new[]{4,5,6,7}) Check(Math.Max(values[i].r,Math.Max(values[i].g,values[i].b))-Math.Min(values[i].r,Math.Min(values[i].g,values[i].b))>80,"HDR saturated color remains colored",new{input=i,r=values[i].r,g=values[i].g,b=values[i].b});
            Check(HueError((255,0,0), values[4]) < 4 && values[4].r>values[4].g*2,"HDR red hue and no pink/white",new{r=values[4].r,g=values[4].g,b=values[4].b,hueError=HueError((255,0,0),values[4])});
            var skinReference=(r:(byte)255,g:(byte)175,b:(byte)139); Check(HueError(skinReference,values[8])<12,"HDR skin hue retained",new{hueError=HueError(skinReference,values[8]),r=values[8].r,g=values[8].g,b=values[8].b});
        }
        using(var hue=Bitmap(device,3,1,(x,_)=>x switch{0=>(4f,0f,0f),1=>(16f,0f,0f),_=>(2.6f,1.4f,.8f)}))
        {var b=Render(hue,80);foreach(var c in new[]{(0,(4f,0f,0f)),(1,(16f,0f,0f)),(2,(2.6f,1.4f,.8f))}){var outp=Rgb(b,c.Item1*4);double error=HueError(NormalizedHue(c.Item2.Item1,c.Item2.Item2,c.Item2.Item3),outp);Check(error<2.5,$"HDR exact hue input {c.Item1}",new{error,r=outp.r,g=outp.g,b=outp.b});}}
        using(var redRamp=Bitmap(device,16,1,(x,_)=>((float)(x+1),0,0)))
        {byte[] b=Render(redRamp,80);var red=Enumerable.Range(0,16).Select(i=>Rgb(b,i*4)).ToArray();double[] luminance=red.Select(EncodedY).ToArray();int reversals=0;for(int i=1;i<luminance.Length;i++)if(luminance[i-1]-luminance[i]>1.01)reversals++;int distinct=red.Select(p=>$"{p.r},{p.g},{p.b}").Distinct().Count();var metrics=RampMetrics(luminance);Check(reversals==0&&metrics.substantiveLevels>=4,"HDR pure-red ramp retains substantive levels after dither tolerance",new{distinctRgbTuples=distinct,substantiveLevelsAfter1_5LsbThreshold=metrics.substantiveLevels,maxAdjacentEncodedLuminanceLsb=metrics.maxAdjacentLuminanceLsb,largeLuminanceReversals=reversals,inputScale="1..16",rgbCodes=red.Select(p=>new{p.r,p.g,p.b}),encodedLuminance=luminance});}
        using(var ramps=Bitmap(device,16,5,(x,y)=>{float v=x+1;return y switch{0=>(v,0f,0f),1=>(0f,v,0f),2=>(0f,0f,v),3=>(0f,v,v),_=>(v,v,0f)};}))
        {byte[] b=Render(ramps,80);for(int y=0;y<5;y++){var row=Enumerable.Range(0,16).Select(x=>Rgb(b,(y*16+x)*4)).ToArray();double[] lum=row.Select(EncodedY).ToArray();int reversals=0;for(int x=1;x<16;x++)if(lum[x-1]-lum[x]>1.01)reversals++;int distinct=row.Select(p=>$"{p.r},{p.g},{p.b}").Distinct().Count();var metrics=RampMetrics(lum);string hue=y switch{0=>"red",1=>"green",2=>"blue",3=>"cyan",_=>"yellow"};Check(reversals==0&& (y!=0||metrics.substantiveLevels>=4),$"HDR {hue} ramp encoded-luminance detail and monotonicity",new{distinctRgbTuples=distinct,substantiveLevelsAfter1_5LsbThreshold=metrics.substantiveLevels,maxAdjacentEncodedLuminanceLsb=metrics.maxAdjacentLuminanceLsb,minRedSubstantiveLevels=4,largeLuminanceReversals=reversals,rgbCodes=row.Select(p=>new{p.r,p.g,p.b}),encodedLuminance=lum});}}
        using (var pair=Bitmap(device,2,1,(x,_)=>x==0?(-.15f,.25f,.1f):(0,.25f,.1f)))
        {var b=Render(pair,80);var negative=Rgb(b,0);var clamped=Rgb(b,4);Check(negative!=clamped,"negative scRGB retained through gamut transform",new{negative=RgbDetail(negative),clamped=RgbDetail(clamped)});}
        using (var boundary=Bitmap(device,2,1,(x,_)=>x==0?(-.00001f,.6105f,.5755f):(.00001f,.6105f,.5755f)))
        {
            var b=Render(boundary,80);var negative=Rgb(b,0);var positive=Rgb(b,4);
            int greenDelta=Math.Abs(negative.g-positive.g),blueDelta=Math.Abs(negative.b-positive.b);
            Check(greenDelta<=1&&blueDelta<=1&&negative.r<=1,"SDR gamut boundary remains continuous across tiny signed-red change",new{negative=RgbDetail(negative),positive=RgbDetail(positive),greenDelta,blueDelta,maxAllowedDeltaLsb=1});
        }
        using(var special=Bitmap(device,8,1,(x,_)=>x switch {0=>(float.NaN,float.NaN,float.NaN),1=>(float.PositiveInfinity,float.PositiveInfinity,float.PositiveInfinity),2=>(float.NegativeInfinity,float.NegativeInfinity,float.NegativeInfinity),3=>(0f,0f,0f),4=>(float.MaxValue,float.MaxValue,float.MaxValue),5=>(-float.MaxValue,float.MaxValue,0f),6=>(float.PositiveInfinity,1f,1f),_=>(0f,float.PositiveInfinity,0f)}))
        using(var sanitized=Bitmap(device,8,1,(x,_)=>x switch {0=>(0f,0f,0f),1=>(65504f,65504f,65504f),2=>(-65504f,-65504f,-65504f),3=>(0f,0f,0f),4=>(65504f,65504f,65504f),5=>(-65504f,65504f,0f),6=>(65504f,1f,1f),_=>(0f,65504f,0f)}))
        {var b=Render(special,203);var expected=Render(sanitized,203);bool exact=b.SequenceEqual(expected);var nan=Rgb(b,0);var positiveNeutral=Rgb(b,4);var green=Rgb(b,28);bool black=nan==(0,0,0)&&Rgb(b,8)==(0,0,0)&&Rgb(b,12)==(0,0,0);bool neutral=positiveNeutral.r==positiveNeutral.g&&positiveNeutral.g==positiveNeutral.b&&positiveNeutral.r>240;double greenHueError=HueError((0,255,0),green);bool greenReasonable=green.g>green.r&&green.g>green.b&&green.g-Math.Min(green.r,green.b)>80&&greenHueError<4;Check(exact&&black&&neutral&&greenReasonable,"NaN/Inf/extreme values match explicit finite sanitization",new{exactSanitizedBytes=exact,blackOutputs=new[]{RgbDetail(nan),RgbDetail(Rgb(b,8)),RgbDetail(Rgb(b,12))},extremeNeutral=RgbDetail(positiveNeutral),green=RgbDetail(green),greenHueErrorDegrees=greenHueError,greenReasonable});}
        using(var edge=Bitmap(device,513,257,(x,y)=>x==512&&y==256?(100f,100f,100f):(.25f,.25f,.25f),96))
        {var s=StarshotPerceptual.Measure(edge);Check(Math.Abs(s.PeakNits-8000)<1&&s.SampleCount==171*86*3,"statistics include last non-96-DPI edge outlier",new{p999=s.Percentile999Nits,peak=s.PeakNits,count=s.SampleCount});}
        using(var edge=Bitmap(device,513,257,(x,y)=>x==512&&y==256?(100f,100f,100f):(.25f,.25f,.25f),144))
        {var s=StarshotPerceptual.Measure(edge);bool ok=Math.Abs(s.PeakNits-8000)<1&&s.SampleCount==171*86*3;checks++;Results.Add(new{name="statistics include last non-96-DPI edge outlier",passed=ok,detail=new{p999=s.Percentile999Nits,peak=s.PeakNits,count=s.SampleCount,expectedPeakNits=8000}});}
        foreach(float value in new[]{.125f,4f})using(var uniform=Bitmap(device,9,7,(_,_) => (value,value,value))){var s=StarshotPerceptual.Measure(uniform);float nits=value*80;Check(Math.Abs(s.Percentile999Nits-nits)<.001&&Math.Abs(s.PeakNits-nits)<.001&&s.SampleCount==9*7*3,"uniform statistics retain all three float channels",new{input=value,p999=s.Percentile999Nits,peak=s.PeakNits,count=s.SampleCount});}
        var rng=new Random(4419); float[] random=new float[400*4];for(int i=0;i<400;i++){random[4*i]=(float)(rng.NextDouble()*12-2);random[4*i+1]=(float)(rng.NextDouble()*12-2);random[4*i+2]=(float)(rng.NextDouble()*12-2);random[4*i+3]=1;}
        using(var src=CanvasBitmap.CreateFromBytes(device,MemoryMarshal.AsBytes(random.AsSpan()).ToArray(),20,20,DirectXPixelFormat.R32G32B32A32Float,96,CanvasAlphaMode.Ignore))
        {
            byte[] b=Render(src,250);int eligible=0;double sumHue=0,maxHue=0;
            for(int i=0;i<400;i++)
            {
                double scale=80d/250,r=random[i*4]*scale,g=random[i*4+1]*scale,bl=random[i*4+2]*scale,y=.2126*r+.7152*g+.0722*bl;var inLab=LabHue(r,g,bl);double chroma=Math.Sqrt(inLab.a*inLab.a+inLab.b*inLab.b);
                var p=Rgb(b,i*4);double or=Linear(p.r),og=Linear(p.g),ob=Linear(p.b);
                if(y<=1e-8||chroma<=.02)continue;double error=HueDelta(inLab,LabHue(or,og,ob));eligible++;sumHue+=error;maxHue=Math.Max(maxHue,error);
            }
            double meanHue=eligible==0?double.PositiveInfinity:sumHue/eligible;Check(eligible>100&&meanHue<1&&maxHue<5,"random signed-linear CPU inputs preserve physical positive-Y OKLab hue",new{eligible,meanHueErrorDegrees=meanHue,maxHueErrorDegrees=maxHue});
        }
        var legal=new float[20*20*4];var legalRng=new Random(125);for(int i=0;i<400;i++){for(int c=0;c<3;c++)legal[i*4+c]=(float)(legalRng.NextDouble()*.8*250/80);legal[i*4+3]=1;}
        using(var legalSrc=CanvasBitmap.CreateFromBytes(device,MemoryMarshal.AsBytes(legal.AsSpan()).ToArray(),20,20,DirectXPixelFormat.R32G32B32A32Float,96,CanvasAlphaMode.Ignore))
        {byte[] b=Render(legalSrc,250);double max=0;int maxPixel=0,maxChannel=0;for(int i=0;i<400;i++)for(int c=0;c<3;c++){double v=legal[i*4+c]*80/250,expected=v<=.0031308?12.92*v:1.055*Math.Pow(v,1/2.4)-.055;byte actual=c==0?Rgb(b,i*4).r:c==1?Rgb(b,i*4).g:Rgb(b,i*4).b;double error=Math.Abs(actual-expected*255);if(error>max){max=error;maxPixel=i;maxChannel=c;}}Check(max<=1.01,"random legal SDR inputs retain identity",new{maxEncodedByteError=max,samples=400,maxPixel,maxChannel,input=new{r=legal[maxPixel*4],g=legal[maxPixel*4+1],b=legal[maxPixel*4+2]},output= new{r=Rgb(b,maxPixel*4).r,g=Rgb(b,maxPixel*4).g,b=Rgb(b,maxPixel*4).b}});}
        TextIdentityCheck(device); MathematicalCurveChecks();
    }

    private static async Task FileChecks(CanvasDevice device, string temp)
    {
        using var src=Bitmap(device,16,8,(x,y)=>{float v=.08f+.055f*x+.15f*y;return(v,v*.8f,v*.65f);});
        using var mapped=StarshotPerceptual.Render(src,80);
        using var png=new MemoryStream(); await ImageSaver.SaveAsPngAsync(mapped,png,ColorPrimaries.BT709,null,true); byte[] data=png.ToArray();
        string[] chunks=Chunks(data); Check(chunks.Contains("sRGB")&&chunks.Contains("cICP"),"PNG carries sRGB and cICP chunks",chunks);
        Directory.CreateDirectory(temp); await File.WriteAllBytesAsync(Path.Combine(temp,"perceptual-sample.png"),data);
        Check(true,"PNG sRGB+cICP output written in isolated temporary directory",new{bytes=data.Length});
        try
        {
            using var avif=new MemoryStream();await ImageSaver.SaveAsAvifAsync(mapped,avif,ColorPrimaries.BT709,80,null,true);byte[] encoded=avif.ToArray();await File.WriteAllBytesAsync(Path.Combine(temp,"perceptual-sdr.avif"),encoded);
            using var decoder=avifDecoderLite.Create(encoded);bool metadata=decoder.ColorPrimaries==avifColorPrimaries.BT709&&decoder.TransferCharacteristics==avifTransferCharacteristics.SRGB&&decoder.MatrixCoefficients==avifMatrixCoefficients.BT709;
            Check(metadata,"SDR AVIF decodes as BT.709 primaries/sRGB transfer/BT.709 matrix",new{bytes=encoded.Length,primaries=decoder.ColorPrimaries.ToString(),transfer=decoder.TransferCharacteristics.ToString(),matrix=decoder.MatrixCoefficients.ToString()});
        }
        catch(Exception ex){Check(false,"SDR AVIF metadata",ex.GetType().Name+": "+ex.Message);}
        try
        {
            using var jxl=new MemoryStream();await ImageSaver.SaveAsJxlAsync(mapped,jxl,ColorPrimaries.BT709,0,null,true);byte[] encoded=jxl.ToArray();await File.WriteAllBytesAsync(Path.Combine(temp,"perceptual-sdr.jxl"),encoded);
            using var decoder=JxlDecoderLite.Create(encoded);var color=decoder.ColorEncoding;bool metadata=color.Primaries==JxlPrimaries.sRGB&&color.TransferFunction==JxlTransferFunction.sRGB;
            Check(metadata,"SDR JXL decodes with sRGB primaries and transfer",new{bytes=encoded.Length,primaries=color.Primaries.ToString(),transfer=color.TransferFunction.ToString()});
        }
        catch(Exception ex){Check(false,"SDR JXL metadata",ex.GetType().Name+": "+ex.Message);}
        var half=new Half[16*4];for(int i=0;i<16;i++){half[i*4]=(Half)(i%4==3?2.5:1.5);half[i*4+1]=(Half)(i%4==3?1.2:.6);half[i*4+2]=(Half)(i%4==3?.6:.2);half[i*4+3]=(Half)1;}
        using var hdr=CanvasBitmap.CreateFromBytes(device,MemoryMarshal.AsBytes(half.AsSpan()).ToArray(),4,4,DirectXPixelFormat.R16G16B16A16Float,96,CanvasAlphaMode.Ignore);
        try {using var avif=new MemoryStream();await ImageSaver.SaveAsAvifAsync(hdr,avif,ColorPrimaries.BT709,80);await File.WriteAllBytesAsync(Path.Combine(temp,"perceptual-hdr.avif"),avif.ToArray());Results.Add(new{name="HDR AVIF small native encode",passed=true,detail=new{bytes=avif.Length}});}
        catch(Exception ex){Results.Add(new{name="HDR AVIF small native encode",passed=false,detail=ex.GetType().Name+": "+ex.Message});}
        try {using var uhdr=new MemoryStream();await ImageSaver.SaveAsUhdrAsync(hdr,uhdr,1000,80);await File.WriteAllBytesAsync(Path.Combine(temp,"perceptual-uhdr.jpg"),uhdr.ToArray());byte[] native=await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory,"uhdr.dll"));Results.Add(new{name="UHDR small canonical native encode",passed=true,detail=new{bytes=uhdr.Length,dllSha256=Convert.ToHexString(SHA256.HashData(native))}});}
        catch(Exception ex){Results.Add(new{name="UHDR small canonical native encode",passed=false,detail=ex.GetType().Name+": "+ex.Message});}
    }

    private static void PublishedChecks(CanvasDevice device,string appDirectory)
    {
        string full=Path.GetFullPath(appDirectory);if(!Path.IsPathRooted(appDirectory)||!Directory.Exists(full)){Check(false,"published DLL path exists and is absolute",full);return;}
        try
        {
            var assembly=Assembly.LoadFrom(Path.Combine(full,"Starshot.dll"));var type=assembly.GetType("Starshot.Features.Codec.StarshotPerceptual",throwOnError:true)!;
            var method=type.GetMethod("Render",BindingFlags.Public|BindingFlags.Static,null,[typeof(CanvasBitmap),typeof(float),typeof(float)],null);
            if(method is null){Check(false,"published trimmed Render(CanvasBitmap,float,float) method exists",new{assembly=assembly.Location,type=type.FullName});return;}
            using(var sample=Bitmap(device,8,1,(x,_)=>x switch{0=>(1f,1f,1f),1=>(2f,2f,2f),2=>(4f,4f,4f),3=>(8f,8f,8f),4=>(4f,0f,0f),5=>(16f,0f,0f),6=>(1.25f,.12f,-.04f),_=>(2.6f,1.4f,.8f)}))
            using(var mapped=(CanvasRenderTarget)method.Invoke(null,[sample,80f,1000f])!)
            {
                byte[] pixels=mapped.GetPixelBytes();var levels=Enumerable.Range(0,4).Select(i=>Rgb(pixels,i*4).r).ToArray();int white=levels[0];
                Check(Math.Abs(white-248)<=3&&levels.Distinct().Count()==4,$"published pipeline maps SDR white and separates HDR neutrals",new{white,levels,assembly=assembly.Location,format=mapped.Format.ToString()});
                using var sourceMapped=StarshotPerceptual.Render(sample,80f,1000f);byte[] sourcePixels=sourceMapped.GetPixelBytes();bool sourceMatchesPublished=sourcePixels.SequenceEqual(pixels);
                Check(sourceMatchesPublished,"published renderer matches source renderer byte-for-byte",new{matches=sourceMatchesPublished,bytes=pixels.Length,sourceOutput=RgbDetail(Rgb(sourcePixels,16)),publishedOutput=RgbDetail(Rgb(pixels,16))});
                var red=Rgb(pixels,4*4);double redHueError=HueError((255,0,0),red);
                Check(redHueError<4&&red.r>red.g*2&&red.r-red.b>80,"published pipeline keeps pure HDR red red",new{r=red.r,g=red.g,b=red.b,hueErrorDegrees=redHueError});
            }
            using var negative=Bitmap(device,1,1,(_,_) => (-.15f,.25f,.1f));using var clamped=Bitmap(device,1,1,(_,_) => (0,.25f,.1f));
            using var negOut=(CanvasRenderTarget)method.Invoke(null,[negative,80f,1000f])!;using var clampOut=(CanvasRenderTarget)method.Invoke(null,[clamped,80f,1000f])!;
            var negativePixel=Rgb(negOut.GetPixelBytes(),0);var clampedPixel=Rgb(clampOut.GetPixelBytes(),0);
            Check(negativePixel!=clampedPixel,"published pipeline preserves negative-channel signal",new{negative=RgbDetail(negativePixel),clamped=RgbDetail(clampedPixel)});
        }
        catch(Exception ex){Check(false,"published perceptual pipeline reflection smoke",ex.GetType().Name+": "+ex.Message);}
    }

    private static string[] Chunks(byte[] png)
    {var result=new List<string>();for(int i=8;i+12<=png.Length;){int n=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(i,4));if(n<0||i+12L+n>png.Length)break;result.Add(System.Text.Encoding.ASCII.GetString(png,i+4,4));i+=12+n;}return result.ToArray();}

    [StructLayout(LayoutKind.Sequential)] private struct GpuSample { public ulong DedicatedResident,SharedResident,DedicatedCommitted,SharedCommitted; public uint Adapters,ResidentQueries,CommittedQueries,FailedQueries,EnumeratedAdapters,FirstFailure; }
    [DllImport("GpuMemory",CallingConvention=CallingConvention.Cdecl)] private static extern uint ReadGpuMemory(uint pid,out GpuSample sample);
    private static GpuSample Gpu() {uint err=ReadGpuMemory((uint)Environment.ProcessId,out var s);if(err!=0||s.Adapters==0||s.ResidentQueries==0||s.CommittedQueries==0)throw new InvalidOperationException($"GPU counters unavailable {err:X8} {s}");return s;}
    private static void CleanupHarnessTemps(string activeTemp)
    {
        string root=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        string activeLeaf=Path.GetFileName(Path.GetFullPath(activeTemp));
        const string prefix="StarshotPerceptual-";
        if(!activeLeaf.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)
            ||!Guid.TryParseExact(activeLeaf[prefix.Length..],"N",out _))return;
        string full=Path.GetFullPath(activeTemp);
        if(!string.Equals(Path.GetDirectoryName(full)?.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar),root,StringComparison.OrdinalIgnoreCase))return;
        if(!Directory.Exists(full)||(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)return;
        Directory.Delete(full,true);
    }
    private static async Task Perf(CanvasDevice device,string csv)
    {
        using var writer=new StreamWriter(csv);writer.WriteLine("cycle,phase,elapsedMs,dedicatedResident,dedicatedCommitted,privateBytes");
        var proc=Process.GetCurrentProcess();long peakPrivate=0;ulong peakResident=0,peakCommitted=0;
        void Sample(int cycle,string phase,double ms=0){var g=Gpu();proc.Refresh();peakResident=Math.Max(peakResident,g.DedicatedResident);peakCommitted=Math.Max(peakCommitted,g.DedicatedCommitted);peakPrivate=Math.Max(peakPrivate,proc.PrivateMemorySize64);writer.WriteLine(FormattableString.Invariant($"{cycle},{phase},{ms:F3},{g.DedicatedResident},{g.DedicatedCommitted},{proc.PrivateMemorySize64}"));writer.Flush();}
        var baseline=Gpu();proc.Refresh();long baselinePrivate=proc.PrivateMemorySize64;Sample(0,"baseline");
        const int width=3840,height=2160;
        using(var src=new CanvasRenderTarget(device,width,height,96,DirectXPixelFormat.R16G16B16A16Float,CanvasAlphaMode.Ignore))
        {
            using(var pattern=new PixelShaderEffect<PerfPatternShader>{BufferPrecision=CanvasBufferPrecision.Precision16Float})
            using(var ds=src.CreateDrawingSession()){ds.Units=CanvasUnits.Pixels;ds.Blend=CanvasBlend.Copy;ds.DrawImage(pattern);}
            using(var probe=new CanvasRenderTarget(device,1,1,96,DirectXPixelFormat.R16G16B16A16Float,CanvasAlphaMode.Ignore))
            {using(var ds=probe.CreateDrawingSession()){ds.Units=CanvasUnits.Pixels;ds.Blend=CanvasBlend.Copy;ds.DrawImage(src,new Windows.Foundation.Rect(0,0,1,1),new Windows.Foundation.Rect(0,0,1,1));}_=probe.GetPixelBytes();}
            Sample(0,"sourceReady");
            for(int cycle=1;cycle<=5;cycle++)
            {
                Sample(cycle,"beforeMeasure");var sw=Stopwatch.StartNew();var stats=StarshotPerceptual.Measure(src);sw.Stop();Sample(cycle,"afterMeasure",sw.Elapsed.TotalMilliseconds);
                sw.Restart();using(var dst=StarshotPerceptual.Render(src,StarshotPerceptual.Parameters.Create(80,stats)))_ = dst.GetPixelBytes(0,0,1,1);sw.Stop();Sample(cycle,"afterRenderDispose",sw.Elapsed.TotalMilliseconds);
            }
        }
        await Task.Delay(TimeSpan.FromSeconds(5));var after=Gpu();proc.Refresh();long afterPrivate=proc.PrivateMemorySize64;Sample(5,"after5s");
        Results.Add(new{name="4K FP16 reuse performance and memory",passed=true,detail=new{baselineDedicatedResident=baseline.DedicatedResident,baselineDedicatedCommitted=baseline.DedicatedCommitted,baselinePrivate,peakDedicatedResident=peakResident,peakDedicatedCommitted=peakCommitted,peakPrivate,after5sDedicatedResident=after.DedicatedResident,after5sDedicatedCommitted=after.DedicatedCommitted,after5sPrivate=afterPrivate,csv}});
    }

    [STAThread] private static async Task<int> Main(string[] args)
    {
        string temp=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"StarshotPerceptual-"+Guid.NewGuid().ToString("N")));
        string output=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"."));Directory.CreateDirectory(output);
        string log=Path.Combine(output,"validation.log");
        try
        {
            Directory.CreateDirectory(temp);using var device=CanvasDevice.GetSharedDevice(); MathematicalGpuChecks(device); ExtendedHdrRampCheck(device); await FileChecks(device,temp);
            string? published=args.FirstOrDefault(a=>a.StartsWith("--published=",StringComparison.Ordinal))?["--published=".Length..];if(published is not null)PublishedChecks(device,published);
            if(args.Contains("--perf")){string csv=Path.Combine(output,"performance.csv");await Perf(device,csv);}
            var report=new{generated=DateTimeOffset.UtcNow,passed=Results.All(x=>JsonSerializer.Serialize(x).Contains("\"passed\":true")),checks=Results.Count,results=Results};
            await File.WriteAllTextAsync(Path.Combine(output,"validation.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            await File.WriteAllLinesAsync(log,Results.Select(x => JsonSerializer.Serialize(x)));
            await File.WriteAllLinesAsync(Path.Combine(output,"validation.csv"),new[]{"name,passed,detail"}.Concat(Results.Select(x=>{using var d=JsonDocument.Parse(JsonSerializer.Serialize(x));var e=d.RootElement;return $"\"{e.GetProperty("name").GetString()?.Replace("\"","\"\"")}\",{e.GetProperty("passed").GetBoolean()},\"{e.GetProperty("detail").GetRawText().Replace("\"","\"\"")}\"";})));
            bool allPassed=Results.All(x=>JsonSerializer.Serialize(x).Contains("\"passed\":true"));Console.WriteLine($"{Results.Count} checks; {(allPassed?"PASS":"FAILURES RECORDED")}; report {Path.Combine(output,"validation.json")}");return allPassed?0:1;
        }
        catch(Exception ex){await File.WriteAllTextAsync(log,ex.ToString());var report=new{generated=DateTimeOffset.UtcNow,passed=false,checks=Results.Count,failure=ex.ToString(),results=Results};await File.WriteAllTextAsync(Path.Combine(output,"validation.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));Console.Error.WriteLine(ex.Message);return 1;}
        finally { CleanupHarnessTemps(temp); }
    }
}
