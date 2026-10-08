using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Graphics.DirectX;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.UI;

namespace Starshot.Features.Screenshot;

public sealed partial class RegionCaptureWindow
{
    private enum AnnotationTool { Select, Rectangle, Ellipse, Line, Arrow, Number, Pen, Highlighter, Mosaic, Blur, Text, Eraser }

    private sealed class Annotation
    {
        public required AnnotationTool Tool { get; init; }
        public Point Start { get; set; }
        public Point End { get; set; }
        public Color Color { get; init; }
        public float Width { get; init; } = 3;
        public string Text { get; init; } = "";
        public List<Point> Points { get; } = new();
    }

    private readonly List<Annotation> _annotations = new();
    private readonly Stack<(Annotation annotation, int index, bool added)> _undoEdits = new();
    private readonly Stack<(Annotation annotation, int index, bool added)> _redoEdits = new();
    private Annotation? _draftAnnotation;
    private AnnotationTool _annotationTool = AnnotationTool.Select;
    private Point _textStart;
    private Color _annotationColor = Color.FromArgb(255, 255, 64, 77);
    private int _annotationWidth = 3;
    private static Vector2 V(Point p) => new((float)p.X, (float)p.Y);

    private void ResetAnnotations()
    {
        _annotations.Clear();
        _undoEdits.Clear();
        _redoEdits.Clear();
        _draftAnnotation = null;
        AnnotationTextEditor.Visibility = Visibility.Collapsed;
        SetAnnotationTool(AnnotationTool.Select);
    }

    private void SetAnnotationTool(AnnotationTool tool)
    {
        _annotationTool = tool;
        _toolbarState.Select(Enum.Parse<RegionToolbarCommand>(tool.ToString()));
        if (_state == RegionCaptureState.Selected) RefreshToolbar();
    }

    private void UndoAnnotation()
    {
        CommitAnnotationText();
        if (_undoEdits.Count == 0) return;
        var edit = _undoEdits.Pop();
        if (edit.added) _annotations.Remove(edit.annotation);
        else _annotations.Insert(Math.Min(edit.index, _annotations.Count), edit.annotation);
        _redoEdits.Push(edit);
        RefreshToolbarAvailability();
        RequestRedraw();
    }

    private void RedoAnnotation()
    {
        if (_redoEdits.Count == 0) return;
        var edit = _redoEdits.Pop();
        if (edit.added) _annotations.Insert(Math.Min(edit.index, _annotations.Count), edit.annotation);
        else _annotations.Remove(edit.annotation);
        _undoEdits.Push(edit);
        RefreshToolbarAvailability();
        RequestRedraw();
    }

    private bool StartAnnotation(Point position)
    {
        if (_state != RegionCaptureState.Selected || _annotationTool == AnnotationTool.Select || !SelectionRect.Contains(position))
            return false;
        if (_annotationTool == AnnotationTool.Eraser)
        {
            for (int i = _annotations.Count - 1; i >= 0; i--)
            {
                if (!AnnotationBounds(_annotations[i]).Contains(position)) continue;
                var erased = _annotations[i];
                _annotations.RemoveAt(i);
                _undoEdits.Push((erased, i, false));
                _redoEdits.Clear();
                RefreshToolbarAvailability();
                RequestRedraw();
                break;
            }
            return true;
        }
        if (_annotationTool == AnnotationTool.Text)
        {
            CommitAnnotationText();
            _textStart = position;
            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(AnnotationTextEditor,
                Math.Clamp(position.X, 0, Math.Max(0, _lockedW - 240)));
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(AnnotationTextEditor,
                Math.Clamp(position.Y, 0, Math.Max(0, _lockedH - 38)));
            AnnotationTextEditor.Text = "";
            AnnotationTextEditor.Visibility = Visibility.Visible;
            AnnotationTextEditor.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            return true;
        }
        _draftAnnotation = new Annotation
        {
            Tool = _annotationTool,
            Start = position,
            End = position,
            Color = _annotationColor,
            Width = _annotationWidth,
        };
        _draftAnnotation.Points.Add(position);
        return true;
    }

    private void UpdateAnnotation(Point position)
    {
        if (_draftAnnotation is null) return;
        position = new Point(Math.Clamp(position.X, SelectionRect.Left, SelectionRect.Right),
            Math.Clamp(position.Y, SelectionRect.Top, SelectionRect.Bottom));
        _draftAnnotation.End = position;
        if (_draftAnnotation.Tool is AnnotationTool.Pen or AnnotationTool.Highlighter or AnnotationTool.Mosaic or AnnotationTool.Blur)
            _draftAnnotation.Points.Add(position);
        RequestRedraw();
    }

