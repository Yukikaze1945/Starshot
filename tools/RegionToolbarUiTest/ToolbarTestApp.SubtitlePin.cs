using System.Reflection;
using Microsoft.UI.Windowing;
using Starshot.Features.ViewHost;

namespace Starshot.ToolbarValidation;

public sealed partial class ToolbarTestApp
{
    private void ValidateSubtitlePin()
    {
        const BindingFlags flags=BindingFlags.Static|BindingFlags.NonPublic;
        var type=typeof(MainWindow);
        var panelField=type.GetField("_subtitlePanel",flags)!;
        var topmost=type.GetProperty("SubtitlePanelTopmost",flags)!;
        var toggle=type.GetMethod("SetSubtitlePanelTopmost",flags)!;
        Check((bool)topmost.GetValue(null)!,"Subtitle pin defaults on");
        using var panel=new MainWindow(subtitlePopup:true);
        panel.ForceExit=true; // Do not initialize WebView2 from its queued callback.
        panelField.SetValue(null,panel);
        try
        {
            var presenter=(OverlappedPresenter)panel.AppWindow.Presenter;
            foreach(bool enabled in new[]{true,false,true})
            {
                toggle.Invoke(null,[enabled]);
                Check(presenter.IsAlwaysOnTop==enabled,"Pin action sets real native window topmost state");
                Check((bool)topmost.GetValue(null)! == enabled,"Pin status matches native presenter");
                Check(!panel.AppWindow.IsVisible,"Pin does not show/activate a hidden window");
            }
        }
        finally{panelField.SetValue(null,null);}
    }
}
