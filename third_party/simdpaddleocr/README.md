# Starshot OCR component attribution

Runtime: `Sdcb.SimdPaddleOCR` 1.4.2, official NuGet package (CPU .NET 10 API).
Models: `Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny` 1.0.0; official transitive
`Sdcb.SimdPaddleOCR.Models.TextLineOrientation` 1.0.0 and ModelProvider 1.0.0.

PP-OCRv6 Tiny DET, REC, dictionary and text-line orientation CLS are embedded in
the model assemblies. NuGet restores them at build time; normal build/publish
copies the assemblies. Runtime loads embedded streams, without external model
paths, filesystem extraction, downloads or dependence on the user's NuGet cache.

Library source/tag: https://github.com/sdcb/SimdPaddleOCR/tree/v1.4.2
Core source revision: `733c0b08a97dfd358a43a0a74938c21f6bccdbb9`.
Tiny model-package source revision: `315e4fff4466260fd8f09cd4d53a70ec3b63f65c`.
Library author: sdcb / Sdcb.SimdPaddleOCR contributors.
Models: Copyright (c) 2016 PaddlePaddle Authors. All Rights Reserved.

Preserve accompanying Apache-2.0 LICENSE and official THIRD-PARTY-NOTICES.md.
Model source/ownership links are recorded in those notices. PaddleOCR / PP-OCR
names belong to their respective owners; no official endorsement is claimed.
Starshot retains its separate LICENSE.

Starshot does not use the repository's ImageSharp, SkiaSharp, OpenCvSharp examples,
ONNX Runtime, Paddle Inference or optional development GPU implementations.

## Optional PP-OCRv6 Small DLC

Small uses the data resources corresponding to the official
`Sdcb.SimdPaddleOCR.Models.ChineseV6Small` **1.0.0** package, at the same pinned
model-source revision `315e4fff4466260fd8f09cd4d53a70ec3b63f65c`.
The Small package/DLL is **not** installed, downloaded or executed by Starshot.
Only `small_det.onnx`, `small_rec.onnx` and `rec_keys.txt` are downloaded from
the official sdcb/SimdPaddleOCR GitHub repository. DET and REC are byte-identical
to the official NuGet embedded resources. The Git dictionary uses LF whereas
NuGet's embedded copy uses CRLF; the dictionary entries are identical.
See `small-manifest.json` for exact pinned URLs, sizes and SHA-256 hashes.

Total download: **31,114,837 bytes (29.67 MiB)**. The already bundled
TextLineOrientation 1.0.0 CLS is reused; no additional classifier download.
Small resources retain the upstream Apache-2.0 terms and PaddlePaddle attribution.

Resources are installed under `<AppConfig.UserDataFolder>/Models/OCR/ppocrv6-small-1.0.0`.
The installer resolves this to the existing per-user application-data location;
portable builds use their existing data-root semantics. No Program Files or
NuGet-cache paths are used. Downloaded files are length/hash checked, flushed,
verified again and published using a same-volume directory rename. Local
`IPaddleOcrModelProvider` streams and `PaddleOcrModelBundle` load them through
SimdPaddleOCR 1.4.2. Each load validates hashes; each Small OCR revalidates the
on-disk bundle, including changes with unchanged size/timestamp.

One semaphore serializes inference, engine switch/disposal, Small deletion and
publication. Settings and status queries never initialize an engine. Only one
Tiny/Small engine exists per process. Missing/damaged/failed Small returns to
Tiny; only a Tiny execution exception reaches the explicit Windows OCR fallback.
