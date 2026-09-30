# Starshot WebUI

React 19 + TypeScript + Vite, hosted locally in the native .NET 10 application using WebView2.

## Development

```powershell
cd src/Starshot.WebUI
npm ci
npm run dev
npm test
```

The browser preview has empty collections and allows visual settings changes. Native capture, clipboard, files and translation explicitly report that they require the desktop host. It never supplies fake capture or translation results.

```powershell
dotnet build src/Starshot/Starshot.csproj -c Debug -p:Platform=x64
dotnet publish src/Starshot/Starshot.csproj -c Release -p:Platform=x64 -r win-x64
```

Node 22.19+ or 24 is required for the frontend build. MSBuild builds assets incrementally and copies `dist` into the application's `WebUI` directory for both build and publish. Shipped UI assets and fonts are offline; a running Vite server is never required by the desktop app. The WebView2 Evergreen Runtime must be available on Windows; failures show a retry screen while the native tray and shortcuts remain usable.

## UI ownership

- React: creative workspace, gallery/search/filter/import, preview drawer, clipboard, OCR editor, translation and preferences.
- C#: WGC capture/overlays, annotations, scrolling/GIF capture, HDR/P3 decoding/encoding, original-image viewer, floating pins, OneOCR installation, global hotkeys, tray, Windows clipboard and batch conversion.
- Native pixel surfaces stay native. Web previews are SDR thumbnails. The original-image action retains the existing HDR viewer.
- The legacy welcome wizard no longer blocks startup. First run creates the config and opens the workspace.

OCR results open a separate 720×560 WebView2 window (`?surface=ocr`), leaving the main translation workspace intact. Both editors use Tiptap rich documents. Selecting text opens a nearby formatting toolbar for emphasis, font size/color, headings, lists and alignment. Translation sends ordered text fragments with stable IDs; the host requires every ID exactly once, and the frontend changes only text nodes in a cloned document. Marks and block attributes remain intact. Malformed responses retain the previous translation. Manual copy supplies HTML and plain text. OCR auto-copy is separate from screenshot auto-copy, defaults off, and is available in both the compact footer and OCR settings. Native configuration changes refresh every open WebUI window.

## Bridge v1

`window.chrome.webview.postMessage({ version: 1, id, method, params })`

Response: `{ type: 'response', id, ok, result }` or `{ type: 'response', id, ok: false, error }`.

Event: `{ type: 'event', name, data }`. `app.ready` establishes frontend readiness, with OCR results retained until ready. Requests are correlated by UUID, support AbortSignal cancellation, and time out after 90 seconds. Frontend reloads do not add multiple host handlers.

Commands are allowlisted in `Features/WebUI/WebUiBridge.cs`. Gallery commands accept host-issued image IDs, never frontend filesystem paths. Only `https://starshot.local` may navigate or call the bridge. New windows, downloads and permission requests are denied. CSP prevents remote scripts, frames and web network APIs. API keys stay in DPAPI storage; only the key-present flag is returned. The bridge never logs message payloads.

Families: `app.*`, `window.*`, `capture.*`, `library.*`, `clipboard.*`, `pin.*`, `settings.*`, `hotkey.*`, `translation.*`, `utility.*`, `request.cancel`.

## Design

The visual system combines a graphite tool rail, paper canvas, chartreuse action accent, Outfit display type, Newsreader italic and Noto Sans SC. Typography is shipped with its OFL licenses.

Hidden mathematical structure: Fibonacci-like spacing 8/13/21/34/55, phi thumbnail ratio and logarithmic HDR controls; 314/194 ms motion and a bounded deterministic phi/pi seed for tiny compositional differences. Motion respects `prefers-reduced-motion`; CPU/GPU are not spent on continuous decorative animation. Modals trap focus, restore it on close, make the underlying interface inert, and support Escape. Errors, empty states and loading states are explicit.
