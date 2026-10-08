# HDR 亮度分析仪验收记录

日期：2026-10-09。仅隔离构建；没有覆盖安装版、推送或发布。

## 功能与数据来源

- 区域截图「更多 → HDR 亮度分析」打开可关闭的浅色浮动面板，沿用 Starshot 中性色和强调色 `#DDF369`。
- Cursor / Max / Avg / Min / P99 均来自冻结后的原始 `R16G16B16A16Float` scRGB 选区。显示器 SDR White 与报告峰值单独显示，不参与内容亮度归一化。
- 唯一绝对亮度公式为 `max(0, (0.2126R + 0.7152G + 0.0722B) × 80)`。负通道保留到亮度求和之后；非有限通道按零处理。P99 使用 nearest-rank。
- 波形按需展开，横轴为选区水平位置，纵轴为线性 nits；自动刻度或固定 400 / 1000 / 2000 / 4000 / 10000 nits。标出 SDR White 与处于刻度内的 1000-nit 参考线，超刻度亮度落在顶部。
- 热力图默认关闭，开启后异步生成，颜色节点为 0 / 80 / 203 / 400 / 1000 / 4000 / 10000 nits，节点间插值。支持不透明度调节和返回原始预览；关闭开关立即 Dispose 热力图纹理。
- 热力图只在覆盖层显示绘制中使用。原始 HDR、SDR crop、标注导出、保存及复制接口均不读取分析叠层。
- BGRA8、轻量 GDI、SDR 显示器区域以及混合 HDR/SDR 选区禁用绝对 nits 分析并显示原因。混合 composite 中被白电平提升的 SDR 区域不冒充原始绝对 HDR 信号。轻量分支仅共享一个禁用命令，没有新增图形依赖。

## 性能与生命周期

- 主动开启时才准备分析缓存；显示器元数据也延迟查询。每次最多读取约 1 MiB FP16 条带，复用调用方 `IBuffer`。不产生整幅 FP16 CPU 副本，不在 redraw 或鼠标移动中 readback。
- GPU 条带读取与现有覆盖层绘制在 UI dispatcher 串行进行；每条带之间让出输入处理机会。亮度转换、统计、排序、波形和热力图计算在后台执行。
- 缓存上限 16,000,000 个亮度样本；亮度缓存最多 61.04 MiB，P99 工作副本同样最多 61.04 MiB，另有约 1 MiB 条带和小型可选分析图。该上限约束活跃分析缓冲，不是整个进程 Private Memory 的硬上限。
- 4K 逐像素分析；8K 使用每 2 像素采样。抽样统计和 Cursor 显示 `≈`，明确提示可能漏掉孤立峰值。热力图最大边长 1024 px，波形 256×128。
- 8 秒预算在条带之间和统计完成后检查。原生同步读取及 `Array.Sort` 本身无法中途强制打断；取消在这些操作返回后生效，排序始终在后台。不是对驱动阻塞的硬实时保证。
- 关闭面板、关闭截图、重新选区、选区变化、窗口 Cleanup 均取消任务、失效 generation、清除 CPU 缓存/波形图并 Dispose 热力图。快速重开等待前一次分析收尾，防止多个大缓存同时创建；旧任务不能复活新面板。

## 自动验证

没有操作桌面鼠标键盘，没有真实屏幕捕获，也没有生成持久测试图片。

| 验证 | 结果 |
| --- | --- |
| 纯数学测试 | 32 项通过：0 / 1 / 2.5 / 10 灰阶分别 0 / 80 / 200 / 800 nits；负通道、NaN/Infinity、stride、P99、色标、波形横向定位、抽样、取消、Dispose |
| 轻量 GDI 测试 | 108 项通过，HDR 分析禁用命令为无副作用操作 |
| 共享布局测试 | 1,210,101 个断言通过，包括 126 个 DPI/屏幕基准组合、负原点、混合 DPI、分析面板边界和有可用空间时不覆盖主工具栏 |
| 真实 XAML 测试 | Debug 50 / Release 59 项通过；隐藏 HWND，合成 FP16 纹理，200% Cursor 映射、元数据区分、真实按钮事件、固定波形刻度、热力图切换、Esc、选区变化、立即取消与会话清理 |
| 输出隔离 | 开启真实热力图纹理后，原始 FP16 bytes 与 SDR 输出 crop 都逐字节保持一致 |
| 构建 | Debug / Release 均 0 警告、0 错误 |

