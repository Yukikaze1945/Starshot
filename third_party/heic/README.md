# HEIC component

Starshot saves both SDR and HDR HEIC using an independent, CPU-only
`StarshotHeic.exe` worker. It does not load downloaded codec DLLs or require the
Windows HEIF/HEVC Store extensions. Runtime support in this build is Windows x64.

Both variants use HEVC Main10, 10-bit, 4:2:0. SDR uses sRGB / BT.709; HDR uses
BT.2020 / ST 2084 PQ with content light metadata when available. Quality 100 is
maximum quality **with chroma subsampling**, not RGB-lossless encoding. Alpha
is not retained. Odd sizes and 1×1 crops retain their displayed dimensions.
The decoder supports ordinary SDR and PQ HEIC; HLG is explicitly rejected.

## Build from corresponding sources

Requirements: Windows x64, Visual Studio 2022 C++ build tools / Windows SDK,
CMake >=3.24, Ninja, PowerShell 7, tar. Run in an x64 Native Tools prompt:

```powershell
pwsh -File tools/Build-HeicCodec.ps1 -WorkRoot ./sources
```

`StarshotHeic-source.zip`, shipped beside the executable, contains the complete
unmodified upstream release archives, the worker source, CMake file and build
script. Extract it first, then run the command from its root. The script verifies
source SHA-256 before extraction, builds static libraries with the static MSVC
runtime, verifies encoder/decoder availability, and generates the executable
manifest. No assembly code, external plugin loading or other HEIF codecs are
enabled. C++/intrinsics x265 uses two worker threads and one frame thread.

Source releases (SHA-256):

| Component | Official source | SHA-256 |
|---|---|---|
| libheif 1.23.6 | https://github.com/strukturag/libheif/releases/download/v1.23.6/libheif-1.23.6.tar.gz | `4484346DC5995319DBC11E3A1C35D0A2EC46511CE370900869337FD2C7033125` |
| x265 4.2 | https://download.videolan.org/pub/videolan/x265/x265_4.2.tar.gz | `40B1EA0453E0309F0EBA934E0DDF533F8F6295966679E8894E8F1C1C8D5E1210` |
| libde265 1.1.3 | https://github.com/strukturag/libde265/releases/download/v1.1.3/libde265-1.1.3.tar.gz | `554228BD17788C99A7E63B37AB5634722190E6E2BF60C1DCB01CEF328E133905` |

## Licenses and attribution

The standalone combined worker is GPL-3.0-or-later; see `LICENSE-worker.txt`.
Its own source is `tools/HeicCodec`. Starshot's existing license is retained
separately. The pipe interface and managed integration remain Starshot code.

- libheif: copyright Dirk Farin and contributors, LGPL-3.0-or-later,
  `COPYING-libheif.txt` and source archive copyright headers.
- libde265: copyright struktur AG / Dirk Farin and contributors,
  LGPL-3.0-or-later, `COPYING-libde265.txt`.
- x265: copyright MulticoreWare, Inc. and contributors, GPL-2.0-or-later,
  `COPYING-x265.txt`; source file headers allow later GPL versions.

This component is provided without warranty. These copyright licenses do not
grant third-party patent rights. Preserve the notices and complete corresponding
source package when distributing the worker.

## Runtime limits and verification

Encoding/decoding is serialized, cancellable internally, limited to 64 megapixels
and a three-minute process timeout. Decoder input is capped at 512 MiB, decoded
libheif allocations at 1 GiB. A gallery thumbnail has bounded output dimensions;
the decoder may still need a full source frame temporarily. No helper process or
decoded image cache remains resident after an operation. The build and first
runtime invocation verify the bundled executable against `manifest.json`.

Tests: `dotnet run --project tools/HeicCodecTest -c Release` in the Starshot repo.
Phone HDR rendering and external color-managed decoder behavior require device
validation; Main10 / 4:2:0 alone does not prove HDR presentation.
