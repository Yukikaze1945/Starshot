using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Vanara.PInvoke;

namespace Starshot.Features.Screenshot;

public sealed partial class RegionCaptureWindow
{
    private XamlRoot? _viewportRoot;
    private bool _viewportSyncQueued;

    private void InitializeOverlayViewport()
    {
        RootGrid.Loaded += OverlayViewportLoaded;
        Canvas.CompositionScaleChanged += OverlayCompositionScaleChanged;
        AttachViewportRoot();
    }
    private void AttachViewportRoot()
    {
        var root = RootGrid.XamlRoot;
        if (ReferenceEquals(root, _viewportRoot)) return;
        if (_viewportRoot is not null) _viewportRoot.Changed -= OverlayRootChanged;
        _viewportRoot = root;
        if (root is not null) root.Changed += OverlayRootChanged;
    }
    private void OverlayViewportLoaded(object sender, RoutedEventArgs e) { AttachViewportRoot(); QueueViewportSync(); }
    private void OverlayCompositionScaleChanged(Microsoft.UI.Xaml.Controls.SwapChainPanel sender, object args) => QueueViewportSync();
    private void OverlayRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => QueueViewportSync();

    private float GetCaptureScale()
    {
        // A spanning HWND still has one XAML coordinate system. A side monitor's
        // DPI says nothing about this root's actual rasterization scale.
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? User32.GetDpiForWindow(WindowHandle) / 96d;
        return double.IsFinite(scale) && scale > 0 ? (float)scale : 1f;
    }
    private void QueueViewportSync()
    {
        if (_isClosed || _canvasOriginal is null || _swapChain is null || _viewportSyncQueued) return;
        _viewportSyncQueued = true;
        int generation = _captureGeneration;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _captureGeneration) return;
            _viewportSyncQueued = false;
            if (_isClosed || _canvasOriginal is null || _swapChain is null) return;
            try { if (SynchronizeViewport()) RequestRedraw(); }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Region viewport DPI synchronization failed");
                CloseWindow(new RegionCaptureResult(RegionCaptureAction.Cancel, default, default, null), ex);
            }
        })) _viewportSyncQueued = false;
    }
    private bool SynchronizeViewport()
    {
        AttachViewportRoot();
        // WinUI/WM_DPICHANGED can suggest resizing the HWND. A frozen virtual
        // desktop must keep its captured physical extent, including offscreen prepaint.
        if (_canvasOriginal is not null && User32.GetClientRect(WindowHandle, out RECT client))
        {
            var pixels = _canvasOriginal.SizeInPixels;
            if (client.Width != pixels.Width || client.Height != pixels.Height)
                User32.SetWindowPos(WindowHandle, IntPtr.Zero, 0, 0, (int)pixels.Width, (int)pixels.Height,
                    User32.SetWindowPosFlags.SWP_NOMOVE | User32.SetWindowPosFlags.SWP_NOACTIVATE | User32.SetWindowPosFlags.SWP_NOZORDER);
        }
        return ApplyViewportScale(GetCaptureScale());
    }
    private bool ApplyViewportScale(float scale)
    {
        if (_canvasOriginal is null || _swapChain is null) return false;
        if (!float.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        var pixels = _canvasOriginal.SizeInPixels;
        if (Math.Abs(_scale - scale) < .001 && Math.Abs(_swapChain.Dpi - scale * 96) < .05
            && _swapChain.SizeInPixels.Width == pixels.Width && _swapChain.SizeInPixels.Height == pixels.Height) return false;
        float old = _scale;
        _swapChain.ResizeBuffers(pixels.Width / scale, pixels.Height / scale, scale * 96);
        // The backing pixel extent does not change with DPI. Rebase only DIP-space
        // UI state; physical crops, analysis caches and capture pixels remain fixed.
        float ratio = old > 0 ? old / scale : 1;
        SelectionRect = ScaleViewportRect(SelectionRect, ratio);
        _hoverRect = ScaleViewportRect(_hoverRect, ratio);
        _windowRects = _windowRects.Select(r => ScaleViewportRect(r, ratio)).ToList();
        _positionOnClick = ScaleViewportPoint(_positionOnClick, ratio);
        _currentMousePos = ScaleViewportPoint(_currentMousePos, ratio);
        _textStart = ScaleViewportPoint(_textStart, ratio);
        _toolbarDismissedAt = ScaleViewportPoint(_toolbarDismissedAt, ratio);
        var annotations = new HashSet<Annotation>(_annotations);
        foreach (var edit in _undoEdits) annotations.Add(edit.annotation);
        foreach (var edit in _redoEdits) annotations.Add(edit.annotation);
        if (_draftAnnotation is not null) annotations.Add(_draftAnnotation);
        foreach (var annotation in annotations) annotation.CoordinateScale *= ratio;
        StopAnalysisDrag(HdrAnalysisPanel); StopAnalysisDrag(GamutAnalysisPanel);
        if (AnnotationTextEditor.Visibility == Visibility.Visible)
        {
            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(AnnotationTextEditor, _textStart.X);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(AnnotationTextEditor, _textStart.Y);
        }
        _scale = scale; _lockedW = (float)_swapChain.Size.Width; _lockedH = (float)_swapChain.Size.Height;
        _toolbarVisualKey = "";
        if (_state == RegionCaptureState.Selected) RefreshToolbar();
        Serilog.Log.Information("Region viewport synchronized: generation={Generation}, scale={Old}->{Scale}, hwndDpi={HwndDpi}, pixels={Width}x{Height}, dip={DipWidth}x{DipHeight}",
            _captureGeneration, old, scale, User32.GetDpiForWindow(WindowHandle), pixels.Width, pixels.Height, _lockedW, _lockedH);
        return true;
    }
    private static Point ScaleViewportPoint(Point p, float factor) => new(p.X * factor, p.Y * factor);
    private static Rect ScaleViewportRect(Rect r, float factor) => new(r.X * factor, r.Y * factor, r.Width * factor, r.Height * factor);

    private void DisposeOverlayViewport()
    {
        RootGrid.Loaded -= OverlayViewportLoaded;
        Canvas.CompositionScaleChanged -= OverlayCompositionScaleChanged;
        if (_viewportRoot is not null) _viewportRoot.Changed -= OverlayRootChanged;
        _viewportRoot = null; _viewportSyncQueued = false;
    }
}
