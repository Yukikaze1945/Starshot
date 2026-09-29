using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Vanara.PInvoke;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace Starshot.Features.Screenshot;

internal partial class ScreenCaptureHelper
{
    public static readonly bool IsTryCreateFromWindowIdPresent = ApiInformation.IsMethodPresent(
        "Windows.Graphics.Capture.GraphicsCaptureItem",
        "TryCreateFromWindowId"
    );

    public static readonly bool IsIncludeSecondaryWindowsPresent = ApiInformation.IsPropertyPresent(
        "Windows.Graphics.Capture.GraphicsCaptureSession",
        "IncludeSecondaryWindows"
    );

    public static readonly bool IsIsBorderRequiredPresent = ApiInformation.IsPropertyPresent(
        "Windows.Graphics.Capture.GraphicsCaptureSession",
        "IsBorderRequired"
    );

    public static readonly bool IsIsCursorCaptureEnabledPresent = ApiInformation.IsPropertyPresent(
        "Windows.Graphics.Capture.GraphicsCaptureSession",
        "IsCursorCaptureEnabled"
    );

    public static readonly bool IsWin10 = Environment.OSVersion.Version.Build < 22000;

    /// <summary>
    /// 按窗口捕获（WGC 窗口级 item）。⚠ 截屏语义上不适用，主流程零调用、仅作设施保留：
    /// 窗口捕获拿到的是该窗口自身的 DWM surface——上方覆盖物（弹窗/其他窗口）不在帧里，
    /// 截到的不是「用户看到的画面」；且窗口最小化即不可捕获。截屏软件要的是合成后的显示器
    /// 输出（CaptureMonitorAsync + 矩形裁剪），所见即所得。窗口级捕获仅适合「只要窗口内容、
    /// 别盖东西反而干净」的场景（如游戏工具截取后台游戏画面）。
    /// </summary>
    public static async Task<Direct3D11CaptureFrame> CaptureWindowAsync(
        nint hwnd,
        DirectXPixelFormat pixelFormat,
        CanvasDevice? device = null,
        CancellationToken cancellationToken = default
    )
    {
        if (!User32.IsWindow(hwnd))
        {
            throw new ArgumentException(
                "The provided handle is not a valid window handle.",
                nameof(hwnd)
            );
        }
        if (User32.IsIconic(hwnd))
        {
            throw new InvalidOperationException("Cannot capture a minimized window.");
        }
        GraphicsCaptureItem item = CreateGraphicsCaptureItemForWindow(hwnd);
        try
        {
            return await CaptureAsync(item, pixelFormat, device, cancellationToken);
        }
        finally
        {
            ((WinRT.IWinRTObject)item).NativeObject.Dispose();
        }
    }

    public static async Task<CanvasRenderTarget> CaptureMonitorBitmapAsync(
        nint monitor,
        DirectXPixelFormat pixelFormat,
        CanvasDevice device,
        bool isHdr,
        CancellationToken cancellationToken = default
    )
    {
        return await MonitorCaptureContext.CaptureAsync(
            monitor, pixelFormat, device, cancellationToken, isHdr
        ).ConfigureAwait(false);
    }

    public static void DisposeMonitorContexts() => MonitorCaptureContext.DisposeAll();

