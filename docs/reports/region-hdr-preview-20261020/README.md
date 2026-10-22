# 区域截图：可拖动分析面板、色域分析与原生 HDR 预览

2026-10-20。源码 Debug 与隔离合成测试；不安装、不发布、不推送，不执行真实桌面截图或桌面鼠标键盘操作。

## 原因与修改

原亮度面板只有事件拦截，没有拖动实现；每次工具栏刷新还会重新计算它的位置。现在标题区提供移动光标和拖动提示，使用指针捕获移动；位置以虚拟桌面物理像素保存，刷新后保留，并限制在某一显示器工作区内。关闭、取消和丢失指针捕获会结束拖动，关闭后清空手动位置。按钮、滚动、滑块和波形展开独立交互。

原覆盖层虽然保存原始 FP16 数据，但使用 BGRA8 双缓冲并绘制 `_displayBitmap`，因此看到的是 SDR 辅助图。现在原始来源为 FP16 时优先创建 FP16 双缓冲，直接绘制 `_canvasOriginal`。HDR 像素不再经过辅助 SDR 图、白电平归一化或 OETF；选区尺寸标签显示“原生 HDR”。若 FP16 swap chain 创建失败，记录警告并回退 SDR 预览。

这遵循微软的 [FP16 / scRGB 显示路径](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/high-dynamic-range)：浮点 swap chain 默认使用线性 BT.709 scRGB，Windows 合成器再适配目标显示器。SDR 复制、OCR 与取色继续使用独立的原 SDR 辅助图，不把 HDR 原图直接夹断来冒充 SDR。

捕获的混合显示器 composite 会为了共用导出白基准而提亮 SDR 屏部分；原生显示时只对这些 SDR 显示器的区域撤销该提亮。HDR 显示器部分的原始数值保持不变，不修改捕获或编码逻辑。屏幕外的暗化蒙层仍是选区交互的一部分，选区内显示干净的原始画面。

## 色域分析

“更多 → 色域分析”直接在截图覆盖层打开另一块可拖动面板，不进入图库。菜单编辑器可调整入口；旧版 v3 配置一次性补入该工具，v4 明确删除的入口不会被强制恢复。

显示四类互斥像素占比：sRGB 内、P3 超出 sRGB、BT.2020 超出 P3、BT.2020 外；附 CIE 1931 xy 密度图与三角形参考，以及可调透明度的区域叠层。黑色/非正亮度/无效色度与非有限输入排除并单独计数。保留原始 scRGB 负通道，复用图库已有分类矩阵与相对容差，不是对显示器色域覆盖率的测量。

按需从选区读取约 1 MiB 的 FP16 条带，GPU 操作仍在 UI dispatcher；每条带之后异步计算并让出消息处理。最多约 100 万点用于统计，抽样会标明近似。缓存区域图最长边 1024 像素，色度密度缓存 128×128，生成的色度图 256×256，不保留整张 FP16 CPU 副本。8 秒上限，支持取消；与亮度分析共用串行读取生命周期，旧 generation 不得发布结果。

区域叠层默认关闭，只有启用后才分配小型显示纹理；关闭开关立即 Dispose 纹理。亮度热力图与色域伪彩色互斥，避免混合后误读颜色。关闭面板、重新选区或结束截图清除统计、区域图、界面引用及纹理。所有叠层只在预览渲染，不进入保存、复制、OCR 或原始 HDR。

SDR/轻量 GDI 入口保持禁用：不根据普通 8-bit 图猜测原始 HDR 色域，不为轻量模式创建 WGC 或 Win2D。

## 修改文件

