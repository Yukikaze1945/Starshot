# Starshot Ultra HDR 高饱和红色变淡 —— 根因、修复与验证报告

仓库：`D:\coding\Starshot`，基线 tag `2.5.3`（commit `850e666`，detached HEAD）。
本报告对应的完整补丁：`D:/coding/starshot-uhdr-work/uhdr-color-fix.patch`（见 §4 末）。

---

## 1. 根因说明

结论：**红色变淡不是 HDR 亮度或 SDR 白电平造成的，主因是「色域信息在截断那一步就丢了」，配套还有两个增益量程/分母的一致性问题会让颜色偏掉、并在 SDR 白≠80nits 时整图重建错误。** 四条都有源码或实测物证。

### 1.0 先确认输入帧到底是什么（不假定）

`SaveAsUhdrAsync` 里判定 `canvasImage.Format is DirectXPixelFormat.R16G16B16A16Float`，这支帧来自 Windows HDR 截图路径（DXGI 合成帧），语义是**线性 scRGB**：BT.709 原色、D65、扩展范围（允许 <0 和 >1）、**1.0 恒等于 80nits**。

三条独立证据（都是本次实测，不是引用声明）：

1. 广色域颜色在这个帧里确实带**负通道**：合成 P3 红输入为 `(2.450, -0.084, -0.039)`，BT.2020 红为 `(3.321, -0.249, -0.036)`（附录 A 表格 input scRGB 列）。只有扩展范围的线性 scRGB 会这样。
2. **1.0 = 80nits 与显示器 SDR 白无关**：把 `sdrWhiteLevel` 从 80 改成 320，改前的分子（未归一化的 canvasImage）让整张图重建出 4 倍亮度，灰阶误差 300%；说明源帧的 1.0 一直是 80nits 口径。
3. 白块输入 `(1,1,1)` 在 80nits 下重建 `ref=(80,80,80)`，与 Windows HDR Calibration 的 SDR 白读数一致。

### 1.1 RC-1（主因）：截断发生在换色域之前，广色域信息被永久丢弃

改前链路（`git show 850e666:src/Starshot/Features/Codec/ImageSaver.cs`）：

```
canvasImage(scRGB) ─┬─ HdrToneMapEffect ─→ WhiteLevelAdjustment ─→ SrgbGamma(OETF) ─→ 8bit base JPEG
                    └─ UhdrPixelGainEffect.HdrSource = canvasImage（未换色域）
gain = (Max(hdr.RGB,0) + OFFSET) / (Max(sdr.RGB,0) + OFFSET)      // UhdrGainmapEffect.cs
```

`Hlsl.Max(hdr.RGB, 0)` 把负通道直接抹成 0。而增益是**逐通道非负倍数**，`0 × gain = 0` 永远是 0：P3 红被抹成 `(2.450, 0, 0)` 之后，任何增益都只能得到一个比 P3 更黄、更淡的红。base 也只有 8bit sRGB（BT.709）可表达，超出 BT.709 的部分同样存不下。

实测（附录 A，80nits）：

| 色块 | 改前 working | 改前重建 chroma(u'v') | 目标 chroma | 改前 maxRelErr |
|---|---|---|---|---|
| P3 red (out of 709) | `(2.450, 0.000, 0.000)` | 1.4500 | 1.8405 | 59.7% |
| BT.2020 red (out of P3) | `(3.321, 0.000, 0.000)` | 1.4500 | 1.8318 | 70.2% |
| bright HDR red @640nits | `(8.000, 0.050, 0.020)` | 1.2283 | 1.3955 | 78.5% |
| P3 green (out of 709) | `(0.000, 2.084, 0.000)` | 0.253 | 0.341 | 39.6% |

1.4500 正是 **BT.709 红色顶点的 u'v' 色度**——改前所有饱和红都被钉在 BT.709 边界上，这就是「红色霓虹/饱和红变淡」的量化形态。

### 1.2 RC-2：增益的分母取的是浮点中间节点，不是文件里真正存着的 base

改前 `SdrSource = toneMapEffect`（tone map 后的浮点），但落盘的 base 还要再过 `WhiteLevelAdjustment → sRGB OETF → 8bit 量化 → JPEG`。libultrahdr 解码时是拿**文件里那份 base** 做 `srgbInvOetf` 后再乘增益（`jpegr.cpp:1453+ applyGainMap`，`sdrGamutConversionFn=identity`）。分母比实际 base 大的通道（被 255 截断的高光）算出来的增益接近 1，base 丢掉的余量就再也补不回来。

实测：`bright HDR red @640nits` 改前只能重建到 **137.5nits**（参考 526.875nits，误差 78.5%）；改后 **526.875nits**，与参考完全相等。附录 A 第二张表能看到分母节点与 `InvOetf(base8)` 的关系：BT.709 红的 R 通道分母 1.237 vs `InvOetf(base8_new)` 1.000（255 截断），实测 gain 1.254 与按截断值算的期望 1.233 一致（偏差 1.66%），说明分母现在确实等于文件里的那份 base。

### 1.3 RC-3：增益分子的参考白没有归一化（本次运行中新发现，用户假设里没有这条）

改前 `HdrSource = canvasImage`，其 1.0 = 80nits；而 base 存的是「1.0 = 该显示器的 SDR 白」。经过 Windows HDR Calibration 后 `sdrWhiteLevel` 往往不是 80（本机测 320），两端量程不一致：

- 整张图会被重建得亮 `sdrWhiteLevel / 80` 倍。分子归一化这一步补上**之前**的中间一轮实测（320nits）：灰阶重建约为参考的 4 倍、相对误差 300%；补上之后同一轮 320nits 灰阶误差 0.27%（附录 A）。
- 所有 gain ≤ 1，`HdrCapacityMax = Max(contentBoost[5], 1)` 退化成 1，libultrahdr 直接拒绝编码：
  `UhdrException: received bad value for hdr capacity max 1.000000, expects to be > hdr capacity min 1.000000`
  （附录 A 的 `!!` 行：最终一轮里，改前臂在 320nits 下**根本产不出文件**，所以依赖基线的对比全部记为 SKIP）。

也就是说：改前的代码只有在「SDR 白恰好等于 80nits」这一种情况下量程自洽。

### 1.4 RC-4：容器里的色彩标记与像素不匹配

- 两张子 JPEG 都声明 `UhdrColorGamut.BT709`，但像素实际是（换色域之后的）工作色域。
- WIC 存 JPEG 会自带一份 sRGB ICC。libultrahdr 只在 base **没有 ICC** 时才按声明色域合成 ICC（`icc.cpp: writeIccProfile(tf, gamut)`，rXYZ/gXYZ/bXYZ 直接取自 `kSRGB` / `kDisplayP3` / `kRec2020`，PCS 白点固定 D50）。gain map 那份 ICC 更糟：`use_base_cg=1` 时解码端把 `hdr_cg→sdr_cg` 的转换作用在重建结果上（`jpegr.cpp:1499-1502`），增益所在色域被误读就会多乘一道矩阵。
- 修好后：`base 的 ICC = Display-P3`、`gain map 无 ICC`、base 4:2:0 + gain 4:4:4。

ICC 标签的**字节级**物证（自写解析器读文件里的 APP2 段落，不依赖 Starward 的解析——它对 libultrahdr 合成的 profile 返回 id=0）：

```
new  rXYZ=(0.6820,0.3193) gXYZ=(0.2845,0.6746) bXYZ=(0.1559,0.0661) wtpt=(0.3457,0.3585)
     与 D65→D50 Bradford 适配后的 Display-P3 原色最大偏差 = 0.0000
     与适配后的 BT.709 = 0.0768
old  rXYZ=(0.6485,0.3309) gXYZ=(0.3212,0.5978) bXYZ=(0.1559,0.0660)
     与适配后的 BT.709 偏差 = 0.0000，与 P3 = 0.0768
```

`wtpt=(0.3457,0.3585)` 就是 D50，证明这些坐标是 PCS(D50) 口径（所以比对前先把公布的 D65 色度适配一次，不是拿两组不同口径的数硬比）。文件字节解出的 profile 与解码器 `GetIccData()` 返回的 profile 逐值一致，排除「解码器自己脑补色域」。

### 1.5 关于用户提出的假设

- 「`Hlsl.Max(hdr.RGB, 0)` 在换色域之前会毁掉广色域信息」——**成立**，就是 RC-1。
- 「BT.709/sRGB 的 SDR base 装不下超出 BT.709 的红」——**成立**，所以 base 必须在更宽的工作色域里生成，并把标签一起改掉（RC-4）。
- 补充：只改这两处还不够。RC-2/RC-3 两个量程问题会让红色高光仍补不回来（表现为「亮度整体偏低」），并且在 Windows HDR Calibration 之后（SDR 白 ≠ 80）会让整张图偏亮 4 倍甚至直接编码失败。这三条是同一个 Ultra HDR 通道里的耦合问题，一起修才是自洽的。

### 1.6 为什么选 Display-P3 而不是 BT.2020 作为工作色域

