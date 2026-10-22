# OCR 多窗口与区域截图画面样式

日期：2026-10-21。仅 Debug／源码构建和隔离验证；未安装、推送或发布。

## 行为

- 每次 OCR 创建独立编辑窗口；之前的窗口、编辑草稿和译文不会被后续识别覆盖。窗口初始位置依次错开 24 DIP，最多循环六个偏移位置。关闭或识别失败只释放对应窗口。
- 在区域截图坐标／尺寸条右侧新增“画面样式”。提供圆角预设与微调、四档阴影、边框宽度与三种颜色、重置。
- 默认关闭效果；本次选区调整期间保留参数，新截图会话恢复无效果。
- 复制、保存、快速保存和贴图应用样式。OCR、公式、版面、二维码、原位翻译、GIF 与长截图仍使用原始选区，避免装饰干扰识别或拼接。
- 圆角产生透明边缘，阴影自动增加透明边距。PNG 支持透明；JPEG、视频等没有透明通道。剪贴板同时提供兼容 CF_DIB 和显式 sRGB／alpha 的 CF_DIBV5，接收软件是否保留透明由其支持情况决定。

## 实现范围

- `OcrResultWindow.cs`：将单例准备／交付改为每次请求独立的 Preparation；未交付的实例自动关闭，已交付的窗口由原生 HWND 注册表持有，直到用户关闭。
- `MainWindow.cs`：OCR 新窗口的位置错开。保留原有启动并行、就绪交接和异常尺寸恢复。
- `ScreenCaptureService.cs`、`ScreenCaptureService.Lightweight.cs`、`PinnedCaptureWindow.Image.cs`、`ImageViewWindow.xaml.cs`、`WebUiBridge.cs`：识别结果交给各自准备好的窗口；贴图位置扣除阴影边距。
- `RegionFrameStyle.cs`：两种截图模式共享参数、动作范围、预设与屏幕边界布局。
- `CpuCaptureFrameStyle.cs`、`CpuRegionCaptureWindow.FrameStyle.cs`、`CpuCaptureGdi.cs`、`CpuRegionCaptureWindow.cs`、`CpuRegionCaptureWindow.Toolbar.cs`：纯 CPU/GDI 透明合成、样式面板、交互和预览缓存。拖动期间只更新圆角裁剪，松手后再计算阴影。
- `RegionFrameRenderer.cs`、`RegionCaptureWindow.FrameStyle.cs`、`RegionCaptureWindow.xaml`、`RegionCaptureWindow.xaml.cs`、`RegionCaptureWindow.Toolbar.cs`：Win2D 同格式合成、HDR 预览及浮动控件。保存 HDR 时在原始浮点裁剪与标注合成后应用几何效果，不提前转为 SDR。临时绘图资源均局部释放。
- `ClipboardHelper.cs`：透明截图增加 BITMAPV5 数据格式。
- 测试：`LightweightCaptureTest/FrameStyleChecks.cs`、`RegionToolbarUiTest/ToolbarTestApp.FrameStyle.cs`、`ToolbarTestApp.OcrStartup.cs` 及其测试入口／项目配置。

没有调整 WGC、色调映射、图片编码器实现或 OCR 推理模型。

## 验证结果

| 检查 | 结果 |
|---|---|
| 应用 Debug 完整重建与最终增量构建 | 通过，0 警告、0 错误 |
| 纯 CPU 测试 | 41,939 项通过，包括逐像素 premultiplied alpha、源像素不变、阴影边距、负屏幕坐标、混合 DPI、Esc 与动作范围 |
| CPU 合成 4K 样式 | 最终一次 78.07ms，3840×2160 → 3952×2272；单次 Debug 测量，不代表真实截图总耗时 |
| 原生样式测试 | 15 项通过；真实 WinUI 控件、200% DPI、PNG 编解码 alpha、HDR FP16 格式／10.0 高光／负蓝通道、复制交付 |
| 剪贴板格式 | 直接检查分配的 BITMAPV5 内存，确认 alpha 掩码、sRGB、bottom-up 行序；不改用户剪贴板 |
| 原有工具栏交互 | 31 项通过：菜单、Esc 层级、参数、快捷键说明、菜单编辑入口 |
| OCR 多窗口及启动 | 23 项通过；两个实际 WebView2 编辑器保留独立文档，第二份结果不覆盖第一份已修改草稿；请求所有权与关闭后迟到回调也通过 |
| OCR 时延记录 | 三次独立浏览器启动至文档就绪约 3317／1017／1096ms；已准备窗口收到结果至提交约 10.44ms。这是隔离启动测量，非模型识别耗时 |
| 合成界面目视检查 | 已检查 GDI 渲染结果：尺寸条、样式弹层、圆角阴影和工具栏无相互遮挡 |

测试宿主有两项既有 WinAppSDK 自动初始化类型冲突警告；构建成功。CPU 测试包含刻意验证跨线程 DestroyWindow 失败恢复的诊断输出，最终检查通过。

本次发现并修复新增内联 XAML 结构的连接目标类型错误：将尺寸行明确命名、拆开子控件后，实际加载和按钮测试通过。测试宿主同时确保使用与应用 DLL 匹配的 PRI。

原始结果见同目录 `cpu-tests.log`、`frame-gpu.json`、`native-startup-multi-window.json`、`xaml-interaction.log`。临时合成图片及隔离浏览器配置由测试 finally 自动清理。

## 验证边界

没有执行真实桌面截图、桌面鼠标键盘操作、模型推理或正式安装。真实显示器上的鼠标拖动流畅度、HDR 目视表现及第三方软件粘贴透明度未实测；多窗口通常会增加各自 WebView2 控制器的内存，活动浏览器环境会共享。短测不能证明长期内存零增长。
