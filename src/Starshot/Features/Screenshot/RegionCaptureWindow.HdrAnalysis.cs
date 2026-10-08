using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Microsoft.Graphics.Display;
using Drawing = System.Drawing;

namespace Starshot.Features.Screenshot;

public sealed partial class RegionCaptureWindow
{
    private sealed record AnalysisMonitor(Rect Bounds, bool Hdr, float White, float Peak);
    private List<AnalysisMonitor>? _analysisMonitors;
    private float _analysisReferenceWhite = 80;
    private CancellationTokenSource? _analysisCancellation;
    private int _analysisGeneration;
    private HdrLuminanceAnalysis? _hdrAnalysis;
    private Rect _analysisRect;
    private CanvasBitmap? _analysisHeatmap;
    private bool _analysisShowHeatmap;
    private float _analysisHeatmapOpacity = .65f;
    private double _analysisUiScale = 1;
    private TextBlock? _analysisStatus, _analysisCursor, _analysisWhite, _analysisPeak;
    private Image? _analysisWaveform;
    private ComboBox? _analysisScale;
    private ToggleSwitch? _analysisHeatmapSwitch;
    private Expander? _analysisWaveExpander;
    private Task _analysisPending = Task.CompletedTask;
    private int _analysisWaveGeneration;
    private int _analysisHeatmapGeneration;
    private TextBlock? _analysisWaveLabels;

    private void PrepareHdrAnalysisMetadata(float white)
    {
        _analysisMonitors = null;
        _analysisReferenceWhite = white;
    }

    private bool CanAnalyzeHdr()
    {
        if (_canvasOriginal?.Format != DirectXPixelFormat.R16G16B16A16Float || _isClosed) return false;
        // Metadata only: no WGC/device creation, no readback. Reject normalized SDR parts of
        // the mixed-monitor composite; those are not an absolute HDR signal.
        if (_analysisMonitors is null)
        {
            _analysisMonitors = new();
            try
            {
                foreach (var area in DisplayArea.FindAll())
                {
                    using var info = DisplayInformation.CreateForDisplayId(area.DisplayId);
                    var color = info.GetAdvancedColorInfo();
                    var b = area.OuterBounds;
                    _analysisMonitors.Add(new(new Rect(b.X, b.Y, b.Width, b.Height),
                        color.CurrentAdvancedColorKind == DisplayAdvancedColorKind.HighDynamicRange,
                        (float)color.SdrWhiteLevelInNits, (float)color.MaxLuminanceInNits));
                }
            }
            catch (Exception ex) { Serilog.Log.Warning(ex, "HDR analysis display metadata unavailable"); }
        }
        var r = GetPhysicalSourceRect();
        var screen = new Drawing.RectangleF((float)r.X + _vx, (float)r.Y + _vy, (float)r.Width, (float)r.Height);
        double hdrArea = 0;
        foreach (var monitor in _analysisMonitors)
        {
            var b = monitor.Bounds;
            var intersection = Drawing.RectangleF.Intersect(screen, new((float)b.X, (float)b.Y, (float)b.Width, (float)b.Height));
            if (intersection.IsEmpty) continue;
            if (!monitor.Hdr) return false;
            hdrArea += intersection.Width * intersection.Height;
        }
        return hdrArea >= screen.Width * (double)screen.Height - 1;
    }

