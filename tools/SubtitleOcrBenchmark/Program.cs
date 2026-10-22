using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Serilog;
using Starshot;
using Starshot.Features.Subtitles;
using Starshot.Helpers;
internal static class Program
{
    private sealed record Fixture(string Name,int Width,int Height,string File,string Expected);
    private static async Task Main(string[] args)
    {
        Console.OutputEncoding=System.Text.Encoding.UTF8;
        string mode=args.Length>3?args[3]:"cpu";bool gpu=mode.StartsWith("gpu");string suffix=mode=="cpu"?"":"-"+mode;
        string root=args[0],models=args[1],report=args[2];AppConfig.UserDataFolder=Path.Combine(root,"appdata");AppConfig.OcrGpuAcceleration=gpu;
        Log.Logger=new LoggerConfiguration().WriteTo.File(Path.Combine(report,"ocr"+suffix+".log")).CreateLogger();
        var fixtures=JsonSerializer.Deserialize<Fixture[]>(File.ReadAllText(Path.Combine(root,"fixtures.json")),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
        var frames=new Dictionary<string,SubtitlePixels>();foreach(var f in fixtures)frames[f.Name]=new(File.ReadAllBytes(Path.Combine(root,f.File)),f.Width,f.Height);
        var rows=new List<object>();
        try
        {
            string import=Path.Combine(root,"small-source");Directory.CreateDirectory(import);foreach(var asset in OcrModelStore.Assets)File.Copy(Path.Combine(models,asset.Source),Path.Combine(import,asset.File));
            OcrModelStore.Current.StartImport(import);await OcrModelStore.Current.WaitForDownloadAsync();var installed=await OcrModelStore.Current.GetStatusAsync();if(!installed.Installed||installed.Warning is not null)throw new Exception("Small resources not exact: "+installed.Error+installed.Warning);
            if(mode=="image")
            {
                foreach(string model in new[]{"tiny","small"})
                {
                    await OcrHelper.SelectModelAsync(model);var fixture=fixtures[0];var frame=frames[fixture.Name];var watch=Stopwatch.StartNew();
                    var lines=await OcrHelper.RecognizeAsync(frame.Pixels,frame.Width,frame.Height,1);
                    rows.Add(new{model,input=$"{frame.Width}x{frame.Height}",elapsedMs=watch.Elapsed.TotalMilliseconds,lines=lines?.Select((l,i)=>new{id=i,text=l.Text,score=l.RecognitionScore,empty=string.IsNullOrWhiteSpace(l.Text),x=l.Rect.X,y=l.Rect.Y,width=l.Rect.Width,height=l.Rect.Height})});
                }
                File.WriteAllText(Path.Combine(report,"attached-image-ocr.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("PASS attached image OCR recorded, CPU only, no translation request");return;
            }
            foreach(string model in mode.Contains("small-first")?new[]{"small"}:new[]{"tiny","small"})
            {
                await OcrHelper.SelectModelAsync(model);int beforeInit=OcrHelper.ModelInitializationCount;long memoryBefore=Memory();var gpuBefore=GpuCounter.Read();var warmTimes=new List<double>();
                foreach(var fixture in fixtures.Take(3))for(int repeat=0;repeat<2;repeat++)
                {
                    var frame=frames[fixture.Name];var watch=Stopwatch.StartNew();var lines=await OcrHelper.RecognizeAsync(frame.Pixels,frame.Width,frame.Height,1);double ms=watch.Elapsed.TotalMilliseconds;
                    string text=string.Join("\n",(lines??[]).Select(l=>l.Text));if(string.IsNullOrWhiteSpace(text))throw new Exception("Empty OCR: "+model+fixture.Name);
                    bool cold=OcrHelper.ModelInitializationCount>beforeInit&&warmTimes.Count==0&&repeat==0&&fixture==fixtures[0];
                    if(!cold)warmTimes.Add(ms);
                    rows.Add(new{kind="inference",model,gpuRequested=gpu,actual=Backend(),fixture=fixture.Name,input=$"{frame.Width}x{frame.Height}",cold,ms,initMs=OcrHelper.ModelInitializationMilliseconds,initCount=OcrHelper.ModelInitializationCount,privateBytes=Memory(),gpuMemory=GpuCounter.Read(),text});
                    var backgroundWatch=Stopwatch.StartNew();var backgroundLines=await OcrHelper.RecognizeBackgroundAsync(frame.Pixels,frame.Width,frame.Height,default);double backgroundMs=backgroundWatch.Elapsed.TotalMilliseconds;
                    if(backgroundLines is null||!backgroundLines.Any(l=>l.Text.Contains(fixture.Expected)))throw new Exception("Background OCR mismatch: "+model+fixture.Name);
                    rows.Add(new{kind="background-inference",model,gpuRequested=gpu,fixture=fixture.Name,input=$"{frame.Width}x{frame.Height}",ms=backgroundMs,text=string.Join("\n",backgroundLines.Select(l=>l.Text))});
                }
                if(OcrHelper.ModelInitializationCount!=beforeInit+1)throw new Exception("Model reloaded during warm OCR");
                // Production gate: foreground ownership must make a background sample return immediately.
                var gate=(SemaphoreSlim)typeof(OcrHelper).GetField("EngineGate",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;await gate.WaitAsync();
                var busyWatch=Stopwatch.StartNew();try{var value=await OcrHelper.RecognizeBackgroundAsync(frames[fixtures[0].Name].Pixels,fixtures[0].Width,fixtures[0].Height,default);if(value is not null||busyWatch.ElapsedMilliseconds>100)throw new Exception("Background request queued behind foreground OCR");}finally{busyWatch.Stop();gate.Release();}
                var scenes=fixtures.Skip(3).ToArray();var elapsed=Stopwatch.StartNew();SubtitleSession? session=null;var latencies=new List<double>();
                try
                {
                    session=new(Path.Combine(root,"timeline-"+model+".txt"),(_,_)=>
                    {
                        long ms=elapsed.ElapsedMilliseconds;string name=ms<900?scenes[0].Name:ms<1300?scenes[1].Name:ms<2200?scenes[2].Name:ms<2600?"blank":scenes[0].Name;
                        if(name=="blank")return Task.FromResult(new SubtitlePixels(new byte[scenes[0].Width*scenes[0].Height*4],scenes[0].Width,scenes[0].Height));return Task.FromResult(frames[name]);
                    },async(p,ct)=>{var timer=Stopwatch.StartNew();var lines=await OcrHelper.RecognizeBackgroundAsync(p.Pixels,p.Width,p.Height,ct);latencies.Add(timer.Elapsed.TotalMilliseconds);return lines is null?null:new(lines.Select(l=>l.Text).ToArray(),model+(gpu?" / GPU requested":" / CPU"));});
                    session.SetRegion(new(0,0,scenes[0].Width,scenes[0].Height));session.SetInterval(200);session.Start();await Task.Delay(3700);await session.StopAsync();var status=session.Snapshot();string text=session.Text();
                    rows.Add(new{kind="timeline",model,elapsedMs=elapsed.ElapsedMilliseconds,status.Captures,status.Recognitions,status.Skipped,status.Count,text,expected=scenes.Select(s=>s.Expected),found=scenes.Select(s=>text.Contains(s.Expected)).ToArray(),shortCaptionMs=400,observedOcrMs=latencies});
                }
                finally{if(session is not null)await session.StopAsync();}
                rows.Add(new{kind="summary",model,gpuRequested=gpu,actual=Backend(),memoryBefore,gpuBefore,privateAfter=Memory(),gpuAfter=GpuCounter.Read(),warmMedianMs=warmTimes.Order().ElementAt(warmTimes.Count/2),warmMaxMs=warmTimes.Max(),initializations=OcrHelper.ModelInitializationCount-beforeInit,foregroundBusyReturnMs=busyWatch.Elapsed.TotalMilliseconds,fallbacks=OcrHelper.FallbackCount});
            }
            if(OcrHelper.FallbackCount!=0)throw new Exception("Unexpected legacy OCR fallback");
            await OcrHelper.SelectAccelerationAsync(!gpu);await OcrHelper.SelectAccelerationAsync(false);await Task.Delay(250);
            rows.Add(new{kind="engines-retired",privateBytes=Memory(),gpuMemory=GpuCounter.Read(),loadedModel=OcrHelper.LoadedModel});
            File.WriteAllText(Path.Combine(report,"speed"+suffix+".json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("PASS real Tiny/Small OCR benchmark and production subtitle timeline; "+(gpu?"GPU requested (actual session status recorded)":"CPU")+", no desktop capture");
        }
        finally{OcrHelper.Dispose();Log.CloseAndFlush();}
    }
    private static long Memory(){using var process=Process.GetCurrentProcess();return process.PrivateMemorySize64;}
    private static object Backend()
    {
        const BindingFlags instance=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
        var engine=typeof(OcrHelper).GetField("_engine",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
        var stages=new List<object>();
        foreach(string name in new[]{"_detector","_classifier","_recognizer"})
        {
            var stage=engine.GetType().GetField(name,instance)!.GetValue(engine);if(stage is null)continue;
            var sessions=(System.Collections.IEnumerable)stage.GetType().GetField("_sessions",instance)!.GetValue(stage)!;
            stages.Add(new{stage=name,sessions=sessions.Cast<object>().Select(s=>new{type=s.GetType().FullName,gpuAlive=s.GetType().GetProperty("GpuAlive",instance)?.GetValue(s)}).ToArray()});
        }
        var device=engine.GetType().Assembly.GetType("Sdcb.SimdPaddleOCR.Backends.Vulkan.GpuBackend")!.GetField("s_device",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null);
        return new{requested=OcrHelper.LoadedBackend.ToString(),device=device?.GetType().GetField("DeviceName",instance)?.GetValue(device),stages};
    }
}

internal static class GpuCounter
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Sample{public ulong DedicatedResident,SharedResident,DedicatedCommitted,SharedCommitted;public uint Adapters,ResidentQueries,CommittedQueries,FailedQueries,Enumerated,FirstFailure;}
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]private delegate uint Query(uint pid,out Sample value);
    internal static object? Read()
    {
        string? path=Environment.GetEnvironmentVariable("STARSHOT_GPU_MEMORY_DLL");if(path is null)return null;
        nint library=System.Runtime.InteropServices.NativeLibrary.Load(path);
        try{var read=System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<Query>(System.Runtime.InteropServices.NativeLibrary.GetExport(library,"ReadGpuMemory"));uint status=read((uint)Environment.ProcessId,out var value);return new{status,dedicatedResidentMiB=value.DedicatedResident/1048576d,sharedResidentMiB=value.SharedResident/1048576d,dedicatedCommittedMiB=value.DedicatedCommitted/1048576d,adapters=value.Adapters,residentQueries=value.ResidentQueries,committedQueries=value.CommittedQueries,failedQueries=value.FailedQueries};}
        finally{System.Runtime.InteropServices.NativeLibrary.Free(library);}
    }
}
