# 离线公式、版面与表格识别交付记录

日期：2026-10-20。仅源码 Debug 和隔离测试，未安装、未覆盖正在运行的应用、未提交或推送、未发布 Release。

## 已实现的入口与闭环

- 区域截图共享工具栏在 OCR 旁增加 **∑**，窄屏进入溢出菜单；“分析版面”进入更多菜单。两者均可在菜单编辑器调整。轻量模式继续使用原来的 CPU/GDI 工具栏渲染。
- 截图入口打开独立截图编辑窗口；公式使用约 680×460 DIP 紧凑窗口。图库入口留在当前图片内，不扫描图库或互相跳转。
- 公式原图、可编辑 LaTeX、本地 KaTeX 预览；复制 LaTeX、Markdown、MathML、Word 可编辑公式。无网络渲染，无 SimpleTeX 或 Index-Translate 调用。
- 版面入口自动检测并按阶段识别，显示分类框、阅读序号、进度和区域结果；可以编辑文本、公式或单元格并单独重试。结果框保持原图像素坐标，阅读顺序使用空间分割与分栏关系。
- 表格保留行列、单元格位置和跨度，支持 HTML、TSV、XLSX；XLSX 使用文本单元格，HTML 转义文字，合并格内容在 TSV 中只放左上位置。整页支持 Markdown。
- 模型库增加三个独立 DLC，支持下载进度、取消、目录导入、重新校验、删除。全部文件在暂存目录完成后发布版本目录和原子 active 指针，半套包不会标记安装完成。失败替换保留旧版本。
- HF 原始来源、HF 镜像及本地目录可用；公式另有官方 GitHub 来源。未找到可核对的魔搭同版包，因此未编造魔搭链接。

**实现闭环和上游一致性验证通过，但质量验收并非全部通过。** 本次指定的老版 RapidLaTeXOCR 在矩阵、多行公式及根指数上出现识别错误；SLANet_plus 在部分表格上误判行列。下文保留这些失败结果，不能把输出一致性当成识别准确率。

## 模型、固定版本、许可与哈希

完整机器可读清单：[models.json](../../../third_party/document-ocr/models.json)。来源与许可：[SOURCES.md](../../../third_party/document-ocr/SOURCES.md)。模型不是内置应用资源，安装目标是当前应用数据目录。开发验证权重只在忽略的 `build/document-ocr/models` 中。

