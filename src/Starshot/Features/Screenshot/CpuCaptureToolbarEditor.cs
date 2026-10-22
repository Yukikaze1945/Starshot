using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Starshot.Features.Screenshot;

/// <summary>CPU-only modal layout editor shared by HDR and GDI capture; no browser/device/model.</summary>
internal sealed class CpuCaptureToolbarEditor : CpuCaptureNativeWindow
{
    private readonly RegionToolbarEditModel _model = new(RegionToolbarPreferences.Current);
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CpuCaptureSurface? _buffer;
    private nint _font, _small, _glyph;
    private float _scale;
    private int _side, _index, _poolIndex, _poolScroll;
    private readonly int[] _scroll = [0, 0];
    private bool _pool, _dragging, _accepted;
    private int _dragSide, _dragIndex = -1;
    private Point _mouse, _press;
    private string _error = "";
    private static readonly Color Paper = Color.FromArgb(245,244,236), Surface = Color.FromArgb(251,252,246),
        Ink = Color.FromArgb(36,43,36), Muted = Color.FromArgb(128,138,119), Accent = Color.FromArgb(221,243,105), Line = Color.FromArgb(215,221,206);

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public CpuCaptureNative.NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }
    private void CreateFonts()
    {
        foreach (nint font in new[] { _font, _small, _glyph }) if (font != 0) CpuCaptureGdi.DeleteObject(font);
        _font = CpuCaptureGdi.Font(D(14)); _small = CpuCaptureGdi.Font(D(11));
        _glyph = CpuCaptureGdi.Font(D(17), "Segoe MDL2 Assets");
    }

    internal CpuCaptureToolbarEditor(nint owner = 0, bool show = false)
    {
        CpuCaptureNative.GetCursorPos(out var mouse);
        var monitors = RegionToolbarMonitors.Read(new(0,0,1920,1080),1);
        var monitor = monitors.FirstOrDefault(m => m.Bounds.Contains(mouse.X,mouse.Y)) ?? monitors[0];
        _scale = monitor.Scale;
        int width = (int)Math.Min(760 * _scale, monitor.WorkArea.Width - 20 * _scale);
        int height = (int)Math.Min(570 * _scale, monitor.WorkArea.Height - 20 * _scale);
        Create("Starshot · 编辑截图菜单", show ? (int)(monitor.WorkArea.X + (monitor.WorkArea.Width-width)/2) : -32000,
            show ? (int)(monitor.WorkArea.Y + (monitor.WorkArea.Height-height)/2) : -32000,
            Math.Max(320,width),Math.Max(300,height),style:0x800C0000,extendedStyle:0x88);
        try
        {
            if(owner!=0) CpuCaptureNative.SetWindowLongPtrW(Hwnd,-8,owner);
            CreateFonts();
            if(show) { CpuCaptureNative.ShowWindow(Hwnd,5); CpuCaptureNative.SetForegroundWindow(Hwnd); CpuCaptureNative.SetFocus(Hwnd); }
        }
        catch { Dispose(); throw; }
    }
    internal static async Task<bool> ShowAsync(nint owner)
    {
        using var editor = new CpuCaptureToolbarEditor(owner,show:true);
        if (owner != 0) CpuCaptureNative.EnableWindow(owner,false);
        try { return await editor._completion.Task; }
        finally { if (owner != 0) { CpuCaptureNative.EnableWindow(owner,true); CpuCaptureNative.SetForegroundWindow(owner); CpuCaptureNative.SetFocus(owner); } }
    }
    private int D(float v) => Math.Max(1,(int)Math.Round(v*_scale));
    private Size Client { get { CpuCaptureNative.GetClientRect(Hwnd,out var r);return new(Math.Max(1,r.Right),Math.Max(1,r.Bottom)); } }
    private Rectangle ListBounds(int side)
    {
        var size=Client;int width=(size.Width-D(116))/2;
        return new(side==0?D(20):D(96)+width,D(110),width,Math.Max(D(60),size.Height-D(205)));
    }
    private Rectangle Button(string name)
    {
        var size=Client;int mid=size.Width/2-D(18);
        return name switch {
            "close"=>new(size.Width-D(48),0,D(48),D(46)),
            "reset"=>new(D(20),size.Height-D(56),D(116),D(36)),
            "cancel"=>new(size.Width-D(112),size.Height-D(56),D(92),D(36)),
            "accept"=>new(size.Width-D(220),size.Height-D(56),D(96),D(36)),
            _=>new(mid,D(114)+Array.IndexOf(new[]{"add","remove","right","left","up","down"},name)*D(44),D(36),D(36))
        };
    }
    private Rectangle PoolBounds() { var size=Client;return new(D(55),D(75),Math.Max(D(160),size.Width-D(110)),Math.Max(D(100),size.Height-D(150))); }
    private int VisibleRows(Rectangle r) => Math.Max(1,(r.Height-D(12))/D(36));
    private void EnsureVisible()
    {
        var list=_model.List(_side);_index=Math.Clamp(_index,0,Math.Max(0,list.Count-1));
        int rows=VisibleRows(ListBounds(_side));
        _scroll[_side]=Math.Clamp(_scroll[_side],Math.Max(0,_index-rows+1),_index);
    }
    private void Text(nint dc,string text,Rectangle rect,bool small=false,Color? color=null,bool center=false) =>
        CpuCaptureGdi.Text(dc,text,rect,small?_small:_font,color??Ink,center);
    private void DrawButton(nint dc,string name,string text,bool accent=false)
    {
        var r=Button(name);CpuCaptureGdi.Fill(dc,r,accent?Accent:r.Contains(_mouse)?Color.FromArgb(231,235,220):Surface,D(7));
        CpuCaptureGdi.Shape(dc,r,Line,D(1));Text(dc,text,r,center:true);
    }
    private void Paint()
    {
        nint target=CpuCaptureNative.BeginPaint(Hwnd,out var paint);
        try
        {
            var size=Client;
            if(_buffer is not null && (_buffer.Width!=size.Width||_buffer.Height!=size.Height)){_buffer.Dispose();_buffer=null;}
            _buffer??=new(size.Width,size.Height);nint dc=_buffer.Dc;
            CpuCaptureGdi.Fill(dc,new(0,0,size.Width,size.Height),Paper);
            Text(dc,"starshot  /  编辑截图菜单",new(D(20),0,size.Width-D(80),D(46)));
            Text(dc,"常用的留在眼前，其余收进「更多」。",new(D(20),D(51),size.Width-D(40),D(24)),true,Muted);
            DrawButton(dc,"close","×");
            for(int side=0;side<2;side++)
            {
                var r=ListBounds(side);var list=_model.List(side);
                Text(dc,side==0?"主按钮":"次选按钮 · 更多菜单",new(r.X,D(82),r.Width,D(25)));
                CpuCaptureGdi.Fill(dc,r,Surface,D(9));CpuCaptureGdi.Shape(dc,r,Line,D(1));
                int rows=VisibleRows(r);_scroll[side]=Math.Clamp(_scroll[side],0,Math.Max(0,list.Count-rows));
                for(int index=_scroll[side];index<Math.Min(list.Count,_scroll[side]+rows);index++)
                {
                    var row=new Rectangle(r.X+D(6),r.Y+D(6)+(index-_scroll[side])*D(36),r.Width-D(12),D(34));
                    if(side==_side&&index==_index)CpuCaptureGdi.Fill(dc,row,Color.FromArgb(232,239,210),D(5));
                    var desc=RegionToolbarCatalog.Get(list[index]);
                    CpuCaptureGdi.Text(dc,desc.Glyph,new(row.X+D(7),row.Y,D(32),row.Height),desc.Symbol?_glyph:_small,Ink,true);
                    Text(dc,desc.Label,new(row.X+D(43),row.Y,row.Width-D(50),row.Height),true);
                }
                if(list.Count==0) Text(dc,"拖入按钮，或点击 + 添加",new(r.X+D(10),r.Y+D(20),r.Width-D(20),D(50)),true,Muted,true);
                if(list.Count>rows)
                {
                    Text(dc,$"{_scroll[side]+1}–{Math.Min(list.Count,_scroll[side]+rows)} / {list.Count}",new(r.Right-D(90),r.Bottom-D(19),D(80),D(16)),true,Muted);
                    int trackHeight = r.Height-D(34), thumbHeight = Math.Max(D(24), trackHeight*rows/list.Count);
                    int thumbY = r.Top+D(8)+(trackHeight-thumbHeight)*_scroll[side]/Math.Max(1,list.Count-rows);
                    CpuCaptureGdi.Fill(dc,new(r.Right-D(5),thumbY,D(3),thumbHeight),Line,D(1));
                }
                if(_dragging&&r.Contains(_mouse))
                {
                    int index=Math.Clamp((_mouse.Y-r.Top-D(6))/D(36)+_scroll[side],0,list.Count);
                    int y=Math.Clamp(r.Y+D(6)+(index-_scroll[side])*D(36),r.Top+D(3),r.Bottom-D(3));
                    CpuCaptureGdi.Line(dc,new(r.X+D(8),y),new(r.Right-D(8),y),Color.FromArgb(131,158,58),D(2));
                }
            }
            foreach(var (name,label) in new[]{("add","+"),("remove","−"),("right","→"),("left","←"),("up","↑"),("down","↓")})DrawButton(dc,name,label);
            DrawButton(dc,"reset","恢复默认");DrawButton(dc,"cancel","取消");DrawButton(dc,"accept","保存布局",true);
            Text(dc,_error.Length>0?_error:"拖动排序 / 跨栏移动 · Alt+↑↓ 排序 · Delete 移除",new(D(20),size.Height-D(89),size.Width-D(40),D(23)),true,_error.Length>0?Color.DarkRed:Muted);
            if(_pool)
            {
                var r=PoolBounds();CpuCaptureGdi.Fill(dc,r,Surface,D(10));CpuCaptureGdi.Shape(dc,r,Color.FromArgb(131,158,58),D(2));
                Text(dc,"添加按钮  ·  Esc 返回",new(r.X+D(14),r.Y+D(8),r.Width-D(28),D(32)));
                var unused=_model.Unused();int rows=Math.Max(1,(r.Height-D(56))/D(34));_poolScroll=Math.Clamp(_poolScroll,0,Math.Max(0,unused.Length-rows));
                for(int index=_poolScroll;index<Math.Min(unused.Length,_poolScroll+rows);index++)
                {
                    var row=new Rectangle(r.X+D(10),r.Y+D(46)+(index-_poolScroll)*D(34),r.Width-D(20),D(32));
                    if(index==_poolIndex)CpuCaptureGdi.Fill(dc,row,Accent,D(5));
                    Text(dc,RegionToolbarCatalog.Get(unused[index]).Label,row,true,center:true);
                }
                if(unused.Length==0)Text(dc,"所有按钮均已加入",new(r.X,r.Y+D(50),r.Width,D(50)),true,Muted,true);
            }
            CpuCaptureGdi.BitBlt(target,0,0,size.Width,size.Height,dc,0,0,CpuCaptureGdi.Copy);
        }
        finally{CpuCaptureNative.EndPaint(Hwnd,ref paint);}
    }
    private void Action(string name)
    {
        _error="";
        if(name is "cancel" or "close"){Dispose();return;}
        if(name=="accept")
        {
            try{RegionToolbarPreferences.Save(_model.Snapshot());_accepted=true;Dispose();}
            catch(Exception ex){_error="布局未保存："+ex.Message;Redraw();}return;
        }
        if(name=="reset"){_model.Replace(RegionToolbarPreferences.Defaults());_index=0;_scroll[0]=_scroll[1]=0;}
        else if(name=="add"){_pool=true;_poolIndex=_poolScroll=0;}
        else if(name=="remove"&&_index<_model.List(_side).Count)_model.List(_side).RemoveAt(_index);
        else if(name=="right"||name=="left")
        {int to=name=="right"?1:0;if(to!=_side&&_model.Move(_side,_index,to,_model.List(to).Count)){_side=to;_index=_model.List(to).Count-1;}}
        else if(name=="up"&&_index>0){_model.Move(_side,_index,_side,_index-1);_index--;}
        else if(name=="down"&&_index+1<_model.List(_side).Count){_model.Move(_side,_index,_side,_index+2);_index++;}
        EnsureVisible();Redraw();
    }
    private void AddFromPool(int index)
    {var unused=_model.Unused();if(index>=0&&index<unused.Length){_model.List(_side).Add(unused[index]);_index=_model.List(_side).Count-1;_pool=false;EnsureVisible();Redraw();}}
    private void Press()
    {
        if(_pool){var r=PoolBounds();if(r.Contains(_mouse)&&_mouse.Y>=r.Y+D(46))AddFromPool((_mouse.Y-r.Y-D(46))/D(34)+_poolScroll);else{_pool=false;Redraw();}return;}
        foreach(var name in new[]{"close","reset","cancel","accept","add","remove","right","left","up","down"})
            if(Button(name).Contains(_mouse)){Action(name);return;}
        for(int side=0;side<2;side++)if(ListBounds(side).Contains(_mouse))
        {
            var r=ListBounds(side);int index=(_mouse.Y-r.Top-D(6))/D(36)+_scroll[side];
            if(index>=0&&index<_model.List(side).Count){_side=side;_index=index;_dragSide=side;_dragIndex=index;_press=_mouse;CpuCaptureNative.SetCapture(Hwnd);}
            else{_side=side;_index=0;}
            Redraw();return;
        }
    }
    private void Release()
    {
        if(_dragging&&_dragIndex>=0)for(int side=0;side<2;side++)if(ListBounds(side).Contains(_mouse))
        {
            var c=_model.List(_dragSide)[_dragIndex];var r=ListBounds(side);
            _model.Move(_dragSide,_dragIndex,side,(_mouse.Y-r.Top-D(6))/D(36)+_scroll[side]);
            _side=side;_index=_model.List(side).IndexOf(c);EnsureVisible();break;
        }
        _dragIndex=-1;_dragging=false;CpuCaptureNative.ReleaseCapture();Redraw();
    }
    protected override nint? HandleMessage(uint message,nint wparam,nint lparam)
    {
        switch(message)
        {
            case 0x24: // Keep both lists and the six transfer buttons clear of the footer.
                var limits = Marshal.PtrToStructure<MinMaxInfo>(lparam);
                limits.MinTrackSize = new() { X = D(570), Y = D(480) };
                Marshal.StructureToPtr(limits, lparam, false); return 0;
            case 0x2E0: // Per-monitor DPI changes, including dragging across differently scaled displays.
                _scale = Math.Clamp((int)((long)wparam & 0xffff) / 96f, .5f, 4f);
                CreateFonts();
                var suggested = Marshal.PtrToStructure<CpuCaptureGdi.NativeRect>(lparam);
                CpuCaptureNative.SetWindowPos(Hwnd, 0, suggested.Left, suggested.Top,
                    suggested.Right-suggested.Left, suggested.Bottom-suggested.Top, 0x14);
                Redraw(); return 0;
            case 0x14:return 1;
            case 0x0F:Paint();return 0;
            case 0x10:Dispose();return 0;
            case 0x84:
                CpuCaptureNative.GetWindowRect(Hwnd,out var outer);
                int x=(short)((long)lparam&0xffff)-outer.Left,y=(short)(((long)lparam>>16)&0xffff)-outer.Top;
                if(y>D(6)&&y<D(46)&&x>D(8)&&x<Client.Width-D(52))return 2;
                return null;
            case 0x200:
                _mouse=new((short)((long)lparam&0xffff),(short)(((long)lparam>>16)&0xffff));
                if(_dragIndex>=0&&Math.Abs(_mouse.X-_press.X)+Math.Abs(_mouse.Y-_press.Y)>D(5))_dragging=true;
                Redraw();return 0;
            case 0x201:
                _mouse=new((short)((long)lparam&0xffff),(short)(((long)lparam>>16)&0xffff));Press();return 0;
            case 0x202:
                _mouse=new((short)((long)lparam&0xffff),(short)(((long)lparam>>16)&0xffff));Release();return 0;
            case 0x215:_dragIndex=-1;_dragging=false;return 0;
            case 0x20A:
                int delta=(short)(((long)wparam>>16)&0xffff)>0?-1:1;
                if(_pool)_poolScroll=Math.Max(0,_poolScroll+delta);
                else{int side=ListBounds(1).Contains(_mouse)?1:0;_scroll[side]=Math.Max(0,_scroll[side]+delta);}
                Redraw();return 0;
            case 0x05:Redraw();return 0;
            case 0x100:
            case 0x104:
                int key=(int)wparam;bool alt=(CpuCaptureNative.GetKeyState(18)&0x8000)!=0;
                if(key==27){if(_pool){_pool=false;Redraw();}else Dispose();return 0;}
                if(key==13){if(_pool)AddFromPool(_poolIndex);else Action("accept");return 0;}
                if(key==9){_side=1-_side;_index=0;EnsureVisible();Redraw();return 0;}
                if(key==46){Action("remove");return 0;}
                if(key==45){Action("add");return 0;}
                if(key==37||key==39){Action(key==37?"left":"right");return 0;}
                if(key==38||key==40)
                {int step=key==38?-1:1;if(_pool){_poolIndex=Math.Clamp(_poolIndex+step,0,Math.Max(0,_model.Unused().Length-1));_poolScroll=Math.Max(0,_poolIndex-3);}else if(alt)Action(step<0?"up":"down");else{_index+=step;EnsureVisible();}Redraw();return 0;}
                break;
        }
        return null;
    }
    protected override void OnNativeClosed()
    {
        _buffer?.Dispose();_buffer=null;
        foreach(nint font in new[]{_font,_small,_glyph})if(font!=0)CpuCaptureGdi.DeleteObject(font);
        _font=_small=_glyph=0;_completion.TrySetResult(_accepted);
    }
}
