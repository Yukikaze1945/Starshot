# 单帧 HDR 视频：隔离版本验证

日期：2026-10-08。版本：2.6.0-preview.6，Windows x64。

## 已实现

- 第四档 `HdrVideo=3` 位于高质量 HDR 之后，设置与托盘同步；旧模式数值和默认值保持兼容。视频编码选项为 HEVC（默认）／AV1，统一 MP4。
- 全屏和区域保存使用一次高质量捕获的冻结画面，含区域裁剪和已有标注。只编码一帧，由 MP4 样本时间信息保持 3 秒，无音轨。复制、OCR、贴图、长截图、GIF 保留各自操作。
- 先保存伴随图片，沿用现有图片格式、品质、实际 SDR 内容判定及附加 Ultra HDR JPEG 设置。视频与图片同目录、同唯一名称主体；视频失败或取消时保留图片，不覆盖重名文件。
- HDR 原始浮点像素经过现有 scRGB→BT.2020/PQ 转换，再编码为 10-bit、4:2:0、有限范围。SDR 使用 BT.709 色彩标记；不会把 SDR 预览当成 HDR 来源。
- HEVC 使用 libx265，AV1 使用 SVT-AV1，均为独立 CPU 编码进程。写入内容亮度 metadata；mastering metadata 仅使用有效单显示器测量值。多显示器或信息无效时省略。
- 视频边缘复制补齐至偶数、最小 64 像素；伴随图片保留原尺寸。异步编码可通过处理提示或托盘取消，进程退出并删除未完成 MP4。
- 自动复制生成文件；图库支持 MP4 筛选、伴随图片缩略图、编码及 HDR/SDR 标记、系统播放器打开和文件复制。MP4 不进入图片 OCR／贴图解码器。
- WGC context 生命周期未改动。视频编码组件仅在需要验证／编码时启动，没有常驻编码进程。

## 验证结果

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| 编码与像素测试 | 7/7 组通过；真实 HEVC／AV1 编解码覆盖 HDR 4K、64×64、奇数 SDR、小尺寸、填边和 metadata | `build/hdr-video-encoder-tests.log` |
| 实际原生保存服务 | 两种编码通过；取消保留图片；重名 MP4 保持原字节且报告失败 | `build/hdr-video-native-tests.log` |
| 模式和资源检查 | 19 项通过，包含模式兼容、HDR/SDR 路径与取消；未写测试图片 | `build/hdr-video-mode-tests.log` |
| 前端 | TypeScript/Vite 构建及 10 项现有桥接／文档测试通过 | `src/Starshot.WebUI` |
| 隔离发布 | 自包含 Release 发布成功；WebUI、新编码组件、既有 uhdr.dll 均包含；固定哈希验证通过 | `build/hdr-video-publish.log` |
| 从空目录恢复编码组件 | 固定包下载、哈希校验、解压与能力检查通过 | `build/hdr-video-restore-check.log` |

最终发布组件的 ffprobe 再次读取三组保留样本，每个文件均为 **一个 packet、一个可解码帧、一个视频轨、样本时长 3.000000 秒**，无音频轨：

| 样本组 | 输出尺寸 | HEVC | AV1 | 色彩 |
| --- | --- | --- | --- | --- |
| HDR 4K | 3840×2160 | Main 10，yuv420p10le | Main，yuv420p10le | BT.2020／PQ，有限范围 |
| HDR 小图 | 64×64 | Main 10，yuv420p10le | Main，yuv420p10le | BT.2020／PQ，有限范围 |
| SDR 奇数图（源 65×67） | 66×68 | Main 10，yuv420p10le | Main，yuv420p10le | BT.709，有限范围 |

详细数据：`build/hdr-video-samples/validation.csv`。仅保留这三组、每组两种编码的合成代表样本；其他测试媒体位于独立 temp 目录，并在 finally 删除。

原生离屏测试从真实 `R16G16B16A16Float` 合成画面经应用的转换 shader 和保存服务编码，再解码 PQ 读回亮度：

| 编码 | 源 80 nit 白 | 源 1000 nit 高光 |
| --- | --- | --- |
| HEVC | 79.59 nit | 997.21 nit |
| AV1 | 79.77 nit | 998.46 nit |

亮度测试同时包含彩色色块和白色标注夹具，高光没有在 SDR 白处提前截断。不存在对真实桌面截图、实际鼠标标注结果或手机播放效果的推定。

## 构建与许可

编码组件固定为 FFmpeg 7.1.1 Gyan shared 构建，校验包及文件 SHA-256，检查 libx265／libsvtav1 能力；运行时不下载组件、不搜索用户 PATH。组件载荷约 151 MiB。独立 MSBuild build/publish、便携包与安装包的 staging 流程均接入复制和验证，本轮只生成隔离发布目录。

`VideoEncoder` 内附 GPL v3 LICENSE、上游 README、来源及构建记录 `SOURCES.md`；Starshot 自身 LICENSE 保留。未来公开分发前仍需准备与实际构建匹配的完整第三方源码／构建材料或有效源码提供方式，不能把链接清单称为完整 GPL 合规包。参考 [FFmpeg 许可说明](https://ffmpeg.org/legal.html)。

单样本时间通过 [FFmpeg setts](https://ffmpeg.org/ffmpeg-bitstream-filters.html#setts) 设置，测试另外读取 MP4 packet 时长，未只依据进程成功或容器总时长。

## 使用位置与尚未验证项

隔离程序：`D:/coding/Starshot/build/hdr-video-publish/app/Starshot.exe`。配置位于该 app 父目录，日志／缓存和手动保存目标使用隔离目录。先从托盘退出当前安装版，再打开隔离程序，以免单实例机制把启动交给旧版本。

设置和托盘、自动文件复制、图库系统播放器入口已接通并检查代码及前端构建，但本轮按约定未进行桌面鼠标键盘交互测试，也未实际操作系统剪贴板／播放器。手机播放器对单帧 MP4 的接受程度仍需代表样本实测；没有改成重复帧。没有覆盖安装版，没有创建安装包或发布 GitHub Release。