- `src/Starshot/Features/Screenshot/RegionCaptureWindow.AnalysisPanels.cs`：共用标题拖动、指针捕获与手动位置。
- `RegionCaptureWindow.HdrAnalysis.cs`：亮度面板接入拖动、保留位置、共享串行读取、叠层互斥、焦点保护；原亮度计算和波形不变。
- `RegionCaptureWindow.GamutAnalysis.cs`：按需色域分析、面板、图表、叠层与取消清理。
- `RegionCaptureWindow.HdrPreview.cs`：FP16 swap chain、原始冻结帧显示、混合 SDR 显示器白电平还原、创建失败回退。
- `RegionCaptureWindow.xaml/.xaml.cs`：面板容器、原生 HDR 绘制分发、预览标识、清理。
- `RegionCaptureWindow.Toolbar.cs`、`CpuRegionCaptureWindow.Toolbar.cs`、`RegionToolbarModel.cs`、`RegionToolbarPreferences.cs`：菜单、可用性、Esc、边界限制及配置升级。
- `src/Starshot/Features/Workspace/GamutAnalysis.cs`：新增条带累积与有界色度图；已有图库分析入口保持原方法和结果语义。
- `tools/RegionToolbarUiTest`、`tools/RegionToolbarTest/Program.cs`、`tools/WorkspaceCoreTest/Program.cs`、`tools/LightweightCaptureTest/Program.cs`：相关验证。UI 宿主显式引用应用同版 ONNX Runtime，避免 SDK 自动引入旧引用；测试不运行模型。

未修改 WGC、MonitorCaptureContext、HDR/UHDR/AVIF 编码、普通 OCR 推理或 GDI 捕获实现。没有加入持续刷新 timer 或生产 GC.Collect。

## 验证范围与限制

应用 Debug 构建 0 警告、0 错误。隐藏 XAML 宿主 83 项检查通过，使用真实 Win2D 纹理和真实控件，验证原始负通道与 >1 高光逐字节保留、混合显示器白电平、200% DPI 物理像素映射、菜单、拖动位置持久、边界、取消、Esc、快速重新打开、换选区清理与保存/复制隔离。拖动的几何和状态通过内部入口驱动，未注入桌面鼠标或执行真实手势。

纯 CPU 核心测试 47 项、工具栏 2,540,871 断言、轻量 GDI 249 项、亮度数学 32 项通过。GDI 测试包含故意跨线程销毁窗口的预期警告；最终检查通过，不代表真实应用报错。

UI 测试宿主存在 Windows App SDK 自动初始化类型重复的 CS0436 警告；不影响构建或实际运行。首次使用的诊断输出目录名还触发 PRI 目录限定符提示，运行时已使用应用自身 PRI。没有把测试警告当成应用的编译错误。

FP16 双缓冲相比 BGRA8 双缓冲理论额外用量为每像素 8 字节：3840×2160 时增加 63.28 MiB。只在 HDR 覆盖层打开期间需要，仍按原关闭策略 Dispose；未增加空闲常驻资源。这个数字是格式大小推算，不是驱动所有 residency 的实测增量。

实测耗时、KMT 和 Private Memory 见 [4k-analysis-time.log](4k-analysis-time.log)、[4k-analysis-memory.csv](4k-analysis-memory.csv)，UI 总结果见 [xaml-interaction.log](xaml-interaction.log)。最终 4K 色域分析 656 ms，920,880 有效抽样点，区域图 2,359,296 字节（2.25 MiB）；首次运行曾为 324 ms，存在运行环境波动，不能视为固定延迟。最终五次亮度分析 1252–1435 ms，中位 1300 ms。这是 Debug 合成测试，不应与此前 Release 的 460 ms 数据直接比较。短测不能证明长期零增长；本轮没有强制回收。

没有验收真实 HDR 显示器上的高光观感、显示器自身 tone mapping、真实鼠标跨 DPI 拖动以及 HDR 状态在截图期间切换。合成数据证明提交给显示链的数据未提前变成 SDR，不等于测量屏幕实际发光。普通 SDR 显示器仍由系统转换/裁剪，不能靠软件变成 HDR。

关闭旧源码进程后使用仓库根目录 `Run-Starshot.cmd` 重新构建启动。在系统 HDR 开启且使用高质量 HDR/HDR 视频捕获、确实获得 FP16 原始帧时，覆盖层会自动采用原生 HDR 路径。只写入本报告与诊断日志，没有测试截图文件。