    private async Task OpenHdrAnalysisAsync()
    {
        if (!CanAnalyzeHdr()) return;
        CloseHdrAnalysis();
        _analysisRect = GetPhysicalSourceRect();
        var cancellation = new CancellationTokenSource();
        _analysisCancellation = cancellation;
        var token = cancellation.Token;
        int generation = ++_analysisGeneration;
        using var diagnostic = HdrAnalysisDiagnostics.Measure("analysis_total", generation, task: true);
        HdrAnalysisDiagnostics.Track("cts", cancellation, generation);
        var source = _canvasOriginal; // Borrowed only while on UI thread; never held by worker code.
        BuildHdrAnalysisPanel();
        HdrAnalysisPanel.Visibility = Visibility.Visible;
        PositionHdrAnalysis();
        var previous = _analysisPending;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _analysisPending = completed.Task;
        var watch = Stopwatch.StartNew();
        HdrLuminanceAnalysis? working = null;
        try
        {
            // Let the pending close/paint/input run before the first readback.
            await previous; // Rapid close/reopen cannot accumulate multiple large analysis buffers.
            await Task.Delay(1, token);
            working = new((int)_analysisRect.Width, (int)_analysisRect.Height);
            HdrAnalysisDiagnostics.Track("model", working, generation);
            int rowBytes = checked(working.SourceWidth * 8);
            int chunkRows = Math.Max(1, 1024 * 1024 / rowBytes);
            byte[] bytes = new byte[checked(chunkRows * rowBytes)];
            HdrAnalysisDiagnostics.Track("stripe", bytes, generation);
            var stripe = bytes.AsBuffer(); // Reuse one caller-owned buffer instead of allocating a full frame over many stripes.
            var rect = _analysisRect;
            for (int y = 0; y < working.SourceHeight; y += chunkRows)
            {
                token.ThrowIfCancellationRequested();
                if (generation != _analysisGeneration || _isClosed || !ReferenceEquals(source, _canvasOriginal)) return;
                if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("选区分析超过 8 秒上限，请缩小选区重试。");
                int rows = Math.Min(chunkRows, working.SourceHeight - y);
                // The GPU API stays serialized with overlay rendering on the UI dispatcher.
                // Only one <=1MiB stripe is synchronized per turn; CPU conversion is off-thread.
                using (HdrAnalysisDiagnostics.Measure("readback", generation))
                    source.GetPixelBytes(stripe, (int)rect.X, (int)rect.Y + y, working.SourceWidth, rows);
                int firstRow = y;
                var conversion = Task.Run(() => { token.ThrowIfCancellationRequested(); working.AppendRows(bytes, firstRow, rows, rowBytes); });
                HdrAnalysisDiagnostics.Track("task", conversion, generation);
                await conversion;
                if (generation != _analysisGeneration) return;
                _analysisStatus!.Text = $"读取原始 scRGB · {(y + rows) * 100 / working.SourceHeight}%";
                await Task.Delay(1, token); // Yield even when a small CPU conversion finishes synchronously.
            }
            var statistics = Task.Run(() => working.Finish(token));
            HdrAnalysisDiagnostics.Track("task", statistics, generation);
            var stats = await statistics;
            if (generation != _analysisGeneration || token.IsCancellationRequested) return;
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("选区分析超过 8 秒上限，请缩小选区重试。");
            _hdrAnalysis = working; working = null;
            SetAnalysisMetric("Max", stats.Max); SetAnalysisMetric("Avg", stats.Average);
            SetAnalysisMetric("Min", stats.Min); SetAnalysisMetric("P99", stats.P99);
            _analysisStatus!.Text = (_hdrAnalysis.SampleStep > 1 ? $"≈ 抽样结果 · 间隔 {_hdrAnalysis.SampleStep} px · 峰值可能漏检" : "逐像素分析 · 精确选区统计") +
                $" · {watch.Elapsed.TotalMilliseconds:0} ms";
            _analysisHeatmapSwitch!.IsEnabled = true;
            UpdateHdrAnalysisCursor(_currentMousePos);
            RefreshHdrWaveform();
            Serilog.Log.Information("HDR luminance analysis: region={Width}x{Height}, step={Step}, samples={Samples}, max={Max}, p99={P99}, elapsed={Elapsed} ms",
                _hdrAnalysis.SourceWidth, _hdrAnalysis.SourceHeight, _hdrAnalysis.SampleStep, stats.SampleCount, stats.Max, stats.P99, watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation == _analysisGeneration)
            {
                _hdrAnalysis?.Dispose(); _hdrAnalysis = null;
                _analysisHeatmap?.Dispose(); _analysisHeatmap = null;
                _analysisStatus!.Text = "分析未完成：" + ex.Message + "（关闭后可重试）";
                Serilog.Log.Warning(ex, "HDR luminance analysis failed");
            }
        }
        finally
        {
            working?.Dispose();
            if (!ReferenceEquals(_analysisCancellation, cancellation)) cancellation.Dispose();
            completed.TrySetResult();
        }
    }

