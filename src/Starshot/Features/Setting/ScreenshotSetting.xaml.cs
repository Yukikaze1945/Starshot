using System;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Starshot.Features.Screenshot;
using Starshot.Frameworks;
using Starshot.Helpers;
using Starshot.Language;

namespace Starshot.Features.Setting;

public sealed partial class ScreenshotSetting : PageBase
{
    public int ScreenshotSDRFormat
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.ScreenCaptureSDRFormat = value;
            }
        }
    } = AppConfig.ScreenCaptureSDRFormat;

    public int ScreenshotHDRFormat
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.ScreenCaptureHDRFormat = value;
            }
        }
    } = AppConfig.ScreenCaptureHDRFormat;

    public int ScreenshotQuality
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.ScreenCaptureEncodeQuality = value;
            }
        }
    } = AppConfig.ScreenCaptureEncodeQuality;

    private bool _enableColorManagement = AppConfig.EnableScreenshotColorManagement;
    public bool EnableScreenshotColorManagement
    {
        get => _enableColorManagement;
        set
        {
            if (value && !_enableColorManagement)
            {
                // 打开前先校验主显示器 primaries；畸形（VM/无 ICC）则弹 Error 并弹回关，避免截图编码时 lcms2 崩溃
                _ = TryEnableColorManagementAsync();
                return;
            }
            if (SetProperty(ref _enableColorManagement, value))
            {
                AppConfig.EnableScreenshotColorManagement = value;
            }
        }
    }

    private async Task TryEnableColorManagementAsync()
    {
        bool ok = await ScreenCaptureService.CanEnableColorManagementAsync();
        if (ok)
        {
            _enableColorManagement = true;
            AppConfig.EnableScreenshotColorManagement = true;
            OnPropertyChanged(nameof(EnableScreenshotColorManagement));
        }
        else
        {
            InAppToast.MainWindow?.Error(
                (string?)null,
                Lang.Starshot_ColorManagementUnavailable,
                7000
            );
            OnPropertyChanged(nameof(EnableScreenshotColorManagement)); // 刷新绑定，UI 弹回关
        }
    }

    public bool AutoSaveUltraHDRJpeg
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.AutoSaveUltraHDRJpeg = value;
            }
        }
    } = AppConfig.AutoSaveUltraHDRJpeg;

    public bool DeleteHDRIfSDRContent
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.DeleteHDRIfSDRContent = value;
            }
        }
    } = AppConfig.DeleteHDRIfSDRContent;

    public bool UhdrCapacityManual
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
                AppConfig.UhdrCapacityManual = value;
        }
    } = AppConfig.UhdrCapacityManual;

    public double UhdrCapacityValue
    {
        get;
        set
        {
            if (!double.IsFinite(value)) return;
            value = Math.Clamp(value, 2, 32);
            if (SetProperty(ref field, value))
            {
                AppConfig.UhdrCapacityValue = value;
                OnPropertyChanged(nameof(UhdrCapacityStops));
                OnPropertyChanged(nameof(UhdrCapacityLabel));
            }
        }
    } = AppConfig.UhdrCapacityValue;

    public double UhdrCapacityStops
    {
        get => Math.Log2(UhdrCapacityValue);
        set
        {
            // Ignore feedback from the slider snapping a typed multiplier to its nearest step.
            if (Math.Abs(value - Math.Log2(UhdrCapacityValue)) > 0.005001)
                UhdrCapacityValue = Math.Round(Math.Pow(2, value), 2);
        }
    }

    public string UhdrCapacityLabel => $"{UhdrCapacityValue:0.##}×";

    private void UhdrCapacityPreset_Click(object sender, RoutedEventArgs e)
    {
        string preset = (string)((Button)sender).Tag;
        if (preset == "Auto")
        {
            UhdrCapacityManual = false;
            return;
        }
        UhdrCapacityValue = double.Parse(preset, System.Globalization.CultureInfo.InvariantCulture);
        UhdrCapacityManual = true;
    }

    public bool AutoCopyScreenshotToClipboard
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.AutoCopyScreenshotToClipboard = value;
            }
        }
    } = AppConfig.AutoCopyScreenshotToClipboard;

    public int CaptureMonitorSource
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.ScreenshotCaptureMonitorSource = value;
            }
        }
    } = AppConfig.ScreenshotCaptureMonitorSource;

    /// <summary>
    /// 自定义 SDR 白基准：开 = 用输入框值覆写全应用色调映射目标白；关（默认）= 跟随显示器。
    /// </summary>
    public bool SdrWhiteLevelCustomize
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.SdrWhiteLevelOverride = value ? (int)Math.Round(SdrWhiteLevelValue) : 0;
            }
        }
    } = AppConfig.SdrWhiteLevelOverride > 0;

    /// <summary>SDR 白基准输入框值（nit）。仅自定义开启时写入配置；未覆写时默认 250。</summary>
    public double SdrWhiteLevelValue
    {
        get;
        set
        {
            if (!double.IsFinite(value))
            {
                return;
            }
            if (SetProperty(ref field, value))
            {
                if (SdrWhiteLevelCustomize)
                {
                    AppConfig.SdrWhiteLevelOverride = (int)Math.Round(value);
                }
            }
        }
    } = AppConfig.SdrWhiteLevelOverride > 0
        ? AppConfig.SdrWhiteLevelOverride
        : 250;

    public ScreenshotSetting()
    {
        InitializeComponent();
        // Run 不在可视化树，x:Bind 不可靠，代码组 Inline；数字绿色区分正文
        TextBlock_DisplaySdrWhite.Inlines.Add(
            new Run { Text = Lang.ScreenshotSetting_SdrWhiteLevelCurrent }
        );
        TextBlock_DisplaySdrWhite.Inlines.Add(
            new Run
            {
                // 当前显示器的 SDR 白（不受覆写影响），无 HDR 屏时为 fallback 250
                Text =
                    $"{AppConfig.GetSdrWhiteLevelFromDisplays(DisplayArea.FindAll())} nit",
                Foreground = (Microsoft.UI.Xaml.Media.Brush)
                    Application.Current.Resources["SystemFillColorSuccessBrush"],
            }
        );
    }

    private async void Button_OcrEngineConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OcrEngineDialog { XamlRoot = this.XamlRoot };
        await dialog.ShowAsync();
    }

    private async void Button_TranslationConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TranslationSettingsDialog(this.XamlRoot);
        await dialog.ShowAsync();
    }
}
