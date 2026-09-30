# Starshot Fork 发布与更新

源码仓库 `Yukikaze1945/Starshot-private` 保持私有。公开仓库
`Yukikaze1945/Starshot-releases` 只保存下载说明、版本记录和编译后的发行包。
不得把源码、配置、API 密钥、数据库、缓存或测试截图上传到下载仓库。

## 发布流程

1. 在私有仓库提交并推送完成验证的代码。
2. 手动运行 **Fork Release** workflow，输入唯一版本号，例如
   `2.6.0-preview.2` 或 `2.6.0`；勾选 publish 才发布到公开下载仓库。
3. workflow 生成 x64 离线安装包、完整便携 ZIP、SHA256SUMS；上传完成后才公开 Release。
   `-preview.N` 自动标记为预览版。已发布版本不可覆盖；修复用新版本号。

跨仓库发布需要私有仓库 Actions secret `RELEASE_TOKEN`：使用 GitHub fine-grained PAT，
只授权公开下载仓库的 **Contents: Read and write**。普通 GITHUB_TOKEN 只能写当前私有仓库。
没有该 secret 也可以取消 publish，仅构建下载 artifacts，然后通过本机已登录的 gh 手动发布。

本机打包使用 `tools/Build-ForkRelease.ps1`，需要 .NET 10、Node、Inno Setup 6、
已编译的原生启动器和 Microsoft 签名的 WebView2 Evergreen x64 离线安装程序。
正式 CI 从当前源码编译启动器。本次本机首次发布使用官方 2.5.3 ZIP 中未修改的启动器，
其归档 SHA256 为 `3e81c63c42e8284124b60e85ea14f7d07336499758a24fbabdb3b2e519866cc4`。

## 客户端行为

更新固定查询公开下载仓库 GitHub Releases；不会再查询上游的软件更新 CDN。
OneOCR 引擎下载源保持独立。预览构建初次启动默认接收预览版；可在“设置 → 应用”关闭。
自动检查与手动检查均先提示，再由用户确认下载。当前发行全量更新；未接入差分发布。

安装版与便携版共享启动器布局：根目录 `Starshot.exe`、`version.ini` 和
`app-{version}`。安装包使用独立的每用户目录 `%LOCALAPPDATA%\Programs\Starshot Fork`，
不会覆盖原版安装目录。配置与密钥文件保存在根目录，更新不会替换；卸载也保留用户数据。
便携 ZIP 不含 WebView2 Runtime；安装包内置完整 Runtime，仅在缺失时安装。

安装包目前未配置产品签名证书，不能声称具有 Starshot 的 Authenticode 签名。
将来增加签名时，在计算 SHA256 和上传之前签名。GitHub HTTPS 与校验文件用于下载校验，
不等同于客户端已实现签名验证。
