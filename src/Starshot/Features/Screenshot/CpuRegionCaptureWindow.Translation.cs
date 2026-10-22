using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Starshot.Features.Screenshot;

internal sealed partial class CpuRegionCaptureWindow
{
    private CancellationTokenSource? _translationCancellation;
    private string _translationStatus="";
    private RectangleF _translationStatusBounds;
    private void CancelRegionTranslation(){_translationCancellation?.Cancel();_translationStatus="";Redraw();}
    private async Task TranslateRegionInPlaceAsync()
    {
        if(_closed||!_selected||_source is null)return;
        if(_translationCancellation is not null){_translationStatus="识别仍在执行或取消收尾，请稍候。";Redraw();return;}
        using var cancellation=new CancellationTokenSource();_translationCancellation=cancellation;var selected=_selection;
        try
        {
            _translationStatus="正在识别并翻译选区… · Esc 取消";Redraw();
            byte[] pixels;using(var crop=_source.Crop(selected))pixels=crop.Pixels.ToArray();
            var lines=await Task.Run(()=>RegionTranslation.TranslateAsync(pixels,selected.Width,selected.Height,cancellation.Token),cancellation.Token);
            if(cancellation.IsCancellationRequested||_closed)return;
            if(!_selected||_selection!=selected){_translationStatus="选区已变化，请重新翻译。";return;}
            ApplyRegionTranslation(lines);_translationStatus=$"已覆盖 {lines.Length} 行 · Ctrl+Z 撤销 · Esc 隐藏提示";
        }
        catch(OperationCanceledException)when(cancellation.IsCancellationRequested){}
        catch(Exception ex){if(!_closed)_translationStatus=ex.Message;Serilog.Log.Warning(ex,"CPU region translation failed; overlay retained");}
        finally{if(ReferenceEquals(_translationCancellation,cancellation))_translationCancellation=null;if(!_closed)Redraw();}
    }
    private void ApplyRegionTranslation(RegionTranslatedText[] lines)
    {
        PushUndo();_annotations.Add(new(){Tool=CpuAnnotationTool.Translation,Start=_selection.Location,End=new Point(_selection.Right,_selection.Bottom),Translation=lines});
        _previewDirty=true;Redraw();
    }
    private void PaintTranslationStatus(nint dc,RegionToolbarLayout layout)
    {
        if(_translationStatus.Length==0)return;float s=layout.Monitor.Scale;
        var screen=RegionToolbarGeometry.ClampPanel(new(layout.Main.X,layout.Main.Top-48*s,Math.Min(460*s,layout.Monitor.WorkArea.Width),40*s),[layout.Monitor]);
        _translationStatusBounds=screen;
        var bounds=LocalRect(screen,_virtualX,_virtualY);PaintPanel(dc,bounds,s);
        CpuCaptureGdi.Text(dc,_translationStatus,new(bounds.X+8,bounds.Y,bounds.Width-16,bounds.Height),_toolbarLabelFont,Ink);
    }
}
