using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starshot.Features.Setting;
using Starshot.Helpers;
using Windows.Graphics;
using Windows.UI;

namespace Starshot.Features.Screenshot;

/// <summary>Compact OCR editor. Clipboard changes only on an explicit copy action.</summary>
internal sealed class OcrResultWindow : Window
{
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    private static readonly HashSet<OcrResultWindow> OpenWindows = new();
    private readonly IReadOnlyList<OcrLine> _lines;
    private readonly TextBox _source = Editor("在这里修正识别错误和段落。");
    private readonly TextBox _translation = Editor("翻译结果会显示在这里，也可以继续编辑。");
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _editorTitle = new() { FontSize = 13,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _count = new() { FontSize = 12, Opacity = 0.62 };
    private readonly ComboBox _language = new() { MinWidth = 126 };
    private readonly Button _translate = new() { Content = "翻译", MinWidth = 82 };
    private readonly Button _copy = new() { MinWidth = 92 };
    private readonly Button _sourceTab = new() { Content = "原文", MinWidth = 72 };
    private readonly Button _translationTab = new() { Content = "译文", MinWidth = 72 };
    private readonly StackPanel _formatActions = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly CancellationTokenSource _close = new();
    private bool _busy, _autoTranslateStarted, _showTranslation;
    private ElementTheme _theme = ElementTheme.Light;

    public OcrResultWindow(IReadOnlyList<OcrLine> lines, bool translateImmediately = false)
    {
        _lines = lines;
        Title = "Starshot OCR";
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = Math.Max(1, GetDpiForWindow(hwnd) / 96.0);
        int windowWidth = (int)Math.Ceiling(760 * scale);
        int windowHeight = (int)Math.Ceiling(540 * scale);
        AppWindow.Resize(new SizeInt32(windowWidth, windowHeight));
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(workArea.X + Math.Max(0, (workArea.Width - windowWidth) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - windowHeight) / 2)));
        _source.Text = OcrTextFormatter.Paragraphs(lines);
        foreach (string language in new[] { "简体中文", "繁体中文", "英语", "日语", "韩语",
            "法语", "德语", "西班牙语", "葡萄牙语", "印尼语", "马来语", "意大利语",
            "俄语", "泰语", "越南语", "阿拉伯语", "土耳其语" })
            _language.Items.Add(language);
        _language.SelectedItem = AppConfig.TranslationTargetLanguage;
        if (_language.SelectedIndex < 0) _language.SelectedIndex = 0;

