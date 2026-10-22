# 字幕摘录范围标记与窗口置顶修复

2026-10-21，Debug/源码隔离构建；未安装、推送或发布。未操作桌面输入、截取桌面或加载识别模型。

## 原因与行为

原区域截图覆盖层在确认后被正常释放，但没有接续的持久 ROI 标记。结果面板也没有设置原生置顶状态或提供开关。

- 新增 SubtitleRegionOutline：四条 2 物理像素宽的 GDI 边缘窗口，全在 OCR 矩形外侧。采用 layered/transparent/tool/topmost/no-activate 样式及捕获排除，不绘制或缓存整个区域，不恢复旧的独立选区编辑窗口。
- 确认选区立即显示标记；暂停保留，重新框选前销毁；取消框选恢复旧范围并暂停，停止、关闭、退出时立即销毁。停止等待推理结束前清理一次，取得串行操作锁后再次清理，防止等待期间建立的标记残留。退出后迟到的选区不能重启。
- 字幕窗口默认置顶。右上角图钉通过 subtitles.topmost 设置 OverlappedPresenter.IsAlwaysOnTop，状态通过 subtitles.status 返回；切换不重新激活窗口。偏好仅在本次应用进程中保留。
- 没有修改捕获、OCR 推理、HDR、模型生命周期或字幕保存算法。

## 验证证据

- SubtitleOcrTest：54 项通过，使用合成边界；包括标记显示、重选替换、取消恢复、停止/关闭/退出清理、退出后拒绝迟到选区，以及置顶状态返回。该层标记为替身，真实 Win32 行为另测。
- LightweightCaptureTest：381 项通过，其中 127 项新增真实隐藏 Win32 标记检查：负坐标、边缘不与 ROI 相交、全部样式、鼠标 hit-test、捕获排除、无激活、五轮创建/销毁后无 GDI/USER 句柄累积。既有跨线程 DestroyWindow 错误注入仍产生一次预期警告，恢复断言通过。
- RegionToolbarUiTest：10 项真实隐藏窗口检查通过，置顶默认值、true/false/true 原生 presenter 状态一致、切换不显示隐藏窗口。见 subtitle-pin.log。未启动 WebView2 或模型。
- 字幕 WebUI：420×400 深浅色、360×360、680×700，共四组，200% DPI；默认图钉开启、取消/恢复置顶、重选、暂停继续、停止、复制、编辑、导出、关闭和 Esc 通过，无横向溢出。见 subtitle-ui.json。可视检查截图在独立 temp 中由 finally 清理。
- 应用 Debug 隔离构建：0 警告、0 错误。测试宿主有两条既有 CS0436 SDK 初始化类型冲突警告，0 错误。前端 TypeScript/Vite 构建通过，保留既有大 chunk 提示。

## 文件

生产：SubtitleRegionOutline.cs、SubtitleController.cs、MainWindow.cs、WebUiBridge.Subtitles.cs、SubtitleApp.tsx、SubtitleApp.css。

测试：SubtitleOcrTest/WorkflowChecks.cs、ui-check.mjs；LightweightCaptureTest/SubtitleOutlineChecks.cs、Program.cs、csproj；RegionToolbarUiTest/ToolbarTestApp.SubtitlePin.cs、Program.cs。

## 边界

验证使用隐藏原生窗口、合成输入和软件浏览器，未对真实视频播放器进行端到端字幕采集。全屏独占播放器可能覆盖普通 topmost 窗口；此修复不宣称绕过系统或播放器的独占显示行为。标记只标识固定屏幕坐标，播放器移动后仍需重新框选。

源码启动：D:/coding/Starshot/Run-Starshot.cmd；本次应用隔离产物位于 build/subtitles-region/app。
