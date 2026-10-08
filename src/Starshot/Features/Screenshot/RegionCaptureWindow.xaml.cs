using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Starshot.Features.Codec;
using Starshot.Frameworks;
using Starshot.Helpers;
using Vanara.PInvoke;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace Starshot.Features.Screenshot;

public enum RegionCaptureAction { Cancel, Save, Copy, Ocr, Translate, Pin, RecordGif, LongCapture }

public sealed record RegionCaptureResult(
    RegionCaptureAction Action,
    Rect SelectionRect,
    Rect PhysicalRect,
    CanvasRenderTarget? SdrCrop,
    CanvasRenderTarget? AnnotationLayer = null
);

public sealed partial class RegionCaptureWindow : WindowEx
{
    private const int MinimumRectangleSize = 5;
    private const int MagnifierPixelCount = 15;
    private const int MagnifierPixelSize = 10;
    private const int MagnifierCoordGap = 4;
    private const int MagnifierCoordHeight = 22;

    private enum RegionCaptureState { Selecting, Selected, Completing, Closed }
    private enum DragMode { None, Creating, Moving, Resizing }
    private enum ResizeHandle { None, TopLeft, Top, TopRight, Left, Right, BottomLeft, Bottom, BottomRight }

    public Rect SelectionRect { get; private set; }
    private RegionCaptureState _state = RegionCaptureState.Closed;
    private RegionCaptureAction _defaultAction;

    private CanvasBitmap _canvasOriginal; // 原始帧（裁剪用，可能 HDR），每次 SetCapture 更新
    private CanvasBitmap? _displayBitmap; // 显示用（SDR 色调映射后），每次 SetCapture 重建；会话间为 null（CloseWindow 清引用）
    private byte[]? _displayPixels; // One readback per capture; color picking never blocks a redraw on GPU readback.
    private Color _sampledColor;
    private float _scale;
    private int _vx,
        _vy; // 虚拟屏幕物理坐标原点（放大镜钳制到当前显示器用）

    private Point _positionOnClick;
    private DragMode _dragMode;
    private ResizeHandle _activeHandle;
    private Rect _dragStartPhysicalRect;
    private Point _dragStartPhysicalPoint;
    private bool _pressedOnHover; // 左键按下瞬间是否悬停在某个窗口上（单击截图用）
    private Point _currentMousePos;

    // 选区来源：true=鼠标框选（端点是光标像素索引，需 +1，对应 CreateRectangle）；
    // false=窗口矩形（本身就是正常尺寸，不 +1）
    private bool _selectionFromDrag;

    private List<Rect> _windowRects = new();
    private Rect _hoverRect;
    private bool _hasHover;

    private bool _isClosed;
    private CanvasSwapChain? _swapChain;
    private DispatcherQueueTimer? _moveInTimer;
    private bool _redrawQueued;
    private bool _traceNextRedraw;

    // 锁定画布尺寸（首帧后固定，防止布局抖动导致冻结帧移动）
    private float _lockedW;
    private float _lockedH;
    private bool _sizeLocked;

    // HDR 时 _displayBitmap 是本窗新建的 SDR 副本，由本窗释放；
    // SDR 时它就是传入的 canvas（= composite），归调用方，不能动
    private bool _ownsDisplayBitmap;
    private bool _cleanedUp;

    // 关窗移屏外方案配套：截图前的前台窗口（关窗时还焦点）、待移回屏内标记与节拍计数
    private nint _prevForeground;
    private bool _pendingMoveIn;
    private int _captureGeneration;

    // 原生兜底覆盖进程内首次完成光标交接前的截图会话；真实物理移动后不再启用。
    // 如果首次会话未发生物理移动就结束，后续会话仍可继续尝试。
    // 所有入口共用此窗口类型；即使窗口销毁重建也不重复兜底（仅 UI 线程访问）。
    // 后续会话完全由原有 ProtectedCursor 管理，不注入任何输入。
    private static bool _firstCaptureCursorHandoffCompleted;
    private bool _captureCursorActive;
    private bool _captureCursorApplied;
    private POINT? _captureCursorOrigin;

    // 单例：最终动作只交付一次；覆盖层与保存/复制/OCR 解耦。
    public TaskCompletionSource<RegionCaptureResult> Completion { get; private set; }

    public RegionCaptureWindow()
    {
        InitializeComponent();
        this.Closed += RegionCaptureWindow_Closed;

        // 窗口设置（单例，只一次）
        // 不写 WindowEx.MainWindowId：那是主窗口的静态锚（CenterInScreen 用），覆盖层窗口
        // 既不用居中，写它会歪曲 UpdateWindow/WelcomeWindow 的定位（且单例重建时会反复覆盖）
        Title = "Starshot";
        AppWindow.IsShownInSwitchers = false;
        SystemBackdrop = new TransparentBackdrop();

        // The monitor WGC session remains active while this overlay is visible. Exclude our
        // own top-level window so presenting the frozen frame cannot feed it back into WGC.
        if (!SetWindowDisplayAffinity(WindowHandle, 0x11))
            Serilog.Log.Warning("Region overlay capture exclusion failed: error={Error}", Marshal.GetLastPInvokeError());
        else
            Serilog.Log.Information("Region overlay excluded from monitor capture: hwnd={Hwnd}", (nint)WindowHandle);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(false, false);
        }

        int vx = User32.GetSystemMetrics((User32.SystemMetric)76);
        int vy = User32.GetSystemMetrics((User32.SystemMetric)77);
        int vw = User32.GetSystemMetrics((User32.SystemMetric)78);
        int vh = User32.GetSystemMetrics((User32.SystemMetric)79);
        _vx = vx;
        _vy = vy;
        AppWindow.MoveAndResize(new RectInt32(vx, vy, vw, vh));

        // 清除残留窗口边框样式（WinUI 的 SetBorderAndTitleBar 仍留 ~2px resize frame）
        var style = (User32.WindowStyles)
            User32.GetWindowLong(WindowHandle, User32.WindowLongFlags.GWL_STYLE);
        style &= ~(
            User32.WindowStyles.WS_THICKFRAME
            | User32.WindowStyles.WS_BORDER
            | User32.WindowStyles.WS_CAPTION
            | User32.WindowStyles.WS_DLGFRAME
        );
        User32.SetWindowLong(WindowHandle, User32.WindowLongFlags.GWL_STYLE, (nint)style);
        // 任务栏硬保证：窗口永不 Hide（只挪屏外）后 IsWindowVisible 恒真，IsShownInSwitchers=false 只是提示，
        // 激活/样式手术等时机会失守让窗口冒进任务栏；TOOLWINDOW + 清 APPWINDOW 是 shell 层的硬规则
        var exStyle = (User32.WindowStylesEx)
            User32.GetWindowLong(WindowHandle, User32.WindowLongFlags.GWL_EXSTYLE);
        exStyle |= User32.WindowStylesEx.WS_EX_TOOLWINDOW;
        exStyle &= ~User32.WindowStylesEx.WS_EX_APPWINDOW;
        User32.SetWindowLong(WindowHandle, User32.WindowLongFlags.GWL_EXSTYLE, (nint)exStyle);
        User32.SetWindowPos(
            WindowHandle,
            IntPtr.Zero,
            vx,
            vy,
            vw,
            vh,
            (User32.SetWindowPosFlags)0x0020 | User32.SetWindowPosFlags.SWP_NOZORDER
        );

        PointerCursor.SetCursorShape(Canvas, InputSystemCursorShape.Cross);

