# WinUI 3 ↔ winfluent-rs 接口对比与缺陷分析

> 更新日期：2026-06-17
> 目的：以 WinUI 3（Windows App SDK）官方控件/接口面为基线，核对 `lib/winfluent-rs`（`win_fluent` crate）当前公开接口能否提供**完整的 UI 接口**；在两者思路不同处借鉴 WinUI 3 的接口设计，梳理 winfluent-rs 的接口清单与缺陷。
>
> 基线来源：Microsoft Learn 官方文档（`learn.microsoft.com/windows/apps/develop/ui/controls/` 等，约 70 个一方控件 + 8 项 XAML 框架概念）。
> 被测对象：`lib/winfluent-rs/crates/win_fluent/src/`（lib.rs 导出 + view.rs 等源码）。

---

## 0. 总体结论

- **对 EasyDict 这个 reference app 而言，当前接口"够用"**：应用实际消费的控件（`text`×1381、`column`/`row`、`button`、`text_editor`、`card`、`combo_box`、`toggle_switch`、`progress_ring`、`checkbox`、`dialog`、`expander`、`slider`、`flyout_button`、`capture_overlay` 等）winfluent-rs 都已提供。
- **作为"WinUI-like 通用框架"而言，接口尚不完整**：覆盖了 WinUI 约 30+ 个高频控件，但在**布局原语、通用集合/虚拟化列表、富文本/Web 内容、单选框、通用 Tooltip/Flyout** 等维度存在结构性缺口。
- **设计理念是 Elm/iced（View Token + Message），不是 XAML（DependencyProperty + Binding）**。这是有意为之，多数差异是"思路不同"而非缺陷。但 XAML 在三处接口设计上值得借鉴：**Grid 布局模型**、**ItemsRepeater/ListView 的数据虚拟化抽象**、**VisualStateManager 的状态自动化**。
- **判断**：winfluent-rs 能成为 EasyDict 的完整 UI 接口，但需补齐下文 **P0 / P1 缺陷**；其中 `Grid` 与 **通用虚拟化列表** 是最值得优先投入的两项。

---

## 1. 设计理念对比（思路差异，非缺陷）

| 维度 | WinUI 3 (XAML) | winfluent-rs (View Token) | 评价 |
| --- | --- | --- | --- |
| UI 描述 | XAML 标记 + 控件树 | `View<Message>` token 树（builder 链式 API） | 思路不同，token 可在不启动窗口时 snapshot 测试，是核心资产 |
| 状态/数据流 | DependencyProperty + `{x:Bind}`/`{Binding}`（双向绑定、`INotifyPropertyChanged`） | 应用持有 state，`update(msg)` 驱动，单向数据流 | Elm 模型；无双向绑定，状态需显式回传 |
| 样式 | `Style` / `ControlTemplate` / `ResourceDictionary` / `{ThemeResource}` | `ThemeTokens`（40+ 颜色 token）+ `FluentStyle`（Tailwind 风格 utility） | **不能重写控件 visual tree**，只能改 token；深度自定义受限 |
| 视觉状态 | `VisualStateManager` + `VisualState` + `AdaptiveTrigger` 自动切换 | `ControlState{hovered,pressed,focused,selected,validation}` **手动传入** | 见 §5 P1：状态自动化是接口体感差距 |
| 事件 | RoutedEvent（**仅冒泡**，`Handled` 截断，固定集合）+ `ICommand` | `Action<Message>`（`Message`/`TextInput`/`BoolInput`/`NumberInput`/`SelectionInput`）+ `command()` | 无事件冒泡/隧道；`pointer_region` 提供低层指针捕获 |
| 主题 | Light / Dark / HighContrast + Mica/Acrylic backdrop | `ThemeMode{System,Light,Dark,Minimal,HighContrast}` + `BackdropKind{Solid,Mica,Acrylic}` | ✅ 对齐，含 HighContrast 与 backdrop |
| 无障碍 | `AutomationProperties` + `AutomationPeer` + UIA pattern | `A11yRole`(14 种) + `A11yHint` + `resolve_accessibility_tree()` | 角色集偏少（见 §5） |
| 动画 | Storyboard / 隐式 Composition / Connected animation | `Transition` + `Easing`(Fluent 曲线) + `CollapseTransition` | 有 Fluent 时长常量；无 storyboard/connected animation（多数非必需） |
| 异步 | `async/await` + `Task`，UI 线程 Dispatcher | `Task<Message>` + `Loadable<T,E>{Idle,Loading,Loaded,Failed}` | ✅ Loadable 是很好的 UI 异步状态机抽象 |

