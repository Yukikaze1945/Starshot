# StarshotPerceptual 实现与验证

日期：2026-10-08。普通 SDR 图片文件导出已经接入独立的 Win2D / D3D11 shader，不使用 `HdrToneMapEffect` 生成这些文件。不增加算法设置。

## 修改范围

| 文件 | 本轮改动 |
| --- | --- |
| `src/Starshot/Features/Codec/StarshotPerceptual.cs` | 参数、GPU 亮度摘要、输出纹理与资源释放 |
| `src/Starshot/Features/Codec/StarshotPerceptualShader.cs` | PQ shoulder、OKLab gamut mapper、sRGB OETF、摘要 shader |
| `src/Starshot/Features/Screenshot/ScreenCaptureService.cs` | 高质量捕获的 `deleteHDR` 普通 SDR 文件分支接入新算法，显式写 sRGB 色彩标记 |
| `src/Starshot/Features/Screenshot/ImageBatchConvertWindow.xaml.cs` | 已识别 HDR 源批量导出普通 JPEG/PNG 使用新算法 |
| `src/Starshot/Features/Screenshot/ImageViewWindow.xaml.cs` | 具有线性 BT.709 scRGB 合同的 HDR 源导出普通 SDR JPEG/PNG 使用新算法 |
| `tools/PerceptualToneMapTest/` | 独立数学、真实 GPU、编码、发布版及性能验证工具 |
| 本报告及同名证据目录 | 可复查的 JSON、CSV、构建日志 |

工作区原先存在的 OCR 迁移改动保留，不属于本轮算法改动。

以下文件相对 Git HEAD 没有改动：`ImageSaver.cs`、`HDR10GammaEffect.cs`、`UhdrColor.cs`、`UhdrGainmapEffect.cs`、`MonitorCaptureContext.cs`、`RegionCaptureWindow.cs`、`ScreenCaptureHelper.cs`。UHDR SDR base / RGB gain map / Scheme D / Display-P3、HDR AVIF、固定版本 libultrahdr、WGC、轻量 GDI、用户设置及保存格式均保留。通用 `TonemapToSdr` 继续服务现有预览、剪贴板、OCR 和 SDR 视频；本轮不统一这些路径。

截图的 HDR 主文件和 UHDR 附加文件路由没有改变；新算法作用于原有普通 SDR 文件分支。任意 ICC / float JXL 的输入传递函数和色域不能仅从 float 类型推断，查看器遇到不具备 BT.709 合同的文件仍保留原来的显示转换。

## scRGB、参考白与亮度

Windows 线性 scRGB 的 `1.0 = 80 nit`，基色为 BT.709。先保留有符号 RGB，再且仅再执行一次 `RGB *= 80 / W`，其中 W 为调用方已经取得的 Windows SDR 参考白。W 合法范围为 1–10000 nit，非有限或非正值回退到 80 nit。

`Y = 0.2126 R + 0.7152 G + 0.0722 B`。输入 NaN 置零，Infinity 和异常有限值限制在 FP16 可表示范围 ±65504；不在色域映射前截掉负通道。非正/极低亮度 Y≤1e-8 输出黑色。负 RGB 的广色域信号一直保留到 OKLab 映射。

## SDR identity 与高光 shoulder

固定关键参数：identity knee=0.8；paper white=0.94；PQ white 端点斜率=0.2。内容统计不调整 identity 或 paper white。

设 PQ 使用 ST.2084 常数，`k=PQ(0.8W)`、`w=PQ(W)`、`a=PQ(0.94W)`、`p=PQ(YW)`：

1. Y≤0.8：亮度原样输出，合法 sRGB 色域内 RGB 原样保留。
2. 0.8<Y≤1：在 PQ 域使用三次 Hermite 桥。`h=w-k; t=(p-k)/h`，输出 `H00(t)k + H10(t)h + H01(t)a + H11(t)h·0.2`。knee 的斜率为 1，white 的斜率为 0.2，连接为 C1 连续且单调。
3. Y>1：`d=w-a; q=0.2(p-w)/d`，输出 `a+d[1-(1+s q)^(-1/s)]`。导数为 `0.2(1+s q)^(-1/s-1)>0`，渐近到目标参考白，不裁掉超过 P99.9 或实际峰值的像素。

