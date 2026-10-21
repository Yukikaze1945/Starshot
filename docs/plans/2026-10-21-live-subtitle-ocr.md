# Starshot: fixed-position live subtitle OCR (MVP)

## Intent
Capture dialogue/subtitle text while a video plays, without opening a full-screen region selector, taking input focus, pausing the player, or taking mouse/keyboard input from other applications.

## How to use
1. Right-click the Starshot tray icon. Open **固定字幕 OCR（点击不挡视频） > 创建 / 调整字幕框**.
2. Move the title strip over the subtitle area. Drag the lower-right border to resize. The center of the box is transparent and passes mouse input to the video player.
3. Choose **锁定并开始识别**. The full box becomes click-through. OCR runs on the fixed screen rectangle.
4. Use **暂停 / 继续** if the video or layout changes. Choose **创建 / 调整字幕框** again to pause OCR and edit its location.
5. Choose **复制已识别文案**, **打开字幕文本文件**, or **结束字幕识别**.

The files go into `AppConfig.UserDataFolder/SubtitleOCR/subtitle-<local-time>-<session-id>.txt`.
Timestamp prefixes are **computer wall-clock times**. They are not timecodes from the video player.
No automatic clipboard operations and no upload to external OCR services.

## Architecture
- `SubtitleOcrWindow` is an independent, non-activating Win32 popup window. A color-key layered surface displays a title strip and outline, but never fills the subtitle content rectangle.
- During editing, the title strip and border can be dragged/resized. The inside is a transparent mouse pass-through region.
- After locking, `WS_EX_TRANSPARENT` makes the entire overlay click-through. `WDA_EXCLUDEFROMCAPTURE` is also requested, but never assumed sufficient to hide the border from every capture backend; the GDI capture rectangle is inside the border.
- `GdiCaptureFrame.Capture` acquires only the small screen ROI as owned BGRA8 bytes. No WGC capture session or D3D device is created.
- `OcrHelper.RecognizeAsync` reuses the current Tiny/Small OCR engine with its existing fallback. Captures and OCR jobs are serialized, with a 450 ms idle delay, cancellation, error backoff, and no per-frame window activation.
- `SubtitleOcrCollector` records new OCR lines. It ignores lines that remain visible and brief gaps/repeats, but allows the same dialogue to appear again later.
- New lines append to a local UTF-8 text file. A tray command can copy the current in-memory transcript.

## Scope and known limits
- V1 tracks a **fixed physical screen rectangle**, not a target video window. If the player moves, resizes, minimizes, or another app covers the region, OCR can read the wrong pixels. A window-bound WGC source is a separate future feature.
- Some DRM video and exclusive full-screen renderers cannot be captured through GDI. This feature does not bypass protected content.
- The 450 ms delay starts after OCR finishes. It is **not** a guaranteed 2+ FPS capture rate and may miss very short captions on slow CPUs.
- Subtitle text is recognized from an SDR desktop composite, even if the source content is HDR. This is not an HDR screenshot archival path.
- Consecutive caption duplication is filtered. Rolling subtitles, OCR misrecognitions, scene text, and multi-line captions still need real-world tuning.
- No auto-translation, audio transcription, image generation, or background video decoding in this MVP.

## Tests
A dependency-free collector test is at `tools/SubtitleOcrTest/`:
`dotnet run --project tools/SubtitleOcrTest/SubtitleOcrTest.csproj`

Windows manual acceptance checks:
- Start video in a browser/player, create the frame, adjust it, and lock.
- Click, scroll, and press playback controls *inside* the outlined area while the frame is locked. They must reach the player.
- Move the cursor/click other app windows. Starshot must not steal focus.
- Let a two-line subtitle remain visible for 10 seconds; each stable line should appear only once in the output.
- Show a new subtitle and verify it appends. Pause, reposition, and resume.
- Verify the file uses local clock timestamps, and no second screenshot window appears.
- Check a second monitor with negative coordinates, 150-250% DPI, 4K input, OCR engine unload, app exit, and repeated start/stop.
- Try paused/nonchanging video, GPU accelerated browser video, HDR, and protected content. Record limits rather than claiming success.

## Future follow-up
A window-relative capture mode (not based on screen position) would help when the user wants to work in front of the video window. WGC capture may help, but still cannot guarantee capture of minimized/protected windows.
