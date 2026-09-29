using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Win32;
using Vanara.PInvoke;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Starshot.Features.Screenshot;

// A slot serializes requests for one monitor. Different monitors never wait on each other.
internal sealed class MonitorCaptureContext : IDisposable
{
    private static readonly object CacheLock = new();
    private static readonly Dictionary<nint, Slot> Cache = new();
    private static bool _shuttingDown;
    private static int _backgroundFramePauseCount;

    // Keep the WGC session alive while the region overlay is open, but stop its
    // Win2D frame copies from racing the overlay's drawing on the shared device.
    public static IDisposable PauseBackgroundFrames()
    {
        MonitorCaptureContext[] contexts;
        lock (CacheLock)
        {
            Interlocked.Increment(ref _backgroundFramePauseCount);
            contexts = new MonitorCaptureContext[Cache.Count];
            int index = 0;
            foreach (var slot in Cache.Values)
                if (slot.Context is { } context)
                    contexts[index++] = context;
            Array.Resize(ref contexts, index);
        }

        // Fence any callback that began copying before the pause was published.
        foreach (var context in contexts)
            lock (context._frameLock) { }

        Serilog.Log.Information("WGC background frame copies paused for region overlay");
        return new BackgroundFramePause();
    }

    private sealed class BackgroundFramePause : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            Interlocked.Decrement(ref _backgroundFramePauseCount);
            Serilog.Log.Information("WGC background frame copies resumed after region overlay");
        }
    }

    private sealed class Slot
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public MonitorCaptureContext? Context;
        public volatile bool Retired;
    }

    static MonitorCaptureContext()
    {
        SystemEvents.DisplaySettingsChanged += (_, _) => InvalidateAll();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeAll();
    }

    public static async Task<CanvasRenderTarget> CaptureAsync(
        nint monitor, DirectXPixelFormat requestedFormat, CanvasDevice device,
        CancellationToken cancellationToken, bool? isHdr)
    {
        while (true)
        {
            Slot slot;
            lock (CacheLock)
            {
                if (_shuttingDown)
                    throw new ObjectDisposedException(nameof(MonitorCaptureContext));
                if (!Cache.TryGetValue(monitor, out slot!))
                    Cache[monitor] = slot = new Slot();
            }

            await slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (slot.Retired)
                    continue;

                int width, height;
                try { (width, height) = GetMonitorSize(monitor); }
                catch
                {
                    slot.Context?.Dispose();
                    slot.Context = null;
                    throw;
                }
                DirectXPixelFormat format = ScreenCaptureHelper.IsWin10
                    ? DirectXPixelFormat.R8G8B8A8UIntNormalized : requestedFormat;
                if (slot.Context is not null && !slot.Context.IsValid(width, height, format, device, isHdr))
                {
                    slot.Context.Dispose();
                    slot.Context = null;
                }
                slot.Context ??= new MonitorCaptureContext(monitor, width, height, format, device, isHdr);

                try
                {
                    var bitmap = await slot.Context.CaptureNextBitmapAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (slot.Retired)
                    {
                        bitmap.Dispose();
                        throw new InvalidOperationException("Capture display changed.");
                    }
                    return bitmap;
                }
                catch (Exception ex)
                {
                    // A canceled or timed-out wait does not invalidate a healthy session.
                    if (ex is not OperationCanceledException and not TimeoutException
                        || slot.Context._invalid)
                    {
                        slot.Context.Dispose();
                        slot.Context = null;
                    }
                    throw;
                }
            }
            finally
            {
                slot.Gate.Release();
            }
        }
    }

    private static (int width, int height) GetMonitorSize(nint monitor)
    {
        var info = new User32.MONITORINFOEX
        {
            cbSize = (uint)Marshal.SizeOf<User32.MONITORINFOEX>(),
        };
        if (!User32.GetMonitorInfo(new HMONITOR(monitor), ref info))
            throw new InvalidOperationException("The capture monitor is no longer available.");
        return (info.rcMonitor.right - info.rcMonitor.left,
            info.rcMonitor.bottom - info.rcMonitor.top);
    }

    private static void InvalidateAll()
    {
        Slot[] retired;
        lock (CacheLock)
        {
            retired = new Slot[Cache.Count];
            Cache.Values.CopyTo(retired, 0);
            Cache.Clear();
            foreach (Slot slot in retired)
            {
                slot.Retired = true;
                slot.Context?.Invalidate();
            }
        }
        foreach (Slot slot in retired)
            _ = Task.Run(() => DisposeSlot(slot));
    }

    private static void DisposeSlot(Slot slot)
    {
        slot.Gate.Wait();
        try
        {
            try { slot.Context?.Dispose(); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Failed to dispose WGC context"); }
            slot.Context = null;
        }
        finally { slot.Gate.Release(); }
    }

    public static void DisposeAll()
    {
        Slot[] retired;
        lock (CacheLock)
        {
            if (_shuttingDown) return;
            _shuttingDown = true;
            retired = new Slot[Cache.Count];
            Cache.Values.CopyTo(retired, 0);
            Cache.Clear();
            foreach (Slot slot in retired)
            {
                slot.Retired = true;
                slot.Context?.Invalidate();
            }
        }
        foreach (Slot slot in retired)
            DisposeSlot(slot);
    }

    private readonly nint _monitor;

    private readonly int _width;
    private readonly int _height;
    private readonly DirectXPixelFormat _format;
    private readonly CanvasDevice _device;
    private readonly bool? _isHdr;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly object _frameLock = new();
    private TaskCompletionSource<CanvasRenderTarget>? _waiter;
    private CanvasRenderTarget? _latest;
    private TimeSpan _requestedAt;
    private bool _started;
    private bool _invalid;
    private bool _disposed;

    private MonitorCaptureContext(
        nint monitor, int width, int height, DirectXPixelFormat format, CanvasDevice device, bool? isHdr)
    {
        _monitor = monitor;
        _width = width;
        _height = height;
        _format = format;
        _device = device;
        _isHdr = isHdr;
        _item = ScreenCaptureHelper.CreateGraphicsCaptureItemForMonitor(monitor);
        try
        {
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, format, 1, _item.Size);
            try
            {
                _session = _pool.CreateCaptureSession(_item);
                try
                {
                    if (ScreenCaptureHelper.IsIncludeSecondaryWindowsPresent)
                        _session.IncludeSecondaryWindows = true;
                    if (ScreenCaptureHelper.IsIsBorderRequiredPresent)
                    {
                        try { _session.IsBorderRequired = false; }
                        catch (COMException ex) when ((uint)ex.HResult == 0x80070490)
                        {
                            Serilog.Log.Debug(ex, "WGC border option unavailable");
                        }
                    }
                    if (ScreenCaptureHelper.IsIsCursorCaptureEnabledPresent)
                        _session.IsCursorCaptureEnabled = false;
                    _pool.FrameArrived += OnFrameArrived;
                    Serilog.Log.Information("WGC context created: monitor={Monitor}, size={Width}x{Height}, format={Format}",
                        monitor, width, height, format);
                }
                catch { _session.Dispose(); throw; }
            }
            catch { _pool.Dispose(); throw; }
        }
        catch
        {
            ((WinRT.IWinRTObject)_item).NativeObject.Dispose();
            throw;
        }
    }

    private bool IsValid(int width, int height, DirectXPixelFormat format, CanvasDevice device, bool? isHdr)
    {
        if (_disposed || _invalid || _width != width || _height != height
            || _format != format || _isHdr != isHdr || !ReferenceEquals(_device, device))
            return false;
        try
        {
            var size = _item.Size;
            return size.Width == width && size.Height == height && !_device.IsDeviceLost();
        }
        catch { return false; }
    }

    private async Task<CanvasRenderTarget> CaptureNextBitmapAsync(CancellationToken cancellationToken)
    {
        var waiter = new TaskCompletionSource<CanvasRenderTarget>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool hasCachedFrame;
        lock (_frameLock)
        {
            if (_disposed || _invalid)
                throw new ObjectDisposedException(nameof(MonitorCaptureContext));
            // Bring the independent snapshot up to date before starting the
            // request's short wait for a genuinely newer compositor frame.
            ProcessFrames(_pool);
            hasCachedFrame = _latest is not null;
            _requestedAt = TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
            _waiter = waiter;
            if (!_started)
            {
                try
                {
                    _session.StartCapture();
                    _started = true;
                }
                catch
                {
                    _waiter = null;
                    _invalid = true;
                    throw;
                }
            }
        }

        using var cancellation = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var timeoutRegistration = timeout.Token.Register(() => waiter.TrySetException(
            new TimeoutException("Screen capture timed out waiting for a new frame")));
        try
        {
            if (hasCachedFrame
                && await Task.WhenAny(waiter.Task, Task.Delay(200, cancellationToken))
                    .ConfigureAwait(false) != waiter.Task)
            {
                lock (_frameLock)
                {
                    if (!waiter.Task.IsCompleted && !_disposed && !_invalid && _latest is not null)
                    {
                        var copy = CopyBitmap(_latest);
                        if (waiter.TrySetResult(copy))
                        {
                            _waiter = null;
                            Serilog.Log.Information("WGC latest-frame fallback: monitor={Monitor}, requestQpc={RequestTime}",
                                _monitor, _requestedAt);
                        }
                        else copy.Dispose();
                    }
                }
            }
            return await waiter.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_frameLock)
            {
                if (ReferenceEquals(_waiter, waiter))
                    _waiter = null;
            }
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_frameLock)
        {
            if (_disposed || Volatile.Read(ref _backgroundFramePauseCount) != 0) return;
            try
            {
                ProcessFrames(sender);
            }
            catch (Exception ex)
            {
                _invalid = true;
                _waiter?.TrySetException(ex);
            }
        }
    }

    private void ProcessFrames(Direct3D11CaptureFramePool pool)
    {
        Direct3D11CaptureFrame? frame;
        while (Volatile.Read(ref _backgroundFramePauseCount) == 0
            && (frame = pool.TryGetNextFrame()) is not null)
        {
            using (frame)
            {
                if (frame.ContentSize.Width != _width || frame.ContentSize.Height != _height)
                    throw new InvalidOperationException("Capture monitor size changed.");
                using var surface = frame.Surface;
                using var source = CanvasBitmap.CreateFromDirect3D11Surface(_device, surface, 96);
                if (_latest is null)
                    _latest = CopyBitmap(source);
                else
                    _latest.CopyPixelsFromBitmap(source);

                if (_waiter is not null && !_waiter.Task.IsCompleted
                    && frame.SystemRelativeTime > _requestedAt)
                {
                    var copy = CopyBitmap(_latest);
                    if (_waiter.TrySetResult(copy))
                    {
                        Serilog.Log.Information("WGC fresh frame: monitor={Monitor}, frameQpc={FrameTime}, requestQpc={RequestTime}",
                            _monitor, frame.SystemRelativeTime, _requestedAt);
                        _waiter = null;
                    }
                    else copy.Dispose();
                }
            }
        }
    }

    private CanvasRenderTarget CopyBitmap(CanvasBitmap source)
    {
        var size = source.SizeInPixels;
        var copy = new CanvasRenderTarget(
            _device, size.Width, size.Height, 96, source.Format, source.AlphaMode
        );
        try
        {
            copy.CopyPixelsFromBitmap(source);
            return copy;
        }
        catch
        {
            copy.Dispose();
            throw;
        }
    }

    private void Invalidate()
    {
        lock (_frameLock)
        {
            _invalid = true;
            _waiter?.TrySetException(new InvalidOperationException("Capture display changed."));
        }
    }

    public void Dispose()
    {
        lock (_frameLock)
        {
            if (_disposed) return;
            _disposed = true;
            _waiter?.TrySetException(new ObjectDisposedException(nameof(MonitorCaptureContext)));
            _waiter = null;
            _latest?.Dispose();
            _latest = null;
        }
        // Do not close the session while holding _frameLock: Close may wait for an
        // in-flight FrameArrived callback, which itself needs that lock.
        try { _pool.FrameArrived -= OnFrameArrived; }
        finally
        {
            try { _session.Dispose(); }
            finally
            {
                try { _pool.Dispose(); }
                finally { ((WinRT.IWinRTObject)_item).NativeObject.Dispose(); }
            }
        }
        Serilog.Log.Information("WGC context disposed: monitor={Monitor}", _monitor);
    }
}
