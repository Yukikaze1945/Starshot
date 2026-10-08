# Preview.7 OCR + StarshotPerceptual 本机覆盖安装

2026-10-08，Windows x64。用户授权将当前 OCR 迁移和普通 SDR 新色调映射一起安装到现有安装版。

- 从当前工作区重新制作 `2.6.0-preview.7` 自包含、裁剪优化发布产物，包含 SimdPaddleOCR 1.4.2、PP-OCRv6 Tiny 1.0.0（DET/CLS/REC）和 StarshotPerceptual。
- 生成离线安装包及便携 ZIP。沿用已有原生启动器，Microsoft 签名的完整 WebView2 Runtime 验证通过；载荷不包含用户配置、密钥、数据库或缓存。
- 在 `%LOCALAPPDATA%/Programs/Starshot Fork` 覆盖安装成功，安装程序返回 0，不需要重启 Windows。旧版本目录保留供回退。
- 安装前备份配置和 version.ini；安装后 `config.sjson` 哈希不变。版本指针指向 `app-2.6.0-preview.7`。备份仅在 Git 忽略的本机构建目录中保留，没有写入报告或版本控制。
- **663 个安装文件逐项 SHA-256 与发布目录一致**。WebUI、模型和许可证齐全，固定 libultrahdr 和视频编码组件哈希校验通过。
- 最终发布包的新映射 **53/53 检查通过**，发布版与源码渲染逐字节一致；没有重复跑 4K 压力测试或真实截图。
- 从安装目录加载实际 Starshot.dll 和四个 Sdcb 程序集，执行 `TonemapToSdr → PreparePixels → RecognizeAsync` 合成 HDR 文字检查：识别 `HDR PREVIEW 203 NITS`，完整调用 **258 ms**，模型初始化一次、fallback=0、输入不变。
- 从安装目录读取内嵌模型资源合计 **7,296,226 byte（6.958 MiB）**，不依赖开发机 NuGet 模型缓存，也不下载 OneOCR。
- 原有根目录启动器已启动新版，实际进程路径为 `app-2.6.0-preview.7/Starshot.exe`，窗口标题“Starshot · 创作台”，`Responding=True`。自启动任务仍指向根目录启动器，因此会使用新的版本指针。
- 没有桌面鼠标键盘操作、没有真实截图/剪贴板 UI 测试。没有推送 GitHub 或创建 Release。

本机安装包：`build/fork-release/2.6.0-preview.7/Starshot-2.6.0-preview.7-setup-x64.exe`。

安装包 SHA-256：`b2c415336b3c3a1d2546f7eceb44c6864a3d6fb455b3292c352ae3386e815c9e`。

便携 ZIP SHA-256：`302c01eb76aacc48afcdd28f662fcf15ffb1eb58d2a7ae723d72284ec8b4a439`。

本机原始记录在 `build/installed-upgrade-20261008-preview7/`；算法和 OCR 的完整验证边界分别见 [StarshotPerceptual 报告](2026-10-08-starshot-perceptual-validation.md)及 [OCR 报告](2026-10-08-paddle-tiny-ocr-validation.md)。
