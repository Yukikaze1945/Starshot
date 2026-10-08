using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Drawing = System.Drawing;
using XamlCanvas = Microsoft.UI.Xaml.Controls.Canvas;

namespace Starshot.Features.Screenshot;

public sealed partial class RegionCaptureWindow
{
    private readonly RegionToolbarState _toolbarState = new();
    private RegionToolbarLayout? _toolbarLayout;
    private RegionToolbarMenu? _toolbarMenu;
    private readonly List<(RegionToolbarCommand Command, Button Button)> _toolbarActionButtons = new();
    private readonly List<Button> _toolbarMenuButtons = new();
    private bool _toolbarDismissedPointer, _toolbarHelpVisible;
    private string _toolbarVisualKey = "";
    private long _toolbarDismissedUntil;
    private Windows.Foundation.Point _toolbarDismissedAt;

    private static SolidColorBrush ToolbarBrush(uint argb) => new(Color.FromArgb((byte)(argb >> 24),
        (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
    private void InitializeToolbarInteraction()
    {
        foreach (var panel in new[] { SelectionToolbar, ToolbarParameters, ToolbarPopup, ShortcutHint })
        {
            // These panels are in the existing overlay, not separate HWND/Popup windows.
            panel.PointerPressed += (_, e) => e.Handled = true;
            panel.PointerReleased += (_, e) => e.Handled = true;
            panel.DoubleTapped += (_, e) => e.Handled = true;
        }
        ShortcutHintText.Text = RegionToolbarCatalog.Help;
        ToolbarPopup.PointerWheelChanged += (_, e) =>
        {
            MoveToolbarMenuFocus(e.GetCurrentPoint(ToolbarPopup).Properties.MouseWheelDelta > 0 ? -1 : 1);
            e.Handled = true;
        };
    }

    private void HideToolbarAuxiliary()
    {
        CloseHdrAnalysis();
        _toolbarState.ClosePopup(); _toolbarHelpVisible = false; _toolbarDismissedPointer = false;
        _toolbarDismissedUntil = 0; _toolbarDismissedAt = default;
        ToolbarPopup.Visibility = ToolbarParameters.Visibility = ShortcutHint.Visibility = Visibility.Collapsed;
        _toolbarMenu = null;
        _toolbarVisualKey = "";
    }

    private Drawing.RectangleF ToolbarPhysicalSelection() => new(
        (float)(SelectionRect.X * _scale + _vx), (float)(SelectionRect.Y * _scale + _vy),
        (float)(SelectionRect.Width * _scale), (float)(SelectionRect.Height * _scale));

    private void RefreshToolbar()
    {
        if (_state != RegionCaptureState.Selected) return;
        if (_analysisCancellation is not null && GetPhysicalSourceRect() != _analysisRect) CloseHdrAnalysis();
        var monitors = RegionToolbarMonitors.Read(new(_vx, _vy, (float)(_lockedW * _scale),
            (float)(_lockedH * _scale)), (float)_scale);
        _toolbarLayout = RegionToolbarGeometry.Arrange(ToolbarPhysicalSelection(), monitors, _toolbarState, _defaultAction);
        var layout = _toolbarLayout;
        PlaceToolbarPanel(SelectionToolbar, layout.Main);
        ToolbarParameters.Visibility = layout.Parameters.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        if (!layout.Parameters.IsEmpty) PlaceToolbarPanel(ToolbarParameters, layout.Parameters);
        // Moving a selection only moves panels. Rebuild small UI elements when their content/metrics change.
        string key = $"{layout.Main.Width},{layout.Main.Height},{layout.Monitor.Scale},{_toolbarState.Tool},{_toolbarState.Color},{_toolbarState.Width},{_defaultAction}," +
            string.Join(',', layout.Slots.Select(slot => _toolbarState.Resolve(slot.Command)));
        if (_toolbarVisualKey != key)
        {
            _toolbarVisualKey = key;
            ToolbarButtons.Children.Clear(); ToolbarParameterButtons.Children.Clear(); _toolbarActionButtons.Clear();
            foreach (var slot in layout.Slots)
            {
                var command = _toolbarState.Resolve(slot.Command);
                var bounds = slot.Bounds;
                if (!slot.Chevron.IsEmpty) bounds.Width -= slot.Chevron.Width;
                var button = ToolbarButton(command, layout.Monitor.Scale, false);
                bool primary = command == RegionToolbarCatalog.Primary(_defaultAction);
                bool active = command == _toolbarState.Tool;
                button.Background = ToolbarBrush(primary ? 0xffddf369 : active ? 0xffe8ecd9 : 0x00ffffff);
                if (active) { button.BorderBrush = ToolbarBrush(0xff9db746); button.BorderThickness = new Thickness(ToolbarDip(layout.Monitor.Scale)); }
                button.Click += (_, _) => InvokeToolbar(command);
                AddToolbarChild(ToolbarButtons, button, bounds, layout.Main);
                _toolbarActionButtons.Add((command, button));
                if (!slot.Chevron.IsEmpty)
                {
                    var chevron = ToolbarButton(slot.Command, layout.Monitor.Scale, false);
                    chevron.Content = new TextBlock { Text = "⌄", FontSize = ToolbarDip(15 * layout.Monitor.Scale), VerticalAlignment = VerticalAlignment.Center };
                    ToolTipService.SetToolTip(chevron, RegionToolbarCatalog.Get(slot.Command).Label + "：展开工具");
                    AutomationProperties.SetName(chevron, RegionToolbarCatalog.Get(slot.Command).Label + "菜单");
                    chevron.Click += (_, _) => OpenToolbarMenu(slot.Command);
                    AddToolbarChild(ToolbarButtons, chevron, slot.Chevron, layout.Main);
                }
            }
            foreach (var parameter in layout.Choices)
            {
                var button = new Button { Padding = new Thickness(0), MinWidth = 0, MinHeight = 0,
                    CornerRadius = new CornerRadius(ToolbarDip(6 * layout.Monitor.Scale)),
                    Background = ToolbarBrush(0x00ffffff), BorderThickness = new Thickness(ToolbarDip(layout.Monitor.Scale)),
                    BorderBrush = ToolbarBrush((parameter.Color ? parameter.Value == _toolbarState.Color : parameter.Value == _toolbarState.Width) ? 0xff9db746 : 0x00ffffff) };
                if (parameter.Color)
                    button.Content = new Ellipse { Width = ToolbarDip(19 * layout.Monitor.Scale), Height = ToolbarDip(19 * layout.Monitor.Scale),
                        Fill = ToolbarBrush(unchecked((uint)parameter.Value)), Stroke = ToolbarBrush(0xffbac1af), StrokeThickness = 1 };
                else
                    button.Content = new Grid { Children = { new Rectangle { Width = ToolbarDip(18 * layout.Monitor.Scale),
                        Height = ToolbarDip(parameter.Value * layout.Monitor.Scale), Fill = ToolbarBrush(0xff242b24), RadiusX = 1, RadiusY = 1 } } };
                string label = parameter.Color ? $"颜色 #{parameter.Value & 0xffffff:X6}" : $"线宽 {parameter.Value}";
                ToolTipService.SetToolTip(button, label); AutomationProperties.SetName(button, label);
                button.Click += (_, _) =>
                {
                    CommitAnnotationText();
                    if (parameter.Color) { _toolbarState.Color = parameter.Value; _annotationColor = ToolbarBrush(unchecked((uint)parameter.Value)).Color; }
                    else { _toolbarState.Width = parameter.Value; _annotationWidth = parameter.Value; }
                    RefreshToolbar();
                };
                AddToolbarChild(ToolbarParameterButtons, button, parameter.Bounds, layout.Parameters);
            }
        }
        RefreshToolbarAvailability();
        RefreshToolbarMenu();
        PositionHdrAnalysis();
    }

    private double ToolbarDip(double physical) => physical / _scale;
    private void PlaceToolbarPanel(FrameworkElement panel, Drawing.RectangleF rect)
    {
        panel.Width = ToolbarDip(rect.Width); panel.Height = ToolbarDip(rect.Height);
        if (panel is Border border)
        {
            float scale = _toolbarLayout?.Monitor.Scale ?? (float)_scale;
            border.CornerRadius = new CornerRadius(ToolbarDip(10 * scale));
            border.BorderThickness = new Thickness(ToolbarDip(scale));
        }
        XamlCanvas.SetLeft(panel, ToolbarDip(rect.X - _vx)); XamlCanvas.SetTop(panel, ToolbarDip(rect.Y - _vy));
    }
    private void AddToolbarChild(XamlCanvas canvas, FrameworkElement child, Drawing.RectangleF bounds, Drawing.RectangleF parent)
    {
        child.Width = ToolbarDip(bounds.Width); child.Height = ToolbarDip(bounds.Height);
        XamlCanvas.SetLeft(child, ToolbarDip(bounds.X - parent.X)); XamlCanvas.SetTop(child, ToolbarDip(bounds.Y - parent.Y));
        canvas.Children.Add(child);
    }
    private Button ToolbarButton(RegionToolbarCommand command, float scale, bool menu)
    {
        var descriptor = RegionToolbarCatalog.Get(command);
        var text = new TextBlock { Text = descriptor.Glyph, FontSize = ToolbarDip((descriptor.Glyph.Length > 1 && !descriptor.Symbol ? 11 : 18) * scale),
            FontFamily = new FontFamily(descriptor.Symbol ? "Segoe MDL2 Assets" : "Segoe UI Variable"),
            VerticalAlignment = VerticalAlignment.Center, Width = menu ? ToolbarDip(28 * scale) : double.NaN,
            TextAlignment = TextAlignment.Center };
        object content = text;
        if (menu)
        {
            var row = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) } } };
            row.Children.Add(text);
            var label = new TextBlock { Text = descriptor.Label, FontSize = ToolbarDip(13 * scale), VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(ToolbarDip(6 * scale), 0, 0, 0) };
            Grid.SetColumn(label, 1); row.Children.Add(label); content = row;
        }
        var button = new Button { Content = content, Tag = command, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0,
            HorizontalContentAlignment = menu ? HorizontalAlignment.Stretch : HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(ToolbarDip(6 * scale)), Foreground = ToolbarBrush(0xff242b24),
            Background = ToolbarBrush(0x00ffffff), BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(button, descriptor.Label);
        ToolTipService.SetToolTip(button, descriptor.Label + (descriptor.Shortcut.Length == 0 ? "" : "  " + descriptor.Shortcut));
        return button;
    }

