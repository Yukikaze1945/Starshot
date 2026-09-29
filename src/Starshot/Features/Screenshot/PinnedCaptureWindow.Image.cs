using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Starshot.Helpers;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace Starshot.Features.Screenshot;

public sealed partial class PinnedCaptureWindow
{
    private enum PinTool { Pen, Rectangle, Arrow }
    private sealed class Stroke
    {
        public required PinTool Tool { get; init; }
        public List<Vector2> Points { get; } = new();
    }
    private readonly List<Stroke> _strokes = new();
    private Stroke? _activeStroke;
    private PinTool _tool = PinTool.Pen;

    private void RebuildImage()
    {
        _imageWidth = _rotation % 2 == 0 ? _originalWidth : _originalHeight;
        _imageHeight = _rotation % 2 == 0 ? _originalHeight : _originalWidth;
        int stride = _imageWidth * 4;
        byte[] pixels = new byte[checked(stride * _imageHeight)];
        for (int y = 0; y < _imageHeight; y++)
        for (int x = 0; x < _imageWidth; x++)
        {
            int tx = _flipHorizontal ? _imageWidth - 1 - x : x;
            int ty = _flipVertical ? _imageHeight - 1 - y : y;
            (int sx, int sy) = _rotation switch
            {
                1 => (ty, _originalHeight - 1 - tx),
                2 => (_originalWidth - 1 - tx, _originalHeight - 1 - ty),
                3 => (_originalWidth - 1 - ty, tx),
                _ => (tx, ty),
            };
            int input = (sy * _originalWidth + sx) * 4;
            int output = y * stride + x * 4;
            int b = _originalPixels[input], g = _originalPixels[input + 1], r = _originalPixels[input + 2];
            if (_grayscale) { int gray = (r * 77 + g * 150 + b * 29) >> 8; r = g = b = gray; }
            if (_inverted) { r = 255 - r; g = 255 - g; b = 255 - b; }
            if (_brightness != 0)
            {
                r = Math.Clamp(r + _brightness, 0, 255);
                g = Math.Clamp(g + _brightness, 0, 255);
                b = Math.Clamp(b + _brightness, 0, 255);
            }
            pixels[output] = (byte)b; pixels[output + 1] = (byte)g;
            pixels[output + 2] = (byte)r; pixels[output + 3] = _originalPixels[input + 3];
        }
        var device = CanvasDevice.GetSharedDevice();
        var nextBitmap = CanvasBitmap.CreateFromBytes(device, pixels, _imageWidth, _imageHeight,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);
        var oldBitmap = _baseBitmap;
        _baseBitmap = nextBitmap;
        oldBitmap?.Dispose();
        if (_rendered is null || _rendered.SizeInPixels.Width != _imageWidth
            || _rendered.SizeInPixels.Height != _imageHeight)
        {
            var old = _rendered;
            _rendered = new CanvasRenderTarget(device, _imageWidth, _imageHeight, 96,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
            old?.Dispose();
        }
        if (_cropped)
        {
            _cropX = Math.Clamp(_cropX, 0, _imageWidth - 1);
            _cropY = Math.Clamp(_cropY, 0, _imageHeight - 1);
            _cropWidth = Math.Clamp(_cropWidth, 1, _imageWidth - _cropX);
            _cropHeight = Math.Clamp(_cropHeight, 1, _imageHeight - _cropY);
        }
        _thumbX = Math.Clamp(_thumbX, 0, Math.Max(0, CropWidth - VisibleWidth));
        _thumbY = Math.Clamp(_thumbY, 0, Math.Max(0, CropHeight - VisibleHeight));
        RedrawAnnotations();
    }

    private void RedrawAnnotations()
    {
        if (_rendered is null || _baseBitmap is null) return;
        using (var ds = _rendered.CreateDrawingSession())
        {
            ds.Clear(Colors.Transparent);
            ds.DrawImage(_baseBitmap);
            if (_showAnnotations)
            {
                foreach (Stroke stroke in _strokes) DrawStroke(ds, stroke);
                if (_activeStroke is not null) DrawStroke(ds, _activeStroke);
            }
        }
        Redraw();
    }

    private static void DrawStroke(CanvasDrawingSession ds, Stroke stroke)
    {
        if (stroke.Points.Count == 0) return;
        Color color = Color.FromArgb(255, 255, 64, 77);
        if (stroke.Tool == PinTool.Pen)
        {
            for (int i = 1; i < stroke.Points.Count; i++)
                ds.DrawLine(stroke.Points[i - 1], stroke.Points[i], color, 3);
            return;
        }
        Vector2 start = stroke.Points[0], end = stroke.Points[^1];
        if (stroke.Tool == PinTool.Rectangle)
        {
            ds.DrawRectangle(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
                Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y), color, 3);
            return;
        }
        ds.DrawLine(start, end, color, 3);
        Vector2 delta = end - start;
        if (delta.LengthSquared() < 16) return;
        Vector2 unit = Vector2.Normalize(delta);
        Vector2 perpendicular = new(-unit.Y, unit.X);
        ds.DrawLine(end, end - unit * 14 + perpendicular * 6, color, 3);
        ds.DrawLine(end, end - unit * 14 - perpendicular * 6, color, 3);
    }

