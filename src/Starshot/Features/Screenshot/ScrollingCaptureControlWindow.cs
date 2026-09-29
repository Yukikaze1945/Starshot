using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage.Streams;
using Windows.UI;

namespace Starshot.Features.Screenshot;

internal enum ScrollCaptureAction
{
    StartStop, Direction, TrimLeading, TrimTrailing, ResetTrim,
    Move, SetSelection, Pin, Save, QuickSave, Copy, Cancel
}

internal readonly record struct ScrollCaptureCommand(
    ScrollCaptureAction Action, int X = 0, int Y = 0, int Width = 0, int Height = 0);

/// <summary>Controls and live thumbnail outside the captured area.</summary>
internal sealed class ScrollingCaptureControlWindow : Window
{
    private const int ToolbarWidthDip = 512;
    private const int ToolbarHeightDip = 48;
    private static readonly Color Ink = Color.FromArgb(255, 35, 42, 53);
    private static readonly Color MutedInk = Color.FromArgb(255, 104, 115, 130);
    private readonly Queue<ScrollCaptureCommand> _commands = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _size = new() { Foreground = new SolidColorBrush(MutedInk),
        FontSize = 12, Width = 88, VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _start = new();
    private readonly Button _direction = new();
    private readonly Button _trimStart = new();
    private readonly Button _trimEnd = new();
    private readonly CheckBox _autoCrop = new()
    {
        Content = "自动裁剪", FontSize = 12, MinWidth = 0,
        VerticalAlignment = VerticalAlignment.Center
    };
    private Window? _cropMenu;
    private bool _cropMenuVisible;
    private Window? _notice;
    private bool _noticeVisible;
    private Window? _preview;
    private Image? _previewImage;
    private Grid? _previewCanvas;
    private Border? _currentFrameMarker;
    private TextBlock? _previewText;
    private RectInt32 _selection;
    private readonly RectInt32 _desktop;
    private bool _running, _everStarted, _closed, _dragging;
    private NativePoint _lastCursor;

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom,
        int ellipseWidth, int ellipseHeight);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);

    private static void Exclude(Window window)
    {
        if (!SetWindowDisplayAffinity(WinRT.Interop.WindowNative.GetWindowHandle(window), 0x11))
            Serilog.Log.Warning("Long capture UI exclusion failed: {Error}", Marshal.GetLastPInvokeError());
    }

    public ScrollingCaptureControlWindow(RectInt32 selection, RectInt32 desktop)
    {
        _selection = selection; _desktop = desktop;
        Title = "Starshot 长截图";
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }
        var root = new Border
        {
            RequestedTheme = ElementTheme.Light,
            Background = new SolidColorBrush(Colors.White),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 224, 228, 234)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 5, 8, 5)
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };
        root.Child = actions;
        actions.Children.Add(new TextBlock
        {
            Text = "⠿", Width = 14, FontSize = 17, Foreground = new SolidColorBrush(MutedInk),
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        });
        actions.Children.Add(_size);
        var move = IconButton("↕", "拖动以移动截图区域", 30);
        ToolTipService.SetToolTip(move, "拖动以移动截图区域");
        move.PointerPressed += MovePressed; move.PointerMoved += MoveMoved; move.PointerReleased += MoveReleased;
        actions.Children.Add(move);
        StyleToolbarButton(_direction, 88, "切换截图方向");
        _direction.Background = new SolidColorBrush(Color.FromArgb(255, 245, 247, 249));
        _direction.CornerRadius = new CornerRadius(16);
        _direction.Click += (_, _) => Enqueue(ScrollCaptureAction.Direction);
        actions.Children.Add(_direction);
        actions.Children.Add(Separator());
        var crop = ActionIconButton("\uE8C6", "裁剪长截图", 32);
        crop.Click += (_, _) => ToggleCropMenu(crop);
        actions.Children.Add(crop);
        StyleToolbarButton(_start, 34, "开始或停止长截图");
        _start.Background = new SolidColorBrush(Color.FromArgb(255, 255, 238, 238));
        _start.CornerRadius = new CornerRadius(16);
        actions.Children.Add(_start);
        actions.Children.Add(Separator());
        actions.Children.Add(ActionButton("\uE840", "贴图", ScrollCaptureAction.Pin));
        actions.Children.Add(ActionButton("\uE74E", "另存为 PNG", ScrollCaptureAction.Save));
        actions.Children.Add(IconButton("↓", "快速保存到截图目录", 32, ScrollCaptureAction.QuickSave));
        actions.Children.Add(ActionButton("\uE8C8", "复制到剪贴板并关闭", ScrollCaptureAction.Copy));
        actions.Children.Add(ActionButton("\uE8BB", "关闭长截图", ScrollCaptureAction.Cancel));
        _start.Click += (_, _) => Enqueue(ScrollCaptureAction.StartStop);
        Content = root;
        SetRunning(false, false);
        SetSelection(selection);
        SetProgress(selection.Width, selection.Height, 1, "正在准备长截图…");
        Closed += (_, _) =>
        {
            _closed = true; Enqueue(ScrollCaptureAction.Cancel);
            _preview?.Close(); _cropMenu?.Close(); _notice?.Close();
        };
        Activate();
        Exclude(this);
    }

    private static TextBlock Glyph(string text, double size) => new()
    {
        Text = text, FontSize = size, Foreground = new SolidColorBrush(Ink),
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
    };
    private static void StyleToolbarButton(Button button, double width, string name)
    {
        button.Width = width; button.Height = 32;
        button.MinWidth = 0; button.MinHeight = 0;
        button.Padding = new Thickness(0);
        button.Background = new SolidColorBrush(Colors.Transparent);
        button.BorderThickness = new Thickness(0);
        button.CornerRadius = new CornerRadius(5);
        button.Foreground = new SolidColorBrush(Ink);
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
    }
    private static Border Separator() => new()
    {
        Width = 1, Height = 21, Margin = new Thickness(3, 0, 3, 0),
        Background = new SolidColorBrush(Color.FromArgb(255, 224, 228, 234)),
        VerticalAlignment = VerticalAlignment.Center
    };
    private Button IconButton(string symbol, string name, double width, ScrollCaptureAction? action = null)
    {
        var button = new Button { Content = Glyph(symbol, 19) };
        StyleToolbarButton(button, width, name);
        if (action is ScrollCaptureAction value) button.Click += (_, _) => Enqueue(value);
        return button;
    }
    private Button ActionButton(string glyph, string name, ScrollCaptureAction action)
    {
        var button = ActionIconButton(glyph, name, 32);
        button.Click += (_, _) => Enqueue(action);
        return button;
    }
    private static Button ActionIconButton(string glyph, string name, double width)
    {
        var button = new Button
        {
            Content = new FontIcon
            {
                Glyph = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 16, Foreground = new SolidColorBrush(Ink)
            }
        };
        StyleToolbarButton(button, width, name);
        return button;
    }
    private StackPanel CreateCropPanel()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 2,
            RequestedTheme = ElementTheme.Light,
            VerticalAlignment = VerticalAlignment.Center
        };
        StyleToolbarButton(_trimStart, 28, "裁剪起点 50 像素");
        StyleToolbarButton(_trimEnd, 28, "裁剪终点 50 像素");
        _trimStart.Content = Glyph("↥", 17);
        _trimEnd.Content = Glyph("↧", 17);
        _trimStart.Click += (_, _) => { Enqueue(ScrollCaptureAction.TrimLeading); HideCropMenu(); };
        _trimEnd.Click += (_, _) => { Enqueue(ScrollCaptureAction.TrimTrailing); HideCropMenu(); };
        panel.Children.Add(_trimStart);
        panel.Children.Add(_trimEnd);
        var reset = IconButton("↶", "重置裁剪", 28);
        reset.Click += (_, _) => { Enqueue(ScrollCaptureAction.ResetTrim); HideCropMenu(); };
        panel.Children.Add(reset);
        panel.Children.Add(Separator());
        ToolTipService.SetToolTip(_autoCrop, "反向滚动时自动裁去已拼接图像的末尾");
        panel.Children.Add(_autoCrop);
        return panel;
    }
    private void ToggleCropMenu(Button anchor)
    {
        if (_cropMenuVisible) { HideCropMenu(); return; }
        if (_cropMenu is null)
        {
            var menu = new Window { Title = "Starshot 长截图裁剪" };
            menu.AppWindow.IsShownInSwitchers = false;
            if (menu.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }
            menu.Content = new Border
            {
                RequestedTheme = ElementTheme.Light,
                Width = 210, Height = 64,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Colors.White),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 224, 228, 234)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 5, 8, 5), Child = CreateCropPanel()
            };
            menu.Closed += (_, _) => { _cropMenu = null; _cropMenuVisible = false; };
            _cropMenu = menu;
        }
        PositionCropMenu(anchor);
        _cropMenu.AppWindow.Show();
        _cropMenu.Activate();
        Exclude(_cropMenu);
        _cropMenuVisible = true;
    }
    private void HideCropMenu()
    {
        if (_cropMenu is null || !_cropMenuVisible) return;
        _cropMenu.AppWindow.Hide();
        _cropMenuVisible = false;
    }
    private void PositionCropMenu(Button anchor)
    {
        if (_cropMenu is null || Content is not UIElement root) return;
        double scale = GetDpiForWindow(Hwnd) / 96.0;
        var anchorPoint = anchor.TransformToVisual(root).TransformPoint(new Point(0, anchor.ActualHeight));
        int width = (int)Math.Ceiling(262 * scale);
        int height = (int)Math.Ceiling(72 * scale);
        int visibleWidth = (int)Math.Ceiling(210 * scale);
        int visibleHeight = (int)Math.Ceiling(64 * scale);
        int x = Math.Clamp(AppWindow.Position.X + (int)Math.Round(anchorPoint.X * scale),
            _desktop.X, _desktop.X + Math.Max(0, _desktop.Width - visibleWidth));
        int below = AppWindow.Position.Y + AppWindow.Size.Height + (int)Math.Ceiling(4 * scale);
        int y = below + visibleHeight <= _desktop.Y + _desktop.Height
            ? below : Math.Max(_desktop.Y, AppWindow.Position.Y - visibleHeight - 4);
        _cropMenu.AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        nint region = CreateRoundRectRgn(0, 0, visibleWidth, visibleHeight,
            (int)Math.Ceiling(16 * scale), (int)Math.Ceiling(16 * scale));
        if (region != 0 && SetWindowRgn(WinRT.Interop.WindowNative.GetWindowHandle(_cropMenu), region, true) == 0)
            DeleteObject(region);
    }
    private void Enqueue(ScrollCaptureAction action, int x = 0, int y = 0) =>
        _commands.Enqueue(new ScrollCaptureCommand(action, x, y));
    public void RequestSelection(RectInt32 selection) => _commands.Enqueue(
        new ScrollCaptureCommand(ScrollCaptureAction.SetSelection,
            selection.X, selection.Y, selection.Width, selection.Height));
    public bool TryDequeue(out ScrollCaptureCommand command)
    {
        if (_commands.Count > 0) { command = _commands.Dequeue(); return true; }
        command = default; return false;
    }
    public bool IsClosed => _closed;
    public bool HasPreview => _previewImage is not null;
    public bool AutoCropEnabled => _autoCrop.IsChecked == true;
    public nint Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    public void SetSelection(RectInt32 selection)
    {
        _selection = selection;
        // AppWindow coordinates are physical pixels; XAML controls are sized in DIPs.
        // Size the window for every row at the DPI of the selected monitor.
        uint dpi = GetDpiForWindow(Hwnd);
        MoveControlWindow(selection, dpi);
        uint targetDpi = GetDpiForWindow(Hwnd);
        if (targetDpi != dpi) MoveControlWindow(selection, targetDpi);
        PositionPreview();
        PositionNotice();
        if (_cropMenuVisible && Content is Border { Child: StackPanel row } && row.Children.Count > 6 &&
            row.Children[6] is Button crop) PositionCropMenu(crop);
    }

    private void MoveControlWindow(RectInt32 selection, uint dpi)
    {
        double scale = (dpi == 0 ? 96 : dpi) / 96.0;
        int width = (int)Math.Ceiling(ToolbarWidthDip * scale);
        int height = (int)Math.Ceiling(ToolbarHeightDip * scale);
        int x = Math.Clamp(selection.X + selection.Width - width,
            _desktop.X, _desktop.X + Math.Max(0, _desktop.Width - width));
        int below = selection.Y + selection.Height + 8;
        int y = below + height <= _desktop.Y + _desktop.Height ? below
            : Math.Max(_desktop.Y, selection.Y - height - 8);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        nint region = CreateRoundRectRgn(0, 0, width, height,
            (int)Math.Ceiling(18 * scale), (int)Math.Ceiling(18 * scale));
        if (region != 0 && SetWindowRgn(Hwnd, region, true) == 0) DeleteObject(region);
    }

    public void SetRunning(bool running, bool horizontal)
    {
        _running = running;
        _everStarted |= running;
        _start.Content = Glyph(running ? "■" : "▶", running ? 12 : 16);
        ((TextBlock)_start.Content).Foreground = new SolidColorBrush(Color.FromArgb(255, 224, 53, 58));
        AutomationProperties.SetName(_start, running ? "停止长截图" : _everStarted ? "继续长截图" : "开始长截图");
        _direction.Content = Glyph(horizontal ? "横向  ⌄" : "垂直  ⌄", 12);
        _direction.IsEnabled = _trimStart.IsEnabled = _trimEnd.IsEnabled = !running;
        if (running) { HideCropMenu(); ShowPreview(); }
        else if (_preview is not null) _preview.Close();
    }
    public void SetProgress(int width, int height, int frames, string message)
    {
        _size.Text = $"{width} × {height}";
        _status.Text = message;
        ToolTipService.SetToolTip(_size, $"{frames} 帧 · {message}");
        if (_previewText is not null) _previewText.Text = message;
    }

    public void ClearNotice()
    {
        if (!_noticeVisible || _notice is null) return;
        _notice.AppWindow.Hide();
        _noticeVisible = false;
    }

    public void SetNotice(string message)
    {
        _status.Text = message;
        ToolTipService.SetToolTip(_status, message);
        if (_notice is null)
        {
            var window = new Window { Title = "Starshot 长截图提示" };
            window.AppWindow.IsShownInSwitchers = false;
            if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }
            _status.Width = 250;
            _status.FontSize = 12;
            _status.Foreground = new SolidColorBrush(Ink);
            _status.TextTrimming = TextTrimming.CharacterEllipsis;
            _status.VerticalAlignment = VerticalAlignment.Center;
            window.Content = new Border
            {
                RequestedTheme = ElementTheme.Light,
                Width = 272, Height = 38,
                Background = new SolidColorBrush(Colors.White),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 224, 228, 234)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 3, 10, 3), Child = _status
            };
            window.Closed += (_, _) => { _notice = null; _noticeVisible = false; };
            _notice = window;
            Exclude(window);
        }
        PositionNotice();
        ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(_notice), 8); // SW_SHOWNA.
        _noticeVisible = true;
    }

    private void PositionNotice()
    {
        if (_notice is null) return;
        double scale = GetDpiForWindow(Hwnd) / 96.0;
        int width = (int)Math.Ceiling(272 * scale);
        int height = (int)Math.Ceiling(38 * scale);
        int x = Math.Clamp(AppWindow.Position.X, _desktop.X,
            _desktop.X + Math.Max(0, _desktop.Width - width));
        int below = AppWindow.Position.Y + AppWindow.Size.Height + 5;
        int y = below + height <= _desktop.Y + _desktop.Height ? below
            : Math.Max(_desktop.Y, AppWindow.Position.Y - height - 5);
        _notice.AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        nint region = CreateRoundRectRgn(0, 0, width, height,
            (int)Math.Ceiling(14 * scale), (int)Math.Ceiling(14 * scale));
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_notice);
        if (region != 0 && SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
    }

    private void MovePressed(object sender, PointerRoutedEventArgs e)
    {
        if (!GetCursorPos(out _lastCursor)) return;
        _dragging = true; ((UIElement)sender).CapturePointer(e.Pointer); e.Handled = true;
    }
    private void MoveMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || !GetCursorPos(out var current)) return;
        int dx = current.X - _lastCursor.X, dy = current.Y - _lastCursor.Y;
        if (dx != 0 || dy != 0) Enqueue(ScrollCaptureAction.Move, dx, dy);
        _lastCursor = current; e.Handled = true;
    }
    private void MoveReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false; ((UIElement)sender).ReleasePointerCapture(e.Pointer); e.Handled = true;
    }

    private void ShowPreview()
    {
        if (_preview is not null) { _preview.AppWindow.Show(); return; }
        var window = new Window { Title = "Starshot 长截图预览" };
        window.AppWindow.IsShownInSwitchers = false;
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true; presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }
        var panel = new StackPanel
        {
            Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center
        };
        _previewText = new TextBlock
        {
            Text = "等待滚动", FontSize = 12, Width = 184,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(MutedInk)
        };
        _previewImage = new Image { Stretch = Stretch.Fill };
        _currentFrameMarker = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 95, 230, 112)),
            BorderThickness = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false
        };
        _previewCanvas = new Grid
        {
            Width = 184, Height = 140,
            Background = new SolidColorBrush(Color.FromArgb(255, 245, 247, 250))
        };
        _previewCanvas.Children.Add(_previewImage);
        _previewCanvas.Children.Add(_currentFrameMarker);
        panel.Children.Add(_previewText); panel.Children.Add(_previewCanvas);
        window.Content = new Border
        {
            RequestedTheme = ElementTheme.Light,
            Width = 210,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Colors.White),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 49, 137, 255)),
            BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8), Child = panel
        };
        window.Closed += (_, _) =>
        {
            _preview = null; _previewImage = null; _previewText = null;
            _previewCanvas = null; _currentFrameMarker = null;
        };
        _preview = window; PositionPreview(); window.Activate(); Exclude(window);
    }
    private void PositionPreview()
    {
        if (_preview is null) return;
        uint dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(_preview));
        MovePreviewWindow(dpi);
        uint targetDpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(_preview));
        if (targetDpi != dpi) MovePreviewWindow(targetDpi);
    }
    private void MovePreviewWindow(uint dpi)
    {
        if (_preview is null) return;
        double scale = (dpi == 0 ? 96 : dpi) / 96.0;
        int previewWidth = (int)Math.Ceiling(262 * scale);
        int visibleWidth = (int)Math.Ceiling(210 * scale);
        int visibleHeight = (int)Math.Ceiling(
            Math.Clamp((_previewCanvas?.Height ?? 140) + 42, 120, 452) * scale);
        int x = _selection.X + _selection.Width + 10;
        if (x + visibleWidth > _desktop.X + _desktop.Width)
            x = Math.Max(_desktop.X, _selection.X - visibleWidth - 10);
        int y = Math.Clamp(_selection.Y, _desktop.Y,
            _desktop.Y + Math.Max(0, _desktop.Height - visibleHeight));
        _preview.AppWindow.MoveAndResize(new RectInt32(x, y, previewWidth, visibleHeight));
        nint region = CreateRoundRectRgn(0, 0, visibleWidth, visibleHeight,
            (int)Math.Ceiling(6 * scale), (int)Math.Ceiling(6 * scale));
        if (region != 0 && SetWindowRgn(WinRT.Interop.WindowNative.GetWindowHandle(_preview), region, true) == 0)
            DeleteObject(region);
    }
    public async Task UpdatePreviewAsync(CanvasRenderTarget image,
        int viewportWidth, int viewportHeight, bool horizontal, int travel)
    {
        if (_previewImage is null) return;
        double scale = Math.Min(184.0 / image.SizeInPixels.Width, 400.0 / image.SizeInPixels.Height);
        int width = Math.Max(1, (int)Math.Round(image.SizeInPixels.Width * scale));
        int height = Math.Max(1, (int)Math.Round(image.SizeInPixels.Height * scale));
        if (_previewCanvas is not null) { _previewCanvas.Width = width; _previewCanvas.Height = height; }
        PositionPreview();
        if (_currentFrameMarker is not null)
        {
            _currentFrameMarker.HorizontalAlignment = horizontal
                ? travel < 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right
                : HorizontalAlignment.Stretch;
            _currentFrameMarker.VerticalAlignment = horizontal
                ? VerticalAlignment.Stretch
                : travel < 0 ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            _currentFrameMarker.Width = horizontal
                ? Math.Min(width, viewportWidth * scale) : width;
            _currentFrameMarker.Height = horizontal
                ? height : Math.Min(height, viewportHeight * scale);
        }
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
        using (var ds = target.CreateDrawingSession()) ds.DrawImage(image, new Rect(0, 0, width, height));
        using var stream = new InMemoryRandomAccessStream();
        await target.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        if (_previewImage is not null) _previewImage.Source = bitmap;
    }
    public void CloseIfOpen() { if (!_closed) Close(); }
}

