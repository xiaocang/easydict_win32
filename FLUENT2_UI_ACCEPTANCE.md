# Fluent 2 全窗口原生 UI：实现与验收

## 本轮实现（2026-09-05）

历史详情结果星标：用户日志未发现收藏／SQLite 异常，偶发点击无响应尚未直接复现。检查发现收藏通知会重建详情，而异步点击处理仍访问可变详情状态；现改为当前未过期历史查询原地更新收藏状态，固定写入查询 ID、拦截同结果并发点击、校验详情及状态请求代次，并在详情数据和收藏状态同时就绪后发布。过期查询仍走列表刷新以保持清理规则。标准／Minimal renderer 增加点击入口日志，宿主记录请求、写入完成、忽略原因和异常，不记录原文。新增 `History_ResultFavorite_PointerToggleSurvivesRefreshAndReentry`：旧实现正常星标切换 2 项通过（`favorite-before.trx`），但控件身份保持检查 2 项失败（`favorite-controls-before.trx`）；修复后 Dark／Minimal 2 项通过（`favorite-controls-after.trx`），各验证 4 次实际鼠标切换及返回翻译后重入的持久化状态。原生构建 0 警告、0 错误。本轮未重跑完整视觉矩阵，也不能据此断言该竞态就是用户那次无响应的唯一原因。

设置宽屏固定分类栏：≥960 DIP 时页头和左侧分类保持固定，右侧内容独立滚动；左侧超高时可单独滚动，关闭向外层的滚动传递。窄屏保留原有换行标签和整体滚动；宽窄重排保留选中分类及右侧位置，切换分类重置内容到顶部。原生构建 0 警告、0 错误，Light／Dark／Minimal 三项 `Settings_WideNavigationStaysFixedWhileDetailsScroll` 通过，见 `settings-fixed-navigation-final.trx`。验证右侧到底及继续滚轮时分类／返回按钮坐标不变、640 DIP 标签恢复、返回宽屏保持滚动位置及分类切换；早期测试持有重排前的标签实例而失败，已改为重新查询控件。截图位于 `artifacts/ui-screenshots/fluent2-settings-fixed/`，已查看深色右侧到底截图，未替换基线。

深色图标裁切修复：用户截图确认上一版存在裁切；`DrawImageUnscaled` 受位图 DPI 影响，不能保证像素对齐。新增四角保留测试在旧实现的 48／144／192 DPI 下失败，96 DPI 通过（`icon-dpi-before.trx`）。按用户要求移除运行时绘制／HICON 转换，从同一张 `AppIconSource.png` 预生成 `Assets/Branding/Dark/` 的七个尺寸 PNG、多尺寸 ICO 和托盘 PNG。生成器使用明确的源／目标像素矩形；窗口和托盘只加载对应文件。原生构建 0 警告、0 错误，12 项图标测试通过（`icon-dpi-fixed.trx`）；已查看 256 像素资源，四边和右下角彩色标记完整。旧主题用例只验证页面配色，不能作为图标无裁切的证据。

修复后的 WinUI 动态主题用例通过（`icon-dpi-ui-final.trx`）。已查看 `artifacts/ui-screenshots/fluent2-icon-dpi-fixed/` 中应用深色＋系统浅／深色两张全屏截图，标题栏和任务栏图标均完整；截图含其他桌面内容，只用于本地诊断，不作为批准基线。托盘图标位于折叠菜单内，本轮不宣称已完成托盘像素验收。资源重新生成方法见 `Assets/Branding/Dark/README.md`。

以下运行时变体记录已被上述预生成资源方案替代：

标题栏／托盘深色图标：Main、Mini、Fixed 标题栏按应用主题选择图标，托盘独立读取 `SystemUsesLightTheme`，应用指定主题时仍响应系统通知。原生运行时为原图添加一像素浅色轮廓，保留外围透明和品牌色；深色变体缓存为一个进程生命周期图标，托盘替换时释放旧实例。原始图稿、打包资源未改动。
验证：`themed-icons-unit.trx` 8 项通过（主题选择及像素颜色／透明度），`themed-icons-ui.trx` 现有应用深色／系统浅色宽窄布局与系统动态切换用例通过。原生构建 0 警告、0 错误；单元测试项目存在原有 `ModelCatalogCacheServiceTests.cs` CS8602 警告。已查看 `artifacts/ui-screenshots/fluent2-themed-icons/settings-dark-on-light-1280.png` 中的深色标题栏图标；本轮未捕获托盘区域、未做托盘像素基线断言，未重跑浮窗或高对比视觉矩阵。