    private void FinishAnnotation(Point position)
    {
        if (_draftAnnotation is null) return;
        UpdateAnnotation(position);
        if (_draftAnnotation.Tool == AnnotationTool.Number ||
            Math.Abs(_draftAnnotation.End.X - _draftAnnotation.Start.X) + Math.Abs(_draftAnnotation.End.Y - _draftAnnotation.Start.Y) >= 2)
        {
            if (_draftAnnotation.Tool == AnnotationTool.Number)
            {
                int count = 1;
                foreach (var annotation in _annotations)
                    if (annotation.Tool == AnnotationTool.Number) count++;
                _draftAnnotation = new Annotation
                {
                    Tool = AnnotationTool.Number, Start = _draftAnnotation.Start, End = _draftAnnotation.End,
                    Color = _draftAnnotation.Color, Text = count.ToString(), Width = _draftAnnotation.Width,
                };
            }
            _undoEdits.Push((_draftAnnotation, _annotations.Count, true));
            _annotations.Add(_draftAnnotation);
            _redoEdits.Clear();
        }
        _draftAnnotation = null;
        RefreshToolbarAvailability();
        RequestRedraw();
    }

    private void AnnotationTextEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CommitAnnotationText();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            AnnotationTextEditor.Text = "";
            AnnotationTextEditor.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void AnnotationTextEditor_LostFocus(object sender, RoutedEventArgs e) => CommitAnnotationText();

    private void CommitAnnotationText()
    {
        if (AnnotationTextEditor.Visibility != Visibility.Visible) return;
        string text = AnnotationTextEditor.Text.Trim();
        AnnotationTextEditor.Visibility = Visibility.Collapsed;
        if (text.Length == 0) return;
        var annotation = new Annotation { Tool = AnnotationTool.Text, Start = _textStart,
            End = new Point(_textStart.X + 240, _textStart.Y + 38), Color = _annotationColor, Text = text };
        _undoEdits.Push((annotation, _annotations.Count, true));
        _annotations.Add(annotation);
        _redoEdits.Clear();
        RefreshToolbarAvailability();
        RequestRedraw();
    }

    private static Rect AnnotationBounds(Annotation a)
    {
        double left = Math.Min(a.Start.X, a.End.X) - 12, top = Math.Min(a.Start.Y, a.End.Y) - 12;
        return new Rect(left, top, Math.Abs(a.End.X - a.Start.X) + 24, Math.Abs(a.End.Y - a.Start.Y) + 24);
    }

    private void TranslateAnnotations(double dx, double dy)
    {
        if (dx == 0 && dy == 0) return;
        foreach (Annotation annotation in _annotations)
        {
            annotation.Start = new Point(annotation.Start.X + dx, annotation.Start.Y + dy);
            annotation.End = new Point(annotation.End.X + dx, annotation.End.Y + dy);
            for (int i = 0; i < annotation.Points.Count; i++)
                annotation.Points[i] = new Point(annotation.Points[i].X + dx, annotation.Points[i].Y + dy);
        }
    }

    private void DrawAnnotations(CanvasDrawingSession ds)
    {
        if (_annotations.Count == 0 && _draftAnnotation is null) return;
        using (ds.CreateLayer(1f, SelectionRect))
        {
            foreach (var a in _annotations) DrawAnnotation(ds, a);
            if (_draftAnnotation is not null) DrawAnnotation(ds, _draftAnnotation);
        }
    }

    private Matrix3x2 AnnotationTransform(Rect physical) =>
        Matrix3x2.CreateScale((float)(_canvasOriginal.SizeInPixels.Width / _lockedW),
            (float)(_canvasOriginal.SizeInPixels.Height / _lockedH))
        * Matrix3x2.CreateTranslation(-(float)physical.X, -(float)physical.Y);

