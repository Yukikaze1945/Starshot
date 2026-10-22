using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Windowing;
using Starshot.Features.ViewHost;

namespace Starshot.ToolbarValidation;

public sealed partial class ToolbarTestApp
{
    private async Task ValidateOcrStartupAsync(string report)
    {
        var type=typeof(MainWindow);
        string temporary=Path.Combine(Path.GetTempPath(),"starshot-ocr-startup-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        foreach(string property in new[]{"CacheFolder","UserDataFolder"})
            typeof(Starshot.AppConfig).GetProperty(property,BindingFlags.Static|BindingFlags.Public)!.SetValue(null,temporary);
        var rows=new List<object>();
        var browserIds=new HashSet<uint>();
        try
        {
            for(int cycle=0;cycle<3;cycle++)
            {
                var timer=Stopwatch.StartNew();
                using var popup=new MainWindow(ocrPopup:true);
                type.GetField("_pendingOcr",Flags)!.SetValue(popup,new{text="冷启动文字 / First OCR result",raw="冷启动文字 / First OCR result",lineCount=1,translate=false,autoCopied=false,id="synthetic-"+cycle});
                type.GetField("_pendingOcrId",Flags)?.SetValue(popup,"synthetic-"+cycle);
                type.GetField("_loadingOcrText",Flags)!.SetValue(popup,"冷启动文字 / First OCR result");
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(25));
                while(!(bool)type.GetField("_ready",Flags)!.GetValue(popup)!)await Task.Delay(10,deadline.Token);
                double readyMs=timer.Elapsed.TotalMilliseconds;
                var core=(CoreWebView2)type.GetField("_core",Flags)!.GetValue(popup)!;
                browserIds.Add(core.BrowserProcessId);
                string result;
                do
                {
                    result=await core.ExecuteScriptAsync("document.querySelector('[aria-label=\"可编辑的原文\"]')?.textContent ?? ''");
                    if(!result.Contains("First OCR result",StringComparison.Ordinal))await Task.Delay(10,deadline.Token);
                }while(!result.Contains("First OCR result",StringComparison.Ordinal));
                double textMs=timer.Elapsed.TotalMilliseconds;
                Check((bool)type.GetField("_ocrContentReady",Flags)!.GetValue(popup)!,"First native readiness includes the committed OCR document");
                Check(type.GetField("_pendingOcr",Flags)!.GetValue(popup) is null,"Acknowledged OCR payload is released");
                Check(!popup.AppWindow.IsVisible,"Startup benchmark never shows a native window");
                string layout=await core.ExecuteScriptAsync("JSON.stringify({text:document.querySelector('[aria-label=\"可编辑的原文\"]')?.textContent,fonts:document.fonts.status,animations:document.querySelector('.compact-ocr')?.getAnimations().length})");
                rows.Add(new{cycle,readyMs,textMs,layout,privateBytes=Process.GetCurrentProcess().PrivateMemorySize64});
            }
            using(var prepared=new MainWindow(ocrPopup:true))
            {
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(25));
                while(!(bool)type.GetField("_ready",Flags)!.GetValue(prepared)!)await Task.Delay(10,deadline.Token);
                Check(!(bool)type.GetField("_ocrContentReady",Flags)!.GetValue(prepared)!,"Prepared empty editor cannot be revealed as OCR content");
                var timer=Stopwatch.StartNew();
                var data=new{text="Prepared OCR / 已预备",raw="Prepared OCR / 已预备",lineCount=1,translate=false,autoCopied=false,id="prepared"};
                type.GetField("_pendingOcr",Flags)!.SetValue(prepared,data);type.GetField("_pendingOcrId",Flags)!.SetValue(prepared,"prepared");
                type.GetMethod("Emit",Flags)!.Invoke(prepared,["ocr.result",data]);
                while(!(bool)type.GetField("_ocrContentReady",Flags)!.GetValue(prepared)!)await Task.Delay(5,deadline.Token);
                rows.Add(new{phase="prepared-result",postOcrMs=timer.Elapsed.TotalMilliseconds});
                // A second real controller borrows this live environment, not a
                // strong static environment cache or an idle hidden browser.
                using var sibling=new MainWindow(ocrPopup:true);
                while(!(bool)type.GetField("_ready",Flags)!.GetValue(sibling)!)await Task.Delay(10,deadline.Token);
                var a=(CoreWebView2)type.GetField("_core",Flags)!.GetValue(prepared)!;
                var b=(CoreWebView2)type.GetField("_core",Flags)!.GetValue(sibling)!;
                browserIds.Add(a.BrowserProcessId);browserIds.Add(b.BrowserProcessId);
                Check(a.BrowserProcessId==b.BrowserProcessId,"Concurrent UI controllers reuse the active browser process");
                await a.ExecuteScriptAsync("(()=>{const e=document.querySelector('[aria-label=\"可编辑的原文\"]');e.focus();const r=document.createRange();r.selectNodeContents(e);const s=getSelection();s.removeAllRanges();s.addRange(r);document.execCommand('insertText',false,'First edited draft');})()");
                var next=new{text="Second independent OCR",raw="Second independent OCR",lineCount=1,translate=false,autoCopied=false,id="second"};
                type.GetField("_pendingOcr",Flags)!.SetValue(sibling,next);type.GetField("_pendingOcrId",Flags)!.SetValue(sibling,"second");
                type.GetMethod("Emit",Flags)!.Invoke(sibling,["ocr.result",next]);
                while(!(bool)type.GetField("_ocrContentReady",Flags)!.GetValue(sibling)!)await Task.Delay(5,deadline.Token);
                string firstText=await a.ExecuteScriptAsync("document.querySelector('[aria-label=\"可编辑的原文\"]')?.textContent");
                string secondText=await b.ExecuteScriptAsync("document.querySelector('[aria-label=\"可编辑的原文\"]')?.textContent");
                Check(firstText.Contains("First edited draft")&&secondText.Contains("Second independent OCR"),"New OCR editor preserves the first editor's edited document");

            }
            await ValidateOcrPresentationAsync();
            File.WriteAllText(Path.Combine(report,"native-startup-"+(Environment.GetEnvironmentVariable("STARSHOT_OCR_BENCH_LABEL")??"current")+".json"),JsonSerializer.Serialize(new{checks=_checks,rows},new JsonSerializerOptions{WriteIndented=true}));
        }
        finally
        {
            // Wait for this isolated profile's browser to finish its last write;
            // deleting while it is exiting can recreate an otherwise empty profile.
            foreach(uint pid in browserIds)
            {
                try
                {
                    using var process=Process.GetProcessById((int)pid);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch(ArgumentException) { }
                catch(TimeoutException) { }
            }
            for(int attempt=0;attempt<20;attempt++)
            {
                try{Directory.Delete(temporary,true);break;}
                catch(IOException)when(attempt<19){await Task.Delay(150);}
                catch(UnauthorizedAccessException)when(attempt<19){await Task.Delay(150);}
            }
        }
    }

