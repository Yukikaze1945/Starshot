using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starshot.Helpers;

namespace Starshot.Features.Setting;

internal sealed partial class TranslationSettingsDialog : ContentDialog
{
    private readonly TextBox _url = new() { Header = "Chat Completions API 地址" };
    private readonly TextBox _model = new() { Header = "模型名称" };
    private readonly ComboBox _availableModels = new()
    {
        PlaceholderText = "从检测到的模型中选择",
        Visibility = Visibility.Collapsed,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly Button _discover = new() { Content = "检测模型", MinWidth = 92 };
    private readonly TextBlock _modelState = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
        FontSize = 12
    };
    private readonly PasswordBox _key = new() { Header = "API Key" };
    private readonly ComboBox _language = new() { Header = "默认目标语言" };
    private readonly TextBlock _keyState = new();
    private readonly TextBlock _error = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed
    };
    private readonly CancellationTokenSource _lifetime = new();

    public TranslationSettingsDialog(XamlRoot root)
    {
        XamlRoot = root;
        Title = "OCR 翻译设置";
        PrimaryButtonText = "保存";
        CloseButtonText = "取消";
        DefaultButton = ContentDialogButton.Primary;
        _url.Text = AppConfig.TranslationApiUrl;
        _model.Text = AppConfig.TranslationModel;
        _availableModels.SelectionChanged += (_, _) =>
        {
            if (_availableModels.SelectedItem is string selected) _model.Text = selected;
        };
        _discover.Click += async (_, _) => await DiscoverModelsAsync();
        _key.PlaceholderText = "留空则保留已保存的密钥";
        foreach (string language in new[] { "简体中文", "繁体中文", "英语", "日语", "韩语",
            "法语", "德语", "西班牙语", "葡萄牙语", "印尼语", "马来语", "意大利语",
            "俄语", "泰语", "越南语", "阿拉伯语", "土耳其语" })
            _language.Items.Add(language);
        _language.SelectedItem = AppConfig.TranslationTargetLanguage;
        if (_language.SelectedIndex < 0) _language.SelectedIndex = 0;
        var clear = new Button { Content = "移除密钥" };
        clear.Click += (_, _) =>
        {
            OcrTranslationClient.ClearApiKey();
            _key.Password = "";
            UpdateKeyState();
        };
        UpdateKeyState();
        var modelRow = new Grid { ColumnSpacing = 8 };
        modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        modelRow.Children.Add(_model);
        Grid.SetColumn(_discover, 1);
        _discover.VerticalAlignment = VerticalAlignment.Bottom;
        modelRow.Children.Add(_discover);
        var keyRow = new Grid();
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _keyState.VerticalAlignment = VerticalAlignment.Center;
        keyRow.Children.Add(_keyState);
        Grid.SetColumn(clear, 1);
        keyRow.Children.Add(clear);
        Content = new ScrollViewer
        {
            MaxHeight = 350,
            Content = new StackPanel
            {
                Width = 470,
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text = "连接 OpenAI 兼容接口，识别文字仅在翻译时发送。",
                        TextWrapping = TextWrapping.Wrap
                    },
                    _url, modelRow, _availableModels, _modelState,
                    _key, keyRow, _language, _error
                }
            }
        };
        PrimaryButtonClick += Save;
        Loaded += async (_, _) =>
        {
            if (OcrTranslationClient.HasApiKey) await DiscoverModelsAsync();
        };
        Closed += (_, _) => { _lifetime.Cancel(); _lifetime.Dispose(); };
    }

    private async Task DiscoverModelsAsync()
    {
        _discover.IsEnabled = false;
        _modelState.Visibility = Visibility.Visible;
        _modelState.Text = "正在读取模型列表…";
        try
        {
            var models = await OcrTranslationClient.GetModelsAsync(
                _url.Text.Trim(), _key.Password, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            _availableModels.Items.Clear();
            foreach (string model in models) _availableModels.Items.Add(model);
            _availableModels.Visibility = models.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _modelState.Text = models.Count == 0
                ? "接口没有返回模型；可手动输入模型名称。"
                : $"已检测到 {models.Count} 个模型。列表也可能包含非聊天模型。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            if (!_lifetime.IsCancellationRequested)
                _modelState.Text = "读取模型列表超时；可重试或手动输入模型名称。";
        }
        catch (Exception ex)
        {
            if (!_lifetime.IsCancellationRequested)
            {
                _availableModels.Visibility = Visibility.Collapsed;
                _modelState.Text = ex.Message;
            }
        }
        finally
        {
            if (!_lifetime.IsCancellationRequested) _discover.IsEnabled = true;
        }
    }

    private void UpdateKeyState() =>
        _keyState.Text = OcrTranslationClient.HasApiKey ? "已保存 API Key" : "尚未配置 API Key";

    private void Save(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!OcrTranslationClient.IsValidEndpoint(_url.Text.Trim()))
        {
            _error.Visibility = Visibility.Visible;
            _error.Text = "API 地址无效；远程服务必须使用 HTTPS，本机服务可使用 HTTP。";
            args.Cancel = true;
            return;
        }
        if (string.IsNullOrWhiteSpace(_model.Text))
        {
            _error.Visibility = Visibility.Visible;
            _error.Text = "请输入模型名称。";
            args.Cancel = true;
            return;
        }
        try
        {
            if (!string.IsNullOrWhiteSpace(_key.Password))
                OcrTranslationClient.SaveApiKey(_key.Password);
            AppConfig.TranslationApiUrl = _url.Text.Trim();
            AppConfig.TranslationModel = _model.Text.Trim();
            AppConfig.TranslationTargetLanguage = (string)_language.SelectedItem;
        }
        catch (Exception ex)
        {
            _error.Visibility = Visibility.Visible;
            _error.Text = $"保存翻译设置失败：{ex.Message}";
            args.Cancel = true;
        }
    }
}