/// <summary>A click-through shade keeps the selected screen content bright while scrolling.</summary>
internal sealed class ScrollingCaptureShadeWindow : IDisposable
{
    private const string WindowClassName = "StarshotLongCaptureShade";
    private const uint ExStyle = 0x00080000 | 0x00000020 | 0x00000080 | 0x00000008 | 0x08000000;
    private const uint Popup = 0x80000000;
    private static readonly WindowProc WindowProcedure = DefWindowProcW;
    private static readonly ushort ClassAtom = RegisterShadeClass();
    private readonly RectInt32 _desktop;
    private nint _hwnd;
    private bool _shown;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public WindowProc Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
        public nint SmallIcon;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClass windowClass);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title,
        uint style, int x, int y, int width, int height, nint parent, nint menu,
        nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd,
        uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey,
        byte alpha, uint flags);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int CombineRgn(nint destination, nint source1, nint source2, int mode);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);

    private static ushort RegisterShadeClass()
    {
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Procedure = WindowProcedure,
            Instance = GetModuleHandleW(null),
            Background = CreateSolidBrush(0),
            ClassName = WindowClassName
        };
        return RegisterClassExW(ref windowClass);
    }
    public ScrollingCaptureShadeWindow(RectInt32 desktop)
    {
        _desktop = desktop;
        if (ClassAtom == 0) return;
        _hwnd = CreateWindowExW(ExStyle, WindowClassName, "", Popup,
            desktop.X, desktop.Y, desktop.Width, desktop.Height,
            0, 0, GetModuleHandleW(null), 0);
        if (_hwnd == 0)
        {
            Serilog.Log.Warning("Long capture shade creation failed: {Error}", Marshal.GetLastPInvokeError());
            return;
        }
        SetLayeredWindowAttributes(_hwnd, 0, 105, 0x2);
    }
    public void Move(RectInt32 selection)
    {
        if (_hwnd == 0) return;
        nint outside = CreateRectRgn(0, 0, _desktop.Width, _desktop.Height);
        nint inside = CreateRectRgn(selection.X - _desktop.X,
            selection.Y - _desktop.Y,
            selection.X - _desktop.X + selection.Width,
            selection.Y - _desktop.Y + selection.Height);
        if (outside == 0 || inside == 0)
        {
            if (outside != 0) DeleteObject(outside);
            if (inside != 0) DeleteObject(inside);
            return;
        }
        CombineRgn(outside, outside, inside, 4); // RGN_DIFF: shade everything except the selection.
        DeleteObject(inside);
        if (SetWindowRgn(_hwnd, outside, true) == 0) DeleteObject(outside);
        else if (!_shown)
        {
            ShowWindow(_hwnd, 8); // SW_SHOWNA: no focus change.
            UpdateWindow(_hwnd);
            _shown = true;
        }
    }
    public void Dispose()
    {
        if (_hwnd != 0) { DestroyWindow(_hwnd); _hwnd = 0; }
    }
}

