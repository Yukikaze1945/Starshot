using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Starshot.Features.Workspace;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace Starshot.Features.Screenshot;

public sealed partial class RegionCaptureWindow
{
    private CancellationTokenSource? _gamutCancellation;
    private Task _gamutPending = Task.CompletedTask;
    private int _gamutGeneration;
    private Rect _gamutRect;
    private GamutAnalysis.Summary? _gamutStats;
    private GamutAnalysis.RegionMap? _gamutMap;
    private CanvasBitmap? _gamutTexture;
    private TextBlock? _gamutStatus;
    private StackPanel? _gamutResults;
    private ToggleSwitch? _gamutSwitch;
    private float _gamutOpacity = .55f;

    private async Task OpenGamutAnalysisAsync()
    {
        if (!CanAnalyzeHdr()) return;
        CloseGamutAnalysis(); _gamutRect = GetPhysicalSourceRect();
        var cancellation = new CancellationTokenSource(); var token = cancellation.Token;
        _gamutCancellation = cancellation; int generation = ++_gamutGeneration;
        BuildGamutPanel(); GamutAnalysisPanel.Visibility = Visibility.Visible; PositionGamutAnalysis();
        // Share the serialized readback lifetime with luminance analysis. No two complete
        // source buffers, no per-mouse-move GPU access, and no new WGC context.
        var previous = Task.WhenAll(_analysisPending, _gamutPending);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _gamutPending = finished.Task;
        var source = _canvasOriginal; var rect = _gamutRect; var watch = Stopwatch.StartNew();
        try
        {
            await previous; token.ThrowIfCancellationRequested(); watch.Restart(); await Task.Delay(1, token);
            int width = (int)rect.Width, height = (int)rect.Height, stride = checked(width * 8), chunk = Math.Max(1, 1024 * 1024 / stride);
            var data = new byte[checked(chunk * stride)]; var buffer = data.AsBuffer();
            var accumulator = new GamutAnalysis.Accumulator(width, height);
            for (int y = 0; y < height; y += chunk)
            {
                token.ThrowIfCancellationRequested();
                if (generation != _gamutGeneration || _isClosed || !ReferenceEquals(source, _canvasOriginal)) return;
                if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("选区过大，请缩小后重试");
                int rows = Math.Min(chunk, height - y), first = y;
                source.GetPixelBytes(buffer, (int)rect.X, (int)rect.Y + y, width, rows);
                await Task.Run(() => accumulator.AppendRows(data, first, rows, stride, token), token);
                if (generation != _gamutGeneration) return;
                _gamutStatus!.Text = $"分析原始 scRGB · {(y + rows) * 100 / height}%";
                await Task.Delay(1, token);
            }
            var result = accumulator.Finish();
            var diagram = await Task.Run(() => GamutAnalysis.CreateDiagram(result.Stats), token);
            if (generation != _gamutGeneration || token.IsCancellationRequested || _isClosed) return;
            _gamutStats = result.Stats; _gamutMap = result.Map;
            foreach (var (label, value) in new[] { ("sRGB 内", result.Stats.Srgb), ("P3 超出 sRGB", result.Stats.DisplayP3Only),
                ("BT.2020 超出 P3", result.Stats.Bt2020Only), ("超出 BT.2020", result.Stats.Outside2020) })
                _gamutResults!.Children.Add(AnalysisText($"{label}    {value:0.00}%", 13));
            var image = new Image { Height = 210 * _analysisUiScale, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, Source = AnalysisBitmap(diagram) };
            _gamutResults!.Children.Add(image);
            _gamutResults.Children.Add(AnalysisText("CIE 1931 xy · x: 0–0.8 / y: 0–0.9\n绿：sRGB · 蓝：Display-P3 · 橙：BT.2020\n区域叠层：红色表示超出 BT.2020", 10));
            _gamutSwitch!.IsEnabled = true;
            _gamutStatus!.Text = $"{(result.Stats.Approximate ? "≈ 抽样" : "逐像素")} · {result.Stats.Samples:N0} 有效色度像素 · {watch.ElapsedMilliseconds} ms\n无色度/非正亮度排除 {result.Stats.Black:N0} · 无效排除 {result.Stats.Invalid:N0}";
            Serilog.Log.Information("Region gamut analysis: {Width}x{Height}, samples={Samples}, approximate={Approximate}, elapsed={Elapsed} ms", width, height, result.Stats.Samples, result.Stats.Approximate, watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == _gamutGeneration) { _gamutStatus!.Text = "分析失败：" + ex.Message; Serilog.Log.Warning(ex, "Region gamut analysis failed"); } }
        finally { if (!ReferenceEquals(_gamutCancellation, cancellation)) cancellation.Dispose(); finished.TrySetResult(); }
    }
    private static WriteableBitmap AnalysisBitmap(GamutAnalysis.RegionMap map)
    {
        var bitmap = new WriteableBitmap(map.Width, map.Height);
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(map.Pixels);
        bitmap.Invalidate(); return bitmap;
    }
    private void BuildGamutPanel()
    {
        _analysisUiScale = (_toolbarLayout?.Monitor.Scale ?? _scale) / _scale; double s = _analysisUiScale;
        var content = new StackPanel { Padding = new Thickness(16 * s), Spacing = 10 * s };
        var header = new Grid(); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var grip = new StackPanel { Background = ToolbarBrush(0x00ffffff), Spacing = 2 * s };
        var title = AnalysisText("色域分析", 17); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        grip.Children.Add(title); grip.Children.Add(AnalysisText("⠿ 拖动标题移动面板", 10)); header.Children.Add(grip);
        InstallAnalysisDrag(grip, GamutAnalysisPanel, true);
        var close = new Button { Content = "×", MinWidth = 30 * s, MinHeight = 30 * s, Padding = new Thickness(0), Background = ToolbarBrush(0x00ffffff) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, "关闭色域分析"); close.Click += (_, _) => CloseGamutAnalysis();
        Grid.SetColumn(close, 1); header.Children.Add(close); content.Children.Add(header);
        _gamutStatus = AnalysisText("正在准备原始 FP16 数据…", 11); content.Children.Add(_gamutStatus);
        _gamutResults = new StackPanel { Spacing = 7 * s }; content.Children.Add(_gamutResults);
        _gamutSwitch = new ToggleSwitch { Header = "色域区域叠层", IsEnabled = false, OnContent = "已开启", OffContent = "原始预览" };
        _gamutSwitch.Toggled += (_, _) =>
        {
            if (_gamutSwitch.IsOn && _analysisHeatmapSwitch?.IsOn == true) _analysisHeatmapSwitch.IsOn = false;
            _gamutTexture?.Dispose(); _gamutTexture = null;
            if (_gamutSwitch.IsOn && _gamutMap is { } map && !_isClosed)
                try { _gamutTexture = CanvasBitmap.CreateFromBytes(_canvasOriginal.Device, map.Pixels, map.Width, map.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Ignore); }
                catch (Exception ex) { _gamutStatus!.Text = "叠层不可用：" + ex.Message; }
            RequestRedraw();
        };
        content.Children.Add(_gamutSwitch); _gamutOpacity = .55f;
        var opacity = new Slider { Minimum = 0, Maximum = 100, Value = 55, Header = "叠层不透明度" };
        opacity.ValueChanged += (_, e) => { _gamutOpacity = (float)e.NewValue / 100; if (_gamutTexture is not null) RequestRedraw(); }; content.Children.Add(opacity);
        content.Children.Add(AnalysisText("基于原始线性 RGB 的色度归属，保留负通道。四类占比互斥；不是屏幕色域覆盖率测量。\n区域叠层最长边 1024 px，细小区域可能漏检。叠层不写入保存、复制或 OCR。", 10));
        GamutAnalysisScroll.Content = content;
        GamutAnalysisPanel.PointerPressed += AnalysisPanelConsume; GamutAnalysisPanel.PointerReleased += AnalysisPanelConsume; GamutAnalysisPanel.DoubleTapped += AnalysisPanelDoubleTap;
    }
    private void PositionGamutAnalysis()
    {
        if (_gamutCancellation is null || _toolbarLayout is null) return;
        var bounds = RegionToolbarGeometry.AnalysisBounds(_toolbarLayout, ToolbarPhysicalSelection(), true);
        if (_gamutManualBounds is { } moved) bounds = new(moved.X, moved.Y, bounds.Width, bounds.Height);
        else if (_analysisCancellation is not null) bounds.Offset(-24 * _scale, 24 * _scale);
        PlaceToolbarPanel(GamutAnalysisPanel, ClampAnalysisPanel(bounds));
    }
    private void DrawGamutAnalysisMap(CanvasDrawingSession ds)
    {
        if (_gamutTexture is not null) ds.DrawImage(_gamutTexture, SelectionRect,
            new Rect(0, 0, _gamutTexture.SizeInPixels.Width, _gamutTexture.SizeInPixels.Height), _gamutOpacity, CanvasImageInterpolation.NearestNeighbor);
    }
    private void CloseGamutAnalysis()
    {
        StopAnalysisDrag(GamutAnalysisPanel); _gamutManualBounds = null; ++_gamutGeneration;
        _gamutCancellation?.Cancel(); if (_gamutPending.IsCompleted) _gamutCancellation?.Dispose(); _gamutCancellation = null;
        _gamutTexture?.Dispose(); _gamutTexture = null; _gamutMap = null; _gamutStats = null;
        _gamutResults = null; _gamutStatus = null; _gamutSwitch = null; GamutAnalysisScroll.Content = null; GamutAnalysisPanel.Visibility = Visibility.Collapsed;
        GamutAnalysisPanel.PointerPressed -= AnalysisPanelConsume; GamutAnalysisPanel.PointerReleased -= AnalysisPanelConsume; GamutAnalysisPanel.DoubleTapped -= AnalysisPanelDoubleTap;
        RequestRedraw();
    }
}
