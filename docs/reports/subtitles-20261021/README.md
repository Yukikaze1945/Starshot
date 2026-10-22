> 后续修正：独立选框已删除，字幕/译层已接回区域截图。当前实现与验证见 [区域工具修复](../region-tools-20261021/README.md)。以下保留初版测速记录。

# 实时字幕摘录：实现与验证

日期：2026-10-21。基于 PR #1 的功能意图重新接入当前源码，没有合并 PR、安装、推送或发布。所有构建为 Debug；所有识别素材均为合成中英双语字幕，没有真实桌面截图。

## 使用

运行仓库根目录 `Run-Starshot.cmd`。创作台截图方式下面的“实时字幕摘录”，或托盘同名菜单 → 选择字幕区域 → 拖动顶栏/边角把框放到字幕上 → 开始摘录。默认 350ms，可选 200/600ms。锁定后鼠标穿透；托盘开始/暂停/结束不自动激活控制面板。面板关闭后任务仍可在托盘控制。

结果是纯文案，不生成时间戳/SRT。显式复制、导出 TXT、整理文案均可使用。整理会暂停采集并打开独立 OCR 编辑浮窗中的快照，不跳图库，也不会被下一帧覆盖。停止后当前结果继续保留；新任务前确认，旧文案保存在应用数据目录 `SubtitleOCR/subtitle-<GUID>.txt`。

## 行为和生命周期

- 只用 GDI 读取有上限的物理像素 ROI；不新建 WGC、Win2D 或整屏冻结缓存。区域至少 80×16，最多 4,194,304 像素。
- 单 worker、单份当前帧；8×8 全像素亮度签名跳过未变化画面，每 2 秒重新确认。采样间隔扣除本轮处理耗时，不在 OCR 后额外累加一个完整间隔。
- 共用现有 Tiny/Small 引擎、GPU 选项和互斥门。后台采样遇到前台 OCR 占用立即返回，不排积压任务；首次调用才加载模型。
- 按字幕出现阶段去重。保留英文词间空格、处理中文/英文换行、滚动重叠与渐进追加，空白间隔达到 300ms 后允许重复台词。只见一次的短字幕在切换/停止时仍会提交。
- 暂停、改框、停止都有 generation 屏障；迟到结果不发布。停止先取消并等待 worker，退出再释放现有共享 OCR 引擎。同步原生推理不能中途强行 Dispose。
- 文案采用 UTF-8 临时文件+原子替换。写入失败保留内存结果，运行期间重试；停止后再次结束/新建前仍可重试。未保存成功不清空旧任务。20,000 段/100万字符上限，避免无限增长。
- Small 实测每次模型重新 SHA-256 校验约增加 80–100ms。只给新后台字幕路径复用已验证的文件指纹；文件变化仍重新检查，正式替换受 EngineGate 保护，模型 provider 打开文件时仍校验。普通 OCR 保持原先完整校验行为。

## CPU / GPU 测速

机器：Ryzen 7 5700X、RTX 5060 Ti，NVIDIA 驱动 32.0.16.1714；SimdPaddleOCR 2.0.0。固定线程配置为当前生产设置，CPU/GPU 独立测试进程，未修改用户 OCR 设置。表内是**完整后台 OCR 调用**，不是只计神经网络，不含桌面 GDI 抓取。每个尺寸两次热调用，仅为本机短测范围：

| 模型 | 字幕 ROI | CPU | GPU Vulkan |
|---|---|---:|---:|
| Tiny | 1280×140 | 34.0–50.6 ms | 11.6–19.2 ms |
| Tiny | 1920×180 | 64.8–66.0 ms | 14.0–15.0 ms |
| Tiny | 3840×360 | 59.1–67.1 ms | 26.5–33.0 ms |
| Small | 1280×140 | 76.1–86.4 ms | 11.5–13.0 ms |
| Small | 1920×180 | 126.4–145.4 ms | 14.1–17.3 ms |
| Small | 3840×360 | 128.7–139.1 ms | 15.4–17.8 ms |

GPU 每个尺寸的 DET/CLS/REC 均通过测试专用反射检查为 `Vulkan.GpuSession`，且 `GpuAlive=true`；检测设备为 RTX 5060 Ti，未把 Auto 请求当作后端证据。未发生 Windows OCR fallback。