    public static async Task<Direct3D11CaptureFrame> CaptureAsync(
        GraphicsCaptureItem item,
        DirectXPixelFormat pixelFormat,
        CanvasDevice? device = null,
        CancellationToken cancellationToken = default
    )
    {
        device ??= CanvasDevice.GetSharedDevice();
        // 必须 CreateFreeThreaded：区域截图在 Task.Run 线程池线程发起捕获（并行抓多显示器），
        // 无 DispatcherQueue——Create 版的 FrameArrived 依赖创建线程的 Dispatcher 投递，线程池上永远不来（Win10 实测 10s 超时）。
        // Win10 格式仍写死 B8G8R8A8（WGC 在 Win10 无 HDR float 捕获），Win11 起用调用方请求的格式
        using Direct3D11CaptureFramePool framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device,
            IsWin10 ? DirectXPixelFormat.R8G8B8A8UIntNormalized : pixelFormat,
            1,
            item.Size
        );
        using GraphicsCaptureSession session = framePool.CreateCaptureSession(item);
        if (IsIncludeSecondaryWindowsPresent)
        {
            session.IncludeSecondaryWindows = true;
        }
        if (IsIsBorderRequiredPresent)
        {
            try { session.IsBorderRequired = false; }
            catch (COMException ex) when ((uint)ex.HResult == 0x80070490)
            {
                // 某些系统不支持此可选设置，捕获本身仍可继续。
                Serilog.Log.Debug(ex, "WGC border option unavailable");
            }
        }
        if (IsIsCursorCaptureEnabledPresent)
        {
            session.IsCursorCaptureEnabled = false;
        }
        var completionSource = new TaskCompletionSource<Direct3D11CaptureFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var cancellationRegistration = cancellationToken.Register(() => completionSource.TrySetCanceled());
        // 额外超时保护：即使外部没传 CancellationToken，也不会永久挂起
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        using var timeoutRegistration = timeoutCts.Token.Register(() =>
            completionSource.TrySetException(
                new TimeoutException("Screen capture timed out after 10 seconds")
            )
        );
        int frameDelivered = 0;
        Windows.Foundation.TypedEventHandler<Direct3D11CaptureFramePool, object> frameArrived = (
            s,
            _
        ) =>
        {
            Direct3D11CaptureFrame? frame = null;
            try
            {
                frame = s.TryGetNextFrame();
                if (frame is not null
                    && Interlocked.Exchange(ref frameDelivered, 1) == 0
                    && completionSource.TrySetResult(frame))
                {
                    // The caller owns the frame only when the handoff succeeds.
                    frame = null;
                }
            }
            catch (Exception ex)
            {
                completionSource.TrySetException(ex);
            }
            finally
            {
                // Includes duplicate frames and frames arriving after cancellation or timeout.
                frame?.Dispose();
            }
        };
        bool subscribed = false;
        try
        {
            framePool.FrameArrived += frameArrived;
            subscribed = true;
            session.StartCapture();
            return await completionSource.Task.ConfigureAwait(false);
        }
        finally
        {
            if (subscribed)
                framePool.FrameArrived -= frameArrived;
            // session and framePool are disposed by their using declarations.
        }
    }

    /// <summary>
    /// 按窗口创建捕获项。TryCreateFromWindowId 仅 Win11 可用；Win10 走
    /// IGraphicsCaptureItemInterop.CreateForWindow（Guid 见文档约定，接口由 GeneratedComInterface 声明）。
    /// </summary>
    public static GraphicsCaptureItem CreateGraphicsCaptureItemForWindow(nint hwnd)
    {
        GraphicsCaptureItem graphicsCaptureItem;
        if (IsTryCreateFromWindowIdPresent)
        {
            graphicsCaptureItem = GraphicsCaptureItem.TryCreateFromWindowId(
                new WindowId((ulong)hwnd)
            );
        }
        else
        {
            Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            nint abi = GraphicsCaptureItem
                .As<IGraphicsCaptureItemInterop>()
                .CreateForWindow(hwnd, GraphicsCaptureItemGuid);
            try { graphicsCaptureItem = GraphicsCaptureItem.FromAbi(abi); }
            finally { Marshal.Release(abi); }
        }
        return graphicsCaptureItem;
    }

    /// <summary>
    /// 按显示器创建捕获项。桌面应用统一走 IGraphicsCaptureItemInterop；
    /// TryCreateFromDisplayId 在部分系统可返回 item，但 StartCapture 会报 ERROR_NOT_FOUND。
    /// </summary>
    public static GraphicsCaptureItem CreateGraphicsCaptureItemForMonitor(nint monitor)
    {
        Guid graphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        nint abi = GraphicsCaptureItem
            .As<IGraphicsCaptureItemInterop>()
            .CreateForMonitor(monitor, graphicsCaptureItemGuid);
        try { return GraphicsCaptureItem.FromAbi(abi); }
        finally { Marshal.Release(abi); }
    }

    [ComVisible(true)]
    [GeneratedComInterface]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [System.Runtime.InteropServices.Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    internal partial interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, in Guid iid);

        IntPtr CreateForMonitor(IntPtr monitor, in Guid iid);
    }
}
