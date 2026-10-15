# Optional workspace model data

These packages are **data**, downloaded only on request. Neither model package
contains executable DLLs. Installation streams SHA-256 verification into a
staging file and publishes the verified file atomically. Models live under the
current `AppConfig.UserDataFolder/Models/Library`, never in Program Files.

| Model | Fixed source | Bytes | SHA-256 | License |
| --- | --- | ---: | --- | --- |
| Index-Translate-2B Q5_K_M | [IndexTeam official GGUF, revision 3a0eca6ce931d88d00614d427ab3ed8ff0743902](https://huggingface.co/IndexTeam/Index-Translate-2B-GGUF/tree/3a0eca6ce931d88d00614d427ab3ed8ff0743902) | 1,454,787,072 | `2A869B5E374FF6121EE5191629FBA8FF4A081951D56EEC90D89771D7971465EE` | Apache-2.0 |
| U²-Net compact / u2netp.onnx | [rembg official conversion release](https://github.com/danielgatis/rembg/releases/tag/v0.0.0), [original U²-Net](https://github.com/xuebinqin/U-2-Net) | 4,574,861 | `309C8469258DDA742793DCE0EBEA8E6DD393174F89934733ECC8B14C76F4DDD8` | Apache-2.0 |

Index-Translate is by IndexTeam / bilibili. The official model card declares
Apache-2.0; the GGUF repository has no separate LICENSE file. Full standard
license text is supplied as `APACHE-2.0.txt`, with this source attribution.
Only the text GGUF is needed for translation of already recognized text; the
visual `mmproj` model is deliberately not downloaded.

U²-Net is by Xuebin Qin and contributors. Original project license is preserved
in `U2NET-LICENSE`. Conversion is by Daniel Gatis and rembg contributors. The
converted file also matches rembg's upstream MD5
`8e83ca70e441ab06c318d82300c84806`; Starshot uses SHA-256 for installation.

The segmentation runtime is Microsoft.ML.OnnxRuntime 1.31.0, MIT; see
`ONNXRUNTIME-LICENSE`. The CPU session is created only for a requested cutout,
uses two intra-op threads, and is disposed after the operation. This does not
change the existing SimdPaddleOCR inference implementation.

The text runtime is the separate, pinned llama.cpp b11429 CPU package. Its
MIT, LLVM OpenMP, cpp-httplib and nlohmann/json notices are supplied under
`third_party/local-inference` and copied alongside the source build runtime.
No PATH-provided executable or model-provided executable is used.

## Lifecycle and limitations

- Index text inference: lazy worker, four CPU threads, GPU offload disabled,
  one active request, 60-second idle unload, explicit unload/delete support.
- Segmentation: 320 × 320 model input, original-resolution soft alpha output.
  Fine hair and small transparent details are not guaranteed by this compact
  model. Polygon/lasso/wand are alternative masks, not a trained refinement
  network.
- Formatted offline translation translates the existing style spans and
  rejoins them programmatically. Span IDs and formatting stay intact; splitting
  sentences across styles can reduce translation context and quality.
- A failed local model request does not silently transmit text to an online API.
- Existing Tiny and Small OCR packages remain managed by their existing store.