**结论**：理念差异处不必"照搬 XAML"，但 **Grid 布局**、**集合虚拟化抽象**、**视觉状态自动化** 这三处 WinUI 的接口设计更成熟，应借鉴（详见 §6）。

---

## 2. 控件映射总表（WinUI 3 基线 → winfluent-rs）

图例：✅ 已覆盖 · 🟡 部分/受限 · ❌ 缺失（按对 EasyDict 的相关性给出优先级）

### 2.1 布局面板与容器
| WinUI 3 | winfluent-rs | 状态 | 备注 |
| --- | --- | --- | --- |
| StackPanel | `column` / `row` | ✅ | 含 padding/spacing/align/distribution/max_* |
| **Grid** | — | ❌ **P0** | 无 2D 网格、无 Row/Column 定义、无 star 尺寸、无 span；当前只能靠嵌套 row/column 模拟 |
| Canvas | — | ❌ P2 | 无绝对坐标定位（`overlay` 仅按 align 分层） |
| RelativePanel | — | ❌ P2 | 无相对布局 |
| WrapPanel / VariableSizedWrapGrid | `wrap` | ✅ | `max_columns`/`spacing`/`run_spacing` |
| Border | `card(Surface)` | 🟡 | 无独立 border 原语（圆角/描边/背景包裹单子元素） |
| ScrollViewer / ScrollView | `scroll_view` | ✅ | `horizontal`/`vertical`/`ScrollPolicy{Auto,Always,Never}` |
| Viewbox | — | ❌ P2 | 无等比缩放容器 |
| Expander | `expander` | ✅ | 头部状态/trailing/on_toggle 齐全 |
| SplitView | `navigation_view` | 🟡 | 导航即 split-view 形态，但无独立可编程双栏 |
| TwoPaneView | `adaptive_switch` | 🟡 | 宽窄分支切换，非双屏/双栏布局 |
| SemanticZoom | — | ❌ P2 | 非必需 |
| TitleBar | `title_bar` | ✅ | caption 控制/drag/min/max/close |

### 2.2 按钮
| WinUI 3 | winfluent-rs | 状态 | 备注 |
| --- | --- | --- | --- |
| Button | `button` | ✅ | 含 `.subtle()/.link()/.chip()/.tile()/.icon_only()` 等变体 |
| HyperlinkButton | `button.link()` (`ButtonKind::Link`) | ✅ | |
| DropDownButton | `flyout_button` | ✅ | items + on_select |
| RepeatButton | — | ❌ P2 | 无"按住连发" |
| SplitButton | — | 🟡/❌ P2 | 可用 flyout_button 近似，无"主操作+菜单"二段式 |
| ToggleButton | `button.chip().selected()` | 🟡 | 无语义化的 on/off 按钮（按下保持态） |
| ToggleSplitButton | — | ❌ P2 | |

### 2.3 集合 / 列表 / 表格
| WinUI 3 | winfluent-rs | 状态 | 备注 |
| --- | --- | --- | --- |
| ListView | `result_list` | 🟡 **P0** | **仅翻译结果专用**；无通用 item 模板的虚拟化列表（历史记录、词典词条、语言列表、长文段落均无处可用） |
| GridView | — | ❌ P1 | 无网格集合 |
| ItemsRepeater | — | ❌ **P0** | 无通用数据驱动虚拟化原语（这是 ListView/GridView 的底座） |
| ItemsView | — | ❌ P1 | |
| FlipView | — | ❌ P2 | |
| TreeView | — | ❌ P1 | 词典/设置层级、长文档大纲可能需要 |
| ListBox | `combo_box` | 🟡 | 无"常开"单选列表 |
| DataGrid (Toolkit) | — | ❌ P2 | 一方亦无 |

