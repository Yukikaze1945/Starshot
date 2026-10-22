# Index-Translate-2B Q5_K_M CPU 推理速度

2026-10-19 实测。独立测试进程，不启动 Starshot、不改模型安装状态或应用设置。

## 配置

- CPU：AMD Ryzen 7 5700X，8 核 16 线程。
- 内置 llama.cpp CPU x64 **b11429 / d81235049**，实际加载 `ggml-cpu-haswell.dll`，未加载 CUDA/Vulkan 后端。
- 模型：Index-Translate-2B.Q5_K_M.gguf，**1,454,787,072 bytes**。
- SHA-256：`2A869B5E374FF6121EE5191629FBA8FF4A081951D56EEC90D89771D7971465EE`。
- 使用应用当前参数：4 CPU 线程、GPU layers=0、context=4096、parallel=1、temperature=0、Jinja 模板、enable_thinking=false。
- 使用应用现有中译提示词，英文合成材料 → 简体中文。唯一输出长度上限改为 256 tokens；全部 6 次都自然停止（finish_reason=stop），没有被此上限截断。
- 每次请求额外指定 cache_prompt=false；返回的 cache_n 和 cached_tokens 均为 0。排除提示词缓存对 prefill 的影响。

## 六轮结果

| 轮次 | 文本 | 输入 tokens | 输出 tokens | Prefill tokens/s | Decode tokens/s | 请求耗时 |
|---|---|---:|---:|---:|---:|---:|
| 1 | 短 | 110 | 41 | 96.38 | 13.05 | 4.2100 s |
| 2 | 中 | 184 | 113 | 127.02 | 13.60 | 9.7032 s |
| 3 | 长 | 300 | 232 | 134.26 | 13.41 | 19.4763 s |
| 4 | 短 | 110 | 41 | 112.19 | 12.95 | 4.0954 s |
| 5 | 中 | 184 | 113 | 127.94 | 13.78 | 9.5865 s |
| 6 | 长 | 300 | 232 | 131.94 | 13.33 | 19.6427 s |

输入 token 数包含模板和系统提示词。速度直接取服务端 timings，不以字符数或请求总时长倒算。该版本服务端的 decode 计时不包括在 prefill 结束时产生的首个 token，汇总采用相同口径。

- 合并 token/执行时间加权：**prefill 124.83 tokens/s，decode 13.42 tokens/s**。
- 各轮速度中位数：prefill 127.48，decode 13.37 tokens/s。
- 每类两次平均：短文本 4.15 s、中等 9.64 s、长文本 19.56 s。耗时大头为生成译文。
- 首次进程启动到 health ready：**2.06 s**，包含服务端模型初始化及默认 warmup，不包含启动前文件哈希检查和 Starshot UI/模型管理校验。未清除 Windows 文件缓存，不能称作磁盘冷缓存加载时间。
- 第一次短文本请求 4.21 s；进程启动加这次请求约 6.27 s，后续请求不重复启动模型进程。

运行期间一次采样：Private Memory **823.26 MiB**，Working Set **1717.43 MiB**。这不是峰值；模型文件内存映射也计入工作集，不能用 Private Memory 单独代表模型总内存。

这是裸模型请求测试。长样本为 1428 个英文字符，直接请求了底层服务；应用 TranslateAsync 对超过 1200 字符的文本会再分段，因此不能把这一行时长直接等同于完整应用长文翻译耗时。没有测试 8 线程调优、GPU 推理、识别耗时或翻译质量排名。

测试完成后已停止独立 worker。没有 GPU 模型推理、桌面截图、图片落盘、安装覆盖、构建或发布。

## 文件

- `results.csv`：完整六轮平铺数据。
- `results.json`：配置、原始 timings 和 usage。
- `summary.json`：分组及合并统计。
- `1-short.json` 等：服务端原始响应与合成译文。
- `runtime-observation.json`：CPU 后端与单次内存采样。
- 复现工具：`tools/OfflineTranslationBenchmark/run.py`。