    private bool ToolbarCommandEnabled(RegionToolbarCommand command) => command switch
    { RegionToolbarCommand.Undo => _undoEdits.Count > 0, RegionToolbarCommand.Redo => _redoEdits.Count > 0,
      RegionToolbarCommand.HdrAnalysis => CanAnalyzeHdr(), _ => true };
    private void RefreshToolbarAvailability()
    {
        foreach (var (command, button) in _toolbarActionButtons) button.IsEnabled = ToolbarCommandEnabled(command);
        foreach (var button in _toolbarMenuButtons) button.IsEnabled = ToolbarCommandEnabled((RegionToolbarCommand)button.Tag);
    }
    private void OpenToolbarMenu(RegionToolbarCommand command)
    {
        CommitAnnotationText(); _toolbarHelpVisible = false; ShortcutHint.Visibility = Visibility.Collapsed;
        if (_toolbarState.Popup == command) _toolbarState.ClosePopup(); else _toolbarState.Open(command);
        RefreshToolbarMenu();
    }
    private void RefreshToolbarMenu()
    {
        ToolbarPopupButtons.Children.Clear(); _toolbarMenuButtons.Clear();
        if (!_toolbarState.Popup.HasValue || _toolbarLayout is null)
        { ToolbarPopup.Visibility = Visibility.Collapsed; _toolbarMenu = null; return; }
        _toolbarMenu = RegionToolbarGeometry.Menu(_toolbarLayout, _toolbarState);
        if (_toolbarMenu.Bounds.IsEmpty) { ToolbarPopup.Visibility = Visibility.Collapsed; return; }
        ToolbarPopup.Visibility = Visibility.Visible; PlaceToolbarPanel(ToolbarPopup, _toolbarMenu.Bounds);
        for (int i = 0; i < _toolbarMenu.Slots.Count; i++)
        {
            int index = _toolbarMenu.FirstIndex + i;
            var slot = _toolbarMenu.Slots[i];
            var button = ToolbarButton(slot.Command, _toolbarLayout.Monitor.Scale, true);
            button.IsEnabled = ToolbarCommandEnabled(slot.Command);
            if (slot.Command == RegionToolbarCommand.HdrAnalysis && !button.IsEnabled)
            {
                ToolTipService.SetToolTip(button, "需要原始 HDR FP16 捕获；SDR、轻量模式及混合 SDR 选区不支持绝对 nits 分析");
                ((Grid)button.Content).Children.OfType<TextBlock>().Last().Text = "HDR 分析 · 需 HDR 捕获";
            }
            button.Click += (_, _) => InvokeToolbar(slot.Command);
            button.PointerEntered += (_, _) => { _toolbarState.MenuFocus = index; RefreshToolbarMenuFocus(); };
            AddToolbarChild(ToolbarPopupButtons, button, slot.Bounds, _toolbarMenu.Bounds); _toolbarMenuButtons.Add(button);
        }
        RefreshToolbarMenuFocus();
    }
    private void RefreshToolbarMenuFocus()
    {
        for (int i = 0; i < _toolbarMenuButtons.Count; i++)
            _toolbarMenuButtons[i].Background = ToolbarBrush(i + (_toolbarMenu?.FirstIndex ?? 0) == _toolbarState.MenuFocus ? 0xffe8ecd9 : 0x00ffffff);
    }
    private void InvokeToolbar(RegionToolbarCommand command)
    {
        if (_state != RegionCaptureState.Selected && command != RegionToolbarCommand.Help) return;
        if (!ToolbarCommandEnabled(command)) return;
        CommitAnnotationText();
        if (RegionToolbarCatalog.IsGroup(command) || command == RegionToolbarCommand.More) { OpenToolbarMenu(command); return; }
        _toolbarState.ClosePopup(); _toolbarHelpVisible = false;
        if (RegionToolbarCatalog.IsTool(command)) SetAnnotationTool(Enum.Parse<AnnotationTool>(command.ToString()));
        else if (RegionToolbarCatalog.Get(command).Action is { } action)
        { if (action == RegionCaptureAction.Cancel) CancelCapture(); else CompleteCapture(action); }
        else switch (command)
        {
            case RegionToolbarCommand.Undo: UndoAnnotation(); break;
            case RegionToolbarCommand.Redo: RedoAnnotation(); break;
            case RegionToolbarCommand.Reselect: ReturnToSelectingState(); break;
            case RegionToolbarCommand.Help: ShowToolbarHelp(); break;
            case RegionToolbarCommand.HdrAnalysis: _ = OpenHdrAnalysisAsync(); break;
        }
        if (_state == RegionCaptureState.Selected) RefreshToolbar();
    }
    private void ShowToolbarHelp()
    {
        _toolbarHelpVisible = true; ToolbarPopup.Visibility = Visibility.Collapsed;
        var layout = _toolbarLayout ?? RegionToolbarGeometry.Arrange(ToolbarPhysicalSelection(),
            RegionToolbarMonitors.Read(new(_vx, _vy, (float)(_lockedW * _scale), (float)(_lockedH * _scale)), (float)_scale), _toolbarState, _defaultAction);
        ShortcutHint.Visibility = Visibility.Visible; ShortcutHintText.FontSize = ToolbarDip(12 * layout.Monitor.Scale);
        PlaceToolbarPanel(ShortcutHint, RegionToolbarGeometry.HelpBounds(layout));
    }
    private bool DismissToolbarOutsidePress()
    {
        if (!_toolbarState.Popup.HasValue && !_toolbarHelpVisible) return false;
        _toolbarState.ClosePopup(); _toolbarHelpVisible = false;
        ToolbarPopup.Visibility = ShortcutHint.Visibility = Visibility.Collapsed;
        return true;
    }
    private bool ConsumeToolbarCanvasPress(Windows.Foundation.Point position)
    {
        if (DismissToolbarOutsidePress())
        {
            _toolbarDismissedAt = position;
            _toolbarDismissedUntil = Environment.TickCount64 + ToolbarDoubleClickTime();
            return true;
        }
        return Environment.TickCount64 <= _toolbarDismissedUntil &&
            Math.Abs(position.X - _toolbarDismissedAt.X) < 6 && Math.Abs(position.Y - _toolbarDismissedAt.Y) < 6;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetDoubleClickTime")]
    private static extern uint ToolbarDoubleClickTime();
    private bool HandleToolbarPopupKey(Windows.System.VirtualKey key)
    {
        if (key == Windows.System.VirtualKey.Escape && _analysisScale?.IsDropDownOpen == true)
        { _analysisScale.IsDropDownOpen = false; return true; }
        if (key == Windows.System.VirtualKey.Escape && !_toolbarState.Popup.HasValue && !_toolbarHelpVisible && _analysisCancellation is not null)
        { CloseHdrAnalysis(); return true; }
        if (!_toolbarState.Popup.HasValue && !_toolbarHelpVisible) return false;
        if (key == Windows.System.VirtualKey.Escape || key == Windows.System.VirtualKey.F1)
        { DismissToolbarOutsidePress(); return true; }
        if (_toolbarHelpVisible) return true;
        if (_toolbarMenu is null || _toolbarMenu.Slots.Count == 0) return true;
        if (key is Windows.System.VirtualKey.Down or Windows.System.VirtualKey.Right or Windows.System.VirtualKey.Tab)
            MoveToolbarMenuFocus(1);
        else if (key is Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Left)
            MoveToolbarMenuFocus(-1);
        else if (key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
            InvokeToolbar(_toolbarState.MenuItems(_toolbarLayout!)[_toolbarState.MenuFocus]);
        else return true; // Modal keyboard input cannot accidentally complete the capture beneath a menu.
        RefreshToolbarMenuFocus(); return true;
    }
    private void MoveToolbarMenuFocus(int delta)
    {
        if (_toolbarLayout is null || !_toolbarState.Popup.HasValue) return;
        int count = _toolbarState.MenuItems(_toolbarLayout).Length;
        if (count == 0) return;
        _toolbarState.MenuFocus = (_toolbarState.MenuFocus + count + delta) % count;
        RefreshToolbarMenu();
    }
}
