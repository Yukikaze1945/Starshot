# PP-OCRv6 Tiny OCR 迁移与验证 — 2026-10-08

默认 OCR 已直接替换为 SimdPaddleOCR PP-OCRv6 Tiny，包含 DET、CLS、REC；没有 OneOCR/Paddle A/B。Debug、Release、自包含且裁剪的 Release publish 均通过。最终隔离产物为 `build/ocr-tiny-publish/app`（2.6.0-preview.7），本轮未修改安装目录、推送 GitHub 或发布 Release。

## 1. 修改文件

- 引擎、像素准备、结果适配与异常回退：`src/Starshot/Helpers/OcrHelper.cs`。
- 应用退出释放：`src/Starshot/App.xaml.cs`。
- HDR 查看器 OCR 输入及移除 OneOCR 提示：`src/Starshot/Features/Screenshot/ImageViewWindow.xaml.cs`。
- 图库桥接 OCR 输入、取消传递与 OneOCR 设置清理：`src/Starshot/Features/WebUI/WebUiBridge.cs`。
- 清除下载门禁：`src/Starshot/Features/Screenshot/ScreenshotPage.xaml.cs`、`ClipboardPage.xaml.cs`。
- 设置清理：`src/Starshot/AppConfig.Setting.cs`、`Features/Setting/ScreenshotSetting.xaml`、`ScreenshotSetting.xaml.cs`、`StorageSetting.xaml.cs`。
- Web 设置与消息类型：`src/Starshot.WebUI/src/SettingsPanel.tsx`、`bridge.ts`、`types.ts`。
- NuGet、模型保留及许可部署：`src/Starshot/Starshot.csproj`、`third_party/simdpaddleocr/{LICENSE,THIRD-PARTY-NOTICES.md,README.md}`。
- 文档：`README.md`、`src/Starshot.WebUI/README.md`、`docs/releasing-fork.md`、本报告与验证 CSV/日志。
- 删除：`src/Starshot/Helpers/OneOcrNative.cs`、`Features/Setting/OcrEngineDialog.xaml`、`OcrEngineDialog.xaml.cs`。
- 仅改旧引擎说明注释：`Features/Screenshot/ScreenCaptureService.cs`，无功能修改。
- 开发验证工具：`tools/OcrTinyTest/{Program.cs,OcrTinyTest.csproj,README.md,PublishedSmoke/Program.cs,PublishedSmoke/PublishedSmoke.csproj}`。

WGC、MonitorCaptureContext、ScreenCaptureHelper、轻量截图实现、RegionCaptureWindow、HDR/P3/UHDR/AVIF/视频编码源码无改动。OCR 结果窗口、翻译界面和剪贴板写入逻辑保持原实现。

## 2. 固定版本与来源

| 组件 | 版本 |
| --- | --- |
| Sdcb.SimdPaddleOCR | 1.4.2 |
| Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny | 1.0.0 |
| Sdcb.SimdPaddleOCR.Models.TextLineOrientation | 1.0.0，官方传递依赖 |
| Sdcb.SimdPaddleOCR.ModelProvider | 1.0.0，官方传递依赖 |