- 截图源帧的色域是 Windows 合成空间（scRGB/BT.709 原色），HDR 内容绝大多数落在 P3 附近；P3 能覆盖 `P3 red`、`bright HDR red` 这类真实饱和色，而 base 用 P3 与 Ultra HDR 生态的惯例（CTA.2048 的 sdr_cg 常为 P3）一致。
- BT.2020 会让 SDR-only 查看器（按 ICC 显示）的偏色更大，且对 8bit base 的量化步长更不友好。
- 超出 P3 的颜色（BT.2020 原色）仍无法完整保留，只能沿亮度线回拉（见 §7 限制 2）。

---

## 2. 修改过的文件

| 文件 | 状态 | 行数变化 |
|---|---|---|
| `src/Starshot/Features/Codec/ImageSaver.cs` | 修改 | +128 / −39 |
| `src/Starshot/Features/Codec/UhdrGainmapEffect.cs` | 修改 | +56 / −7 |
| `src/Starshot/Features/Codec/UhdrColor.cs` | 新增 | +74 |
| `tools/UhdrColorTest/UhdrColorTest.csproj` | 新增（测试工具） | +39 |
| `tools/UhdrColorTest/Program.cs` | 新增 | +146 |
| `tools/UhdrColorTest/Harness.cs` | 新增 | +927 |
| `tools/UhdrColorTest/LegacyUhdr.cs` | 新增 | +178 |
| `tools/UhdrColorTest/IccProbe.cs` | 新增 | +191 |
| `tools/UhdrColorTest/JpegProbe.cs` | 新增 | +150 |
| `tools/UhdrColorTest/Dump.cs` | 新增 | +84 |

合计 `1980 insertions(+), 39 deletions(-)`。**改动全部限制在 Ultra HDR 分支内**：`SaveAsUhdrAsync` 的 `R16G16B16A16Float` 分支、`UhdrGainmapEffect.cs` 里只被这条分支使用的两个 shader/effect、以及只被这条分支引用的新文件 `UhdrColor.cs`。HDR AVIF/JXL/PNG、普通 SDR JPEG、普通截图保存、区域截图、剪贴板、`GetContentLightLevels`（maxCLL/maxFALL）、Windows HDR Calibration 读数逻辑均未改动（`git status` 可核）。

---

## 3. 每个关键修改的原因

### 3.1 新增 `UhdrWorkingGamutEffect`（`UhdrGainmapEffect.cs`）：先换色域，再截断

```csharp
ColorMatrixEffect { Source, ColorMatrix = UhdrColor.ScRgbToWorkingMatrix, ClampOutput = false }
  → PixelShaderEffect<UhdrGamutMapShader>(ConstantBuffer = new(C{ ... WorkingLuma }))
```

- `ColorMatrixEffect` 做**线性 scRGB(BT.709) → 线性 Display-P3** 的原色转换。`ClampOutput = false` 是必须的：D2D 颜色矩阵默认会截到 [0,1]，那会在换色域这一步就把 HDR 扩展范围和负通道抹掉。
- `UhdrGamutMapShader` 做**色相保持**的域内压缩：`y = dot(luma, rgb)`，`pull = saturate(max(0-rgb,0) / max(y-rgb, 1e-6))`，`rgb = lerp(rgb, max(y,0), pull)`。这是沿亮度线向中性点回拉——**Y 不变、色相角不变**，只把通道拉回非负。逐通道硬截断会把红推向黄/白（正是 RC-1 的表现），这条线不会。
- `luma` 用工作色域 RGB→XYZ 的 **Y 行**（实测 `(0.228975, 0.691738, 0.079287)`，和为 1）。用 Y 行而不是「三行都试试」是有断言保护的：`亮度系数取到的是 XYZ 的 Y 行`。同时保证压缩后的 Y 与 `GetContentLightLevels` 算 maxCLL 时的亮度定义同源。
- base 与 gain **都吃这一个节点的输出**，两边像素定义才可能一致（用户方向 2/3 的要求）。
- 矩阵来源用 `Starward.Codec` 自带的 `ColorPrimaries.GetColorTransferMatrix(BT709, DisplayP3)`，不硬编码矩阵数值。D2D 颜色矩阵按 `out_j = Σ v_i·M[i][j]` 组织，与 `Vector3.Transform` 同式，因此可直接转 `Matrix5x4` 交给 `ColorMatrixEffect`。

### 3.2 增益两端重接（`ImageSaver.cs`）

```csharp
SdrSource = storedLinearEffect   // renderTarget_sdr(8bit base) 过 EOTF 回线性
HdrSource = hdrNormalizedEffect  // workingEffect → WhiteLevelAdjustment(80 → sdrWhiteLevel)，不过 tone map
```

- **分母 = 文件里真正存着的 base**（RC-2）：先把 base 真渲染成 `R8G8B8A8UIntNormalized` 的 `renderTarget_sdr`，再用 `SrgbGammaMode.EOTF` 取回线性值当分母。libultrahdr 解码时做的正是 `srgbInvOetf(base)`，两端同口径。为此把 `renderTarget_sdr` 的创建提前到 gain 之前（原本它在后面）。
  `SrgbGammaMode.EOTF` 是 OETF 的真逆函数（阈值 0.0031308 / 0.04045 成对），已在源码核对。
- **分子与分母同一个参考白**（RC-3）：`WhiteLevelAdjustmentEffect(InputWhiteLevel: 80, OutputWhiteLevel: sdrWhiteLevel)`，即把 scRGB 的 1.0=80nits 归一化成「1.0 = 该显示器 SDR 白」。**这一步不能省**，否则 SDR 白 ≠ 80 时整图重建亮度错 `sdrWhiteLevel/80` 倍。它是实测确认的：320nits 下改前灰阶误差 300%、改后 0.27%。
- 分子**不过 tone map**：Ultra HDR 的语义就是 base 是 tone-mapped SDR、gain 把 HDR 拉回来；分子必须是原始（换色域+域内压缩后的）HDR 信号。
- `UhdrPixelGainShader` 里的 `Hlsl.Max(..., 0)` **保留**（用户禁令 2）：负通道已经在 3.1 的换色域 + 域内压缩里解决了，走到这里时两端都非负，截断只是防御性的 16bit 下溢保护，不再毁颜色。

### 3.3 增益偏移量单一来源

`OFFSET` 从 shader 内硬编码改为构造参数 `new UhdrPixelGainShader(UhdrColor.GainOffset)`，metadata 的 `OffsetSdr/OffsetHdr` 也取同一个常量。原来两处各写 `0.015625f`，任何一处改动都会让分子分母的量程悄悄错位（断言 `实测 gain = 分子/文件里的 base` 会立刻抓到）。

### 3.4 容器：显式子采样 + 剥 ICC + 声明工作色域

- `SaveAsJpegAsync` 增加 `subsampling` 参数（`WICJpegYCrCbSubsamplingOption`：1=4:2:0、3=4:4:4）。base 用 **4:2:0**（Ultra HDR 基图惯例，也省体积）；gain map 用 **4:4:4**——它是逐通道增益，4:2:0 会让增益在色边互相渗透，产生彩色描边。质量固定 95（= libultrahdr 的 `kBaseCompressQualityDefault`/`kMapCompressQualityDefault`）。
- 新增 `RemoveJpegIccSegments`：剥掉两张子 JPEG 的 APP2 `ICC_PROFILE` 段。base 无 ICC 才会被 libultrahdr 按声明色域补一份正确的 ICC；gain map 带 ICC 会被当成增益自身所在色域，`use_base_cg=1` 路径下就多乘一道矩阵。
- base / gain map 的 `ColorGamut` 都改成 `UhdrColor.WorkingColorGamut`（DisplayP3）。这不是「只改标签」：像素已经在 3.1 里真的转成 P3 了（用户禁令 1）。gain map 那份声明同时决定解码端 `dest->cg`（重建结果的色域标签），必须等于工作色域。
- `UseBaseColorSpace = 1` **保留**，理由是 libultrahdr 源码而非猜测：
  - 解码端 `jpegr.cpp:1495-1502`：`use_base_cg=1` ⇒ `sdrGamutConversionFn = identity`（base 像素原样用），`hdrGamutConversionFn = getGamutConversionFn(hdr_cg, sdr_cg)`（作用在重建结果上）。我们的 gain 正是拿 base 色域的线性信号当分母算的，所以 base 不该再被转一次。
  - `hdr_cg` 取 gain map 声明的色域、`sdr_cg` 取 base ICC 的色域；两边都是 P3 ⇒ 这道转换是恒等。若 gain map 仍声明 BT709 而 base 是 P3，解码端会把一个 709→P3 矩阵乘在本来就已经是 P3 的重建值上，并把输出错标成 BT709。
  - 编码端 `jpegr.cpp:607-637`：当 `sdr_cg == hdr_cg` 时 libultrahdr 自己的选择也是两个转换都恒等 + `use_base_cg=true`，与本设置一致。
- `HdrCapacityMax = MathF.Max(..., 1)` 未改动（它不是问题根源；RC-3 修好后分子分母量程自然 > 1 的关系就成立了）。

### 3.5 测试工具（`tools/UhdrColorTest/`）

项目里没有图像色彩测试框架，按用户方向 8 建一个可重复执行的控制台工具：

