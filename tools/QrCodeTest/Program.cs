using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Starshot.Features.Screenshot;
using Starshot.Helpers;
using ZXing;

internal static class Program
{
    private static int _checks;
    private static readonly List<object> Measurements=[];
    private static void Check(bool condition,string message)
    { _checks++;if(!condition)throw new Exception(message); }
    private static byte[] Make(string text,int size=240,bool inverted=false,string charset="UTF-8")
    {
        var matrix=new ZXing.QrCode.QRCodeWriter().encode(text,BarcodeFormat.QR_CODE,size,size,
            new Dictionary<EncodeHintType,object>{{EncodeHintType.CHARACTER_SET,charset},{EncodeHintType.MARGIN,4}});
        byte[] pixels=new byte[size*size*4];
        for(int y=0;y<size;y++)for(int x=0;x<size;x++)
        {
            int i=(y*size+x)*4;byte v=(byte)(matrix[x,y]^inverted?0:255);
            pixels[i]=pixels[i+1]=pixels[i+2]=v;pixels[i+3]=255;
        }
        return pixels;
    }
    private static void Verify(string label,byte[] pixels,int width,int height,string[] expected,int stride=0)
    {
        byte[] before=(byte[])pixels.Clone();var clock=Stopwatch.StartNew();
        string[] actual=QrCodeHelper.Decode(pixels,width,height,stride:stride);
        Check(actual.Order().SequenceEqual(expected.Order()),label+": "+string.Join("|",actual));
        Check(before.AsSpan().SequenceEqual(pixels),label+" preserves input");
        Measurements.Add(new {label,width,height,count=actual.Length,ms=clock.ElapsedMilliseconds});
    }
    [STAThread]
    private static void Main()
    {
        const string url="https://example.com/starshot?hdr=1";
        const string chinese="星光截图：中文与 English 2026 / 日本語テスト";
        Verify("url",Make(url),240,240,[url]);
        Verify("unicode",Make(chinese),240,240,[chinese]);
        Verify("GB18030-ECI",Make("中文二维码测试",charset:"GB18030"),240,240,["中文二维码测试"]);
        Verify("Shift-JIS",Make("日本語テスト",charset:"SJIS"),240,240,["日本語テスト"]);
        Verify("inverted",Make(chinese,inverted:true),240,240,[chinese]);
        byte[] source=Make(url),rotated=new byte[source.Length];
        for(int y=0;y<240;y++)for(int x=0;x<240;x++)
            source.AsSpan((y*240+x)*4,4).CopyTo(rotated.AsSpan((x*240+239-y)*4,4));
        Verify("rotated-90",rotated,240,240,[url]);
        const int stride=240*4+28;
        byte[] padded=new byte[stride*240];
        for(int y=0;y<240;y++)source.AsSpan(y*960,960).CopyTo(padded.AsSpan(y*stride,960));
        Verify("padded-stride",padded,240,240,[url],stride);
        byte[] transparent=Make(chinese);
        for(int i=0;i<transparent.Length;i+=4)if(transparent[i]==255)Array.Clear(transparent,i,4);
        Verify("premultiplied-transparent",transparent,240,240,[chinese]);
        byte[] multiple=Enumerable.Repeat((byte)255,600*280*4).ToArray(),second=Make(chinese,inverted:true);
        for(int y=0;y<240;y++)
        {
            source.AsSpan(y*960,960).CopyTo(multiple.AsSpan(((y+20)*600+20)*4,960));
            second.AsSpan(y*960,960).CopyTo(multiple.AsSpan(((y+20)*600+340)*4,960));
        }
        Verify("multiple-normal-and-inverted",multiple,600,280,[url,chinese]);
        byte[] desktop=Enumerable.Repeat((byte)255,3840*2160*4).ToArray();byte[] small=Make(url,120);
        for(int y=0;y<120;y++)small.AsSpan(y*480,480).CopyTo(desktop.AsSpan(((y+1300)*3840+2700)*4,480));
        Verify("4K-small-code-original-resolution",desktop,3840,2160,[url]);
        Verify("no-QR",Enumerable.Repeat((byte)255,640*480*4).ToArray(),640,480,[]);
        using(var cancel=new CancellationTokenSource())
        {
            cancel.Cancel();bool canceled=false;
            try{QrCodeHelper.Decode(source,240,240,cancel.Token);}catch(OperationCanceledException){canceled=true;}
            Check(canceled,"Canceled decode exits without a result");
        }
        bool invalid=false;try{QrCodeHelper.Decode(source,240,240,stride:100);}catch(ArgumentException){invalid=true;}
        Check(invalid,"Invalid stride rejected");
        invalid=false;try{QrCodeHelper.Decode([],10000,10000);}catch(ArgumentException){invalid=true;}
        Check(invalid,"Unbounded image rejected before allocation");
        foreach(string text in new[]{"file:///C:/x.exe","javascript:alert(1)","shell:startup","data:text/html,x","Hello","mailto:a@example.com"})
            Check(QrCodeHelper.WebLink(text) is null,"No launch for "+text);
        Check(QrCodeHelper.WebLink(url)?.Scheme=="https","HTTPS link allowed");
        Check(QrCodeHelper.WebLink("http://example.com") is not null,"HTTP link allowed");

        var legacy=RegionToolbarPreferences.Parse("{\"version\":1,\"main\":[\"Copy\"],\"secondary\":[\"Save\"]}");
        Check(legacy.Main.SequenceEqual(new[]{RegionToolbarCommand.Copy})&&legacy.Secondary.Contains(RegionToolbarCommand.QrCode),"Existing menus gain QR once without reordering main");
        var custom=new RegionToolbarCustomization([RegionToolbarCommand.Copy],[RegionToolbarCommand.Save]);
        Check(!RegionToolbarPreferences.Parse(RegionToolbarPreferences.Serialize(custom)).Secondary.Contains(RegionToolbarCommand.QrCode),"Intentional QR removal stays removed");
        Check(RegionToolbarCatalog.Get(RegionToolbarCommand.QrCode).Action==RegionCaptureAction.QrCode,"Both renderer commands use shared semantic action");
        uint before=GetGuiResources(Process.GetCurrentProcess().Handle,0);
        for(int i=0;i<3;i++)
        {
            using var window=new QrCodeResultWindow(show:false);
            Check(!CpuCaptureNative.IsWindowVisible(window.Handle),"Test result window is hidden");
            window.SetResults([url,chinese]);Check(window.SelectedText==url&&window.CanOpenLink,"URL result offers link");
            window.MoveResult(1);Check(window.SelectedText==chinese&&!window.CanOpenLink,"Text result offers copy only");
            nint edit=FindWindowExW(window.Handle,0,"EDIT",null);var text=new StringBuilder(1000);
            CpuCaptureNative.GetWindowTextW(edit,text,text.Capacity);Check(text.ToString()==chinese,"Native selectable text is populated");
            Check(((long)GetWindowLongPtrW(edit,-16)&0x800)!=0,"Native text is readonly");
            Check(((long)GetWindowLongPtrW(edit,-16)&0x80)==0,"Long QR text wraps rather than hiding horizontally");
            foreach(int dpi in new[]{96,192})
            {
                int scale=dpi/96;var rect=new CpuCaptureGdi.NativeRect(new(-32000,-32000,440*scale,316*scale));
                nint ptr=Marshal.AllocHGlobal(Marshal.SizeOf<CpuCaptureGdi.NativeRect>());
                try{Marshal.StructureToPtr(rect,ptr,false);CpuCaptureNative.SendMessageW(window.Handle,0x2e0,dpi|(dpi<<16),ptr);}
                finally{Marshal.FreeHGlobal(ptr);}
                CpuCaptureNative.GetWindowRect(edit,out var bounds);
                Check(bounds.Right-bounds.Left==392*scale&&bounds.Bottom-bounds.Top==134*scale,"Result text layout follows DPI without covering footer");
            }
            window.MoveResult(1);Check(window.SelectedText==url,"Multiple results wrap");
            CpuCaptureNative.SendMessageW(edit,0x100,27,0);
            Check(window.Handle==0&&window.SelectedText=="","Esc from text field closes and releases results");
        }
        Check(GetGuiResources(Process.GetCurrentProcess().Handle,0)<=before+2,"No GDI object retention across result windows");
        Check(!Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Any(m=>m.ModuleName.Contains("onnx",StringComparison.OrdinalIgnoreCase)||m.ModuleName.Equals("d3d11.dll",StringComparison.OrdinalIgnoreCase)),"CPU test has no inference/D3D module");
        Console.WriteLine(JsonSerializer.Serialize(new{ok=true,checks=_checks,screenshots=0,modelInference=false,measurements=Measurements},new JsonSerializerOptions{WriteIndented=true}));
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern nint FindWindowExW(nint parent,nint after,string cls,string? title);
    [DllImport("user32.dll")]private static extern nint GetWindowLongPtrW(nint hwnd,int index);
    [DllImport("user32.dll")]private static extern uint GetGuiResources(nint process,uint flags);
}
