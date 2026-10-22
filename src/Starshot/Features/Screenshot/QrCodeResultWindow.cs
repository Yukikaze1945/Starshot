using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Starshot.Helpers;

namespace Starshot.Features.Screenshot;

/// <summary>Shared, CPU/GDI result surface, including lightweight capture. Never retains the source image.</summary>
internal sealed class QrCodeResultWindow : CpuCaptureNativeWindow
{
    private string[] _results = [];
    private int _index;
    private string _status = "正在本机识别…";
    private readonly CancellationTokenSource _closed = new();
    private readonly SubclassProc _editProc;
    private nint _edit, _font, _small;
    private float _scale;
    internal nint Handle => Hwnd;
    internal string SelectedText => _results.Length == 0 ? "" : _results[_index];
    internal bool CanOpenLink => QrCodeHelper.WebLink(SelectedText) is not null;
    private static readonly Color Paper = Color.FromArgb(248,249,242), Ink = Color.FromArgb(36,43,36),
        Muted = Color.FromArgb(117,126,107), Accent = Color.FromArgb(221,243,105);

    internal QrCodeResultWindow(bool show = true)
    {
        _editProc = EditProcedure;
        CpuCaptureNative.GetCursorPos(out var mouse);
        var monitors = RegionToolbarMonitors.Read(new(0,0,1920,1080),1);
        var monitor = monitors.FirstOrDefault(m => m.Bounds.Contains(mouse.X,mouse.Y)) ?? monitors[0];
        _scale = monitor.Scale;
        int w = Math.Min(D(440),(int)monitor.WorkArea.Width-D(24));
        int h = Math.Min(D(316),(int)monitor.WorkArea.Height-D(24));
        Create("Starshot · 二维码",show ? (int)(monitor.WorkArea.X+(monitor.WorkArea.Width-w)/2) : -32000,
            show ? (int)(monitor.WorkArea.Y+(monitor.WorkArea.Height-h)/2) : -32000,w,h,
            style:0x80000000,extendedStyle:0x88);
        try
        {
            _font = CpuCaptureGdi.Font(D(14)); _small = CpuCaptureGdi.Font(D(11));
            _edit = CpuCaptureNative.CreateWindowExW(0,"EDIT","",0x50210844,0,0,1,1,Hwnd,0,
                CpuCaptureNative.GetModuleHandleW(null),0); // child, visible, readonly multiline, vertical scroll
            if (_edit == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            CpuCaptureNative.SendMessageW(_edit,0x30,_font,1);
            if (!SetWindowSubclass(_edit,_editProc,1,0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            LayoutEdit();
            if(show) { CpuCaptureNative.ShowWindow(Hwnd,5);CpuCaptureNative.SetForegroundWindow(Hwnd); }
        }
        catch { Dispose();_closed.Dispose();throw; }
    }

    internal static async Task ShowAsync(byte[] pixels,int width,int height,CancellationToken ct = default)
    {
        // The HWND keeps the result rooted after this method returns. Only the decoder borrows pixels.
        var window = new QrCodeResultWindow();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct,window._closed.Token);
        try
        {
            var values = await QrCodeHelper.DecodeAsync(pixels,width,height,linked.Token);
            if(window.Hwnd != 0 && !linked.IsCancellationRequested) window.SetResults(values);
        }
        catch(OperationCanceledException) { window.Dispose(); }
        catch(Exception ex)
        {
            Serilog.Log.Warning(ex,"[QR] recognition failed");
            if(window.Hwnd != 0) { window._status="识别失败："+ex.Message;window.Redraw(); }
        }
        finally { window._closed.Dispose(); }
    }

    internal void SetResults(string[] values)
    {
        _results=values;_index=0;UpdateText();
        _status=values.Length==0 ? "未发现二维码，请框选完整二维码及周围留白。" : "仅识别内容，不会自动复制或打开链接。";
        Redraw();
    }
    internal void MoveResult(int delta)
    { if(_results.Length>0) { _index=(_index+delta+_results.Length)%_results.Length;UpdateText();Redraw(); } }
    private void UpdateText()
    { CpuCaptureNative.SetWindowTextW(_edit,SelectedText.Replace("\r\n","\n").Replace("\n","\r\n")); }
    private int D(float v) => Math.Max(1,(int)Math.Round(v*_scale));
    private Size Client { get { CpuCaptureNative.GetClientRect(Hwnd,out var r);return new(r.Right,r.Bottom); } }
    private Rectangle Button(string key)
    {
        var s=Client;
        return key switch {
            "close"=>new(s.Width-D(44),D(4),D(36),D(36)),
            "previous"=>new(s.Width-D(86),D(54),D(28),D(26)),
            "next"=>new(s.Width-D(52),D(54),D(28),D(26)),
            "copy"=>new(s.Width-D(128),s.Height-D(54),D(108),D(34)),
            _=>new(D(20),s.Height-D(54),D(110),D(34)),
        };
    }
    private void LayoutEdit()
    {
        var s=Client;
        CpuCaptureNative.SetWindowPos(_edit,0,D(24),D(92),Math.Max(1,s.Width-D(48)),Math.Max(1,s.Height-D(182)),0x14);
    }
    private void CopyText()
    {
        if(_results.Length==0)return;
        try{ClipboardHelper.SetText(SelectedText);_status="内容已复制";}
        catch(Exception ex){_status="复制未完成："+ex.Message;}
        Redraw();
    }
    private void OpenLink()
    {
        try { if(QrCodeHelper.WebLink(SelectedText) is {} uri)Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true}); }
        catch(Exception ex){_status="打开未完成："+ex.Message;Redraw();}
    }
    private void Text(nint dc,string text,Rectangle rect,bool small=false,Color? color=null,bool center=false) =>
        CpuCaptureGdi.Text(dc,text,rect,small?_small:_font,color??Ink,center);
    private void DrawButton(nint dc,string key,string label,bool enabled,bool accent=false)
    {
        var rect=Button(key);
        CpuCaptureGdi.Fill(dc,rect,accent&&enabled?Accent:Paper,D(8));
        Text(dc,label,rect,color:enabled?Ink:Muted,center:true);
    }
    private void Paint()
    {
        nint dc=CpuCaptureNative.BeginPaint(Hwnd,out var paint);
        try
        {
            var s=Client;
            CpuCaptureGdi.Fill(dc,new(0,0,s.Width,s.Height),Paper);
            CpuCaptureGdi.Shape(dc,new(0,0,s.Width-1,s.Height-1),Color.FromArgb(208,216,197),1);
            Text(dc,"starshot  /  二维码",new(D(20),D(6),s.Width-D(70),D(32)));
            DrawButton(dc,"close","×",true);
            Text(dc,_results.Length>0?$"识别结果   {_index+1} / {_results.Length}":"QR READER",new(D(24),D(52),s.Width-D(130),D(28)),true,Muted);
            DrawButton(dc,"previous","‹",_results.Length>1);DrawButton(dc,"next","›",_results.Length>1);
            Text(dc,_status,new(D(24),s.Height-D(82),s.Width-D(48),D(24)),true,Muted);
            DrawButton(dc,"open","打开链接 ↗",CanOpenLink);
            DrawButton(dc,"copy","复制内容",_results.Length>0,true);
        }
        finally {CpuCaptureNative.EndPaint(Hwnd,ref paint);}
    }
    protected override nint? HandleMessage(uint message,nint wparam,nint lparam)
    {
        if(message==0x14)return 1;
        if(message==0x0F){Paint();return 0;}
        if(message==0x85 || message==0x86)return 1; // custom frame
        if(message==0x84)
        {
            CpuCaptureNative.GetWindowRect(Hwnd,out var rect);
            int x=(short)((long)lparam&0xffff)-rect.Left,y=(short)((long)lparam>>16)-rect.Top;
            return y<D(44)&&!Button("close").Contains(x,y)?2:1;
        }
        if(message==0x100)
        {
            if(wparam==27){Dispose();return 0;}
            if(wparam==13 || wparam==0x43&&CpuCaptureNative.GetKeyState(0x11)<0){CopyText();return 0;}
            if(wparam==0x4f&&CpuCaptureNative.GetKeyState(0x11)<0){OpenLink();return 0;}
            if(wparam==37){MoveResult(-1);return 0;}
            if(wparam==39){MoveResult(1);return 0;}
            if(wparam==9){CpuCaptureNative.SetFocus(_edit);return 0;}
        }
        if(message==0x201)
        {
            var p=new Point((short)((long)lparam&0xffff),(short)((long)lparam>>16));
            if(Button("close").Contains(p)){Dispose();return 0;}
            if(Button("previous").Contains(p))MoveResult(-1);
            else if(Button("next").Contains(p))MoveResult(1);
            else if(Button("copy").Contains(p))CopyText();
            else if(Button("open").Contains(p))OpenLink();
            return 0;
        }
        if(message==0x18 && wparam!=0){LayoutEdit();return 0;}
        if(message==0x2e0) // WM_DPICHANGED: keep the floating result usable when moved between monitors.
        {
            _scale=((int)wparam&0xffff)/96f;
            nint oldFont=_font,oldSmall=_small;
            _font=CpuCaptureGdi.Font(D(14));_small=CpuCaptureGdi.Font(D(11));
            CpuCaptureNative.SendMessageW(_edit,0x30,_font,1);
            CpuCaptureGdi.DeleteObject(oldFont);CpuCaptureGdi.DeleteObject(oldSmall);
            var rect=Marshal.PtrToStructure<CpuCaptureGdi.NativeRect>(lparam);
            CpuCaptureNative.SetWindowPos(Hwnd,0,rect.Left,rect.Top,rect.Right-rect.Left,rect.Bottom-rect.Top,0x14);
            LayoutEdit();Redraw();return 0;
        }
        return null;
    }
    protected override void OnNativeClosed()
    {
        // ShowAsync disposes its CTS after the borrowed-input task finishes; closure can happen later.
        try {_closed.Cancel();_closed.Dispose();}catch(ObjectDisposedException){}
        _results=[];_edit=0;
        if(_font!=0)CpuCaptureGdi.DeleteObject(_font);
        if(_small!=0)CpuCaptureGdi.DeleteObject(_small);
        _font=_small=0;
    }
    private nint EditProcedure(nint hwnd,uint message,nint wparam,nint lparam,nuint id,nuint data)
    {
        if(message==0x100&&wparam==27){Dispose();return 0;}
        if(message==0x100&&wparam==13){CopyText();return 0;}
        if(message==0x100&&wparam==0x4f&&CpuCaptureNative.GetKeyState(0x11)<0){OpenLink();return 0;}
        if(message==0x100&&wparam==9){CpuCaptureNative.SetFocus(Hwnd);return 0;}
        if(message==0x82)RemoveWindowSubclass(hwnd,_editProc,id);
        return DefSubclassProc(hwnd,message,wparam,lparam);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint SubclassProc(nint hwnd,uint message,nint wparam,nint lparam,nuint id,nuint data);
    [DllImport("comctl32.dll",SetLastError=true)] private static extern bool SetWindowSubclass(nint hwnd,SubclassProc proc,nuint id,nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd,SubclassProc proc,nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd,uint message,nint wparam,nint lparam);
}