- `Harness.cs`：合成 10 个色块（半浮点写入 `R16G16B16A16Float`），走**真 Win2D 效果链** + **真 libultrahdr 编解码**（`Starward.Codec`），两个白电平（80/320）各跑一遍，分 `new`（改后）/`old`（改前）两臂对比。
- `LegacyUhdr.cs`：逐行照抄改前实现作为基线（不是「想象中改前的样子」）。
- `IccProbe.cs` / `JpegProbe.cs`：自己解析 JPEG 段头和 ICC 标签，读文件字节而不是信解码器的声明。
- `Program.cs`：打印两张定量表 + 断言，`return 失败断言数`（0 = 全通过）。
- 断言覆盖用户方向 8/9 的每一项，另加两条自证：`解码取样自洽`（同一色块块内偏差 < 0.02，证明取样点没踩到邻块）、`同一解析器读改前的 ICC = BT.709`（解析器自检，否则「认成 P3」没意义）。

---

## 4. 完整 diff

生产代码（`src/`，含新增文件 `UhdrColor.cs`）：

```diff
diff --git a/src/Starshot/Features/Codec/ImageSaver.cs b/src/Starshot/Features/Codec/ImageSaver.cs
index 901127d..1a422ce 100644
--- a/src/Starshot/Features/Codec/ImageSaver.cs
+++ b/src/Starshot/Features/Codec/ImageSaver.cs
@@ -1,4 +1,5 @@
 using System;
+using System.Buffers.Binary;
 using System.Collections.Generic;
 using System.IO;
 using System.IO.Compression;
@@ -41,10 +42,23 @@ internal static class ImageSaver
         }
     }
 
+    // WICJpegYCrCbSubsamplingOption（Windows SDK wincodec.h）
+    private const byte JpegSubsampling420 = 1;
+    private const byte JpegSubsampling444 = 3;
+
+    /// <summary>libultrahdr 自带编码器的默认质量：kBaseCompressQualityDefault / kMapCompressQualityDefault。</summary>
+    private const int UhdrJpegQuality = 95;
+
     /// <summary>
     /// 8bit 位图存普通 JPEG（WIC，quality 0-100）。输入需已 SDR（HDR 先过 tonemap）。
+    /// subsampling 取 WICJpegYCrCbSubsamplingOption：1=4:2:0、2=4:2:2、3=4:4:4。
     /// </summary>
-    public static async Task SaveAsJpegAsync(CanvasBitmap bitmap, Stream stream, int quality)
+    public static async Task SaveAsJpegAsync(
+        CanvasBitmap bitmap,
+        Stream stream,
+        int quality,
+        byte subsampling = JpegSubsampling444
+    )
     {
         if (
             bitmap.Format
@@ -69,7 +83,7 @@ internal static class ImageSaver
         );
         options.Add(
             "JpegYCrCbSubsampling",
-            new BitmapTypedValue(3, Windows.Foundation.PropertyType.UInt8)
+            new BitmapTypedValue(subsampling, Windows.Foundation.PropertyType.UInt8)
         );
         var encoder = await BitmapEncoder.CreateAsync(
             BitmapEncoder.JpegEncoderId,
@@ -760,31 +774,75 @@ internal static class ImageSaver
         if (canvasImage.Format is DirectXPixelFormat.R16G16B16A16Float)
         {
             await Task.Delay(1).ConfigureAwait(false);
-            using HdrToneMapEffect toneMapEffect = new()
+            // 先换色域并把超域颜色压回工作色域，再做任何截断：scRGB 的广色域颜色本来就靠负通道表达，
+            // 负通道一旦被截成 0，逐通道增益再也乘不回来（增益只能放大，不能翻负）。
+            using UhdrWorkingGamutEffect workingEffect = new()
             {
                 Source = canvasImage,
+                BufferPrecision = CanvasBufferPrecision.Precision16Float,
+            };
+            using HdrToneMapEffect toneMapEffect = new()
+            {
+                Source = workingEffect,
                 InputMaxLuminance = maxCLL,
                 OutputMaxLuminance = sdrWhiteLevel,
                 DisplayMode = HdrToneMapEffectDisplayMode.Hdr,
                 BufferPrecision = CanvasBufferPrecision.Precision16Float,
             };
-            using WhiteLevelAdjustmentEffect whiteLevelEffect = new()
+            using WhiteLevelAdjustmentEffect sdrLinearEffect = new()
             {
                 Source = toneMapEffect,
                 InputWhiteLevel = 80,
                 OutputWhiteLevel = sdrWhiteLevel,
                 BufferPrecision = CanvasBufferPrecision.Precision16Float,
             };
+            // base 走的是 toneMap → 白电平 → OETF → 8bit → JPEG，libultrahdr 解码后对它做 srgbInvOetf
+            // 才拿来乘增益。所以增益的分母只能是"已经落到 8bit 的那个值"，不能是上面任何一级浮点：
+            // 分母比实际 base 小的通道（被 255 截断的高光）算出来的增益 ≈ 1，base 丢掉的余量就永久丢了。
             using SrgbGammaEffect gammaEffect = new()
             {
-                Source = whiteLevelEffect,
+                Source = sdrLinearEffect,
                 GammaMode = SrgbGammaMode.OETF,
                 BufferPrecision = CanvasBufferPrecision.Precision16Float,
             };
+
+            // 分子必须和分母用同一个参考白：base 存的是「1.0 = 该显示器的 SDR 白」，
+            // 而 scRGB 的 1.0 恒等于 80nits。少这一步，SDR 白不是 80nits 时整张图会被重建得
+            // 亮 sdrWhiteLevel/80 倍（320nits 下实测 4 倍，灰阶误差 300%）。
+            using WhiteLevelAdjustmentEffect hdrNormalizedEffect = new()
+            {
+                Source = workingEffect,
+                InputWhiteLevel = 80,
+                OutputWhiteLevel = sdrWhiteLevel,
+                BufferPrecision = CanvasBufferPrecision.Precision16Float,
+            };
+
+            using CanvasRenderTarget renderTarget_sdr = new(
+                CanvasDevice.GetSharedDevice(),
+                canvasImage.SizeInPixels.Width,
+                canvasImage.SizeInPixels.Height,
+                96,
+                DirectXPixelFormat.R8G8B8A8UIntNormalized,
+                CanvasAlphaMode.Premultiplied
+            );
+            using (CanvasDrawingSession ds = renderTarget_sdr.CreateDrawingSession())
+            {
+                ds.Units = CanvasUnits.Pixels;
+                ds.Clear(Colors.Transparent);
+                ds.DrawImage(gammaEffect);
+            }
+
+            using SrgbGammaEffect storedLinearEffect = new()
+            {
+                Source = renderTarget_sdr,
+                GammaMode = SrgbGammaMode.EOTF,
+                BufferPrecision = CanvasBufferPrecision.Precision16Float,
+            };
             using UhdrPixelGainEffect uhdrPixelGainEffect = new()
             {
-                SdrSource = toneMapEffect,
-                HdrSource = canvasImage,
+                SdrSource = storedLinearEffect,
+                HdrSource = hdrNormalizedEffect,
+                BufferPrecision = CanvasBufferPrecision.Precision16Float,
             };
 
             using CanvasRenderTarget renderTarget_gain = new(
@@ -825,34 +883,27 @@ internal static class ImageSaver
                 ds.DrawImage(uhdrGainmapEffect);
             }
 
-            using CanvasRenderTarget renderTarget_sdr = new(
-                CanvasDevice.GetSharedDevice(),
-                canvasImage.SizeInPixels.Width,
-                canvasImage.SizeInPixels.Height,
-                96,
-                DirectXPixelFormat.R8G8B8A8UIntNormalized,
-                CanvasAlphaMode.Premultiplied
-            );
-            using (CanvasDrawingSession ds = renderTarget_sdr.CreateDrawingSession())
-            {
-                ds.Units = CanvasUnits.Pixels;
-                ds.Clear(Colors.Transparent);
-                ds.DrawImage(gammaEffect);
-            }
-
             using MemoryStream ms_base = new();
             using MemoryStream ms_gainmap = new();
-            await renderTarget_sdr.SaveAsync(
-                ms_base.AsRandomAccessStream(),
-                CanvasBitmapFileFormat.Jpeg
+            // base 走 4:2:0（Ultra HDR 基图惯例，与 libultrahdr 自带编码器一致）；
+            // gain map 走 4:4:4，因为它是逐通道增益，4:2:0 会让增益在色边互相渗透。
+            await SaveAsJpegAsync(
+                renderTarget_sdr,
+                ms_base,
+                UhdrJpegQuality,
+                JpegSubsampling420
             );
-            await renderTarget_gainmap.SaveAsync(
-                ms_gainmap.AsRandomAccessStream(),
-                CanvasBitmapFileFormat.Jpeg
+            await SaveAsJpegAsync(
+                renderTarget_gainmap,
+                ms_gainmap,
+                UhdrJpegQuality,
+                JpegSubsampling444
             );
 
-            byte[] baseArray = ms_base.ToArray();
-            byte[] gainArray = ms_gainmap.ToArray();
+            // base 只有在没有 ICC 时才会被 libultrahdr 按声明色域补一份 ICC；
+            // gain map 带 ICC 会被当成增益自身所在色域，use_base_cg=1 下再乘一道转换就把增益压灰。
+            byte[] baseArray = RemoveJpegIccSegments(ms_base.ToArray());
+            byte[] gainArray = RemoveJpegIccSegments(ms_gainmap.ToArray());
 
             using var encoder = new UhdrEncoder();
             unsafe
@@ -867,7 +918,7 @@ internal static class ImageSaver
                         Data = (nint)b,
                         DataSize = (uint)baseArray.Length,
                         Capacity = (uint)baseArray.Length,
-                        ColorGamut = UhdrColorGamut.BT709,
+                        ColorGamut = UhdrColor.WorkingColorGamut,
                         ColorRange = UhdrColorRange.FullRange,
                         ColorTransfer = UhdrColorTransfer.SRGB,
                     };
@@ -876,7 +927,8 @@ internal static class ImageSaver
                         Data = (nint)g,
                         DataSize = (uint)gainArray.Length,
                         Capacity = (uint)gainArray.Length,
-                        ColorGamut = UhdrColorGamut.BT709,
+                        // 增益本身没有色域，声明成 base 色域是为了让 libultrahdr 走恒等转换
+                        ColorGamut = UhdrColor.WorkingColorGamut,
                         ColorRange = UhdrColorRange.FullRange,
                         ColorTransfer = UhdrColorTransfer.SRGB,
                     };
@@ -884,13 +936,15 @@ internal static class ImageSaver
                     UhdrGainmapMetadata metadata = new UhdrGainmapMetadata
                     {
                         Gamma = new FixedArray3<float>(1),
-                        OffsetSdr = new FixedArray3<float>(0.015625f),
-                        OffsetHdr = new FixedArray3<float>(0.015625f),
+                        OffsetSdr = new FixedArray3<float>(UhdrColor.GainOffset),
+                        OffsetHdr = new FixedArray3<float>(UhdrColor.GainOffset),
                         HdrCapacityMin = 1,
                         HdrCapacityMax = MathF.Max(
                             MathF.Max(contentBoost[3], contentBoost[4]),
                             MathF.Max(contentBoost[5], 1)
                         ),
+                        // use_base_cg≠0：base 不做色域转换，增益按 base 色域表达（jpegr.cpp applyGainMap）。
+                        // 我们的 gain 正是拿 base 的线性信号当分母算的，故为 1。
                         UseBaseColorSpace = 1,
                     };
                     metadata.MinContentBoost[0] = contentBoost[0];
@@ -907,6 +961,51 @@ internal static class ImageSaver
         }
     }
 
+    /// <summary>
+    /// 剥掉 JPEG 头里的 ICC 段（APP2 + "ICC_PROFILE\0"，可分多段）。SOS 之后的熵编码数据原样拷走。
+    /// 没找到 ICC 时返回等价字节流。
+    /// </summary>
+    private static byte[] RemoveJpegIccSegments(byte[] jpeg)
+    {
+        using var result = new MemoryStream(jpeg.Length);
+        if (jpeg.Length < 2 || jpeg[0] is not 0xFF || jpeg[1] is not 0xD8)
+        {
+            return jpeg;
+        }
+        result.Write(jpeg, 0, 2);
+        int pos = 2;
+        while (pos + 3 < jpeg.Length && jpeg[pos] is 0xFF)
+        {
+            byte marker = jpeg[pos + 1];
+            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2)) + 2;
+            if (marker is 0xFF)
+            {
+                pos++;
+                continue;
+            }
+            if (segmentLength > jpeg.Length - pos)
+            {
+                break;
+            }
+            bool isIcc =
+                marker is 0xE2
+                && segmentLength >= 16
+                && jpeg.AsSpan(pos + 4, 12).SequenceEqual("ICC_PROFILE\0"u8);
+            if (!isIcc)
+            {
+                result.Write(jpeg, pos, segmentLength);
+            }
+            pos += segmentLength;
+            if (marker is 0xDA)
+            {
+                result.Write(jpeg, pos, jpeg.Length - pos);
+                return result.ToArray();
+            }
+        }
+        result.Write(jpeg, pos, jpeg.Length - pos);
+        return result.ToArray();
+    }
+
     /// <summary>
     /// return min rgb, max rgb
     /// </summary>
diff --git a/src/Starshot/Features/Codec/UhdrColor.cs b/src/Starshot/Features/Codec/UhdrColor.cs
new file mode 100644
index 0000000..12eae2f
--- /dev/null
+++ b/src/Starshot/Features/Codec/UhdrColor.cs
@@ -0,0 +1,74 @@
+using System;
+using System.Numerics;
+using ComputeSharp;
+using Microsoft.Graphics.Canvas.Effects;
+using Starward.Codec.ICC;
+using Starward.Codec.UltraHdr;
+
+namespace Starshot.Features.Codec;
+
+/// <summary>
+/// Ultra HDR 通道（base JPEG + gain map）的色彩定义。
+/// 截图帧是线性 scRGB（BT.709 原色、1.0=80nits、允许负通道），而 base 是 8bit sRGB JPEG、
+/// gain map 只能表达逐通道非负倍数：负通道必须在截断前靠换色域解决，否则广色域红色只能被拉灰。
+/// </summary>
+internal static class UhdrColor
+{
+    /// <summary>
+    /// CTA.2048 默认增益偏移。gain 着色器和 gainmap metadata 必须同源，
+    /// 否则分子分母各自加在不同量程上，重建值逐通道偏移。
+    /// </summary>
+    public const float GainOffset = 0.015625f;
+
+    /// <summary>base 与 gain 共用的工作色域。</summary>
+    public static ColorPrimaries WorkingPrimaries { get; } = ColorPrimaries.DisplayP3;
+
+    /// <summary>base JPEG 声明的色域，必须与 <see cref="WorkingPrimaries"/> 同步（libultrahdr 据此合成 ICC）。</summary>
+    public static UhdrColorGamut WorkingColorGamut { get; } = UhdrColorGamut.DisplayP3;
+
+    /// <summary>
+    /// 线性 scRGB(BT.709) → 线性工作色域。D2D 颜色矩阵按行向量乘（out_j = Σ v_i·M[i][j]），
+    /// 与 <see cref="Vector3.Transform(Vector3, Matrix4x4)"/> 同式，故可直接转交 ColorMatrixEffect。
+    /// </summary>
+    public static Matrix5x4 ScRgbToWorkingMatrix { get; } = ToMatrix5x4(
+        ColorPrimaries.GetColorTransferMatrix(ColorPrimaries.BT709, WorkingPrimaries)
+    );
+
+    /// <summary>
+    /// 工作色域的线性亮度系数（RGB→XYZ 的 Y 行，和为 1）。色域压缩沿亮度线回拉时用，
+    /// 保证压缩后的 Y 与 <see cref="Screenshot.ScreenCaptureService.GetContentLightLevels"/> 算出的 maxCLL 同值。
+    /// </summary>
+    public static float3 WorkingLuma { get; } = GetWorkingLuma();
+
+    private static float3 GetWorkingLuma()
+    {
+        Matrix4x4 xyz = WorkingPrimaries.GetRGBToXYZMatrix();
+        return new float3(xyz.M12, xyz.M22, xyz.M32);
+    }
+
+    private static Matrix5x4 ToMatrix5x4(Matrix4x4 matrix4x4)
+    {
+        return new Matrix5x4(
+            matrix4x4.M11,
+            matrix4x4.M12,
+            matrix4x4.M13,
+            matrix4x4.M14,
+            matrix4x4.M21,
+            matrix4x4.M22,
+            matrix4x4.M23,
+            matrix4x4.M24,
+            matrix4x4.M31,
+            matrix4x4.M32,
+            matrix4x4.M33,
+            matrix4x4.M34,
+            matrix4x4.M41,
+            matrix4x4.M42,
+            matrix4x4.M43,
+            matrix4x4.M44,
+            0,
+            0,
+            0,
+            0
+        );
+    }
+}
diff --git a/src/Starshot/Features/Codec/UhdrGainmapEffect.cs b/src/Starshot/Features/Codec/UhdrGainmapEffect.cs
index 0f09e0d..b1e89f4 100644
--- a/src/Starshot/Features/Codec/UhdrGainmapEffect.cs
+++ b/src/Starshot/Features/Codec/UhdrGainmapEffect.cs
@@ -3,10 +3,42 @@ using ComputeSharp;
 using ComputeSharp.D2D1;
 using ComputeSharp.D2D1.WinUI;
 using Microsoft.Graphics.Canvas;
+using Microsoft.Graphics.Canvas.Effects;
 using Windows.Graphics.Effects;
 
 namespace Starshot.Features.Codec;
 
+/// <summary>
+/// 线性 scRGB → 线性工作色域（Display-P3），并把超出工作色域的颜色沿亮度线回拉进色域。
+/// base 和 gain 都必须吃同一个节点的输出，否则两边像素定义不一致。
+/// </summary>
+public partial class UhdrWorkingGamutEffect : CanvasEffect
+{
+    public IGraphicsEffectSource Source { get; set; }
+
+    public CanvasBufferPrecision? BufferPrecision { get; set; }
+
+    protected override void BuildEffectGraph(CanvasEffectGraph effectGraph)
+    {
+        ColorMatrixEffect colorEffect = new()
+        {
+            Source = Source,
+            ColorMatrix = UhdrColor.ScRgbToWorkingMatrix,
+            ClampOutput = false,
+            BufferPrecision = BufferPrecision,
+        };
+        PixelShaderEffect<UhdrGamutMapShader> effect = new()
+        {
+            BufferPrecision = BufferPrecision,
+            ConstantBuffer = new UhdrGamutMapShader(UhdrColor.WorkingLuma),
+        };
+        effect.Sources[0] = colorEffect;
+        effectGraph.RegisterOutputNode(effect);
+    }
+
+    protected override void ConfigureEffectGraph(CanvasEffectGraph effectGraph) { }
+}
+
 public partial class UhdrPixelGainEffect : CanvasEffect
 {
     public IGraphicsEffectSource SdrSource { get; set; }
@@ -17,7 +49,11 @@ public partial class UhdrPixelGainEffect : CanvasEffect
 
     protected override void BuildEffectGraph(CanvasEffectGraph effectGraph)
     {
-        PixelShaderEffect<UhdrPixelGainShader> effect = new() { BufferPrecision = BufferPrecision };
+        PixelShaderEffect<UhdrPixelGainShader> effect = new()
+        {
+            BufferPrecision = BufferPrecision,
+            ConstantBuffer = new UhdrPixelGainShader(UhdrColor.GainOffset),
+        };
         effect.Sources[0] = SdrSource;
         effect.Sources[1] = HdrSource;
         effectGraph.RegisterOutputNode(effect);
@@ -52,18 +88,35 @@ public partial class UhdrGainmapEffect : CanvasEffect
     }
 }
 
-[D2DInputCount(2)]
+[D2DInputCount(1)]
+[D2DInputSimple(0)]
 [D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
 [D2DGeneratedPixelShaderDescriptor]
-internal readonly partial struct UhdrPixelGainShader : ID2D1PixelShader
+internal readonly partial struct UhdrGamutMapShader(float3 luma) : ID2D1PixelShader
 {
-    private const float OFFSET = 0.015625f;
+    public float4 Execute()
+    {
+        float4 color = D2D.GetInput(0);
+        float3 rgb = color.RGB;
+        float y = Hlsl.Dot(luma, rgb);
+        // 沿亮度线向中性点回拉：Y 不变、色相角不变，t 取刚好让所有通道非负。
+        // 逐通道直接截断会把红色推向黄/白，这条线只会把它变浓回不了的程度限死。
+        float3 pull = Hlsl.Saturate(Hlsl.Max(0 - rgb, 0) / Hlsl.Max(y - rgb, 1e-6f));
+        rgb = Hlsl.Lerp(rgb, Hlsl.Max(new float3(y, y, y), 0), pull);
+        return new float4(Hlsl.Max(rgb, 0), color.A);
+    }
+}
 
+[D2DInputCount(2)]
+[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
+[D2DGeneratedPixelShaderDescriptor]
+internal readonly partial struct UhdrPixelGainShader(float offset) : ID2D1PixelShader
+{
     public float4 Execute()
     {
         float4 sdr = D2D.GetInput(0);
         float4 hdr = D2D.GetInput(1);
-        float3 gain = (Hlsl.Max(hdr.RGB, 0) + OFFSET) / (Hlsl.Max(sdr.RGB, 0) + OFFSET);
+        float3 gain = (Hlsl.Max(hdr.RGB, 0) + offset) / (Hlsl.Max(sdr.RGB, 0) + offset);
         return new float4(gain, sdr.A);
     }
 }
```

