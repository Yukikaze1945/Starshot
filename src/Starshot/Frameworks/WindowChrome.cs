using System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Starshot.Frameworks;

/// <summary>Shared, content-integrated chrome; Windows retains caption buttons and snap/resize.</summary>
internal static class WindowChrome
{
    internal static bool Configure(AppWindow window, bool compact = false)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return false;
        var bar = window.TitleBar;
        bar.ExtendsContentIntoTitleBar = true;
        bar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        bar.PreferredHeightOption = compact ? TitleBarHeightOption.Standard : TitleBarHeightOption.Tall;
        return true;
    }

    internal static void ApplyTheme(AppWindow window, bool dark)
    {
        if (!window.TitleBar.ExtendsContentIntoTitleBar) return;
        var bar = window.TitleBar;
        var ink = dark ? Color.FromArgb(255, 232, 236, 222) : Color.FromArgb(255, 36, 43, 36);
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = ink;
        bar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(255, 128, 138, 124) : Color.FromArgb(255, 139, 147, 130);
        bar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(255, 48, 57, 46) : Color.FromArgb(255, 231, 235, 220);
        bar.ButtonHoverForegroundColor = ink;
        bar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(255, 63, 73, 54) : Color.FromArgb(255, 221, 227, 205);
        bar.ButtonPressedForegroundColor = ink;
    }

    // Native utility pages keep their content and behavior, but use the same
    // paper/ink caption treatment instead of a separate system title strip.
    internal static void Attach(Window window, FrameworkElement dragArea)
    {
        if (!Configure(window.AppWindow)) return;
        window.SetTitleBar(dragArea);
        void Theme(FrameworkElement sender, object args)
        {
            bool dark = sender.ActualTheme == ElementTheme.Dark;
            ApplyTheme(window.AppWindow, dark);
            var paper = new SolidColorBrush(dark ? Color.FromArgb(255, 24, 27, 25) : Color.FromArgb(255, 245, 244, 236));
            if (window.Content is Panel panel) panel.Background = paper;
            else if (window.Content is Control control) control.Background = paper;
        }
        dragArea.ActualThemeChanged += Theme;
        window.Closed += (_, _) => dragArea.ActualThemeChanged -= Theme;
        Theme(dragArea, EventArgs.Empty);
    }

    internal static void AddHeader(Window window, string title)
    {
        if (window.Content is not FrameworkElement content) return;
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { Margin = new Thickness(20, 0, 144, 0) };
        header.Children.Add(new TextBlock { Text = "starshot   /   " + title, FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        window.Content = null;
        Grid.SetRow(content, 1);
        panel.Children.Add(header); panel.Children.Add(content); window.Content = panel;
        Attach(window, header);
    }
}
