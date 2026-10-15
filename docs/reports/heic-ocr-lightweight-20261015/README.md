# HEIC、紧凑 OCR 窗口与轻量选区绘制

日期：2026-10-15。当前工作区修改，未安装、未推送、未发布。

用户最新决定：今后默认直接运行源码 Debug 版，不再默认制作 Release 或安装包。本轮在这项决定之前已完成一次隔离 Release 验证；后续交付入口改为根目录 `Run-Starshot.cmd`。

## 源码运行

双击 `D:/coding/Starshot/Run-Starshot.cmd`。它检查 .NET 10 SDK 和 Node.js，恢复依赖、增量构建 Debug，再启动程序。

- 程序：`build/source-run/app/Starshot.exe`。
- 独立配置：`build/source-run/config.sjson`；不复制或覆盖安装版配置。
- 先从托盘退出已有 Starshot；脚本不会终止用户进程，避免单实例接管或覆盖正在使用的 DLL。
- `Run-Starshot.cmd -BuildOnly` 只构建。`-DotnetPath` 可指定 SDK。
- 兼容 Windows PowerShell 5.1；脚本不修改全局 NuGet 配置，恢复时显式使用官方源，绕开本机已失效的本地源。
- `global.json` 使用 .NET 10 的最新已安装稳定 feature band。没有运行发布、安装或管理员提权命令。

## HEIC

SDR/HDR 格式设置各增加数值 3，旧默认值及格式数值保持。全屏、区域、轻量保存和视频伴随图片使用同一选择。图库增加 HEIC 筛选、导入、CPU 缩略图；原生查看器可打开 SDR 与 PQ HDR HEIC，另存为也增加 HEIC。

- SDR：BT.709/sRGB，10-bit，HEVC Main10，4:2:0。
- HDR：原始浮点图经过现有 scRGB→BT.2020/PQ 转换，再以 10-bit HEIC 编码；没有使用 SDR 预览编码 HDR。写入可用 MaxCLL/MaxFALL 和 XMP。
- 最高质量仍为有损 4:2:0，设置界面明确标示；不保存 alpha。
- 独立 CPU 进程，libheif 1.23.6、x265 4.2、libde265 1.1.3，约 3.29 MiB。进程退出后无常驻编解码器；轻量输入不创建 Win2D/WGC。
- 组件与源代码包、完整许可证、源发布哈希、构建脚本随输出部署，运行前核验组件 SHA-256。
- Worker SHA-256：`0EEAE4B20EACFAD2308AE78F2EF1893CF05F0AB19FEBF1A84EC269C24F734729`。
- 本轮仅提供 Windows x64 组件。HLG HEIC 明确拒绝，未宣称支持所有外部 ICC/HEIF 变体。

没有修改 UHDR SDR base、gain map、P3 UHDR、AVIF 算法或 WGC 生命周期。原 `uhdr.dll` SHA-256 仍是 `415EE12ED6E979D1A96A495AFCB634D54FBACF69E5B9E45C95C38793C737384B`。

## OCR 窗口

初始外框从 600×430 改为 460×300 DIP，最小 360×230。首次显示按文字量估计高度，范围 250–430 DIP，并限制在工作区内；已显示的窗口保留用户调整尺寸。减少外边距、移除编辑区描边和阴影、收紧页签/底栏/翻译栏，保留文字编辑、格式、复制、自动复制与翻译设置。

没有修改本轮 OCR 推理、模型或截图预处理。

## 轻量选区闪烁

原来在窗口 DC 依次画暗底、亮选区、标注和工具栏，可能暴露不完整帧。现在在 CPU DIB 中完成整帧，再一次 BitBlt 上屏；关闭选区时释放 DIB/DC。保持事件驱动绘制，未添加持续重绘计时器或 GPU 依赖。