测试工具 `tools/UhdrColorTest/`（合计 1715 行，7 个文件）不在这里逐行贴：它是纯验证代码，不参与应用构建，也不被 `src/` 引用。完整内容在 `D:/coding/starshot-uhdr-work/uhdr-color-fix.patch`，10 个文件、`1980 insertions(+), 39 deletions(-)`；在干净检出（`850e666`）上 `git apply` 可直接落地，在当前工作区上 `git apply --check --reverse` 已通过（即补丁与工作区逐字节一致，生产部分与上面完全相同）。

---

## 5. 编译结果

工具链：`D:/coding/dotnet-sdk10`（SDK 10.0.401），x64，目标框架 `net10.0-windows10.0.26100.0`。

```
> dotnet restore --configfile D:/coding/starshot-uhdr-work/nuget.config && dotnet build -c Debug --no-restore   # src/Starshot
  Starshot.Language -> D:\coding\Starshot\src\Starshot.Language\bin\Debug\net10.0\Starshot.Language.dll
  0 IID calculations/fetches patched
  Starshot -> D:\coding\Starshot\build\app\Starshot.dll
已成功生成。    0 个警告    0 个错误

> dotnet build -c Debug --no-restore   # tools/UhdrColorTest
  UhdrColorTest -> ...\bin\Debug\net10.0-windows10.0.26100.0\win-x64\UhdrColorTest.dll
已成功生成。    0 个警告    0 个错误
```

