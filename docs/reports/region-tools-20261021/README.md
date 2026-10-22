# 区域截图工具集成与译层修复 · 2026-10-21

## 改正交互

- 实时字幕：区域截图选择字幕范围 → 更多 → 实时字幕摘录，确认现有选区立即开始，不需要再拖一个自绘框。
- 删除旧 SubtitleRegionWindow。重新选区也使用原来的区域截图；轻量与标准/HDR共用动作和物理坐标转换，支持菜单编辑器。旧版布局自动补一次入口，用户移除后不会再强加。
- 控制面板缩为420×400 DIP，沿用Starshot中性配色、强调色与原生合并标题栏，锚定选区外侧可用工作区；可通过正常标题栏移动。面板X、原生关闭按钮和Esc会停止任务；停止/重选不丢文案，取消选区不会清空旧任务。不保留第二个选框HWND。
- 区域“原位翻译覆盖”在原来的覆盖层完成OCR、翻译和绘制，不再进入图片工作区、图库或独立编辑窗口。一次翻译为一个可撤销标注；Ctrl+Z整批撤销、重做恢复，复制/保存包含译文。
- GPU覆盖层读取已有SDR crop；CPU覆盖层读取CPU裁剪图。翻译背景取文字框周边的原图颜色，选择对比文字色，不强制白色遮盖深色UI。OCR、色调映射、WGC、编码算法不因这次修改重写。
- 取消/关闭使任务失效，改选区后旧结果不得贴回；同步OCR推理仅取消后安全收尾，不强行Dispose执行中的模型。

## 附图为什么不能翻译

日志与1134×1047附图实际复现一致：Small返回25行，其中两个下拉箭头是空字符串（框约7×5像素）。原来的workspace.ocr把空行一并交给translation.formatted；OcrTranslationClient第178行拒绝空片段，抛ArgumentException，发生在请求翻译后端之前。Tiny对同图返回20行且无空行。

本次在工作区OCR结果、WebUI输入边界及区域直接翻译公共入口过滤空白/无效尺寸结果；保留客户端对缺ID、重复ID等无效参数的校验。截图里的Small输出成为23个有效翻译片段，不改普通OCR模型推理和识别结果原始数据。原图与OCR元数据不被原地修改。

实际模型复现见attached-image-ocr.json、ocr-image.log；CPU执行，没有加载GPU翻译模型。真实翻译服务未调用：后续参数与绘制测试使用确定性翻译替身，不能把这些文本当作真实大模型翻译质量验收。

## 验证

- Debug隔离应用构建：0警告/0错误。无Release、安装、推送或发布。
- 区域工具栏纯逻辑/布局回归：2,750,433个断言，涵盖126种DPI/显示器布局组合；新字幕动作、旧配置迁移、负坐标只偏移一次通过。
- 字幕采集与控制器：45项通过，包括原选区直接开始、重选/取消保留文案、面板关闭停止、退出后拒绝迟到结果及原有保存/去重检查。控制器测试注入合成捕获，不读取桌面。
- CPU/GDI：254项通过。附图实际Small OCR输出重放验证25→23片段；译层一批标注、撤销/重做、复制带译文、关闭/改区后迟到结果不发布、字幕只交付坐标且销毁旧覆盖层。该测试设施的既有错误注入会输出一次Failed to destroy CPU capture window警告，资源恢复断言通过。
- 标准/HDR覆盖层隐藏窗口：10项新增检查通过；200%DPI译层画面参与SDR crop、撤销逐像素恢复、重做一致、复制交付同一译文crop、字幕物理坐标正确、关闭释放借用源/swapchain。合成BGRA输入，不执行WGC或桌面截图。测试宿主构建有2条既有CS0436 Windows SDK初始化器同名警告，应用构建无此警告。
- 字幕WebUI：4组窗口尺寸/深浅主题，200%DPI；重选、开始暂停、结束后复制、整理、导出、新建确认、X与Esc均通过，没有图库命令。
- 图库/独立选区编辑器WebUI回归通过，并添加空OCR图标行；翻译请求只含有效文字。前端16项单测通过。
- 所有新测试图片/临时模型目录由测试finally清理；可视检查图片也在独立temp内自动清理，未留下新测试截图。保留日志和JSON结果。

## 修改范围

共享动作/设置：RegionToolbarModel、RegionToolbarPreferences、SubtitleSelectionRegion。
字幕：SubtitleController、MainWindow、WebUiBridge.Subtitles、SubtitleApp/CSS、创作台和托盘入口；删除旧SubtitleRegionWindow。
截图译层：RegionTranslation、两种RegionCaptureWindow的Translation partial、工具栏分发、注释渲染与必要取消/退出逻辑、原区域服务路由。
图库边界：WebUiBridge.Workspace的空OCR过滤，ImageWorkspace的防御过滤。
测试：SubtitleOcrTest、LightweightCaptureTest、RegionToolbarTest、RegionToolbarUiTest、WorkspaceUiTest；SubtitleOcrBenchmark添加仅用于附图CPU复现的入口。

## 实测边界

没有操纵桌面鼠标键盘或新截桌面，没有安装/关闭用户正在运行的Starshot。真实播放器、实际标题栏鼠标拖动和用户翻译API/离线2B推理仍需交互验收；本次已验证真实Win32/Win2D隐藏窗口绘制、物理坐标、资源收尾及API前置参数。已有照片的图库译层继续保留；截图工具不会再借它实现原位覆盖。

运行源码测试：退出旧进程后运行仓库根目录Run-Starshot.cmd。本次隔离构建在build/subtitles-region/app，未替换正式安装目录。
