# 2026-10-16：区域截图交接失败与关闭后闪退

本轮修复两个独立问题，只构建源码 Debug 版。没有覆盖安装版、启动主程序、
执行桌面截图/鼠标键盘自动化或运行任何模型，也没有推送或发布。

## 1. 截图已完成，打开编辑器时焦点调用失败

用户日志在 15:53:54.486 和 15:54:01.002 记录 `Region capture failed`，调用链为：

```text
CoreWebView2Controller.MoveFocus
MainWindow.Activate / ShowWorkspaceCapture
WorkspaceCaptureBroker.Publish
ScreenCaptureService.CaptureRegionInternal
```

此前 WGC fresh frame、覆盖层 initial Present/MoveOnscreen/Activate 与选区提交均已完成。
异常为 `System.ArgumentException`，发生在编辑器窗口交接阶段。原来 Show 期间同步触发
WM_SETFOCUS 时，控制器可能还未显示；Activate 又直接调用 MoveFocus，异常传播到截图流程。

修改 `Features/ViewHost/MainWindow.cs`，新增 `DeferredWebFocus.cs`：

- 焦点请求在 UI Dispatcher 低优先级队列中执行，不在 Show/WM_SETFOCUS 原生回调栈内执行。
- 请求合并；检查窗口可见、未最小化、当前前台窗口、控制器身份/可见性及有效边界。
- Hide、控制器关闭和 WebView 故障使旧请求失效；回调不能清除新请求。
- 可预期的参数/控制器关闭/COM 异常仅记录警告，不中断截图交付。
- 保留等待前端 ready 后发送 workspace.capture 的行为。

实现依据：[WebView2 线程模型与回调重入限制](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/threading-model)。

## 2. 真正的进程闪退：迟到的 PointerReleased 访问已清空的截图

Windows Application Error（事件 1000）记录：

- 时间：2026-10-16 15:56:23，本地时间。
- 应用：安装版 `2.6.0-preview.10`，PID 26952。
- 模块：Microsoft.UI.Xaml.dll 3.1.8.0。
- 异常：`0xC000027B`（stowed exception），模块偏移 `0x3A7515`。
- Report ID：`fc49a3c5-652c-47f8-86c1-09e5ae5e3ac0`。
- 原始转储：`%LOCALAPPDATA%/CrashDumps/Starshot.exe.26952.dmp`，58,078,060 bytes。

不能仅从 XAML 模块名认定框架故障。本轮解析转储的 ExceptionStream 和 stowed exception，
找到 UI 线程 0x78A4 上的 `0x80004003`，再从嵌套 LEO1 指针找到 ComWrappers CCW，
由 dotnet-dump 解出其真正持有的托管异常对象：

```text
System.NullReferenceException, HRESULT 80004003
RegionCaptureWindow.ComputePhysicalRect(Rect, Boolean)
RegionCaptureWindow.EnterSelectedState()
RegionCaptureWindow.Canvas_PointerReleased(Object, PointerRoutedEventArgs)
ABI.Microsoft.UI.Xaml.Input.PointerEventHandler.Do_Abi_Invoke(...)
```

详见 [转储中提取的实际异常及堆栈](crash-managed-exception.log)。没有把转储本体加入仓库。

崩溃前最后的日志与堆栈一致：

```text
15:56:20.727 pointer press: generation=43, state=Selecting
15:56:22.861 WGC background frame copies resumed after region overlay
15:56:22.912 pointer release: generation=44, state=Closed, drag=Creating
```

关闭会话已经把 `_canvasOriginal` 置空，但原 PointerReleased 没有关闭状态保护，
仍按 Creating 进入 EnterSelectedState，最终在 ComputePhysicalRect 读取
`_canvasOriginal.SizeInPixels` 时抛异常。异常跨 WinRT 事件边界后被 XAML 以 stowed
exception 终止进程，普通应用日志没有写出这个堆栈。

最小修复 `Features/Screenshot/RegionCaptureWindow.xaml.cs`：

- PointerMoved/PointerReleased 与 PointerPressed 一样，在 Completing/Closed 状态直接返回，
  不再查询事件点位、访问截图或重新进入 Selected。
- CloseWindow 首先发布 Closed / `_isClosed` 状态、使会话 generation 失效并清理拖拽和句柄状态。
- 在状态失效后释放鼠标捕获；预期 COM/关闭异常记录警告，继续原资源清理。
- 不修改 WGC context、HDR 编码、OCR、截图格式或现有重绘/资源释放架构。

## 验证与限制

| 检查 | 结果 |
| --- | --- |
| 源码 Debug x64 | 0 警告、0 错误，12.61 秒；[日志](debug-build.log) |
| WorkspaceCoreTest | 42 项通过，含 9 项焦点调度/失效/错误隔离检查；[日志](workspace-core.log) |
| 隐藏 XAML 窗口 | 9 项关闭流程检查通过；[日志](pointer-close.log) |
| WorkspacePreviewTest | 6 项 WIC 内存 PNG 往返检查通过；纯 CPU、0 文件输出 |
| 补丁检查 | git diff --check 通过 |

新增 `STARSHOT_POINTER_CLOSE_ONLY=1` 测试模式：复用 RegionToolbarUiTest 的真实 XAML 窗口，
不 Show/Activate、不创建捕获纹理，不执行原有 HDR 分析/4K benchmark。
检查 Completing/Closed 状态下迟到的 press/move/release，以及拖拽中取消后的事件。
测试宿主构建另有 3 项依赖版本/生成类型冲突警告；主应用构建没有这些警告。

已确认并修复转储指向的空引用路径；真实桌面捕获、前台焦点切换、长时间使用仍未实测，
不能用隐藏窗口测试宣称所有闪退均已消除。退出旧实例后，可使用仓库根目录
`Run-Starshot.cmd` 运行已经构建好的源码 Debug 版本（脚本会先进行增量构建）。

之前的工作区 RGBA/BGRA 颜色修复仍保留，见
[工作区报告](../image-workspace-20261015/README.md)。
