# 浮动文字格式工具栏修复 · 2026-10-21

- 字号/颜色控件会把焦点移出 contenteditable，旧工具栏在 blur 后卸载；选择器的后续事件因此丢失。旧构建的实际下拉菜单回归复现为：点击字号后，控件消失，无法继续按键操作。
- 现在在工具栏 pointer/focus 进入时保存 ProseMirror selection bookmark，随事务映射。选择器暂时失焦时保留工具栏，应用格式前恢复原选区；返回编辑器重新选词时更新 bookmark，点击外部恢复正常隐藏。监听与延迟 blur 回调在卸载时清理。
- 斜体命令原本会写入 em，但全局 font-synthesis:none 禁止当前字体合成斜体。只在富文本编辑区启用 font-synthesis:style，其他界面不变，不增加字体依赖。

## 修改

- src/Starshot.WebUI/src/RichEditor.tsx：选区保留、事务映射和工具栏交互。
- src/Starshot.WebUI/src/styles.css：富文本区域斜体合成。
- tools/OcrPopupUiTest/format-check.mjs：共享编辑器回归。

## 验证

- 四种配置：1060×780 明/暗文字实验室、584×391 暗 OCR 小窗、344×240 明 OCR 小窗，均为 200% scale。
- 浏览器真实字号下拉菜单按键；显式模拟原生选择器/颜色对话框导致的 blur、DOM selection 清空和延迟 change。
- 字号/颜色只作用于选中的 ControlNet；格式应用后选区恢复；重新选 extra 不会误用旧选区。
- 斜体开关、em 的 computed style、实际字形栅格差异均通过。临时截图仅在内存中比较，不写入用户目录。
- 撤销、富文本复制、翻译后的字号/颜色/斜体保持通过。
- 旧 OCR 小窗四种布局/启动/复制/设置回归通过；16 项前端单元测试通过。
- Debug 隔离构建通过，详见 app-build.log；无安装、Release 构建、推送或发布。

颜色对话框失焦使用软件浏览器模拟，没有操作用户桌面的 Windows 系统颜色对话框。未加载 OCR/翻译模型，未修改推理、截图或 HDR 管线。启动最新源码版需要退出旧进程后运行 Run-Starshot.cmd。
