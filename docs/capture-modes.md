# Screenshot capture modes

Status: implemented, updated 2026-10-08. See [lightweight architecture](adr/0001-lightweight-cpu-capture.md) and [validation report](reports/2026-10-08-lightweight-cpu-validation.md).

Starshot provides four capture modes so a user can trade HDR fidelity for lower
capture residency without changing selection, annotation, OCR or output tools.

| Mode | Capture | Frozen image | Output |
| --- | --- | --- | --- |
| Lightweight | GDI desktop DC + top-down DIB + BitBlt | BGRA8 in RAM, GDI selection/annotation, CPU crop | SDR sRGB; CPU PNG/AVIF/JXL |
| Standard | Existing monitor WGC context, BGRA8 | Independent 8-bit copy | SDR sRGB |
| High-quality HDR | Existing monitor WGC context | FP16 scRGB on HDR monitors; existing 8-bit path on SDR monitors | Existing HDR/P3/UHDR pipeline |
| Single-frame HDR video | Same high-quality HDR capture path | Same frozen pixels, crop and annotations | Companion image plus one-frame, three-second HEVC/AV1 MP4 |

High-quality HDR is the default for an existing or new configuration. This
preserves the previous capture behavior, including the Windows 10 SDR fallback.
Capture mode is separate from the encoder's quality setting and file format.
HDR preferences stay saved while either SDR mode is selected.

Single-frame video is validated on Windows x64. It uses a pinned independent CPU
encoder, preserves SDR fallback, and leaves WGC context lifetimes unchanged.
See [video architecture](adr/0002-static-hdr-video.md) and
[video validation](reports/2026-10-08-static-hdr-video-validation.md).

Settings and the tray quick switch use one persisted `ScreenCaptureMode` value.
A capture operation owns its mode until selection/recording/encoding finishes.
A change during that operation is rejected with a retryable message. A successful
change retires and disposes all previous WGC contexts before publishing the new
mode. The cache can be used again later; this is not application shutdown.
Standard and HDR retain monitor-context reuse and the existing suspension of
background Win2D copies during the region overlay.

Lightweight never starts WGC or maintains a GPU latest-frame cache. Ordinary
capture, selection, annotation, crop, copy and encoding use CPU buffers and
short-lived GDI windows. They do not create a CanvasDevice, CanvasBitmap or
CanvasSwapChain. Closing the overlay destroys its HWND and releases its DIBs,
fonts and selection buffers; the confirmation notification also uses GDI.
GDI errors are reported rather than silently starting WGC.

Pinning, GIF and long capture retain their existing native image tools. Selecting
one explicitly uploads only the selected image/annotation layer at that boundary.
OCR recognition takes CPU pixels, then opens its existing WebView2 editor. Active
image viewers/editors/recorders can therefore exceed the ordinary capture budget.

Switching to lightweight closes the old GPU selection and confirmation windows.
WGC retirement attempts every context's cleanup, including native teardown when
latest-bitmap disposal fails; a failed retirement prevents publishing the new mode.
Previously used shared CanvasDevice caches are trimmed without disposing the
shared device or creating a new graphics device on pure lightweight startup.

A hidden main WebUI in lightweight snapshots its workspace to RAM and closes its
windowed WebView2 controller, native callbacks and HWND. The main and compact OCR
windows host the same React UI directly, without a main XAML composition scene.
Reopening constructs a new
window and restores the draft. A pending translation, open utility window or
unsaved settings defers retirement. API keys are excluded from the snapshot; no
draft is written to disk. Standard/HDR retain the existing main-window behavior.

The initial mode implementation kept the GPU overlay. The lightweight follow-up
replaces that overlay with a CPU renderer to meet the requested dedicated-memory
budget; standard and HDR context reuse is preserved.

Validation commands:

- `dotnet run --project tools/CaptureModesTest`: transition ownership, format
  contracts, actual RAM BitBlt/crop/cancellation and native GDI handle pairing.
- `npm test` in `src/Starshot.WebUI`: existing message bridge and text workflows.
- `dotnet run --project tools/LightweightCaptureTest`: CPU raster, selection,
  annotations, ownership transfer and offscreen HWND teardown. No desktop capture.
- `tools/Validate-LightweightOffscreen.ps1`: isolated actual-app synthetic 4K
  captures, paired resident/committed counters, main WebUI lifecycle and encoder
  smoke checks. No desktop input or WGC. Temporary images/profile/cache are removed
  in finally; only logs, CSV and curves remain.
- Self-contained Release build/publish. No real desktop screenshot interaction or
  visual settings/tray test was performed, as requested by the user.

The lightweight acceptance target is both dedicated resident and committed
memory below 150 MiB, preferably below 120 MiB, during ordinary capture and idle.
Offscreen results are evidence for that test configuration, not an unconditional
bound across all monitors, drivers, visible windows or active native image tools.
No production HDR math or gain-map metadata changes.