    private readonly Dictionary<string, TextBlock> _analysisMetrics = new();
    private TextBlock AnalysisText(string text, double size = 12) => new()
    {
        Text = text, FontSize = size * _analysisUiScale, Foreground = ToolbarBrush(0xff242b24),
        TextWrapping = TextWrapping.Wrap
    };
    private void BuildHdrAnalysisPanel()
    {
        _analysisUiScale = (_toolbarLayout?.Monitor.Scale ?? _scale) / _scale;
        double s = _analysisUiScale;
        _analysisHeatmapOpacity = .65f;
        _analysisMetrics.Clear();
        var content = new StackPanel { Spacing = 8 * s, Padding = new Thickness(16 * s) };
        var header = new Grid();
        var title = AnalysisText("HDR 亮度分析", 17); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        header.Children.Add(title);
        var close = new Button { Content = "×", MinWidth = 30 * s, MinHeight = 30 * s, Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right, Background = ToolbarBrush(0x00ffffff) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, "关闭 HDR 亮度分析");
        close.Click += (_, _) => CloseHdrAnalysis(); header.Children.Add(close); content.Children.Add(header);
        content.Children.Add(AnalysisText("内容亮度 / nits", 11));
        var metrics = new Grid { ColumnSpacing = 12 * s, RowSpacing = 10 * s };
        for (int i = 0; i < 3; i++) metrics.ColumnDefinitions.Add(new());
        for (int i = 0; i < 2; i++) metrics.RowDefinitions.Add(new() { Height = GridLength.Auto });
        string[] names = ["Cursor", "Max", "Avg", "Min", "P99", "SDR White"];
        for (int i = 0; i < names.Length; i++)
        {
            var tile = new StackPanel { Spacing = 3 * s };
            var label = AnalysisText(names[i], 11); label.Foreground = ToolbarBrush(0xff818a77); tile.Children.Add(label);
            var value = AnalysisText("—", 18); value.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono");
            tile.Children.Add(value); _analysisMetrics[names[i]] = value;
            Grid.SetRow(tile, i / 3); Grid.SetColumn(tile, i % 3); metrics.Children.Add(tile);
        }
        _analysisCursor = _analysisMetrics["Cursor"]; _analysisWhite = _analysisMetrics["SDR White"];
        content.Children.Add(metrics);
        _analysisPeak = AnalysisText("显示器峰值：—", 11); content.Children.Add(_analysisPeak);
        _analysisStatus = AnalysisText("正在准备原始 FP16 数据…", 11); content.Children.Add(_analysisStatus);
        _analysisHeatmapSwitch = new ToggleSwitch { Header = "伪彩色亮度叠层", OnContent = "热力图", OffContent = "原始预览",
            IsEnabled = false, FontSize = 12 * s };
        var heatmapControls = new StackPanel { Spacing = 6 * s, Visibility = Visibility.Collapsed };
        _analysisHeatmapSwitch.Toggled += (_, _) =>
        {
            _analysisShowHeatmap = _analysisHeatmapSwitch.IsOn;
            heatmapControls.Visibility = _analysisShowHeatmap ? Visibility.Visible : Visibility.Collapsed;
            _ = UpdateHdrAnalysisHeatmapAsync();
            RequestRedraw();
        };
        content.Children.Add(_analysisHeatmapSwitch);
        var opacity = new Slider { Minimum = 0, Maximum = 100, Value = 65, Header = "叠层不透明度", FontSize = 11 * s };
        opacity.ValueChanged += (_, e) => { _analysisHeatmapOpacity = (float)e.NewValue / 100; if (_analysisShowHeatmap) RequestRedraw(); };
        heatmapControls.Children.Add(opacity);
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 * s };
        foreach (float n in new float[] { 0, 80, 203, 400, 1000, 4000, 10000 })
        {
            var color = HdrLuminanceAnalysis.HeatmapColor(n);
            var block = new Border { Background = ToolbarBrush(color), Height = 6 * s, Width = 39 * s, CornerRadius = new(2 * s) };
            ToolTipService.SetToolTip(block, $"{n:0} nits"); legend.Children.Add(block);
        }
        heatmapControls.Children.Add(legend);
        heatmapControls.Children.Add(AnalysisText("0   80   203   400   1000   4000   10000+ nits", 10));
        content.Children.Add(heatmapControls);
        _analysisWaveform = new Image { Height = 128 * s, Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill };
        _analysisWaveLabels = AnalysisText("纵轴 nits · 横轴选区水平位置", 10);
        _analysisScale = new ComboBox { ItemsSource = new[] { "自动刻度", "400 nits", "1000 nits", "2000 nits", "4000 nits", "10000 nits" },
            SelectedIndex = 0, FontSize = 12 * s, HorizontalAlignment = HorizontalAlignment.Stretch };
        _analysisScale.SelectionChanged += (_, _) => RefreshHdrWaveform();
        var wave = new StackPanel { Spacing = 8 * s }; wave.Children.Add(_analysisScale); wave.Children.Add(_analysisWaveLabels);
        wave.Children.Add(new Border { Background = ToolbarBrush(0xff20271f), CornerRadius = new(6 * s), Child = _analysisWaveform });
        _analysisWaveExpander = new Expander { Header = "亮度波形  /  Waveform", Content = wave, HorizontalAlignment = HorizontalAlignment.Stretch };
        _analysisWaveExpander.Expanding += (_, _) => { DispatcherQueue.TryEnqueue(RefreshHdrWaveform); PositionHdrAnalysis(true); };
        _analysisWaveExpander.Collapsed += (_, _) => PositionHdrAnalysis(false);
        content.Children.Add(_analysisWaveExpander);
        content.Children.Add(AnalysisText("scRGB 信号亮度估计 · 1.0 = 80 nits\n不是屏幕实际发光测量。内容峰值超过显示器报告值不等于显示器必然裁剪。热力图不保存、不复制。", 10));
        HdrAnalysisScroll.Content = content;
        HdrAnalysisDiagnostics.Track("panel", content, _analysisGeneration);
        HdrAnalysisPanel.PointerPressed += AnalysisPanelConsume;
        HdrAnalysisPanel.PointerReleased += AnalysisPanelConsume;
        HdrAnalysisPanel.PointerMoved += AnalysisPanelPointerMoved;
        HdrAnalysisPanel.DoubleTapped += AnalysisPanelDoubleTap;
    }
    private void AnalysisPanelConsume(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => e.Handled = true;
    private void AnalysisPanelPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => UpdateHdrAnalysisCursor(e.GetCurrentPoint(Canvas).Position);
    private void AnalysisPanelDoubleTap(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e) => e.Handled = true;
    private void SetAnalysisMetric(string key, double nits) => _analysisMetrics[key].Text =
        (_hdrAnalysis?.SampleStep > 1 ? "≈" : "") + $"{nits:0.#}";

    private void UpdateHdrAnalysisCursor(Point position)
    {
        if (_hdrAnalysis is null) return;
        double x = position.X * _canvasOriginal.SizeInPixels.Width / _lockedW - _analysisRect.X;
        double y = position.Y * _canvasOriginal.SizeInPixels.Height / _lockedH - _analysisRect.Y;
        _analysisCursor!.Text = _hdrAnalysis.TrySample((int)Math.Floor(x), (int)Math.Floor(y), out var value)
            ? (_hdrAnalysis.SampleStep > 1 ? "≈" : "") + value.ToString("0.#") : "—";
        float sx = (float)(x + _analysisRect.X) + _vx, sy = (float)(y + _analysisRect.Y) + _vy;
        var monitor = _analysisMonitors?.Find(m => sx >= m.Bounds.X && sy >= m.Bounds.Y && sx < m.Bounds.Right && sy < m.Bounds.Bottom);
        if (!_hdrAnalysis.TrySample((int)Math.Floor(x), (int)Math.Floor(y), out _))
        {
            sx = (float)(_analysisRect.X + _analysisRect.Width / 2) + _vx;
            sy = (float)(_analysisRect.Y + _analysisRect.Height / 2) + _vy;
            monitor = _analysisMonitors?.Find(m => sx >= m.Bounds.X && sy >= m.Bounds.Y && sx < m.Bounds.Right && sy < m.Bounds.Bottom);
        }
        if (monitor?.White > 0) _analysisReferenceWhite = monitor.White;
        _analysisWhite!.Text = monitor?.White > 0 ? monitor.White.ToString("0.#") : "—";
        _analysisPeak!.Text = monitor?.Peak > 0 ? $"显示器报告峰值 {monitor.Peak:0.#} nits（参考）" : "显示器报告峰值：不可用";
    }
    private async void RefreshHdrWaveform()
    {
        using var diagnostic = HdrAnalysisDiagnostics.Measure("waveform_total", _analysisGeneration, task: true);
        if (_hdrAnalysis is null || _analysisWaveform is null || _analysisScale is null) return;
        float scale = _analysisScale.SelectedIndex switch { 1 => 400, 2 => 1000, 3 => 2000, 4 => 4000, 5 => 10000,
            _ => HdrLuminanceAnalysis.SelectScale(_hdrAnalysis.Stats.Max) };
        if (_analysisWaveExpander?.IsExpanded != true) return;
        var values = _hdrAnalysis.Values;
        int width = _hdrAnalysis.Width, height = _hdrAnalysis.Height;
        int generation = ++_analysisWaveGeneration, session = _analysisGeneration;
        HdrBgraImage bitmap;
        var token = _analysisCancellation?.Token ?? default;
        try { bitmap = await Task.Run(() => HdrLuminanceAnalysis.CreateWaveform(values, width, height, scale, cancellationToken: token)); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Serilog.Log.Warning(ex, "HDR waveform generation failed"); return; }
        if (generation != _analysisWaveGeneration || session != _analysisGeneration || _analysisWaveform is null) return;
        // Raster generation uses cached CPU luminance only, never reads the GPU again.
        var white = _analysisReferenceWhite;
        foreach (float line in new[] { white, 1000f })
        {
            if (line > scale) continue;
            int row = Math.Clamp((int)((1 - line / scale) * (bitmap.Height - 1)), 0, bitmap.Height - 1);
            for (int x = 0; x < bitmap.Width; x += 2)
            { int p = (row * bitmap.Width + x) * 4; bitmap.Pixels[p] = 105; bitmap.Pixels[p + 1] = 243; bitmap.Pixels[p + 2] = 221; bitmap.Pixels[p + 3] = 255; }
        }
        var writable = new WriteableBitmap(bitmap.Width, bitmap.Height);
        using (var stream = writable.PixelBuffer.AsStream()) stream.Write(bitmap.Pixels);
        writable.Invalidate(); _analysisWaveform.Source = writable;
        HdrAnalysisDiagnostics.Track("waveform_bitmap", writable, session);
        _analysisWaveLabels!.Text = $"纵轴：{scale:0} ↑ {scale / 2:0} ↑ 0 nits · 横轴：左 → 右\n虚线：SDR White {white:0.#}" + (scale >= 1000 ? " / 1000 nits" : "") + " · 超刻度高光位于顶部";
        ToolTipService.SetToolTip(_analysisWaveform, $"横轴：选区水平位置 · 纵轴：0–{scale:0} nits\n虚线：SDR White {white:0.#} / 1000 nits · 超刻度高光显示在顶部");
    }
    private void PositionHdrAnalysis(bool? expanded = null)
    {
        if (_analysisCancellation is null || _toolbarLayout is null) return;
        PlaceToolbarPanel(HdrAnalysisPanel, RegionToolbarGeometry.AnalysisBounds(_toolbarLayout,
            ToolbarPhysicalSelection(), expanded ?? _analysisWaveExpander?.IsExpanded ?? false));
    }
    private void DrawHdrAnalysisHeatmap(CanvasDrawingSession ds)
    {
        if (_analysisShowHeatmap && _analysisHeatmap is not null && _hdrAnalysis is not null)
            ds.DrawImage(_analysisHeatmap, SelectionRect,
                new Rect(0, 0, _analysisHeatmap.SizeInPixels.Width, _analysisHeatmap.SizeInPixels.Height),
                _analysisHeatmapOpacity, CanvasImageInterpolation.NearestNeighbor);
    }
    private async Task UpdateHdrAnalysisHeatmapAsync()
    {
        using var diagnostic = HdrAnalysisDiagnostics.Measure("heatmap_total", _analysisGeneration, task: true);
        int version = ++_analysisHeatmapGeneration, session = _analysisGeneration;
        _analysisHeatmap?.Dispose(); _analysisHeatmap = null;
        if (!_analysisShowHeatmap || _hdrAnalysis is null || _analysisCancellation is null) return;
        var analysis = _hdrAnalysis;
        var token = _analysisCancellation.Token;
        try
        {
            var image = await Task.Run(() => analysis.CreateHeatmap(cancellationToken: token));
            if (version != _analysisHeatmapGeneration || session != _analysisGeneration || token.IsCancellationRequested || _isClosed) return;
            // Allocate this small display-only GPU texture only when heatmap is explicitly enabled.
            using (HdrAnalysisDiagnostics.Measure("heatmap_upload", session))
                _analysisHeatmap = CanvasBitmap.CreateFromBytes(_canvasOriginal.Device, image.Pixels, image.Width, image.Height,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Ignore);
            HdrAnalysisDiagnostics.Track("heatmap_texture", _analysisHeatmap, session);
            RequestRedraw();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version == _analysisHeatmapGeneration && session == _analysisGeneration)
            {
                _analysisStatus!.Text = "热力图生成失败，可关闭开关后重试。";
                Serilog.Log.Warning(ex, "HDR analysis heatmap generation failed");
            }
        }
    }
    private void CloseHdrAnalysis()
    {
        HdrAnalysisDiagnostics.Mark("close_request", _analysisGeneration);
        ++_analysisGeneration;
        ++_analysisWaveGeneration;
        ++_analysisHeatmapGeneration;
        // Active jobs dispose their CTS in finally; a completed panel owns it until close.
        _analysisCancellation?.Cancel();
        if (_analysisPending.IsCompleted) _analysisCancellation?.Dispose();
        _analysisCancellation = null;
        _hdrAnalysis?.Dispose(); _hdrAnalysis = null;
        _analysisHeatmap?.Dispose(); _analysisHeatmap = null; _analysisShowHeatmap = false;
        if (_analysisWaveform is not null) _analysisWaveform.Source = null;
        _analysisWaveform = null; _analysisScale = null; _analysisWaveExpander = null;
        _analysisWaveLabels = null;
        _analysisHeatmapSwitch = null; _analysisMetrics.Clear();
        _analysisStatus = _analysisCursor = _analysisWhite = _analysisPeak = null;
        HdrAnalysisScroll.Content = null; HdrAnalysisPanel.Visibility = Visibility.Collapsed;
        HdrAnalysisPanel.PointerPressed -= AnalysisPanelConsume;
        HdrAnalysisPanel.PointerReleased -= AnalysisPanelConsume;
        HdrAnalysisPanel.PointerMoved -= AnalysisPanelPointerMoved;
        HdrAnalysisPanel.DoubleTapped -= AnalysisPanelDoubleTap;
        RequestRedraw();
    }
    private bool IsHdrAnalysisControlFocused()
    {
        if (_analysisCancellation is null || RootGrid.XamlRoot is null) return false;
        if (_analysisScale?.IsDropDownOpen == true) return true;
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (ReferenceEquals(focused, HdrAnalysisPanel)) return true;
            focused = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(focused);
        }
        return false;
    }
}
