using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Serilog;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Starshot;
using Starshot.Features.DocumentOcr;
using Starshot.Helpers;

internal static class Program
{
    private static readonly List<object> Rows=[];
    private static int _checks;
    private static void Check(bool ok,string label){if(!ok)throw new InvalidOperationException(label);_checks++;}
    private sealed record Fixture(string Name,int Width,int Height,string File,string Kind,string? Expected);
    private static async Task Main(string[] args)
    {
        if(args[0]=="--layout-variants"){await LayoutVariantTests.RunAsync(args[1..]);return;}
        string root=Path.GetFullPath(args[0]);AppConfig.UserDataFolder=Path.Combine(root,"appdata");
        var options=new JsonSerializerOptions{PropertyNameCaseInsensitive=true,WriteIndented=true};
        var fixtures=JsonSerializer.Deserialize<Fixture[]>(await File.ReadAllTextAsync(Path.Combine(root,"fixtures.json")),options)!;
        string report=args[1];File.Delete(Path.ChangeExtension(report,"log"));Serilog.Log.Logger=new Serilog.LoggerConfiguration().MinimumLevel.Information().WriteTo.File(Path.ChangeExtension(report,"log")).CreateLogger();var baseline=Process.GetCurrentProcess().PrivateMemorySize64;
        try
        {
            var descriptors=JsonSerializer.Deserialize<DocumentModelStore.Package[]>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"ThirdParty","DocumentOcr","models.json")))!;
            foreach(var p in descriptors.Where(p=>p.Id is not("layout-doclayout-m" or "layout-doclayout-l")))
            {DocumentModelStore.Current.Start(p.Id,"auto",args[2],default);var status=await WaitInstalled(p.Id);Check(status.Warning is null,"Official model hashes "+p.Id);}
            var gray=new GrayImage([255,255,255,255],2,2);Check(gray.TightPad() is null,"Blank input is not formula");
            var stride=new DocumentImage([0,0,0,255,1,2,3,255,9,9,9,9,255,255,255,255,20,30,40,255,9,9,9,9],2,2,12);
            Check(stride.Crop(new(1,1,1,1)).Pixels.SequenceEqual(new byte[]{20,30,40,255}),"Padded stride crop");
            Check(new DocumentImage([0,0,255,255,0,255,0,255,255,0,0,255,0,0,0,0],4,1).Gray(default).SequenceEqual(new byte[]{76,150,29,255}),"BT.601 channels + transparent white compositing");
            Check(stride.Pixels[8]==9,"No caller-buffer mutation");
            var ordered=DocumentReadingOrder.Sort([new("right",2,"text",1,new(600,90,200,90)),new("title",11,"doc_title",1,new(0,0,900,40)),new("left-bottom",2,"text",1,new(0,210,200,90)),new("left-top",2,"text",1,new(0,90,200,90))]);
            Check(string.Join(',',ordered.Select(r=>r.Id))=="title,left-top,left-bottom,right","Heading + multicolumn reading order");
            var table=TableStructure.Parse("<table><tr><td colspan=\"2\"></td></tr><tr><td></td><td></td></tr></table>",[new(0,0,100,30),new(0,30,50,30),new(50,30,50,30)],1);
            table=table with{Cells=table.Cells.Select((c,i)=>c with{Text=i==0?"=SUM(A1) <&>":i.ToString()}).ToList()};
            Check(table.Rows==2&&table.Columns==2&&table.Cells[0].ColSpan==2,"Table spans");
            Check(table.Html().Contains("&lt;&amp;&gt;")&&table.Tsv().Split('\n')[0].EndsWith('\t'),"HTML escaping + TSV merged-cell semantics");
            string xlsx=Path.Combine(root,"table-test.xlsx");DocumentExport.Excel(xlsx,table);
            using(var doc=SpreadsheetDocument.Open(xlsx,false)){Check(!new OpenXmlValidator().Validate(doc).Any(),"XLSX schema");var xml=doc.WorkbookPart!.WorksheetParts.Single().Worksheet.OuterXml;Check(xml.Contains("A1:B1")&&!xml.Contains("<x:f>"),"XLSX merges + no formula injection");}
            File.Delete(xlsx);
            Check(FormulaClipboard.Normalize("<math xmlns=\"http://www.w3.org/1998/Math/MathML\"><mfrac><mi>x</mi><mn>2</mn></mfrac></math>").Contains("mfrac"),"Valid MathML");
            try{FormulaClipboard.Normalize("<!DOCTYPE math [<!ENTITY a SYSTEM 'file:///C:/Windows/win.ini'>]><math>&a;</math>");throw new Exception("XXE accepted");}catch(System.Xml.XmlException){_checks++;}
            foreach(var fixture in fixtures)
            {
                byte[] bgra=await File.ReadAllBytesAsync(Path.Combine(root,fixture.File));Check(bgra.Length==fixture.Width*fixture.Height*4,"Fixture actual pixel dimensions "+fixture.Name);var image=new DocumentImage(bgra,fixture.Width,fixture.Height);var timer=Stopwatch.StartNew();
                if(fixture.Kind=="formula")
                {
                    var first=await DocumentInference.FormulaAsync(image,default);var initial=new GrayImage(image.Gray(default),image.Width,image.Height).TightPad()!;await File.WriteAllBytesAsync(Path.Combine(root,fixture.Name+".initial.bin"),initial.Pixels);await File.WriteAllTextAsync(Path.Combine(root,fixture.Name+".initial.json"),JsonSerializer.Serialize(new[]{initial.Height,initial.Width}));var trace=new List<object>();var prepared=DocumentInference.ResizeFormula(initial,default,(step,gray,predicted)=>{File.WriteAllBytes(Path.Combine(root,fixture.Name+".trace-"+step+".bin"),gray.Pixels);trace.Add(new{height=gray.Height,width=gray.Width,predicted});});await File.WriteAllTextAsync(Path.Combine(root,fixture.Name+".trace.json"),JsonSerializer.Serialize(trace));byte[] tensorBytes=new byte[prepared.Length*4];Buffer.BlockCopy(prepared.ToArray(),0,tensorBytes,0,tensorBytes.Length);await File.WriteAllBytesAsync(Path.Combine(root,fixture.Name+".formula.bin"),tensorBytes);await File.WriteAllTextAsync(Path.Combine(root,fixture.Name+".shape.json"),JsonSerializer.Serialize(prepared.Dimensions.ToArray()));Rows.Add(new{fixture.Name,formula=first,privateBytes=Memory()});Check(first.Complete,"Formula terminates: "+fixture.Name);Check(!string.IsNullOrWhiteSpace(first.Latex),"Formula nonempty: "+fixture.Name);
                }
                else if(fixture.Kind=="table")
                {
                    var color=typeof(DocumentInference).GetMethod("ColorTensor",BindingFlags.NonPublic|BindingFlags.Static)!;var tensor=(Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>)color.Invoke(null,[image,488,488,true,CancellationToken.None])!;byte[] binary=new byte[tensor.Length*4];Buffer.BlockCopy(tensor.ToArray(),0,binary,0,binary.Length);await File.WriteAllBytesAsync(Path.Combine(root,fixture.Name+".input.bin"),binary);
                    var structure=await DocumentInference.TableAsync(image,default);var ocr=await OcrHelper.RecognizeAsync(bgra,image.Width,image.Height,1);
                    var populated=TableStructure.Associate(structure,(ocr??[]).Select(l=>(l.Text,new PixelBox((float)l.Rect.X,(float)l.Rect.Y,(float)l.Rect.Width,(float)l.Rect.Height))));
                    Rows.Add(new{fixture.Name,table=populated,elapsedMs=timer.ElapsedMilliseconds});Check(populated.Cells.Any(c=>!string.IsNullOrWhiteSpace(c.Text)),"Table OCR association");
                }
                else
                {
                    var regions=await DocumentInference.LayoutAsync(image,default);Rows.Add(new{fixture.Name,regions,elapsedMs=timer.ElapsedMilliseconds});Check(regions.Count>0,"Layout regions");
                    var color=typeof(DocumentInference).GetMethod("ColorTensor",BindingFlags.NonPublic|BindingFlags.Static)!;
                    var tensor=(Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>)color.Invoke(null,[image,480,480,false,CancellationToken.None])!;
                    byte[] binary=new byte[tensor.Length*4];Buffer.BlockCopy(tensor.ToArray(),0,binary,0,binary.Length);await File.WriteAllBytesAsync(Path.Combine(root,"layout-input.bin"),binary);
                }
            }
            var pageFixture=fixtures.First(f=>f.Kind=="layout");var pagePixels=await File.ReadAllBytesAsync(Path.Combine(root,pageFixture.File));
            using(var pageJob=new PageRecognitionJob(new(pagePixels,pageFixture.Width,pageFixture.Height),false,default)){await pageJob.Completion;var page=pageJob.Snapshot();Rows.Add(new{fullPage=page});Check((page.State is "done" or "partial")&&page.Regions.Any(r=>r.State=="done"),"Full page automatic pipeline");}
            var large=await File.ReadAllBytesAsync(Path.Combine(root,"page4k.bgra"));Rows.Add(await FourK(large));large=null!;
            var simple=fixtures.First(f=>f.Kind=="formula");var original=await File.ReadAllBytesAsync(Path.Combine(root,simple.File));
            var weak=new List<WeakReference>();
            for(int i=0;i<5;i++)
            {
                var row=await Lifecycle(original,simple.Width,simple.Height,i);weak.Add(row.Weak);Rows.Add(row.Measurement);
            }
            typeof(DocumentInference).GetField("_lastUse",BindingFlags.NonPublic|BindingFlags.Static)!.SetValue(null,Stopwatch.GetTimestamp()-Stopwatch.Frequency*61);
            typeof(DocumentInference).GetMethod("RetireIdle",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,null);
            Check(typeof(DocumentInference).GetField("_family",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null) is null,"Idle timeout releases the active model family");
            await DocumentInference.UnloadAsync(default);OcrHelper.Dispose();GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
            Rows.Add(new{weakBuffersAlive=weak.Count(w=>w.IsAlive)});
            Check(weak.All(w=>!w.IsAlive),"Closed jobs do not retain source buffers after diagnostic GC");
            using(var job=new PageRecognitionJob(new((byte[])original.Clone(),simple.Width,simple.Height),true,default)){job.Cancel();await job.Completion;Check(job.Snapshot().State=="canceled","Cancellation completes");}
            for(int cycle=0;cycle<2;cycle++){using var canceled=new PageRecognitionJob(new((byte[])original.Clone(),simple.Width,simple.Height),true,default);await Task.Delay(5);var cancelWatch=Stopwatch.StartNew();canceled.Cancel();await canceled.Completion;Rows.Add(new{cancelCycle=cycle,cancelMs=cancelWatch.ElapsedMilliseconds,state=canceled.Snapshot().State});Check(canceled.Snapshot().State=="canceled","In-flight cancellation does not publish success");}
            var custom=new DocumentModelStore(()=>Path.Combine(root,"package-tests"),()=>[descriptors[1]]);
            custom.Start(descriptors[1].Id,"auto",args[2],default);await WaitCustom(custom,descriptors[1].Id);Check((await custom.StatusAsync(descriptors[1].Id,true,default)).Installed,"Atomic directory publication");
            var missingDir=Path.Combine(root,"missing");Directory.CreateDirectory(missingDir);custom.Start(descriptors[1].Id,"auto",missingDir,default);await WaitCustom(custom,descriptors[1].Id);Check((await custom.StatusAsync(descriptors[1].Id,true,default)).Installed,"Failed replacement preserves installed revision");
            await custom.DeleteAsync(descriptors[1].Id,default);Check(!(await custom.StatusAsync(descriptors[1].Id,true,default)).Installed,"Delete removes active revision");
            Check(OcrHelper.FallbackCount==0,"Tiny OCR pipeline has no legacy fallback");
            _checks+=await PackageTests.RunAsync(root,descriptors[1],args[2]);
            Rows.Add(new{word=await WordClipboardTest.RunAsync().WaitAsync(TimeSpan.FromSeconds(45))});_checks++;
            string dataFolder=AppConfig.UserDataFolder;AppConfig.UserDataFolder=Path.Combine(root,"no-models");
            using(var missingJob=new PageRecognitionJob(new(original,simple.Width,simple.Height),true,default)){await missingJob.Completion;Check(missingJob.Snapshot().State=="error"&&missingJob.Snapshot().Error!.Contains("RapidLaTeX"),"Missing DLC produces an explicit error without model fallback");}
            AppConfig.UserDataFolder=dataFolder;
            var summary=new{ok=true,checks=_checks,baselinePrivateBytes=baseline,finalPrivateBytes=Memory(),weakBuffersAlive=weak.Count(w=>w.IsAlive),rows=Rows};await File.WriteAllTextAsync(report,JsonSerializer.Serialize(summary,options));Console.WriteLine(JsonSerializer.Serialize(new{ok=true,checks=_checks,report}));
        }
        catch(Exception ex){await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new{ok=false,checks=_checks,error=ex.ToString(),rows=Rows},options));throw;}
        finally{await DocumentInference.UnloadAsync(default);OcrHelper.Dispose();Serilog.Log.CloseAndFlush();}
    }
    private static long Memory(){using var p=Process.GetCurrentProcess();return p.PrivateMemorySize64;}
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<object> FourK(byte[] pixels)
    {
        long before=Memory(),peakPrivate=before,peakWorkingSet=0;
        using var sampling=new CancellationTokenSource();
        var monitor=Task.Run(async()=>
        {
            try
            {
                while(true)
                {
                    using(var process=Process.GetCurrentProcess())
                    {
                        peakPrivate=Math.Max(peakPrivate,process.PrivateMemorySize64);
                        peakWorkingSet=Math.Max(peakWorkingSet,process.WorkingSet64);
                    }
                    await Task.Delay(10,sampling.Token);
                }
            }
            catch(OperationCanceledException)when(sampling.IsCancellationRequested){}
        });
        PageRecognitionResult result;
        try
        {
            using var job=new PageRecognitionJob(new(pixels,3840,2160),false,default);
            await job.Completion;result=job.Snapshot();
            Check((result.State is "done" or "partial")&&result.Regions.Count>0,"4K full-page pipeline");
        }
        finally{sampling.Cancel();await monitor;}
        return new{input="3840x2160",regions=result.Regions.Count,state=result.State,elapsedMs=result.ElapsedMs,timingsMs=result.TimingsMs,privateBefore=before,privateAfter=Memory(),sampledPeakPrivateBytes=peakPrivate,sampledPeakWorkingSetBytes=peakWorkingSet,samplingIntervalMs=10};
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference Weak,object Measurement)> Lifecycle(byte[] original,int w,int h,int cycle)
    {
        byte[] pixels=(byte[])original.Clone();var weak=new WeakReference(pixels);DocumentImage? image=new(pixels,w,h);PageRecognitionJob? job=new(image,true,default);
        await job.Completion;Check(job.Snapshot().State=="done","Warm recognition cycle "+cycle);var memory=GC.GetGCMemoryInfo();
        object measurement=new{cycle,elapsedMs=job.Snapshot().ElapsedMs,privateBytes=Memory(),managedBytes=GC.GetTotalMemory(false),heapSize=memory.HeapSizeBytes,committed=memory.TotalCommittedBytes,gen2=GC.CollectionCount(2)};
        job.Dispose();job=null;image=null;pixels=null!;
        return(weak,measurement);
    }
    private static async Task<DlcModelStore.Status> WaitInstalled(string id)
    {while(true){await Task.Delay(50);var s=await DocumentModelStore.Current.StatusAsync(id,false,default);if(s.State is "error" or "corrupt")throw new Exception(s.Error);if(s.Installed)return s;}}
    private static async Task WaitCustom(DocumentModelStore store,string id)
    {while(true){await Task.Delay(50);var s=await store.StatusAsync(id,false,default);if(s.State is not("importing" or "downloading" or "validating"))return;}}
}