    private Vector2 ImagePoint(PointerRoutedEventArgs e)
    {
        float dpi = Vanara.PInvoke.User32.GetDpiForWindow(new Vanara.PInvoke.HWND(WindowHandle)) / 96f;
        var point = e.GetCurrentPoint(_panel).Position;
        return new Vector2((float)(_cropX + _thumbX + point.X * dpi / _zoom),
            (float)(_cropY + _thumbY + point.Y * dpi / _zoom));
    }

    private void StartAnnotation(PointerRoutedEventArgs e)
    {
        _activeStroke = new Stroke { Tool = _tool };
        _activeStroke.Points.Add(ImagePoint(e));
        _panel.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ExtendAnnotation(PointerRoutedEventArgs e)
    {
        if (_activeStroke is null) return;
        Vector2 point = ImagePoint(e);
        if (_activeStroke.Tool != PinTool.Pen && _activeStroke.Points.Count > 1)
            _activeStroke.Points[^1] = point;
        else _activeStroke.Points.Add(point);
        RedrawAnnotations();
        e.Handled = true;
    }

    private void FinishAnnotation(PointerRoutedEventArgs e)
    {
        if (_activeStroke is null) return;
        _activeStroke.Points.Add(ImagePoint(e));
        _strokes.Add(_activeStroke);
        _activeStroke = null;
        _panel.ReleasePointerCapture(e.Pointer);
        RedrawAnnotations();
        e.Handled = true;
    }

    private void BuildAnnotationBar()
    {
        foreach (var (label, tool) in new[]
        {
            ("画笔", PinTool.Pen), ("矩形", PinTool.Rectangle), ("箭头", PinTool.Arrow),
        })
        {
            var button = new Button { Content = label, MinWidth = 45 };
            button.Click += (_, _) => _tool = tool;
            _annotationBar.Children.Add(button);
        }
        var undo = new Button { Content = "撤销" };
        undo.Click += (_, _) => UndoAnnotation();
        _annotationBar.Children.Add(undo);
        var done = new Button { Content = "完成" };
        done.Click += (_, _) => ToggleAnnotation();
        _annotationBar.Children.Add(done);
    }

    private void ToggleAnnotation()
    {
        if (_locked) return;
        _annotating = !_annotating;
        _annotationBar.Visibility = _annotating ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UndoAnnotation()
    {
        if (_strokes.Count == 0) return;
        _strokes.RemoveAt(_strokes.Count - 1);
        RedrawAnnotations();
    }

    private CanvasRenderTarget CreateCurrentImage(bool original = false, bool includeOpacity = true)
    {
        var device = CanvasDevice.GetSharedDevice();
        int width = original ? _originalWidth : Math.Max(1, (int)Math.Round(VisibleWidth * _zoom));
        int height = original ? _originalHeight : Math.Max(1, (int)Math.Round(VisibleHeight * _zoom));
        var target = new CanvasRenderTarget(device, width, height, 96,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        try
        {
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(Colors.Transparent);
                if (original)
                {
                    using var bitmap = CanvasBitmap.CreateFromBytes(device, _originalPixels,
                        width, height, DirectXPixelFormat.B8G8R8A8UIntNormalized);
                    ds.DrawImage(bitmap);
                }
                else if (_rendered is not null)
                    ds.DrawImage(_rendered, new Windows.Foundation.Rect(0, 0, width, height),
                        new Windows.Foundation.Rect(_cropX + _thumbX, _cropY + _thumbY,
                            VisibleWidth, VisibleHeight));
            }
            if (includeOpacity && !original && _opacity < 0.999)
            {
                byte[] pixels = target.GetPixelBytes();
                for (int i = 0; i < pixels.Length; i += 4)
                    for (int channel = 0; channel < 4; channel++)
                        pixels[i + channel] = (byte)Math.Round(pixels[i + channel] * _opacity);
                target.SetPixelBytes(pixels);
            }
            return target;
        }
        catch { target.Dispose(); throw; }
    }

    private async Task CopyImageAsync(bool original = false)
    {
        try
        {
            using var image = CreateCurrentImage(original);
            await ScreenCaptureService.CopyCaptureToClipboardAsync(image, force: true);
        }
        catch (Exception ex) { Serilog.Log.Error(ex, "Copy pinned image failed"); }
    }

    private async Task SaveImageAsync(bool original = false)
    {
        try
        {
            string? path = await FileDialogHelper.OpenSaveFileDialogAsync(WindowHandle,
                $"Starshot_Pin_{DateTime.Now:yyyyMMdd_HHmmss}", ("PNG 图像", ".png"));
            if (path is null) return;
            using var image = CreateCurrentImage(original);
            await image.SaveAsync(path, CanvasBitmapFileFormat.Png);
            Serilog.Log.Information("Pinned image saved: {Path}", path);
        }
        catch (Exception ex) { Serilog.Log.Error(ex, "Save pinned image failed"); }
    }

    private async Task RecognizeTextAsync(bool translate = false)
    {
        try
        {
            using var image = CreateCurrentImage(includeOpacity: false);
            byte[] pixels = image.GetPixelBytes();
            int width = (int)image.SizeInPixels.Width, height = (int)image.SizeInPixels.Height;
            var lines = await Task.Run(() => OcrHelper.RecognizeAsync(pixels, width, height, 1.0));
            if (lines is { Count: > 0 }) _ = new OcrResultWindow(lines, translate);
        }
        catch (Exception ex) { Serilog.Log.Error(ex, "Pinned image OCR failed"); }
    }
}