Main 双栏滚动补充：关闭外层纵向滚动和左右栏向外层的滚动传递，左侧能放入视口时固定显示，超高时仍可独立滚动到语言操作栏；布局变化前保存滚动位置，进入双栏时重置外层偏移。新增 `Main_WideColumnsScrollIndependently` 使用固定 Provider 卡片验证右栏滚动到底及继续滚轮时左侧不动、宽窄切换保留草稿和右侧位置、短窗口下左栏独立滚动。测试不调用在线翻译。
最终 Light／Dark／Minimal 三项均通过，见 [滚动回归 TRX](artifacts/fluent2-all/test-results/fluent-main-scroll-verified.trx)；原生构建 0 警告、0 错误。截图位于 `artifacts/ui-screenshots/fluent2-main-scroll/`，已抽查短窗口与窄屏滚动图，未替换基线。早期运行包含 UIA 获取窗口超时，以及 Minimal 的 UIA 百分比操作未到达底部；最终窄屏同时以实际滚轮验证到底，保留原失败记录，不计为通过证据。

Main 翻译页随后按用户要求改为左对齐：保留 1280 DIP 最大宽度、360 DIP 输入栏和 16 DIP 栏间距，超过最大宽度的空间留在右侧。现有 `Main_ReflowsExistingInputAndSettingsCategories` 加入 1600 DIP 实际页面宽度，验证左边距、输入栏宽度及窄屏返回宽屏后的草稿保持。截图候选位于 `artifacts/ui-screenshots/fluent2-main-left/`。
本次原生构建 0 警告、0 错误；100% 实际 DPI 下 Light、Dark 紧凑及 Minimal 的布局回归均有通过记录，覆盖 400／640／959／960／1280／1600 DIP。Light、Dark 见 `fluent-main-left-verified.trx`，Minimal 首次在布局断言通过后遇到菜单未弹出的失败，单独复测见 `fluent-main-left-minimal-retry.trx`，不将前者整份报告标为全绿。已查看三个主题的 1600 DIP 截图，符合左对齐目标；未替换截图基线。系统缩放已恢复 200%。

设置页主题混用修复：在初始化前应用用户主题，设置资源提供独立 Light／Dark 字典，文本显式绑定主题前景色。沿用 `ThemeContrastTests`，将对比度断言更新到重构后的实际设置行标签，并增加卡片背景断言。四种系统／应用浅深色组合及中文应用深色、系统浅色的 1280／640 DIP 与系统动态切换均通过（2 项 UI 测试）；6 项主题回归配置单元测试通过，原生构建 0 警告、0 错误。
测试记录：[UI TRX](artifacts/fluent2-all/test-results/settings-theme-final.trx)、[单元 TRX](artifacts/fluent2-all/test-results/settings-theme-unit.trx)。已查看[中文深色设置截图](artifacts/ui-screenshots/settings-theme-fix/settings-dark-on-light-1280.png)及系统深色／应用浅色截图，卡片与文字配色正确。初始隔离运行未稳定重现用户截图，但加强后的现有矩阵实际捕获了反向组合的白底白字失败，修复后通过；候选未替换批准基线。

本轮覆盖 Main 翻译、长文档、Mini、Fixed、设置及保存项整合。采用最新透明图标，沿用已有数据与查询服务。
后续按用户要求恢复结果标题栏原字号：Provider 12 DIP、耗时／状态 10 DIP，保留原有字体缩放行为；此前截图候选尚未刷新此字号修正。
设置布局随后按用户要求调整：窄屏恢复原有图标加文字标签并自动换行，宽屏保留左分类／右内容；页面在最大宽度之外仍左对齐。此前下拉分类截图不代表当前窄屏设计。
此次调整的原生构建为 0 警告、0 错误；`fluent-settings-tabs.trx` 三项 UI 回归通过（Light、Dark 紧凑、Minimal），验证 400 DIP 标签换行、640 DIP 横向标签、宽屏左右分栏、1600 DIP 页面左对齐及分类保持。新截图位于 `artifacts/ui-screenshots/fluent2-settings-tabs/`，已查看 Dark 的窄屏和宽屏左对齐截图。
以下是本轮证据；后文保留前一轮保存项记录，不将旧测试计入本轮结果。

