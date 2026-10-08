# Static HDR video capture

2026-10-08 — accepted for isolated Windows x64 builds.

The fourth capture mode snapshots the same high-quality WGC frame as HDR image
capture, then writes a companion image and one MP4 video sample of three seconds.
It does not record for three seconds or generate duplicate frames. SDR sources
remain SDR. The existing screenshot modes retain their persisted numeric values.

An independent, pinned FFmpeg process provides CPU libx265/SVT-AV1 encoding and
ffprobe validates the completed sample before it is committed. This keeps encoder
resources out of idle/lightweight operation and makes results independent of the
machine's encoder installations and graphics-driver encoding capabilities. The
cost is an additional approximately 151 MiB component payload, CPU encoding time,
and third-party GPL distribution/source obligations. The runtime stays offline.

The frozen HDR crop uses the existing scRGB-to-BT.2020/PQ shader, followed by
10-bit limited-range YUV420 conversion. It never encodes the SDR overlay preview
as HDR. HEVC SEI / AV1 metadata includes valid content-light data and only measured,
valid single-display mastering data. Original pixels stay in the image; video-only
edge padding accommodates chroma and minimum encoder dimensions.

Image success is independent of video success. Cancellation destroys the child
process and partial MP4 while preserving the already saved image. The paired base
name is allocated under the existing encode gate. Metadata sidecars identify
generated video codec, dynamic range and companion thumbnail without retaining a
GPU surface or image cache. Arbitrary MP4 files remain playable/copyable and are
never passed to image OCR/pin loaders.

Alternative hardware encoders are deferred: GPU residency and driver metadata
variation would complicate this initial single-frame path. Duplicating frames for
player compatibility is explicitly disallowed unless the user changes that
requirement. Android/iOS player compatibility is a separate sample-based manual
check; container and decoder validation do not prove all phone players support it.
