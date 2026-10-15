using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Graphics.DirectX;

internal static class Program
{
    [STAThread] static void Main()
    {
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string path = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(args =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new HeicTestApp();
        });
    }
}

// Real Win2D effects and app codec integration; no HWND, WGC or desktop capture.
internal sealed class HeicTestApp : Starshot.App
{
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        string report = Environment.GetEnvironmentVariable("STARSHOT_HEIC_TEST_REPORT")!;
        string temp = Path.Combine(Path.GetTempPath(), "StarshotHeicNative-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            ((System.Timers.Timer)typeof(Starshot.App).GetField("_gcTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!).Stop();
            var app = typeof(Starshot.App).Assembly;
            var save = app.GetType("Starshot.Features.Codec.ImageSaver")!.GetMethods().Single(m => m.Name == "SaveAsHeicAsync" && m.GetParameters()[0].ParameterType == typeof(CanvasBitmap));
            var load = app.GetType("Starshot.Features.Codec.ImageLoader")!.GetMethod("LoadImageAsync")!;
            var primaries = save.GetParameters()[2].ParameterType.GetProperty("BT709")!.GetValue(null);
            var rows = new List<object>();
            using var device = CanvasDevice.GetSharedDevice();
            if(Environment.GetEnvironmentVariable("STARSHOT_WORKSPACE_NATIVE_TEST")=="1")
            {
                await ValidateUhdrAsync(app,device,load,temp,report);
                return;
            }
            foreach (bool hdr in new[] { false, true })
            {
                const int w = 256, h = 96;
                byte[] pixels = new byte[w * h * (hdr ? 8 : 4)];
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * (hdr ? 8 : 4);
                    if (hdr)
                    {
                        float value = new float[] { 1, 2.5f, 10, 50 }[x / 64];
                        for (int c = 0; c < 4; c++) BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(i + c * 2), BitConverter.HalfToUInt16Bits((Half)(c == 3 ? 1 : value)));
                    }
                    else { pixels[i] = 220; pixels[i + 1] = 30; pixels[i + 2] = 10; pixels[i + 3] = 255; }
                }
                using var bitmap = CanvasBitmap.CreateFromBytes(device, pixels, w, h, hdr ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized);
                using var stream = new MemoryStream();
                var timer = Stopwatch.StartNew();
                await (Task)save.Invoke(null, [bitmap, stream, primaries, 90, null, true, hdr ? 4000f : (float?)null, hdr ? 350f : (float?)null])!;
                long saveMs = timer.ElapsedMilliseconds;
                string path = Path.Combine(temp, hdr ? "hdr.heic" : "sdr.heic");
                await File.WriteAllBytesAsync(path, stream.ToArray());
                var task = (Task)load.Invoke(null, [path, CancellationToken.None])!;
                await task;
                using var info = (IDisposable)task.GetType().GetProperty("Result")!.GetValue(task)!;
                bool loadedHdr = (bool)info.GetType().GetProperty("HDR")!.GetValue(info)!;
                var decoded = (CanvasBitmap)info.GetType().GetProperty("CanvasBitmap")!.GetValue(info)!;
                if (loadedHdr != hdr || decoded.SizeInPixels.Width != w || decoded.SizeInPixels.Height != h) throw new Exception("HEIC app dimensions / HDR classification");
                byte[] output = decoded.GetPixelBytes();
                var actual = new List<double>();
                if (hdr)
                {
                    if (decoded.Format != DirectXPixelFormat.R16G16B16A16Float) throw new Exception("HDR must load as FP16 scRGB");
                    for (int strip = 0; strip < 4; strip++)
                    {
                        int i = ((h / 2) * w + strip * 64 + 32) * 8;
                        double nits = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(i))) * 80;
                        double expected = new double[] { 80, 200, 800, 4000 }[strip];
                        if (Math.Abs(nits / expected - 1) > .08) throw new Exception($"HDR highlight roundtrip {expected} -> {nits}");
                        actual.Add(nits);
                    }
                }
                else if (BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(((h / 2) * w + w / 2) * 8 + 4)) < 50000) throw new Exception("SDR B/R channel preservation");
                rows.Add(new { hdr, saveMs, roundtripMs = timer.ElapsedMilliseconds, bytes = stream.Length, format = decoded.Format.ToString(), nits = actual });
            }
            File.WriteAllText(Path.Combine(report, "heic-native.json"), JsonSerializer.Serialize(new { ok = true, rows }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(report, "heic-native-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
        finally { Directory.Delete(temp, true); Exit(); }
    }

    private static async Task ValidateUhdrAsync(Assembly app,CanvasDevice device,MethodInfo load,string temp,string report)
    {
        var saver=app.GetType("Starshot.Features.Codec.ImageSaver")!;
        var encode=saver.GetMethod("SaveAsUhdrAsync")!;
        var rows=new List<object>();const int width=256,height=96;
        var sourceBytes=new byte[width*height*8];
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)for(int c=0;c<4;c++)
        {
            float v=c==3?1:new float[]{1,2.5f,10,30}[x/64];
            BinaryPrimitives.WriteUInt16LittleEndian(sourceBytes.AsSpan((y*width+x)*8+c*2),BitConverter.HalfToUInt16Bits((Half)v));
        }
        using var bitmap=CanvasBitmap.CreateFromBytes(device,sourceBytes,width,height,DirectXPixelFormat.R16G16B16A16Float);
        foreach(float white in new[]{80f,203f,320f})
        {
            using var encoded=new MemoryStream();var watch=Stopwatch.StartNew();
            await (Task)encode.Invoke(null,[bitmap,encoded,2400f,white,0f])!;
            string file=Path.Combine(temp,$"uhdr-{white}.jpeg");await File.WriteAllBytesAsync(file,encoded.ToArray());
            var task=(Task)load.Invoke(null,[file,CancellationToken.None])!;await task;
            using var info=(IDisposable)task.GetType().GetProperty("Result")!.GetValue(task)!;
            var type=info.GetType();if(!(bool)type.GetProperty("HDR")!.GetValue(info)!)throw new Exception("UHDR must load as HDR");
            float decodedWhite=(float)type.GetProperty("SdrReferenceWhiteNits")!.GetValue(info)!;
            if(decodedWhite!=white)throw new Exception("UHDR EXIF white reference");
            var loaded=(CanvasBitmap)type.GetProperty("CanvasBitmap")!.GetValue(info)!;
            if(loaded.Format!=DirectXPixelFormat.R16G16B16A16Float)throw new Exception("UHDR must retain FP16");
            byte[] pixels=loaded.GetPixelBytes();var nits=new List<double>();
            for(int strip=0;strip<4;strip++)
            {
                int i=((height/2)*width+strip*64+32)*8;
                double actual=(float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(pixels.AsSpan(i)))*80;
                double expected=new double[]{80,200,800,2400}[strip];
                if(Math.Abs(actual/expected-1)>.12)throw new Exception($"UHDR absolute roundtrip white={white}: {expected} -> {actual}");nits.Add(actual);
            }
            var documentType=app.GetType("Starshot.Features.Workspace.WorkspaceDocument")!;
            var create=documentType.GetMethod("FromCanvasAsync")!;
            var primaries=type.GetProperty("ColorPrimaries")!.GetValue(info);
            var documentTask=(Task)create.Invoke(null,[loaded,"test.jpeg","synthetic",white,primaries,"Ultra HDR JPEG",CancellationToken.None,true])!;await documentTask;
            using var document=(IDisposable)documentTask.GetType().GetProperty("Result")!.GetValue(documentTask)!;
            byte[] raw=(byte[])documentType.GetProperty("HdrPixels")!.GetValue(document)!;
            if(raw.Length!=sourceBytes.Length)throw new Exception("Workspace must retain full-resolution raw HDR");
            rows.Add(new{white,nits,elapsedMs=watch.ElapsedMilliseconds,bytes=encoded.Length});
        }
        // Ordinary JPEG must remain SDR, including .jpeg extension.
        using var sdr=CanvasBitmap.CreateFromBytes(device,Enumerable.Repeat((byte)255,8*8*4).ToArray(),8,8,DirectXPixelFormat.B8G8R8A8UIntNormalized);
        using var plain=new MemoryStream();await (Task)saver.GetMethod("SaveAsJpegAsync")!.Invoke(null,[sdr,plain,90,(byte)3])!;
        string plainPath=Path.Combine(temp,"plain.jpeg");await File.WriteAllBytesAsync(plainPath,plain.ToArray());
        var plainTask=(Task)load.Invoke(null,[plainPath,CancellationToken.None])!;await plainTask;
        using var plainInfo=(IDisposable)plainTask.GetType().GetProperty("Result")!.GetValue(plainTask)!;
        if((bool)plainInfo.GetType().GetProperty("HDR")!.GetValue(plainInfo)!)throw new Exception("Ordinary JPEG falsely tagged HDR");
        File.WriteAllText(Path.Combine(report,"uhdr-workspace-native.json"),JsonSerializer.Serialize(new{ok=true,rows,ordinaryJpegSdr=true},new JsonSerializerOptions{WriteIndented=true}));
    }
}