- CPU 模型冷调用：Tiny 339ms（加载 99ms）、Small 401ms（加载178ms，运行时已由 Tiny 热身）。
- GPU 第一次测试中 Tiny 冷调用约 **17.3秒**；原始 `ocr-gpu.log` 保留了模型加载444ms、首次 Run 16871ms。后续新进程在驱动缓存已热的情况下 Tiny 约1007ms、Small独立首加载约1284ms。首次着色器/驱动准备开销不可忽略，界面已增加首次准备提示，不能用热态12–38ms掩盖此问题。没有清理用户驱动缓存来重复制造这个冷态。
- 同一 GPU 进程中 Tiny 后切 Small 模型冷调用约711ms，此值不能当作 Small 的完全冷启动。
- Small 独立 GPU 进程热后台调用18.6–37.9ms；前述表格中的 Small 使用同一测试进程切换模型后的结果。
- Tiny CPU 对350ms默认采样有充足余量；GPU热态更快，但会增加显存且首次准备更慢。字幕短于采样间隔、识别过慢或播放器画面被遮挡时，仍可能漏采，不承诺逐字幕无损提取。

### 内存（独立测试进程，不代表整个 Starshot 的总占用）

- CPU：Tiny测试结束 Private Memory约131.8MiB；切换Small测试结束约268.3MiB。切换模型会释放前一个引擎，不能把这两个数值差当作干净Small模型的独立增量。
- GPU：Tiny测试结束可查询的Dedicated Resident约187.2MiB；切换Small后约259.2MiB。退休模型后约50.1MiB。Small独立GPU进程约244.0MiB，退休后约34.9MiB。
- 退休后LoadedModel=null；Vulkan进程级设备/着色器缓存与CLR堆仍可能保留。正式功能停止采集不卸载共享OCR模型，沿用现有普通OCR生命周期。
- KMT对部分适配器查询失败，JSON保留failedQueries和成功查询数。GPU值来自成功查询的汇总，不能视为所有适配器完整总量。CPU进程没有GPU上下文（成功查询数0），表中的0不能解释成全面测得显存绝对为零。
- 无强制GC、没有新增全局大型数组池。短测不证明长期内存零增长。

## 自动验证

- `tools/SubtitleOcrTest`：80项通过；包括重复对白/渐进/双语换行/滚动重叠、保存失败重试、停止后重试、暂停恢复、旧generation丢弃、取消收尾、静态跳帧、五轮重复停止和五轮隐藏原生窗口。
- 隐藏HWND测试确认物理ROI、非激活/分层样式、锁定鼠标穿透标记、编辑中心HTTRANSPARENT、前台窗口不变、GDI资源释放。没有用实际鼠标点击播放器验证跨进程穿透。
- 软件浏览器：4个尺寸/深浅色组合（360×420至680×700），200%DPI；验证创作台入口、控制操作、结束后复制、整理/导出命令、错误恢复、新建确认、纯文本注入防护、按钮不越界。此前OCR格式浮窗4组回归通过；前端16项单测通过。
- 实际模型识别：CPU Tiny/Small、GPU Tiny/Small及Small独立GPU进程，全部合成中英字幕正确返回，无Windows fallback。每组3.7秒轮播为900/400/900ms三个字幕、400ms空白、第一句重复：均得到4段，400ms短字幕和重复对白均保留；19次采样仅5次OCR，14次因画面未变化跳过。
- Debug隔离应用构建0警告/0错误；详细原始数据见同目录 speed*.json、ocr*.log、ui.json；构建与回归日志在 `build/subtitles/`。

## 修改范围

新增 `Features/Subtitles/{SubtitleCollector,SubtitlePixels,SubtitleSession,SubtitleRegionWindow,SubtitleController}.cs`、`WebUiBridge.Subtitles.cs`、`SubtitleApp.tsx/.css`；接入 `MainWindow.cs`、`SystemTrayWindow.xaml/.cs`、`App.xaml.cs`、`WebUiBridge.cs`、前端 `App.tsx/main.tsx/styles.css`。`OcrHelper.cs`新增不排队的后台调用，`OcrModelStore.cs`增加仅后台使用的指纹复用参数。测试设施为 `tools/SubtitleOcrTest` 和 `tools/SubtitleOcrBenchmark`。截图捕获、HDR编码、区域截图交互未因本功能修改。

## 未验证与限制

未操作桌面鼠标键盘；真实播放器、独占全屏、DRM黑屏、HDR桌面上的GDI画面、多显示器拖动/DPI切换尚未做端到端验证。位置是屏幕固定ROI，不会自动追踪播放器；移动、遮挡、最小化会影响识别。文字编辑/导出按钮的RPC已浏览器验证，真实剪贴板和文件选择对话框未自动操作。GPU首次推理期间取消需等待同步原生调用收尾。

所有识别测试图片及临时模型导入目录已由finally清理。界面视觉检查的一张预览图位于 `C:/Users/18392/AppData/Local/Temp/starshot-subtitle-visual-20261021/panel.png`，自动审批两次拒绝删除（blocked by policy），作为唯一代表预览保留；没有测试图写入用户图片目录。
