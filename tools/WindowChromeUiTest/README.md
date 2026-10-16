# Integrated title bar layout checks

Run `node tools/WindowChromeUiTest/ui-check.mjs <playwright package path> <browser executable>`
from the repository root after building `src/Starshot.WebUI`.

The browser stays headless with GPU disabled. A mock bridge supplies native caption
insets and state. Six layouts cover main/image/OCR, light/dark, 150%/200% DPR, minimum
window sizes, caption exclusion, drag/no-drag areas, state changes and OCR copying.
Set `STARSHOT_UI_SAMPLE_DIR` to keep up to three synthetic representative images.
All other temporary images are removed in finally. No desktop input or inference runs.

The native Windows caption buttons do not appear in browser-only screenshots.
Their geometry/configuration is tested by `RegionToolbarUiTest` with
`STARSHOT_CHROME_ONLY=1`; actual visible dragging and Snap still need desktop validation.
