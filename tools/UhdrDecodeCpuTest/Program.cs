using System.Buffers.Binary;
using Starshot.Features.Codec;
using Starward.Codec;
using Starward.Codec.UltraHdr;

int checks=0;void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
var rows=new List<object>();
byte[] baseBytes=Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQH/2wBDAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQH/wAARCAAQABADAREAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwAoAKACgAoA/9k=");
byte[] gainBytes=Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQH/2wBDAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQH/wAARCAAQABADAREAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD+/igAoAKACgD/2Q==");
Check(!UhdrLinearDecoder.IsUhdr(baseBytes),"Ordinary JPEG must not be detected as UHDR");
foreach(var gamut in new[]{UhdrColorGamut.BT709,UhdrColorGamut.DisplayP3})
foreach(float white in new[]{80f,203f,320f})
{
    byte[] taggedBase=UhdrReferenceWhite.AttachToBaseJpeg(baseBytes,white);
    using var encoder=new UhdrEncoder();
    unsafe
    {
        fixed(byte* b=taggedBase,g=gainBytes)
        {
            UhdrCompressedImage Image(byte* data,int length)=>new(){Data=(nint)data,DataSize=(uint)length,Capacity=(uint)length,ColorGamut=gamut,ColorTransfer=UhdrColorTransfer.SRGB,ColorRange=UhdrColorRange.FullRange};
            encoder.SetCompressedImage(Image(b,taggedBase.Length),UhdrImageLabel.Base);
            encoder.SetGainmapImage(Image(g,gainBytes.Length),new UhdrGainmapMetadata{MinContentBoost=new FixedArray3<float>(1),MaxContentBoost=new FixedArray3<float>(16),Gamma=new FixedArray3<float>(1),OffsetSdr=new FixedArray3<float>(0),OffsetHdr=new FixedArray3<float>(0),HdrCapacityMin=1,HdrCapacityMax=16,UseBaseColorSpace=1});
        }
    }
    encoder.Encode();byte[] bytes=encoder.GetEncodedBytes().ToArray();
    Check(UhdrLinearDecoder.IsUhdr(bytes),"Actual UHDR detected");
    var decoded=await UhdrLinearDecoder.DecodeAsync(bytes,CancellationToken.None);
    Check(decoded.Width==16&&decoded.Height==16&&decoded.Pixels.Length==16*16*8,"FP16 geometry and stride");
    Check(Math.Abs(decoded.White-white)<.01,"SDR white provenance");
    Check(decoded.Primaries.Id==(gamut==UhdrColorGamut.DisplayP3?Starward.Codec.ICC.ColorPrimaries.DisplayP3:Starward.Codec.ICC.ColorPrimaries.BT709).Id,"Decoded output gamut retained");
    float channel=(float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(decoded.Pixels));
    double expected=Math.Pow((128/255d+.055)/1.055,2.4)*16;
    Check(Math.Abs(channel-expected)<.02,"Full gain-map reconstruction");
    rows.Add(new{gamut=gamut.ToString(),white,linear=channel,expected,absoluteNits=channel*white});
}
using var cts=new CancellationTokenSource();cts.Cancel();try{await UhdrLinearDecoder.DecodeAsync(baseBytes,cts.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){checks++;}
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{ok=true,checks,cpuOnly=true,rows},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
