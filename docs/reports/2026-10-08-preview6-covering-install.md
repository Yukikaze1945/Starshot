# Preview.6 本机覆盖安装

2026-10-08，Windows x64。用户授权覆盖当前安装版并提交到现有 GitHub fork。

- 将已验证的 `2.6.0-preview.6` 自包含发布目录打成离线安装包和便携 ZIP。
- 打包使用已有原生启动器与 Microsoft 签名验证通过的完整 WebView2 Runtime；确认载荷不含配置、数据库、日志或浏览器用户数据。
- 在 `%LOCALAPPDATA%/Programs/Starshot Fork` 完成每用户覆盖安装，安装程序返回 0，无需重启 Windows。
- 覆盖前备份配置和版本信息；安装后 `config.sjson` SHA-256 与备份相同。旧版本目录保留，没有删除用户数据。
- `version.ini` 指向 `app-2.6.0-preview.6`；安装后的 `Starshot.dll` 与隔离验证构建 SHA-256 相同；FFmpeg 组件通过固定文件哈希验证。
- 原有根目录启动器成功启动新版：实际进程路径位于 `app-2.6.0-preview.6`，产品版本为 `2.6.0-preview.6`，进程保持运行且 `Responding=True`。
- 没有进行鼠标键盘截图测试。单帧视频的自动验证见 [视频报告](2026-10-08-static-hdr-video-validation.md)，轻量模式验证见 [CPU 报告](2026-10-08-lightweight-cpu-validation.md)。手机兼容性仍待实测。
- 本次仅向仓库提交源码、测试与精选验证记录，没有发布新的 GitHub Release 或上传二进制包。

本地安装包 SHA-256：`7edba9c76782af0698294aad505240caf7174eaa92afb5372c1b62df2b7dc054`。

本地便携 ZIP SHA-256：`092178edbc29c95a78cf5d90ef167514f6199e8f8f8c15eac2b72b90e85c09dc`。

安装和启动的原始记录保存在 `build/installed-upgrade-20261008-preview6/`，其中配置备份不会纳入版本控制。
