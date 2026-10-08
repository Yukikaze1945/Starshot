# PP-OCRv6 Small 可选 DLC — 2026-10-08

已完成：Tiny 仍是内置默认，Small 为可选数据 DLC。设置 → OCR 与翻译可选择模型；设置 → DLC 提供下载、进度、取消、重新下载、强制校验、使用、删除和官方来源入口。完成隔离 Release/publish，版本 `2.6.0-preview.8`，目录 `build/ocr-small-publish/app`。本轮没有覆盖安装版、推送 GitHub 或发布 Release。

## 本轮修改文件

- `src/Starshot/Helpers/OcrModelStore.cs`：固定资源清单、下载状态、SHA-256、取消、原子安装、本地 model provider、删除。
- `src/Starshot/Helpers/OcrHelper.cs`：单实例 Tiny/Small 生命周期、串行切换、Small → Tiny 安全回退；原有 `PreparePixels`、HDR SDR 输入、BGRA32、结果坐标适配和 Windows 最终回退保留。
- `src/Starshot/AppConfig.Setting.cs`：持久化 `OcrModel`，默认 `tiny`；设置缓存及配置落盘加锁，避免 OCR 后台回退写配置与设置页写配置竞争。列表取值返回副本，避免锁外修改正在序列化的列表。
- `src/Starshot/Features/WebUI/WebUiBridge.cs`：允许列表中的 DLC RPC、设置同步、固定官方来源链接。
- `src/Starshot.WebUI/src/{SettingsPanel.tsx,OcrModels.tsx,types.ts,bridge.ts,styles.css}`：模型选择及 DLC 管理，沿用现有设计；浏览器预览不伪造模型下载。
- `third_party/simdpaddleocr/{README.md,small-manifest.json}`：固定来源、校验值、部署与许可；既有 Apache-2.0 LICENSE/THIRD-PARTY-NOTICES 继续随发布包落地。
- `tools/OcrSmallTest/{OcrSmallTest.csproj,Program.cs,AppConfig.cs,ui-check.mjs}`：模型下载/错误/生命周期/识别/重启/资源及无桌面操作 UI 验证。
- `tools/OcrTinyTest/{OcrTinyTest.csproj,AppConfig.cs,PublishedSmoke/Program.cs}`：保持已有 Tiny 验证工具可编译，增加实际发布程序集 Small provider 测试。
- `README.md`、`docs/releasing-fork.md`、`src/Starshot.WebUI/README.md` 和本报告。

本轮未修改截图显存优化、WGC context、轻量截图、RegionCaptureWindow、截图 SDR 预处理、HDR/P3/UHDR/AVIF 编码。工作区中这些路径已有上一轮尚未提交的修改，不能把整个 `git diff` 误计为本轮修改。

## 版本、来源、大小和 SHA-256

运行时：`Sdcb.SimdPaddleOCR 1.4.2`；`ModelProvider 1.0.0`；Tiny 内置模型包 `ChineseV6Tiny 1.0.0`；共享 `TextLineOrientation 1.0.0`。

Small 数据对应官方 `Sdcb.SimdPaddleOCR.Models.ChineseV6Small 1.0.0`。官方包源提交：`315e4fff4466260fd8f09cd4d53a70ec3b63f65c`。Starshot 不引用、下载或执行 Small 模型 DLL，只读取下表三个模型数据文件。

