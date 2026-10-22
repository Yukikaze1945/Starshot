# Local inference runtime

Pinned official llama.cpp CPU and Vulkan x64 packages: b11429.
https://github.com/ggml-org/llama.cpp/releases/tag/b11429

Archive: llama-b11429-bin-win-cpu-x64.zip
SHA-256: 1283323272B04CD07905816A597A0DA810918102DE958F4FF6F7BBAA70ED2EFE
Source: https://github.com/ggml-org/llama.cpp/tree/b11429

Optional GPU archive: llama-b11429-bin-win-vulkan-x64.zip
SHA-256: 1BFE78AD9168B79FA02BF67F6AF9F5E17A966D824D77238517F7BEF12AC73B36
The CPU and Vulkan packages are deployed separately; selecting CPU never loads
the Vulkan backend. Vulkan requires a compatible installed graphics driver.

The executable and required DLLs are unmodified. Starshot starts one private
loopback server with a random internal key and four CPU threads. GPU mode requests
all layers on Vulkan0; it never silently reports a CPU fallback as GPU inference.
An opt-in authenticated local text API proxies to that same worker.
Models are separate data-only DLC. No executable from a model repository is run.

llama.cpp / ggml: MIT, copyright The ggml authors; see LICENSE.
The CPU distribution contains LLVM OpenMP (libomp.dll), Apache-2.0 WITH LLVM
exception; see LLVM-LICENSE.txt and https://github.com/llvm/llvm-project.
llama.cpp's vendored cpp-httplib and nlohmann/json retain their MIT licenses in
the corresponding source tree. Preserve these notices when distributing.

Restore: scripts/Restore-LocalInference.ps1. Both restore and runtime verify
SHA-256. The application never searches PATH for a translation executable.