        // _scale 按覆盖层窗口 DPI（d56df02）；swapChain 移到 SetCapture 创建（CloseWindow 释放本进程显存）
        float dpi = User32.GetDpiForWindow(WindowHandle);
        _scale = dpi / 96f;
    }

    /// <summary>
    /// 窗口被用户真关（任务栏/系统关闭）后置 true 且不再复位——
    /// service 据此丢弃单例重建窗口，SetCapture 也不会再碰已销毁的 HWND。
    /// </summary>
    public bool IsDestroyed { get; private set; }

    /// <summary>
    /// 每次截图调用：更新冻结帧 + 重置交互状态 + 显示。窗口单例且永不 Hide——
    /// 关窗时移到屏外保持 IsWindowVisible（合成管线不停摆），下次截图先把新帧 Present 上屏
    /// 再移回屏内，从根上避免 Show 瞬间 DWM 先合成保留的旧会话帧（启动闪上次截图界面）。
    /// </summary>
    public void SetCapture(CanvasBitmap canvas, float sdrWhiteLevel, int physW, int physH,
        RegionCaptureAction defaultAction)
    {
        int generation = ++_captureGeneration;
        StopMoveInTimer();
        _pendingMoveIn = false;
        _redrawQueued = false;
        LogOverlayPhase("Region overlay SetCapture begin", generation);
        try
        {
        _vx = User32.GetSystemMetrics((User32.SystemMetric)76);
        _vy = User32.GetSystemMetrics((User32.SystemMetric)77);
        // 显示器布局改变时先在屏外调整 HWND 尺寸；不能把旧内容提前移回桌面。
        User32.SetWindowPos(WindowHandle, IntPtr.Zero, -32000, -32000, physW, physH,
            User32.SetWindowPosFlags.SWP_NOZORDER | User32.SetWindowPosFlags.SWP_NOACTIVATE);
        _scale = GetCaptureScale();
        // 每次会话在屏外创建新 swap chain，结束时释放，避免 idle 常驻全屏双缓冲。
        float needW = physW / _scale,
            needH = physH / _scale;
        if (
            _swapChain is null
            || Math.Abs((float)_swapChain.Size.Width - needW) > 0.5f
            || Math.Abs((float)_swapChain.Size.Height - needH) > 0.5f
        )
        {
            ReleaseSwapChain();
            _swapChain = new CanvasSwapChain(
                CanvasDevice.GetSharedDevice(),
                needW,
                needH,
                _scale * 96f,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                CanvasAlphaMode.Premultiplied
            );
            Canvas.SwapChain = _swapChain;
        }

        // 更新帧（旧的已在 CloseWindow 释放并清引用）
        _canvasOriginal = canvas;
        _displayBitmap = CreateDisplayBitmap(canvas, physW, physH, sdrWhiteLevel);
        _ownsDisplayBitmap = !ReferenceEquals(_displayBitmap, canvas);
        try { _displayPixels = _displayBitmap.GetPixelBytes(); }
        catch (Exception ex)
        {
            _displayPixels = null;
            Serilog.Log.Warning(ex, "Region color sampling unavailable");
        }

        // 重置交互状态（为本次截图清场）
        SelectionRect = default;
        _state = RegionCaptureState.Selecting;
        _defaultAction = defaultAction;
        var accent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(255, 221, 243, 105));
        var clear = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Transparent);
        ToolbarSaveButton.Background = defaultAction == RegionCaptureAction.Save ? accent : clear;
        ToolbarCopyButton.Background = defaultAction == RegionCaptureAction.Copy ? accent : clear;
        ToolbarOcrButton.Background = defaultAction == RegionCaptureAction.Ocr ? accent : clear;
        _positionOnClick = default;
        _dragMode = DragMode.None;
        _activeHandle = ResizeHandle.None;
        _pressedOnHover = false;
        _captureCursorActive = false;
        _captureCursorApplied = false;
        _captureCursorOrigin = null;
        if (User32.GetCursorPos(out var initCursor))
        {
            _currentMousePos = new Point(
                (initCursor.x - _vx) / _scale,
                (initCursor.y - _vy) / _scale
            );
        }
        _selectionFromDrag = false;
        _windowRects = new List<Rect>();
        ResetAnnotations();
        _hoverRect = default;
        _hasHover = false;
        _lockedW = 0;
        _lockedH = 0;
        _sizeLocked = false; // 首帧重新锁尺寸 + 触发 DetectWindows
        _cleanedUp = false;
        Completion = new TaskCompletionSource<RegionCaptureResult>();
        SelectionToolbar.Visibility = Visibility.Collapsed;
        SelectionMetrics.Visibility = Visibility.Collapsed;
        _prevForeground = (nint)User32.GetForegroundWindow();

        // 首次 Show 也先放到屏外，避免 DWM 在首帧 Present 前合成空白/旧帧。
        MoveOffscreen();
        Show(); // 首次显示；后续会话窗口一直可见（在屏外），no-op
        // 会话激活放在 Show 之后：万一 Show 在已销毁窗口上抛（用户真关过窗、service 未能重建的兜底路径），
        // _isClosed 仍为 true、timer 未启动，Redraw 守卫生效，不会拿已释放的帧再画导致 FATAL
        _isClosed = false;
        Redraw(); // 屏外先把新冻结帧 Present 上屏（窗口可见，合成照常提交）
        LogOverlayPhase("Region overlay initial Present done", generation);
        ScheduleMoveIn(generation);
        }
        catch
        {
            StopMoveInTimer(); _pendingMoveIn = false; _isClosed = true;
            _state = RegionCaptureState.Closed;
            try { ReleaseSwapChain(); } catch { }
            if (_ownsDisplayBitmap) { try { _displayBitmap?.Dispose(); } catch { } }
            _displayBitmap = null!; _canvasOriginal = null!; _displayPixels = null;
            _ownsDisplayBitmap = false;
            throw;
        }
    }

    private void ScheduleMoveIn(int generation)
    {
        _pendingMoveIn = true;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(24);
        timer.IsRepeating = false;
        _moveInTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!ReferenceEquals(_moveInTimer, timer)
                || generation != _captureGeneration || _isClosed || !_pendingMoveIn)
                return;
            _moveInTimer = null;
            _pendingMoveIn = false;
            MoveOnscreen(generation);
        };
        LogOverlayPhase("Region overlay move-in scheduled", generation);
        timer.Start();
    }

    private void StopMoveInTimer()
    {
        _moveInTimer?.Stop();
        _moveInTimer = null;
    }

    private void LogOverlayPhase(string phase, int generation)
    {
        bool hasRect = User32.GetWindowRect(WindowHandle, out RECT rect);
        Serilog.Log.Information(
            "{Phase}: generation={Generation}, closed={Closed}, pendingMoveIn={PendingMoveIn}, swapChain={HasSwapChain}, hwnd={Hwnd}, visible={Visible}, rect={X},{Y},{Width}x{Height}, rectValid={RectValid}",
            phase, generation, _isClosed, _pendingMoveIn, _swapChain is not null,
            (nint)WindowHandle, User32.IsWindowVisible(WindowHandle),
            hasRect ? rect.left : 0, hasRect ? rect.top : 0,
            hasRect ? rect.Width : 0, hasRect ? rect.Height : 0, hasRect
        );
    }

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    private float GetCaptureScale()
    {
        var anchor = new POINT { x = _vx + 1, y = _vy + 1 };
        var monitor = User32.MonitorFromPoint(anchor, User32.MonitorFlags.MONITOR_DEFAULTTONEAREST);
        return GetDpiForMonitor(monitor.DangerousGetHandle(), 0, out uint dpi, out _) == 0
            ? dpi / 96f : User32.GetDpiForWindow(WindowHandle) / 96f;
    }

    private static CanvasBitmap CreateDisplayBitmap(
        CanvasBitmap source,
        int w,
        int h,
        float sdrWhiteLevel
    )
    {
        if (
            source.Format
            is DirectXPixelFormat.R8G8B8A8UIntNormalized
                or DirectXPixelFormat.B8G8R8A8UIntNormalized
        )
        {
            return source;
        }

        var device = CanvasDevice.GetSharedDevice();
        var sdr = new CanvasRenderTarget(
            device,
            w,
            h,
            96,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            CanvasAlphaMode.Premultiplied
        );
        using (var ds = sdr.CreateDrawingSession())
        {
            var wle = new WhiteLevelAdjustmentEffect
            {
                Source = source,
                InputWhiteLevel = 80,
                OutputWhiteLevel = sdrWhiteLevel,
                BufferPrecision = CanvasBufferPrecision.Precision16Float,
            };
            var gamma = new SrgbGammaEffect
            {
                Source = wle,
                GammaMode = SrgbGammaMode.OETF,
                BufferPrecision = CanvasBufferPrecision.Precision16Float,
            };
            ds.DrawImage(gamma);
        }
        return sdr;
    }

    // 直接 P/Invoke DwmGetWindowAttribute，避免 Vanara 泛型重载在 DWMWA_CLOAKED 上 marshal 不可靠
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetCloaked(
        IntPtr hwnd,
        int attr,
        out int pvAttribute,
        int cbAttribute
    );

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetExtendedFrameBounds(
        IntPtr hwnd,
        int attr,
        ref RECT pvAttribute,
        int cbAttribute
    );

    // 移植自 WindowsRectangleList：跳过 cloaked / TOOLWINDOW&NOACTIVATE 等垃圾窗口，
    // DWM 扩展边界去阴影，额外加入 client rect（可吸到内容区），最后去重。
    private void DetectWindows()
    {
        var raw = new List<(Rect rect, bool isWindow)>();

        User32.EnumWindows(
            (hWnd, _) =>
            {
                try
                {
                    if (!User32.IsWindowVisible(hWnd))
                        return true;
                    if (User32.IsIconic(hWnd))
                        return true;
                    if (hWnd == WindowHandle)
                        return true;

                    // cloaked（隐藏的 UWP / 最小化到任务栏 / 其它虚拟桌面等，真正不可见）
                    try
                    {
                        if (
                            DwmGetCloaked(
                                hWnd.DangerousGetHandle(),
                                14,
                                out int cloaked,
                                sizeof(int)
                            ) == 0
                            && cloaked != 0
                        )
                            return true;
                    }
                    catch { }

                    // 跳过 non-activatable tool windows：任务栏/托盘/平铺管理器 overlay/各种小工具
                    var exStyle = (User32.WindowStylesEx)
                        User32.GetWindowLong(hWnd, User32.WindowLongFlags.GWL_EXSTYLE);
                    const User32.WindowStylesEx junk =
                        User32.WindowStylesEx.WS_EX_TOOLWINDOW
                        | User32.WindowStylesEx.WS_EX_NOACTIVATE;
                    if ((exStyle & junk) == junk)
                        return true;

                    // 窗口矩形：DWM 扩展边界（去阴影），失败回退 GetWindowRect
                    RECT wr = default;
                    bool hasWr = false;
                    try
                    {
                        if (
                            DwmGetExtendedFrameBounds(
                                hWnd.DangerousGetHandle(),
                                9,
                                ref wr,
                                Marshal.SizeOf<RECT>()
                            ) == 0
                        )
                            hasWr = wr.Width > 0 && wr.Height > 0;
                    }
                    catch { }
                    if (!hasWr)
                    {
                        if (!User32.GetWindowRect(hWnd, out wr))
                            return true;
                    }
                    if (wr.Width <= 5 || wr.Height <= 5)
                        return true;

                    var winRect = new Rect(
                        (wr.left - _vx) / _scale,
                        (wr.top - _vy) / _scale,
                        wr.Width / _scale,
                        wr.Height / _scale
                    );

                    // 客户区（若与窗口矩形明显不同）：放在窗口矩形之前入列，使悬停优先命中内容区
                    Rect? clientRect = null;
                    try
                    {
                        if (
                            User32.GetClientRect(hWnd, out RECT cr)
                            && cr.Width > 5
                            && cr.Height > 5
                        )
                        {
                            POINT tl = new POINT { x = 0, y = 0 };
                            if (User32.ClientToScreen(hWnd, ref tl))
                            {
                                var c = new Rect(
                                    (tl.x + cr.left - _vx) / _scale,
                                    (tl.y + cr.top - _vy) / _scale,
                                    cr.Width / _scale,
                                    cr.Height / _scale
                                );
                                if (
                                    Math.Abs(c.X - winRect.X) > 2
                                    || Math.Abs(c.Y - winRect.Y) > 2
                                    || Math.Abs(c.Width - winRect.Width) > 2
                                    || Math.Abs(c.Height - winRect.Height) > 2
                                )
                                {
                                    clientRect = c;
                                }
                            }
                        }
                    }
                    catch { }

                    if (clientRect.HasValue)
                        raw.Add((clientRect.Value, false));
                    raw.Add((winRect, true));
                }
                catch { }
                return true;
            },
            IntPtr.Zero
        );

        // 去重：仅对非顶级窗口（client rect）做包含剔除，顶级窗口始终保留
        var result = new List<Rect>();
        foreach (var (rect, isWindow) in raw)
        {
            bool keep = true;
            if (!isWindow)
            {
                foreach (var r in result)
                {
                    // Windows.Foundation.Rect 没有 Contains(Rect)，手动判断 outer 是否包含 inner
                    if (
                        r.X <= rect.X
                        && r.Y <= rect.Y
                        && r.X + r.Width >= rect.X + rect.Width
                        && r.Y + r.Height >= rect.Y + rect.Height
                    )
                    {
                        keep = false;
                        break;
                    }
                }
            }
            if (keep)
                result.Add(rect);
        }
        _windowRects = result;

        // 窗口列表就绪后，立即对初始光标位置做悬停命中——不必等第一次 PointerMoved
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isClosed)
            {
                if (_state == RegionCaptureState.Selecting)
                {
                    UpdateHover(_currentMousePos);
                    RequestRedraw();
                }
            }
        });
    }

    private void RequestRedraw()
    {
        if (_isClosed || _pendingMoveIn || _redrawQueued)
            return;
        int generation = _captureGeneration;
        _redrawQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                if (generation != _captureGeneration)
                    return;
                _redrawQueued = false;
                if (!_pendingMoveIn)
                    Redraw();
            }))
            _redrawQueued = false;
    }

    private void Redraw()
    {
        if (_isClosed || _swapChain is null || _displayBitmap is null)
            return;

        bool trace = _traceNextRedraw;
        _traceNextRedraw = false;
        if (trace)
            Serilog.Log.Information("Region overlay click redraw begin: generation={Generation}, state={State}, drag={Drag}",
                _captureGeneration, _state, _dragMode);

        // 首次会话真实移动前由原生光标兜底；移动后立即交回 ProtectedCursor。
        if (_captureCursorActive)
            ApplyCaptureCursor();

        // 首帧锁定画布尺寸（_scale 构造时已按覆盖层窗口 DPI 设定）
        if (!_sizeLocked)
        {
            _lockedW = (float)_swapChain.Size.Width;
            _lockedH = (float)_swapChain.Size.Height;
            _sizeLocked = true;
            _ = Task.Run(DetectWindows);
        }

        // 不依赖 PointerMoved 的缓存位置：窗口复用/激活时的指针事件可能仍带旧坐标。
        // 每帧在绘制前读取真实屏幕位置；屏外预绘时也用目标虚拟屏幕原点转换，
        // 不能用当前屏外 HWND 的 ScreenToClient，否则会把 -32000 偏移带进预览。
        if (User32.GetCursorPos(out var cursorPosition))
        {
            _currentMousePos = new Point(
                (cursorPosition.x - _vx) / _scale,
                (cursorPosition.y - _vy) / _scale
            );
            if (_state == RegionCaptureState.Selecting && _dragMode == DragMode.None)
                UpdateHover(_currentMousePos);
        }

        using (var ds = _swapChain.CreateDrawingSession(Colors.Transparent))
        {
            float physW = (float)_displayBitmap.SizeInPixels.Width;
            float physH = (float)_displayBitmap.SizeInPixels.Height;

            // 1. 画冻结帧（铺满，尺寸锁定，不动）
            ds.DrawImage(
                _displayBitmap,
                new Rect(0, 0, _lockedW, _lockedH),
                new Rect(0, 0, physW, physH),
                1f,
                CanvasImageInterpolation.Linear
            );

            // Darken the surroundings; repaint the selected area from the frozen frame below.
            ds.FillRectangle(new Rect(0, 0, _lockedW, _lockedH), Color.FromArgb(145, 0, 0, 0));

            // 2. 选区或悬停边框（纯绘图，不碰冻结帧）
            Rect rect = default;
            bool hasRect = false;

            if (
                (_state == RegionCaptureState.Selected || _dragMode == DragMode.Creating)
                && SelectionRect.Width > MinimumRectangleSize
                && SelectionRect.Height > MinimumRectangleSize
            )
            {
                rect = SelectionRect;
                hasRect = true;
            }
            else if (_state == RegionCaptureState.Selecting && _hasHover && _hoverRect.Width > 2 && _hoverRect.Height > 2)
            {
                rect = _hoverRect;
                hasRect = true;
            }

            if (hasRect)
            {
                // 选区/hover 位置挖洞：重画干净原图抵消压黑（backgroundHighlight）
                // hover rect 可能含标题栏/阴影（位置负，超画布），只挖与画布的交集，避免 sourceRect 越出 bitmap 边界被拉伸
                double cx = Math.Max(rect.X, 0);
                double cy = Math.Max(rect.Y, 0);
                double cw = Math.Max(0, Math.Min(rect.X + rect.Width, _lockedW) - cx);
                double ch = Math.Max(0, Math.Min(rect.Y + rect.Height, _lockedH) - cy);
                var clip = new Rect(cx, cy, cw, ch);
                if (clip.Width > 0 && clip.Height > 0)
                {
                    ds.DrawImage(
                        _displayBitmap,
                        clip,
                        new Rect(
                            clip.X / _lockedW * physW,
                            clip.Y / _lockedH * physH,
                            clip.Width / _lockedW * physW,
                            clip.Height / _lockedH * physH
                        ),
                        1f,
                        CanvasImageInterpolation.Linear
                    );
                }

                if (_state == RegionCaptureState.Selected)
                    DrawAnnotations(ds);
                ds.DrawRectangle(rect, Color.FromArgb(180, 2, 10, 24), 4);
                ds.DrawRectangle(rect, Color.FromArgb(255, 157, 183, 70), 2);
                if (_state == RegionCaptureState.Selected)
                    DrawResizeHandles(ds, rect);
            }
            UpdateSelectionMetrics(rect, hasRect);

            // 3. 放大镜与坐标条整体钳制到光标所在显示器，坐标条随上下翻转保持在远离鼠标的一侧
            float mx = (float)_currentMousePos.X,
                my = (float)_currentMousePos.Y;
            GetActiveMonitorDip(mx, my, out float ml, out float mt, out float mr, out float mb);
            bool hasColor = TrySampleColor(mx, my, out _sampledColor);
            bool hex = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            DrawMagnifier(
                ds,
                mx,
                my,
                ml,
                mt,
                mr,
                mb,
                $"({(int)(mx * _scale) + _vx}, {(int)(my * _scale) + _vy})\n"
                    + (hasColor ? (hex ? $"#{_sampledColor.R:X2}{_sampledColor.G:X2}{_sampledColor.B:X2}"
                        : $"RGB: {_sampledColor.R},{_sampledColor.G},{_sampledColor.B}") : "RGB: —")
                    + "\nC: 复制颜色值"
            );
        }
        if (trace)
            Serilog.Log.Information("Region overlay click drawing done: generation={Generation}", _captureGeneration);
        _swapChain.Present();
        if (trace)
            Serilog.Log.Information("Region overlay click Present done: generation={Generation}", _captureGeneration);

    }

    // 光标所在显示器在 canvas DIP 坐标下的边界（放大镜、坐标框共用，不跨屏）
    private void GetActiveMonitorDip(
        float mx,
        float my,
        out float l,
        out float t,
        out float r,
        out float b
    )
    {
        l = 0;
        t = 0;
        r = _lockedW;
        b = _lockedH;
        try
        {
            POINT phys = new POINT { x = (int)(mx * _scale + _vx), y = (int)(my * _scale + _vy) };
            var mon = User32.MonitorFromPoint(phys, User32.MonitorFlags.MONITOR_DEFAULTTONEAREST);
            var mi = new User32.MONITORINFOEX
            {
                cbSize = (uint)Marshal.SizeOf<User32.MONITORINFOEX>(),
            };
            if (User32.GetMonitorInfo(mon, ref mi))
            {
                l = (mi.rcMonitor.left - _vx) / _scale;
                t = (mi.rcMonitor.top - _vy) / _scale;
                r = (mi.rcMonitor.right - _vx) / _scale;
                b = (mi.rcMonitor.bottom - _vy) / _scale;
            }
        }
        catch { }
    }

    private void DrawMagnifier(
        CanvasDrawingSession ds,
        float mx,
        float my,
        float monLeft,
        float monTop,
        float monRight,
        float monBottom,
        string coordText
    )
    {
        if (_displayBitmap is null)
            return;
        int halfCount = MagnifierPixelCount / 2;
        int magSize = MagnifierPixelCount * MagnifierPixelSize;
        const int offset = 10;
        // 先按放大镜 + 坐标条的整体高度定位，再按上下方向排列两者
        int totalH = magSize + MagnifierCoordGap + 58;

        float destX = mx + offset;
        float groupY = my + offset;
        bool showAbove = groupY + totalH > monBottom;
        if (destX + magSize > monRight)
            destX = mx - offset - magSize;
        if (showAbove)
            groupY = my - offset - totalH;
        if (destX < monLeft)
            destX = monLeft;
        if (groupY < monTop)
            groupY = monTop;

        if (_state == RegionCaptureState.Selected && SelectionToolbar.Visibility == Visibility.Visible)
        {
            double barX = Microsoft.UI.Xaml.Controls.Canvas.GetLeft(SelectionToolbar);
            double barY = Microsoft.UI.Xaml.Controls.Canvas.GetTop(SelectionToolbar);
            if (!double.IsNaN(barX) && !double.IsNaN(barY)
                && destX < barX + SelectionToolbar.Width && destX + magSize > barX
                && groupY < barY + SelectionToolbar.Height && groupY + totalH > barY)
            {
                groupY = (float)Math.Max(monTop, barY - totalH - 8);
                showAbove = true;
            }
        }

        // 坐标条始终放在远离鼠标的一侧：向下展开时在像素图下方，向上翻转时在像素图上方
        float destY = groupY + (showAbove ? 58 + MagnifierCoordGap : 0);
        float coordY = showAbove ? groupY : destY + magSize + MagnifierCoordGap;

        // 源矩形整数对齐，让 NearestNeighbor 真正锐利（不再糊）
        int srcX = (int)Math.Floor(mx * _scale) - halfCount;
        int srcY = (int)Math.Floor(my * _scale) - halfCount;
        // 钳制到 bitmap bounds 内：鼠标在屏幕边缘时 srcX/srcY 可能负或越界，
        // DrawImage sourceRect 越出 bitmap → E_BOUNDS → stowed exception → fail-fast
        srcX = Math.Clamp(srcX, 0, (int)_displayBitmap.SizeInPixels.Width - MagnifierPixelCount);
        srcY = Math.Clamp(srcY, 0, (int)_displayBitmap.SizeInPixels.Height - MagnifierPixelCount);

        ds.DrawImage(
            _displayBitmap,
            new Rect(destX, destY, magSize, magSize),
            new Rect(srcX, srcY, MagnifierPixelCount, MagnifierPixelCount),
            1f,
            CanvasImageInterpolation.NearestNeighbor
        );

        // 像素网格：让放大的每个像素清晰可辨
        var grid = Color.FromArgb(45, 0, 0, 0);
        for (int i = 1; i < MagnifierPixelCount; i++)
        {
            float gx = destX + i * MagnifierPixelSize;
            float gy = destY + i * MagnifierPixelSize;
            ds.DrawLine(new Vector2(gx, destY), new Vector2(gx, destY + magSize), grid, 1);
            ds.DrawLine(new Vector2(destX, gy), new Vector2(destX + magSize, gy), grid, 1);
        }

        ds.DrawRectangle(new Rect(destX - 1, destY - 1, magSize + 2, magSize + 2), Colors.White, 1);
        ds.DrawRectangle(new Rect(destX, destY, magSize, magSize), Colors.Black, 1);

        float cx = destX + magSize / 2f;
        float cy = destY + magSize / 2f;
        float ps = MagnifierPixelSize / 2f;
        var cc = Color.FromArgb(125, 173, 216, 230);
        ds.FillRectangle(new Rect(destX, cy - ps / 2, cx - ps / 2 - destX, ps), cc);
        ds.FillRectangle(new Rect(cx + ps / 2, cy - ps / 2, destX + magSize - cx - ps / 2, ps), cc);
        ds.FillRectangle(new Rect(cx - ps / 2, destY, ps, cy - ps / 2 - destY), cc);
        ds.FillRectangle(new Rect(cx - ps / 2, cy + ps / 2, ps, destY + magSize - cy - ps / 2), cc);

        // 坐标条随展开方向换边，文字居中，同款黑底样式
        DrawCoordStrip(ds, coordText, destX, coordY, magSize);
    }

    private bool TrySampleColor(float x, float y, out Color color)
    {
        color = default;
        if (_displayPixels is null || _displayBitmap is null)
            return false;
        int width = (int)_displayBitmap.SizeInPixels.Width;
        int height = (int)_displayBitmap.SizeInPixels.Height;
        int px = Math.Clamp((int)Math.Floor(x * _scale), 0, width - 1);
        int py = Math.Clamp((int)Math.Floor(y * _scale), 0, height - 1);
        long offset = ((long)py * width + px) * 4;
        if (offset + 3 >= _displayPixels.Length)
            return false;
        int index = (int)offset;
        bool bgra = _displayBitmap.Format == DirectXPixelFormat.B8G8R8A8UIntNormalized;
        color = Color.FromArgb(255,
            _displayPixels[index + (bgra ? 2 : 0)],
            _displayPixels[index + 1],
            _displayPixels[index + (bgra ? 0 : 2)]);
        return true;
    }

    /// <summary>坐标条：宽与放大镜对齐，文本水平居中，黑底白字圆角。</summary>
    private void DrawCoordStrip(
        CanvasDrawingSession ds,
        string text,
        float x,
        float y,
        float width
    )
    {
        try
        {
            using var fmt = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat
            {
                FontSize = 13,
                FontFamily = "Consolas",
                HorizontalAlignment = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Center,
            };
            using var layout = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(
                ds,
                text,
                fmt,
                width,
                58
            );
            float textY = y + (58 - (float)layout.LayoutBounds.Height) / 2;
            var stripRect = new Rect(x, y, width, 58);
            ds.FillRoundedRectangle(stripRect, 8, 8, Color.FromArgb(248, 249, 250, 243));
            ds.DrawRoundedRectangle(stripRect, 8, 8, Color.FromArgb(255, 215, 221, 206), 1);
            if (_displayPixels is not null)
            {
                ds.FillRectangle(new Rect(x + 3, y + 22, 11, 11), _sampledColor);
                ds.DrawRectangle(new Rect(x + 3, y + 22, 11, 11), Colors.White, 1);
            }
            ds.DrawTextLayout(layout, new Vector2(x, textY), Color.FromArgb(255, 36, 43, 36));
        }
        catch { }
    }

    private void UpdateSelectionMetrics(Rect rect, bool visible)
    {
        SelectionMetrics.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
            return;
        var physical = ComputePhysicalRect(rect, _dragMode == DragMode.Creating);
        SelectionMetricsText.Text = $"{(int)physical.X},{(int)physical.Y}  {(int)physical.Width} × {(int)physical.Height} px";
        double labelWidth = Math.Max(SelectionMetrics.ActualWidth, 230);
        double x = Math.Clamp(rect.Left, 0, Math.Max(0, _lockedW - labelWidth));
        double y = rect.Top >= 40 ? rect.Top - 38 : Math.Min(_lockedH - 32, rect.Top + 5);
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(SelectionMetrics, x);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(SelectionMetrics, Math.Max(0, y));
    }

    // ===== 鼠标事件 =====

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_state is RegionCaptureState.Completing or RegionCaptureState.Closed)
            return;
        var pt = e.GetCurrentPoint(Canvas);
        _currentMousePos = pt.Position;
        if (pt.Properties.IsLeftButtonPressed)
        {
            if (StartAnnotation(pt.Position))
            {
                Canvas.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }
            Serilog.Log.Information("Region overlay pointer press: generation={Generation}, state={State}, pos={X},{Y}",
                _captureGeneration, _state, pt.Position.X, pt.Position.Y);
            _traceNextRedraw = true;
            if (_state == RegionCaptureState.Selected)
            {
                _activeHandle = HitTestHandle(pt.Position);
                if (_activeHandle != ResizeHandle.None || SelectionRect.Contains(pt.Position))
                {
                    _dragMode = _activeHandle == ResizeHandle.None ? DragMode.Moving : DragMode.Resizing;
                    _dragStartPhysicalRect = GetPhysicalSourceRect();
                    _dragStartPhysicalPoint = ToPhysicalPoint(pt.Position);
                }
                else
                {
                    ReturnToSelectingState();
                    BeginSelection(pt.Position);
                }
            }
            else
                BeginSelection(pt.Position);
            Canvas.CapturePointer(e.Pointer);
            RequestRedraw();
            e.Handled = true;
        }
    }

    private void BeginSelection(Point pos)
    {
        _positionOnClick = pos;
        _dragMode = DragMode.Creating;
        _selectionFromDrag = true;
        _pressedOnHover = _hasHover;
        SelectionRect = new Rect(pos.X, pos.Y, 0, 0);
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(Canvas).Position;
        if (_captureCursorActive && User32.GetCursorPos(out var cursorPosition))
        {
            if (_captureCursorOrigin is null)
            {
                _captureCursorOrigin = cursorPosition;
            }
            else if (
                (cursorPosition.x != _captureCursorOrigin.Value.x)
                || (cursorPosition.y != _captureCursorOrigin.Value.y)
            )
            {
                _captureCursorActive = false;
                _firstCaptureCursorHandoffCompleted = true;
            }
        }
        _currentMousePos = pos;

        if (_draftAnnotation is not null)
        {
            UpdateAnnotation(pos);
            e.Handled = true;
            return;
        }

        if (_dragMode == DragMode.Creating)
        {
            double px = Math.Clamp(pos.X, 0, _lockedW);
            double py = Math.Clamp(pos.Y, 0, _lockedH);
            double x = Math.Min(_positionOnClick.X, px);
            double y = Math.Min(_positionOnClick.Y, py);
            double w = Math.Abs(px - _positionOnClick.X);
            double h = Math.Abs(py - _positionOnClick.Y);
            SelectionRect = new Rect(x, y, w, h);
        }
        else if (_dragMode == DragMode.Moving)
            UpdateSelectionMove(pos);
        else if (_dragMode == DragMode.Resizing)
            UpdateSelectionResize(pos);
        else if (_state == RegionCaptureState.Selecting)
        {
            UpdateHover(pos);
        }
        if (_state == RegionCaptureState.Selected)
            UpdateToolbarPlacement();
        RequestRedraw();
        e.Handled = true;
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(Canvas);
        Serilog.Log.Information("Region overlay pointer release: generation={Generation}, state={State}, drag={Drag}, pos={X},{Y}",
            _captureGeneration, _state, _dragMode, pt.Position.X, pt.Position.Y);
        _traceNextRedraw = true;

        if (_draftAnnotation is not null)
        {
            FinishAnnotation(pt.Position);
            Canvas.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }

        if (pt.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonReleased)
        {
            HandleRightClick();
            e.Handled = true;
            return;
        }

        if (_dragMode != DragMode.None)
        {
            var completedDrag = _dragMode;
            _dragMode = DragMode.None;
            Canvas.ReleasePointerCapture(e.Pointer);
            if (completedDrag == DragMode.Creating)
            {
                if (SelectionRect.Width > MinimumRectangleSize && SelectionRect.Height > MinimumRectangleSize)
                    EnterSelectedState();
                else if (_pressedOnHover && _hoverRect.Width > 2 && _hoverRect.Height > 2)
                {
                    SelectionRect = _hoverRect;
                    _selectionFromDrag = false;
                    EnterSelectedState();
                }
                else
                    SelectionRect = default;
            }
            else
                UpdateToolbarPlacement();
            RequestRedraw();
            e.Handled = true;
        }
    }

    private Point ToPhysicalPoint(Point dip) => new(
        Math.Round(dip.X * _canvasOriginal.SizeInPixels.Width / _lockedW),
        Math.Round(dip.Y * _canvasOriginal.SizeInPixels.Height / _lockedH)
    );

    private void SetPhysicalSelection(Rect physical)
    {
        Rect oldSelection = SelectionRect;
        double rx = _canvasOriginal.SizeInPixels.Width / _lockedW;
        double ry = _canvasOriginal.SizeInPixels.Height / _lockedH;
        SelectionRect = new Rect(physical.X / rx, physical.Y / ry,
            physical.Width / rx, physical.Height / ry);
        if (Math.Abs(oldSelection.Width - SelectionRect.Width) < 0.01
            && Math.Abs(oldSelection.Height - SelectionRect.Height) < 0.01)
            TranslateAnnotations(SelectionRect.X - oldSelection.X, SelectionRect.Y - oldSelection.Y);
        _selectionFromDrag = false;
    }

    private void UpdateSelectionMove(Point pos)
    {
        Point current = ToPhysicalPoint(pos);
        int width = (int)_canvasOriginal.SizeInPixels.Width;
        int height = (int)_canvasOriginal.SizeInPixels.Height;
        Rect start = _dragStartPhysicalRect;
        int x = Math.Clamp((int)(start.X + current.X - _dragStartPhysicalPoint.X), 0,
            width - (int)start.Width);
        int y = Math.Clamp((int)(start.Y + current.Y - _dragStartPhysicalPoint.Y), 0,
            height - (int)start.Height);
        SetPhysicalSelection(new Rect(x, y, start.Width, start.Height));
    }

    private void UpdateSelectionResize(Point pos)
    {
        Point current = ToPhysicalPoint(pos);
        int dx = (int)(current.X - _dragStartPhysicalPoint.X);
        int dy = (int)(current.Y - _dragStartPhysicalPoint.Y);
        Rect r = _dragStartPhysicalRect;
        int left = (int)r.Left, top = (int)r.Top;
        int right = (int)r.Right, bottom = (int)r.Bottom;
        const int min = MinimumRectangleSize;
        int maxX = (int)_canvasOriginal.SizeInPixels.Width;
        int maxY = (int)_canvasOriginal.SizeInPixels.Height;
        if (_activeHandle is ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft)
            left = Math.Clamp(left + dx, 0, right - min);
        if (_activeHandle is ResizeHandle.TopRight or ResizeHandle.Right or ResizeHandle.BottomRight)
            right = Math.Clamp(right + dx, left + min, maxX);
        if (_activeHandle is ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight)
            top = Math.Clamp(top + dy, 0, bottom - min);
        if (_activeHandle is ResizeHandle.BottomLeft or ResizeHandle.Bottom or ResizeHandle.BottomRight)
            bottom = Math.Clamp(bottom + dy, top + min, maxY);
        SetPhysicalSelection(new Rect(left, top, right - left, bottom - top));
    }

    private static Point[] HandleCenters(Rect r) =>
    [
        new(r.Left, r.Top), new(r.Left + r.Width / 2, r.Top), new(r.Right, r.Top),
        new(r.Left, r.Top + r.Height / 2), new(r.Right, r.Top + r.Height / 2),
        new(r.Left, r.Bottom), new(r.Left + r.Width / 2, r.Bottom), new(r.Right, r.Bottom),
    ];

    private ResizeHandle HitTestHandle(Point pos)
    {
        Point[] centers = HandleCenters(SelectionRect);
        for (int i = 0; i < centers.Length; i++)
            if (Math.Abs(pos.X - centers[i].X) <= 10 && Math.Abs(pos.Y - centers[i].Y) <= 10)
                return (ResizeHandle)(i + 1);
        return ResizeHandle.None;
    }

    private static void DrawResizeHandles(CanvasDrawingSession ds, Rect rect)
    {
        foreach (Point c in HandleCenters(rect))
        {
            ds.FillCircle(new Vector2((float)c.X, (float)c.Y), 7, Color.FromArgb(170, 0, 0, 0));
            ds.FillCircle(new Vector2((float)c.X, (float)c.Y), 6, Colors.White);
            ds.FillCircle(new Vector2((float)c.X, (float)c.Y), 5, Color.FromArgb(255, 157, 183, 70));
        }
    }

    private void EnterSelectedState()
    {
        Rect physical = GetPhysicalSourceRect();
        Serilog.Log.Information("Region selection committed: dip={DipRect}, physical={PhysicalRect}, fromDrag={FromDrag}",
            SelectionRect, physical, _selectionFromDrag);
        if (physical.Width < MinimumRectangleSize || physical.Height < MinimumRectangleSize)
            return;
        SetPhysicalSelection(physical);
        _state = RegionCaptureState.Selected;
        _hasHover = false;
        SelectionToolbar.Visibility = Visibility.Visible;
        UpdateToolbarPlacement();
        RequestRedraw();
    }

    private void ReturnToSelectingState()
    {
        if (_state != RegionCaptureState.Selected)
            return;
        ResetAnnotations();
        _state = RegionCaptureState.Selecting;
        _dragMode = DragMode.None;
        SelectionRect = default;
        SelectionToolbar.Visibility = Visibility.Collapsed;
        UpdateHover(_currentMousePos);
        RequestRedraw();
    }

    private void UpdateToolbarPlacement()
    {
        if (_state != RegionCaptureState.Selected)
            return;
        double barWidth = SelectionToolbar.Width, barHeight = SelectionToolbar.Height;
        const double gap = 8;
        GetActiveMonitorDip(
            (float)(SelectionRect.Left + SelectionRect.Width / 2),
            (float)(SelectionRect.Top + SelectionRect.Height / 2),
            out float ml, out float mt, out float mr, out float mb);
        // 跨屏选区以覆盖面积最大的显示器放工具栏；FindAll 的 WinRT vector
        // 使用索引访问，避免此环境中 foreach 枚举器的 CsWinRT 接口异常。
        try
        {
            Rect physical = GetPhysicalSourceRect();
            double bestArea = 0;
            var displays = DisplayArea.FindAll();
            for (int i = 0; i < displays.Count; i++)
            {
                var bounds = displays[i].OuterBounds;
                double left = Math.Max(physical.Left + _vx, bounds.X);
                double top = Math.Max(physical.Top + _vy, bounds.Y);
                double right = Math.Min(physical.Right + _vx, bounds.X + bounds.Width);
                double bottom = Math.Min(physical.Bottom + _vy, bounds.Y + bounds.Height);
                double area = Math.Max(0, right - left) * Math.Max(0, bottom - top);
                if (area <= bestArea)
                    continue;
                bestArea = area;
                ml = (bounds.X - _vx) / _scale;
                mt = (bounds.Y - _vy) / _scale;
                mr = (bounds.X + bounds.Width - _vx) / _scale;
                mb = (bounds.Y + bounds.Height - _vy) / _scale;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to locate primary monitor for region toolbar");
        }
        double x = Math.Clamp(SelectionRect.Right - barWidth,
            ml, Math.Max(ml, mr - barWidth));
        double y = SelectionRect.Bottom + gap + barHeight + 16 <= mb
            ? SelectionRect.Bottom + gap : SelectionRect.Top - gap - barHeight;
        y = Math.Clamp(y, mt, Math.Max(mt, mb - barHeight));
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(SelectionToolbar, x);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(SelectionToolbar, y);
    }

    private void UpdateHover(Point pos)
    {
        // _windowRects 为 EnumWindows 的 Z 序（顶层在前），首个命中即最上层窗口。
        // 不能选"最小矩形"，否则会高亮被遮挡的后台小窗口（同 FindSelectedWindow 语义）。
        _hasHover = false;
        foreach (var rect in _windowRects)
        {
            if (rect.Contains(pos))
            {
                _hoverRect = rect;
                _hasHover = true;
                return;
            }
        }
    }

    private void CompleteCapture(RegionCaptureAction action)
    {
        if (_state != RegionCaptureState.Selected)
            return;
        _state = RegionCaptureState.Completing;
        Rect physical = GetPhysicalSourceRect();
        try
        {
            CommitAnnotationText();
            // 完成动作前裁出 SDR 选区；保存时 HDR crop 由 service 从原始 composite 裁出。
            CanvasRenderTarget sdrCrop = CropDisplayToBgra();
            CanvasRenderTarget? annotationLayer = null;
            try
            {
                if (_annotations.Count > 0)
                    annotationLayer = CreateAnnotationLayer(physical);
                CloseWindow(new RegionCaptureResult(action, SelectionRect, physical, sdrCrop, annotationLayer));
            }
            catch
            {
                sdrCrop.Dispose();
                annotationLayer?.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            CloseWindow(new RegionCaptureResult(RegionCaptureAction.Cancel, default, default, null), ex);
        }
    }

    private void CancelCapture() => CloseWindow(
        new RegionCaptureResult(RegionCaptureAction.Cancel, default, default, null));

    private void CloseWindow(RegionCaptureResult result, Exception? error = null)
    {
        if (_state == RegionCaptureState.Closed)
            return;
        _state = RegionCaptureState.Closed;
        ++_captureGeneration;
        StopMoveInTimer();
        SelectionToolbar.Visibility = Visibility.Collapsed;
        AnnotationTextEditor.Visibility = Visibility.Collapsed;
        ReleaseCaptureCursor();
        _isClosed = true;
        _pendingMoveIn = false;
        // 不 Hide：移到屏外保持 IsWindowVisible，合成管线不停摆，
        // 否则下次 Show 瞬间 DWM 先合成保留的旧会话帧（启动闪上次截图的完整界面）
        MoveOffscreen();
        ReleaseSwapChain();
        // 交还焦点（屏外窗口不 Hide 仍持有键盘焦点，不还的话用户打字被吞）
        if (_prevForeground != 0 && _prevForeground != (nint)WindowHandle)
        {
            try
            {
                User32.SetForegroundWindow(new HWND(_prevForeground));
            }
            catch { }
        }
        if (_ownsDisplayBitmap)
        {
            try
            {
                _displayBitmap?.Dispose();
            }
            catch { }
        }
        // 窗口不再持有 full desktop texture；service 只接收物理裁剪坐标和选区 SDR crop。
        _displayBitmap = null;
        _displayPixels = null;
        _canvasOriginal = null!;
        _ownsDisplayBitmap = false;
        if (error is null)
            Completion?.TrySetResult(result);
        else
            Completion?.TrySetException(error);
    }

    private void MoveOffscreen()
    {
        User32.SetWindowPos(
            WindowHandle,
            IntPtr.Zero,
            -32000,
            -32000,
            0,
            0,
            User32.SetWindowPosFlags.SWP_NOSIZE
                | User32.SetWindowPosFlags.SWP_NOZORDER
                | User32.SetWindowPosFlags.SWP_NOACTIVATE
        );
    }

    private void ReleaseSwapChain()
    {
        var old = _swapChain;
        _swapChain = null;
        try { Canvas.SwapChain = null; }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Failed to detach region swap chain"); }
        try { old?.Dispose(); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Failed to dispose region swap chain"); }
    }

    private void Toolbar_Copy_Click(object sender, RoutedEventArgs e) => CompleteCapture(RegionCaptureAction.Copy);
    private void Toolbar_Save_Click(object sender, RoutedEventArgs e) => CompleteCapture(RegionCaptureAction.Save);
    private void Toolbar_Ocr_Click(object sender, RoutedEventArgs e) => CompleteCapture(RegionCaptureAction.Ocr);
    private void Toolbar_Translate_Click(object sender, RoutedEventArgs e) => CompleteCapture(RegionCaptureAction.Translate);
    private void Toolbar_Pin_Click(object sender, RoutedEventArgs e) => CompleteCapture(RegionCaptureAction.Pin);
    private void Toolbar_RecordGif_Click(object sender, RoutedEventArgs e) => CompleteCapture(RegionCaptureAction.RecordGif);
    private void Toolbar_LongCapture_Click(object sender, RoutedEventArgs e) => CompleteCapture(RegionCaptureAction.LongCapture);
    private void Toolbar_Cancel_Click(object sender, RoutedEventArgs e) => CancelCapture();
    private void Toolbar_Reselect_Click(object sender, RoutedEventArgs e) => ReturnToSelectingState();

    /// <summary>首帧已 Present 后，由当前会话的一次性 timer 移回虚拟屏幕。</summary>
    private void MoveOnscreen(int generation)
    {
        LogOverlayPhase("Region overlay MoveOnscreen begin", generation);
        int vx = User32.GetSystemMetrics((User32.SystemMetric)76);
        int vy = User32.GetSystemMetrics((User32.SystemMetric)77);
        int vw = User32.GetSystemMetrics((User32.SystemMetric)78);
        int vh = User32.GetSystemMetrics((User32.SystemMetric)79);
        User32.SetWindowPos(
            WindowHandle,
            IntPtr.Zero,
            vx,
            vy,
            vw,
            vh,
            User32.SetWindowPosFlags.SWP_NOZORDER
        );
        LogOverlayPhase("Region overlay MoveOnscreen done", generation);
        Activate();
        LogOverlayPhase("Region overlay Activate done", generation);
        // 首次完成交接前持续兜底；只有本次会话真正发生物理移动后，后续会话才永久停用。
        _captureCursorActive = !_firstCaptureCursorHandoffCompleted;
        _captureCursorOrigin = User32.GetCursorPos(out var cursorPosition)
            ? cursorPosition
            : null;
        if (_captureCursorActive)
            ApplyCaptureCursor();
    }

    private bool IsPointerOverCaptureWindow()
    {
        if (IsDestroyed || !User32.GetCursorPos(out var point))
            return false;

        // WinUI 的命中窗口通常是 InputSite 子 HWND，不能只比较顶层句柄。
        var target = User32.WindowFromPoint(point);
        return target == WindowHandle || User32.IsChild(WindowHandle, target);
    }

    private void ApplyCaptureCursor()
    {
        if (
            !_captureCursorActive
            || _isClosed
            || IsDestroyed
            || _pendingMoveIn
            || !IsPointerOverCaptureWindow()
        )
            return;

        // 只有首次会话仍未完成真实移动交接时才进入这里；交接后及后续会话由 ProtectedCursor 管理。
        // LoadCursor 是共享系统资源，不创建/销毁自定义 HCURSOR。
        var cursor = User32.LoadCursor(IntPtr.Zero, User32.IDC_CROSS);
        if (cursor.IsNull)
            return;
        var previous = User32.SetCursor(cursor);
        if (!_captureCursorApplied)
        {
            _captureCursorApplied = true;
            Serilog.Log.Debug(
                "Region cursor guard started: previous={Previous}, cross={Cross}",
                previous,
                cursor
            );
        }
    }

    private void ReleaseCaptureCursor()
    {
        _captureCursorActive = false;
        // 先停维持，再清理本会话的原生光标；不清/重设 ProtectedCursor。
        // 使用同样的鼠标命中边界，即使焦点变化也配对清理；不改其他窗口下的光标。
        if (
            _captureCursorApplied
            && !IsDestroyed
            && IsPointerOverCaptureWindow()
        )
        {
            var arrow = User32.LoadCursor(IntPtr.Zero, User32.IDC_ARROW);
            if (!arrow.IsNull)
                User32.SetCursor(arrow);
        }
        _captureCursorApplied = false;
    }

    // 从 _displayBitmap 裁出选区为 B8G8R8A8 SDR（CF_DIB 要 BGRA）
    private CanvasRenderTarget CropDisplayToBgra()
    {
        var srcRect = GetPhysicalSourceRect();
        int w = (int)srcRect.Width;
        int h = (int)srcRect.Height;
        var device = CanvasDevice.GetSharedDevice();
        var rt = new CanvasRenderTarget(
            device,
            w,
            h,
            96,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            CanvasAlphaMode.Premultiplied
        );
        try
        {
            using (var ds = rt.CreateDrawingSession())
            {
                ds.DrawImage(
                    _displayBitmap,
                    new Windows.Foundation.Rect(0, 0, w, h),
                    srcRect,
                    1f,
                    CanvasImageInterpolation.Linear
                );
                if (_annotations.Count > 0)
                {
                    ds.Transform = AnnotationTransform(srcRect);
                    DrawAnnotations(ds);
                }
            }
            return rt;
        }
        catch { rt.Dispose(); throw; }
    }

    private void RegionCaptureWindow_Closed(object sender, WindowEventArgs e)
    {
        // 用户从任务栏/系统真关了窗口（正常运行期我们只移屏外不 Close）：
        // 标记销毁让 service 下次重建，放行 pending 的 Completion 防 service 悬等
        IsDestroyed = true;
        _state = RegionCaptureState.Closed;
        Completion?.TrySetResult(new RegionCaptureResult(RegionCaptureAction.Cancel, default, default, null));
        Cleanup();
    }

    /// <summary>
    /// 释放覆盖层资源。窗口被用户真关（任务栏/系统关闭）时调用：运行期单例只移屏外不 Close，真关才真销毁；
    /// 进程退出的资源回收靠进程终止本身。
    /// </summary>
    public void Cleanup()
    {
        if (_cleanedUp)
            return;
        _cleanedUp = true;
        ReleaseCaptureCursor();
        ++_captureGeneration;
        _pendingMoveIn = false;
        _isClosed = true;
        StopMoveInTimer();
        try { ReleaseSwapChain(); }
        catch { }
        try
        {
            Canvas.RemoveFromVisualTree();
        }
        catch { }
        if (_ownsDisplayBitmap)
        {
            try
            {
                _displayBitmap?.Dispose();
            }
            catch { }
        }
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (HandleCaptureKey(e.Key))
            e.Handled = true;
    }

    private bool HandleCaptureKey(Windows.System.VirtualKey key)
    {
        if (_isClosed)
            return false;
        if (AnnotationTextEditor.Visibility == Visibility.Visible)
        {
            if (key == Windows.System.VirtualKey.Escape)
            {
                AnnotationTextEditor.Text = "";
                AnnotationTextEditor.Visibility = Visibility.Collapsed;
                return true;
            }
            if (key == Windows.System.VirtualKey.Enter)
            {
                CommitAnnotationText();
                return true;
            }
            return false;
        }
        bool control = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (control && key == Windows.System.VirtualKey.Z && _state == RegionCaptureState.Selected)
        {
            if (shift) RedoAnnotation(); else UndoAnnotation();
            return true;
        }
        if (control && key == Windows.System.VirtualKey.Y && _state == RegionCaptureState.Selected)
        {
            RedoAnnotation();
            return true;
        }
        if (key == Windows.System.VirtualKey.C)
        {
            if (control && _state == RegionCaptureState.Selected)
                CompleteCapture(RegionCaptureAction.Copy);
            else if (shift && _state == RegionCaptureState.Selected)
                CompleteCapture(RegionCaptureAction.Ocr);
            else if (TrySampleColor((float)_currentMousePos.X, (float)_currentMousePos.Y, out var color))
            {
                try
                {
                    ClipboardHelper.SetText(shift ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
                        : $"{color.R},{color.G},{color.B}");
                }
                catch (Exception ex) { Serilog.Log.Warning(ex, "Failed to copy region pixel color"); }
            }
            return true;
        }
        if (control && key == Windows.System.VirtualKey.S && _state == RegionCaptureState.Selected)
        {
            CompleteCapture(RegionCaptureAction.Save);
            return true;
        }
        if (control && key == Windows.System.VirtualKey.Q && _state == RegionCaptureState.Selected)
        {
            CompleteCapture(RegionCaptureAction.Translate);
            return true;
        }
        if (key == Windows.System.VirtualKey.Escape)
        {
            if (_state == RegionCaptureState.Selected)
                ReturnToSelectingState();
            else if (_state == RegionCaptureState.Selecting)
                CancelCapture();
            return true;
        }
        if (key == Windows.System.VirtualKey.Enter)
        {
            if (_state == RegionCaptureState.Selected)
            {
                CompleteCapture(_defaultAction);
                return true;
            }
            else if (_state == RegionCaptureState.Selecting && _hasHover && _dragMode == DragMode.None)
            {
                SelectionRect = _hoverRect;
                _selectionFromDrag = false;
                EnterSelectedState();
                return true;
            }
        }
        return _state == RegionCaptureState.Selected && TryAdjustWithArrow(key);
    }

    private bool TryAdjustWithArrow(Windows.System.VirtualKey key)
    {
        int dx = key switch
        {
            Windows.System.VirtualKey.Left => -1,
            Windows.System.VirtualKey.Right => 1,
            _ => 0,
        };
        int dy = key switch
        {
            Windows.System.VirtualKey.Up => -1,
            Windows.System.VirtualKey.Down => 1,
            _ => 0,
        };
        if (dx == 0 && dy == 0)
            return false;
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        Rect r = GetPhysicalSourceRect();
        int left = (int)r.Left, top = (int)r.Top;
        int right = (int)r.Right, bottom = (int)r.Bottom;
        int maxX = (int)_canvasOriginal.SizeInPixels.Width;
        int maxY = (int)_canvasOriginal.SizeInPixels.Height;
        if (shift)
        {
            if (dx < 0) left = Math.Min(left + 1, right - MinimumRectangleSize);
            if (dx > 0) right = Math.Max(right - 1, left + MinimumRectangleSize);
            if (dy < 0) top = Math.Min(top + 1, bottom - MinimumRectangleSize);
            if (dy > 0) bottom = Math.Max(bottom - 1, top + MinimumRectangleSize);
        }
        else if (ctrl)
        {
            if (dx < 0) left = Math.Max(0, left - 1);
            if (dx > 0) right = Math.Min(maxX, right + 1);
            if (dy < 0) top = Math.Max(0, top - 1);
            if (dy > 0) bottom = Math.Min(maxY, bottom + 1);
        }
        else
        {
            left = Math.Clamp(left + dx, 0, maxX - (int)r.Width);
            top = Math.Clamp(top + dy, 0, maxY - (int)r.Height);
            right = left + (int)r.Width;
            bottom = top + (int)r.Height;
        }
        SetPhysicalSelection(new Rect(left, top, right - left, bottom - top));
        UpdateToolbarPlacement();
        RequestRedraw();
        return true;
    }

    private void HandleRightClick()
    {
        if (_state == RegionCaptureState.Selected)
            ReturnToSelectingState();
        else if (_state == RegionCaptureState.Selecting && _dragMode != DragMode.None)
        {
            _dragMode = DragMode.None;
            SelectionRect = default;
            RequestRedraw();
        }
        else if (_state == RegionCaptureState.Selecting)
            CancelCapture();
    }

    protected override nint WindowSubclassProc(
        HWND hWnd,
        uint uMsg,
        nint wParam,
        nint lParam,
        nuint uIdSubclass,
        nint dwRefData
    )
    {
        if (uMsg == (uint)User32.WindowMessage.WM_RBUTTONUP)
        {
            HandleRightClick();
            return 0;
        }
        if ((uMsg == 0x100 || uMsg == 0x101)
            && (Windows.System.VirtualKey)(int)wParam == Windows.System.VirtualKey.Shift)
            RequestRedraw();
        if (uMsg == 0x100 && HandleCaptureKey((Windows.System.VirtualKey)(int)wParam))
            return 0;
        return base.WindowSubclassProc(hWnd, uMsg, wParam, lParam, uIdSubclass, dwRefData);
    }

    public Rect GetPhysicalSourceRect()
    {
        return ComputePhysicalRect(SelectionRect, _selectionFromDrag);
    }

    // 鼠标框选时两端是光标像素索引（含端点），宽 = |x2-x1| + 1（CreateRectangle）；
    // 窗口矩形本身就是正常尺寸，不 +1。WinUI 指针是 DIP，先 round 成物理像素索引。
    private Rect ComputePhysicalRect(Rect dipRect, bool fromDrag)
    {
        double ratioX = _canvasOriginal.SizeInPixels.Width / _lockedW;
        double ratioY = _canvasOriginal.SizeInPixels.Height / _lockedH;

        int x1 = (int)Math.Round(dipRect.X * ratioX);
        int y1 = (int)Math.Round(dipRect.Y * ratioY);
        int x2 = (int)Math.Round((dipRect.X + dipRect.Width) * ratioX);
        int y2 = (int)Math.Round((dipRect.Y + dipRect.Height) * ratioY);

        int physW = (int)_canvasOriginal.SizeInPixels.Width;
        int physH = (int)_canvasOriginal.SizeInPixels.Height;
        int left = Math.Clamp(Math.Min(x1, x2), 0, physW);
        int top = Math.Clamp(Math.Min(y1, y2), 0, physH);
        int right = Math.Clamp(Math.Max(x1, x2) + (fromDrag ? 1 : 0), 0, physW);
        int bottom = Math.Clamp(Math.Max(y1, y2) + (fromDrag ? 1 : 0), 0, physH);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}
