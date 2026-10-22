using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starshot.Helpers;
using Windows.Foundation;
using Drawing = System.Drawing;

namespace Starshot.Features.Screenshot;

public sealed partial class RegionCaptureWindow
{
    private Drawing.RectangleF? _analysisManualBounds, _gamutManualBounds;
    private FrameworkElement? _analysisDragGrip;
    private Border? _analysisDragPanel;
    private Point _analysisDragStart;
    private Drawing.RectangleF _analysisDragOrigin;

    private Drawing.RectangleF ClampAnalysisPanel(Drawing.RectangleF bounds) => RegionToolbarGeometry.ClampPanel(bounds,
        RegionToolbarMonitors.Read(new(_vx, _vy, _lockedW * _scale, _lockedH * _scale), _scale));

    private void InstallAnalysisDrag(FrameworkElement grip, Border panel, bool gamut)
    {
        // A dedicated header grip leaves scrolling, sliders, expanders and close usable.
        grip.PointerPressed += (_, e) =>
        {
            if (_isClosed || !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            _analysisDragStart = e.GetCurrentPoint(Canvas).Position;
            _analysisDragOrigin = new((float)(Microsoft.UI.Xaml.Controls.Canvas.GetLeft(panel) * _scale + _vx),
                (float)(Microsoft.UI.Xaml.Controls.Canvas.GetTop(panel) * _scale + _vy), (float)(panel.Width * _scale), (float)(panel.Height * _scale));
            if (grip.CapturePointer(e.Pointer)) { _analysisDragGrip = grip; _analysisDragPanel = panel; }
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!ReferenceEquals(_analysisDragGrip, grip)) return;
            var p = e.GetCurrentPoint(Canvas).Position;
            MoveAnalysisPanel(panel, new(_analysisDragOrigin.X + (float)((p.X - _analysisDragStart.X) * _scale),
                _analysisDragOrigin.Y + (float)((p.Y - _analysisDragStart.Y) * _scale), _analysisDragOrigin.Width, _analysisDragOrigin.Height), gamut);
            e.Handled = true;
        };
        grip.PointerReleased += (_, e) => { if (ReferenceEquals(_analysisDragGrip, grip)) StopAnalysisDrag(panel); e.Handled = true; };
        grip.PointerCanceled += (_, _) => StopAnalysisDrag(panel);
        grip.PointerCaptureLost += (_, _) => { if (ReferenceEquals(_analysisDragGrip, grip)) { _analysisDragGrip = null; _analysisDragPanel = null; } };
        PointerCursor.SetCursorShape(grip, InputSystemCursorShape.SizeAll);
    }
    private void MoveAnalysisPanel(Border panel, Drawing.RectangleF bounds, bool gamut)
    {
        bounds = ClampAnalysisPanel(bounds);
        if (gamut) _gamutManualBounds = bounds; else _analysisManualBounds = bounds;
        PlaceToolbarPanel(panel, bounds);
    }
    private void StopAnalysisDrag(Border panel)
    {
        if (!ReferenceEquals(panel, _analysisDragPanel)) return;
        var grip = _analysisDragGrip; _analysisDragGrip = null; _analysisDragPanel = null;
        grip?.ReleasePointerCaptures();
    }
}
