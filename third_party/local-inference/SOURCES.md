# Local inference runtime

Pinned official llama.cpp CPU x64 package: b11429.
https://github.com/ggml-org/llama.cpp/releases/tag/b11429

Archive: llama-b11429-bin-win-cpu-x64.zip
SHA-256: 1283323272B04CD07905816A597A0DA810918102DE958F4FF6F7BBAA70ED2EFE
Source: https://github.com/ggml-org/llama.cpp/tree/b11429

The executable and required CPU DLLs are unmodified. Starshot starts a private
loopback server with a random API key, GPU offload disabled and four CPU threads.
Models are separate data-only DLC. No executable from a model repository is run.

llama.cpp / ggml: MIT, copyright The ggml authors; see LICENSE.
The CPU distribution contains LLVM OpenMP (libomp.dll), Apache-2.0 WITH LLVM
exception; see LLVM-LICENSE.txt and https://github.com/llvm/llvm-project.
llama.cpp's vendored cpp-httplib and nlohmann/json retain their MIT licenses in
the corresponding source tree. Preserve these notices when distributing.

Restore: scripts/Restore-LocalInference.ps1. Both restore and runtime verify
SHA-256. The application never searches PATH for a translation executable.
