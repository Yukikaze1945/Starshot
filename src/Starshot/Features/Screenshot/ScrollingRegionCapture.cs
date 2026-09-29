using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Starshot.Helpers;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.DirectX;

namespace Starshot.Features.Screenshot;

/// <summary>Automatically matches scrolled frames and keeps the stitched image in memory until an output action.</summary>
internal static class ScrollingRegionCapture
{
    private sealed record Segment(byte[] Pixels, int Width, int Height);
    private const int MaxAxisPixels = 16000; // Win2D / GPU render-target limit.
    private const long MaxImageBytes = 256L * 1024 * 1024;

    public static async Task<string?> CaptureAsync(Rect initialSelection, CanvasRenderTarget firstFrame,
        CanvasRenderTarget? annotations, int virtualX, int virtualY, int virtualWidth, int virtualHeight)
    {
        int width = (int)initialSelection.Width, height = (int)initialSelection.Height;
        if (width < 2 || height < 2) return null;
        var desktop = new RectInt32(virtualX, virtualY, virtualWidth, virtualHeight);
        Rect selection = initialSelection;
        var frame = new ScrollingCaptureFrameWindow(ScreenRect(selection, virtualX, virtualY), desktop);
        var control = new ScrollingCaptureControlWindow(ScreenRect(selection, virtualX, virtualY), desktop);
        frame.SelectionRequested += control.RequestSelection;
        byte[] previous = firstFrame.GetPixelBytes();
        var segments = new List<Segment> { new(previous, width, height) };
        bool horizontal = false, running = false, hasStarted = false;
        int travel = 0; // +1: down/right, -1: up/left; learned from the first scroll.
        byte[]? reverseCandidate = null;
        int reverseCandidateShift = 0;
        int reverseCandidateFrames = 0;
        bool pendingSelectionReset = false;
        DateTime lastSelectionChange = DateTime.MinValue;
        int total = height, trimLeading = 0, trimTrailing = 0, autoTrim = 0, timeoutCount = 0;
        DateTime nextCapture = DateTime.UtcNow;
        DateTime lastDiagnosticLog = DateTime.MinValue;
        string lastDiagnostic = "";
        try
        {
            try
            {
                control.SetProgress(width, height, 1, "正在准备长截图…");
                await ResetFromScreenAsync();
                hasStarted = running = true;
                frame.CanResize = false;
                control.SetRunning(true, horizontal);
                control.SetProgress(width, height, 1, "拼接中 · 滚动目标内容");
                control.ClearNotice();
                await RefreshPreviewAsync();
                Serilog.Log.Information("Long capture started automatically: selection={Width}x{Height}", width, height);
            }
            catch (Exception ex)
            {
                control.SetProgress(width, height, 1, "起始帧获取失败 · 点击开始重试");
                control.SetNotice("起始帧获取失败 · 点击开始重试");
                Serilog.Log.Warning(ex, "Long capture initial frame failed");
            }
            while (!control.IsClosed)
            {
                bool changed = false;
                while (control.TryDequeue(out ScrollCaptureCommand command))
                {
                    switch (command.Action)
                    {
                        case ScrollCaptureAction.Cancel: return null;
                        case ScrollCaptureAction.StartStop:
                            if (running)
                            {
                                running = false;
                                reverseCandidate = null;
                                reverseCandidateFrames = 0;
                                Serilog.Log.Information("Long capture stopped: frames={Frames}, travel={Travel}", segments.Count, travel);
                            }
                            else
                            {
                                try
                                {
                                    if (!hasStarted || pendingSelectionReset)
                                    {
                                        await ResetFromScreenAsync();
                                        hasStarted = true;
                                        pendingSelectionReset = false;
                                    }
                                    running = true;
                                    Serilog.Log.Information("Long capture resumed: frames={Frames}, travel={Travel}", segments.Count, travel);
                                }
                                catch (Exception ex)
                                {
                                    control.SetProgress(width, height, segments.Count, "起始帧获取失败 · 点击开始重试");
                                    control.SetNotice("起始帧获取失败 · 点击开始重试");
                                    Serilog.Log.Warning(ex, "Long capture start failed");
                                    break;
                                }
                            }
                            frame.CanResize = !running;
                            control.SetRunning(running, horizontal);
                            control.SetProgress(horizontal ? total : width, horizontal ? height : total,
                                segments.Count, running ? "拼接中 · 滚动目标内容" : "已停止 · 可调整区域或导出");
                            control.ClearNotice();
                            nextCapture = DateTime.UtcNow;
                            changed = true;
                            break;
                        case ScrollCaptureAction.Direction when !running:
                            horizontal = !horizontal;
                            segments.Clear();
                            segments.Add(new Segment(previous, width, height));
                            total = horizontal ? width : height;
                            trimLeading = trimTrailing = autoTrim = travel = 0;
                            reverseCandidate = null;
                            reverseCandidateFrames = 0;
                            control.SetRunning(false, horizontal);
                            changed = true;
                            break;
                        case ScrollCaptureAction.TrimLeading when !running:
                            trimLeading = Math.Min(trimLeading + 50,
                                Math.Max(0, total - trimTrailing - (travel < 0 ? autoTrim : 0) - 64));
                            changed = true;
                            break;
                        case ScrollCaptureAction.TrimTrailing when !running:
                            trimTrailing = Math.Min(trimTrailing + 50,
                                Math.Max(0, total - trimLeading - (travel >= 0 ? autoTrim : 0) - 64));
                            changed = true;
                            break;
                        case ScrollCaptureAction.ResetTrim when !running:
                            trimLeading = trimTrailing = autoTrim = 0;
                            changed = true;
                            break;
                        case ScrollCaptureAction.Move:
                        {
                            int dx = running && !horizontal ? 0 : command.X;
                            int dy = running && horizontal ? 0 : command.Y;
                            int x = Math.Clamp((int)selection.X + dx, 0, virtualWidth - width);
                            int y = Math.Clamp((int)selection.Y + dy, 0, virtualHeight - height);
                            if (x == selection.X && y == selection.Y) break;
                            selection = new Rect(x, y, width, height);
                            frame.Move(ScreenRect(selection, virtualX, virtualY));
                            control.SetSelection(ScreenRect(selection, virtualX, virtualY));
                            if (!running)
                            {
                                pendingSelectionReset = true;
                                lastSelectionChange = DateTime.UtcNow;
                            }
                            changed = true;
                            break;
                        }
                        case ScrollCaptureAction.SetSelection when !running:
                        {
                            if (command.Width < 64 || command.Height < 64) break;
                            int x = Math.Clamp(command.X - virtualX, 0, virtualWidth - command.Width);
                            int y = Math.Clamp(command.Y - virtualY, 0, virtualHeight - command.Height);
                            width = command.Width; height = command.Height;
                            selection = new Rect(x, y, width, height);
                            frame.Move(ScreenRect(selection, virtualX, virtualY));
                            control.SetSelection(ScreenRect(selection, virtualX, virtualY));
                            pendingSelectionReset = true;
                            lastSelectionChange = DateTime.UtcNow;
                            changed = true;
                            break;
                        }
                        case ScrollCaptureAction.Pin:
                        case ScrollCaptureAction.Save:
                        case ScrollCaptureAction.QuickSave:
                        case ScrollCaptureAction.Copy:
                        {
                            if (pendingSelectionReset)
                            {
                                await ResetFromScreenAsync();
                                pendingSelectionReset = false;
                            }
                            using var image = Stitch(segments, horizontal, EffectiveTrimLeading(),
                                EffectiveTrimTrailing(), travel < 0 ? total - (horizontal ? width : height) : 0,
                                annotations);
                            if (command.Action == ScrollCaptureAction.Pin)
                            {
                                double zoom = Math.Min(1, Math.Min(900.0 / image.SizeInPixels.Width,
                                    700.0 / image.SizeInPixels.Height));
                                _ = new PinnedCaptureWindow(image,
                                    virtualX + (int)selection.X, virtualY + (int)selection.Y, zoom);
                                return null;
                            }
                            if (command.Action == ScrollCaptureAction.Copy)
                            {
                                await ScreenCaptureService.CopyCaptureToClipboardAsync(image, force: true);
                                return null;
                            }
                            string? path = command.Action == ScrollCaptureAction.Save
                                ? await FileDialogHelper.OpenSaveFileDialogAsync(control.Hwnd,
                                    $"Starshot_Long_{DateTime.Now:yyyyMMdd_HHmmss}", ("PNG 图像", ".png"))
                                : QuickSavePath((int)image.SizeInPixels.Width, (int)image.SizeInPixels.Height);
                            if (path is null) break;
                            bool saved = false;
                            try { await image.SaveAsync(path, CanvasBitmapFileFormat.Png); saved = true; }
                            finally { if (!saved) { try { File.Delete(path); } catch { } } }
                            Serilog.Log.Information("Long screenshot saved: {Path}, frames={Frames}", path, segments.Count);
                            return path;
                        }
                    }
                }
                if (pendingSelectionReset && !frame.IsDragging &&
                    DateTime.UtcNow - lastSelectionChange >= TimeSpan.FromMilliseconds(180))
                {
                    await ResetFromScreenAsync();
                    pendingSelectionReset = false;
                }
                if (changed)
                {
                    control.ClearNotice();
                    string message = running ? "拼接中 · 滚动目标内容"
                        : trimLeading + trimTrailing + autoTrim > 0
                            ? $"已裁剪：起点 {EffectiveTrimLeading()}px · 终点 {EffectiveTrimTrailing()}px"
                            : "已停止，可裁剪或导出";
                    control.SetProgress(horizontal ? total - EffectiveTrimLeading() - EffectiveTrimTrailing() : width,
                        horizontal ? height : total - EffectiveTrimLeading() - EffectiveTrimTrailing(),
                        segments.Count, message);
                    if (control.HasPreview) await RefreshPreviewAsync();
                }
                if (!running || DateTime.UtcNow < nextCapture)
                {
                    await Task.Delay(80);
                    continue;
                }
                nextCapture = DateTime.UtcNow.AddMilliseconds(220);
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    // Annotations stay separate so a fixed mark cannot corrupt scroll matching.
                    byte[] current = await GifRegionRecorder.CaptureStillAsync(selection, null,
                        virtualX, virtualY, virtualWidth, virtualHeight, timeout.Token);
                    var forward = ScrollingFrameMatcher.Analyze(previous, current, width, height, horizontal, false);
                    var reverse = ScrollingFrameMatcher.Analyze(previous, current, width, height, horizontal, true);
                    if (travel == 0)
                    {
                        if (forward.Shift >= 8 && reverse.Shift >= 8)
                        {
                            if (forward.Error < reverse.Error * 0.8) travel = 1;
                            else if (reverse.Error < forward.Error * 0.8) travel = -1;
                            else { ReportDiagnostic("ambiguous-direction", forward); continue; }
                        }
                        else if (forward.Shift >= 8) travel = 1;
                        else if (reverse.Shift >= 8) travel = -1;
                        if (travel != 0)
                            Serilog.Log.Information("Long capture direction established: axis={Axis}, travel={Travel}",
                                horizontal ? "horizontal" : "vertical", travel < 0 ? "up-left" : "down-right");
                    }
                    var primary = travel < 0 ? reverse : forward;
                    var opposite = travel < 0 ? forward : reverse;
                    if (travel != 0 && primary.Shift < 8 && opposite.Shift >= 8)
                    {
                        if (reverseCandidate is not null)
                        {
                            var confirmed = ScrollingFrameMatcher.Analyze(reverseCandidate, current,
                                width, height, horizontal, travel > 0);
                            if (confirmed.Shift >= 8)
                            {
                                reverseCandidateShift += confirmed.Shift;
                                reverseCandidate = current;
                                if (++reverseCandidateFrames < 3)
                                {
                                    ReportDiagnostic("confirming-direction", primary);
                                    continue;
                                }
                                int reverseShift = reverseCandidateShift;
                                reverseCandidate = null;
                                reverseCandidateFrames = 0;
                                if (control.AutoCropEnabled)
                                {
                                    autoTrim = Math.Min(autoTrim + reverseShift,
                                        Math.Max(0, total - trimLeading - trimTrailing - 64));
                                    previous = current;
                                    control.SetProgress(horizontal ? total - EffectiveTrimLeading() - EffectiveTrimTrailing() : width,
                                        horizontal ? height : total - EffectiveTrimLeading() - EffectiveTrimTrailing(),
                                        segments.Count, $"反向滚动 · 自动裁剪 {reverseShift} px");
                                    control.ClearNotice();
                                    Serilog.Log.Information("Long capture reverse trim: shift={Shift}, travel={Travel}", reverseShift, travel);
                                    await RefreshPreviewAsync();
                                }
                                else ReportDiagnostic("reverse-ignored", primary);
                                continue;
                            }
                            if (confirmed.Reason == "unchanged") continue;
                        }
                        reverseCandidate = current;
                        reverseCandidateShift = opposite.Shift;
                        reverseCandidateFrames = 1;
                        ReportDiagnostic("confirming-direction", primary);
                        continue;
                    }
                    if (travel == 0 || primary.Shift < 8)
                    {
                        if (forward.Reason != "unchanged")
                        {
                            reverseCandidate = null;
                            reverseCandidateFrames = 0;
                        }
                        ReportDiagnostic(forward.Reason == "unchanged" ? "unchanged" :
                            forward.Reason == "ambiguous" || reverse.Reason == "ambiguous" ? "ambiguous" :
                            "no-overlap", primary);
                        continue;
                    }
                    int newPixels = Math.Max(0, primary.Shift - autoTrim);
                    reverseCandidate = null;
                    reverseCandidateFrames = 0;
                    autoTrim = Math.Max(0, autoTrim - primary.Shift);
                    if (newPixels == 0)
                    {
                        previous = current;
                        await RefreshPreviewAsync();
                        continue;
                    }
                    if ((long)(horizontal ? total + newPixels : width) *
                        (horizontal ? height : total + newPixels) * 4 > MaxImageBytes ||
                        total + newPixels > MaxAxisPixels)
                    {
                        running = false; frame.CanResize = true; control.SetRunning(false, horizontal);
                        control.SetProgress(horizontal ? total : width, horizontal ? height : total,
                            segments.Count, "已达到图像尺寸上限，请保存或复制");
                        control.SetNotice("已达到图像尺寸上限 · 请保存或复制");
                        continue;
                    }
                    var strip = ExtractNewStrip(current, width, height, newPixels, horizontal, travel);
                    if (travel < 0) segments.Insert(0, strip);
                    else segments.Add(strip);
                    total += newPixels;
                    previous = current;
                    timeoutCount = 0;
                    control.SetProgress(horizontal ? total - EffectiveTrimLeading() - EffectiveTrimTrailing() : width,
                        horizontal ? height : total - EffectiveTrimLeading() - EffectiveTrimTrailing(),
                        segments.Count, $"已拼接 {segments.Count} 帧 · 新增 {newPixels} px");
                    control.ClearNotice();
                    Serilog.Log.Information("Long capture matched: axis={Axis}, travel={Travel}, shift={Shift}, error={Error:F2}, frames={Frames}",
                        horizontal ? "horizontal" : "vertical", travel < 0 ? "up-left" : "down-right",
                        newPixels, primary.Error, segments.Count);
                    await RefreshPreviewAsync();
                }
                catch (OperationCanceledException)
                {
                    if (++timeoutCount >= 3)
                    {
                        running = false; frame.CanResize = true; control.SetRunning(false, horizontal);
                        control.SetProgress(horizontal ? total : width, horizontal ? height : total,
                            segments.Count, "捕获超时，已暂停；可继续或导出已有画面");
                        control.SetNotice("捕获超时 · 已暂停，可继续或导出");
                    }
                }
                catch (Exception ex)
                {
                    running = false; frame.CanResize = true; control.SetRunning(false, horizontal);
                    control.SetProgress(horizontal ? total : width, horizontal ? height : total,
                        segments.Count, "捕获失败，已暂停；可导出已有画面");
                    control.SetNotice("捕获失败 · 已暂停，可导出已有画面");
                    Serilog.Log.Error(ex, "Long capture frame failed");
                }
            }
            return null;
        }
        finally { control.CloseIfOpen(); frame.Close(); }