还原用临时 `--configfile`（`D:/coding/starshot-uhdr-work/nuget.config`）而不是改全局配置：本机全局 NuGet 配置里有一个不存在的本地源（`D:\code项目\HDRImageViewer-main\libheif-vcpkg`），直接还原会报 `NU1301`。**没有改动用户的全局 NuGet 配置，也没有改动仓库里任何 `.csproj`/`Directory.Build.props` 之外的构建配置**（`tools/UhdrColorTest/UhdrColorTest.csproj` 是新增工具自带的）。

## 6. 测试结果

执行方式（可重复）：

```
cd Starshot/tools/UhdrColorTest && dotnet build -c Debug --no-restore
cd bin/Debug/net10.0-windows10.0.26100.0/win-x64 && dotnet UhdrColorTest.dll
```

进程退出码 = 失败断言数。最终一轮：**exit 0，`ALL ASSERTIONS PASSED`**（41 项 PASS，11 项 SKIP，0 FAIL）。SKIP 全部集中在「改前臂在 320nits 下产不出文件，因此没有基线可比」这一件事上（同一个白电平下每一项依赖基线的对比各记一条 SKIP），这本身就是 RC-3 的证据。原始输出见附录 A。

关键定量结果（u'v' 色度 = 饱和度指标；80nits 臂）：

| 色块 | 指标 | 改前 | 改后 | 目标 |
|---|---|---|---|---|
| P3 red (out of 709) | 重建 chroma | 1.4500 | **1.8403** | 1.8405 |
| BT.2020 red (out of P3) | 重建 chroma | 1.4500 | **1.8318** | 1.8318（域内压缩后） |
| bright HDR red @640nits | 重建 chroma | 1.2283 | **1.3960** | 1.3955 |
| bright HDR red @640nits | 重建亮度 R | 137.5nits | **526.875nits** | 526.875nits |
| P3 green (out of 709) | maxRelErr | 39.6% | **0.0%** | — |
| BT.709 red @120nits | maxRelErr | 33.2% | **1.7%** | — |
| BT.709 green @160nits | maxRelErr | 31.8% | **0.6%** | — |
| BT.709 blue @200nits | maxRelErr | 60.6% | **0.7%** | — |
| 灰阶（18%/white/near-black） | maxRelErr | 0.5–0.9% | **0.9% 以内** | — |
| SDR white @320nits | maxRelErr | 改前臂无文件（capacity max=min=1） | **0.3%** | — |

容器/信令结果：

- base 的 ICC 三原色与 D65→D50 Bradford 适配后的 **Display-P3** 偏差 **0.0000**（改前那份与 **BT.709** 偏差 0.0000，与 P3 偏差 0.0768）；文件字节与解码器返回的 profile 逐值一致。
- gain map：`icc=no`、`4:4:4`；base：`icc=yes`、`4:2:0`。
- 增益自洽：分母 vs 文件里的 base 最大差 **0.5/255**（80nits）、**1.3/255**（320nits）；实测 gain vs 分子/分母 最大偏离 **1.66%**（80nits）、**0.93%**（320nits）。
- 数值健康：无 NaN/Inf，`minBoost` 0.2628 / 0.9809（>0，故不会出现负增益或 0 除），`HdrCapacityMax` 6.50 / 1.64（>1）。
- SDR base 仍然合理：`base(white)=(229,229,229)`、`base(gray 18%)=(118,118,118)`、near-black `(27,29,28)`，三通道中性、随亮度单调、全部落在 [0,1]。
- 解码取样自洽：同一色块块内最大偏差 0.0000（排除「取样点踩到邻块」这种假通过）。

断言清单与逐色块数据（原始 HDR 线性 RGB → 工作色域 RGB → base 8bit → 重建 HDR → 参考值 → 相对误差 → 色度）全部在附录 A。

## 7. 仍然存在的兼容性限制