PQ 逆变换后得到目标线性亮度，RGB 同比例乘以 Yout/Y；随后进行感知色域映射。

这是必要且明确的白色取舍：若中性 SDR 白色占满输出 1.0，其上 HDR 中性高光就没有任何合法单调输出空间。本实现让原始白色输出线性 0.94，8-bit code 为 **248**，比 255 少 7 code；阴影、中间调和合法色域内 Y≤0.8 不受此白色压缩影响。不能称为“整个 SDR 区间完全 identity”。

内容自适应只影响超过参考白的尾部。设 T=max(W,P99.9)，`rd=clamp(displayPeak/T,1,4)`、`rp=clamp(actualPeak/T,1,4)`，则 `reference=T·sqrt(rd)·rp^(1/8)`，`s=clamp(log2(reference/W)/2,0.75,3)`。缺失显示器峰值不引入额外曝光。孤立极亮像素对 reference 的影响有界，最大约 1.189 倍；显示能力因素最大 2 倍。完整实际峰值仍参与尾部设计，没有分位数硬裁切。

## 内容亮度统计

使用第二个小型 GPU shader：block=ceil(max(width,height)/256)。每个 block 读取全部像素得到真实最大 Y，并返回三个分层采样位置的 Y。GPU 输出 RGBA32F 摘要；CPU 只对摘要中的三个采样通道排序取得近似 P99.9。

3840×2160 时摘要为 **240×135×16 = 518400 byte（506.25 KiB）**，包含 97200 个分位数样本。没有将整张 FP16 截图读回 CPU。实际峰值来自 GPU 分块最大值，含边缘像素；P99.9 是采样估计值，不是精确全图直方图。规则性细小图案可能影响分位数估计。

摘要 effect 的 transform mapper 显式声明缩小后的区域，避免 Direct2D 为摘要创建全屏 FP32 中间纹理。非 96 DPI 的位图使用同一 Direct3D surface 的 96 DPI 视图，避免隐式 DPI 重采样漏掉单像素峰值。该视图没有 GPU 像素复制。

## 感知式 gamut mapping

使用标准 OKLab 矩阵及有符号立方根，适用于 SDR 目标范围的色相/色度投影；它不是 HDR 绝对亮度的均匀色貌模型。输入已经先经过亮度 shoulder。

- 合法 sRGB 且处于 identity 区间的颜色直接通过。
- 以保持 a:b 比例的方向搜索该 hue 的 sRGB 最大色度 cusp：12 步二分求 L=1 的非负 RGB 边界，再缩放到 max RGB=1。
- 对超色域且高色度的高光，应用 `Lout=Lcusp·[1+e/(1+4e)]`，`e=max(L/Lcusp-1,0)`，渐近到 1.25Lcusp，导数为正。按 C/L 的 0.15–0.30 smoothstep 混合，保护低色度中性色和肤色。此参数经过排除抖动影响的红色渐变断言验证。
- 使用自适应中性 L0 作为锚点：`delta=L-0.5; E=0.5+abs(delta)+0.2C; L0=0.5[1+sign(delta)(E-sqrt(E²-2abs(delta)))]`，范围限制到 [1e-6,1-1e-6]。
- 沿 `(L0+t(L-L0), t·a, t·b)` 的色相保持射线，16 步二分求合法 sRGB 边界 tB。
- 在边界内最多保留 2% 软压缩区，按 HDR 权重或越界幅度渐变；SDR 越界量趋零时软区也趋零，避免文字抗锯齿的微负通道跨零后产生可见跳变。指数软肩将颜色放在合法边界内。
- 最后的 RGB saturate 仅处理数值舍入误差。实际 gamut mapping 不是分别裁 RGB 或 `rgb/=maxChannel`。

