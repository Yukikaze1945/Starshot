using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using Starshot.Features.Screenshot;
using Starshot.Features.Subtitles;

internal static partial class Program
{
    private static void SubtitleOutlineChecks()
    {
        nint foreground=CpuCaptureNative.GetForegroundWindow();
        using var process=Process.GetCurrentProcess();
        uint gdi=GetGuiResources(process.Handle,0),user=GetGuiResources(process.Handle,1);
        var region=new Rectangle(-28000,-25000,1280,140);
        for(int cycle=0;cycle<5;cycle++)
        {
            var outline=new SubtitleRegionOutline(region,visible:false);
            var edges=(Array)typeof(SubtitleRegionOutline).GetField("_edges",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(outline)!;
            var handles=new List<nint>();int index=0;
            foreach(var edge in edges)
            {
                nint hwnd=(nint)typeof(CpuCaptureNativeWindow).GetProperty("Hwnd",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(edge)!;
                handles.Add(hwnd);
                CpuCaptureNative.GetWindowRect(hwnd,out var rect);
                var bounds=Rectangle.FromLTRB(rect.Left,rect.Top,rect.Right,rect.Bottom);
                Check(bounds==SubtitleRegionOutline.EdgeBounds(region)[index++],"Marker preserves negative physical desktop coordinates");
                Check(!bounds.IntersectsWith(region),"Every border pixel lies outside the sampled ROI");
                long styles=GetWindowLongPtrW(hwnd,-20).ToInt64();
                Check((styles&0x080800A8)==0x080800A8,"Outline uses layered, transparent, tool, topmost and no-activate styles");
                Check(CpuCaptureNative.SendMessageW(hwnd,0x84,0,0)==-1&&CpuCaptureNative.SendMessageW(hwnd,0x21,0,0)==3,"Outline hit tests never take focus or intercept clicks");
                Check(GetWindowDisplayAffinity(hwnd,out uint affinity)&&affinity==0x11,"Outline is excluded from capture");
                Check(!CpuCaptureNative.IsWindowVisible(hwnd),"Synthetic test never shows a desktop overlay");
            }
            outline.Dispose();outline.Dispose();
            Check(handles.All(h=>!IsWindow(h)),"Dispose destroys every marker HWND, including repeated dispose");
        }
        Check(CpuCaptureNative.GetForegroundWindow()==foreground,"Hidden outline creation never steals activation");
        Check(GetGuiResources(process.Handle,0)<=gdi+1&&GetGuiResources(process.Handle,1)<=user+1,"Five outline lifetimes retain no GDI/USER handles");
    }
    [DllImport("user32.dll")]private static extern nint GetWindowLongPtrW(nint hwnd,int index);
    [DllImport("user32.dll")]private static extern bool GetWindowDisplayAffinity(nint hwnd,out uint affinity);
}