### 2.4 文本控件
| WinUI 3 | winfluent-rs | 状态 | 备注 |
| --- | --- | --- | --- |
| TextBlock | `text` | ✅ | `TextStyle` 11 档 + 对齐/margin |
| TextBox | `text_editor` | ✅ | 多行/placeholder/min-max height/on_key |
| PasswordBox | `text_editor.password()/.secure()` | ✅ | |
| **RichTextBlock** | — | ❌ **P1** | 无富文本：无 inline run、链接、内嵌图片 —— **词典模式 / MDX 富文档** 的硬需求 |
| RichEditBox | — | ❌ P2 | 无富文本编辑 |
| AutoSuggestBox | — | ❌ P1 | 无 as-you-type 建议（搜索、语言选择器） |
| NumberBox | `slider` / `Action::NumberInput` | 🟡 P1 | 有数值输入回调，但无带 spin/校验/表达式的数值输入控件 |

### 2.5 选择 / 取值
| WinUI 3 | winfluent-rs | 状态 | 备注 |
| --- | --- | --- | --- |
| CheckBox | `checkbox` | ✅ | （无第三态 indeterminate） |
| **RadioButton / RadioButtons** | `FlyoutMenuItem::radio`（仅菜单项） | ❌ **P1** | **无独立单选控件**；radio 仅作为 flyout 菜单项种类存在。Settings 迁移计划明确需要 |
| ComboBox | `combo_box` | ✅ | label/placeholder/selected/on_change |
| ToggleSwitch | `toggle_switch` | ✅ | header/on_toggle |
| Slider | `slider` | ✅ | range/step/on_change |
| RatingControl | — | ❌ P2 | |
| ColorPicker | — | ❌ P2 | |

### 2.6 日期 / 时间
| CalendarView / CalendarDatePicker / DatePicker / TimePicker | — | ❌ P2 | EasyDict 暂无需求 |

### 2.7 导航
| WinUI 3 | winfluent-rs | 状态 | 备注 |
| --- | --- | --- | --- |
| NavigationView | `navigation_view` | 🟡 **P1** | 仅 `id/selected/content/on_select`；**缺** PaneDisplayMode（Left/Top/LeftCompact/LeftMinimal）、header、footer menu items、settings item、back button |
| TabView | — | ❌ P1 | 多文档/长文档分页 |
| BreadcrumbBar | — | ❌ P2 | |
| Pivot | `adaptive_switch` | 🟡 | 非分段切换 |
| SelectorBar | — | ❌ P2 | |

### 2.8 菜单 / 浮出 / 对话框 / 命令
| WinUI 3 | winfluent-rs | 状态 | 备注 |
| --- | --- | --- | --- |
| ContentDialog | `dialog` | ✅ | `DialogKind{Content,Confirmation,Error}` + primary/secondary |
| CommandBar | `command_bar` | ✅ | compact/align/distribution |
| MenuFlyout | `flyout_button` | 🟡 | 浮出菜单需挂在按钮上，无独立 menu flyout |
| Flyout | `overlay` / `flyout_button` | 🟡 P1 | 无"任意内容 + 锚定到元素"的通用 flyout |
| Popup | `overlay` | 🟡 | 分层覆盖 + scrim + blocks_input |
| ToolTip | `button.tooltip()` | 🟡 **P1** | **仅按钮有 tooltip**；无通用元素 tooltip |
| TeachingTip | — | ❌ P2 | 无引导提示 |
| MenuBar | — | ❌ P2 | 工具型应用一般用托盘/命令栏代替 |
| CommandBarFlyout | — | ❌ P2 | |
| AppBarButton/Toggle/Separator | `button` 变体 | 🟡 | |

### 2.9 状态 / 信息
| WinUI 3 | winfluent-rs | 状态 | 备注 |
| --- | --- | --- | --- |
| ProgressBar | `progress_bar` | ✅ | value/indeterminate |
| ProgressRing | `progress_ring` | ✅ | active/size/label |
| InfoBar | `info_bar` | 🟡 **P2(导出缺陷)** | 控件已实现，但 `lib.rs` crate 根 **未 re-export**（仅 `prelude` 有 `info_bar`，且 `InfoBarBuilder`/`InfoBarToken` 两边都未导出）。见 §5 |
| InfoBadge | `status_badge` | 🟡 | 近似，无数字/dot 徽标语义 |
| PipsPager | — | ❌ P2 | |