之后执行标准分段 sRGB OETF，输出 B8G8R8A8 UIntNormalized。仅 Y>1 的 HDR 高光使用确定性的 ±0.5 LSB 抖动，SDR 文本不加噪。没有 LUT、额外图形库、常驻大纹理或新 CanvasDevice 策略。输出和摘要纹理、effect、临时 surface 视图均按调用释放；输出所有权交给现有调用方。

## 自动验证

验证使用实际生产 shader、独立 CPU PQ/Hermite/OKLab 期望值及实际图片编码器。没有桌面鼠标/键盘操作，真实自动截图数为 **0/10**。测试图片仅在工具自己创建的 GUID temp 目录中生成并在 finally 删除，没有在用户图片目录保留文件。

覆盖内容包括：

- 80/203/250/400 nit SDR identity、400 个随机合法 SDR 颜色、Segoe UI 和微软雅黑真实抗锯齿文字。
- 独立 C1 和正导数检查（W=1、80、203、250、400、10000）；16k 中性渐变；SDR/HDR 交界。
- 纯红、绿、蓝、青、黄色 HDR 渐变和肤色；有符号随机 RGB 的 OKLab 色相；微负红通道跨零连续性。
- NaN/Infinity/极大有限值与显式清洗后的同坐标输入精确输出一致；零和非正亮度黑色；极亮中性/绿色保持有效颜色。
- GPU 摘要真实峰值、非整块图像边缘、96/144 DPI、三采样通道；异常元数据和内容改变下 SDR 前缀的稳定性。
- PNG sRGB+cICP；实际解码 SDR AVIF 的 BT.709/sRGB 标记、JXL sRGB 标记；HDR AVIF 与 canonical UHDR 的小图真实编码。
- 加载实际经过裁剪优化的发布版 Starshot.dll 调用 Render，检查与源码渲染的字节一致性及 HDR/负值表现。

最终 **53/53 项检查通过**。SDR 随机颜色最大误差约 0.55 LSB，实测抗锯齿中英文文字最大误差为 0 LSB；400 个随机输入中 382 个满足正亮度/可评估色相条件，OKLab hue 平均误差约 0.29°、最大 3.92°。4×纯红输出 (255,108,91)，色相误差约 0.84°；16×纯红误差约 0.85°；肤色固定色块误差约 0.71°。输出非红通道是保持色相、提高可表示亮度的色域折中，并非 RGB 通道裁剪。

纯色高光的层次不能只按不同 RGB tuple 数计数，因为抖动本身会制造 tuple。按编码后的中性等亮度值累计超过 1.5 LSB 才算实质色阶：纯红 1×–16× 为 4 级、蓝为 6 级，绿/青/黄为 2 级，均未观察到超过 1 LSB 的亮度倒退。2^-16–2^16 的实际 GPU 中性对数渐变也没有超过 1 LSB 的倒退，HDR 区域共 8 种输出 code。极端饱和色的高倍部分仍明显压缩；8-bit 输出并不能保留所有相邻 HDR 强度层级，不能据此声称游戏特效都已无损保留。

不同内容参数使 tailShape 从 0.75 变到约 2.947 时，6 个 SDR 输入（包括白色、边界饱和色和微负通道）逐字节一致；HDR 尾部实际发生变化。固定 P99.9=320 nit，实际峰值由 320 提到 1000000 nit，tailShape 只从 1 变到 1.125。NaN/Inf 检查比较同坐标的清洗前后输入输出，不使用“byte 值不超过 255”这类无效断言。最终发布版与源码渲染逐字节一致。

Release 构建成功，0 警告/0 错误。裁剪优化的独立 publish 成功；仍存在原有第三方库 IL2104 trim warnings，发布版 shader 实际调用检查通过。没有覆盖正式安装目录。

## 性能和显存

本机包含 NVIDIA GeForce RTX 5060 Ti（驱动 32.0.16.1714）及虚拟显示适配器。计数是测试进程的 GPU resident / committed 合计，不能等同于 Starshot 安装版全部进程或桌面捕获耗时。

