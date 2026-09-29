using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vanara.PInvoke;
using Windows.Graphics;

namespace Starshot.Features.Screenshot;

public sealed partial class PinnedCaptureWindow
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);

    private static MenuFlyoutItem Item(string label, Action action, string? key = null)
    {
        var item = new MenuFlyoutItem { Text = label };
        if (key is not null) item.KeyboardAcceleratorTextOverride = key;
        item.Click += (_, _) => action();
        return item;
    }

    private static ToggleMenuFlyoutItem Toggle(string label, bool initial, Action<bool> action,
        string? key = null)
    {
        var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = initial };
        if (key is not null) item.KeyboardAcceleratorTextOverride = key;
        item.Click += (_, _) => action(item.IsChecked);
        return item;
    }

    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("识别文字并排版", () => _ = RecognizeTextAsync(), "Shift+C"));
        menu.Items.Add(Item("OCR 翻译", () => _ = RecognizeTextAsync(translate: true), "Ctrl+Q"));
        menu.Items.Add(new MenuFlyoutSeparator());
        var effects = new MenuFlyoutSubItem { Text = "图像处理" };
        effects.Items.Add(Item("向右旋转 90°", () => Rotate(1), "1"));
        effects.Items.Add(Item("向左旋转 90°", () => Rotate(-1), "2"));
        effects.Items.Add(Item("水平翻转", () => { _flipHorizontal = !_flipHorizontal; RebuildImage(); }, "3"));
        effects.Items.Add(Item("垂直翻转", () => { _flipVertical = !_flipVertical; RebuildImage(); }, "4"));
        effects.Items.Add(Item("灰度化", () => { _grayscale = !_grayscale; RebuildImage(); }, "5"));
        effects.Items.Add(Item("反色", () => { _inverted = !_inverted; RebuildImage(); }, "6"));
        effects.Items.Add(Item("亮度 +10%", () => { _brightness = Math.Min(255, _brightness + 26); RebuildImage(); }, "7"));
        effects.Items.Add(Item("亮度 -10%", () => { _brightness = Math.Max(-255, _brightness - 26); RebuildImage(); }, "8"));
        effects.Items.Add(Item("不透明度 +10%", () => SetOpacity(_opacity + 0.1)));
        effects.Items.Add(Item("不透明度 -10%", () => SetOpacity(_opacity - 0.1)));
        effects.Items.Add(new MenuFlyoutSeparator());
        effects.Items.Add(Item("重置图像处理", ResetProcessing, "0"));
        menu.Items.Add(effects);
        menu.Items.Add(Item("复制当前图像", () => _ = CopyImageAsync(), "Ctrl+C"));
        menu.Items.Add(Item("复制原始图像", () => _ = CopyImageAsync(original: true)));
        menu.Items.Add(Item("当前图像另存为…", () => _ = SaveImageAsync(), "Ctrl+S"));
        menu.Items.Add(Item("原始图像另存为…", () => _ = SaveImageAsync(original: true)));
        menu.Items.Add(Item("恢复完整图像", RestoreFullImage));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("标注", ToggleAnnotation, "空格"));
        var hideAnnotations = Toggle("隐藏标注", false, hidden =>
        {
            _showAnnotations = !hidden; RedrawAnnotations();
        }, "H");
        var border = Toggle("窗口边框", true, show => { _showBorder = show; UpdateBorder(); }, "Y");
        var topmost = Toggle("窗口置顶", true, SetAlwaysOnTop, "T");
        menu.Items.Add(hideAnnotations);
        menu.Items.Add(border);
        menu.Items.Add(topmost);
        menu.Items.Add(new MenuFlyoutSeparator());
        var thumbnail = Toggle("缩略图模式", false, SetThumbnail, "R");
        menu.Items.Add(thumbnail);
        menu.Items.Add(Item("设置标题", () => _ = SetTitleAsync(), "F2"));
        var locked = Toggle("锁定", false, SetLocked, "L");
        menu.Items.Add(locked);
        menu.Items.Add(Item("关闭", Close, "Esc"));
        menu.Items.Add(Item("销毁", Destroy, "Shift+Esc"));
        menu.Opening += (_, _) =>
        {
            hideAnnotations.IsChecked = !_showAnnotations;
            border.IsChecked = _showBorder;
            topmost.IsChecked = _alwaysOnTop;
            thumbnail.IsChecked = _thumbnail;
            locked.IsChecked = _locked;
        };
        return menu;
    }

    private void Rotate(int steps)
    {
        _cropped = false; _cropX = _cropY = _cropWidth = _cropHeight = 0;
        _rotation = (_rotation + steps + 4) % 4;
        _thumbX = 0; _thumbY = 0;
        RebuildImage();
        ResizePin(AppWindow.Position.X, AppWindow.Position.Y);
    }

    private void ResetProcessing()
    {
        _rotation = 0; _flipHorizontal = _flipVertical = _grayscale = _inverted = false;
        _brightness = 0; _thumbX = _thumbY = 0;
        _thumbnail = false;
        _cropped = false; _cropX = _cropY = _cropWidth = _cropHeight = 0;
        _zoom = 1;
        RebuildImage();
        SetOpacity(1);
        ResizePin(AppWindow.Position.X, AppWindow.Position.Y);
    }

    private void SetThumbnail(bool enabled)
    {
        _thumbnail = enabled;
        if (!enabled) _thumbX = _thumbY = 0;
        ResizePin(AppWindow.Position.X, AppWindow.Position.Y);
    }

    private void SetAlwaysOnTop(bool enabled)
    {
        _alwaysOnTop = enabled;
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = enabled;
    }

    private void SetLocked(bool enabled)
    {
        _locked = enabled;
        if (enabled && _annotating)
        {
            _annotating = false;
            _annotationBar.Visibility = Visibility.Collapsed;
        }
        UpdateBorder();
    }

    private void Destroy()
    {
        _destroyed = true;
        Close();
    }

    private async System.Threading.Tasks.Task SetTitleAsync()
    {
        if (Content is not FrameworkElement root) return;
        var editor = new TextBox { Text = _pinTitle, PlaceholderText = "贴图标题" };
        var dialog = new ContentDialog
        {
            Title = "设置贴图标题", Content = editor,
            PrimaryButtonText = "确定", CloseButtonText = "取消",
            XamlRoot = root.XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _pinTitle = editor.Text.Trim();
            _titleLabel.Text = _pinTitle;
            _titleHost.Visibility = _pinTitle.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            Title = _pinTitle.Length == 0 ? "Starshot Pin" : $"Starshot Pin — {_pinTitle}";
        }
    }

    protected override nint WindowSubclassProc(HWND hwnd, uint message, nint wParam, nint lParam,
        nuint id, nint data)
    {
        if (message == 0x24 && lParam != 0)
        {
            var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            limits.MinTrackSize = new NativePoint { X = 1, Y = 1 };
            Marshal.StructureToPtr(limits, lParam, false);
            return 0;
        }
        if (message == 0x100 && HandleKey((int)wParam)) return 0;
        return base.WindowSubclassProc(hwnd, message, wParam, lParam, id, data);
    }

    private bool HandleKey(int key)
    {
        bool ctrl = (GetKeyState(0x11) & 0x8000) != 0;
        bool shift = (GetKeyState(0x10) & 0x8000) != 0;
        if (key == 0x1B && shift) { Destroy(); return true; }
        if (key == 0x1B || (ctrl && key == 0x57)) { Close(); return true; }
        if (ctrl && key == 0x44) { Destroy(); return true; }
        if (ctrl && key == 0x43) { _ = CopyImageAsync(); return true; }
        if (ctrl && key == 0x53) { _ = SaveImageAsync(); return true; }
        if (shift && key == 0x43) { _ = RecognizeTextAsync(); return true; }
        if (ctrl && key == 0x51) { _ = RecognizeTextAsync(translate: true); return true; }
        if (ctrl && key == 0x5A) { UndoAnnotation(); return true; }
        if (key == 0x20) { ToggleAnnotation(); return true; }
        if (key == 0x71) { _ = SetTitleAsync(); return true; }
        if (key == 0x54) { SetAlwaysOnTop(!_alwaysOnTop); return true; }
        if (key == 0x4C) { SetLocked(!_locked); return true; }
        if (key == 0x48) { _showAnnotations = !_showAnnotations; RedrawAnnotations(); return true; }
        if (key == 0x59) { _showBorder = !_showBorder; UpdateBorder(); return true; }
        if (key == 0x52) { SetThumbnail(!_thumbnail); return true; }
        if (key == 0x31) { Rotate(1); return true; }
        if (key == 0x32) { Rotate(-1); return true; }
        if (key == 0x33) { _flipHorizontal = !_flipHorizontal; RebuildImage(); return true; }
        if (key == 0x34) { _flipVertical = !_flipVertical; RebuildImage(); return true; }
        if (key == 0x35) { _grayscale = !_grayscale; RebuildImage(); return true; }
        if (key == 0x36) { _inverted = !_inverted; RebuildImage(); return true; }
        if (key == 0x37) { _brightness = Math.Min(255, _brightness + 26); RebuildImage(); return true; }
        if (key == 0x38) { _brightness = Math.Max(-255, _brightness - 26); RebuildImage(); return true; }
        if (key == 0x30) { ResetProcessing(); return true; }
        if (!_locked && key is >= 0x25 and <= 0x28)
        {
            int step = shift ? 10 : 1;
            int dx = key == 0x25 ? -step : key == 0x27 ? step : 0;
            int dy = key == 0x26 ? -step : key == 0x28 ? step : 0;
            AppWindow.Move(new PointInt32(AppWindow.Position.X + dx, AppWindow.Position.Y + dy));
            return true;
        }
        return false;
    }
}
