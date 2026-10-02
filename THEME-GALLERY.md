# Codex 主题图库

在配置工具中选择主题并应用到 Codex 桌面应用。主题操作独立于网站地址、模型和 API Key 设置。

打开配置工具，在“外观设置”的“主题图库”页浏览主题。单击主题图片或名称选择主题，双击图片查看大图；“已选择”只表示选中了卡片，点击“应用并重启”才会改变 Codex 外观。“详情”打开作者的主题页面。

## 图库与预览

- 内置图库包含 [CodexThemes](https://codexthemes.ai/) 的完整目录快照，按行列展示图片卡片，图片下方提供应用按钮，并保留作者与详情页信息。
- 本地资源完整时优先使用离线图片和主题包；便携包只附带目录清单，缺失的普通主题资源按需联网获取并缓存，后续优先读取缓存。需要本地适配的主题仍须提供对应资源。
- 网站提供 `.codex-theme` 包的条目在完整性校验通过后可以应用；设计参考、需要登录下载的归档和资源不完整的包会单独标注。
- 预览使用作者发布的图片，实际效果取决于当前 Codex 版本和主题自身覆盖范围。主题通常改变全局颜色和材质，部分主题仅在首页显示背景画。
- 下载内容只用于样式和图片，不执行主题包中的脚本。

分发软件时，请保留 EXE 旁的 `theme-gallery` 资源目录，不要只复制 EXE。源码资源位于 `assets\theme-gallery`，构建脚本会复制到输出目录；`bundle-inventory.json` 记录下载来源、大小与 SHA-256。原始下载文件保留不变，已核验的包 ID 别名及图片 MIME 元数据修复通过与文件 SHA-256 绑定的清单记录。

`theme-1786242140810`（党政风格）、`chicken-soup-codex` 和 `kuromi` 的原始公开包缺少 CSS 引用的附图，现有包和公开主题页没有提供这些文件。三者保留完整下载及预览，显示具体缺失原因并禁用应用，不使用其他图片冒充缺失素材。当前离线库的 70 个公开包中，67 个通过应用前校验。

## 应用与恢复

直接应用需要 Codex 提供本机调试端口。已经以外观模式启动的 Codex 可直接切换；普通启动的 Codex 若没有调试端口，可以选择主题并点击“应用并重启 Codex”。确认后，工具先下载并校验主题，再请求 Codex 正常退出，以外观模式重新启动并自动应用所选主题。重启可能中断当前任务，应先保存工作。

如果 Codex 拒绝退出或退出超时，工具会停止本次操作并给出提示，不强制结束进程。只有重新启动后的页面通过主题校验，才会显示应用成功。

主题卡片仅保留“应用并重启”和“详情”两个按钮；窗口中的“恢复原生主题”入口继续保留。

主题通过当前会话的样式注入生效，不修改官方程序文件、认证信息或 API 配置。恢复入口移除本工具注入的外观。完全退出后重新启动 Codex 也可恢复原生外观。

应用内普通页面导航会保留样式；如果刷新了渲染页面或重启 Codex，需要重新应用。图库预览是作者提供的效果图，不等同于对每个主题进行过当前 Codex 版本的实机视觉验证。

## 构建

本项目是 Windows WinForms 桌面程序，无需构建或重启 Docker。修改后需重新编译 EXE。若旧版本仍在运行，应关闭配置工具后覆盖原文件，或者使用单独的构建输出目录测试新版。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

旧版仍在运行时，使用独立输出目录：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test -OutputDirectory bin\theme-preview
.\bin\theme-preview\CodexCompanion.exe
```

网站接口与包格式参考：[使用说明](https://codexthemes.ai/docs)、[搜索接口](https://github.com/codexthemes/skills/blob/main/skills/codex-theme-finder/references/search-api.md)、[下载接口](https://github.com/codexthemes/skills/blob/main/skills/codex-theme-installer/references/download-api.md)。

## 本次验证（2026-09-20）

- 独立目录编译成功；原有 8 项回归及图库分页、主题包校验、预览缓存测试通过。
- `python tests\skin_mock.py` 通过，包含 QQ 皮肤和主题 CDP 应用、恢复、部分页面失败检查。
- 实际网站返回 126 项：70 个可安装主题、4 个需要手动下载的主题、52 个设计参考。目录随网站更新。
- 公开 `gpt` 主题包下载与校验成功；真实目录及作者预览图截图检查通过。
- 没有自动更改正在使用的 Codex 外观，也没有声称全部 70 个主题都经过真实 Codex 实机测试。页面刷新或重启后需要重新应用。

只读真实图库截图检查（不会应用外观或读取个人接入配置）：

```powershell
.\bin\theme-preview\CodexCompanion.exe --gallery-preview-live .\test-output\gallery-live-final.png
```

## 实际应用入口复测与修复（2026-09-20）

在真实配置工具窗口中加载图库并点击 `GPT看板娘` 的“应用到 Codex”，发现旧版运行时的 512 KiB CSS 限额无法容纳内联背景图。该主题应用后的 CSS 为 3,355,428 字节，因此之前的目录、下载和小型模拟测试不足以证明真实主题可以完成应用。

已修复 `GalleryCatalog.cs`、`ThemeRuntime.cs`、`SkinBridge.cs` 的大小限制：保持原始主题包 30 MiB、原始 CSS 8 MiB 的限制，内联后的 CSS 统一限制为 32 MiB，CDP JSON 消息限制为 64 MiB。超限仍明确拒绝。另修复了无监听端口或探测超时被显示成“已取消一个任务”的错误提示。

补充的 4 MiB 以上合法背景图、超过 32 MiB 的重复内联拒绝、大于 4 MiB 的 CDP 消息发送、无监听端口检查均已通过。正式 `build.ps1 -Test` 和 `python tests\skin_mock.py` 通过，修复版输出为 `bin\CodexCompanion.exe`。

使用同一真实缓存主题包再次执行实际解析与运行时调用，返回“未检测到调试端口，请退出后启用外观并启动”，不再出现 CSS 大小或序列化错误。当前 Codex 未启用调试端口，用户选择暂不重启，因此尚未完成真实 Codex 界面换肤、连续切换和恢复的视觉验收；本轮没有重启或更改当前 Codex 外观。

## 离线网格图库与重启入口验证（2026-09-20）

- 软件包含完整的 126 项目录、126 张预览和全部 70 个公开包；196 个下载资源与原始 SHA-256 清单逐项一致。
- 离线验收使用拒绝全部 HTTP 请求的测试客户端，目录、预览解码、包解析与准备的 HTTP 请求数为 0。67 包通过，3 个缺资源包正确拒绝；两个包 ID 别名通过 SHA-256 绑定校验。
- 修复了正常 CSS 选择器与 Unicode 转义被误拒的问题；转义后的外部 URL、`@import`、`image-set` 等外部资源形式仍拒绝。
- 重启回归覆盖校验失败不关闭、退出失败不启动、主题验证失败不报成功、取消后不启动及窗体销毁取消。没有实际重启当前用户的 Codex，会话恢复与真实换肤效果仍需实机验收。
- 完整离线验收记录见 `test-output\offline-bundle-report.json`。

本轮主要源码边界为 `ThemeGalleryForm.cs`（网格与按钮）、`GalleryCatalog.cs`（离线资源与校验）、`CodexThemeGalleryService.cs`、`ThemeRuntime.cs`、`CodexDesktopRestart.cs`（重启应用）及 `build.ps1`（资源分发）；相关回归位于 `tests`。`Core.cs`、`CodexConfig.cs` 和凭据助手未改动，不涉及 Docker 服务。

## 图库加载与选择（2026-09-20）

- 卡片的浅绿色背景与“已选择”文字表示当前选择，不代表已应用。应用动作在图片下方。
- 顶部显示普通启动与外观模式下的操作区别；单击选择、双击查看大图。
- 搜索和筛选使用短暂防抖，滚动时按可视卡片加载预览，避免重复创建全部图片。
- 离线目录读取约 31KB 的 bundle-status.json 及文件元数据，浏览时不再逐包读取约 313MiB 的主题内容；应用选中主题时仍完整验证。缺少状态清单时沿用完整校验。
- 完整 126 项离线目录的独立实测为 14858ms → 124ms，HTTP 请求均为 0；该数字仅为目录加载，不含界面控件和图片绘制。
- 通过独立交互测试窗口实际验证了图片单击选择、标题切换选择、双击大图预览，未调用真实 Codex 的应用或重启。
- 主题网格每页 12 项，使用“上一页 / 下一页”浏览；搜索和筛选覆盖全部目录，并返回第一页。
- 构建资源必须包含 bundle-status.json；该文件仅用于快速展示，不能代替应用前校验。更新离线包时应同步重新生成状态快照。
- 最终 build.ps1 -Test 与 python tests/skin_mock.py 均通过。最终默认 bin 首屏截图进程实测 2575ms（旧版 16398ms），包括截图绘制与退出；详见 test-output/gallery-ui-performance.json。

## 图库文字可读性（2026-09-20）

图库字体由像素单位改为点字号，窗体使用微软雅黑 11pt，卡片采用约 10.5pt 正文和更醒目的标题。文字行高和按钮高度随字体调整，卡片操作按钮改为两行，避免“应用并重启”被截断。主题加载、分页和应用流程保持不变。

## 滚动预览加载修复（2026-09-20）

滚动后使用卡片所在面板的客户区判断可见性，避免重复叠加滚动偏移。滚轮和滚动条操作会在位置更新后继续加载新进入视口的图片，仍保持按需加载及并发限制。
卡片宽度按可用区域和边距计算，滚动内容高度按最终卡片布局计算，保证本页四行可达。回归覆盖首屏按需请求、滚动到底后第四行请求预览及失败预览不递归重试。独立交互窗口实测滚轮到底后“定点空投 / CO₂ 蓝天宝宝 / 深海乐园”三张图片显示正常，拖动滚动条也能加载新进入视口的预览。最终默认 bin 构建及完整回归通过，未应用主题或重启真实 Codex。

## 真实应用与新版主面板兼容（2026-09-20 后续验证）

后续检查确认当前 Codex 实际开启了动态调试端口 61990，先前“未开启调试端口”的判断来自固定端口列表漏检。运行时现从官方可执行文件所属进程发现监听端口，并只向符合条件的主应用页面注入；不处理其他应用或内嵌网页。图库失败时展示完整错误，避免状态栏截断原因。重启流程仅请求有窗口的官方进程关闭，仍等待其子进程退出，不强杀进程。

真实应用发现主题使用旧版颜色变量和 `main.main-surface` 选择器，新版 Codex 主面板因此仍为白色。现补充新版主面板识别及通用旧/新颜色变量映射，页面切换继续更新标记，恢复时撤销自身添加的样式与标记。

已通过正式程序同一服务调用链，将 GPT看板娘应用至当前 Codex 会话，无重启。实测两个主面板背景均由白色变为 `rgb(16, 24, 39)`，主文字为 `rgb(245, 242, 234)`，页面标记为 conversation。记录见 `test-output/theme-live-fixed-tokens.jsonl` 与 `test-output/theme-live-final-page.jsonl`。原主题大图按其首页规则保留，不强制铺到对话正文。

最终 `build.ps1 -Test`、`python tests/skin_mock.py` 与 `python tests/theme_runtime_browser.py` 通过。后者使用真实 Edge DOM/CSS 验证计算颜色、页面切换及恢复；未实际重启用户 Codex，未逐个实机验收所有主题。已重新构建 `bin/CodexCompanion.exe`，重新打开配置工具即可使用修复版，不涉及 Docker 镜像或服务重启。

### 首页人物主图修复

后续首页实测发现，约 3.3 MB 的内联图片放入 `--ct-art` 时被 Chromium 丢弃，星点背景仍显示，人物层的计算背景为 none。最初对变量引用展开的修复在全量测试中导致部分大图重复、超过 32 MiB。最终方案已替换为运行时将校验后的内联图片去重转换为页面内 Blob URL，切换和恢复时回收；`GalleryCatalog` 保留原始变量与作用域语义，资源校验及大小限制不变。

修复已重新应用到当前 Codex。真实页面样式表中的首页图片成功解码为 1672×941，见 `test-output/theme-live-art-rule-decoded.jsonl`。该次读取时用户位于对话页，首页最终视觉效果尚待用户切回确认。隔离 Edge 回归使用实际离线 GPT 包，经生产 `ParsePackage` 和运行时注入，验证首页伪元素图片存在且解码成功、切换页面及恢复正常；完整构建回归和 CDP 模拟回归通过。无需重启 Codex 或 Docker。

## 全量主题测试（2026-09-21）

- 全部 196 个资源的大小与 SHA-256 符合原清单；126 张预览均可解码。
- 126 项目录中，70 项有包。67 包通过解析，3 包因原始资源缺失继续拒绝（党政风格、鸡汤来喽 Codex、Kuromi淡紫像素风）；其余 56 项没有可应用包。离线检查 HTTP 请求数为 0。
- 最终隔离 Edge 矩阵为 67/67 通过，覆盖生产包解析、生产运行时注入、图片解码、首页/对话/返回首页、前后主题切换、浅色主题移除宿主深色标记，以及恢复原状态。每项保存截图和计算样式。
- 全量测试修复：大图 Blob URL 去重复用；显式浅色模式清除原有 dark 标记并在恢复时还原；新版 main 补充自有 `data-app-shell-main-surface` 兼容标记；注入前去掉 CSS 首部 BOM。移除了会复制大图的变量展开代码。
- `argentina-vs-spain-final` 的主图只在存在 `section.group/home-suggestions` 时显示。矩阵具备这个条件；其他首页布局仍可能不显示，未擅自放宽原主题规则。
- 真实 Codex 保持 GPT看板娘，仅重新应用同一主题验证最终 Blob 路径：一份图片，解码为 1672×941，样式文本约 18 KB，主面板深蓝色。没有逐一切换用户的完整桌面应用，矩阵截图不等于全部桌面页面的视觉验收。

结果：`test-output/theme-gallery-browser/run-20260921-010724/report.html`（可浏览截图），同目录 `report.json`、`browser-results.json` 和 `per-theme/` 为逐项证据。资源与预览结果见 `test-output/all-theme-resource-integrity.json`、`test-output/all-theme-offline-report.json`，实机 Blob 验证见 `test-output/theme-live-blob-verified.jsonl`。

复测命令：`python tests/theme_gallery_browser.py`；报告生成：`python tests/gallery_report.py <运行结果目录>`。最终 `build.ps1 -Test`、`python tests/skin_mock.py`、`python tests/theme_runtime_browser.py` 通过，正式程序已重新编译到 `bin/CodexCompanion.exe`。重新打开配置工具即可使用新版；本次不涉及 Docker 镜像或容器，无需 Docker 重建、重启。

## 不再展示设计参考图（2026-09-21）

按用户要求，图库服务在加载与条目重新查找时排除 `kind=skin` 的 52 项设计参考。原始目录分页读取保持完整，界面的搜索、计数和分页使用过滤后的列表：74 项主题、7 页，其中 67 项可直接应用，3 项缺资源不可用，4 项为网站登录后下载的手动安装压缩包主题。

`build.ps1 -Test` 通过，正式程序已重新编译。`--gallery-preview-live` 实际窗口截图显示“可应用 67 / 共 74”和“第 1/7 页”，见 `test-output/gallery-themes-only.png`。重新打开 `bin/CodexCompanion.exe` 生效，无需重启 Codex 或 Docker。

## 登录存档包接入（2026-09-21）

用户下载的 Miku、Trump、波奇酱、gandum 四个 ZIP 已转成内置离线包。图库现在为 **71 项可应用 / 74 项主题**；另外 3 项仍因原包缺资源而不可用，52 项设计参考仍不展示。转换入口为 `python tests/bundle_downloaded_archives.py <压缩包目录>`，来源 ZIP 与转换结果哈希记录于 `assets/theme-gallery/archive-provenance.json`，原许可和声明随离线资源保留。没有执行压缩包内的安装脚本。

Miku 和 Trump 补充了目录 ID 选择器及现代主面板图层兼容。波奇酱和高达沿用原 CSS、图片、配色与装饰，由 `DreamThemeRuntime.cs` 提供经过审查的固定主题适配；高达保留照片卡片拖动、缩放、旋转。切换、恢复会清理自有节点和监听器；不读写原包 localStorage，不运行归档 JavaScript。新版页面结构与原作者环境不同，不承诺逐像素复刻。

验证：`build.ps1 -Test`、`python tests/skin_mock.py`、`python tests/theme_runtime_browser.py`、`python tests/dream_theme_browser.py` 通过。四包矩阵使用 `$env:MIDWEB_THEME_IDS='miku,trump-maga-presidential,theme-1786780354717,gandum'; python tests/theme_gallery_browser.py`，4/4 通过。测试参数采用与图库按钮一致的目录 ID，避免内部 manifest ID 导致假通过。

四包截图和报告：`test-output/theme-gallery-browser/run-20260921-020026/report.html`。Dream 路由、拖动/缩放/旋转、普通主题切换、同名外部节点保留和恢复清理证据：`test-output/dream-theme-browser/run-20260921-015716/dream-results.json`。实际图库截图：`test-output/gallery-archives-bundled.png`，显示“可应用 71 / 共 74”。这些是隔离 Edge 页面与本工具窗口测试，没有切换或重启用户当前 Codex，仍需用户在实际桌面布局下验收。

正式输出已重新构建为 `bin/CodexCompanion.exe`，重新打开配置工具后搜索上述主题并点击“应用”。本次不涉及 Docker，无需重建镜像或重启容器。
