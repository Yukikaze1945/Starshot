using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Starshot.Features.Codec;

string temp = Path.Combine(Path.GetTempPath(), "StarshotHeic-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
var rows = new List<object>();
void Check(bool condition, string text) { if (!condition) throw new Exception(text); }
byte[] Pixels(int width, int height, bool hdr)
{
    byte[] result = new byte[checked(width * height * (hdr ? 8 : 4))];
    for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
    {
        int strip = Math.Min(5, x * 6 / width), i = (y * width + x) * (hdr ? 8 : 4);
        if (hdr)
        {
            float nits = new float[] { 0, 80, 203, 1000, 4000, 200 }[strip];
            double p = Math.Pow(nits / 10000d, 2610d / 16384);
            ushort pq = (ushort)Math.Round(65535 * Math.Pow((3424d / 4096 + 2413d / 128 * p) / (1 + 2392d / 128 * p), 2523d / 32));
            for (int c = 0; c < 3; c++) BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(i + 2 * c), pq);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(i + 6), 65535);
        }
        else
        {
            result[i] = strip is 0 or 3 ? (byte)230 : (byte)30;
            result[i + 1] = strip is 1 or 3 ? (byte)200 : (byte)20;
            result[i + 2] = strip is 2 or 3 ? (byte)210 : (byte)10;
            result[i + 3] = 255;
        }
    }
    return result;
}
double Nits(ushort pq)
{
    double p = Math.Pow(pq / 65535d, 32d / 2523);
    return 10000 * Math.Pow(Math.Max(p - 3424d / 4096, 0) / (2413d / 128 - 2392d / 128 * p), 16384d / 2610);
}
try
{
    foreach (var test in new[] { ("sdr",137,91,false,90), ("hdr",128,80,true,90), ("maximum",65,63,false,100), ("tiny",1,1,false,90), ("4k",3840,2160,true,80), ("hdr-maximum",128,80,true,100) })
    {
        var (name,w,h,hdr,quality) = test;
        var pixels = Pixels(w,h,hdr);
        byte[] xmp = Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><rdf:Description xmlns:s='urn:starshot:test' s:marker='HEIC-test'/></rdf:RDF></x:xmpmeta>");
        var timer = Stopwatch.StartNew();
        byte[] encoded = await HeicCodec.EncodeAsync(pixels,w,h,hdr ? "rgba16" : "bgra8",quality,hdr,xmp,4000,350);
        long encodeMs = timer.ElapsedMilliseconds;
        string file = Path.Combine(temp, "中文 " + name + ".heic"); await File.WriteAllBytesAsync(file,encoded);
        var size = await HeicCodec.SizeAsync(file);
        Check(size == ((uint)w,(uint)h), name + " exact dimensions");
        var decoded = await HeicCodec.DecodeAsync(file);
        Check(decoded.Width == w && decoded.Height == h && decoded.Depth == 10, name + " Main10 pixels");
        Check(decoded.Transfer == (hdr ? 16 : 13) && decoded.Primaries == (hdr ? 9 : 1), name + " CICP metadata");
        Check(Encoding.UTF8.GetString(encoded).Contains("HEIC-test"), "XMP must survive");
        if (hdr)
        {
            Check(decoded.MaxCll == 4000 && decoded.MaxFall == 350, "HDR content light metadata");
            for (int strip = 1; strip <= 4; strip++)
            {
                int i = ((h/2)*w + (2*strip+1)*w/12)*8;
                double actual = Nits(BinaryPrimitives.ReadUInt16LittleEndian(decoded.Pixels.AsSpan(i)));
                double expected = new double[] { 0,80,203,1000,4000,200 }[strip];
                Check(Math.Abs(actual / expected - 1) < .07, $"{name} brightness {expected}: {actual}");
            }
        }
        else
        {
            int x = Math.Min(w-1,w/12), i = ((h/2)*w+x)*8;
            Check(BinaryPrimitives.ReadUInt16LittleEndian(decoded.Pixels.AsSpan(i+4)) > 54000, "BGRA blue channel must not become red");
        }
        var thumbnail = await HeicCodec.DecodeAsync(file,128);
        Check(Math.Max(thumbnail.Width,thumbnail.Height) <= 128, "Bounded CPU thumbnail");
        Check(HeicCodec.PreviewBgra(thumbnail).Length == thumbnail.Width*thumbnail.Height*4, "CPU sRGB preview");
        rows.Add(new {name,w,h,hdr,quality,bytes=encoded.Length,encodeMs,depth=decoded.Depth,decoded.Primaries,decoded.Transfer,decoded.MaxCll,decoded.MaxFall});
    }
    string p3File=Path.Combine(temp,"p3.heic");
    await File.WriteAllBytesAsync(p3File,await HeicCodec.EncodeAsync(Pixels(128,80,false),128,80,"bgra8",90,false,primaries:12));
    var p3=await HeicCodec.DecodeAsync(p3File);
    Check(p3.Primaries==12 && p3.Transfer==13 && !p3.Hdr,"Display-P3 SDR metadata");
    string corrupt=Path.Combine(temp,"corrupt.heic"); await File.WriteAllTextAsync(corrupt,"not a HEIC file");
    bool failed=false; try { await HeicCodec.DecodeAsync(corrupt); } catch (Exception) { failed=true; }
    Check(failed,"Corrupt HEIC must fail");
    using(var cancel=new CancellationTokenSource())
    {
        var pixels=Pixels(3840,2160,true); cancel.CancelAfter(10);
        bool cancelled=false; try { await HeicCodec.EncodeAsync(pixels,3840,2160,"rgba16",90,true,ct:cancel.Token); } catch(OperationCanceledException) {cancelled=true;}
        Check(cancelled,"Encoding cancellation");
    }
    await Task.Delay(200);
    Check(Process.GetProcessesByName("StarshotHeic").Length==0,"No lingering HEIC process");
    string originalDirectory=HeicCodec.ComponentDirectory;
    string altered=Path.Combine(temp,"component"); Directory.CreateDirectory(altered);
    File.Copy(Path.Combine(originalDirectory,"StarshotHeic.exe"),Path.Combine(altered,"StarshotHeic.exe"));
    await File.WriteAllTextAsync(Path.Combine(altered,"manifest.json"),"{\"sha256\":\"BAD\"}");
    try
    {
        HeicCodec.ComponentDirectory=altered;
        bool rejected=false; try { await HeicCodec.SizeAsync(p3File); } catch(InvalidDataException) {rejected=true;}
        Check(rejected,"Component hash must be validated before execution");
    }
    finally {HeicCodec.ComponentDirectory=originalDirectory;}
    Console.WriteLine(JsonSerializer.Serialize(new {ok=true,rows,corruptRejected=true,cancellation=true},new JsonSerializerOptions{WriteIndented=true}));
}
finally { Directory.Delete(temp,true); }
