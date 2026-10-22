using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Starshot.Features.Subtitles;
using Starshot.Features.ViewHost;
using Starshot.Helpers;

namespace Starshot.Features.WebUI;

internal sealed partial class WebUiBridge
{
    private async Task<object?> SubtitlesAsync(string method,JsonElement p,CancellationToken ct)
    {
        switch(method)
        {
            case "subtitles.open":MainWindow.OpenSubtitlePanel();return null;
            case "subtitles.status":return SubtitleController.Snapshot();
            case "subtitles.configure":_window.Hide();await SubtitleController.ConfigureAsync(p.TryGetProperty("fresh",out var fresh)&&fresh.GetBoolean());break;
            case "subtitles.close":MainWindow.CloseSubtitlePanel();return null;
            case "subtitles.topmost":MainWindow.SetSubtitlePanelTopmost(p.GetProperty("value").GetBoolean());break;
            case "subtitles.start":SubtitleController.Start();break;
            case "subtitles.pause":SubtitleController.Pause();break;
            case "subtitles.stop":await SubtitleController.StopAsync();break;
            case "subtitles.interval":SubtitleController.Interval(Range(p.GetProperty("value"),200,600));break;
            case "subtitles.text":return new{text=SubtitleController.Text()};
            case "subtitles.edit":
                SubtitleController.Pause();string draft=SubtitleController.Text();if(string.IsNullOrWhiteSpace(draft))throw new InvalidOperationException("还没有可整理的文案。");
                var lines=draft.Split('\n').Select((line,i)=>new OcrLine(line.TrimEnd('\r'),new Windows.Foundation.Rect(0,i*24,Math.Max(1,line.Length*8),18),[])).ToArray();
                var editor=new MainWindow(ocrPopup:true);try{editor.ShowOcr(lines,false,autoCopyAllowed:false);}catch{editor.Dispose();throw;}break;
            case "subtitles.copy":
                string text=SubtitleController.Text();if(string.IsNullOrWhiteSpace(text))throw new InvalidOperationException("还没有可复制的字幕。");ClipboardHelper.SetText(text);return new{copied=true};
            case "subtitles.export":
                string content=SubtitleController.Text();if(string.IsNullOrWhiteSpace(content))throw new InvalidOperationException("还没有可导出的字幕。");
                string? path=await FileDialogHelper.OpenSaveFileDialogAsync(_window.WindowHandle,"字幕文案",("纯文本",".txt"));if(path is null)return null;
                string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                try{await File.WriteAllTextAsync(temp,content,new UTF8Encoding(false),ct);ct.ThrowIfCancellationRequested();File.Move(temp,path,true);}finally{File.Delete(temp);}
                return new{saved=true};
            default:throw new ArgumentException("未知字幕摘录操作。");
        }
        return SubtitleController.Snapshot();
    }
}
