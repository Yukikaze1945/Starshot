# FFmpeg video component provenance

Starshot launches this component as an independent command-line program. The
component is GPL v3, including its enabled GPL libraries; it is not labelled MIT
or LGPL. Starshot's own LICENSE remains separate. See the accompanying LICENSE.

Pinned Windows x64 distribution:
https://github.com/GyanD/codexffmpeg/releases/tag/7.1.1

Archive: `ffmpeg-7.1.1-full_build-shared.zip`
SHA-256: `9F28727E8B472A04C1D2E520AAA425DCA82721B995139B35710091130EA6E699`

The accompanying upstream README.txt records the complete enabled-component list,
build configuration and external library revisions. `ffmpeg -buildconf` prints the
configure arguments. The binaries are unmodified; ffplay, headers and import
libraries are omitted from the application payload. Starshot uses CPU libx265 and
SVT-AV1, RGB-to-YUV scaling, MP4 muxing and ffprobe validation only.

Exact FFmpeg source revision:
https://github.com/FFmpeg/FFmpeg/commit/db69d06eee
Source archive: https://github.com/FFmpeg/FFmpeg/archive/db69d06eee.zip

Codec source repositories (select the revisions in README.txt):
- x265: https://bitbucket.org/multicoreware/x265_git/src/master/
- SVT-AV1: https://gitlab.com/AOMediaCodec/SVT-AV1
- zimg: https://github.com/sekrit-twc/zimg

Distribution/build records and requests for corresponding external-library source
and toolchain material are maintained by the binary distributor:
https://github.com/GyanD/codexffmpeg
https://www.gyan.dev/ffmpeg/builds/

Restore with `scripts/Restore-VideoEncoder.ps1`. Build/publish copies these files
to VideoEncoder and verifies them against the canonical manifest. This is an
offline runtime dependency: the application never downloads or searches PATH for
an encoder. A future redistributed release must include the matching third-party
source/build materials or a valid source offer as required by its licences; source
links alone must not be described as full GPL compliance. This task makes an
isolated local build and does not publish a redistributed release.
