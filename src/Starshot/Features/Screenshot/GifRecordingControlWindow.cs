using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vanara.PInvoke;
using Windows.Graphics;
using Windows.UI;

namespace Starshot.Features.Screenshot;

/// <summary>Small recording control excluded from screen capture.</summary>
internal sealed class GifRecordingControlWindow : Window
{
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(30);
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextBlock _elapsed = new() { Foreground = new SolidColorBrush(Colors.White) };
    private readonly DispatcherQueueTimer _timer;
    private readonly DateTimeOffset _started = DateTimeOffset.Now;
    public Task<bool> Completion => _completion.Task;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    public GifRecordingControlWindow(int x, int y)
    {
        Title = "Starshot GIF";
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }
        AppWindow.MoveAndResize(new RectInt32(x, y, 280, 66));
        var grid = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(245, 12, 14, 20)),
            Padding = new Thickness(10),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        var label = new StackPanel { Spacing = 2 };
        label.Children.Add(new TextBlock { Text = "● 录制 GIF", Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 64, 77)) });
        label.Children.Add(_elapsed);
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);
        var stop = new Button { Content = "完成", MinWidth = 56 };
        stop.Click += (_, _) => Finish(true);
        Grid.SetColumn(stop, 1);
        grid.Children.Add(stop);
        var cancel = new Button { Content = "取消", MinWidth = 56 };
        cancel.Click += (_, _) => Finish(false);
        Grid.SetColumn(cancel, 2);
        grid.Children.Add(cancel);
        Content = grid;
        _timer = DispatcherQueue.CreateTimer();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _completion.TrySetResult(false);
        };
        Activate();
        if (!SetWindowDisplayAffinity((nint)AppWindow.Id.Value, 0x11))
            Serilog.Log.Warning("GIF recording control capture exclusion failed: {Error}", Marshal.GetLastPInvokeError());
        _timer.Interval = TimeSpan.FromMilliseconds(200);
        _timer.Tick += (_, _) =>
        {
            TimeSpan duration = DateTimeOffset.Now - _started;
            _elapsed.Text = $"{duration:mm\\:ss} / 00:30";
            if (duration >= MaximumDuration) Finish(true);
        };
        _timer.Start();
    }

    private void Finish(bool save)
    {
        if (!_completion.TrySetResult(save)) return;
        _timer.Stop();
        Close();
    }
}