| 区域 | 实现与设计差异修正 |
| --- | --- |
| Main 翻译 | 960 DIP 起左侧 360 DIP 输入、右侧自适应结果，间距 16；不足断点上下排列；最大 1280。复用原控件，恢复分栏／堆叠滚动偏移，保留输入和查询状态 |
| 公共组件 | `FluentPresentation` 原生表面、`FluentLanguageBar` 单份语言与查询栏、`FluentSettingRow` 设置行、`ResultMessageView` 反馈；宿主继续独立管理业务状态 |
| 结果卡 | 标准／Minimal 的成功结果常驻标题复制，折叠仍可用；操作区不随悬停出现；正文 14 × FontScale；无结果时 Minimal 不展开空正文 |
| Mini／Fixed | 标准／紧凑留白 12／8，语言栏共用；保留检测语言和状态。Mini 更多菜单进入 Main，Fixed 保持无保存项导航；隐藏与真正关闭生命周期分离 |
| 长文档 | 输入、常驻语言与服务、输出设置、进度和任务历史保留；输入方式、并发、页码收进更多选项，宽窄重排和切换模式不丢参数 |
| 设置 | 七个原分类不变；宽屏 192 DIP 分类栏，窄屏恢复原有图标文字标签并换行；页面左对齐；设置行按空间横排／堆叠；修正语言展示排序、默认自动查询值引起的误报修改 |
| 高对比 | 公共资源与设置页独立高对比字典使用系统颜色；消除设置页局部颜色覆盖导致的白底和文字衬底 |
| 保存项 | 沿用现有分页、编辑、对比及状态恢复；品牌选中资源接入公共色，原生焦点框限定卡片区域，排除日期标题 |
| 富词典 | 仅正文使用 WebView2；ResizeObserver 处理延迟图片与字体，消息校验代次及有限高度；初始化过期保护，折叠按需创建，失败用原生文字与可关闭提示 |

## 本轮功能与技术边界证据

测试结果位于 `artifacts/fluent2-all/test-results/`，截图位于 `artifacts/ui-screenshots/fluent2-all/`。

| 验证 | 结果与证据 |
| --- | --- |
| 最终构建 | 原生未打包 WinUI 与 UIAutomation 项目构建均为 0 警告、0 错误；`git diff --check` 通过 |
| 单元测试 | `fluent-all-unit-final.trx` 162 项、`fluent-business-regressions.trx` 81 项均通过，去重共 237 项；涵盖设置、保存项、快照、预览、语言、查询、长文档及结果生命周期 |
| 主窗口 | `fluent-windows-ui-v7.trx` 中 Main 三项通过：Light 标准、Dark 紧凑、Minimal；1280/960/959/640/400 DIP，布局、草稿、长文档参数、设置分类及返回 |
| 富词典 | `fluent-windows-ui-v4.trx` 中三个字号 0.85/1.0/1.4 通过；等待延迟图片实际增加高度，再折叠并复制。`fluent-final-dpi100.trx` 中不可用运行时的文字回退与复制通过 |
| 保存项 | `fluent-windows-final-regressions.trx` 中 Light 200、Dark 5000 通过；Minimal 200 在 `fluent-final-dpi100.trx` 复验通过；包含分页、虚拟化、双列／堆叠对比、折叠复制、收藏编辑与按需加载 |
| Mini | `fluent-windows-ui-v6.trx` 中标准／紧凑两项通过，包含从设置恢复主窗口及草稿保留 |
| Fixed | `fluent-fixed-final.trx` 两项通过：实际原生窗口可见性、失焦不隐藏、切换隐藏重开、多行草稿、无保存项入口 |
| 高对比 | `fluent-final-dpi100.trx` 中 Main／长文档／设置两种密度通过；保存项四个宽度及两种密度见 `fluent-windows-final-regressions.trx`；高对比测试恢复原系统状态 |
| 控件回收 | `fluent-final-completion.trx` 的富词典快照参与 12 次导航、详情、对比及三种主题切换通过：ActiveResults=3、AliveResults=3、RealizedRows=23；原生文字结果在 `fluent-windows-final-regressions.trx` 中也通过 |
| 最终交互复验 | `fluent-final-completion.trx` 共 4 项通过：最新标题／操作尺寸的 Main、长文档输入方式和输出方式切换、富词典回收 |
| 实际 DPI | 本轮 100% 的布局与交互用例见上述记录；`fluent-final-dpi150.trx` 7 项通过，覆盖 Main 三种主题及两种密度的 Mini／Fixed。每个 Main 用例均验证 1280/960/959/640/400 DIP，并包含长文档和设置 |
| 原生边界 | 页面 XAML 和 C# 宿主保留；WebView2 创建点仅在 `ServiceResultItem.EnsureDictionaryWebView`。复制、标题、操作区和 InfoBar 不依赖网页。未增加数据库迁移或公共服务 API |

