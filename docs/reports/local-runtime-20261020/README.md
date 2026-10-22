# Index-Translate-2B：GPU、常驻与本地 API

## 使用

入口：**设置 → OCR 与翻译 → 本地翻译运行控制**，或 **模型管理库 → 让翻译随时就绪**。

- CPU 保持原来的四线程推理。GPU 使用独立的官方 Vulkan 运行时，指定 Vulkan0、全层卸载和 4096 上下文。GPU 启动失败会显示错误，不静默冒充 GPU 成功。
- **加载并常驻**：立即加载选中的 CPU/GPU 模式，不再执行 60 秒空闲卸载。按钮变为 **卸载模型**。常驻表示保留模型进程及缓存，不是将 Windows 的物理内存页强行锁定，也不会持续生成文本。
- **卸载模型**：关闭本地 API、取消外部 API 任务、等待内部任务收尾、终止并等待模型进程退出，释放其运行资源。切换 CPU/GPU 时先结束旧进程，不同时加载两套模型。
- **开启本地 API**：自动加载并常驻同一模型。关闭 API 保留常驻模型，卸载模型同时关闭 API。
- 常驻与 API 仅本次进程运行有效，启动时不自动占用模型内存/显存。CPU/GPU 选择和成功启用的端口持久化。

## 其他软件配置

| 项目 | 默认值 |
|---|---|
| Base URL | `http://127.0.0.1:43182/v1` |
| Model | `Index-Translate-2B` |
| API Key | 开启后点击 **复制密钥** |
| 协议 | OpenAI 兼容的文本 Chat Completions / Completions 子集 |

支持：

- `GET /v1/models`
- `POST /v1/chat/completions`
- `POST /v1/completions`
- 普通 JSON 返回与 `stream=true` 的 SSE 流式返回。

示例请求（密钥为占位符，不是真实密钥）：

```http
POST /v1/chat/completions HTTP/1.1
Host: 127.0.0.1:43182
Authorization: Bearer YOUR_COPIED_KEY
Content-Type: application/json

{"model":"Index-Translate-2B","messages":[{"role":"system","content":"将用户文本翻译成简体中文，只输出译文。"},{"role":"user","content":"Keep the highlights and accurate colors."}],"temperature":0,"max_tokens":256,"stream":false}
```

只监听 IPv4 loopback，不开放局域网，不需要管理员 URL ACL。密钥与内部模型 worker 的密钥分离，使用随机 256-bit 密钥，Windows DPAPI CurrentUser 加密保存于当前应用数据目录。界面轮询不返回密钥，用户明确点击复制时才解密使用。

不开放浏览器跨域、模型管理、文件读取、远程图像或工具调用。模型名固定为 Index-Translate-2B，输入为纯文本；默认 temperature=0、关闭 thinking，一次一份结果，输出最多 2048 tokens。请求体上限 128 KiB，外部并发/等待请求最多 4 个，超出返回 429；内部和外部推理共用一个执行锁、一个模型进程。未提供 `/v1/responses`、embeddings、音视频等接口。

## 组件与部署

- llama.cpp **b11429** 官方 CPU 与 Vulkan x64 包，分别部署至 `LocalInference` 和 `LocalInferenceVulkan`，CPU 模式不加载 Vulkan 运行时。
- Vulkan archive SHA-256：`1BFE78AD9168B79FA02BF67F6AF9F5E17A966D824D77238517F7BEF12AC73B36`。
- 来源：https://github.com/ggml-org/llama.cpp/releases/tag/b11429
- MIT 主许可及原有第三方说明随运行时复制；使用系统 Vulkan 驱动。模型数据仍由原 DLC 管理，模型仓库不会提供动态执行 DLL。
- HTTP 入口使用 .NET 10 的 ASP.NET Core Kestrel，由自包含构建携带。禁用环境/appsettings 添加额外监听端点，日志不保存请求正文或密钥；不依赖 HttpListener 的管理员注册。
- build 和未来 publish 的复制规则均包含两套固定版本组件。本轮只实际执行 Debug 构建，没有 Release/publish/安装/推送。