### 2.10 媒体 / 图像
| WinUI 3 | winfluent-rs | 状态 | 备注 |
| --- | --- | --- | --- |
| Image | `image_bgra_file` | 🟡 P1 | **仅支持截图来的 BGRA8 文件**；无通用 file/URI 图源、无 Stretch 模式、无 icon 图像元素 |
| WebView2 | — | ❌ P1 | **词典 / MDX HTML 富内容** 可能需要；当前无 Web 宿主 |
| MediaPlayerElement / PersonPicture / AnimatedVisualPlayer | — | ❌ P2 | 非必需 |

### 2.11 其他
| SwipeControl / ParallaxView / AnnotatedScrollBar / RatingControl | — | ❌ P2 | 非必需 |

### 2.12 winfluent-rs 额外提供（WinUI 无直接对应，体现 Fluent/EasyDict 定位）
- `result_card` / `result_list`（翻译结果卡片 + 折叠动画 + 虚拟化）
- `capture_overlay` + `image_bgra_file`（OCR 截图覆盖层）
- `adaptive_switch`（宽窄响应式分支，对应 `AdaptiveTrigger`）
- `busy_overlay`（内容 + 加载遮罩，集成 `Loadable`）
- `settings_row`（Fluent 设置行）
- `pointer_region`（低层指针捕获：move/drag/wheel/escape，用于划词/拖拽）
- `lazy`（keyed 缓存子树）+ `overlay`（z 分层）
- 平台 token：托盘、热键、剪贴板、文件对话框、shell verb、协议注册、命名事件 IPC、屏幕捕获/窗口快照

---

## 3. winfluent-rs 接口清单（公开 API 速览）

> 完整方法签名见源码；此处为类别化清单，供缺陷核对。

- **布局原语**：`Length{Shrink,Fill,FillPortion,Fixed}`、`Alignment{Start,Center,End,Stretch}`、`LayoutDistribution{Start,SpaceBetween}`、`Edges`
- **布局控件**：`column`/`row`/`wrap`/`spacer`/`scroll_view`/`overlay`/`adaptive_switch`/`lazy`
- **基础控件**：`text`(+`TextStyle`11档)、`button`/`primary_button`(+`ButtonKind`10种)、`toggle_switch`、`checkbox`、`slider`、`text_editor`(+`TextEditorChrome`/`TextEditorKey`/键绑定)
- **选择/下拉**：`combo_box`(+`ComboBoxItem`)、`flyout_button`(+`FlyoutMenuItem`/`FlyoutMenuItemKind`)
- **进度/忙**：`progress_ring`、`progress_bar`、`busy_overlay`
- **状态/反馈**：`status_badge`、`info_bar`（导出不一致）、`ValidationState`/`ValidationSeverity`、`ControlState`
- **卡片/容器**：`card`(+`CardKind`)、`expander`、`settings_row`(+`SettingsRowKind`)
- **导航/多窗口**：`navigation_view`(+`NavigationItem`)、`page`、`title_bar`、`dialog`(+`DialogKind`)
- **结果专用**：`result_card`/`result_list`(+`ResultItem`/`ResultStatus`/`CollapseTransition`)、`service_result_*`(deprecated 别名)
- **OCR/图像**：`capture_overlay`(+`CaptureOverlayPhase`/`Rect`)、`image_bgra_file`
- **命令/输入**：`command_bar`、`command`(+`CommandPlacement`/`KeyboardAccelerator`)、`pointer_region`(+`PointerPosition`/`PointerWheel`/`PointerRegionAction`)、`Action`/`ActionKind`
- **主题**：`ThemeMode`、`Color`、`ThemeTokens`(+`AccentPalette`/`Typography`/`Spacing`/`CornerRadius`/`Stroke`/`Elevation`/`ControlMetrics`/`VisualEffects`/`Density`/`BackdropKind`)
- **窗口**：`WindowOptions`/`WindowId`/`WindowLevel`/`WindowFrame`/`WindowResizeMode`/`WindowPlacement`/`WindowScreenConstraint`/`WindowThemePreference`/`WindowCommand`
- **平台**：`Hotkey`/`HotkeyModifier`/`HotkeyKey`、`ClipboardFormat`、`FileDialogOptions`/`Filter`、`FolderDialogOptions`、`TrayMenu`/`TrayMenuItem`、`ShellVerb`、`ProtocolRegistration`、`ScreenCaptureRequest`/`Result`/`ScreenRect`/`ScreenWindow`、`NamedEventRegistration`、`PlatformCommand`
- **无障碍**：`A11yRole`(14)、`A11yHint`、`A11yNode`、`resolve_accessibility_tree`
- **i18n**：`LocaleId`、`LocalizedText`、`t()`、`I18n`/`I18nBundle`/`I18nArg`、`Localizer`
- **异步**：`Loadable<T,E>`、`Task`
- **运行时**：`Application` trait、`RuntimePlan`、`DesktopIntegrationPlan`、`RuntimeError`
- **动画**：`Transition`、`Easing`、`CONTROL_*_ANIMATION_MS`
- **订阅/事件**：`Subscription`、`SubscriptionKind`、`PlatformEvent`、`WindowEvent`
- **图标**：`IconToken` + 约 19 个内置 const（translate/camera/copy/...）
- **样式**：`FluentStyle`、`utility_scale`
- **测试/分析**：`view_schema`/`ViewSchema`/`SchemaNode`、`diff_views`/`ViewChange`、`WindowScreenshot`、`FrameCoalescer`/`TextStreamCoalescer`

