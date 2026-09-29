using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starshot.Features.Codec;
using Starshot.Frameworks;
using Starshot.Helpers;
using Vanara.PInvoke;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Graphics.DirectX;
using Windows.Storage;
using Windows.UI;

namespace Starshot.Features.Screenshot;

/// <summary>Independent image pin with its own copy of the screenshot pixels.</summary>
public sealed partial class PinnedCaptureWindow : WindowEx
{
    private static readonly HashSet<PinnedCaptureWindow> OpenWindows = new();
    private static readonly LinkedList<PinSnapshot> ClosedPins = new();
    private const long MaximumHistoryBytes = 128L * 1024 * 1024;
    private sealed record PinSnapshot(
        byte[] Pixels, int Width, int Height, int X, int Y, double Zoom,
        int Rotation, bool FlipHorizontal, bool FlipVertical, bool Grayscale,
        bool Inverted, int Brightness, double Opacity, bool Thumbnail,
        int ThumbnailX, int ThumbnailY, bool Cropped, int CropX, int CropY,
        int CropWidth, int CropHeight, bool ShowAnnotations,
        bool AlwaysOnTop, bool ShowBorder, string Title, List<Stroke> Strokes);
    private readonly CanvasSwapChainPanel _panel = new();
    private readonly Border _border = new() { BorderThickness = new Thickness(2), IsHitTestVisible = false };
    private readonly Border _cropOutline = new()
    {
        BorderThickness = new Thickness(2),
        BorderBrush = new SolidColorBrush(Color.FromArgb(255, 49, 137, 255)),
        Background = new SolidColorBrush(Color.FromArgb(40, 49, 137, 255)),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
    };
    private readonly TextBlock _titleLabel = new()
    {
        Foreground = new SolidColorBrush(Colors.White),
    };
    private readonly Border _titleHost = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Bottom,
        Background = new SolidColorBrush(Color.FromArgb(200, 10, 11, 17)),
        Padding = new Thickness(6, 2, 6, 2),
        Visibility = Visibility.Collapsed, IsHitTestVisible = false,
    };
    private readonly StackPanel _annotationBar = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top,
        Background = new SolidColorBrush(Color.FromArgb(240, 12, 14, 20)),
        Spacing = 4, Visibility = Visibility.Collapsed,
    };
    private readonly byte[] _originalPixels;
    private readonly int _originalWidth, _originalHeight;
    private CanvasRenderTarget? _rendered;
    private CanvasBitmap? _baseBitmap;
    private CanvasSwapChain? _swapChain;
    private bool _dragging, _closed, _destroyed, _locked, _alwaysOnTop = true, _showBorder = true;
    private bool _thumbnail, _showAnnotations = true, _annotating;
    private bool _cropped, _rightDragging;
    private int _cropX, _cropY, _cropWidth, _cropHeight;
    private Windows.Foundation.Point _rightStart;
    private MenuFlyout? _menu;
    private bool _middleReset;
    private double _previousZoom = 1, _previousOpacity = 1;
    private double _zoom, _opacity = 1;
    private int _rotation, _brightness, _thumbX, _thumbY;
    private bool _flipHorizontal, _flipVertical, _grayscale, _inverted;
    private int _imageWidth, _imageHeight;
    private int _dragCursorX, _dragCursorY, _dragWindowX, _dragWindowY;
    private string _pinTitle = "";

    public PinnedCaptureWindow(CanvasRenderTarget source, int desktopX, int desktopY,
        double initialZoom = 1)
    {
        _originalWidth = (int)source.SizeInPixels.Width;
        _originalHeight = (int)source.SizeInPixels.Height;
        _originalPixels = source.GetPixelBytes();
        _imageWidth = _originalWidth; _imageHeight = _originalHeight;
        _zoom = Math.Clamp(initialZoom, 0.2, 5);
        Title = "Starshot Pin";
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }
        var root = new Grid { Background = new SolidColorBrush(Colors.Black), RequestedTheme = ElementTheme.Dark };
        root.Children.Add(_panel);
        root.Children.Add(_border);
        root.Children.Add(_cropOutline);
        _titleHost.Child = _titleLabel;
        root.Children.Add(_titleHost);
        root.Children.Add(_annotationBar);
        Content = root;
        BuildAnnotationBar();
        _menu = BuildMenu();
        _panel.PointerPressed += Panel_PointerPressed;
        _panel.PointerMoved += Panel_PointerMoved;
        _panel.PointerReleased += Panel_PointerReleased;
        _panel.PointerWheelChanged += Panel_PointerWheelChanged;
        _panel.DoubleTapped += (_, _) => { if (!_annotating) Close(); };
        Closed += (_, _) =>
        {
            if (!_destroyed) RememberClosedPin();
            _closed = true;
            OpenWindows.Remove(this);
            _panel.SwapChain = null;
            _swapChain?.Dispose(); _swapChain = null;
            _rendered?.Dispose(); _rendered = null;
            _baseBitmap?.Dispose(); _baseBitmap = null;
        };
        RebuildImage();
        UpdateBorder();
        ResizePin(desktopX, desktopY);
        Activate();
        Redraw();
        OpenWindows.Add(this);
        Serilog.Log.Information("Image pin created: {Width}x{Height} at {X},{Y}",
            _originalWidth, _originalHeight, desktopX, desktopY);
    }

    public static void PinClipboard() => _ = PinClipboardAsync();

    public static void ReopenLast()
    {
        if (ClosedPins.Last is null) return;
        PinSnapshot snapshot = ClosedPins.Last.Value;
        ClosedPins.RemoveLast();
        try
        {
            var device = CanvasDevice.GetSharedDevice();
            using var bitmap = CanvasBitmap.CreateFromBytes(device, snapshot.Pixels,
                snapshot.Width, snapshot.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized);
            using var source = new CanvasRenderTarget(device, snapshot.Width, snapshot.Height, 96,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
            using (var ds = source.CreateDrawingSession()) ds.DrawImage(bitmap);
            var pin = new PinnedCaptureWindow(source, snapshot.X, snapshot.Y, snapshot.Zoom);
            pin._rotation = snapshot.Rotation;
            pin._flipHorizontal = snapshot.FlipHorizontal;
            pin._flipVertical = snapshot.FlipVertical;
            pin._grayscale = snapshot.Grayscale;
            pin._inverted = snapshot.Inverted;
            pin._brightness = snapshot.Brightness;
            pin._thumbnail = snapshot.Thumbnail;
            pin._thumbX = snapshot.ThumbnailX;
            pin._thumbY = snapshot.ThumbnailY;
            pin._cropped = snapshot.Cropped;
            pin._cropX = snapshot.CropX;
            pin._cropY = snapshot.CropY;
            pin._cropWidth = snapshot.CropWidth;
            pin._cropHeight = snapshot.CropHeight;
            pin._showAnnotations = snapshot.ShowAnnotations;
            pin._showBorder = snapshot.ShowBorder;
            pin._strokes.AddRange(snapshot.Strokes);
            pin._pinTitle = snapshot.Title;
            pin._titleLabel.Text = pin._pinTitle;
            pin._titleHost.Visibility = pin._pinTitle.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            pin.RebuildImage();
            pin.ResizePin(snapshot.X, snapshot.Y);
            pin.SetOpacity(snapshot.Opacity);
            pin.SetAlwaysOnTop(snapshot.AlwaysOnTop);
            pin.UpdateBorder();
        }
        catch (Exception ex)
        {
            ClosedPins.AddLast(snapshot);
            Serilog.Log.Error(ex, "Reopen image pin failed");
        }
    }

    private void RememberClosedPin()
    {
        var strokes = new List<Stroke>(_strokes.Count);
        foreach (Stroke stroke in _strokes)
        {
            var copy = new Stroke { Tool = stroke.Tool };
            copy.Points.AddRange(stroke.Points);
            strokes.Add(copy);
        }
        ClosedPins.AddLast(new PinSnapshot(_originalPixels, _originalWidth, _originalHeight,
            AppWindow.Position.X, AppWindow.Position.Y, _zoom, _rotation,
            _flipHorizontal, _flipVertical, _grayscale, _inverted, _brightness,
            _opacity, _thumbnail, _thumbX, _thumbY, _cropped,
            _cropX, _cropY, _cropWidth, _cropHeight, _showAnnotations,
            _alwaysOnTop, _showBorder, _pinTitle, strokes));
        long bytes = 0;
        foreach (PinSnapshot item in ClosedPins) bytes += item.Pixels.LongLength;
        while (ClosedPins.Count > 10 || bytes > MaximumHistoryBytes)
        {
            bytes -= ClosedPins.First!.Value.Pixels.LongLength;
            ClosedPins.RemoveFirst();
        }
    }

    private static async Task PinClipboardAsync()
    {
        try
        {
            var data = Clipboard.GetContent();
            if (data is not null && data.Contains(StandardDataFormats.StorageItems))
            {
                var items = await data.GetStorageItemsAsync();
                var file = items.OfType<StorageFile>().FirstOrDefault(item =>
                    new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".avif", ".jxl" }
                        .Contains(Path.GetExtension(item.Path).ToLowerInvariant()));
                if (file is not null)
                {
                    using var image = await ImageLoader.LoadImageAsync(file.Path);
                    using var canvas = CopyToSdr(image.CanvasBitmap);
                    ShowCentered(canvas);
                    return;
                }
            }
            var streamRef = await ClipboardHelper.GetClipboardImageAsync();
            if (streamRef is null)
            {
                Serilog.Log.Information("Pin clipboard: no image or supported image file");
                return;
            }
            using var stream = await streamRef.OpenReadAsync();
            using var bitmap = await CanvasBitmap.LoadAsync(CanvasDevice.GetSharedDevice(), stream);
            using var target = CopyToSdr(bitmap);
            ShowCentered(target);
        }
        catch (Exception ex) { Serilog.Log.Error(ex, "Pin clipboard failed"); }
    }

    private static CanvasRenderTarget CopyToSdr(CanvasBitmap source)
    {
        int width = (int)source.SizeInPixels.Width, height = (int)source.SizeInPixels.Height;
        if (width < 1 || height < 1 || (long)width * height > 100_000_000)
            throw new InvalidOperationException("Clipboard image dimensions are unsupported.");
        var result = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        try
        {
            if (source.Format is DirectXPixelFormat.R16G16B16A16Float or DirectXPixelFormat.R32G32B32A32Float)
            {
                using var toneMapped = ScreenCaptureService.TonemapToSdr(source, 203);
                using var ds = result.CreateDrawingSession();
                ds.DrawImage(toneMapped);
            }
            else using (var ds = result.CreateDrawingSession()) ds.DrawImage(source);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static void ShowCentered(CanvasRenderTarget source)
    {
        var work = DisplayArea.Primary.WorkArea;
        int width = (int)source.SizeInPixels.Width, height = (int)source.SizeInPixels.Height;
        double zoom = Math.Min(1, Math.Min(work.Width * 0.8 / width, work.Height * 0.8 / height));
        int x = work.X + (work.Width - (int)Math.Round(width * zoom)) / 2;
        int y = work.Y + (work.Height - (int)Math.Round(height * zoom)) / 2;
        _ = new PinnedCaptureWindow(source, x, y, zoom);
    }

    private int CropWidth => _cropped ? _cropWidth : _imageWidth;
    private int CropHeight => _cropped ? _cropHeight : _imageHeight;
    private int VisibleWidth => _thumbnail ? Math.Min(240, CropWidth) : CropWidth;
    private int VisibleHeight => _thumbnail ? Math.Min(180, CropHeight) : CropHeight;

    private void ResizePin(int x, int y)
    {
        if (_closed) return;
        int width = Math.Max(1, (int)Math.Round(VisibleWidth * _zoom));
        int height = Math.Max(1, (int)Math.Round(VisibleHeight * _zoom));
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        float scale = User32.GetDpiForWindow(new HWND(WindowHandle)) / 96f;
        if (scale <= 0) scale = 1;
        var old = _swapChain;
        _panel.SwapChain = null;
        _swapChain = new CanvasSwapChain(CanvasDevice.GetSharedDevice(), width / scale,
            height / scale, 96 * scale, DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2, CanvasAlphaMode.Premultiplied);
        _panel.SwapChain = _swapChain;
        old?.Dispose();
        Redraw();
    }

    private void Redraw()
    {
        if (_closed || _swapChain is null || _rendered is null) return;
        float scale = User32.GetDpiForWindow(new HWND(WindowHandle)) / 96f;
        if (scale <= 0) scale = 1;
        using (var ds = _swapChain.CreateDrawingSession(Colors.Transparent))
            ds.DrawImage(_rendered,
                new Windows.Foundation.Rect(0, 0, AppWindow.Size.Width / scale, AppWindow.Size.Height / scale),
                new Windows.Foundation.Rect(_cropX + _thumbX, _cropY + _thumbY,
                    VisibleWidth, VisibleHeight));
        _swapChain.Present();
    }

    private void Panel_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_panel);
        if (point.Properties.IsMiddleButtonPressed && !_locked)
        {
            if (_middleReset)
            {
                SetZoom(_previousZoom);
                SetOpacity(_previousOpacity);
            }
            else
            {
                _previousZoom = _zoom; _previousOpacity = _opacity;
                SetZoom(1); SetOpacity(1);
            }
            _middleReset = !_middleReset;
            e.Handled = true;
            return;
        }
        if (point.Properties.IsRightButtonPressed && !_locked)
        {
            _rightStart = point.Position;
            _rightDragging = true;
            _panel.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }
        if (point.Properties.IsRightButtonPressed) return;
        if (!point.Properties.IsLeftButtonPressed || _locked) return;
        if (_annotating) { StartAnnotation(e); return; }
        if (!User32.GetCursorPos(out var cursor)) return;
        _dragging = true;
        _dragCursorX = cursor.x; _dragCursorY = cursor.y;
        _dragWindowX = AppWindow.Position.X; _dragWindowY = AppWindow.Position.Y;
        _panel.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Panel_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_rightDragging)
        {
            var point = e.GetCurrentPoint(_panel).Position;
            double left = Math.Max(0, Math.Min(_rightStart.X, point.X));
            double top = Math.Max(0, Math.Min(_rightStart.Y, point.Y));
            double right = Math.Min(_panel.ActualWidth, Math.Max(_rightStart.X, point.X));
            double bottom = Math.Min(_panel.ActualHeight, Math.Max(_rightStart.Y, point.Y));
            if (right - left >= 4 && bottom - top >= 4)
            {
                _cropOutline.Margin = new Thickness(left, top, 0, 0);
                _cropOutline.Width = right - left;
                _cropOutline.Height = bottom - top;
                _cropOutline.Visibility = Visibility.Visible;
            }
            e.Handled = true;
            return;
        }
        if (_annotating && _activeStroke is not null) { ExtendAnnotation(e); return; }
        if (!_dragging || !User32.GetCursorPos(out var cursor)) return;
        int dx = cursor.x - _dragCursorX, dy = cursor.y - _dragCursorY;
        if (_thumbnail && e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift))
        {
            _thumbX = Math.Clamp(_thumbX - (int)(dx / _zoom), 0, CropWidth - VisibleWidth);
            _thumbY = Math.Clamp(_thumbY - (int)(dy / _zoom), 0, CropHeight - VisibleHeight);
            _dragCursorX = cursor.x; _dragCursorY = cursor.y;
            Redraw();
        }
        else AppWindow.Move(new PointInt32(_dragWindowX + dx, _dragWindowY + dy));
        e.Handled = true;
    }

    private void Panel_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_rightDragging)
        {
            _rightDragging = false;
            _cropOutline.Visibility = Visibility.Collapsed;
            _panel.ReleasePointerCapture(e.Pointer);
            var point = e.GetCurrentPoint(_panel).Position;
            if (Math.Abs(point.X - _rightStart.X) >= 5 &&
                Math.Abs(point.Y - _rightStart.Y) >= 5)
                CropToSelection(_rightStart, point);
            else
                _menu?.ShowAt(_panel, new FlyoutShowOptions { Position = point });
            e.Handled = true;
            return;
        }
        if (_activeStroke is not null) { FinishAnnotation(e); return; }
        _dragging = false;
        _panel.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void Panel_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_locked) return;
        int delta = e.GetCurrentPoint(_panel).Properties.MouseWheelDelta;
        if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control))
            SetOpacity(_opacity + Math.Sign(delta) * 0.1);
        else SetZoom(_zoom * (delta > 0 ? 1.1 : 1 / 1.1));
        e.Handled = true;
    }

    private void CropToSelection(Windows.Foundation.Point start, Windows.Foundation.Point end)
    {
        double left = Math.Clamp(Math.Min(start.X, end.X), 0, _panel.ActualWidth);
        double top = Math.Clamp(Math.Min(start.Y, end.Y), 0, _panel.ActualHeight);
        double right = Math.Clamp(Math.Max(start.X, end.X), 0, _panel.ActualWidth);
        double bottom = Math.Clamp(Math.Max(start.Y, end.Y), 0, _panel.ActualHeight);
        if (right - left < 5 || bottom - top < 5) return;
        double dpi = User32.GetDpiForWindow(new HWND(WindowHandle)) / 96.0;
        if (dpi <= 0) dpi = 1;
        int x = Math.Clamp(_cropX + _thumbX + (int)Math.Floor(left * dpi / _zoom), 0, _imageWidth - 1);
        int y = Math.Clamp(_cropY + _thumbY + (int)Math.Floor(top * dpi / _zoom), 0, _imageHeight - 1);
        int width = Math.Clamp((int)Math.Ceiling((right - left) * dpi / _zoom), 1, _imageWidth - x);
        int height = Math.Clamp((int)Math.Ceiling((bottom - top) * dpi / _zoom), 1, _imageHeight - y);
        int windowX = AppWindow.Position.X + (int)Math.Round(left * dpi);
        int windowY = AppWindow.Position.Y + (int)Math.Round(top * dpi);
        _cropped = true;
        _cropX = x; _cropY = y; _cropWidth = width; _cropHeight = height;
        _thumbnail = false;
        _thumbX = _thumbY = 0;
        ResizePin(windowX, windowY);
    }

    private void RestoreFullImage()
    {
        if (!_cropped) return;
        _cropped = false;
        _cropX = _cropY = 0;
        _cropWidth = _cropHeight = 0;
        _thumbX = _thumbY = 0;
        ResizePin(AppWindow.Position.X, AppWindow.Position.Y);
    }

    private void SetZoom(double zoom)
    {
        double maxZoom = Math.Min(5,
            Math.Min(8192.0 / VisibleWidth, 8192.0 / VisibleHeight));
        zoom = Math.Clamp(zoom, 0.2, Math.Max(0.2, maxZoom));
        if (Math.Abs(zoom - _zoom) < 0.001) return;
        int centerX = AppWindow.Position.X + AppWindow.Size.Width / 2;
        int centerY = AppWindow.Position.Y + AppWindow.Size.Height / 2;
        _zoom = zoom;
        ResizePin(centerX - (int)Math.Round(VisibleWidth * zoom) / 2,
            centerY - (int)Math.Round(VisibleHeight * zoom) / 2);
    }

    private void SetOpacity(double opacity)
    {
        _opacity = Math.Clamp(Math.Round(opacity, 1), 0.1, 1);
        var style = User32.GetWindowLongPtr(WindowHandle, User32.WindowLongFlags.GWL_EXSTYLE);
        User32.SetWindowLong(WindowHandle, User32.WindowLongFlags.GWL_EXSTYLE,
            style | (nint)User32.WindowStylesEx.WS_EX_LAYERED);
        User32.SetLayeredWindowAttributes(WindowHandle, 0, (byte)Math.Round(_opacity * 255),
            User32.LayeredWindowAttributes.LWA_ALPHA);
    }

    private void UpdateBorder()
    {
        _border.BorderThickness = new Thickness(_showBorder ? 2 : 0);
        _border.BorderBrush = new SolidColorBrush(_locked
            ? Color.FromArgb(255, 255, 155, 60) : Color.FromArgb(255, 49, 137, 255));
    }
}
