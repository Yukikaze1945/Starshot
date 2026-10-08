# OCR Tiny validation harness

This is a developer-only, offscreen harness for the bundled SimdPaddleOCR PP-OCRv6 Tiny integration. It generates all fixtures in memory with Win2D; it does not use the desktop, capture windows, save fixture images, or touch the clipboard. The HDR fixture is synthesized as FP16 scRGB and sent through the project HDR-to-SDR preview conversion before OCR.

The full run exercises Chinese, English, mixed text, dark-on-white, light-on-black, small game UI text, HDR-to-SDR preview, ordinary SDR, a small crop, and 4K small text. It also checks padded stride, input-buffer immutability, formatting, pixel preparation/rejection cases, initialization count, fallback behavior, and one fault-injected Windows OCR fallback. The 4K fixture uses the current detector side limit of 2048.

Scores returned by this SimdPaddleOCR version are raw API scores (observed roughly 28–39 here), not probabilities in the range 0–1. The harness checks that scores are finite and sensible for the API; it does not interpret them as probabilities.

## Restore and run

From the repository root, using the .NET SDK installed at `D:\coding\dotnet-sdk10`:

```powershell
$dotnet = 'D:\coding\dotnet-sdk10\dotnet.exe'
& $dotnet restore tools/OcrTinyTest/OcrTinyTest.csproj --source https://api.nuget.org/v3/index.json
& $dotnet build tools/OcrTinyTest/OcrTinyTest.csproj -c Release --no-restore -o build/ocr-tiny-test
& $dotnet build/ocr-tiny-test/OcrTinyTest.dll
```

The memory-only run executes one cold request, a second request, and six warm requests on the same synthetic SDR image, without forced garbage collection:

```powershell
& $dotnet build/ocr-tiny-test/OcrTinyTest.dll --memory-only
```

The final-app smoke harness is built separately. It loads the app assembly from the specified published output and preloads the published Sdcb assemblies, while using the harness's Win2D host/runtime:

```powershell
& $dotnet restore tools/OcrTinyTest/PublishedSmoke/PublishedSmoke.csproj --source https://api.nuget.org/v3/index.json
& $dotnet build tools/OcrTinyTest/PublishedSmoke/PublishedSmoke.csproj -c Release --no-restore -o build/ocr-tiny-published-smoke-fixed
& $dotnet build/ocr-tiny-published-smoke-fixed/PublishedSmoke.dll --published-app (Resolve-Path 'build/ocr-tiny-publish/app/Starshot.dll').Path
& $dotnet build/ocr-tiny-published-smoke-fixed/PublishedSmoke.dll --model-info (Resolve-Path 'build/ocr-tiny-publish/app/Starshot.dll').Path
```

The initial host mixed the stock Win2D native asset with Starward's projection, producing `0x80040111`. The smoke project now excludes the stock Win2D assets exactly as Starshot does. The final run passed: actual published `TonemapToSdr → PreparePixels → RecognizeAsync`, model assemblies loaded from the publish directory, one initialization, zero fallback, unchanged BGRA input, and the expected HDR text. This does not constitute a desktop/clipboard UI test.

## Artifacts

- `build/ocr-tiny-test/ocr-tiny-validation.csv` — latest run output; after `--memory-only`, this contains the cold/second/six-warm measurements.
- `build/ocr-tiny-test/ocr-tiny-validation.log` — Serilog output for harness runs.
- `build/ocr-tiny-test/ocr-tiny-validation-full.csv` — preserved summary of the completed ten-fixture run, including timings and raw scores.
- `build/ocr-tiny-published-smoke-fixed/smoke-final.log` — successful final-app smoke output.
- `build/ocr-tiny-published-smoke-fixed/model-resources.log` — sizes read from final published embedded resources (no inference).

The full fixture run initialized the model once, had no fallback in the ten standard cases, and recognized the 4K 22px fixture. The later fault-injection check exercised Windows OCR once. The model bytes are embedded in the application assemblies; no model files or fixture images are written by this harness.