        async Task ResetFromScreenAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            previous = await GifRegionRecorder.CaptureStillAsync(selection, null,
                virtualX, virtualY, virtualWidth, virtualHeight, timeout.Token);
            segments.Clear(); segments.Add(new Segment(previous, width, height));
            total = horizontal ? width : height;
            trimLeading = trimTrailing = autoTrim = travel = 0;
            reverseCandidate = null;
            reverseCandidateFrames = 0;
        }

        async Task RefreshPreviewAsync()
        {
            using var image = Stitch(segments, horizontal, EffectiveTrimLeading(),
                EffectiveTrimTrailing(), travel < 0 ? total - (horizontal ? width : height) : 0,
                annotations);
            await control.UpdatePreviewAsync(image, width, height, horizontal, travel);
        }

        int EffectiveTrimLeading() => trimLeading + (travel < 0 ? autoTrim : 0);
        int EffectiveTrimTrailing() => trimTrailing + (travel >= 0 ? autoTrim : 0);

        void ReportDiagnostic(string reason, ScrollingFrameMatcher.MatchResult result)
        {
            string message = reason switch
            {
                "unchanged" => "未检测到页面滚动 · 将鼠标放在可滚动内容上",
                "reverse-ignored" => "反向滚动未追加 · 可开启自动裁剪",
                "confirming-direction" => "正在确认滚动方向…",
                "ambiguous" or "ambiguous-direction" => "内容重复，暂无法可靠确定拼接位置",
                _ => "画面已变化但没有足够重叠 · 请放慢滚动或滚回上一段"
            };
            control.SetProgress(horizontal ? total - EffectiveTrimLeading() - EffectiveTrimTrailing() : width,
                horizontal ? height : total - EffectiveTrimLeading() - EffectiveTrimTrailing(),
                segments.Count, message);
            control.SetNotice(message);
            if (reason != lastDiagnostic || DateTime.UtcNow - lastDiagnosticLog > TimeSpan.FromSeconds(2))
            {
                Serilog.Log.Information("Long capture unmatched: reason={Reason}, axis={Axis}, travel={Travel}, baseline={Baseline:F2}, error={Error:F2}",
                    reason, horizontal ? "horizontal" : "vertical", travel, result.Baseline, result.Error);
                lastDiagnostic = reason;
                lastDiagnosticLog = DateTime.UtcNow;
            }
        }
    }

    private static RectInt32 ScreenRect(Rect selection, int virtualX, int virtualY) =>
        new(virtualX + (int)selection.X, virtualY + (int)selection.Y,
            (int)selection.Width, (int)selection.Height);

    private static Segment ExtractNewStrip(byte[] pixels, int width, int height, int shift,
        bool horizontal, int travel)
    {
        if (!horizontal)
        {
            byte[] strip = new byte[checked(width * shift * 4)];
            Buffer.BlockCopy(pixels, (travel < 0 ? 0 : height - shift) * width * 4,
                strip, 0, strip.Length);
            return new Segment(strip, width, shift);
        }
        byte[] columns = new byte[checked(shift * height * 4)];
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(pixels, (y * width + (travel < 0 ? 0 : width - shift)) * 4,
                columns, y * shift * 4, shift * 4);
        return new Segment(columns, shift, height);
    }

    private static CanvasRenderTarget Stitch(IReadOnlyList<Segment> segments, bool horizontal,
        int trimLeading, int trimTrailing, int initialOffset, CanvasRenderTarget? annotations)
    {
        int axis = 0;
        foreach (Segment segment in segments) axis += horizontal ? segment.Width : segment.Height;
        int width = horizontal ? axis - trimLeading - trimTrailing : segments[0].Width;
        int height = horizontal ? segments[0].Height : axis - trimLeading - trimTrailing;
        var device = CanvasDevice.GetSharedDevice();
        var output = new CanvasRenderTarget(device, width, height, 96,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        try
        {
            using var ds = output.CreateDrawingSession();
            ds.Clear(Colors.Transparent);
            int position = -trimLeading;
            foreach (Segment segment in segments)
            {
                using var bitmap = CanvasBitmap.CreateFromBytes(device, segment.Pixels,
                    segment.Width, segment.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized);
                ds.DrawImage(bitmap, horizontal ? position : 0, horizontal ? 0 : position);
                position += horizontal ? segment.Width : segment.Height;
            }
            if (annotations is not null)
                ds.DrawImage(annotations, horizontal ? initialOffset - trimLeading : 0,
                    horizontal ? 0 : initialOffset - trimLeading);
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    private static string QuickSavePath(int width, int height)
    {
        string root = string.IsNullOrWhiteSpace(AppConfig.ScreenshotFolder)
            ? Path.Combine(AppConfig.UserDataFolder, "Screenshots") : AppConfig.ScreenshotFolder;
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        string stem = $"Starshot_Long_{DateTime.Now:yyyyMMdd_HHmmss}_{width}x{height}";
        string path = Path.Combine(root, stem + ".png");
        for (int suffix = 2; File.Exists(path); suffix++)
            path = Path.Combine(root, $"{stem}_{suffix}.png");
        return path;
    }
}
