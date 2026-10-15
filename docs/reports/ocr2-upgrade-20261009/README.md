# SimdPaddleOCR 2.0 与紧凑 OCR 窗口验证

2026-10-09，Windows x64 / .NET 10 / RTX 5060 Ti；仅隔离构建，未覆盖安装版或发布。

## 实现

- 核心 `Sdcb.SimdPaddleOCR` 固定为 2.0.0。Tiny/ModelProvider/CLS 官方模型包仍为 1.0.0；Small 已有数据模型与 SHA-256 不变。
- 设置 → OCR 与翻译增加持久化 GPU 加速识别，默认关闭。关闭时 DET/REC/CLS 都显式指定 Cpu，避免上游 Auto 默认探测 GPU。开启时均使用 Auto，允许上游按设备或 graph/session 选择 CPU。
- 切换与识别共用原有 EngineGate；等待正在运行的识别收尾后 Dispose，下一次调用才 lazy-load。一次只保留一个 OCR engine。
- Auto 的可捕获异常先用同一模型 CPU 重试；仍失败才遵循现有 Small→Tiny→Windows 异常回退。CPU 重试实例复用，不每次重新尝试 GPU。
- 上游 2.0 默认高优先级 Vulkan 队列在本机 vkCreateDevice 触发原生 0xC0000005。使用官方 SIMD_OCR_VK_PRIORITY=off 普通队列后成功；仅初始化期间设置并 finally 恢复环境变量。原生进程崩溃不能靠 managed catch 恢复，不能据此保证所有驱动安全。
- OCR 窗口默认由 720×560 缩为 600×430 DIP（面积减少约 36%），最小 480×340；8px 外留白、1px 边框、无装饰侧栏。文字格式选择、保留格式翻译、原图断行、复制及自动复制继续保留。
- OCR 编辑器直接随入口加载，避免 lazy waterfall；主 App、设置面板按需加载。设置为居中的可滚动小面板。浏览器初始化前用现有 HWND/GDI 先显示识别文本；准备完成后解除临时文本引用并交给编辑器。未增加后台常驻 WebView/controller。
- BGRA/stride、SDR crop、HDR SDR preprocessing、捕获/WGC/HDR 编码未改变。

## 识别实测

| 用例 | 时间 |
|---|---:|
| Tiny CPU 首次中文（含模型 init 123ms） | 475ms |
| Tiny CPU 英文热调用 | 128ms |
| Tiny CPU 游戏小字 | 123ms |
| Tiny CPU 4K 小字 | 372ms |
| Tiny CPU HDR→SDR 合成 crop | 60ms |
| Tiny Auto 本轮冷调用（已有驱动缓存） | 768ms |
| Tiny Auto 英文热调用 | 20ms |
| 切回 CPU 首次 | 181ms |
| Small CPU 首次中英日文（含 init 262ms） | 932ms |
| Small CPU 热调用 | 398 / 340ms |

本机首次成功 Vulkan 识别曾耗时 **16,505ms**，包含首次 shader/pipeline 准备，不能用已有缓存的 768ms 代替首次用户体验。GPU 因设备/驱动不同可能更慢，默认保留 CPU。

Tiny 8 次合成升级验证、CPU 重试故障注入、Small 8 次回归及发布程序集 HDR→SDR OCR 均通过；BGRA 输入 hash 未变、行框有效，正常路径未触发 Windows fallback。Small 校验、原子安装、取消、断网/损坏、切换并发与重启保持通过；Tiny 对日文不作质量保证，Small 合成日文识别通过。

## 内存边界

Tiny 合成测试宿主 Private Memory：基线约 222MiB，CPU 中文后 334MiB、英文后 357MiB；4K/HDR fixtures 后 515MiB；Auto 两次 659/663MiB，切回 CPU 后 765MiB。该宿主持有多组测试输入、Win2D fixture 和 CLR 已分配堆，不能解释为纯引擎内存或据此判定泄漏。没有强制 GC。

Small 相同输入首次/两次热调用 Private Memory 为 394,440,704 / 394,731,520 / 394,874,880 bytes；热调用没有重新解析模型。短时少量测试不能证明长期零增长。

2.0 上游进程级 Vulkan device/pipeline 缓存可能保留到进程退出；关闭加速/Dispose engine 不等于全部 driver/CLR 内存立即归还。本轮不修改第三方缓存架构，也不新增截图 GPU 缓存。

## 界面与发布

- 584×391 / 464×301 DIP、200% 缩放、明暗主题：无横向溢出；排版、复制、翻译格式、自动复制、GPU 设置 RPC、提前 OCR 结果事件和 DLC 操作通过。
- 交替三次新浏览器上下文，当前编辑器首显中位约 180ms，旧 preview.9 195ms；首次 JS 641,150 vs 693,820 bytes。差异很小，不能宣称显著冷启动提速。
- 真实隐藏 HWND/WebView2：首轮/次轮完整编辑器结果 1644/2680ms；GDI 文本绘制 41.94/1.75ms（首轮含字体冷启动）。隐藏 WebView 的调度不等同前台用户体验；真实可见窗口冷启动仍需用户环境反馈。
- 关闭后临时 loading text 引用已解除，窗口/controller 正常 Dispose；未恢复长驻浏览器窗口。
- 前端 10 项测试、Debug/Release 构建、自包含 trimmed/ReadyToRun publish 通过。Vite 对约 641KB 首屏 chunk 有体积建议警告；保留完整编辑器功能，未为拆包再次引入启动 waterfall。
- 发布包从自身目录加载核心 2.0.0 和模型资源，HDR→SDR crop 识别通过；uhdr.dll SHA-256 与固定 v2.0.2 资产一致。
- 所有 OCR 输入为内存合成；未进行真实桌面截图/鼠标键盘操作。浏览器 UI 样本写独立 temp 并 finally 删除，没有保留图片。原生 WebView 测试缓存位于隔离 build 目录。

## 实际文件

生产：Starshot.csproj、OcrHelper.cs、AppConfig.Setting.cs、WebUiBridge.cs、MainWindow.cs；WebUI 的 main.tsx、OcrPopupApp.tsx、OcrLab.tsx、OcrPopup.css、SettingsPanel.tsx、types.ts、bridge.ts；third_party/simdpaddleocr/README.md。

验证：OcrTinyTest / OcrSmallTest 核心引用与配置 stubs、Tiny 升级/重试测试；新增 OcrPopupUiTest 与 OcrPopupNativeTest。已有 HDR 分析修复未纳入本轮改动。

官方说明：https://github.com/sdcb/SimdPaddleOCR