---

## 4. EasyDict 实际消费 vs 接口供给

应用源码统计（`rs/crates/easydict_app/src/`）实际调用：`text`(1381)、`column`(105)、`row`(92)、`button`(75)、`text_editor`(33)、`card`(28)、`combo_box`(25)、`toggle_switch`(13)、`primary_button`(10)、`progress_ring`(7)、`checkbox`(7)、`progress_bar`(5)、`expander`(5)、`dialog`(5)、`title_bar`(4)、`wrap`(3)、`settings_row`(3)、`scroll_view`(3)、`adaptive_switch`(3)、`result_list`(2)、`pointer_region`(2)、`command_bar`(2)、`status_badge`(1)、`slider`(1)、`image_bgra_file`(1)、`flyout_button`(1)、`capture_overlay`(1)、`busy_overlay`(1)。

- 应用**未使用** `navigation_view`、`info_bar`、`result_card`(直接)、`overlay`(直接)、`lazy`(直接)。
- 现有需求被现有接口满足；缺陷主要在**未来页面**（词典富文本、历史/词条列表、设置单选、复杂布局）会暴露。

---

## 5. 缺陷清单（按优先级）

### P0 —— 结构性缺口，建议优先补齐
1. **无 Grid 布局原语**。只有 `column`/`row`/`wrap`，缺 WinUI 的网格主力布局（Row/Column 定义、`Auto`/star 尺寸、行列 span、对齐到网格线）。复杂设置页/主窗口只能用深层嵌套 row/column 模拟，易脆、难维护、难对齐像素。
2. **无通用虚拟化集合控件**。`result_list` 把虚拟化硬编码进"翻译结果"语义，缺一个**通用 `list_view`/`items_repeater`**（任意 item 视图 + 数据虚拟化）。历史记录、词典词条、语言列表、长文档段落都无处落地——这是性能 KPI（虚拟化列表）的接口前提。