1. **base 现在是 P3 ICC。** 按 ICC 正确渲染的查看器（Windows 照片、Ultra HDR 查看器、任何 color-managed SDR 路径）显示正常；**完全忽略 ICC 的 SDR 查看器**会把 P3 当 sRGB 解读，饱和色会显得比改前更艳。这是「把饱和度保住」的必然代价，方向与改前的「偏灰」相反，不是回归。
2. **超出 P3 的颜色不可逆。** BT.2020 原色沿亮度线回拉进 P3：亮度与色相保住（实测 BT.2020 red 重建 214.844nits，误差 0.0%），饱和度只能到 P3 边界（1.8318 而非 BT.2020 顶点的 ~1.895）。任何 8bit base 都装不下 BT.2020 顶点，这一条是 base 的物理限制，不是本实现的选择。
3. **base 是 4:2:0。** 极细的高饱和色边会有亚抽样损失（Ultra HDR 基图惯例，libultrahdr 自带编码器同样默认 4:2:0）。gain map 已强制 4:4:4 以免增益在色边互渗。
4. **8bit 量化 / JPEG 失真留在结果里。** 分母用的是 EOTF(8bit base)，因此 DCT+量化的残差会成为增益的一部分：实测与浮点分母差 ≤1.3/255。若今后把 `UhdrJpegQuality`（现 95）调低，这条误差会放大。
5. **白电平只验了 80 / 320 两点，maxCLL 只验了 1000nits。** 归一化用的是 `WhiteLevelAdjustment(80 → sdrWhiteLevel)`，逻辑上任意值成立，但没有扫全档；`sdrWhiteLevel` 来自 Windows HDR Calibration 读数。
6. **只做了合成色块，没有真实截图端到端 PSNR/SSIM。** 走的是真 Win2D 效果链 + 真 libultrahdr 编解码，但纹理、噪声、大面积渐变高光等真实内容未做量化对比。
7. `HdrToneMapEffect`（Windows 自带效果）对输入所在色域敏感这一点没有被消除，只是被固定住了：base 侧现在明确喂给它 P3 工作空间的值。若换成别的 tone map 节点，两端量程需要重新实测。
8. **本报告本身未进仓库。** 代码改动已提交并推送到 fork：`Yukikaze1945/Starshot` 分支
   `fix/uhdr-wide-gamut-red`，commit `2d8ca43`（基线 `850e666` = tag `2.5.3` 的 detached HEAD，
   与本地补丁 `D:/coding/starshot-uhdr-work/uhdr-color-fix.patch` 逐字节一致）。上游 `loliri/Starshot` 未被写入。
   本报告不进那次提交：它含本机绝对路径与原始 run 日志，作为交付物留在工作区。

---

## 附录 A：UhdrColorTest 最终一轮原始输出（exit 0）

