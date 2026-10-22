# 选区编辑隔离与原位翻译按钮修复

## 原因

1. 区域截图的抠图和原位翻译调用全局 WorkspaceCaptureBroker，强制唤起主窗口并发送 `workspace.capture`。主界面收到事件后切换到图库，再打开图像工作区。这是错误的产品流程。
2. 图库左侧“原位翻译覆盖”图标只调用 setTool/setInspector，既不识别也不翻译。截图进入后的 initialTool=translate 也只选中工具，没有开始执行。

## 改动

- 区域动作打开专属原生 WebView2 编辑窗口 `?surface=capture`。不调用 EnsureMainWindow，不修改主界面页面或图库状态，不扫描图库，没有图片翻页或双图对比。
- 保留当前选区的多边形、套索、魔棒、智能抠图、Ctrl+Z/Y、文字覆盖和直接复制/保存 PNG。临时编辑目前延续原编辑器的 **SDR 透明 PNG** 输出，不宣称提供新增 HDR 蒙版编码能力。
- 每个窗口独立持有待编辑 WorkspaceDocument，转交给自己的消息桥一次；关闭前未被前端取走的图像也会释放。移除全局 broker 和主界面的捕获重定向订阅。
- 截图顶点和历史草稿保存在该窗口的内存中，不把临时选区像素写入图库，不产生截图草稿文件。图库已有图像的磁盘草稿行为保留。浏览器普通刷新可从同一个消息桥重新读取当前文档及草稿。
- 截图以原位翻译入口进入时自动执行 OCR → 翻译 → 可编辑位置覆盖。图库点击同名工具也直接执行这个流程。
- 进度区分识别与翻译，失败显示实际错误和重试；专属截图窗口可以直接打开翻译设置弹层。设置弹层中的 Esc 不会穿透关闭截图编辑窗口。
- 取消、切换文档与卸载使旧翻译 generation 失效；失败和取消不清空已有覆盖层。最初的空蒙版不再创建一个无效的异步更新任务，与自动翻译抢状态。
- 原生标题栏关闭请求由前端处理，未保存的编辑沿用确认和取消流程；确认关闭后释放原生窗口及其消息桥。

## 修改文件

- `src/Starshot/Features/ViewHost/MainWindow.cs`
- `src/Starshot/Features/Workspace/WorkspaceDocument.cs`
- `src/Starshot/Features/WebUI/WebUiBridge.Workspace.cs`
- `src/Starshot/Features/Screenshot/ScreenCaptureService.cs`
- `src/Starshot/Features/Screenshot/ScreenCaptureService.Lightweight.cs`
- `src/Starshot.WebUI/src/App.tsx`
- `src/Starshot.WebUI/src/main.tsx`
- `src/Starshot.WebUI/src/ImageWorkspace.tsx`、`ImageWorkspace.css`
- 新增 `src/Starshot.WebUI/src/CaptureEditorApp.tsx`、`CaptureEditor.css`
- `tools/WorkspaceUiTest/ui-check.mjs`、`tools/RegionToolbarUiTest/Program.cs`

截图服务仅改变这两个动作的编辑窗口入口；捕获、HDR 编码、tone mapping、OCR 推理和翻译后端保持原有实现。

## 验证

- 隐藏原生窗口 **14 项**真实所有权/消息桥/内存草稿/关闭检查通过：两个同时存在的窗口不抢占输入、一次转交、前端刷新重新取当前文档、草稿读写、关闭清理、未初始化前端时关闭清理。用 2×2 合成 CPU 图像，不启动 WebView、不截图、不运行模型。见 `native/xaml-interaction.log`。测试宿主原有的 3 项依赖/类型冲突警告仍存在，不影响运行。
- 软件渲染浏览器在 1280×900 浅色、960×640 深色及 200% 缩放下通过：图库图标直接翻译、失败可见与重试、已有覆盖保留；独立截图页面零图库读取、无翻页/对比、顶点撤销重做、直接保存、自动翻译只执行一次、取消后迟到结果不发布、设置 Esc 不穿透、原生关闭请求处理。**0 页面错误**。所有 OCR/翻译为合成结果替身，不调用模型或外部翻译 API。见 `web-ui.json`。
- 前端 **16 项**单元测试通过。
- 独立 Debug 构建成功，0 警告、0 错误，输出 `build/capture-editor/app`。前端 Vite 仍提示既有的大分块体积建议。
- 仅保留 1 张合成界面代表图 `capture-translation.png`。界面测试中的其他图片由独立 temp 目录 finally 清理。

未进行真实桌面截图、模型推理或实际翻译质量测试；没有覆盖正在运行的源码输出/安装目录，没有 Release 构建、推送或发布。用户重新运行源码启动脚本后加载新实现。