测试源为 GPU 生成的 4K FP16 图，复用同一源测五轮，映射后只读 1 像素以等待 GPU 完成。只统计内容摘要与映射阶段，不包含 WGC、选区显示、磁盘编码和保存。完整数值在 `starshot-perceptual-validation-20261008/performance.csv`。

| 项目 | 最终测量 |
| --- | --- |
| 首轮摘要统计 / 映射完成 | 10.683 / 2.515 ms（合计 13.198 ms） |
| 第 2–5 轮摘要统计 | 10.059–12.437 ms |
| 第 2–5 轮映射完成 | 0.997–1.461 ms |
| 第 2–5 轮统计+映射 | 11.081–13.898 ms，均值 11.963 ms |
| 源纹理生成并完成 GPU 提交后 dedicated resident/committed | 193.95 MiB |
| 首轮摘要之后 | 195.22 MiB，摘要阶段新增 1.27 MiB |
| 首轮映射及输出释放后 | 258.98 MiB，映射阶段新增 63.76 MiB |
| 第 2–5 轮 / 所有源释放后等待 5 秒 | 均为 258.98 MiB，没有本轮累积增长，也没有观察到立即回落 |
| Private Memory：源就绪 / 首轮后 / 第五轮后 / 等待 5 秒 | 187.83 / 221.52 / 226.88 / 226.86 MiB |

摘要的 1.27 MiB 不是算法全部 GPU 开销。映射阶段仍有 4K BGRA 输出及 Direct2D 工作纹理/驻留，测得约 63.76 MiB 的新增驻留；测试源生成阶段本身另有约 128 MiB 的跃升。这些计数不能归因到 WGC 生命周期或正式程序空闲预算。本轮没有增加显式长期 GPU 缓存，五轮没有持续 GPU 增长；尚未证明其他驱动下的长期 residency 行为。

不强制 GC。摘要 readback byte[] 和采样 float[] 是短命托管分配，五轮 Private Memory 可暂时上升；不能用这五轮证明长期托管内存绝对平坦。GPU 资源 Dispose 后驱动/设备 residency 不必立即回落，报告同时保留最终等待 5 秒的数字，不把 Dispose 当成显存已归零。

## 尚未验证与限制

没有实际采集 HDR 游戏、霓虹、火焰或真人肤色画面；验证的是合成颜色、文字与实际编码。不同厂商 HDR 策略、多 HDR 显示器不同参考白、其他 GPU/驱动、极端色域/图案的采样偏差仍需真实素材验证。现有多屏合成与参考白选择没有改动。

本轮按要求保留 overlay / clipboard / OCR / viewer preview 的旧转换，故普通 SDR 文件与这些预览可能存在差异。非 scRGB 合同的任意 ICC/float JXL 输入也不声称已统一升级。sRGB 色相保持意味着允许亮度/色度折中，尤其不可能同时保持无限 HDR 高光亮度、最大饱和度和 8-bit SDR 原始白色。

## 参考与来源

scRGB 亮度合同：[Microsoft Advanced Color](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/high-dynamic-range)。PQ 高光压缩背景：[ITU-R BT.2390](https://www.itu.int/pub/R-REP-BT.2390)。OKLab 与色相保持 gamut 投影：[OKLab](https://bottosson.github.io/posts/oklab/)、[Gamut clipping](https://bottosson.github.io/posts/gamutclipping/)。GPU/DPI 行为：[Win2D custom effects](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/custom-effects)、[DPI and DIPs](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/dpi-and-dips)。

参考 Snow Shot 的 PQ 域 shoulder 思路：[当前官方 tone-map shader](https://github.com/mg-chao/snow-apps/blob/main/snow-crates/crates/snow-capture/src/platform/windows/tonemap_cs.hlsl)。Starshot 的参数、内容统计、肩部及 gamut 实现独立，不复制其应用 shader 代码，不沿用其提前丢弃负 RGB 或最终 max-channel 缩放路径。