运行验证修正了 XAML 生成连接编号错误、WebView2 COM 包装对象引用比较误拒有效尺寸消息、设置误报修改与高对比资源覆盖。
测试侧修正了不可访问 Border 的 UIA 查询、收藏异步切换过早断言、将销毁误当隐藏，以及隐藏窗口 `IsOffscreen` 返回旧值；Fixed 现验证原生 `IsWindowVisible`。
TRX 中保留的早期失败用于定位记录；只有上表明确列出的通过项作为证据。

## 本轮视觉与覆盖边界

已实际查看宽屏 Main、400 DIP Minimal、宽窄长文档、宽窄设置、高对比设置、Mini 菜单、Fixed 重开、双列对比、富词典主题切换、延迟图片及回退截图。
共整理 [11 张新截图候选](artifacts/ui-screenshots/fluent2-all/baseline-candidates/README.md)，保留各自原始文件来源。
截图是候选，未替换任何已批准基线。原生 SelectorBar 下划线、Minimal 线框、高对比系统配色属于计划允许的视觉差异。

- 本轮不运行依赖真实 Provider 的完整 PDF 翻译／输出作业；队列、重试和检查点以现有业务测试验证，模式与参数由 UI 测试验证。
- 本轮显示环境为 1920×1080，常规缩放仅提供 100%、125%、150%、175%；尝试 200% 时没有该选项，未执行真实 200%。本轮结束已恢复原 100%；前一轮 200% 记录不算本轮覆盖。
- 不将截图存在等同于像素差异验收；没有穷举所有系统高对比配色、全部界面语言及全部主题×密度×DPI 的笛卡尔积。
- 新增资源键已进入 15 个本地化文件，部分语言新文案仍使用英文回退；长文档原有英文文案继续保留。
- 新图标实际窗口标题栏可见且无黑色矩形底；托盘和任务栏的每种系统表面仍需单独人工核验，不能以标题栏截图代替。
- 未执行 MSIX 发布、自动 Git 提交或批准截图基线。

## 前一轮保存项验收（历史记录）

范围为提交 `7b15a066` 后的三批重构：公共布局与列表、详情与收藏、Mini 与历史设置。
以 [PLAN.md](PLAN.md) 的 WinUI 3 原生方案为准；四张参考图仅提供视觉方向。
上述 UI 验收使用提交 `7b15a066` 的透明品牌图标。
随后按用户提供的 `e3b344d0-f932-4bd0-a5d1-dc908ecc998e.png` 更新品牌原图、ICO 和 Windows 各尺寸资源，保留原始透明通道。
本文截图候选仍为换图标之前的 UI 验收记录，不作为新图标的截图基线。

## 与参考图的差异及修改