### P1 —— 重要缺口，迁移到对应页面前需补
3. **无独立 RadioButton / RadioButtons**。radio 仅作为 `FlyoutMenuItemKind::Radio` 存在；Settings 迁移清单明确点名需要单选。
4. **无富文本 / Web 内容**（`RichTextBlock` / `WebView2`）。`text` 仅整块 `TextStyle`，无 inline run、链接、内嵌图片。**词典模式与 MDX 富文档是 EasyDict 的核心特性**，这是最可能阻塞主线的内容能力缺口。
5. **通用 Tooltip 缺失**。tooltip 仅挂在 `button` 上，无任意元素的 hover 提示（图标、结果项、设置项均需要）。
6. **NavigationView 能力过薄**。缺 PaneDisplayMode、header、footer/settings item、back button——而迁移计划把它作为 Settings 与 Main 的骨架。
7. **通用 Image 受限**。`image_bgra_file` 仅吃截图 BGRA8 文件，无 file/URI 图源、无 Stretch 模式、无通用图标图像元素（服务图标、语言旗帜等）。
8. **无 AutoSuggestBox / NumberBox 控件**。有 `Action::NumberInput` 回调但无成品数值输入；搜索/语言选择缺 as-you-type 建议。
9. **无通用 Flyout**（任意内容锚定到元素），`flyout_button` 仅菜单语义。

### P2 —— 次要 / 一致性 / 视情况
10. **`info_bar` 导出不一致（接口卫生）**：`pub fn info_bar` 在 `prelude` 导出，但 `lib.rs` crate 根未 re-export，且 `InfoBarBuilder` / `InfoBarToken` 两处都未导出。应统一到 `lib.rs` + `prelude` 并导出其 builder/token 类型。
11. **A11yRole 角色集偏少（14）**。缺 `Tab`、`TreeItem`、`RadioButton`、`Hyperlink`、`Image`、`ProgressBar`、`MenuItem`、`Tooltip` 等；自绘控件补齐后需同步扩充 UIA 角色映射。
12. **CheckBox 无第三态**（indeterminate）。
13. **缺 ToggleButton / SplitButton / RepeatButton / TabView / TreeView / Border(独立) / Viewbox** 等——按页面需求逐步评估，非全部必要。
14. **无 ControlTemplate 等价物**：只能改 `ThemeTokens`，无法重写单个控件的 visual tree；深度自定义控件时是灵活性上限（设计取舍，记录在案）。

### 设计性问题（需团队决策，非纯缺陷）
15. **`ControlState`（hover/press/focus）手动传入 vs WinUI 的 `VisualStateManager` 自动化**。需明确：runtime 是否自动管理指针/焦点态并回灌，还是应用层每帧自算并传入？若是后者，长期会带来样板代码与状态不一致风险。建议在接口层提供"自动视觉状态"默认值，仅在需要时允许覆盖。

---

## 6. 借鉴 WinUI 3 的接口改进建议

> 思路不同处不强求对齐，但以下三项 WinUI 接口模型更成熟，建议吸收其"接口形状"。

1. **引入 `grid` 布局（借鉴 Grid）**
   - 提供 `grid()` builder：`rows([Length...])`、`columns([Length...])`，子项用 `.cell(row,col)`/`.span(r,c)` 定位；`Length` 复用现有 `Fixed/Fill/FillPortion(=star)/Shrink(=Auto)`，语义已天然匹配 WinUI 的 `Auto`/`*`/定值。
   - 这是 P0-1 的直接解法，且与现有 `Length` 枚举零冲突。

2. **引入通用虚拟化集合（借鉴 ItemsRepeater / ListView）**
   - 提供 `list_view<Item, Message>(items).item_view(|item| View)`，内部复用 `result_list` 已有的虚拟化/折叠基建；`result_list` 退化为它的一个特化。
   - 支持 `selection`、`on_select`、`max_height`、`virtualized()`，对齐 ListView 的最小可用面。

3. **视觉状态自动化（借鉴 VisualStateManager）**
   - runtime 内部根据指针/焦点事件自动维护 hover/press/focus，并通过 backend 注入控件 style；`ControlState` 仅在应用需要**强制**某态（如禁用、校验）时显式覆盖。
   - 配合现有 `adaptive_switch`（已对齐 `AdaptiveTrigger` 的宽度断点），形成"自动态 + 响应式断点"的完整状态接口。

4. **富内容路线（借鉴 RichTextBlock / WebView2）**
   - 短期：为 `text` 增加 inline run（`text_runs([Run::plain, Run::link, Run::bold])`）满足词典基本富排版；
   - 长期：评估 `web_view`（HTML 宿主）承载 MDX 词典——这关系到 EasyDict 字典/长文档体验，建议立项单独评审。

