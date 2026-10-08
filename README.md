<div align="center">

<img src="docs/assets/readme/hero.svg" width="100%" alt="Starshot — Capture the light. Keep the detail. 深色与荧光黄绿的产品横幅">

<br><br>

**让截图留住高光，让文字继续表达。**

Windows 原生捕获 × HDR 色彩 × 文字识别与翻译<br>
一个截图工具，也是一块轻巧的桌面创作台。

<p>
<a href="https://github.com/Yukikaze1945/Starshot/releases"><img src="https://img.shields.io/github/v/release/Yukikaze1945/Starshot?include_prereleases&amp;style=for-the-badge&amp;label=RELEASE&amp;labelColor=182019&amp;color=DDF369" alt="最新公开版本，包含预览版"></a>
<a href="https://github.com/Yukikaze1945/Starshot/releases"><img src="https://img.shields.io/github/downloads/Yukikaze1945/Starshot/total?style=for-the-badge&amp;label=DOWNLOADS&amp;labelColor=182019&amp;color=DDF369" alt="GitHub Release 资源累计下载次数"></a>
<a href="https://github.com/Yukikaze1945/Starshot/stargazers"><img src="https://img.shields.io/github/stars/Yukikaze1945/Starshot?style=for-the-badge&amp;labelColor=182019&amp;color=DDF369" alt="GitHub Stars"></a>
<a href="LICENSE"><img src="https://img.shields.io/badge/SOURCE-MIT-DDF369?style=for-the-badge&amp;labelColor=182019" alt="项目源码 MIT 许可"></a>
</p>

<p>
<a href="https://github.com/Yukikaze1945/Starshot/releases/download/2.6.0-preview.8/Starshot-2.6.0-preview.8-setup-x64.exe"><img src="docs/assets/readme/download-installer.svg" width="290" alt="下载 Windows x64 离线安装包"></a> &nbsp; <a href="https://github.com/Yukikaze1945/Starshot/releases/download/2.6.0-preview.8/Starshot-2.6.0-preview.8-win-x64.zip"><img src="docs/assets/readme/download-portable.svg" width="290" alt="下载 Windows x64 便携版"></a>
</p>

<sub>直达公开版本 2.6.0-preview.8 · Windows x64 · 安装包 / ZIP / SHA-256</sub>

<br><br>