/// <summary>Blue outline and draggable resize handles without covering the captured interior.</summary>
internal sealed class ScrollingCaptureFrameWindow
{
    private readonly ScrollingCaptureShadeWindow _shade;
    private readonly RectInt32 _desktop;
    private RectInt32 _selection, _selectionAtDrag;
    private NativePoint _dragStart;
    private DragHandle _dragHandle;
    private nint _hwnd;
    private bool _shown;
    private const string WindowClassName = "StarshotLongCaptureOutline";
    private const uint ExStyle = 0x00080000 | 0x00000080 | 0x00000008 | 0x08000000;
    private static readonly Dictionary<nint, ScrollingCaptureFrameWindow> Instances = new();
    private static readonly WindowProc WindowProcedure = Dispatch;
    private static readonly ushort ClassAtom = RegisterOutlineClass();
    private enum DragHandle
    {
        None, Move, TopLeft, Top, TopRight, Right,
        BottomRight, Bottom, BottomLeft, Left
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public WindowProc Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
        public nint SmallIcon;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClass windowClass);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title,
        uint style, int x, int y, int width, int height, nint parent, nint menu,
        nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd,
        uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hwnd, nint after,
        int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey,
        byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateEllipticRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int CombineRgn(nint destination, nint source1, nint source2, int mode);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);

    public event Action<RectInt32>? SelectionRequested;
    public bool CanResize { get; set; } = true;
    public bool IsDragging => _dragHandle != DragHandle.None;

    private static ushort RegisterOutlineClass()
    {
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Procedure = WindowProcedure,
            Instance = GetModuleHandleW(null),
            Background = CreateSolidBrush(0x00FF8931), // RGB #3189FF in COLORREF order.
            ClassName = WindowClassName
        };
        return RegisterClassExW(ref windowClass);
    }
    private static nint Dispatch(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (Instances.TryGetValue(hwnd, out var window))
            return window.HandleMessage(message, wParam, lParam);
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }
    private nint HandleMessage(uint message, nuint wParam, nint lParam)
    {
        if (message == 0x0201 && CanResize && GetCursorPos(out var start)) // WM_LBUTTONDOWN
        {
            _dragStart = start;
            _selectionAtDrag = _selection;
            _dragHandle = HitHandle(start);
            SetCapture(_hwnd);
            return 0;
        }
        if (message == 0x0200 && _dragHandle != DragHandle.None && // WM_MOUSEMOVE
            GetCursorPos(out var current))
        {
            SelectionRequested?.Invoke(DragTo(current));
            return 0;
        }
        if (message == 0x0202 && _dragHandle != DragHandle.None) // WM_LBUTTONUP
        {
            _dragHandle = DragHandle.None;
            ReleaseCapture();
            return 0;
        }
        if (message == 0x0215) _dragHandle = DragHandle.None; // WM_CAPTURECHANGED
        return DefWindowProcW(_hwnd, message, wParam, lParam);
    }
    private DragHandle HitHandle(NativePoint point)
    {
        bool left = Math.Abs(point.X - _selection.X) <= 14;
        bool right = Math.Abs(point.X - (_selection.X + _selection.Width)) <= 14;
        bool top = Math.Abs(point.Y - _selection.Y) <= 14;
        bool bottom = Math.Abs(point.Y - (_selection.Y + _selection.Height)) <= 14;
        if (top && left) return DragHandle.TopLeft;
        if (top && right) return DragHandle.TopRight;
        if (bottom && left) return DragHandle.BottomLeft;
        if (bottom && right) return DragHandle.BottomRight;
        if (top) return DragHandle.Top;
        if (bottom) return DragHandle.Bottom;
        if (left) return DragHandle.Left;
        if (right) return DragHandle.Right;
        return DragHandle.Move;
    }
    private RectInt32 DragTo(NativePoint point)
    {
        int dx = point.X - _dragStart.X, dy = point.Y - _dragStart.Y;
        int left = _selectionAtDrag.X, top = _selectionAtDrag.Y;
        int right = left + _selectionAtDrag.Width, bottom = top + _selectionAtDrag.Height;
        if (_dragHandle == DragHandle.Move)
            return new RectInt32(
                Math.Clamp(left + dx, _desktop.X, _desktop.X + _desktop.Width - _selectionAtDrag.Width),
                Math.Clamp(top + dy, _desktop.Y, _desktop.Y + _desktop.Height - _selectionAtDrag.Height),
                _selectionAtDrag.Width, _selectionAtDrag.Height);
        if (_dragHandle is DragHandle.TopLeft or DragHandle.Left or DragHandle.BottomLeft)
            left = Math.Clamp(left + dx, _desktop.X, right - 64);
        if (_dragHandle is DragHandle.TopRight or DragHandle.Right or DragHandle.BottomRight)
            right = Math.Clamp(right + dx, left + 64, _desktop.X + _desktop.Width);
        if (_dragHandle is DragHandle.TopLeft or DragHandle.Top or DragHandle.TopRight)
            top = Math.Clamp(top + dy, _desktop.Y, bottom - 64);
        if (_dragHandle is DragHandle.BottomLeft or DragHandle.Bottom or DragHandle.BottomRight)
            bottom = Math.Clamp(bottom + dy, top + 64, _desktop.Y + _desktop.Height);
        return new RectInt32(left, top, right - left, bottom - top);
    }

    public ScrollingCaptureFrameWindow(RectInt32 selection, RectInt32 desktop)
    {
        _desktop = desktop;
        _shade = new ScrollingCaptureShadeWindow(desktop);
        if (ClassAtom != 0)
        {
            _hwnd = CreateWindowExW(ExStyle, WindowClassName, "Starshot 长截图区域",
                0x80000000, selection.X - 7, selection.Y - 7,
                selection.Width + 14, selection.Height + 14, 0, 0,
                GetModuleHandleW(null), 0);
            if (_hwnd != 0)
            {
                Instances[_hwnd] = this;
                SetLayeredWindowAttributes(_hwnd, 0, 255, 0x2);
                if (!SetWindowDisplayAffinity(_hwnd, 0x11))
                    Serilog.Log.Warning("Long capture outline exclusion failed: {Error}",
                        Marshal.GetLastPInvokeError());
            }
        }
        Move(selection);
    }
    public void Move(RectInt32 selection)
    {
        _selection = selection;
        _shade.Move(selection);
        if (_hwnd == 0) return;
        SetWindowPos(_hwnd, (nint)(-1), selection.X - 7, selection.Y - 7,
            selection.Width + 14, selection.Height + 14, 0x0010); // SWP_NOACTIVATE
        nint region = BuildOutlineRegion(selection.Width, selection.Height);
        if (region == 0) return;
        if (SetWindowRgn(_hwnd, region, true) == 0) DeleteObject(region);
        else if (!_shown)
        {
            ShowWindow(_hwnd, 8); // SW_SHOWNA
            UpdateWindow(_hwnd);
            _shown = true;
        }
    }
    private static nint BuildOutlineRegion(int width, int height)
    {
        nint region = CreateRectRgn(0, 0, 0, 0);
        if (region == 0) return 0;
        void Add(nint part)
        {
            if (part == 0) return;
            CombineRgn(region, region, part, 2); // RGN_OR
            DeleteObject(part);
        }
        Add(CreateRectRgn(7, 5, width + 7, 7));
        Add(CreateRectRgn(7, height + 7, width + 7, height + 9));
        Add(CreateRectRgn(5, 7, 7, height + 7));
        Add(CreateRectRgn(width + 7, 7, width + 9, height + 7));
        int[] xs = [7, width / 2 + 7, width + 7];
        int[] ys = [7, height / 2 + 7, height + 7];
        foreach (int y in ys)
            foreach (int x in xs)
                if (x != xs[1] || y != ys[1])
                    Add(CreateEllipticRgn(x - 5, y - 5, x + 5, y + 5));
        return region;
    }
    public void Close()
    {
        if (_hwnd != 0)
        {
            if (_dragHandle != DragHandle.None) ReleaseCapture();
            Instances.Remove(_hwnd);
            DestroyWindow(_hwnd);
            _hwnd = 0;
        }
        _shade.Dispose();
    }
}
