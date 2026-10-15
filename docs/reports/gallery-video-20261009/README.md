# Gallery static-video compatibility verification — 2026-10-09

## Result

Debug and Release x64 builds of `src/Starshot/Starshot.csproj` succeeded with 0 warnings and 0 errors. StaticVideoTest passed 9/9 checks, including cancellation cleanup, descriptor schema and legacy-sidecar read compatibility, HDR/SDR output checks, and packet timeline checks. StaticVideoNativeTest passed: native GPU HEVC and AV1 saves, cancellation preserving the companion image/removing the video, and MP4 basename collision safety.

NuGet restore used explicit `nuget.org` and the existing Visual Studio offline package feed. The user's global NuGet configuration was not changed. Logs are in this directory: `restore-app.log`, `build-debug.log`, `build-release.log`, `restore-static-test.log`, `static-video-test.log`, `restore-native-test.log`, and `static-video-native-test.log`.

## 1080p HEVC cadence comparison

All variants encoded the same synthetic PQ HDR gradient (1920×1080, 12,441,600 bytes per input frame) to 3 seconds. `bytesFed` is repeated raw input, and memory figures are peak FFmpeg process Working Set / Private Bytes.

| Variant | Frames / packets | Sum packet duration | Output bytes | Encode ms | Peak memory MiB (WS / private) | Bytes fed |
|---|---:|---:|---:|---:|---:|---:|
| Original one-frame comparator (1/3 fps) | 1 / 1 | 3.000000 s | 9,301 | 398 | 108.8 / 130.6 | 12,441,600 |
| 5 fps | 15 / 15 | 3.000000 s | 17,979 | 1,055 | 346.8 / 380.5 | 186,624,000 |
| 10 fps | 30 / 30 | 3.000000 s | 21,692 | 1,748 | 529.2 / 562.8 | 373,248,000 |
| 30 fps | 90 / 90 | 2.999970 s | 37,006 | 4,317 | 547.2 / 582.5 | 1,119,744,000 |

PTS and DTS were strictly increasing for every multi-packet variant. All had 3-second durations within the test tolerance. Subsequent Redmi K60 Android 15 tests verified Media3 first-frame-to-ended times of 2,975 / 3,010 / 3,005 / 3,008 ms for one-frame / 5 / 10 / 30 fps. Original Aves 1.14.5 stayed at 0:00 for the one-frame file, while all multi-frame variants progressed over approximately three seconds and looped. Xiaomi Gallery opened the samples through MediaStore, but actual playback progress was not obtained; its compatibility is not yet accepted. The 5 fps default is the lowest tested multi-frame cadence and materially cheaper than 10/30 fps, but is larger and uses more memory/time than the original single-frame comparator. Full device evidence is recorded in the independent StarshotGallery project, docs/reports/starshot-gallery-20261009.md.

## Retained sample groups

Only three representative groups were retained under `build/hdr-video-cadence-samples-20261009/`:

- `cadence-hevc-1080p/`: one-frame, 5, 10, and 30 fps gradient comparisons.
- `hdr-4k/`: 4K HEVC and AV1 HDR outputs.
- `sdr-odd/`: odd-sized SDR HEVC and AV1 outputs.

The default 5 fps outputs validated 15 frames and 3 seconds for each codec. HDR tests verified the existing PQ/BT.2020 and HDR metadata checks; SDR odd-size tests verified BT.709 and dimension padding. StaticVideoSaver and HDR conversion/model code were not changed for this verification task. No install, publish, or push was performed.

## Matched UHDR still companion

`build/hdr-video-cadence-samples-20261009/cadence-hevc-1080p/cadence-5fps.jpg` is a **1920×1080** Ultra HDR JPEG paired with `cadence-5fps.mp4`; it was generated from the first decoded frame of that exact HEVC file, so its pixels share the video's scene and dimensions. The source MP4 is HEVC and already lossy, so the still represents a lossy decode of the encoded sample rather than the original pre-encode synthetic frame.

FFmpeg 7.1.1 decoded one frame to planar `gbrpf32le` (G, B, R planes), retaining the input's tagged BT.2020/PQ transfer:

```powershell
F:\WhisperJAV\Library\bin\ffmpeg.exe -hide_banner -loglevel error -i build\hdr-video-cadence-samples-20261009\cadence-hevc-1080p\cadence-5fps.mp4 -map 0:v:0 -frames:v 1 -pix_fmt gbrpf32le -f rawvideo uhdr-companion-source.gbrpf32le -y
```

The temporary harness decoded ST 2084 PQ to absolute nits using the standard `m1=2610/16384`, `m2=2523/32`, `c1=3424/4096`, `c2=2413/128`, and `c3=2392/128` constants; transformed linear BT.2020 RGB to BT.709/scRGB with `R'=(1.660491R−0.587641G−0.072850B)/80`, `G'=(-0.124550R+1.132900G−0.008349B)/80`, `B'=(-0.018151R−0.100579G+1.118730B)/80`; and packed `R16G16B16A16Float` (`1.0 = 80 nits`). It then called the production `ImageSaver.SaveAsUhdrAsync(frame, output, 1000, 80)` and reopened the JPEG using `UhdrDecoder`. The output is 181,904 bytes; decoded gainmap capacity metadata is **1.000×–12.600×**. The reusable one-off conversion source is in `uhdr-companion-harness.cs`.

The adjacent descriptor is `cadence-5fps.mp4.starshot.json`; it references the matching JPEG and records `frameRate: "5/1"`, 15 frames, 3,000 ms, HEVC, and HDR.