| 包 | 固定来源 | 实际资源大小 | 许可 |
| --- | --- | ---: | --- |
| RapidLaTeXOCR | [RapidAI v0.0.0](https://github.com/RapidAI/RapidLaTeXOCR/releases/tag/v0.0.0) | 178,952,787 字节，170.66 MiB | RapidAI / pix2tex MIT 声明已保留 |
| PP-DocLayout-S | [社区 ONNX 7e851b9](https://huggingface.co/stefanj0/PP-DocLayout-S-ONNX/tree/7e851b9e5ad66613d7318a3213ab3336ebe0a514) | 4,917,852 字节，约 4.69 MiB | 上游 Paddle Apache-2.0 声明已保留 |
| SLANet_plus | [官方 ONNX 7dbe640](https://huggingface.co/PaddlePaddle/SLANet_plus_onnx/tree/7dbe640e127602bf506815e822c09758de73c482) | 模型 7,782,138 + 配置 2,035 = 7,784,173 字节 | Apache-2.0 |

| 文件 | SHA-256 |
| --- | --- |
| encoder.onnx | `01bf5dc25539ca0cd5b1bd29296ea495977a6ba5f629dc4178277809d26e5e7d` |
| decoder.onnx | `bd695497bf1b22279b7626f5916c79226e1e244c84355f8da7edfd2d921d0072` |
| image_resizer.onnx | `e0b075c39700f64d50400f39c8fc186bbb3b5d84d31864008313f376603aca9d` |
| tokenizer.json | `1dc27b18d6a518d0d5ff3f4bb7bd98521fe80ad39e5b2a246d4109f1bb9d5019` |
| layout.onnx | `33688dbee1c23e34b81777e97cb428eb40f24b242c02b5f623484959e830aec8` |
| table.onnx | `7790c0c13ce064782c9d22ebeb16b4da8216f83d3ba576da962c106ef58386da` |
| table.yml | `8a6372d3269a6f112fe13a2da7952a84da6e112c10a3146cbb43de5bd01d19fa` |

公式 HF 镜像固定 revision `0d0a23977b50433b07cdf95513ed7b7e45d3a761`，四文件逐一核对为官方权重相同字节。版面参考使用官方 Paddle revision `8ac289e66575bb9bba6e15c53719d8b15cc9b3b2`。

保留“不同哈希可以提醒后尝试使用”的约定：结构有效的不同哈希 ONNX 可安装并警告；缺少资源、非法文件、加载或接口不兼容会明确报错，不以普通 OCR 冒充公式或表格结构。新下载内容只有数据，不执行下载 DLL。

运行依赖：既有 ONNX Runtime 1.31.0 CPU、KaTeX 0.19.0、Open XML SDK 3.3.0。既有普通文字 OCR 的 SimdPaddleOCR 2.0.0 / Tiny 保持原逻辑。应用不依赖 Python、OpenCV 或外部执行器；隔离参考验证使用独立 Python 环境，未打包给用户。没有搬用 Evan 应用代码或分发 Office 转换文件。

## 推理、输入与生命周期

- 复用 WorkspaceDocument 已正确映射的 SDR BGRA 裁剪输入；验证 stride，透明像素合成白背景，不原地交换通道、不改 HDR 原图。没有为了识别把图片写到磁盘。
- 公式 C# 实现灰度、裁剪、补边、上游式 resize、学习式尺寸预测、编码、自回归贪心解码及 tokenizer。最多 512 token，EOS 只表示解码结束，不能保证公式正确。空白输入提示无公式，超限标明不完整。
- 版面使用 480×480 RGB 与原图 scale factor；表格使用 BGR、等比四舍五入缩放、归一化后补零至 488×488，结构由有界 XML 解析器生成，OCR 文本再关联到格子。
- 新增模型 CPU intra-op 2 / inter-op 1、单任务、按需首次加载；阶段切换时卸载之前模型家族，空闲 60 秒释放。普通 OCR 引擎维持既有策略；并未强行修改用户原有设置。
- 取消通过 ORT RunOptions.Terminate 传递；取消和状态命令不等待工作区长任务锁。每个桥实例仅发布当前 jobId 的结果，换图、关闭或重启会取消旧任务，旧任务无法替换新结果。
- 取消后不 Dispose 正在 Run 的会话；运行退出后释放输出、tensor 和临时图。完成任务的 TaskCompletionSource 与 async 状态分离，清空源图引用，避免已完成 Task 持有图像。
- KaTeX 本地代码/字体按需加载，trust=false、限制宏展开和尺寸，语法错误显示错误并保留源码。MathML 来自解析器，不用正则造 XML；Word 剪贴板使用注册格式 MathML / MathML Presentation。
- 生产代码没有 GC.Collect，也没有引入全局大型数组池或 GPU 常驻缓存。

## 修改范围

| 范围 | 实际文件 |
| --- | --- |
| 领域模型、推理、导出、任务 | `src/Starshot/Features/DocumentOcr/DocumentResults.cs`, `DocumentImages.cs`, `DocumentInference.cs`, `TableStructure.cs`, `DocumentExport.cs`, `PageRecognitionJob.cs` |
| DLC 与本地资源 | `Helpers/DocumentModelStore.cs`, 现有 `DlcModelStore.cs`, `DlcModelStore.Local.cs`, `ModelDownloadSources.cs`, `LocalModelFiles.cs` |
| MathML 剪贴板 | `Helpers/FormulaClipboard.cs` |
| 桥与窗口 | `Features/WebUI/WebUiBridge.DocumentOcr.cs`, `WebUiBridge.Workspace.cs`, `WebUiBridge.Models.cs`, `Features/ViewHost/MainWindow.cs` |
| 工具栏与动作路由 | `RegionToolbarModel.cs`, `RegionToolbarPreferences.cs`, `ScreenCaptureService.cs`, `ScreenCaptureService.Lightweight.cs`，仅增加动作/编辑窗口分发 |
| 前端 | `DocumentRecognition.tsx/.css`, `FormulaPopupApp.tsx`, `ImageWorkspace.tsx`, `ModelLibrary.tsx`, `main.tsx`, 包清单 |
| 部署与声明 | `Starshot.csproj`, `third_party/document-ocr/*` |
| 隔离测试 | `tools/DocumentOcrTest/*`，既有测试工程新增模型库编译链接及无推理的边界替身 |

不改 WGC 生命周期、截图显存策略、HDR/UHDR 编码、GDI 捕获或普通 OCR 推理实现。仓库原有未提交修改保留，不能把整个工作区 diff 视为这次新改动。

## 验证结果

- 应用和独立测试宿主 Debug/x64 构建均 **0 警告、0 错误**。前端构建通过；KaTeX 的延迟加载包会触发 Vite 大 chunk 提示，并不在首屏加载。
- [真实推理与生命周期](inference.json)：65 项检查通过，包含 stride/颜色/透明度、空白、阅读顺序、结构跨度、XLSX schema 与文本安全、XXE 拒绝、真实模型、缺包、取消、重复使用及释放。
- [上游一致性](reference.json)：6 个公式和上游确定性结果一致；2 个表格的完整结构（含行列和跨度）一致；社区版面 ONNX 与官方 Paddle 同一输入的 18 个框匹配，最小 IoU 大于 0.999996，最大置信度差小于 0.000001。
- 预处理不是逐比特完全相同：公式个别 resize 像素有 1 个灰度级差，表格独立 OpenCV 参考归一化最大差约 0.01751；本次最终 token/结构对照通过，不据此声称任意输入完全一致。公式参考显式使用正确 RGB→灰度，不复现上游文件读取的 RGB/BGR 颠倒问题。
- 下载测试使用真实下载器及受控 HTTP 响应，验证逐文件准备、总体状态、取消、失败保留、断网不安装、重启、目录导入、哈希提醒后使用和删除；实际官方资源另经下载/哈希核对。不是在用户应用目录实际安装三套 DLC。
- **真实 Word 后台文档**：粘贴得到 1 个原生 OMath，OMML 中存在 fraction 与 radical，不是图片或纯文本。测试只操作独立新建隐藏文档，退出并恢复原剪贴板。
- [界面检查](ui.json)：软件渲染的无头浏览器，200% DPI，1280×900 浅色及 680×460 深色；公式编辑、错误预览、Word/Markdown、表格编辑/导出、分类框、缩放、关闭取消、独立入口、原位图库入口均通过。
- [既有工作区界面回归](workspace-regression.json)通过。另完成 WorkspaceCore 42 项、模型库 27 项、工具栏 2,318,048 断言（126 个 DPI/显示器组合）及前端 16 项测试。
- 未执行真实桌面截图或鼠标键盘测试。合成图片和临时应用目录在 finally 删除，仅保留这份结果、日志及两张 UI 代表图。

## 识别质量：明确未通过的项目

| 合成样本 | 实际情况 |
| --- | --- |
| 分数 + 根号 | 正确识别 `\frac{x^{2}+1}{\sqrt{y}}` |
| 积分、上下标 | 本次积分样本正确 |
| 求和 | 主体识别，但多出标点 |
| 三次根 | 根指数 3 丢失，被识别成平方根 |
| 矩阵、多行公式 | 结果明显错误，部分 LaTeX 无效；与上游错误相同，预览报错，仍可编辑源码 |
| 有线表格 | 直接整图识别多出空白行，预测为 6×3；页面中裁剪后本次为 5×3 |
| 无线表格 | 预测 5×2，真实三列部分合并，需人工检查 |
| 双栏页面 | 顺序/自动正文及表格提取闭环通过，但有重叠或错误类别；该样本中的公式未被检测成公式，不能声称整页公式召回通过 |

结构置信度很高也可能预测错误；前端提醒核对，置信度不是准确率。当前编辑器支持单元格文字编辑，**不提供人工重建表格行列/跨度的复杂编辑器**。Markdown 的图片区域保留原图坐标注释，**未附带图片区域裁剪资源包**。

尚未验证真实 HDR 桌面输入、物理多显示器/DPI 鼠标交互、更多语言论文、复杂跨行跨列表格与所有复杂 Word 公式。既有 SDR 输入路径复用和静态代码核对不能代替这些验收。

## 使用与复现

退出旧的源码进程后运行仓库根目录 `Run-Starshot.cmd`。在模型管理库下载三个 DLC；也可为各包选择 `build/document-ocr/models` 导入已经验证过的开发缓存，避免重复下载。应用运行只查当前应用数据目录，不依赖开发缓存或 NuGet 缓存。

测试入口与说明：[tools/DocumentOcrTest/README.md](../../../tools/DocumentOcrTest/README.md)。隔离输出 `build/document-ocr/app` 内已确认资源清单和第三方许可落地。

以下性能数据来自本机合成图、CPU 模式及同一测试宿主，包含测试运行时/模型堆保留；不是安装版整体内存或手机性能承诺。

## 本次性能与内存数据

| 公式 | 耗时 ms |
| --- | ---: |
| fraction | 1739 |
| sum | 456 |
| integral | 358 |
| matrix | 755 |
| multiline | 821 |
| roots | 245 |

首次分数总耗时含模型加载；三组件日志 init 分别为 resizer 290 ms、encoder 604 ms、decoder 428 ms。不同公式输出长度不同，不能把复杂公式与简单公式作为等价基准。

- 1020×1034 整页：1031 ms；阶段 {"prepare": 3, "layout": 141, "text": 496, "formula": 0, "table": 389}。
- 4K 完整提取：2030 ms，18 个区域；阶段 {"prepare": 4, "layout": 302, "text": 1231, "formula": 0, "table": 491}。
- 4K 前/后 Private Memory：297.31 / 348.84 MiB；10 ms 采样峰值 Private 348.84 MiB，Working Set 342.08 MiB。这是进程总量的采样峰值，可能漏掉更短瞬时峰值，不是新增算法的独占内存。

| 重复轮次 | 总耗时 ms | Private MiB | managed MiB | 最近 GC 堆 MiB | committed MiB | Gen2 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1549 | 542.55 | 164.71 | 164.80 | 182.85 | 3 |
| 2 | 220 | 608.94 | 165.82 | 164.80 | 182.85 | 3 |
| 3 | 208 | 609.20 | 166.82 | 164.80 | 182.85 | 3 |
| 4 | 212 | 609.46 | 167.91 | 164.80 | 182.85 | 3 |
| 5 | 206 | 609.73 | 168.91 | 164.80 | 182.85 | 3 |

后四次热调用中位数 210 ms。第一轮在表格阶段之后切回公式家族，因此重新加载，不计作热调用。

跟踪源图缓冲区在全部任务收尾、卸载后，隔离诊断 GC 结果：0 个存活。测试初始/最终 Private Memory 为 12.83 / 211.61 MiB；中间含原图测试数据、普通 OCR、不同模型初始化、运行时及原生分配。末尾数字不等于单个引擎常驻占用。

GC HeapSize/TotalCommitted 来自最近一次 GC 的快照，不是实时堆量。短测中 Private Memory 没有每轮恢复，不能据此判定泄漏，也不能宣称长期零增长。正式代码保持 60 秒空闲卸载，未为降低数字强制 GC。

- 运行中取消 1：248 ms，状态 canceled。ORT 初始化阶段不能保证即时中断。
- 运行中取消 2：0 ms，状态 canceled。ORT 初始化阶段不能保证即时中断。
- 空闲释放分支通过注入过期时间戳验证，没有把正式 60 秒策略缩短，也未声称实际等待 60 秒的端到端测试。
- 日志：[inference.log](inference.log)；原始完整数据：[inference.json](inference.json)。
