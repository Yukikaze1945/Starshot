using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace Starshot.Features.Screenshot;

public sealed partial class RegionCaptureWindow
{
    private CancellationTokenSource? _translationCancellation;
    private void CancelRegionTranslation(){_translationCancellation?.Cancel();TranslationNotice.Visibility=Visibility.Collapsed;}
    private void TranslationCancel_Click(object sender,RoutedEventArgs e)=>CancelRegionTranslation();
    private void TranslationNoticeText(string text)
    {
        if(_isClosed||_toolbarLayout is null)return;
        TranslationNoticeLabel.Text=text;TranslationNotice.Visibility=Visibility.Visible;
        var l=_toolbarLayout;float s=l.Monitor.Scale;
        var bounds=RegionToolbarGeometry.ClampPanel(new(l.Main.X,l.Main.Top-52*s,Math.Min(380*s,l.Monitor.WorkArea.Width),44*s),[l.Monitor]);
        PlaceToolbarPanel(TranslationNotice,bounds);
    }
    private async Task TranslateRegionInPlaceAsync()
    {
        if(_isClosed||_state!=RegionCaptureState.Selected)return;
        if(_translationCancellation is not null){TranslationNoticeText("识别仍在执行或取消收尾，请稍候。");return;}
        using var cancellation=new CancellationTokenSource();_translationCancellation=cancellation;
        long generation=_captureGeneration;var selected=GetPhysicalSourceRect();
        try
        {
            TranslationNoticeText("正在识别并翻译当前选区…");
            byte[] pixels;int w,h;
            using(var crop=CropDisplayToBgra()){w=(int)crop.SizeInPixels.Width;h=(int)crop.SizeInPixels.Height;pixels=crop.GetPixelBytes();}
            var lines=await Task.Run(()=>RegionTranslation.TranslateAsync(pixels,w,h,cancellation.Token),cancellation.Token);
            if(cancellation.IsCancellationRequested||_isClosed||generation!=_captureGeneration)return;
            if(_state!=RegionCaptureState.Selected||GetPhysicalSourceRect()!=selected){TranslationNoticeText("选区已变化，请重新翻译。");return;}
            ApplyRegionTranslation(lines);
            TranslationNoticeText($"已覆盖 {lines.Length} 行 · Ctrl+Z 撤销 · 复制/保存包含译文");
        }
        catch(OperationCanceledException)when(cancellation.IsCancellationRequested){}
        catch(Exception ex){if(!_isClosed&&generation==_captureGeneration)TranslationNoticeText(ex.Message);Serilog.Log.Warning(ex,"Region translation failed; overlay retained");}
        finally{if(ReferenceEquals(_translationCancellation,cancellation))_translationCancellation=null;}
    }
    private void ApplyRegionTranslation(RegionTranslatedText[] lines)
    {
        var a=new Annotation{Tool=AnnotationTool.Translation,Start=new Point(SelectionRect.X,SelectionRect.Y),End=new Point(SelectionRect.Right,SelectionRect.Bottom),
            Translation=lines.Select(l=>l with{Bounds=new(l.Bounds.X/_scale,l.Bounds.Y/_scale,l.Bounds.Width/_scale,l.Bounds.Height/_scale),FontSize=l.FontSize/_scale}).ToArray()};
        _undoEdits.Push((a,_annotations.Count,true));_annotations.Add(a);_redoEdits.Clear();
        RefreshToolbarAvailability();RequestRedraw();
    }
}
