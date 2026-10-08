using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Xaml.Interactivity;

namespace Starshot.Helpers;

public class InAppToast : Behavior<StackPanel>
{
    private readonly DispatcherQueueTimer _dismissTimer;
    private Action<InfoBarSeverity, string?, string?, int, string?, Action?, Action?>? _webUi;

    // The main WebUI uses a native HWND host, so it has no XAML toast visual tree.
    public static IDisposable UseWebUiHost(Action<InfoBarSeverity, string?, string?, int, string?, Action?, Action?> notify)
    {
        var owner = new InAppToast { _webUi = notify };
        MainWindow = owner;
        return new WebUiLease(owner);
    }

    private sealed class WebUiLease(InAppToast owner) : IDisposable
    {
        public void Dispose()
        {
            _pendingToasts.RemoveAll(entry => ReferenceEquals(entry.Owner, owner));
            if (ReferenceEquals(MainWindow, owner)) MainWindow = null;
            owner._webUi = null;
            owner._dismissTimer.Stop();
        }
    }

    public string Tag
    {
        get { return (string)GetValue(TagProperty); }
        set { SetValue(TagProperty, value); }
    }
    public static readonly DependencyProperty TagProperty = DependencyProperty.Register(
        "Tag",
        typeof(string),
        typeof(InAppToast),
        new PropertyMetadata(default)
    );

    public static InAppToast? MainWindow { get; private set; }

    // 启动期间 splash 盖住窗口，toast 入队；MainWindow Loaded + splash 完成后调 FlushPending 依次显示
    private static bool _deferring = true;

    private static readonly List<(InAppToast Owner, Action Show)> _pendingToasts = new();

    public InAppToast()
    {
        _dismissTimer = DispatcherQueue.CreateTimer();
        _dismissTimer.Interval = TimeSpan.FromSeconds(30);
        _dismissTimer.IsRepeating = true;
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        _dismissTimer.Tick += _dismissTimer_Tick;
        if (Tag is nameof(MainWindow))
        {
            MainWindow = this;
        }
    }

    protected override void OnDetaching()
    {
        _dismissTimer.Stop();
        _dismissTimer.Tick -= _dismissTimer_Tick;
        _pendingToasts.RemoveAll(entry => ReferenceEquals(entry.Owner, this));
        base.OnDetaching();
        if (Tag is nameof(MainWindow))
        {
            if (ReferenceEquals(MainWindow, this)) MainWindow = null;
        }
    }

    /// <summary>
    /// splash 完成后调：停止 defer，依次显示启动期间累积的 toast。
    /// </summary>
    public static void FlushPending()
    {
        _deferring = false;
        var pending = _pendingToasts.ToArray();
        _pendingToasts.Clear();
        foreach (var entry in pending)
        {
            entry.Show();
        }
    }