## 验证

- **32 项**不加载模型的 API/生命周期测试通过：DPAPI 非明文且稳定、鉴权、Origin/Host 限制、私有路由隔离、普通文本请求、危险参数剥离、图像/错误 JSON/错误 token 上限拒绝、128 KiB 上限、SSE 首块与结束标记、4 请求上限/第 5 请求 429、禁用取消请求和释放端口、重复 Dispose、切换模式不自动加载、已取消加载不启动 worker。见 `api-security-lifecycle.json`。
- 200% 浅色 1280 与深色 720 UI 模拟测试通过，涵盖设置和模型库入口、CPU/GPU、加载后变卸载、API 开关、修改端口、密钥复制、关闭 API 保留常驻、卸载后恢复按钮及布局边界。没有通过 UI 模拟加载真实模型。见 `model-library-ui.json`。
- 截图库、截图独立编辑及翻译交互回归通过，0 页面错误。见 `workspace-ui.json`。
- WorkspaceCore **42 项**及前端 **16 项**单元测试通过，均未运行额外模型推理。
- 主程序独立 Debug 构建成功，0 警告、0 错误，输出 `build/local-runtime/app`。见 `debug-build.log`。前端 Vite 原有的大分块体积建议仍存在。

### 唯一一次 GPU 实测及其限制

用户已明确允许一次 GPU 短测。实测设备为 **NVIDIA GeForce RTX 5060 Ti**。

- 实际加载 GPU 模型，worker PID 45024，启动约 **6954 ms**。
- 原生断言确认：resident=true、activeBackend=Vulkan0、加载 ggml-vulkan.dll；模拟越过空闲阈值仍保持加载；开启 API 复用相同 worker 对象，没有二次加载。
- 已通过真实 API 的鉴权模型列表请求和 **恰好一条**英文 → 中文生成请求；返回 HTTP 200、非空译文。
- 卸载后断言 Loaded=false、apiEnabled=false 已通过。随后测试脚本用瞬时进程枚举要求 PID 立即不出现而误报，导致最终性能与 KMT 采样对象未序列化。
- 独立后续检查确认没有剩余 llama-server 进程。退出断言已改为等待进程退出状态及 OS 枚举收尾，没有为了修正测试再次加载 GPU 或生成译文。
- 因上述测试宿主误报，**本轮不提供未落盘的 GPU decode/prefill 或显存数值，不宣称完整 GPU 测试程序最终返回成功**。实测事实与限制见 `gpu-smoke.json`。

没有覆盖正在运行的源码输出或安装版，没有真实桌面截图；仅保留最多一张合成界面代表图，其他界面图使用独立 temp 并 finally 清理。没有第二次 GPU 模型加载。

## 主要文件

- `src/Starshot/Helpers/OfflineTranslation.cs`
- 新增 `LocalTranslationApi.cs`、`LocalTranslationApiKey.cs`
- `src/Starshot/Helpers/OcrTranslationClient.cs`（复用既有 DPAPI 封装）
- `src/Starshot/AppConfig.Setting.cs`
- `src/Starshot/Features/WebUI/WebUiBridge.Models.cs`
- 新增 `src/Starshot.WebUI/src/LocalTranslationRuntime.tsx`、`.css`
- `ModelLibrary.tsx`、`SettingsPanel.tsx`
- `src/Starshot/Starshot.csproj`、`scripts/Restore-LocalInference.ps1`
- `third_party/local-inference/manifest-vulkan.json`、`SOURCES.md`、`.gitignore`
- `tools/LocalTranslationRuntimeTest`、相关 UI/WorkspaceCore 测试链接与替身。

未修改 WGC、截图 tone mapping、HDR 编码或 OCR 推理实现。
