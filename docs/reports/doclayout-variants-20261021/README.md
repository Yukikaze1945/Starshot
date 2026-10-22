# PP-DocLayout-M / L integration — 2026-10-21

## Delivered

Model Library now lists S, M and L together. Installed layout cards expose a persisted “设为版面模型” action. S remains the default. New page jobs capture the selection once; required packages are formula + selected layout + table, rather than every variant. Formula-only jobs remain independent. The active page header identifies its actual model.

Downloads use pinned HF / hf-mirror resources and existing staged/atomic installation, progress, cancellation, validation, local directory import and deletion. No verified same-version ModelScope mirror was found, so none is fabricated. Model data lives under the current app data directory. Hash mismatches remain advisory, while missing files or incompatible interfaces fail explicitly. Scanning recognizes variant filenames and registered canonical sizes/paths without labeling M/L as S.

M/L are CPU-only, two inference threads. Selection waits for the current inference stage, unloads sessions and stays lazy; only one layout session is resident. Existing idle unload after 60 seconds is unchanged. Deleting the selected package leaves a clear missing-model error on recognition rather than silently falling back to S.

## Pinned model data

Community ONNX export revision: `x3zvawq/paddleocr-js-onnx@51c2133b5a7ea27b795fa8c400fdbfbd5337dd6a`. These are conversions of Paddle checkpoints, not official Paddle ONNX releases. Apache-2.0 model attribution and existing notices are retained. See `third_party/document-ocr/SOURCES.md` and `models.json` for URLs and licenses.

| Model | Bytes | MiB | SHA-256 |
|---|---:|---:|---|
| PP-DocLayout-M · 版面检测 | 23,496,395 | 22.41 | `c5032bdae772887e9de4d2e955c05c42ac7f38c8c8eb6bdc1b4560dc5cf1c786` |
| PP-DocLayout-L · 版面检测 | 129,398,697 | 123.40 | `01fd1a44fbea5b0a76302de356c1518250cbd34ee82833ac04d907034c1376e1` |

M uses RGB cubic resize to 640², /255 and ImageNet normalization. L uses RGB cubic resize to 640² and /255 only, plus im_shape. Returned coordinates use the original input dimensions. S preprocessing remains unchanged. No runtime Python/OpenCV dependency is introduced.

## Verification

- Debug application and inference harness builds: 0 warnings / 0 errors. No Release build, installation, push or publication.
- 40 real-weight checks: S/M/L inference, variant imports/hash checks, selected-package requirement, lazy selection, single resident session, deterministic warm output, 4K input, cancellation/retirement, scanning, real-file HTTP transport, atomic multi-file package publication, interrupted/offline download cleanup, restart status, deletion, advisory hashes and wrong-model interface rejection. HTTP fault injection is local and deterministic; canonical M/L bytes were fetched from HF and verified separately.
- M/L ONNX vs pinned official Paddle checkpoints: class/count/boxes/scores pass on a synthetic two-column document. Official revisions and per-box metrics are in reference.json.
- Original model-store tests: 27 pass; workspace core: 47 pass; frontend: 16 pass.
- Software-browser model library tests: 1280×900 light, 720×900 dark, 200% scale; S/M/L buttons, progress/cancel/import/switch/verify/delete, warning activation and button bounds pass.
- Document UI regression: 1280×900 and 680×460 at 200%, formula preview/edit/copy, table export, cancellation/standalone screenshot editor pass.
- No desktop capture or mouse/keyboard automation. Synthetic images, temporary app data and exported tensors are deleted in finally. No persistent test screenshots.

## Timings / memory

All times include C# preprocessing and synchronous model inference as observed by the async caller. Cold time includes initial validation/session creation; host and OS caches are not reset. Private memory is the entire isolated Debug test process, not a model-only allocation measurement.

| Variant/input | Cold ms | Warm ms | Regions | Private before MiB | Private after MiB |
|---|---:|---:|---:|---:|---:|
| M / 972x1380 | 775 | 567 | 28 | 51.08 | 233.84 |
| M / 3840x2160 | — | 491 | 29 | — | 270.50 |
| L / 972x1380 | 2249 | 1277 | 33 | 128.13 | 521.13 |
| L / 3840x2160 | — | 1180 | 32 | — | 507.22 |

Official parity details:
- M: 28 regions, minimum box IoU 0.99999555, maximum score difference 0.00000073. C# cubic preprocessing differs from OpenCV by at most one 8-bit level at a small number of rounding ties (mean normalized tensor error 0.000001438).
- L: 33 regions, minimum box IoU 0.99997075, maximum score difference 0.00000269. C# cubic preprocessing differs from OpenCV by at most one 8-bit level at a small number of rounding ties (mean normalized tensor error 0.000000326).

This is integration/contract validation on synthetic material, not a quality benchmark. M/L can still miss or misclassify regions; larger does not guarantee accuracy on every page. No long-run zero-growth claim is made. Peak memory and mobile/other CPU hardware have not been measured for these variants. Existing OCR, WGC, screenshot interactions, HDR encoders and GPU lifetime management were not changed.

## Modified areas

- `Helpers/DocumentModelStore.cs`, `ModelDownloadSources.cs`, `LocalModelFiles.cs`: catalog, acquisition, persisted selection and local scanning.
- `Features/DocumentOcr/DocumentInference.cs`, `PageRecognitionJob.cs`, `DocumentResults.cs`: per-variant preprocessing, model IO validation, job selection/requirements and model identity.
- `Features/WebUI/WebUiBridge.Models.cs`, `WebUiBridge.DocumentOcr.cs`: library selection command and selected-job requirements.
- WebUI `ModelLibrary.tsx`, `DocumentRecognition.tsx`: variant cards, switching and active model label.
- `third_party/document-ocr/models.json`, `SOURCES.md`: pinned resources/hashes/licenses.
- `tools/DocumentOcrTest/*`, `tools/OcrSmallTest/ui-check.mjs`, model/workspace test configuration boundaries: isolated regression checks.

Use `Run-Starshot.cmd` after closing the currently running source instance. This task does not install the test weights into the normal user model directory; choose Download or Import in Model Library.