| 元素 | 原实现与设计方向的差异 | 本轮修改 |
| --- | --- | --- |
| 返回与导航 | 返回翻译入口不突出，切换缺少一致选中态 | 常驻带文字的「返回翻译」；56 DIP 原生导航；400 DIP 收为菜单；详情另有「返回列表」 |
| 列表与详情 | 列宽、留白与宽窄切换不一致 | 宽屏 360 DIP 列表加自适应详情；960 以下逐页显示；分别滚动，缩放保留选择 |
| 分类 | 标签挤压，缺少自然的窄屏形式 | 原生 SelectorBar，按文本实际宽度决定是否改用 ComboBox |
| 列表卡片 | 层级密集，选中与悬停不清楚 | 14/12 DIP 正文与元信息、8 DIP 圆角、浅强调色选中底与侧标、原生键盘焦点 |
| 摘要与标签 | 固定行数二次裁切正文，长标签溢出 | 保留 100 个用户可见字符算法，移除 MaxLines；正文、元信息和标签自然换行 |
| 日期与搜索 | 分组不完整，搜索结果与预览可能不对应 | 本地日期四组、分页合并与跨午夜刷新；搜索取消分组，增加命中高亮与 Provider 预览优先级 |
| 原文操作 | 窄屏按钮拥挤 | 原生 CommandBar 自动溢出，保留复制、整次收藏、重新翻译和更多操作 |
| 结果与对比 | 普通结果和双服务对比布局混用 | 普通结果占满详情宽度；对比最多两个，详情宽度 640 DIP 起等宽并排，否则上下排列 |
| 折叠与复制 | 折叠后复制不可达，Minimal 自动重新展开 | 两种 renderer 均保留标题区复制；稳定的播放/收藏/更多操作区；约 3 秒页内反馈并播报 |
| 收藏编辑 | 备注编辑控件常驻，图钉与收藏语义混淆 | 默认摘要，显式编辑/保存/取消；未保存切换保护；星标收藏、图钉置顶 |
| 单结果收藏 | 其他结果缺少层级 | 优先目标结果；其他结果带数量并折叠，按需创建，切换条目后恢复折叠 |
| 深色与高对比 | 折叠标题对比不足，下拉框浅底使箭头不清楚 | 页面专用主题资源；高对比改用系统颜色，补齐下拉框背景和文本状态 |
| Mini | 标准与紧凑入口不一致 | 两种模式都有更多菜单；恢复并激活 Main，保留 Mini 草稿；Fixed 维持原边界 |
| 历史设置 | 层级、说明和反馈不完整 | 「常规 → 历史记录与隐私」；标签与控件分列；补齐保留规则、期限校验和保存/清理反馈 |

运行验收同时修正两处问题：分页预取延后到下一次 UI 调度，避免在原生布局过程中修改列表；
搜索高亮颜色按页面缓存，在主题变化时刷新，避免逐个文本控件重复遍历原生主题资源、拖慢重返页面。

## 有意保留的区别

- 分类采用原生下划线选中态，而非参考图中的自绘胶囊按钮。
- Minimal 保留现有线框风格；高对比遵循 Windows 当前配色。
- 翻译与词典保持合并分类；历史仅有成功结果，移除成功徽标和「仅显示成功」。
- Mini 入口打开主窗口，不实现参考图中的内嵌保存项面板。
- 此前保存项一轮未包含主翻译结果页、其他设置、Fixed、长文档；本轮已纳入全窗口重构。

## 验证结果

2026-09-05，本地 Windows 交互桌面，原生未打包 WinUI 应用。

| 验证组 | 结果与证据 |
| --- | --- |
| WinUI 构建 | 0 警告、0 错误；`artifacts/fluent2/final-build.log` |
| 保存项、快照、预览、设置单元测试 | 134 项通过；`fluent2-unit.trx` |
| 现有原生入口与设置 | Main 历史/收藏、窄屏、Mini/Fixed 边界、设置持久化共 5 项通过；`fluent2-native-ui.trx` 中对应五项 |
| 实际 DPI 与页面宽度 | 100%、150%、200% 各 3 个 Light/Dark/Minimal 用例通过；每个用例测 400/640/960/1280 DIP；`fluent2-final-dpi100/150/200.trx` 中 SeededHistory 三项 |
| 密度 | 100%/200% 标准、150% 紧凑；200% 另有紧凑搜索及 Mini 用例 |
| 高对比 | 实际启用 Windows 高对比，标准/紧凑、四个宽度通过；`fluent2-final-refinements.trx` 的 HighContrast 用例 |
| Mini | 标准/紧凑两项通过；从最小化的设置页恢复 Main、切换历史/收藏并保留草稿；`fluent2-final-interactions.trx` 中两项 MiniEntry |
| 历史设置 | 改期限、关闭、重启保持值、重新启用通过；`fluent2-final-refinements.trx` 的 HistorySettings 用例 |
| 搜索与状态恢复 | 高亮性能修复后通过；命中 Bing、独立分类状态、四种宽度、返回重入、无结果与清除搜索；`fluent2-final-search-lifetime.trx` |
| 主题与控件回收 | 修复后 12 次导航/详情/对比循环及 Dark/Minimal/Light 切换通过；AliveResults=3、ActiveResults=3、LoadedRows=25、RealizedRows=20；同上 TRX |