```text
UhdrColorTest  device=Microsoft.Graphics.Canvas.CanvasDevice
working gamut = 12 / DisplayP3  gain offset=0.015625
working luma = (0.2289746,0.6917385,0.0792869)  sum=1.0000000

===== maxCLL=1000nits   SDR white=80nits   (scRGB 里 SDR 白 = 1.000)   patch=32px =====
patch                          arm  input scRGB(linear)     working(base gamut)   base8(8bit)     recon(nits)     ref(nits)      maxRelErr  chroma-ref chroma-rec
------------------------------------------------------------------------------------------------------------------------------------------------------------------------
neutral gray 18%                 old  (  0.180,  0.180,  0.180) (  0.180,  0.180,  0.180) (118,118,118)    ( 14.531, 14.385, 14.404) ( 14.400, 14.400, 14.400)     0.9%    0.000    0.002
neutral gray 18%                 new  (  0.180,  0.180,  0.180) (  0.180,  0.180,  0.180) (118,118,118)    ( 14.424, 14.365, 14.277) ( 14.404, 14.404, 14.404)     0.9%    0.000    0.001
------------------------------------------------------------------------------------------------------------------------------------------------------------------------
SDR white                        old  (  1.000,  1.000,  1.000) (  1.000,  1.000,  1.000) (229,229,229)    ( 79.570, 80.000, 79.844) ( 80.000, 80.000, 80.000)     0.5%    0.000    0.001
SDR white                        new  (  1.000,  1.000,  1.000) (  1.000,  1.000,  1.000) (229,229,229)    ( 80.781, 80.156, 80.625) ( 80.000, 80.000, 80.000)     1.0%    0.000    0.002
------------------------------------------------------------------------------------------------------------------------------------------------------------------------
near black                       old  (  0.010,  0.012,  0.011) (  0.010,  0.012,  0.011) (27,29,28)       (  0.838,  0.934,  0.898) (  0.800,  0.960,  0.880)     0.0%    0.030    0.019
near black                       new  (  0.010,  0.012,  0.011) (  0.010,  0.012,  0.011) (27,29,28)       (  0.803,  0.938,  0.870) (  0.828,  0.955,  0.884)     0.0%    0.030    0.033
------------------------------------------------------------------------------------------------------------------------------------------------------------------------
BT.709 red @120nits              old  (  1.500,  0.000,  0.000) (  1.500,  0.000,  0.000) (255,3,0)        ( 80.156,  0.012,  0.000) (120.000,  0.000,  0.000)    33.2%    1.450    1.449
BT.709 red @120nits              new  (  1.500,  0.000,  0.000) (  1.233,  0.050,  0.026) (255,64,45)      (100.312,  3.997,  2.052) ( 98.672,  3.984,  2.051)     1.7%    1.450    1.455
------------------------------------------------------------------------------------------------------------------------------------------------------------------------
BT.709 green @160nits            old  (  0.000,  2.000,  0.000) (  0.000,  2.000,  0.000) (60,255,39)      (  0.023,109.141,  0.005) (  0.000,160.000,  0.000)    31.8%    0.253    0.253
BT.709 green @160nits            new  (  0.000,  2.000,  0.000) (  0.355,  1.934,  0.145) (146,255,100)    ( 28.340,154.375, 11.650) ( 28.398,154.688, 11.582)     0.6%    0.253    0.253
------------------------------------------------------------------------------------------------------------------------------------------------------------------------
BT.709 blue @200nits             old  (  0.000,  0.000,  2.500) (  0.000,  0.000,  2.500) (0,3,254)        (  0.000,  0.000, 78.789) (  0.000,  0.000,200.000)    60.6%    0.264    0.264
BT.709 blue @200nits             new  (  0.000,  0.000,  2.500) (  0.000,  0.000,  2.277) (0,3,254)        (  0.003,  0.000,180.938) (  0.000,  0.000,182.188)     0.7%    0.264    0.264
------------------------------------------------------------------------------------------------------------------------------------------------------------------------
P3 red (out of 709)              old  (  2.450, -0.084, -0.039) (  2.450,  0.000,  0.000) (254,0,0)        ( 79.062,  0.004,  0.000) (195.990,  0.000,  0.000)    59.7%    1.450    1.450
P3 red (out of 709)              new  (  2.450, -0.084, -0.039) (  1.999,  0.000,  0.000) (255,3,0)        (160.469,  0.005,  0.000) (159.922,  0.000,  0.002)     0.3%    1.841    1.840
------------------------------------------------------------------------------------------------------------------------------------------------------------------------
P3 green (out of 709)            old  ( -0.450,  2.084, -0.157) (  0.000,  2.084,  0.000) (0,255,1)        (  0.000,100.703,  0.025) (  0.000,166.729,  0.000)    39.6%    0.253    0.253
P3 green (out of 709)            new  ( -0.450,  2.084, -0.157) (  0.000,  2.000,  0.000) (60,255,39)      (  0.025,160.000,  0.014) (  0.000,160.000,  0.001)     0.0%    0.341    0.341
------------------------------------------------------------------------------------------------------------------------------------------------------------------------
bright HDR red @640nits          old  (  8.000,  0.050,  0.020) (  8.000,  0.050,  0.020) (255,98,53)      (137.500,  3.882,  1.606) (640.000,  4.000,  1.600)    78.5%    1.396    1.228
bright HDR red @640nits          new  (  8.000,  0.050,  0.020) (  6.586,  0.314,  0.159) (255,142,100)    (526.875, 25.098, 12.627) (526.875, 25.117, 12.686)     0.5%    1.396    1.396
------------------------------------------------------------------------------------------------------------------------------------------------------------------------
BT.2020 red (out of P3)          old  (  3.321, -0.249, -0.036) (  3.321,  0.000,  0.000) (254,0,0)        ( 79.062,  0.004,  0.000) (265.679,  0.000,  0.000)    70.2%    1.450    1.450
BT.2020 red (out of P3)          new  (  3.321, -0.249, -0.036) (  2.686,  0.000,  0.006) (255,3,18)       (214.844,  0.000,  0.450) (214.844,  0.000,  0.449)     0.0%    1.832    1.832
------------------------------------------------------------------------------------------------------------------------------------------------------------------------

===== 增益两端量程核对   SDR white=80nits   （「分母改后」==「InvOetf(base8)」才自洽；「期望 gain」应等于「实测 gain」）=====
patch                            分子改后(hdrNorm)     分母改后(SdrLinear)   InvOetf(base8_new)  期望gain  实测gain  分母改前(toneMap)   改前期望gain
----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
neutral gray 18%                 (  0.180,  0.180,  0.180) (  0.181,  0.181,  0.181) (  0.181,  0.181,  0.181) 0.994      0.995      (  0.181,  0.181,  0.181) 0.993   
SDR white                        (  1.000,  1.000,  1.000) (  0.783,  0.783,  0.783) (  0.784,  0.784,  0.784) 1.276      1.289      (  0.783,  0.783,  0.783) 1.278   
near black                       (  0.010,  0.012,  0.011) (  0.011,  0.012,  0.011) (  0.011,  0.012,  0.012) 0.972      0.954      (  0.010,  0.012,  0.011) 0.972   
BT.709 red @120nits              (  1.233,  0.050,  0.026) (  1.237,  0.051,  0.026) (  1.000,  0.051,  0.026) 1.233      1.254      (  1.505,  0.001,  0.000) 0.997   
BT.709 green @160nits            (  0.355,  1.934,  0.145) (  0.288,  1.342,  0.128) (  0.287,  1.000,  0.127) 1.934      1.930      (  0.044,  1.469,  0.020) 1.362   
BT.709 blue @200nits             (  0.000,  0.000,  2.277) ( -0.000,  0.001,  2.282) (  0.000,  0.001,  0.991) 2.298      2.282      ( -0.000,  0.001,  2.505) 0.998   
P3 red (out of 709)              (  1.999,  0.000,  0.000) (  2.004,  0.001,  0.000) (  1.000,  0.001,  0.000) 1.999      2.006      (  2.455, -0.083, -0.039) 0.998   
P3 green (out of 709)            (  0.000,  2.000,  0.000) (  0.044,  1.469,  0.020) (  0.045,  1.000,  0.020) 2.000      2.000      ( -0.318,  1.660, -0.115) 1.255   
bright HDR red @640nits          (  6.586,  0.314,  0.159) (  3.880,  0.271,  0.128) (  1.000,  0.270,  0.127) 6.586      6.586      (  4.645,  0.126,  0.035) 1.722   
BT.2020 red (out of P3)          (  2.686,  0.000,  0.006) (  2.692,  0.001,  0.006) (  1.000,  0.001,  0.006) 2.686      2.686      (  3.328, -0.249, -0.037) 0.998   
----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

!! old arm @ 320nits 编码失败：UhdrException: An application-supplied parameter is not valid. received bad value for hdr capacity max 1.000000, expects to be > hdr capacity min 1.000000
===== maxCLL=1000nits   SDR white=320nits   (scRGB 里 SDR 白 = 4.000)   patch=32px =====
patch                          arm  input scRGB(linear)     working(base gamut)   base8(8bit)     recon(nits)     ref(nits)      maxRelErr  chroma-ref chroma-rec
------------------------------------------------------------------------------------------------------------------------------------------------------------------------
neutral gray 18%                 old  该臂在此白电平下没有产出文件（编码失败）
neutral gray 18%                 new  (  0.180,  0.180,  0.180) (  0.180,  0.180,  0.180) (60,60,60)       ( 14.395, 14.443, 14.443) ( 14.404, 14.404, 14.404)     0.3%    0.000    0.001
------------------------------------------------------------------------------------------------------------------------------------------------------------------------
SDR white                        old  该臂在此白电平下没有产出文件（编码失败）
SDR white                        new  (  1.000,  1.000,  1.000) (  1.000,  1.000,  1.000) (137,137,137)    ( 80.234, 80.156, 80.156) ( 80.000, 80.000, 80.000)     0.3%    0.000    0.000
------------------------------------------------------------------------------------------------------------------------------------------------------------------------
near black                       old  该臂在此白电平下没有产出文件（编码失败）
near black                       new  (  0.010,  0.012,  0.011) (  0.010,  0.012,  0.011) (10,10,10)       (  0.925,  0.953,  0.980) (  0.828,  0.955,  0.884)     0.0%    0.030    0.009
------------------------------------------------------------------------------------------------------------------------------------------------------------------------
BT.709 red @120nits              old  该臂在此白电平下没有产出文件（编码失败）
BT.709 red @120nits              new  (  1.500,  0.000,  0.000) (  1.233,  0.050,  0.026) (151,30,19)      ( 98.047,  4.077,  2.042) ( 98.672,  3.984,  2.051)     2.3%    1.450    1.442
------------------------------------------------------------------------------------------------------------------------------------------------------------------------
BT.709 green @160nits            old  该臂在此白电平下没有产出文件（编码失败）
BT.709 green @160nits            new  (  0.000,  2.000,  0.000) (  0.355,  1.934,  0.145) (84,185,55)      ( 28.340,155.078, 11.934) ( 28.398,154.688, 11.582)     3.0%    0.253    0.254
------------------------------------------------------------------------------------------------------------------------------------------------------------------------
BT.709 blue @200nits             old  该臂在此白电平下没有产出文件（编码失败）
BT.709 blue @200nits             new  (  0.000,  0.000,  2.500) (  0.000,  0.000,  2.277) (0,1,198)        (  0.014,  0.000,181.094) (  0.000,  0.000,182.188)     0.6%    0.264    0.264
------------------------------------------------------------------------------------------------------------------------------------------------------------------------
P3 red (out of 709)              old  该臂在此白电平下没有产出文件（编码失败）
P3 red (out of 709)              new  (  2.450, -0.084, -0.039) (  1.999,  0.000,  0.000) (189,1,0)        (161.406,  0.000,  0.027) (159.922,  0.000,  0.002)     0.9%    1.841    1.840
------------------------------------------------------------------------------------------------------------------------------------------------------------------------
P3 green (out of 709)            old  该臂在此白电平下没有产出文件（编码失败）
P3 green (out of 709)            new  ( -0.450,  2.084, -0.157) (  0.000,  2.000,  0.000) (2,188,1)        (  0.071,160.000,  0.024) (  0.000,160.000,  0.001)     0.0%    0.341    0.341
------------------------------------------------------------------------------------------------------------------------------------------------------------------------
bright HDR red @640nits          old  该臂在此白电平下没有产出文件（编码失败）
bright HDR red @640nits          new  (  8.000,  0.050,  0.020) (  6.586,  0.314,  0.159) (255,79,56)      (526.875, 24.961, 12.842) (526.875, 25.117, 12.686)     1.2%    1.396    1.397
------------------------------------------------------------------------------------------------------------------------------------------------------------------------
BT.2020 red (out of P3)          old  该臂在此白电平下没有产出文件（编码失败）
BT.2020 red (out of P3)          new  (  3.321, -0.249, -0.036) (  2.686,  0.000,  0.006) (214,1,5)        (214.062,  0.000,  0.424) (214.844,  0.000,  0.449)     0.4%    1.832    1.832
------------------------------------------------------------------------------------------------------------------------------------------------------------------------

===== 增益两端量程核对   SDR white=320nits   （「分母改后」==「InvOetf(base8)」才自洽；「期望 gain」应等于「实测 gain」）=====
patch                            分子改后(hdrNorm)     分母改后(SdrLinear)   InvOetf(base8_new)  期望gain  实测gain  分母改前(toneMap)   改前期望gain
----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
neutral gray 18%                 (  0.045,  0.045,  0.045) (  0.045,  0.045,  0.045) (  0.045,  0.045,  0.045) 0.996      0.996      (  0.181,  0.181,  0.181) 0.993   
SDR white                        (  0.250,  0.250,  0.250) (  0.251,  0.251,  0.251) (  0.250,  0.250,  0.250) 0.999      1.002      (  1.003,  1.003,  1.003) 0.997   
near black                       (  0.003,  0.003,  0.003) (  0.003,  0.003,  0.003) (  0.003,  0.003,  0.003) 0.983      0.981      (  0.010,  0.012,  0.011) 0.972   
BT.709 red @120nits              (  0.308,  0.012,  0.006) (  0.309,  0.013,  0.007) (  0.309,  0.013,  0.007) 0.996      0.990      (  1.505,  0.001,  0.000) 0.997   
BT.709 green @160nits            (  0.089,  0.483,  0.036) (  0.089,  0.485,  0.037) (  0.089,  0.485,  0.038) 0.996      0.999      (  0.001,  2.005,  0.001) 0.998   
BT.709 blue @200nits             (  0.000,  0.000,  0.569) ( -0.000,  0.000,  0.571) (  0.000,  0.000,  0.565) 1.008      1.002      ( -0.000,  0.001,  2.505) 0.998   
P3 red (out of 709)              (  0.500,  0.000,  0.000) (  0.501,  0.000,  0.000) (  0.509,  0.000,  0.000) 0.982      0.991      (  2.455, -0.083, -0.039) 0.998   
P3 green (out of 709)            (  0.000,  0.500,  0.000) (  0.000,  0.501,  0.000) (  0.001,  0.503,  0.000) 0.994      0.994      ( -0.449,  2.089, -0.157) 0.998   
bright HDR red @640nits          (  1.646,  0.078,  0.040) (  1.649,  0.079,  0.040) (  1.000,  0.078,  0.040) 1.646      1.646      (  8.014,  0.052,  0.021) 0.998   
BT.2020 red (out of P3)          (  0.671,  0.000,  0.001) (  0.673,  0.000,  0.002) (  0.672,  0.000,  0.002) 0.998      0.995      (  3.328, -0.249, -0.037) 0.998   
----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

decoded raw geometry / sampling diagnostics:
  decoded _32bppRGBA8888 160x64 stride=160 gamut=BT709 transfer=Unspecified
  decoded _64bppRGBAHalfFloat 160x64 stride=160 gamut=BT709 transfer=Linear
  decoded _32bppRGBA8888 160x64 stride=160 gamut=DisplayP3 transfer=Unspecified
  decoded _64bppRGBAHalfFloat 160x64 stride=160 gamut=DisplayP3 transfer=Linear

assertions:
PASS  亮度系数取到的是 XYZ 的 Y 行  luma=(0.228975,0.691738,0.079287) sum=1.000000
PASS  解码取样自洽  最大块内偏差=0.0000  decoded _32bppRGBA8888 160x64 stride=160 gamut=BT709 transfer=Unspecified | decoded _64bppRGBAHalfFloat 160x64 stride=160 gamut=BT709 transfer=Linear | decoded _32bppRGBA8888 160x64 stride=160 gamut=DisplayP3 transfer=Unspecified | decoded _64bppRGBAHalfFloat 160x64 stride=160 gamut=DisplayP3 transfer=Linear
PASS  [80] 灰阶无偏色 neutral gray 18%  maxRelErr=0.88% ref=( 14.404, 14.404, 14.404) rec=( 14.424, 14.365, 14.277)
PASS  [80] 灰阶无偏色 SDR white  maxRelErr=0.98% ref=( 80.000, 80.000, 80.000) rec=( 80.781, 80.156, 80.625)
PASS  [80] 灰阶无偏色 near black  maxRelErr=0.00% ref=(  0.828,  0.955,  0.884) rec=(  0.803,  0.938,  0.870)
PASS  [80] 增益分母=文件里的 base（未截断像素）  参与比对 3 块，最大偏差=0.5/255（8bit 量化+JPEG 级别）
PASS  [80] 改前分母确实取错了节点  old 线性最大偏差=100.3% vs new 0.6%
PASS  [80] 实测 gain = 分子/文件里的 base  最大偏离=1.66%（BT.709 red @120nits），参与 9 块
PASS  [80] 域内色不被改动 BT.709 red @120nits  maxRelErr old=33.20% new=1.66%
PASS  [80] 域内色不被改动 BT.709 green @160nits  maxRelErr old=31.79% new=0.59%
PASS  [80] 域内色不被改动 BT.709 blue @200nits  maxRelErr old=60.61% new=0.69%
PASS  [80] 红色饱和度保留 P3 red (out of 709)  重建 chroma old=1.4500 new=1.8403（目标 1.8405）
PASS  [80] 红色饱和度保留 bright HDR red @640nits  重建 chroma old=1.2283 new=1.3960（目标 1.3955）
PASS  [80] 红色饱和度保留 BT.2020 red (out of P3)  重建 chroma old=1.4500 new=1.8318（目标 1.8318）
PASS  [80] 重建 chroma 达标 P3 red (out of 709)  new=1.8403 目标=1.8405
PASS  [80] 重建 chroma 达标 bright HDR red @640nits  new=1.3960 目标=1.3955
PASS  [80] 重建 chroma 达标 BT.2020 red (out of P3)  new=1.8318 目标=1.8318
PASS  [80] 绿/蓝不劣化 P3 green (out of 709)  maxRelErr old=39.60% new=0.00%
PASS  [80] 绿/蓝不劣化 BT.709 green @160nits  maxRelErr old=31.79% new=0.59%
PASS  [80] 绿/蓝不劣化 BT.709 blue @200nits  maxRelErr old=60.61% new=0.69%
PASS  [80] 高光未压暗  R rec=526.9nits ref=526.9nits
PASS  [80] 数值健康  minBoost=0.2628 capacityMax=6.50
PASS  [80] SDR base 合理  base(white)=(229,229,229) base(gray)=(118,118,118) 数值在[0,1]=True 中性=True 单调=True
PASS  [80] base 的 ICC 标签就是工作色域 P3（读文件字节）  new 文件字节(len=601,@1): 起点 1, class=mntr, pcs=XYZ , tagCount 9: wtpt=(0.3457,0.3585) rXYZ=(0.6820,0.3193) gXYZ=(0.2845,0.6746) bXYZ=(0.1559,0.0661) | 解码器(len=614,@14): 起点 14, class=mntr, pcs=XYZ , tagCount 9: wtpt=(0.3457,0.3585) rXYZ=(0.6820,0.3193) gXYZ=(0.2845,0.6746) bXYZ=(0.1559,0.0661) | 两处一致 | 与 P3(D50 适配后)最大偏差=0.0000 与 BT.709(D50 适配后)=0.0768 base[icc=yes subsampling=4:2:0(2x2,1x1,1x1)]
PASS  [80] 同一解析器读改前的 ICC = BT.709  old 文件字节(len=589,@1): 起点 1, class=mntr, pcs=XYZ , tagCount 9: wtpt=(0.3457,0.3585) rXYZ=(0.6485,0.3309) gXYZ=(0.3212,0.5978) bXYZ=(0.1559,0.0660) | 解码器(len=602,@14): 起点 14, class=mntr, pcs=XYZ , tagCount 9: wtpt=(0.3457,0.3585) rXYZ=(0.6485,0.3309) gXYZ=(0.3212,0.5978) bXYZ=(0.1559,0.0660) | 两处一致 | 与 P3(D50 适配后)最大偏差=0.0768 与 BT.709(D50 适配后)=0.0000
PASS  [80] gain map 无 ICC 且 4:4:4  icc=no subsampling=4:4:4(1x1,1x1,1x1)
PASS  [80] base 为 4:2:0（Ultra HDR 惯例，显式指定）  new base[icc=yes subsampling=4:2:0(2x2,1x1,1x1)]（改前用 WIC 默认参数，未显式指定）  old 文件字节(len=589,@1): 起点 1, class=mntr, pcs=XYZ , tagCount 9: wtpt=(0.3457,0.3585) rXYZ=(0.6485,0.3309) gXYZ=(0.3212,0.5978) bXYZ=(0.1559,0.0660) | 解码器(len=602,@14): 起点 14, class=mntr, pcs=XYZ , tagCount 9: wtpt=(0.3457,0.3585) rXYZ=(0.6485,0.3309) gXYZ=(0.3212,0.5978) bXYZ=(0.1559,0.0660) | 两处一致 | 与 P3(D50 适配后)最大偏差=0.0768 与 BT.709(D50 适配后)=0.0000
SKIP  [320] 依赖「改前」基线的对比项  改前实现在该白电平下无法编码出文件（见上方 !! 行），无基线可比
PASS  [320] 灰阶无偏色 neutral gray 18%  maxRelErr=0.27% ref=( 14.404, 14.404, 14.404) rec=( 14.395, 14.443, 14.443)
PASS  [320] 灰阶无偏色 SDR white  maxRelErr=0.29% ref=( 80.000, 80.000, 80.000) rec=( 80.234, 80.156, 80.156)
PASS  [320] 灰阶无偏色 near black  maxRelErr=0.00% ref=(  0.828,  0.955,  0.884) rec=(  0.925,  0.953,  0.980)
PASS  [320] 增益分母=文件里的 base（未截断像素）  参与比对 9 块，最大偏差=1.3/255（8bit 量化+JPEG 级别）
PASS  [320] 实测 gain = 分子/文件里的 base  最大偏离=0.93%（P3 red (out of 709)），参与 9 块
SKIP  [320] 域内色不被改动 BT.709 red @120nits  无 old 基线
SKIP  [320] 域内色不被改动 BT.709 green @160nits  无 old 基线
SKIP  [320] 域内色不被改动 BT.709 blue @200nits  无 old 基线
SKIP  [320] 红色饱和度保留 P3 red (out of 709)  无 old 基线
SKIP  [320] 红色饱和度保留 bright HDR red @640nits  无 old 基线
SKIP  [320] 红色饱和度保留 BT.2020 red (out of P3)  无 old 基线
PASS  [320] 重建 chroma 达标 P3 red (out of 709)  new=1.8399 目标=1.8405
PASS  [320] 重建 chroma 达标 bright HDR red @640nits  new=1.3966 目标=1.3955
PASS  [320] 重建 chroma 达标 BT.2020 red (out of P3)  new=1.8323 目标=1.8318
SKIP  [320] 绿/蓝不劣化 P3 green (out of 709)  无 old 基线
SKIP  [320] 绿/蓝不劣化 BT.709 green @160nits  无 old 基线
SKIP  [320] 绿/蓝不劣化 BT.709 blue @200nits  无 old 基线
PASS  [320] 高光未压暗  R rec=526.9nits ref=526.9nits
PASS  [320] 数值健康  minBoost=0.9809 capacityMax=1.64
PASS  [320] SDR base 合理  base(white)=(137,137,137) base(gray)=(60,60,60) 数值在[0,1]=True 中性=True 单调=True
PASS  [320] base 的 ICC 标签就是工作色域 P3（读文件字节）  new 文件字节(len=601,@1): 起点 1, class=mntr, pcs=XYZ , tagCount 9: wtpt=(0.3457,0.3585) rXYZ=(0.6820,0.3193) gXYZ=(0.2845,0.6746) bXYZ=(0.1559,0.0661) | 解码器(len=614,@14): 起点 14, class=mntr, pcs=XYZ , tagCount 9: wtpt=(0.3457,0.3585) rXYZ=(0.6820,0.3193) gXYZ=(0.2845,0.6746) bXYZ=(0.1559,0.0661) | 两处一致 | 与 P3(D50 适配后)最大偏差=0.0000 与 BT.709(D50 适配后)=0.0768 base[icc=yes subsampling=4:2:0(2x2,1x1,1x1)]
SKIP  [320] ICC 解析器自检（old=BT.709）  无 old 文件
PASS  [320] gain map 无 ICC 且 4:4:4  icc=no subsampling=4:4:4(1x1,1x1,1x1)
PASS  [320] base 为 4:2:0（Ultra HDR 惯例，显式指定）  new base[icc=yes subsampling=4:2:0(2x2,1x1,1x1)]（改前用 WIC 默认参数，未显式指定）  old 无文件

ALL ASSERTIONS PASSED
report -> D:\coding\Starshot\tools\UhdrColorTest\bin\Debug\net10.0-windows10.0.26100.0\win-x64\uhdr-color-report.txt
```
