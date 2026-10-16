# 2026-10-16：统一标题栏与修复轻量选区内容错位

只更新源码 Debug 输出；没有覆盖安装版、推送、发布、真实桌面截图或模型推理。

## 轻量模式：选框内的画面来自错误的行

合成 80×60 BGRA 图直接复现：要求绘制 `(17,11,36,28)`，旧 GDI 预览首像素
来自 `(17,21)`。最终 Crop 的行复制正确，预览的 `StretchDIBits` 局部源矩形
解释却不一致，导致选框内看到的内容与框外位置、导出的图片不一致。

此前窗口测试给所有选区加了标注，走了 `EnsurePreview / CopyFrom`，没有覆盖
无标注时直接绘制原始 DIB 局部矩形的路径。因此先前 146 项检查没有发现这个问题。

修复 `CpuCaptureGdi.Blit`：将源指针偏移到 `source.Y` 对应的首行，保留原始
整行 stride，将 DIB 视图高度设为 `source.Height`，始终从视图 y=0 读取完整视图。
这样实际 GDI 的拉伸/非拉伸分支都读取同一段行，不依赖局部 y 坐标解释。
同时校验源矩形和缓冲大小。没有新增复制数组、DIB、GPU 纹理或缓存。

单纯把 y 换成 `height-source.Bottom` 只能修复部分路径；扩展四角测试发现
左下角等尺寸拷贝仍错误，因此最终采用上述行视图方式，没有保留不完整的补丁。

新增检查覆盖：四角、非对称区域、完整图、单像素、奇数高度、1×/2×、
非零目标偏移、未标注的真实离屏选区，以及预览/标注/导出源像素一致性。
CPU/GDI 套件 **183 项通过**，全部图像在内存中。日志中的一次 DestroyWindow
警告来自已有的跨线程失败/原线程重试测试，不是资源泄漏。

证据：[修复前](lightweight-before.log)、[修复后](lightweight-after.log)。

## 标题栏融入内容

- 主 WebUI 与图像工作区：使用单层 48 DIP 顶部栏，不再保留上方独立系统标题行。
- OCR 小窗：32 DIP 暖白/深绿紧凑顶栏，与编辑器使用同一配色。
- Windows 继续绘制和管理最小化、最大化/还原、关闭；没有用网页按钮模拟窗口管理。
- 根据实际系统 caption inset / DPI 在网页右侧留空，避开工具、搜索和编辑按钮。
- WebView2 原生 `app-region: drag/no-drag` 支持标题空白拖动、双击还原及系统菜单；
  不引入逐帧 JS 拖动或定时重绘。
- 失焦、最大化、窗口/DPI 改变时同步状态。先前的延迟焦点和截图关闭保护保持不变。
- 批量转换页将原有标题并入 48 DIP 内容栏；更新和欢迎窗口使用统一标题组件，
  原生图片查看器同步按钮/标题颜色并移除重复系统图标。
- 已经没有系统标题栏的截图、贴图、录制浮层保持原样；轻量截图继续纯 CPU/GDI。

额外修复：OCR 选中文字的格式浮条原来用固定视口阈值定位；合并标题栏后会遮住
“原图断行”等按钮。现在在编辑区域上下边界内定位，原 OCR 交互测试已通过。

主要文件：

- `Frameworks/WindowChrome.cs`：原生标题栏、主题与工具窗口共用头部。
- `Features/ViewHost/MainWindow.cs`：主窗口/OCR 容器、原生 caption 信息、WebView 拖动支持。
- `Features/WebUI/WebUiBridge.cs`：bootstrap 标题信息、批量转换宿主。
- `Features/Screenshot/ImageBatchConvertWindow.xaml(.cs)`、`ImageViewWindow.xaml.cs`、
  `Features/Update/UpdateWindow.xaml.cs`、`Features/ViewHost/WelcomeWindow.xaml.cs`：各工具窗口集成。
- WebUI `WindowChrome.tsx/.css`、`App.tsx`、`OcrPopupApp.tsx`、`types.ts`、`RichEditor.tsx`：
  网页标题外观、窗口状态与浮条防遮挡。
- `Features/Screenshot/CpuCaptureGdi.cs`：轻量预览源行修复。
- `tools/WindowChromeUiTest`、`RegionToolbarUiTest`、`LightweightCaptureTest`：针对性回归。

## 验证

| 检查 | 结果 |
| --- | --- |
| 主应用 Debug x64 | 0 警告、0 错误，22.98 秒；[日志](debug-build.log) |
| 原生窗口标题栏 | 19 项通过；真实隐藏 HWND，校验客户区已延伸进原标题行、caption inset、主/OCR/工具窗口；[日志](native-chrome.log) |
| 网页标题栏 | 6 个布局通过；1280/960/760 主窗、460/360 OCR、1280 图像工作区，深浅主题、150%/200% DPR；[日志](web-chrome.log) |
| OCR 编辑器回归 | 4 个尺寸的排版、复制、翻译模拟回复、设置、浮条等通过；[日志](ocr-regression.log) |
| 轻量 CPU/GDI | 183 项通过；无桌面捕获、剪贴板操作或图片落盘 |
| 前端单元测试 | 16/16 通过；[日志](frontend-tests.log) |

原生测试宿主有既有的 3 项依赖/生成类型冲突警告；主应用没有警告。
Vite 仍提示大于 500 kB 的 chunk，没有为本轮标题调整额外引入 UI 库。

保留最多三张软件渲染样本：[主界面](main-1280.png)、[OCR](ocr-460.png)、
[图像工作区](image-1280.png)。这些样本只包含网页区域，右侧留空由实际 Windows
caption 按钮覆盖；它们不是桌面截图，也不能证明实际 Snap/拖动已验收。

仍未验证：可见窗口的真实鼠标拖动、最大化悬停 Snap、多显示器间拖动、
真实桌面选区和 Windows 10/其他显卡环境。构建及合成像素检查不能替代这些实测。

参考的是官方 API，具体实现独立编写：
[Windows 标题栏定制](https://learn.microsoft.com/en-us/windows/apps/develop/title-bar?tabs=wasdk)、
[WebView2 拖动区域](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2settings9?view=webview2-1.0.3537.50)。

## 后续：左下角快捷键提示避让

轻量窗口的常驻快捷键提示改在暗幕之后、选区恢复之前绘制，因而选区（包括拖拽和
窗口悬停预览）覆盖到的部分会恢复为干净原图，而非将提示叠在选区上。
鼠标距离提示矩形 24 DIP 内时整块隐藏，离开后按事件重绘恢复；不增加定时器。
标准/HDR 界面没有这个常驻左下角提示，仅有主动打开的 F1 帮助面板，保持其阅读交互。

新增 14 项 100%/200% 检查，验证靠近隐藏、离开恢复、部分/完整遮挡及最终导出
逐像素等于源图裁剪；CPU 套件共 **197 项通过**，不捕获桌面或落盘图像。
见 [提示避让检查](shortcut-hints-test.log)、[隔离 Debug 构建](shortcut-hints-build.log)。
当前源码程序正在运行，常用输出目录被锁定，因此本次改用 `build/shortcut-hints/app`
完成构建，没有终止用户进程。退出后重新运行 `Run-Starshot.cmd` 会将改动编译到常用源码输出。