TRX 根目录：`artifacts/fluent2/test-results/`。上表按具体用例引用：
旧组合运行包含已修复的搜索失败，不能把整个旧 TRX 当作全绿；
最终搜索与生命周期运行两项全部通过。
跨 DPI 的 19 次相关 UI 用例通过记录汇总于
[accepted-ui-results.json](artifacts/fluent2/accepted-ui-results.json)。

内容和数据覆盖：中文、emoji、组合字符、长单词；200/5000 条固定数据的游标分页与虚拟化；
显式 Provider 与命中 Provider 优先级；部分成功、全部失败、取消、历史关闭、保留清理、整次/单结果收藏。
UI 用例断言对比卡片真实坐标、折叠后复制、备注取消与未保存切换保护、其他结果懒加载和折叠复位。
本地日期边界及 DST 由展示单元测试验证。

每个 DPI 目录的 `layout-metrics.jsonl` 同时记录原生窗口 DPI 和页面 `ActualWidth`，
目标宽度误差小于 0.5 DIP。系统缩放已恢复原来的 200%，高对比已恢复关闭。

## 截图复核

截图根目录：`artifacts/ui-screenshots/fluent2/`。
已逐张查看的 16 张候选位于 [baseline-candidates](artifacts/ui-screenshots/fluent2/baseline-candidates/)；
[索引](artifacts/ui-screenshots/fluent2/baseline-candidates/README.md) 可逐张打开，
[manifest.json](artifacts/ui-screenshots/fluent2/baseline-candidates/manifest.json) 记录捕获时间和 SHA-256。

候选覆盖 Light/Dark/Minimal、高对比、标准/紧凑、四种宽度、三个实际 DPI，以及收藏、对比、Mini、设置和无结果状态。
复核发现并修正：深色其他结果标题对比不足、窄屏隐藏列间距、高对比下拉框浅底、Mini 弹出菜单被截图边界裁切。
当前候选未发现阻塞使用的裁切、遮挡或配色问题；设置图中的其他设置为滚动上下文。

未作为候选：`fluent2_failed_*` 诊断图、早期被其他窗口遮挡的图、旧的仅按名义窗口宽度调整的图。
搜索超时诊断图虽然显示页面，但对应失败上下文，不能用作通过证据。
其余批量截图采用抽样复核，未宣称逐张检查全部产物；没有替换已批准基线，也未进行像素差异阈值验收。

## 覆盖限制与复现

- DPI、主题和宽度按上述组合验证，未穷举全部组合、系统高对比配色或用户正文字号。
- 15 个现有语言资源文件均补齐新键；简体中文已翻译，其他新文案仍有英文回退。
- 本轮验证未打包应用，不包含 MSIX 打包与发布。
- 控件订阅和回收经过有限次数检查，不等同于长时间压力运行。

```powershell
dotnet build dotnet/src/Easydict.WinUI/Easydict.WinUI.csproj -c Debug -p:Platform=x64 -p:EasydictUiTestBuild=true -p:WindowsPackageType=None -p:AppxPackage=false --no-restore
dotnet test dotnet/tests/Easydict.WinUI.Tests/Easydict.WinUI.Tests.csproj --no-restore --filter 'FullyQualifiedName~SavedItems|FullyQualifiedName~SavedResultPreview|FullyQualifiedName~QuerySnapshotDraft|FullyQualifiedName~SettingsServiceTests'
```

UI 自动化需交互桌面，设置 `EASYDICT_EXE_PATH` 指向构建输出、`SCREENSHOT_OUTPUT_DIR` 指向截图目录。
使用 `EASYDICT_EXPECTED_DPI=100/150/200` 校验真实系统缩放，`EASYDICT_UIA_COMPACT=1` 验证紧凑密度。
高对比用例需 `EASYDICT_UIA_CONTRAST_ACCEPTANCE=1`；测试结束由 scope 恢复原状态。
共享桌面运行应排除 `DesktopSetting=HighContrast`，并避免其他 UI 自动化同时抢占焦点。