    private async Task ValidateOcrPresentationAsync()
    {
        var type=typeof(MainWindow);
        using var window=new MainWindow(ocrPopup:true);window.ForceExit=true;
        window.AppWindow.MoveAndResize(new(-32000,-32000,1,1));
        type.GetMethod("EnsureOcrBounds",Flags)!.Invoke(window,["Short text"]);
        Check(window.AppWindow.Size.Width>=360*window.UIScale&&window.AppWindow.Size.Height>=230*window.UIScale,"Corrupt tiny bounds are replaced by readable dimensions");
        var area=DisplayArea.GetFromWindowId(window.AppWindow.Id,DisplayAreaFallback.Primary).WorkArea;
        Check(window.AppWindow.Position.X>=area.X&&window.AppWindow.Position.Y>=area.Y,"Offscreen OCR window is brought back inside a current work area");
        Check(!window.AppWindow.IsVisible,"Bounds repair does not show or activate a hidden host");
        type.GetField("_pendingOcr",Flags)!.SetValue(window,new{id="new"});
        type.GetField("_pendingOcrId",Flags)!.SetValue(window,"new");
        type.GetMethod("FrontendReady",Flags)!.Invoke(window,["old"]);
        Check(!(bool)type.GetField("_ocrContentReady",Flags)!.GetValue(window)!,"Stale ready notification cannot reveal a newer pending result");
        type.GetMethod("FrontendReady",Flags)!.Invoke(window,["new"]);
        Check((bool)type.GetField("_ocrContentReady",Flags)!.GetValue(window)!&&type.GetField("_pendingOcr",Flags)!.GetValue(window) is null,"Matching ready notification releases pending result");
        var ocr=type.Assembly.GetType("Starshot.Features.Screenshot.OcrResultWindow")!;
        var prepare=ocr.GetMethod("Prepare",BindingFlags.Static|BindingFlags.NonPublic)!;
        var first=(IDisposable)prepare.Invoke(null,null)!;var second=(IDisposable)prepare.Invoke(null,null)!;
        var windowField=first.GetType().GetField("_window",Flags)!;
        var firstHost=(MainWindow)windowField.GetValue(first)!;var secondHost=(MainWindow)windowField.GetValue(second)!;
        firstHost.ForceExit=secondHost.ForceExit=true;
        Check(firstHost!=secondHost&&firstHost.WindowHandle!=secondHost.WindowHandle,"Each OCR request owns an independent native editor");
        first.Dispose();Check(firstHost.WindowHandle==0&&secondHost.WindowHandle!=0,"Failed first OCR closes only its own prepared editor");
        second.Dispose();Check(secondHost.WindowHandle==0,"No-text OCR closes its independent editor");
        second.Dispose();
        var delivered=(IDisposable)prepare.Invoke(null,null)!;
        var deliveredHost=(MainWindow)windowField.GetValue(delivered)!;deliveredHost.ForceExit=true;
        delivered.GetType().GetField("_delivered",Flags)!.SetValue(delivered,true);
        delivered.Dispose();Check(deliveredHost.WindowHandle!=0,"Delivered editor survives its preparation lease until explicitly closed");
        deliveredHost.Dispose();
        var delayed=new MainWindow(ocrPopup:true);
        type.GetField("_ready",Flags)!.SetValue(delayed,true); // Simulate a prepared controller; no browser/input needed.
        var lines=new Starshot.Helpers.OcrLine[]{new("Delayed content",new Windows.Foundation.Rect(0,0,120,20),[])};
        type.GetMethod("ShowOcr",Flags)!.Invoke(delayed,[lines,false,false]);
        Check(!delayed.AppWindow.IsVisible&&(bool)type.GetField("_ocrShowWhenReady",Flags)!.GetValue(delayed)!,"Prepared result waits for a committed document without flashing native UI");
        delayed.Dispose();await Task.Delay(160);
        Check(delayed.WindowHandle==0&&type.GetField("_pendingOcr",Flags)!.GetValue(delayed) is null,"Closed editor cannot be resurrected by the 120ms preview fallback");
    }
}
