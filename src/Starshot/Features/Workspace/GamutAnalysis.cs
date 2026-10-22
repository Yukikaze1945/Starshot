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

    /// <summary>Consumes bounded readback stripes without retaining a full FP16 frame.</summary>
    public sealed class Accumulator
    {
        private readonly int _width, _height, _step;
        private readonly int[] _density = new int[128 * 128];
        private readonly int[] _counts = new int[4];
        private readonly RegionMap _map;
        private int _nextRow, _mapRow, _valid, _invalid, _black;
        public Accumulator(int width, int height)
        {
            ImageMask.Validate(width, height); _width = width; _height = height;
            _step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((long)width * height / 1_000_000d)));
            double scale = Math.Min(1, 1024d / Math.Max(width, height));
            int w = Math.Max(1, (int)(width * scale)), h = Math.Max(1, (int)(height * scale));
            _map = new(w, h, new byte[checked(w * h * 4)]);
        }
        public void AppendRows(ReadOnlySpan<byte> bytes, int first, int rows, int stride, CancellationToken ct)
        {
            if (first != _nextRow || rows <= 0 || first + rows > _height || stride < checked(_width * 8) || bytes.Length < checked(rows * stride))
                throw new ArgumentException("色域条带尺寸或顺序无效");
            for (int y = first; y < first + rows; y++)
            {
                ct.ThrowIfCancellationRequested();
                if (y % _step == 0)
                    for (int x = 0; x < _width; x += _step)
                    {
                        int i = (y - first) * stride + x * 8;
                        float r = Half(bytes, i), g = Half(bytes, i + 2), b = Half(bytes, i + 4);
                        if (!float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b)) { _invalid++; continue; }
                        double X = .4123908 * r + .3575843 * g + .1804808 * b,
                            Y = .212639 * r + .7151687 * g + .0721923 * b,
                            Z = .0193308 * r + .1191948 * g + .9505322 * b, sum = X + Y + Z;
                        if (Y <= 1e-7 || sum <= 1e-7) { _black++; continue; }
                        _valid++; _counts[Classify(r, g, b)]++;
                        int xx = (int)Math.Clamp(X / sum / .8 * 127, 0, 127), yy = (int)Math.Clamp((1 - Y / sum / .9) * 127, 0, 127);
                        _density[yy * 128 + xx]++;
                    }
                // Match the existing spatial map's nearest-center sampling, independently
                // of statistics sampling. Never allocate a source-sized region map.
                while (_mapRow < _map.Height && Math.Min(_height - 1, (int)((_mapRow + .5) * _height / _map.Height)) == y)
                {
                    for (int x = 0; x < _map.Width; x++)
                    {
                        int sx = Math.Min(_width - 1, (int)((x + .5) * _width / _map.Width)), i = (y - first) * stride + sx * 8;
                        float r = Half(bytes, i), g = Half(bytes, i + 2), b = Half(bytes, i + 4);
                        int region = !float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b) ? 5 : .2126 * r + .7152 * g + .0722 * b <= 1e-7 ? 4 : Classify(r, g, b);
                        WriteColor(_map.Pixels, (_mapRow * _map.Width + x) * 4, region);
                    }
                    _mapRow++;
                }
            }
            _nextRow += rows;
        }
        public (Summary Stats, RegionMap Map) Finish()
        {
            if (_nextRow != _height) throw new InvalidOperationException("色域数据尚未读取完毕");
            double d = Math.Max(1, _valid);
            return (new(_valid, _invalid, _black, 100 * _counts[0] / d, 100 * _counts[1] / d, 100 * _counts[2] / d, 100 * _counts[3] / d, _density, 128, _step > 1), _map);
        }
        private static float Half(ReadOnlySpan<byte> p, int i) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(p[i..]));
    }
    private static readonly byte[,] MapPalette = { {90,160,95}, {235,135,80}, {45,170,245}, {80,70,240}, {24,29,25}, {200,60,220} };
    private static void WriteColor(byte[] pixels, int i, int category)
    { pixels[i] = MapPalette[category, 0]; pixels[i + 1] = MapPalette[category, 1]; pixels[i + 2] = MapPalette[category, 2]; pixels[i + 3] = 255; }

    public static RegionMap CreateDiagram(Summary stats)
    {
        const int size = 256; byte[] p = new byte[size * size * 4]; int max = 1;
        foreach (int n in stats.Density) max = Math.Max(max, n);
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            int i = (y * size + x) * 4, count = stats.Density[Math.Min(stats.Grid - 1, y * stats.Grid / size) * stats.Grid + Math.Min(stats.Grid - 1, x * stats.Grid / size)];
            double a = Math.Log(1d + count) / Math.Log(1d + max);
            p[i] = (byte)(25 + a * 80); p[i + 1] = (byte)(29 + a * 214); p[i + 2] = (byte)(24 + a * 197); p[i + 3] = 255;
        }
        void Line(double x1, double y1, double x2, double y2, byte b, byte g, byte r)
        {
            int ax = (int)(x1 / .8 * 255), ay = (int)((1 - y1 / .9) * 255), bx = (int)(x2 / .8 * 255), by = (int)((1 - y2 / .9) * 255);
            int n = Math.Max(Math.Abs(bx - ax), Math.Abs(by - ay));
            for (int j = 0; j <= n; j++) { int x = Math.Clamp(ax + (bx - ax) * j / Math.Max(1, n), 0, 255), y = Math.Clamp(ay + (by - ay) * j / Math.Max(1, n), 0, 255), i = (y * size + x) * 4; p[i] = b; p[i + 1] = g; p[i + 2] = r; }
        }
        void Triangle(double rx, double ry, double gx, double gy, double bx, double by, byte b, byte g, byte r)
        { Line(rx, ry, gx, gy, b, g, r); Line(gx, gy, bx, by, b, g, r); Line(bx, by, rx, ry, b, g, r); }
        Triangle(.708,.292,.170,.797,.131,.046,45,170,245);
        Triangle(.680,.320,.265,.690,.150,.060,235,135,80);
        Triangle(.640,.330,.300,.600,.150,.060,90,160,95);
        return new(size, size, p);
    }
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