5. **接口卫生**：统一 `info_bar` 导出；扩充 `A11yRole`；给 `checkbox` 增加 indeterminate；为通用元素提供 `.tooltip()`（提到 `View` 通用方法层，而非仅 button）。

---

## 7. 优先级路线建议

| 阶段 | 补齐项 | 对应迁移目标 |
| --- | --- | --- |
| 即时（接口卫生） | `info_bar` 导出统一、`A11yRole` 扩充、通用 `.tooltip()`、`checkbox` 第三态 | 全局 |
| P0 | `grid` 布局、通用 `list_view`/`items_repeater` | Settings / Main Window / 历史·词条列表 |
| P1 | `radio_button(s)`、通用 `image`、`number_box`、`auto_suggest_box`、NavigationView 增强、通用 `flyout` | Settings 迁移 |
| P1（内容） | `text` inline run（富文本）→ 评估 `web_view` | 词典模式 / MDX / 长文档 |
| P2 | `tab_view`、`tree_view`、`toggle_button`/`split_button`、独立 `border`/`viewbox` 等 | 按页面需求 |
| 决策项 | 视觉状态自动化（VSM 等价）、ControlTemplate 等价物是否需要 | 框架接口稳定性 |

---

## 8. 实现状态（2026-06-18 落地）

下表的所有 P0 / P1 / P2 缺口与 §6 借鉴项均已在 `lib/winfluent-rs` 实现：token + builder API + `lib.rs`/`prelude.rs` 导出 + `schema.rs` 快照 + `a11y.rs` 角色 + `diff.rs` 指纹 + `win_fluent_testkit` 布局转储 + `win_fluent_backend_iced` 渲染，并各配 snapshot 测试（`win_fluent` 单测 36 → 53 全绿，下游 `easydict_app` 仍编译通过）。

- **接口卫生**：`info_bar`/`InfoBarBuilder`/`InfoBarToken` 统一从 `lib.rs` 导出；`A11yRole` 扩充至 25 种（含 `RadioButton`/`Tab`/`TabItem`/`TreeItem`/`Hyperlink`/`Image`/`ProgressBar`/`MenuItem`/`Tooltip`/`Tree`，并同步 `WindowsUiaControlType` 映射）；`checkbox().indeterminate(true)` 第三态；`View::tooltip()` 通用 tooltip（任意元素）。
- **P0**：`grid()`（rows/columns 复用 `Length`，`cell`/`cell_span`）；通用 `list_view([ListViewItem...])`（选择/虚拟化/max_height，`result_list` 仍为其特化）。
- **P1**：`radio_group()`（RadioButtons）；通用 `image()`（file/URI 源 + `ImageStretch`，保留 `image_bgra_file`）；`number_box()`；`auto_suggest_box()`；NavigationView 增强（`PaneDisplayMode`、`header`、`footer_items`、settings item、back button）；通用 `flyout(anchor, content)`；`text_runs([TextRun...])` 富文本内联 run（plain/bold/italic/link）；`web_view_url`/`web_view_html`（接口层占位，iced 后端渲染说明性面板，真实 WebView2 宿主另立平台工作）。
- **P2**：`toggle_button`、`split_button`、`tab_view`（+`TabItem`）、`tree_view`（+`TreeNode`）、独立 `border`、`viewbox`。
- **决策项（未改动，待团队定夺）**：§5 #15 视觉状态自动化（VSM 等价，runtime 自动回灌 hover/press/focus）与 §5 #14 ControlTemplate 等价物属设计取舍，非可直接落地的接口缺口，保留现状。

## 附：基线来源
- WinUI 3 控件索引：https://learn.microsoft.com/windows/apps/develop/ui/controls/
- 布局面板：https://learn.microsoft.com/windows/apps/develop/ui/layout-panels
- ListView/GridView：https://learn.microsoft.com/windows/apps/develop/ui/controls/listview-and-gridview
- ItemsView / ItemsRepeater、NavigationView、数据绑定、VisualStateManager、System Backdrop、Accessibility 等官方文档（详见研究记录）
- winfluent-rs：`lib/winfluent-rs/crates/win_fluent/src/`（lib.rs / prelude.rs / view.rs / theme.rs / platform.rs 等）