4K 会话新增一个约 31.64 MiB 的 CPU 绘制缓冲，非 dedicated VRAM。隐藏窗口检查验证了完成帧的选区像素、关闭后的 DIB/DC 释放、重复关闭和已有工具行为。没有操作桌面鼠标键盘，实际闪烁是否完全消失仍需用户交互确认；不能把合成检查当成实机视觉验收。

## 测试

| 检查 | 结果 |
|---|---|
| HEIC 独立组件 | 10 组场景通过：SDR、HDR、最高质量、1×1、4K、HDR 最高质量、P3、损坏输入、取消、哈希损坏 |
| 内容与部署断言 | 原尺寸、10-bit、CICP、MaxCLL/MaxFALL、XMP、BGRA 通道、缩略图上限与编码进程退出通过 |
| 4K 合成 HDR 编码 | 3840×2160，质量 80，3095 ms；低复杂度色块，不能代表真实图片体积或耗时 |
| 应用真实 Win2D 保存→打开 | SDR / FP16 HDR 均通过，Debug 与隔离 Release 均验证 |
| HDR 亮度往返 | 80/200/800/4000 nits 输入，读回约 78.63/201.88/785.63/3962.50 nits |
| 轻量 CPU 检查 | 132 项通过，无桌面输入、截图或图像文件；故意跨线程销毁的负例会产生一条预期日志 |
| OCR 前端 | 4 组尺寸/浅深色、200% 缩放通过，包含小至 344×198 的内容区、格式、复制、翻译与设置边界检查 |
| OCR 原生隐藏窗口 | 2 次实际 WebView2 初始化通过，内容区 447×265，无横向溢出，编辑框 border=0 |
| 前端既有单元测试 | 10/10 通过 |
| Debug / 隔离 Release | 构建与组件校验通过；源码运行脚本的 Debug 构建 0 警告、0 错误 |

原生 OCR 测试与构建并发时，WebView2 就绪分别约 2.85 / 4.92 秒；这不是稳定的启动性能基准。这里不声称浏览器启动时间已降低。

测试图片仅使用独立 temp 目录，测试代码 finally 清理；没有真实桌面截图，没有残留测试图片，没有覆盖安装目录。

未验证：手机 HEIC HDR 实际显示、外部查看器兼容性、真实桌面闪烁、多屏混合 DPI 的人工交互、ARM64 组件。

## 清理限制

自动审批拒绝删除隔离 WebView2 缓存及测试辅助文件，理由仅为 `blocked by policy`。未换手段强行删除。保留位置：

- `D:/coding/starshot-uhdr-work/heic-native/popup-profile`：隐藏测试的浏览器缓存，没有测试截图。
- `D:/coding/Starshot/build/heic-preview/app/HeicNativeTest*`：验证用宿主文件，不在最终推荐的 `build/source-run/app` 中。

## 主要修改位置

- `Run-Starshot.cmd`、`Run-Starshot.ps1`、`global.json`、README：源码运行入口。
- `Features/Codec/HeicCodec.cs`、`ImageSaver.cs`、`ImageLoader.cs`：进程编解码、保存与加载。
- `ScreenCaptureService.cs`、`ScreenCaptureService.Lightweight.cs`、格式设置/消息桥/图库筛选/查看器与贴图导入：格式集成。
- `Features/ViewHost/MainWindow.cs`、`Starshot.WebUI/src/OcrPopup.css`：紧凑 OCR 窗口。
- `Features/Screenshot/CpuRegionCaptureWindow.cs`：CPU 整帧绘制与缓冲释放。
- `third_party/heic`、`tools/HeicCodec`、`Build-HeicCodec.ps1`、项目内容部署：固定组件、许可与可重建源码。
- `HeicCodecTest`、`HeicNativeTest`、`LightweightCaptureTest`、`OcrPopupUiTest`：相关自动验证。

日志与 JSON 见本目录；更完整的构建日志位于 `D:/coding/starshot-uhdr-work/heic-native`。
