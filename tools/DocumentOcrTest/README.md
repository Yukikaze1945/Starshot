# Document OCR validation

Build `DocumentOcrTest.csproj` as Debug/x64 with the local .NET 10 SDK.
Run `run.mjs <playwright-core package> <chrome.exe>` from the repository. The runner uses a headless software browser, creates nine synthetic images in an independent OS temp directory, runs the real C# CPU pipeline and then the isolated upstream reference oracle, and removes images/application-data in finally. It does not capture the desktop or touch installed Starshot.

Prerequisites: the pinned models in `build/document-ocr/models`, the development-only Python environment described in `third_party/document-ocr/SOURCES.md`, and pinned RapidAI/Paddle reference sources under `build/document-ocr/reference`. `reference.py` checks real official Paddle inference and formula/table decoding. Word is required only for the final editable-equation clipboard test; that test creates and closes its own invisible document and restores the clipboard.

`ui-check.mjs <playwright-core package> <chrome.exe> <report directory>` verifies the built WebUI with synthetic bridge responses at 200% DPI, including a 680x460 window. It keeps at most two representative UI images and removes its temp directory in finally. This is not a real screenshot test or a model-accuracy test.

The diagnostic full GC exists only in the test host. Its purpose is to check weak references to completed-job source buffers; production code never forces collection. `GC.GetGCMemoryInfo()` fields describe the latest GC snapshot, not instantaneous heap use. The idle-release branch is tested by injecting an expired timestamp into the isolated host, not by changing the production timeout.