    private void _dismissTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        try
        {
            int i = 0;
            var count = AssociatedObject.Children.Count;
            while (i < count)
            {
                var item = AssociatedObject.Children[i] as InfoBar;
                if (item != null && !item.IsOpen)
                {
                    AssociatedObject.Children.RemoveAt(i);
                    count--;
                }
                else
                {
                    i++;
                }
            }
        }
        catch { }
    }

    public void Show(InfoBar infoBar, int duration = 0, int index = -1)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                infoBar.IsOpen = true;
                if (index > 0)
                {
                    AssociatedObject.Children.Insert(index, infoBar);
                }
                else
                {
                    AssociatedObject.Children.Add(infoBar);
                }
                if (duration > 0)
                {
                    await Task.Delay(duration);
                    infoBar.IsOpen = false;
                }
            }
            catch { }
        });
    }

    private void AddInfoBar(
        InfoBarSeverity severity,
        string? title,
        string? message,
        int duration = 0,
        string? accentMessage = null
    )
    {
        if (_webUi is not null)
        {
            void deliver() => _webUi?.Invoke(severity, title,
                string.Join(" ", new[] { message, accentMessage }), duration, null, null, null);
            if (_deferring) _pendingToasts.Add((this, deliver)); else deliver();
            return;
        }
        void core() =>
            DispatcherQueue.TryEnqueue(() =>
            {
                var infoBar = new InfoBar
                {
                    Title = title,
                    Severity = severity,
                    IsOpen = true,
                };
                if (accentMessage is not null)
                {
                    // message + 空格 + accentMessage（强调色），用 Content 富文本替代 Message
                    var tb = new TextBlock();
                    if (!string.IsNullOrEmpty(message))
                    {
                        tb.Inlines.Add(
                            new Microsoft.UI.Xaml.Documents.Run { Text = message + "    " }
                        );
                    }
                    tb.Inlines.Add(
                        new Microsoft.UI.Xaml.Documents.Run
                        {
                            Text = accentMessage,
                            Foreground =
                                Application.Current.Resources["SystemFillColorSuccessBrush"]
                                as Brush,
                        }
                    );
                    infoBar.Content = tb;
                }
                else
                {
                    infoBar.Message = message;
                }
                if (severity == InfoBarSeverity.Informational)
                {
                    infoBar.Background =
                        Application.Current.Resources["CustomAcrylicBrush"] as Brush;
                }
                Show(infoBar, duration);
            });
        if (_deferring)
            _pendingToasts.Add((this, core));
        else
            core();
    }

    public void Information(
        string? title,
        string? message = null,
        int duration = 3000,
        string? accentMessage = null
    )
    {
        AddInfoBar(InfoBarSeverity.Informational, title, message, duration, accentMessage);
    }

    public void Success(string? title, string? message = null, int duration = 3000)
    {
        AddInfoBar(InfoBarSeverity.Success, title, message, duration);
    }

    public void Warning(string? title, string? message = null, int duration = 5000)
    {
        AddInfoBar(InfoBarSeverity.Warning, title, message, duration);
    }

    public void Error(string? title, string? message = null, int duration = 7000)
    {
        AddInfoBar(InfoBarSeverity.Error, title, message, duration);
    }

    public void Error(Exception ex, string? message = null, int duration = 7000)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            AddInfoBar(InfoBarSeverity.Error, ex.GetType().Name, ex.Message, duration);
        }
        else
        {
            AddInfoBar(
                InfoBarSeverity.Error,
                $"{ex.GetType().Name} - {message}",
                ex.Message,
                duration
            );
        }
    }

    public void ShowWithButton(
        InfoBarSeverity severity,
        string? title,
        string? message,
        string buttonContent,
        Action buttonAction,
        Action? closedAction = null,
        int duration = 0
    )
    {
        if (_webUi is { } notify) { notify(severity, title, message, duration, buttonContent, buttonAction, closedAction); return; }
        void core() =>
            DispatcherQueue.TryEnqueue(() =>
            {
                var infoBar = Create(
                    severity,
                    title,
                    message,
                    buttonContent,
                    buttonAction,
                    closedAction
                );
                Show(infoBar, duration);
            });
        if (_deferring)
            _pendingToasts.Add((this, core));
        else
            core();
    }

    private InfoBar Create(
        InfoBarSeverity severity,
        string? title,
        string? message = null,
        string? buttonContent = null,
        Action? buttonAction = null,
        Action? closedAction = null
    )
    {
        Button? button = null;
        if (!string.IsNullOrWhiteSpace(buttonContent) && buttonAction != null)
        {
            button = new Button
            {
                Content = buttonContent,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            button.Click += (_, _) =>
            {
                try
                {
                    buttonAction();
                }
                catch { }
            };
        }
        var infoBar = new InfoBar
        {
            Severity = severity,
            Title = title,
            Message = message,
            ActionButton = button,
            IsOpen = true,
        };
        if (closedAction is not null)
        {
            infoBar.CloseButtonClick += (_, _) =>
            {
                try
                {
                    closedAction();
                }
                catch { }
            };
        }
        return infoBar;
    }
}