### 4K 原生 GPU 条带读取实测

Release 隐藏 XAML 测试使用 3840×2160 合成原始 FP16 纹理，连续执行 5 次开启/关闭分析；每次包含原生 GPU 条带读取及精确统计。时间和 KMT 显存 / Private Memory 原始值见 [耗时日志](release/4k-analysis-time.log) 和 [内存 CSV](release/4k-analysis-memory.csv)。耗时分别为 538 / 460 / 419 / 457 / 462 ms，中位数 460 ms。

4K 缓存为 31.64 MiB。默认不开热力图时专用显存只增加约 8 KiB，后续开启保持相同水平；共享驻留增加约 5.06 MiB。关闭后的字段/纹理清理断言全部通过，但 Private Memory 不会立即返回起点：五次关闭后增量分别约 69.81 / 133.74 / 198.04 / 230.11 / 231.11 MiB。对象引用已释放并不能证明 CLR 已完成回收，进程堆保留及驱动 residency 也需与应用持有对象区分。这里没有强制 GC，也没有用两轮结果宣称进程内存稳定。五轮不足以证明长期驻留上限，不能把清理断言描述成进程内存已经立即归零。

独立 CPU 基准（包括合成输入填充、统计、热力图和波形）：4K 1.84 s，8K 3.13 s；两者缓存均 31.64 MiB。它不是桌面截图耗时，也不是 8K GPU 读取基准。

## 实际修改

新增：

- `src/Starshot/Features/Screenshot/HdrLuminanceAnalysis.cs`：有界 FP16 分析、统计、色标、波形。
- `src/Starshot/Features/Screenshot/RegionCaptureWindow.HdrAnalysis.cs`：数据资格、异步条带读取、浮动面板、热力图及清理。
- `tools/HdrLuminanceTest/`：数学、边界、取消及 4K/8K CPU 基准。

扩展上一轮未提交的工具栏实现：

- `RegionToolbarModel.cs`：更多菜单命令与分析面板定位。
- `RegionCaptureWindow.Toolbar.cs`：入口、禁用原因、Esc 与面板控制焦点。
- `CpuRegionCaptureWindow.Toolbar.cs`：禁用入口及提示。
- `RegionCaptureWindow.xaml` / `.xaml.cs`：分析容器、显示专用叠层、鼠标缓存查询和清理挂钩。
- `tools/RegionToolbarTest/`、`tools/LightweightCaptureTest/Program.cs`、`tools/RegionToolbarUiTest/`：布局、禁用操作、原生 XAML、原生条带及显存验证。

没有修改捕获实现、WGC context 生命周期、HDR/P3/UHDR 编码、OCR 推理、图像保存算法或轻量捕获路径。

## 参考与限制

设计参考 [Lilium HDR/SDR Analysis](https://github.com/EndlesslyFlowering/ReShade_HDR_shaders/blob/master/Shaders/lilium__hdr_and_sdr_analysis.fx) 的独立读数、亮度分布波形和可选伪彩色显示概念。未复制 GPL shader 源码，计算与绘制实现为本项目独立代码。条带读取使用 [Win2D 的调用方 IBuffer 接口](https://microsoft.github.io/Win2D/WinUI3/html/M_Microsoft_Graphics_Canvas_CanvasBitmap_GetPixelBytes_3.htm)。

尚未进行真实多显示器 HDR 内容、特殊驱动阻塞及屏幕发光亮度的硬件测量。分析结果是 scRGB 内容信号估计；内容峰值、显示器报告峰值、SDR White 是不同概念，不能据此直接断言最终显示裁剪。8K 抽样可能漏掉细小峰值，热力图本身也有可视化分辨率限制。