使用[官方 SimdPaddleOCR](https://github.com/sdcb/SimdPaddleOCR/tree/v1.4.2)及其正式 NuGet API，不按更新中的 main 分支 API 编程。核心源码版本 `733c0b08a97dfd358a43a0a74938c21f6bccdbb9`；Tiny 模型包源码版本 `315e4fff4466260fd8f09cd4d53a70ec3b63f65c`。

模型通过官方资源包内嵌程序集流加载。发布显式保留核心、ModelProvider、Tiny、TextLineOrientation 四个程序集；不读取用户 NuGet 缓存、开发机模型路径或解压临时模型。现有安装/portable 打包递归复制整个发布目录，包含这些程序集及 `ThirdParty/SimdPaddleOCR` 许可文件。Apache-2.0 与 PaddlePaddle attribution 均保留。

OCR 未新增 OpenCV 或 ONNX Runtime 依赖；SimdPaddleOCR 使用自己的托管执行实现。现有 WindowsAppSDK 自包含部署原本会附带 WinML/ONNX 相关组件，本次 OCR 不调用它们。

## 3. 模型大小

从最终发布程序集读取嵌入资源的实际字节数：

| 资源 | 字节 |
| --- | ---: |
| Tiny DET | 1,780,590 |
| Tiny REC | 4,462,639 |
| 字典 | 34,057 |
| CLS | 1,018,940 |
| 合计 | **7,296,226，约 6.96 MiB** |

两个模型程序集落盘合计 7,344,128 字节（约 7.00 MiB）；加核心与 ModelProvider 后四个发布程序集约 8.29 MiB。没有约 95MB OneOCR 下载。

## 4–6. 输入、尺寸限制与 HDR

之前：统一 PreparePixels 按 Windows OCR 上限缩图；OneOCR 原地 BGRA→RGBA 交换后可能把同一数组交给 BGRA Windows 回退；部分查看器浮点图先被直接画到 8-bit 目标。

现在：

- 区域 OCR 继续直接使用覆盖层已有的 SDR BGRA crop；轻量 OCR 继续使用现有 CPU BGRA buffer；贴图继续使用已有 SDR 图。
- 普通 SDR 查看器/图库直接准备原图像素。图库原先对 SDR 图片也调用 HDR 转换，现已取消这次重复转换。
- HDR 查看器/图库先调用现有 `ScreenCaptureService.TonemapToSdr`，再把其 RGBA8 结果正确转换为 BGRA8；不增加新的 HDR 算法。`PreparePixels` 明确拒绝未经 SDR 转换的 FP16/FP32 输入。
- BGRA32 原始 buffer、宽高与 stride 直接传给官方 `Run`。不编码 PNG/JPEG、不落盘、不做原地通道交换。所有测试输入哈希保持不变。
- `PreparePixels` 保留原分辨率且 Scale=1；Windows 的 `MaxImageDimension` 仅出现在 Windows 异常回退内部。
- Tiny 内部仍使用官方检测预处理，检测最长边配置为 **2048**，REC 使用原始输入裁出的文本。默认 960 在 4K/22px 测试中漏检；2048 同一输入识别出完整 `4K HUD 60 FPS`。不自行重写 resize/后处理，也没有锐化、二值化或 CLAHE。

HDR 发布验证确实调用最终 Starshot.dll 的 `TonemapToSdr → PreparePixels → RecognizeAsync`。合成 scRGB 前景最大 2.5；SDR 输入通道范围 30–253；识别成功，模型从最终发布目录加载，初始化一次、零回退、输入不变。

## 7–8. OneOCR 与 fallback

OneOCR 原生调用、引擎选择、下载 URL/对话框/下载门禁已删除。旧配置中的未知引擎字段不再影响当前选择；本轮不操作旧安装目录残留文件。Windows.Media.Ocr 仅在 Tiny 实际异常时回退；取消、无文本不会回退。

正常日志：`[OCR] engine=SimdPaddleOCR-Tiny input=WxH lines=N elapsed=... ms`。实际异常明确记录 `[OCR] SimdPaddleOCR failed, falling back to Windows.Media.Ocr`，释放失败的 Tiny 实例，后续可重新 lazy 初始化。

测试末尾故意 Dispose 引擎制造一次异常：Windows 回退成功，结果 `GAME SETTINGS`，输入 buffer 不变，损坏引擎已退役。该检查用于验证错误路径，不作引擎质量 A/B。

## 9–11. 时间、生命周期与内存

第一次调用 lazy 创建；串行调用共享同一实例；退出 Dispose。LineWorkerCount=2、DET 线程=2，DET/CLS/REC 池上限分别 1/2/2，不按 CPU 核数创建大池。

独立进程使用同一 1280×720 SDR 输入：

| 指标 | 结果 |
| --- | ---: |
| 模型初始化 | **110 ms** |
| 第一次纯识别（引擎日志） | **369 ms** |
| 第一次完整调用 | **500 ms** |
| 第二次调用 | **142 ms** |
| 后续六次热调用 | **126 / 170 / 147 / 142 / 126 / 125 ms** |
| 热调用中位数 | **134 ms** |
| OCR 前 Private Memory | **78.81 MiB** |
| 第一次后 | **210.55 MiB，增加 131.74 MiB** |
| 第二次后 | **214.83 MiB** |
| 六次热调用首尾 | **215.33 → 218.36 MiB，增加 3.02 MiB** |

八次调用初始化计数始终 1，fallback 始终 0；未强制 GC。多尺寸测试完成后的另一段六次热调用约 532.31→530.25 MiB，包含保留测试图和 Win2D 宿主。短序列没有出现大幅逐次积累，但独立小图序列仍有约 3 MiB 的托管分配/运行时增长；不能把短样本称为零内存增长或长期泄漏排除。池数量在代码中受上限约束，本轮不增加 idle timeout 或独立 OCR 进程。

以上是测试进程 Private Memory，不是安装版 Starshot 总内存，也不是 GPU 专用内存。第一次增量包含模型、首次推理/JIT和工作空间，未单独拆出模型常驻字节。

## 12. 实际识别与发布测试

测试像素全部由 Win2D 在内存中合成，无桌面截图操作、图片落盘或剪贴板修改。游戏 UI 测试为模拟文字，不冒充真实游戏截图。

| 场景 | 实际输出 | 完整调用 ms |
| --- | --- | ---: |
| 中文 | 星光截图文字识别 | 467（首次） |
| 英文 | GAME SETTINGS | 148 |
| 混排 | FPS 120帧 | 125 |
| 深色白字 | DARK MODE 2026 | 77 |
| 浅色黑字 | LIGHT MODE 2026 | 65 |
| 模拟游戏小字/带 padding stride | HP 100 / MP 60 | 137 |
| HDR→SDR | HDR PREVIEW 203 NITS | 65 |
| 普通 SDR | ORDINARY SDR 80 NITS | 176 |
| 小选区 | Small crop: OCR 42 | 30 |
| 4K、22px 小字 | 4K HUD 60 FPS | 354 |

十类均通过文本、行矩形、格式化输出及输入不变检查；正常 cases 零回退。另验证无效 buffer/预取消不加载引擎、4K PreparePixels 原尺寸、FP16 输入拒绝、Windows fallback stride packing 不改源数组。

WebUI 构建及十项前端测试通过；Debug 与 Release 编译通过。最终 trimmed/self-contained Release publish 通过；发布程序集 OCR/HDR 冒烟通过（267 ms），四个 Sdcb 依赖的实际加载位置均在发布目录内。发布原生 UHDR DLL 哈希及已有视频组件校验通过。

测试宿主初次混用了 stock Win2D 与 Starward Win2D，导致 0x80040111；只修正测试项目资产选择，生产捕获代码无改动。最终宿主与 Starshot 一致排除 stock 资产，随后通过。

原始/汇总证据见 [验证数据目录](ocr-tiny-validation-20261008/)，包含 recognition.csv、memory.csv、ocr.log、published-smoke.log、model-resources.log。无测试图片需要清理。

## 13. 已知问题和验证边界

- 正式 1.4.2 尚无 main 文档中的 CTC alignment/EstimateCharacterBoxes API；保持准确的行框，每行作为一个可选文本单元，未伪造单词框。查看器细粒度词选区退化为行级，整行复制和排版仍可用。
- RecognitionScore 原样保留；本版本实测值约 28–39，不能解释为 0–1 概率或百分比。本轮不修改上游后处理。
- 输入保留原分辨率不等于 DET 完全不缩放；检测仍由 PaddleOCR 原生预处理限到 2048。更小字、复杂真实游戏/低对比内容的质量没有被这十类合成输入完全证明。
- 区域入口、行文本排序、OCR popup 与复制实现保留，格式化适配已通过；未操作真实选区窗口或系统剪贴板，故没有声称完成真实桌面区域复制端到端测试。
- 本轮只验证 x64；未覆盖安装版，也未制作/实装安装包。发布模型可用及现有安装/portable 递归打包布局已核查，不依赖 NuGet cache。
