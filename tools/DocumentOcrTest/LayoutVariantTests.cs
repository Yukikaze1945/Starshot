using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Starshot;
using Starshot.Features.DocumentOcr;
using Starshot.Helpers;

internal static class LayoutVariantTests
{
    internal static async Task RunAsync(string[] args)
    {
        string root=args[0],models=args[1],report=args[2];AppConfig.UserDataFolder=Path.Combine(root,"appdata");
        int checks=0;void Check(bool ok,string name){if(!ok)throw new Exception(name);checks++;}
        var rows=new List<object>();
        async Task<DlcModelStore.Status> Wait(DocumentModelStore store,string id)
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));
            while(true){await Task.Delay(20,timeout.Token);var s=await store.StatusAsync(id,false,timeout.Token);if(s.State is "error" or "corrupt")throw new Exception(s.Error);if(s.Installed)return s;}
        }
        var packages=JsonSerializer.Deserialize<DocumentModelStore.Package[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"ThirdParty/DocumentOcr/models.json")))!;
        byte[] pixels=await File.ReadAllBytesAsync(Path.Combine(root,"page.bgra"));var image=new DocumentImage(pixels,972,1380);
        try
        {
            Check(DocumentModelStore.SelectedLayout=="layout-doclayout-s","S remains default");
            Check(DocumentModelStore.Required(false,"layout-doclayout-m").SequenceEqual(new[]{"formula-rapidlatex","layout-doclayout-m","table-slanet-plus"}),"Only selected layout required");
            try{await DocumentModelStore.SelectLayoutAsync("layout-doclayout-l",default);throw new Exception("Missing selection accepted");}catch(InvalidOperationException){checks++;}
            foreach(string v in new[]{"m","l"})
            {
                string id="layout-doclayout-"+v;DocumentModelStore.Current.Start(id,"auto",Path.Combine(models,v),default);
                var status=await Wait(DocumentModelStore.Current,id);Check(status.Warning is null&&status.Task=="layout","Exact variant package + classification "+v);
                await DocumentModelStore.SelectLayoutAsync(id,default);Check(DocumentModelStore.SelectedLayout==id,"Selection "+v);
                Check(typeof(DocumentInference).GetField("_family",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null) is null,"Select stays lazy "+v);
                var tensor=DocumentInference.LayoutTensor(image,id,default);byte[] bytes=new byte[tensor.Length*4];Buffer.BlockCopy(tensor.ToArray(),0,bytes,0,bytes.Length);await File.WriteAllBytesAsync(Path.Combine(root,v+"-input.bin"),bytes);
                long before=Memory();var timer=Stopwatch.StartNew();var regions=await DocumentInference.LayoutAsync(image,default);long cold=timer.ElapsedMilliseconds;
                Check(regions.Count>0&&regions.All(r=>r.Box.X>=0&&r.Box.Y>=0&&r.Box.Right<=image.Width&&r.Box.Bottom<=image.Height),"Actual original-coordinate regions "+v);
                timer.Restart();var warm=await DocumentInference.LayoutAsync(image,default);long hot=timer.ElapsedMilliseconds;
                Check(regions.Count==warm.Count&&regions.Zip(warm).All(p=>p.First.Box==p.Second.Box),"Deterministic warm run "+v);
                var family=typeof(DocumentInference).GetField("_family",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null);
                Check((string?)family==id,"Actual selected inference "+v);
                Check(((System.Collections.IDictionary)typeof(DocumentInference).GetField("Sessions",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!).Count==1,"Only one layout session resident "+v);
                rows.Add(new{variant=v,input=$"{image.Width}x{image.Height}",coldMs=cold,warmMs=hot,privateBefore=before,privateAfter=Memory(),regions});
                var big=new DocumentImage(await File.ReadAllBytesAsync(Path.Combine(root,"page4k.bgra")),3840,2160);timer.Restart();var bigRegions=await DocumentInference.LayoutAsync(big,default);Check(bigRegions.Count>0,"4K inference "+v);
                rows.Add(new{variant=v,input="3840x2160",warmMs=timer.ElapsedMilliseconds,regions=bigRegions.Count,privateAfter=Memory()});
                using var cancel=new CancellationTokenSource();var pending=DocumentInference.LayoutAsync(image,cancel.Token);cancel.CancelAfter(10);
                try{await pending;throw new Exception("Not canceled");}catch(OperationCanceledException){checks++;}
                Check(typeof(DocumentInference).GetField("_family",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null) is null,"Canceled inference releases family "+v);
            }
            var scan=await LocalModelFiles.ScanAsync(new[]{models,Path.Combine(root,"appdata")},default);
            Check(scan.Items.Any(i=>i.Id=="layout-doclayout-m")&&scan.Items.Any(i=>i.Id=="layout-doclayout-l")&&!scan.Items.Any(i=>i.Id=="layout-doclayout-s"),"Variant filenames and installed canonical files scan without S misclassification");
            // Real stream download through the production install path, with deterministic HTTP transport.
            using var http=new HttpClient(new ModelFiles(models));var downloaded=new DocumentModelStore(()=>Path.Combine(root,"download"),()=>packages,http);
            downloaded.Start("layout-doclayout-m","huggingface",null,default);Check((await Wait(downloaded,"layout-doclayout-m")).Warning is null,"M download/atomic install/hash");
            var reopened=new DocumentModelStore(()=>Path.Combine(root,"download"),()=>packages,http);Check((await reopened.StatusAsync("layout-doclayout-m",true,default)).Installed,"Package survives store restart");
            await reopened.DeleteAsync("layout-doclayout-m",default);Check(!(await reopened.StatusAsync("layout-doclayout-m",false,default)).Installed,"Delete removes active revision");
            string transport=Path.Combine(root,"transport-fixture");Directory.CreateDirectory(transport);File.Copy(Path.Combine(models,"m","PP-DocLayout-M_infer.onnx"),Path.Combine(transport,"layout.onnx"));
            checks+=await PackageTests.RunAsync(root,packages.Single(p=>p.Id=="layout-doclayout-m"),transport);
            await DocumentModelStore.SelectLayoutAsync("layout-doclayout-m",default);
            await DocumentModelStore.Current.DeleteAsync("layout-doclayout-m",default);
            try{await DocumentInference.LayoutAsync(image,default);throw new Exception("Silently fell back to S");}catch(InvalidOperationException){checks++;}
            DocumentModelStore.Current.Start("layout-doclayout-m","auto",Path.Combine(models,"m"),default);await Wait(DocumentModelStore.Current,"layout-doclayout-m");
            string corrupt=Path.Combine(root,"wrong");Directory.CreateDirectory(corrupt);File.Copy(Path.Combine(models,"..","document-ocr","models","layout.onnx"),Path.Combine(corrupt,"layout.onnx"));
            DocumentModelStore.Current.Start("layout-doclayout-m","auto",corrupt,default);Check((await Wait(DocumentModelStore.Current,"layout-doclayout-m")).Warning is not null,"Hash mismatch advisory remains");
            try{await DocumentInference.LayoutAsync(image,default);throw new Exception("S accepted as M");}catch(InvalidDataException){checks++;}
            DocumentModelStore.Current.Start("layout-doclayout-s","auto",Path.Combine(models,"..","document-ocr","models"),default);await Wait(DocumentModelStore.Current,"layout-doclayout-s");
            await DocumentModelStore.SelectLayoutAsync("layout-doclayout-s",default);Check((await DocumentInference.LayoutAsync(image,default)).Count>0,"Existing S inference still works");
            rows.Add(new{checks,status="passed"});Directory.CreateDirectory(Path.GetDirectoryName(report)!);await File.WriteAllTextAsync(report,JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"Layout variants: {checks} checks passed");
        }
        finally{await DocumentInference.UnloadAsync(default);}
    }
    private static long Memory(){using var p=Process.GetCurrentProcess();return p.PrivateMemorySize64;}
    private sealed class ModelFiles(string root):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            string v=request.RequestUri!.AbsolutePath.Contains("/pp_doclayout_m/")?"m":"l";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(File.OpenRead(Path.Combine(root,v,$"PP-DocLayout-{v.ToUpperInvariant()}_infer.onnx")))});
        }
    }
}
