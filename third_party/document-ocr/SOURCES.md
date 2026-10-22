# Document OCR resources

The application downloads data only. It does not execute Python or downloaded DLLs.
The byte sizes, SHA-256 values and canonical download URLs are recorded per file in `models.json`.
Models are optional, stored in the current application-data directory and lazily loaded by the CPU ONNX Runtime 1.31.0 backend (two threads).

## RapidLaTeXOCR

- Official weights: https://github.com/RapidAI/RapidLaTeXOCR/releases/tag/v0.0.0
- Reference source revision: `21a6365738e6ae74006983ee023755f508739532`.
- Encoder 89,008,136 bytes; decoder 50,952,726; resizer 38,967,751; tokenizer 24,174. Total 178,952,787 bytes (170.66 MiB).
- MIT code and upstream pix2tex attribution: `RapidLaTeXOCR-LICENSE.txt`, `pix2tex-LICENSE.txt`.
- Verified byte-identical HF mirror: https://huggingface.co/inYourOwnBrowser/rapid-latex-ocr-onnx/tree/0d0a23977b50433b07cdf95513ed7b7e45d3a761
- Formula preprocessing and decoder adaptation are implemented in C#. Correct SDR BGRA is composited on white and converted to grayscale. The upstream file loader's RGB/BGR inversion is deliberately not reproduced; parity tests feed both implementations the same correctly computed grayscale.
- Greedy decoding, BOS=1, EOS=2, at most 512 generated tokens. EOS means decoding finished, not that the equation is correct. Complex matrices and multiline equations can be inaccurate or syntactically invalid; local preview reports parser errors and the source remains editable.

## PP-DocLayout-S

- Original Apache-2.0 model: https://huggingface.co/PaddlePaddle/PP-DocLayout-S/tree/8ac289e66575bb9bba6e15c53719d8b15cc9b3b2
- Community ONNX export: https://huggingface.co/stefanj0/PP-DocLayout-S-ONNX/tree/7e851b9e5ad66613d7318a3213ab3336ebe0a514
- 4,917,852 bytes. SHA-256 `33688dbee1c23e34b81777e97cb428eb40f24b242c02b5f623484959e830aec8`.
- 23 labels, input 480 x 480 RGB, original-image scale factors; NMS is included in the graph.
- The isolated reference suite compares its results against the pinned official Paddle checkpoint using the exact same input tensor. This does not establish dataset-level accuracy.

## PP-DocLayout-M / PP-DocLayout-L (optional, 2026-10-21)

- Official Paddle checkpoints (Apache-2.0): [M](https://huggingface.co/PaddlePaddle/PP-DocLayout-M/tree/7dbfcce3154a55776dc71ca026a4a2a8388dad8d), [L](https://huggingface.co/PaddlePaddle/PP-DocLayout-L/tree/ca760c3336d922ee63cdbff85003181222e3911c).
- Pinned community ONNX conversion (not an official Paddle ONNX release): https://huggingface.co/x3zvawq/paddleocr-js-onnx/tree/51c2133b5a7ea27b795fa8c400fdbfbd5337dd6a . Only model data is downloaded; no code from that repository is executed or incorporated.
- M: `pp_doclayout_m/PP-DocLayout-M_infer.onnx`, 23,496,395 bytes (22.41 MiB), SHA-256 `c5032bdae772887e9de4d2e955c05c42ac7f38c8c8eb6bdc1b4560dc5cf1c786`.
- L: `pp_doclayout_l/PP-DocLayout-L_infer.onnx`, 129,398,697 bytes (123.40 MiB), SHA-256 `01fd1a44fbea5b0a76302de356c1518250cbd34ee82833ac04d907034c1376e1`.
- Both preserve the same 23 categories. M uses 640x640 RGB, cubic resize, /255 and ImageNet mean/std; L uses 640x640 RGB, cubic resize and /255 only, with `im_shape` and original-image `scale_factor`. The official YAML settings were checked against both pinned checkpoints.
- `tools/DocumentOcrTest/layout-variants-reference.py` compares the C# tensors with OpenCV cubic preprocessing, then compares original-coordinate boxes/scores with the official Paddle predictors. Tiny byte-rounding differences are bounded; this is an integration check, not a dataset accuracy evaluation.
- Weights remain subject to Paddle's Apache-2.0 attribution (`PaddleOCR-LICENSE.txt`). Existing code, source and license notices are retained. HF and hf-mirror serve the pinned bytes; no same-version ModelScope resource has been verified, so that source remains disabled.
- S stays the default. A persisted selection chooses exactly one layout package per new page job; an in-flight job keeps its captured selection. Selection waits for the executing inference stage before disposing sessions and does not load a replacement until recognition. All layout sessions are CPU-only, two threads, and idle-unloaded after 60 seconds.

## SLANet_plus

- Official Apache-2.0 ONNX/config: https://huggingface.co/PaddlePaddle/SLANet_plus_onnx/tree/7dbe640e127602bf506815e822c09758de73c482
- Model 7,782,138 bytes, config 2,035 bytes. Total 7,784,173 bytes.
- BGR input, proportional resize (rounded dimensions) and zero padding to 488 x 488 after normalization. The model predicts structure tokens and cell polygons; the existing OCR engine supplies cell text.
- Pre/postprocessing follows PaddleX. Cell predictions and structure can be wrong, especially in complex or borderless tables. Structure confidence is not a guarantee of correctness.
- Paddle copyright/license preserved in `PaddleOCR-LICENSE.txt`.

## Preview and exports

- KaTeX 0.19.0 (MIT), bundled locally with its fonts. Vite's license collector includes KaTeX notices in the WebUI output. No CDN or online formula rendering.
- Open XML SDK 3.3.0 (MIT), `OpenXML-SDK-LICENSE.txt`; XLSX cells are inline text, with explicit merge ranges.
- Word clipboard uses UTF-16LE registered `MathML` and `MathML Presentation` formats, documented at https://learn.microsoft.com/en-us/office/math/mathml . Word performs the native OMML import; no Office XSL file is redistributed.
- No Evan OCR application code is included.

## Development-only reference environment

Python 3.11, NumPy 1.26.4, Pillow, opencv-python-headless 4.10.0.84, tokenizers, ONNX Runtime 1.23.2 and PaddlePaddle 3.2.2 are used only by `tools/DocumentOcrTest/reference.py`. None is an application runtime dependency. Model caches and this isolated environment remain under ignored `build/document-ocr/`.
