# 二维码识别 · 2026-10-19

已实现区域截图「更多 → 识别二维码」、截图库内置图像查看器左侧二维码按钮、剪贴板图片二维码按钮。

## 实现范围

- 使用 ZXing.Net **0.16.11** 的托管 CPU 解码器，Apache-2.0。构建输出的 `zxing.dll` 为 516,096 bytes；许可和来源随输出复制到 `ThirdParty/ZXing`。
- 对现有 SDR **premultiplied BGRA8** 直接构造亮度数据；支持 stride、透明背景合成白底、正常/反色、多码、Unicode/ECI、旋转；输入不会被修改。不加载 OCR/翻译模型，不写图片、不编码 PNG，不创建 D3D 设备。
- 最大 40,000,000 像素（包含 8K），超出时提示缩小区域，不暗中缩小二维码。临时亮度/二值化内存仅在当前任务中使用，没有静态图像缓存。
- HDR 区域使用已有 `CropDisplayToBgra` 产出的 SDR crop；图库使用既有 WorkspaceDocument.Bgra。没有修改 tone mapping、捕获生命周期或编码。
- 剪贴板直接解码原图为 BGRA8，尊重 EXIF 方向并转到 sRGB，避免使用缩略预览和额外 PNG 中转。
- 所有入口共用 CPU/GDI 紧凑结果窗口：原文可选择，支持多结果切换、复制内容、显式打开 HTTP(S) 链接、Esc 关闭。普通文本/Wi-Fi/其他协议仅显示与复制，不自动执行。二维码内容不写日志。
- 解码后台执行，关闭结果窗口取消并丢弃迟到结果。ZXing 单次解码调用没有可中断 API，取消在调用前后检查，不宣称能够立即中断该同步调用。
- 截图菜单版本 1 自动迁移，把新入口补入次选菜单；版本 2 中用户主动移除的入口不会再次添加。两种渲染器继续共享同一动作与布局定义。

## 文件

- `src/Starshot/Helpers/QrCodeHelper.cs`
- `src/Starshot/Features/Screenshot/QrCodeResultWindow.cs`
- `src/Starshot/Features/Screenshot/RegionToolbarModel.cs`
- `src/Starshot/Features/Screenshot/RegionToolbarPreferences.cs`
- `src/Starshot/Features/Screenshot/ScreenCaptureService.cs`
- `src/Starshot/Features/Screenshot/ScreenCaptureService.Lightweight.cs`
- `src/Starshot/Features/WebUI/WebUiBridge.cs`
- `src/Starshot/Features/WebUI/WebUiBridge.Workspace.cs`
- `src/Starshot/Starshot.csproj`
- `src/Starshot.WebUI/src/App.tsx`
- `src/Starshot.WebUI/src/ImageWorkspace.tsx`
- `third_party/zxing/LICENSE.txt`、`NOTICE.txt`
- `tools/QrCodeTest/*`、`tools/WorkspaceUiTest/ui-check.mjs`、`tools/RegionToolbarUiTest/Program.cs`

## 验证

- **68 项** CPU 解码与隐藏 Win32 结果窗口检查通过：URL、中英日混排、GB18030 ECI、Shift-JIS、旋转、反色、同时正常/反色多码、stride、透明背景、4K 小二维码、无二维码、输入不变、取消、尺寸上限、链接协议、旧配置迁移及主动隐藏、原生只读文字、96/192 DPI、Esc、GDI 资源释放。测试进程确认未加载 OCR 推理/D3D11 模块。数据见 `decoder-and-window.json`。
- 合成 3840×2160 输入包含 120×120 QR，最终运行解码 **91 ms**；普通 240×240 热调用约 **1 ms**。单次测量包含当前机器和 JIT/负载影响，不代表所有真实二维码耗时。
- 隐藏 XAML 工具栏 **31 项**检查通过，包括实际「更多」菜单中的二维码按钮可用，无捕获纹理、无模型、无桌面输入。见 `native-toolbar/xaml-interaction.log`。
- 布局测试 **1,971,318** 断言通过；轻量原生测试 **231 项**通过。轻量测试中的 destroy-failure 日志来自已有故障注入检查。
- 软件渲染浏览器测试：1280×900 浅色、960×640 深色、200% 缩放；图库与剪贴板二维码 RPC 入口可点击，既有查看器/编辑/分析界面回归通过，0 页面错误。见 `web-ui.json`。模型与 OCR/翻译接口为测试替身，没有推理。
- 前端 **16 项**单元测试通过。
- 独立 **Debug 构建成功，0 警告、0 错误**。见 `debug-build.log`。隐藏 XAML 测试宿主仍有其原有的 3 项依赖/类型冲突警告，运行通过；未更改生产依赖来掩盖测试宿主警告。

没有真实桌面截图、用户剪贴板改写、真实链接打开或模型测试。浏览器测试的临时界面图在独立 temp 目录中生成并由 finally 删除，没有保留图片。未测试严重模糊、残缺、强透视或复杂艺术二维码的识别率。

输出位于 `build/qr-code/app`。没有修改正在运行的源码输出目录或安装目录，没有 Release 构建、推送或发布。

## 来源

- https://www.nuget.org/packages/ZXing.Net/0.16.11
- https://github.com/micjahn/ZXing.Net
- https://github.com/micjahn/ZXing.Net/blob/master/Source/lib/BarcodeReaderGeneric.cs

