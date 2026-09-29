# libultrahdr 2.0.2 for Starshot

`win-x64/uhdr.dll` is the verified JPEG-only Windows x64 shared library used by
Starshot's Ultra HDR JPEG output. It replaces the `uhdr.dll` runtime asset in
`Starward.Codec` 0.5.2 after the SDK's build and publish copy steps. The managed
`Starward.Codec` package and its API remain unchanged.

| Property | Value |
| --- | --- |
| Version | libultrahdr v2.0.2 |
| Target | Windows x64 |
| Size | 1,541,632 bytes |
| SHA-256 | `415EE12ED6E979D1A96A495AFCB634D54FBACF69E5B9E45C95C38793C737384B` |
| `UHDR_ENABLE_HEIF` | `FALSE` |
| `UHDR_WRITE_ISO` | `TRUE` |
| `UHDR_WRITE_XMP` | `TRUE` |

Provenance: copied from
`D:\coding\starshot-uhdr-work\build-libultrahdr-2.0.2\uhdr.dll`, which matches
the previously tested `port4-v202\Starshot\uhdr.dll` byte for byte. The source
checkout is Google libultrahdr tag `v2.0.2`, commit
`e5f5a022fe96fc4dc2ee35c19f733a50df807abe`. The local build script is
`D:\coding\starshot-uhdr-work\scripts\build-uhdr-2.0.2.bat`; it uses MSVC,
Ninja, static libjpeg-turbo, and `/MT`. Its source checkout has a local CMake
change to respect an explicitly set `CMAKE_MSVC_RUNTIME_LIBRARY` for shared
builds. The build cache records the JPEG-only and dual-write options above.

The build and publish targets in `src/Starshot/Starshot.csproj` verify this
specific SHA-256 both before and after copying. A replacement binary requires
an intentional project and provenance update.