[下载与安装](#下载与安装) · [四档捕获](#四档捕获) · [功能巡礼](#功能巡礼) · [快速上手](#快速上手) · [开发与贡献](#开发与贡献)

**简体中文** · [English](README.en.md)

</div>

<br>

## 截图，不止按下快门

游戏里的火焰、电影里的霓虹、桌面上的一段文字——值得被保留下来的，既有画面，也有信息。

Starshot 保留 C# 原生截图能力，用 React / WebView2 呈现新的工作空间。从低负担的日常截取，到保留原始 HDR 信号，再到 OCR 排版与大模型翻译，按你的场景选择。

<table>
<tr>
<td width="50%" valign="top">

### 01 / 留住光

**HDR 捕获与色彩输出**

FP16 scRGB 原始捕获，支持 HDR AVIF、JPEG XL、PNGv3 与 Ultra HDR JPEG。普通 SDR 输出使用 StarshotPerceptual 色调映射；HDR 保存与 SDR 预览各走对应路径。

</td>
<td width="50%" valign="top">

### 02 / 收好画面

**区域选取与桌面工具**

窗口检测、像素放大镜、紧凑分组工具栏、按需标注参数。形状、箭头、画笔、文字、撤销 / 重做，以及贴图、长截图、GIF 录制，都从选区继续。

</td>
</tr>
<tr>
<td width="50%" valign="top">

### 03 / 读懂文字

**OCR → 排版 → 翻译**

内置 PP-OCRv6 Tiny，可选 Small 模型 DLC。识别后打开独立文字窗口，先编辑再复制；可连接兼容 OpenAI 接口的大模型，自动发现模型列表，翻译并保留排版结构。

</td>
<td width="50%" valign="top">

### 04 / 看见亮度

**HDR 亮度分析仪**

从原始 HDR 选区查看 Cursor、Min、Max、Avg、P99，展开 Waveform，叠加可调透明度的 Heatmap。热力图只用于观察，不写进保存图片。

</td>
</tr>
</table>

<br>

## 四档捕获

<img src="docs/assets/readme/modes.svg" width="100%" alt="轻量、标准、高质量 HDR、单帧 HDR 视频四档捕获模式">

| 模式 | 捕获与输出 | 适合什么 |
| :--- | :--- | :--- |
| **轻量** | CPU / GDI 捕获与选区，原生 SDR | 桌面、文档、日常截图，优先减少图形资源负担 |
| **标准** | WGC 捕获，SDR 输出 | 通用窗口与屏幕截取 |
| **高质量 HDR** | HDR 屏幕使用 FP16 scRGB；支持 HDR 图片与 SDR 映射 | 游戏、影视、高光与广色域内容 |
| **单帧 HDR 视频** | HEVC / AV1 · MP4；一个视频帧停留 3 秒，无音轨，另存同画面图片 | 尝试通过手机的视频 HDR 解码路径分享截图 |

在 **设置 → 截图** 中选择，也可在 **托盘右键** 快速切换。<br>
HDR 模式遇到 SDR 屏幕会保存对应的 SDR 内容；手机播放兼容性仍取决于具体设备与播放器。

<br>

## 功能巡礼

<details open>
<summary><b>✦ 冻结画面，慢慢处理</b></summary>

- 拖拽选区或点击窗口，跨显示器选取；坐标与尺寸独立显示。
- 常用操作一步可达，同类工具分组收纳；选中标注工具才显示颜色与线宽参数。
- 保存、复制、OCR 按截图入口突出；Esc 逐层关闭菜单、编辑状态与截图会话。
- 贴图支持桌面查看与交互；长截图支持纵向 / 横向拼接，另有 GIF 录制入口。
- 截图库与剪贴板页面支持预览、复制、管理和批量格式转换。

</details>

<details>
<summary><b>✦ HDR 图片、SDR 观感与亮度分析</b></summary>

- 原始 HDR 浮点数据用于 HDR 输出；普通 SDR 图片使用 **StarshotPerceptual**，保护桌面亮度并平滑压缩高光。
- **Ultra HDR JPEG** 提供 SDR 基础图与 HDR gain map，支持额外保存；可调整 HDR Capacity Max。
- **HDR 亮度分析**仅在选区拥有原始 HDR FP16 数据时可用。轻量 / SDR 模式不会把 8-bit RGB 冒充绝对 nits。
- scRGB 亮度为内容信号估计：`max(0, (0.2126R + 0.7152G + 0.0722B) × 80)`，并非显示器实际发光测量。
- Waveform / Heatmap 默认关闭，主动打开才准备分析数据；大区域可使用标明近似的抽样结果。

[捕获模式说明](docs/capture-modes.md) · [亮度分析与资源验证](docs/reports/hdr-analysis-lifecycle-20261009/REPORT.md)

</details>

<details>
<summary><b>✦ OCR 本地识别，大模型按需翻译</b></summary>

- **PP-OCRv6 Tiny 内置默认**；Small 在 DLC 页面下载、校验、切换、删除，复用已有 CLS。
- 模型首次识别时加载；Tiny / Small 不同时常驻。Small 未安装或损坏时回到 Tiny。
- 识别结果先进入独立的小窗口，支持智能分段、原图断行与编辑；OCR 自动复制可在设置中开启，默认关闭。
- 文字工作空间也可以直接输入文字使用；提供选区格式编辑与翻译。
- API 地址、Key、模型可自行配置，支持模型列表发现。Key 使用 Windows DPAPI 加密保存。
- **OCR 在本机进行**；点击翻译时，文本发送至你配置的 API，可能产生相应服务费用。

[OCR 模型与许可](third_party/simdpaddleocr/README.md)

</details>

<br>

## 下载与安装

| 发行方式 | 下载 | 说明 |
| :--- | :--- | :--- |
| **离线安装包** | [下载 EXE ↗](https://github.com/Yukikaze1945/Starshot/releases/download/2.6.0-preview.8/Starshot-2.6.0-preview.8-setup-x64.exe) | 约 **322 MiB**；包含 WebView2 Runtime，缺失时安装；覆盖升级保留配置 |
| **便携版** | [下载 ZIP ↗](https://github.com/Yukikaze1945/Starshot/releases/download/2.6.0-preview.8/Starshot-2.6.0-preview.8-win-x64.zip) | 约 **165 MiB**；解压后运行根目录 `Starshot.exe`；需要 WebView2 Runtime |
| **完整性校验** | [SHA256SUMS.txt ↗](https://github.com/Yukikaze1945/Starshot/releases/download/2.6.0-preview.8/SHA256SUMS.txt) | 核对安装包 / ZIP 的 SHA-256 |
| **全部版本** | [GitHub Releases ↗](https://github.com/Yukikaze1945/Starshot/releases) | 发布说明、历史版本与后续更新 |

> [!IMPORTANT]
> 当前公开下载为 **Windows x64 预览版**，推荐 Windows 11。HDR 捕获需要 Windows 已开启 HDR 的显示器；普通 SDR 显示器也可使用截图与 OCR。预览版功能与已知限制请查看对应 Release 说明。安装包尚未配置 Starshot 产品签名证书。

安装目录：`%LOCALAPPDATA%\Programs\Starshot Fork`。<br>
更新检查来自 **本仓库 GitHub Releases**，发现版本先提示；预览版需开启接收预览更新。当前使用全量更新。

<br>

## 快速上手

1. 打开 Starshot，在设置或托盘菜单选择捕获模式。
2. 用快捷键截屏，拖拽选区或点击窗口。
3. 标注、复制、保存，或把文字送入 OCR 编辑窗口。

| 动作 | 默认快捷键 |
| :--- | :--- |
| 全屏截图 | <kbd>Alt</kbd> + <kbd>W</kbd> |
| 区域截图 | <kbd>Alt</kbd> + <kbd>Q</kbd> |
| 区域仅复制 | <kbd>Alt</kbd> + <kbd>A</kbd> |
| 区域 OCR | <kbd>Alt</kbd> + <kbd>O</kbd> |

快捷键支持自定义组合键或单键；已修改的配置优先于以上默认值。自动复制行为在设置中单独控制。

<details>
<summary><b>下载与 HDR 的几个小问题</b></summary>

**为什么版本徽章变了，直达下载按钮还是旧版本？**<br>
徽章自动读取 GitHub；直达按钮指向已核验的具体发行包。始终可以从「全部版本」获取新的预览版。

**开启 HDR 后，分析按钮仍然是灰色？**<br>
需要真实原始 FP16 HDR 选区，混合 SDR 区域不能做绝对 nits 分析。另外，`preview.8` 存在显示器枚举误判，已有修复；修复版安装包目前尚未上传 Releases。

**手机显示 HDR 图片和视频的效果不同？**<br>
系统、屏幕 headroom 与应用支持会影响效果。Ultra HDR JPEG 和单帧 HDR 视频提供不同分享路径，但不保证所有设备显示一致。

**软件需要上传截图才能 OCR 吗？**<br>
不需要。OCR 模型本地运行；大模型翻译是独立、主动触发的 API 操作。

</details>

<br>

## 开发与贡献

<p>
<img src="https://img.shields.io/badge/.NET-10-182019?style=flat-square&amp;logo=dotnet&amp;logoColor=DDF369" alt=".NET 10">
<img src="https://img.shields.io/badge/React-19-182019?style=flat-square&amp;logo=react&amp;logoColor=DDF369" alt="React 19">
<img src="https://img.shields.io/badge/TypeScript-182019?style=flat-square&amp;logo=typescript&amp;logoColor=DDF369" alt="TypeScript">
<img src="https://img.shields.io/badge/WebView2-182019?style=flat-square" alt="WebView2">
<img src="https://img.shields.io/badge/Vite-182019?style=flat-square&amp;logo=vite&amp;logoColor=DDF369" alt="Vite">
</p>

React / TypeScript / Vite 负责主界面，WebView2 消息桥连接 C# 原生能力。捕获、HDR、快捷键、托盘、剪贴板与 OCR 保留对应原生实现；轻量选区继续走 CPU / GDI。

<details>
<summary><b>从源码构建 · Windows x64</b></summary>

准备 **.NET 10 SDK、Node.js 24、Visual Studio 2026 的 C++ / .NET 桌面工作负载、Windows SDK 10.0.26100**。

```powershell
git clone https://github.com/Yukikaze1945/Starshot.git
cd Starshot
dotnet build src/Starshot/Starshot.csproj -c Debug -p:Platform=x64
```

项目构建会准备 WebUI 与固定版本编码组件，首次构建需要网络。调试应用位于 `build/app/Starshot.exe`。

仅运行前端开发预览：

```powershell
cd src/Starshot.WebUI
npm ci
npm run dev
```

浏览器预览不具备桌面原生消息桥。安装包 / 便携包构建需要原生启动器、Inno Setup 和 Microsoft 签名的 WebView2 离线安装程序；具体参见 [发布与更新说明](docs/releasing-fork.md)。

</details>

[报告问题](https://github.com/Yukikaze1945/Starshot/issues/new) · [查看提交](https://github.com/Yukikaze1945/Starshot/commits/fix/uhdr-wide-gamut-red) · [贡献代码](https://github.com/Yukikaze1945/Starshot/pulls) · [发布流程](docs/releasing-fork.md)

## 致谢与许可

基于 [loliri / Starshot](https://github.com/loliri/Starshot)，保留原作者与贡献者声明。本仓库源码采用 [MIT License](LICENSE)。

感谢 [SimdPaddleOCR](https://github.com/sdcb/SimdPaddleOCR)、libultrahdr、FFmpeg、Win2D 与 WebView2 等项目。**第三方组件保持各自许可**：OCR 模型说明见 [SimdPaddleOCR notices](third_party/simdpaddleocr/README.md)，独立 FFmpeg 编码组件使用 [GPL v3 构建](third_party/ffmpeg/7.1.1/win-x64/README.txt)，发布包携带相应许可与源码来源资料。

<br>

<div align="center">

**CAPTURE THE LIGHT. KEEP THE DETAIL.**

<sub>Starshot Fork · 源码、安装包与更新，一个仓库。</sub>

<br><br>

[![Star this project](https://img.shields.io/badge/LIKE%20THE%20VIEW%3F-GIVE%20IT%20A%20STAR-DDF369?style=for-the-badge&labelColor=182019&logo=github&logoColor=DDF369)](https://github.com/Yukikaze1945/Starshot/stargazers)

</div>
