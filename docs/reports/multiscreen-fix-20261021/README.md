# 多屏区域截图错位修复

2026-10-21。只做源码 Debug 和隔离测试；没有启动区域截图、抓取真实桌面、操作鼠标键盘、加载模型、安装、推送或发布。当前用户运行的 `build/source-run` 进程未被关闭或覆盖。

## 根因与证据

实际显示布局（只查询系统元数据）：

| 屏幕 | 桌面物理位置 | 分辨率 | 缩放 |
| --- | --- | --- | --- |
| 主屏 | 0,0 | 3840×2160 | 150%，144 DPI |
| 左副屏 | -2560,720 | 2560×1440 | 100%，96 DPI |
| 右副屏 | 3840,1166 | 1920×1080 | 100%，96 DPI |

虚拟桌面边界为 `-2560,0,8320×2246`。用户日志也记录了三屏截图和这个窗口范围。

旧 `GetCaptureScale` 选择虚拟桌面左上附近的显示器 DPI，取得左副屏的 **96 DPI**；跨屏 WinUI 覆盖层仍有一个统一坐标空间，其实际窗口在主屏时为 **144 DPI**。因此 swap chain 按 100% 创建，XAML 按 150% 合成，既放大/截掉图像，也使鼠标 DIP 到源像素的映射错误。旧有测试手工设置统一缩放，没有覆盖“左副屏 DPI 与覆盖层 DPI 不同”的情况。

最终专项隐藏窗口测试真实查询到 `legacyTopLeftDpi=96`、`hwndDpi=144`、`xamlScale=1.5`、新方法 `actualScale=1.5`，记录于 [multiscreen.json](multiscreen.json)。新方法优先读取 XamlRoot.RasterizationScale，尚未附着时回退实际 HWND DPI。该测试未显示窗口，不能描述成已肉眼验收实际三屏覆盖层。

参考：[Win2D 的 DPI 与 DIP 规则](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/dpi-and-dips)。CanvasSwapChain 不会替应用自动决定显示 DPI，必须与 XAML 的坐标空间一致。

## 实际修改

- `RegionCaptureWindow.Viewport.cs`：新增统一视口同步。优先使用实际 XamlRoot 缩放，未附着时回退该 HWND DPI；监听 XamlRoot、composition scale 与 WM_DPICHANGED，按会话 generation 合并一次性更新。
- `RegionCaptureWindow.xaml.cs`：删除按左上副屏猜 DPI 的方法；首帧、移回屏内和绘制前同步视口；保持原始物理像素范围，并防止系统 DPI 建议尺寸拉伸冻结桌面。新同步异常转为截图失败并清理，避免从异步回调直接逃逸。
- 视口变化只调整 swap chain 的 DIP 尺寸/DPI，保持原图与物理缓冲区大小不变。选区、鼠标状态和悬停框重新换算；物理裁剪、分析缓存与捕获像素不重新缩放。
- `RegionCaptureWindow.Annotations.cs`：标注独立记录坐标缩放，保持已有笔迹、字体、线宽和位置的物理尺寸；撤销/重做中暂不可见的标注也同步。橡皮命中和马赛克/模糊取色使用同一换算。没有清空标注进度。
- 窗口悬停检测改为在后台收集物理矩形，再在 UI 线程按当前缩放发布；过期截图会话不能写回旧窗口框，避免 DPI 变化与后台枚举竞争。
- `RegionCaptureWindow.HdrPreview.cs`：复用现有 swap chain 时同时核对 DPI，防止只比较 DIP 大小而沿用错误 DPI。原生 FP16、SDR 辅助图和混合 HDR/SDR 白电平逻辑保留。
- `ScreenCaptureService.cs`：仅新增每屏原点、偏移、帧尺寸、DPI 和 HDR 标记日志，便于区分捕获帧问题与覆盖层问题；没有改多屏捕获或颜色合成算法。
- `tools/RegionToolbarUiTest/ToolbarTestApp.MultiMonitor.cs` 与测试入口：新增定向回归模式。

没有修改 WGC session/context 生命周期、编码、OCR、GDI 捕获或保存格式。保持事件驱动重绘，没有增加持续计时器。没有修改或覆盖之前其他功能的未提交工作。

## 验证

- 应用 Debug 构建通过，0 警告、0 错误。
- 三屏专项 **82 项通过**：模拟与用户相同的三屏错位布局；SDR/BGRA8 与原生 FP16 两条预览路径，100%、125%、150%、175%、200%、225% 及返回 100% 的变化。
- 验证所有三屏像素的位置、有效桌面间隙、物理缓冲区大小、鼠标对应的像素边界、跨屏选区裁剪不变；FP16 负通道和高光保留。
- 复制结果包含已有标注；DPI 切换后图像像素最多 1 个 8-bit 量化级差。测试了隐藏在重做栈内的标注、橡皮命中、撤销恢复以及绘制中途 DPI 改变后继续落笔。未因缩放变化丢失进度。
- 故意释放隔离测试的 swap chain，验证同步失败会结束本次截图并报告错误、清空源图和 swap chain 字段，不从 dispatcher 直接抛出；没有模拟真实显卡重置或更改系统设备。
- 原有实际 XAML/分析面板回归 **83 项通过**：隐藏窗口、合成纹理、零桌面截图、零桌面输入，结果在 [regression/xaml-interaction.log](regression/xaml-interaction.log)。
- 测试宿主保留 Windows App SDK 自动初始化类型重复的两条 CS0436 警告；应用自身构建没有这些警告。
- `git diff --check` 无新增格式错误；已有 WindowChrome.css 换行转换提示与本修复无关。

像素素材只在内存中创建，没有保存测试截图。未以短测宣称长期显存零增长。系统切换显示器排列/分辨率、真实 HDR 屏幕观感以及现场完整鼠标操作尚未做真实截图验收。

三块屏幕上下错开时，虚拟桌面矩形内有实际不存在的桌面区域。跨屏整图包含这些区域时保留黑色间隙是正确行为；修复不会把各屏强制拼成同高排列。正常覆盖层应让每块实体屏显示它原来的内容和位置。

退出旧源码进程后运行仓库根目录 `Run-Starshot.cmd`，重新构建并启动修复版。隔离构建和证据保存在 `build/multiscreen-fix` 与本目录，不改变安装版。