        var root = new Grid { Padding = new Thickness(20, 16, 20, 16), RowSpacing = 12 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headingText = new StackPanel { Spacing = 2 };
        headingText.Children.Add(new TextBlock { Text = "识别文字", FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        headingText.Children.Add(new TextBlock { Text = $"已识别 {lines.Count} 行 · 编辑后再复制或翻译",
            FontSize = 12, Opacity = 0.65 });
        heading.Children.Add(headingText);
        var settings = Action("API 设置", async () =>
        {
            var dialog = new TranslationSettingsDialog(root.XamlRoot);
            await dialog.ShowAsync();
            _language.SelectedItem = AppConfig.TranslationTargetLanguage;
        });
        settings.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(settings, 1);
        heading.Children.Add(settings);
        root.Children.Add(heading);

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _sourceTab.Click += (_, _) => ShowTranslation(false);
        _translationTab.Click += (_, _) => ShowTranslation(true);
        tabs.Children.Add(_sourceTab);
        tabs.Children.Add(_translationTab);
        Grid.SetRow(tabs, 1);
        root.Children.Add(tabs);

        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 14, 12),
            Background = new SolidColorBrush(Colors.White),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 226, 229, 235))
        };
        var cardLayout = new Grid { RowSpacing = 8 };
        cardLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cardLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var cardHeader = new Grid();
        cardHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cardHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cardHeader.Children.Add(_editorTitle);
        Grid.SetColumn(_count, 1);
        cardHeader.Children.Add(_count);
        cardLayout.Children.Add(cardHeader);
        Grid.SetRow(_source, 1);
        Grid.SetRow(_translation, 1);
        cardLayout.Children.Add(_source);
        cardLayout.Children.Add(_translation);
        card.Child = cardLayout;
        Grid.SetRow(card, 2);
        root.Children.Add(card);

        var actions = new Grid { ColumnSpacing = 10 };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _formatActions.Children.Add(Action("原始逐行", () => _source.Text = OcrTextFormatter.Lines(_lines)));
        _formatActions.Children.Add(Action("智能段落", () => _source.Text = OcrTextFormatter.Paragraphs(_lines)));
        _formatActions.VerticalAlignment = VerticalAlignment.Center;
        actions.Children.Add(_formatActions);
        var primaryActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        primaryActions.Children.Add(_language);
        _translate.Click += async (_, _) => await TranslateCurrentAsync();
        primaryActions.Children.Add(_translate);
        _copy.Click += (_, _) => Copy(_showTranslation ? _translation.Text : _source.Text);
        _copy.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        primaryActions.Children.Add(_copy);
        Grid.SetColumn(primaryActions, 1);
        actions.Children.Add(primaryActions);
        Grid.SetRow(actions, 3);
        root.Children.Add(actions);
        Grid.SetRow(_status, 4);
        root.Children.Add(_status);
        _source.TextChanged += (_, _) => UpdateCount();
        _translation.TextChanged += (_, _) => UpdateCount();
        ShowTranslation(false);
        _status.Text = "文字仅在点击复制时写入剪贴板。";
        void ApplyTheme()
        {
            _theme = root.ActualTheme;
            bool dark = _theme == ElementTheme.Dark;
            root.Background = new SolidColorBrush(dark
                ? Color.FromArgb(255, 21, 23, 27) : Color.FromArgb(255, 247, 249, 252));
            card.Background = new SolidColorBrush(dark
                ? Color.FromArgb(255, 34, 36, 40) : Colors.White);
            card.BorderBrush = new SolidColorBrush(dark
                ? Color.FromArgb(255, 63, 66, 72) : Color.FromArgb(255, 226, 229, 235));
            _copy.Background = new SolidColorBrush(Color.FromArgb(255, 43, 111, 221));
            _copy.Foreground = new SolidColorBrush(Colors.White);
            UpdateTabAppearance();
        }
        root.Loaded += (_, _) => ApplyTheme();
        root.ActualThemeChanged += (_, _) => ApplyTheme();

        Content = root;
        OpenWindows.Add(this);
        Closed += (_, _) => { _close.Cancel(); OpenWindows.Remove(this); _close.Dispose(); };
        Activate();
        if (translateImmediately)
            root.Loaded += async (_, _) =>
            {
                if (_autoTranslateStarted) return;
                _autoTranslateStarted = true;
                await TranslateCurrentAsync();
            };
    }

    private static TextBox Editor(string placeholder) => new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Stretch,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        BorderThickness = new Thickness(0),
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        FontSize = 15,
        PlaceholderText = placeholder
    };

    private void ShowTranslation(bool show)
    {
        _showTranslation = show;
        _source.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        _translation.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _formatActions.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        UpdateTabAppearance();
        _editorTitle.Text = show ? "翻译结果 · 可编辑" : "OCR 原文 · 可编辑";
        _copy.Content = show ? "复制译文" : "复制原文";
        UpdateCount();
    }

    private void UpdateCount() =>
        _count.Text = $"{(_showTranslation ? _translation.Text : _source.Text).Length} 字";

    private void UpdateTabAppearance()
    {
        bool dark = _theme == ElementTheme.Dark;
        var selected = new SolidColorBrush(_theme == ElementTheme.Dark
            ? Color.FromArgb(255, 45, 91, 160) : Color.FromArgb(255, 217, 233, 255));
        var selectedText = new SolidColorBrush(_theme == ElementTheme.Dark
            ? Colors.White : Color.FromArgb(255, 29, 78, 155));
        var idle = new SolidColorBrush(dark
            ? Color.FromArgb(255, 37, 40, 45) : Color.FromArgb(255, 235, 238, 243));
        var idleText = new SolidColorBrush(dark
            ? Color.FromArgb(255, 222, 225, 230) : Color.FromArgb(255, 62, 68, 78));
        _sourceTab.Background = _showTranslation ? idle : selected;
        _translationTab.Background = _showTranslation ? selected : idle;
        _sourceTab.Foreground = _showTranslation ? idleText : selectedText;
        _translationTab.Foreground = _showTranslation ? selectedText : idleText;
    }

    private static Button Action(string label, Action action)
    {
        var button = new Button { Content = label };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button Action(string label, Func<Task> action)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) => await action();
        return button;
    }

    private void Copy(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { _status.Text = "没有可复制的文字。"; return; }
        ClipboardHelper.SetText(text);
        _status.Text = "已复制到剪贴板。";
    }

    private async Task TranslateCurrentAsync()
    {
        if (_busy) return;
        _busy = true;
        _translate.IsEnabled = false;
        _status.Text = "正在翻译…";
        try
        {
            _translation.Text = await OcrTranslationClient.TranslateAsync(
                _source.Text, _language.SelectedItem as string ?? "简体中文", _close.Token);
            if (!_close.IsCancellationRequested)
            {
                ShowTranslation(true);
                _status.Text = "翻译完成；可继续编辑译文。";
            }
        }
        catch (OperationCanceledException) when (_close.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_close.IsCancellationRequested)
            {
                _status.Text = ex.Message;
                Serilog.Log.Warning("OCR translation failed: {Type}", ex.GetType().Name);
            }
        }
        finally
        {
            if (!_close.IsCancellationRequested) { _busy = false; _translate.IsEnabled = true; }
        }
    }
}
