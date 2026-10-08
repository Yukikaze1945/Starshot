# 区域截图工具栏重构验证

日期：2026-10-09。验证对象：当前仓库。仅本地实现及隔离构建；未推送、发布或覆盖安装目录。

## 实际效果

原固定 880 DIP、二十多个平铺按钮，改成常规宽度 **580 DIP** 的主工具栏：

选择 · 形状▾ · 箭头 · 画笔▾ · 文字▾ · 撤销 · 重做 · OCR · 贴图 · 更多 · 复制 · 保存 · 取消。

形状收纳矩形、椭圆和直线；画笔收纳画笔、荧光笔、马赛克、模糊和擦除；文字收纳文字和序号。分组主按钮执行上次选择的成员，展开箭头打开组菜单；成员选择在本次进程内跨截图、跨两种模式记忆。翻译、GIF、长截图、重新选区及完整快捷键说明放入更多菜单。

选中适用的标注工具才显示参数栏：五个明确的颜色色块，线宽 2 / 3 / 5 / 8 / 12 预设；文字及序号仅显示颜色。取消了盲选循环。面板采用中性浅色、细边框和圆角，#DDF369 强调当前入口的主要动作。复制、保存和 OCR 入口分别强调对应按钮，空历史的撤销/重做禁用。

坐标和尺寸保持独立，并修正标准/HDR 坐标标签缺少虚拟桌面偏移的问题。完整快捷键说明通过更多/F1 展开，不常驻屏幕。

## 共享规则与交互

- 动作、图标、分组、快捷键和布局定义由两种渲染器共用，移除了按钮数组下标分发。
- 以选区覆盖面积最大的显示器定位，使用其工作区和 DPI；优先在选区下方放置，空间不足移到上方，最终约束在该显示器内。
- 窄屏将低优先级操作收入更多，主操作和取消保持直接可达，按钮保持 36 DIP。菜单可分列、分页，通过方向键、Tab、Enter/空格或滚轮访问，不缩小点击区域。
- 菜单绘制在原选区窗口内。点击外部仅关闭菜单，消费相应的按下/释放及双击；不会继续拖动选区、绘制标注或完成截图。
- Esc 先关闭菜单/说明或丢弃文字编辑，再回到选区，最后退出截图；右键先关闭菜单。
- 保留原快捷键，并为标准/HDR 补齐 Ctrl+T、G、L 和 W/A/S/D 的操作。两种模式共享其定义。
- 轻量渲染器仍为纯 CPU/GDI；新增资源仅为可确定释放的小型 UI 字体，不依赖 WinUI、Win2D、D3D 或 GPU 纹理。

## 修改文件

| 文件（位于 src/Starshot/Features/Screenshot） | 用途 |
| --- | --- |
| RegionToolbarModel.cs | 共享语义动作、分组、状态、快捷键、参数预设及布局 |
| RegionToolbarMonitors.cs | 独立于图形框架的物理显示器坐标、工作区与 DPI 读取 |
| CpuRegionCaptureWindow.cs | GDI 窗口输入接入、移除平铺数组、统一快捷键与 Esc |
| CpuRegionCaptureWindow.Toolbar.cs | GDI 主栏、菜单、参数、说明、命中测试和字体释放 |
| RegionCaptureWindow.xaml | 替换固定长栏，增加窗口内的动态菜单及参数面板 |
| RegionCaptureWindow.xaml.cs | 接入共享定位、语义快捷键、菜单手势屏蔽及坐标修正 |
| RegionCaptureWindow.Annotations.cs | 工具选择/撤销历史同步 UI 状态，移除循环颜色和宽度按钮 |
| RegionCaptureWindow.Toolbar.cs | XAML 动态工具栏、分组、参数、可用状态及键盘导航 |

测试增加 `tools/RegionToolbarTest`、`tools/RegionToolbarUiTest`，扩展 `tools/LightweightCaptureTest` 的链接文件及隐藏窗口交互测试。捕获服务、MonitorCaptureContext、ImageSaver、HDR/P3/UHDR/AVIF、OCR 推理及既有截图对象释放逻辑没有修改。

## 验证结果

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| Debug 构建及真实 XAML 控件 | 0 警告、0 错误；26 项通过 | debug-ui-run.log、xaml-debug.log |
| Release 构建及真实 XAML 控件 | 0 警告、0 错误；26 项通过 | release-ui-run.log、xaml-release.log |
| GDI 图像/窗口/工具栏/所有权 | 107 项通过 | gdi-interaction.log |
| 共享布局与动作规则 | 1,065,831 项断言通过 | layout.log |
| 代码差异检查 | 无空白错误；仅工具栏、必要交互及测试文件 | git diff --check |

布局覆盖 100%、125%、150%、200%、300%、400% DPI，160～1920 DIP 宽度，360/720/1080 DIP 高度，四种入口、三种工具状态及三种选区位置，共 126 种基础显示环境、4,536 种布局情形；另有跨屏负坐标和不同 DPI 的对照。检查主栏/参数/菜单边界、菜单不盖住主栏或参数、按钮不缩小，以及全部动作仍可访问。

真实控件测试使用独立隐藏测试应用加载实际 Starshot 程序集及资源，覆盖实际颜色/线宽按钮调用、分组键盘切换、入口强调、撤销状态、菜单外按下与双击、文字编辑及 Esc 分层行为。它绕过普通应用启动，不注册单实例、托盘或快捷键。GDI 检查还覆盖全部工具映射、输出动作、参数进入实际标注、200% 说明行布局和 UI 字体清理。

GDI 日志中的一次 DestroyWindow 警告来自既有的故意跨线程销毁测试；随后创建线程重试成功、句柄归零，这是预期断言，非测试失败。

本轮实际桌面截图 **0 次**，桌面鼠标/键盘自动输入 **0 次**，截图文件 **0 个**。测试图像仅在内存中构造，未进入用户图片目录。没有重新测试或调整 HDR 编码、OCR 识别及 WGC 显存曲线。混合 DPI 和多显示器边界经过自动几何验证，尚未在多台实机上逐屏人工拖动核对。

## 参考与源码边界

研究 [Snow Shot](https://github.com/mg-chao/snow-apps/tree/9ca44a60f3a49c2c36071d1a06ef721883ba0259/snow_shot) 的描述符分组、主按钮/参数栏分离、选区锚定及 DPI 定位思路，独立编写 Starshot 的实现，未移植 GPL 源码。研究文件：

- [screenshottoolbarlayoutmodel.h](https://github.com/mg-chao/snow-apps/blob/9ca44a60f3a49c2c36071d1a06ef721883ba0259/snow_shot/include/snow_shot/presentation/screenshottoolbarlayoutmodel.h)
- [screenshottoolpalette.h](https://github.com/mg-chao/snow-apps/blob/9ca44a60f3a49c2c36071d1a06ef721883ba0259/snow_shot/include/snow_shot/presentation/screenshottoolpalette.h)
- [screenshottoolbarmainpanel.cpp](https://github.com/mg-chao/snow-apps/blob/9ca44a60f3a49c2c36071d1a06ef721883ba0259/snow_shot/src/presentation/tools/screenshottoolbarmainpanel.cpp)
- [screenshottoolbarpresenter.cpp](https://github.com/mg-chao/snow-apps/blob/9ca44a60f3a49c2c36071d1a06ef721883ba0259/snow_shot/src/presentation/toolbar/screenshottoolbarpresenter.cpp)
