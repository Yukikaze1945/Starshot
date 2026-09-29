# Region capture reference style and tools

Goal: make Starshot's region capture match the supplied blue-selection screenshot in appearance and interaction while retaining the working WGC pause/resume and HDR save pipeline.

## Existing baseline

- `RegionCaptureWindow.xaml(.cs)` owns the frozen preview, selection, magnifier, and post-selection toolbar.
- `ScreenCaptureService.cs` owns crop, save, copy, and OCR. It pauses `MonitorCaptureContext` background frame copies while the overlay is open.
- The reference shows a blue border and round handles, black position/size tag, pixel magnifier with color readout, shortcut hints, and a dense bottom-right editing toolbar.

## Implementation

1. Match the selection presentation: outside dim, bright blue border, eight round handles, black position/size tag, toolbar anchored to selection, and compact magnifier with RGB/HEX readout. Keep all controls inside virtual-desktop bounds and scale for DPI.
2. Add real editing tools represented in the reference: shapes, line/arrow, freehand/highlighter, numbered marker, mosaic, text, eraser, undo/redo, and color/width controls. Maintain an annotation model separate from the frozen frame and use event-driven redraw.
3. Composite annotations into the copied SDR crop and the saved HDR crop at matching physical coordinates. Keep existing format selection, HDR metadata, and image encoding behavior. Add pin-to-screen and other reference actions only with working handlers.
4. Preserve input behavior: selecting, moving/resizing, drawing, text entry, Esc, Enter, right-click, keyboard adjustments, and focus. Reset tools and temporary resources on every new capture.
5. Validate Debug/Release build, inspect the isolated release visually, and exercise the tool paths without saving test screenshots. The user raised the automation limit from 20 to 30 capture triggers total after the first 20; all 30 have now been used. All temporary images go to a dedicated temp directory with `finally` cleanup. Pure WGC tests never save images.

Completion: reference-style controls are functional, edits appear in copied/saved output, no regression in overlay pause/resume or low-idle GPU behavior, and the test artifacts contain only logs plus up to three representative samples.