| 本地文件 | 官方固定下载来源 | 实际字节数 | SHA-256 |
|---|---|---:|---|
| det.onnx | [small_det.onnx](https://raw.githubusercontent.com/sdcb/SimdPaddleOCR/315e4fff4466260fd8f09cd4d53a70ec3b63f65c/models/small_det.onnx) | 9,880,512 | `D73E0058B7A8086BBD57F3D10B8BCD4FF95363F67E06E2762B5E814FE9C9410E` |
| rec.onnx | [small_rec.onnx](https://raw.githubusercontent.com/sdcb/SimdPaddleOCR/315e4fff4466260fd8f09cd4d53a70ec3b63f65c/models/small_rec.onnx) | 21,159,378 | `5435FD747C9E0EFE15A96D0B378D5BD157E9492ED8FD80EDF08F30D02FA24634` |
| dict.txt | [rec_keys.txt](https://raw.githubusercontent.com/sdcb/SimdPaddleOCR/315e4fff4466260fd8f09cd4d53a70ec3b63f65c/models/rec_keys.txt) | 74,947 | `B5F2BFE2BDD9448429E3E82B51C789775D9B42F2403D082B00662EB77E401C5D` |

总下载量 **31,114,837 字节 = 29.6734 MiB**。共享 CLS 的模型资源为 1,018,940 字节，已经内置，新增下载量为零。

使用 PEReader 仅把官方 NuGet 的程序集作为数据解析，未加载/执行该 DLL：DET/REC 与官方包内资源逐字节一致。NuGet 内字典为 CRLF：93,655 字节，SHA-256 `769E7FA79BB297B5F18D8DBD149E364A45BC61F2B3F574E5EA836F0B261C23A6`；固定 GitHub 源字典为 LF。归一化换行后，全部字典内容一致。第一次验证错误地要求两份字典字节一致，导致该断言失败；修正为“下载文件严格校验其固定 SHA，官方资源字典核对完整条目内容”，只重跑资源核对，已通过。原始失败日志保留，未抹掉。

许可 **Apache-2.0**；保留 `Copyright (c) 2016 PaddlePaddle Authors. All Rights Reserved.` 和 sdcb/SimdPaddleOCR attribution。官方 [项目](https://github.com/sdcb/SimdPaddleOCR) 的模型说明/第三方通知列出 PaddlePaddle 模型来源，包含 [Small DET](https://huggingface.co/PaddlePaddle/PP-OCRv6_small_det_onnx)、[Small REC](https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec_onnx)。Starshot 自身许可保持原样。

## 安装与生命周期

模型目录：`<AppConfig.UserDataFolder>/Models/OCR/ppocrv6-small-1.0.0`。安装版使用现有每用户 LocalAppData 数据根目录；便携版使用现有应用数据根目录。没有开发机绝对模型路径、Program Files 路径或运行时 NuGet cache 依赖。

依次下载 DET/REC/字典到同一数据根目录下的独立 `.install-<GUID>` 目录。只接受固定 URL，限制字节数，流式 SHA-256，刷新文件到磁盘，再验证一次实际落盘文件。整个模型组合通过同卷目录 rename 发布。安装完整前，最终目录不可见；替换失败尝试恢复旧组合。失败/取消在 finally 删除自己的临时目录。只清理专属目录里的三个已知文件，拒绝链接及未知子目录，不递归清理用户目录。

`IPaddleOcrModelProvider.OpenRead/OpenReadAsync` 返回校验后的本地文件流，`PaddleOcrModelBundle` 组合 DET/REC/字典及内置 CLS，交给 1.4.2 的 `PaddleOcrAll.LoadAsync`。没有自行实现 OCR 前后处理。

`EngineGate` 同时保护推理、切换、释放、Small 文件删除及安装发布。切换等待现有同步 `Run` 完成；取消在执行前后及等锁/加载期间观察，不会在运行中的推理上 Dispose。旧实例先释放，新实例只在下一次 OCR 才创建；没有 Tiny/Small 双实例常驻。模型初始化保留 workers=2、detThreads=2、DET pool=1、CLS/REC pool=2。

每次 Small OCR 强制核对资源 SHA，能发现文件大小/时间戳未变的损坏；模型实际打开时再次校验，避免校验与读取之间换文件。设置页的状态轮询使用缓存指纹，不反复全量读模型。状态读取不等待安装发布锁，避免“推理锁 → 状态锁”和“安装锁 → 推理锁”形成死锁。

未安装、损坏或 Small 加载/执行异常：明确日志并持久化回到 Tiny。只有实际 Tiny 异常才进入 Windows.Media.Ocr 的最后回退。模型切换不改 OCR 结果编辑、文本排序、复制行为或截图输入。

## 自动验证

总计 **10 次真实 OCR 调用**，没有桌面鼠标/键盘操作，没有写测试截图；模型/配置测试数据均放独立 temp 根目录，finally 自动删除，只保留日志、JSON、CSV。没有 OneOCR A/B、压力循环或 GC.Collect。

| 验证项 | 结果 |
|---|---|
| 官方 NuGet/固定源模型数据对应关系 | 通过；字典 LF/CRLF 差异单独说明并复核 |
| 真实网络下载、进度、SHA、最终目录原子出现 | 通过；31,114,837 字节，100 次进度采样，约 10.18 秒 |
| 断网、错误长度、正确长度但错误 SHA、取消、临时清理 | 通过；错误网络用注入传输模拟，不修改用户网络配置 |
| 下载/设置/status 仍 lazy | 通过；第一次 OCR 前初始化次数为 0 |
| 取消等锁的切换、发布等锁时继续查状态 | 通过；没有死锁，不发布未完成组合 |
| Small 中文、英文、日文 | 通过；同一内存 BGRA 选区返回 3 行，见下文 |
| 热 OCR 复用实例 | 通过；3 次 Small 调用后初始化次数仍为 1 |
| 新进程重启保存选择并按需加载 | 通过；新测试进程读取持久化 Small，OCR 前仍未加载 |
| 正在真实 OCR 时切换 | 通过；推理结束后才释放 Small，下一次 OCR 才创建 Tiny |
| 相同大小/mtime 的损坏、缺失、删除 | 通过；回 Tiny，没有 Windows fallback |
| 200% 缩放浅色/深色 DLC 与模型选择 | 通过 headless 浏览器；选择、禁用状态、下载/进度/取消/校验/切换/删除与按钮边界检查，传输为 UI 合同桩；真实下载已在上方独立验证 |
| 现有前端测试 | 10/10 通过 |
| Release build / trimmed self-contained publish | 通过；已有依赖 IL2104 裁剪警告保留，无新增 OCR 编译错误 |
| 实际 publish 程序集本地 Small provider、Tiny默认、选择持久化、删除、并行设置落盘 | 通过；不含 ChineseV6Small DLL，模型程序集来自发布目录 |

Small 的实际识别输出：

```text
截图文字识别 星光 2026
SCREENSHOT OCR / English 123
日本語の文字認識テスト
```

输入 `1280×420`，原 BGRA buffer 的 SHA 前后相同。区域 OCR 与其他 OCR 入口仍共用现有 helper/UI 适配，无截图/预处理改动。

## 时间与资源

| 场景 | 总调用耗时 | 模型初始化 | Private Memory |
|---|---:|---:|---:|
| 开发验证，加载前（下载/故障测试后） | — | 0 次 | 198.57 MiB |
| Small 第一次 OCR | 893 ms | 255 ms；累计 1 次 | 371.42 MiB（增量 172.85 MiB） |
| Small 热调用 1 | 352 ms | 仍为 1 次 | 371.59 MiB |
| Small 热调用 2 | 339 ms | 仍为 1 次 | 371.59 MiB |
| 新进程 Small 首次 OCR | 798 ms | 238 ms | 263.03 MiB（增量 202.94 MiB） |
| 实际裁剪发布包 Tiny | 303 ms | 运行时 lazy | 182.93 MiB |
| 同一发布验证进程切到 Small | 531 ms | 运行时 lazy | 335.79 MiB |

以上是隔离测试进程，不包含完整 WebUI/图库运行负载。发布验证 Tiny → Small 的 Private Memory 差值约 **152.86 MiB**，不是模型文件大小；不把释放后的托管堆保留误当成两套引擎常驻。冷 Small 到第二次热调用净变化仅 **0.18 MiB**，未见逐次模型/session 累积；短测试不能证明长时间内存绝对没有增长。

时间表为整个 helper 调用，包含 Small 文件完整性验证。引擎推理日志为首次 527 ms、热调用 247/233 ms；当前机器每次完整 hash 和调度约额外 100 ms。Small 使用 CPU，没有新 GPU 纹理、GPU 推理 session 或截图显存缓存。

## 证据与限制

- `build/ocr-small-validation/{ocr-small-validation.log,memory.csv,restart-memory.csv,results.json,final-results.json,ui.json}`。
- `build/ocr-small-resources-validation/results.json`：只重跑官方资源对应核对。
- `build/ocr-small-publish-validation/publish-memory.csv`、`build/ocr-small-published-smoke-run.log`。
- `build/ocr-small-{build,publish,frontend-tests,ui-test}.log`。
- 发布许可/manifest 与源清单 SHA 一致；发布目录中只有既有 Tiny/CLS/provider/runtime 模型程序集。

本轮没有覆盖安装版或打包/启动新的安装程序，没有进行真实桌面截图或真实 WebView2 鼠标交互；这些操作不作为已验证结果。安装/便携都通过既有 `AppConfig.UserDataFolder` 语义确定模型位置；实际发布程序集测试通过临时 user-data root 验证，不修改用户真实配置。Tiny 本身在日文样本中有错字，Small 在该样本中正确；没有对所有日文字体、竖排或游戏字体作质量保证。

复现识别验证：先用 NuGet 官方源 restore `tools/OcrSmallTest/OcrSmallTest.csproj`，构建 x64 Release，然后运行 `OcrSmallTest.exe <官方资源与校验用NuGet目录> <日志输出目录>`。发布验证：`PublishedSmoke.exe --small-dlc <publish/Starshot.dll> <官方资源目录> <日志输出目录>`。`ui-check.mjs` 参数为 Playwright 包路径、Chromium 可执行文件和报告路径，仅启动 headless 浏览器。上述测试只使用参数指定的测试资源，应用运行时无这些开发机路径依赖。