    private CanvasRenderTarget CreateAnnotationLayer(Rect physical)
    {
        var layer = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), (int)physical.Width,
            (int)physical.Height, 96, DirectXPixelFormat.B8G8R8A8UIntNormalized,
            CanvasAlphaMode.Premultiplied);
        try
        {
            using var ds = layer.CreateDrawingSession();
            ds.Clear(Colors.Transparent);
            ds.Transform = AnnotationTransform(physical);
            DrawAnnotations(ds);
            return layer;
        }
        catch
        {
            layer.Dispose();
            throw;
        }
    }

    private void DrawAnnotation(CanvasDrawingSession ds, Annotation a)
    {
        Vector2 start = V(a.Start), end = V(a.End);
        float left = Math.Min(start.X, end.X), top = Math.Min(start.Y, end.Y);
        float width = Math.Abs(end.X - start.X), height = Math.Abs(end.Y - start.Y);
        switch (a.Tool)
        {
            case AnnotationTool.Rectangle:
            ds.DrawRectangle(left, top, width, height, a.Color, a.Width);
                break;
            case AnnotationTool.Ellipse:
                ds.DrawEllipse(new Vector2(left + width / 2, top + height / 2), width / 2, height / 2, a.Color, a.Width);
                break;
            case AnnotationTool.Line:
                ds.DrawLine(start, end, a.Color, a.Width);
                break;
            case AnnotationTool.Arrow:
                ds.DrawLine(start, end, a.Color, a.Width);
                Vector2 delta = end - start;
                if (delta.Length() > 1)
                {
                    Vector2 direction = Vector2.Normalize(delta);
                    Vector2 side = new(-direction.Y, direction.X);
                    ds.DrawLine(end, end - direction * 15 + side * 7, a.Color, a.Width);
                    ds.DrawLine(end, end - direction * 15 - side * 7, a.Color, a.Width);
                }
                break;
            case AnnotationTool.Number:
                ds.FillCircle(start, 13, a.Color);
                using (var numberFormat = new CanvasTextFormat { FontSize = 16,
                    HorizontalAlignment = CanvasHorizontalAlignment.Center,
                    VerticalAlignment = CanvasVerticalAlignment.Center })
                ds.DrawText(a.Text.Length == 0 ? "1" : a.Text,
                    new Rect(a.Start.X - 13, a.Start.Y - 13, 26, 26), Colors.White, numberFormat);
                break;
            case AnnotationTool.Pen:
            case AnnotationTool.Highlighter:
                Color stroke = a.Tool == AnnotationTool.Highlighter
                    ? Color.FromArgb(110, a.Color.R, a.Color.G, a.Color.B) : a.Color;
                float thickness = a.Tool == AnnotationTool.Highlighter ? a.Width * 4 : a.Width;
                for (int i = 1; i < a.Points.Count; i++)
                    ds.DrawLine(V(a.Points[i - 1]), V(a.Points[i]), stroke, thickness);
                break;
            case AnnotationTool.Mosaic:
                DrawMosaic(ds, a);
                break;
            case AnnotationTool.Blur:
                DrawBlur(ds, a);
                break;
            case AnnotationTool.Text:
                using (var textFormat = new CanvasTextFormat { FontSize = 24 })
                ds.DrawText(a.Text, new Rect(a.Start.X, a.Start.Y, Math.Max(240, SelectionRect.Right - a.Start.X), 60),
                    a.Color, textFormat);
                break;
        }
    }

    private void DrawMosaic(CanvasDrawingSession ds, Annotation a)
    {
        // Use a coarse grid of colors sampled from the frozen frame. The overlay and exported
        // bitmap draw the same deterministic marks, with no per-pointer GPU readback.
        var visited = new HashSet<(int x, int y)>();
        foreach (Point point in a.Points)
        {
            int gx = (int)Math.Floor(point.X / 10), gy = (int)Math.Floor(point.Y / 10);
            for (int y = gy - 1; y <= gy + 1; y++)
            for (int x = gx - 1; x <= gx + 1; x++)
            {
                if (!visited.Add((x, y))) continue;
                float px = x * 10, py = y * 10;
                if (!SelectionRect.Contains(new Point(px + 5, py + 5))) continue;
                if (TrySampleColor(px + 5, py + 5, out Color color))
                    ds.FillRectangle(px, py, 10, 10, color);
            }
        }
    }

    private void DrawBlur(CanvasDrawingSession ds, Annotation a)
    {
        var visited = new HashSet<(int x, int y)>();
        foreach (Point point in a.Points)
        {
            int gx = (int)Math.Floor(point.X / 6), gy = (int)Math.Floor(point.Y / 6);
            for (int y = gy - 2; y <= gy + 2; y++)
            for (int x = gx - 2; x <= gx + 2; x++)
            {
                if (!visited.Add((x, y))) continue;
                float px = x * 6, py = y * 6;
                if (!SelectionRect.Contains(new Point(px + 3, py + 3))) continue;
                int r = 0, g = 0, b = 0, samples = 0;
                for (int sy = -12; sy <= 12; sy += 12)
                for (int sx = -12; sx <= 12; sx += 12)
                {
                    if (!TrySampleColor(px + 3 + sx, py + 3 + sy, out Color sample)) continue;
                    r += sample.R; g += sample.G; b += sample.B; samples++;
                }
                if (samples > 0)
                    ds.FillRectangle(px, py, 6, 6, Color.FromArgb(255,
                        (byte)(r / samples), (byte)(g / samples), (byte)(b / samples)));
            }
        }
    }
}
