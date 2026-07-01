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

## 9. 100 项问题执行清单（2026-07-01 重新开始）

本节把 Fluent UI / WinUI-like 通用 Windows UI 框架基线拆成 100 个可审计问题。状态列按实际实现推进：`todo` 表示尚未开始，`doing` 表示本轮正在补代码/测试，`done` 只能在对应修复和测试证据都落地后填写。不要把历史实现或仅文档描述视作完成。

### 9.1 第一批：公开 API / schema / a11y / diff 合同

本批先建立外部应用视角的合同测试：从 `win_fluent::prelude::*` 构造控件树，并同时经过 schema、a11y、diff 三条分析管线。对应测试文件：`lib/winfluent-rs/crates/win_fluent/tests/fluent_contract.rs`。

| # | 状态 | 问题 | 修复目标 | 测试证据 |
| ---: | --- | --- | --- | --- |
| 1 | done | `info_bar` / builder / token 必须能从公开 prelude 使用 | 外部 crate 视角构造 `InfoBar` | `fluent_contract::public_prelude_builds_first_wave_winui_controls` |
| 2 | done | A11y role 必须能表达新增控件语义 | 合同视图解析 accessibility tree | `fluent_contract::contract_tree_flows_through_schema_a11y_and_diff` |
| 3 | done | 新增控件必须进入 schema 合同 | schema snapshot 包含关键控件类型 | `fluent_contract::contract_tree_flows_through_schema_a11y_and_diff` |
| 4 | done | 新增控件必须进入 diff 合同 | diff 能识别 contract view 更新 | `fluent_contract::contract_tree_flows_through_schema_a11y_and_diff` |
| 5 | done | 通用 `View::tooltip()` 必须可从公开 API 使用 | `text(...).tooltip(...)` 进入合同视图 | `fluent_contract::public_prelude_builds_first_wave_winui_controls` |
| 6 | done | Grid rows/columns/cell/span 必须可从公开 API 使用 | 构造设置页式二维网格 | `fluent_contract::public_prelude_builds_first_wave_winui_controls` |
| 7 | done | 通用 ListView 必须可从公开 API 使用 | 构造 selected/virtualized list | `fluent_contract::public_prelude_builds_first_wave_winui_controls` |
| 8 | done | RadioGroup 必须可从公开 API 使用 | 构造 horizontal RadioButtons | `fluent_contract::public_prelude_builds_first_wave_winui_controls` |
| 9 | done | Image file/URI + stretch 必须可从公开 API 使用 | 构造 generic image | `fluent_contract::public_prelude_builds_first_wave_winui_controls` |
| 10 | done | NumberBox 必须可从公开 API 使用 | 构造 range/step/spin number box | `fluent_contract::public_prelude_builds_first_wave_winui_controls` |
| 11 | done | AutoSuggestBox 必须可从公开 API 使用 | 构造 suggestion dropdown | `fluent_contract::public_prelude_builds_first_wave_winui_controls` |
| 12 | done | NavigationView 增强项必须可从公开 API 使用 | 构造 pane mode/header/footer/settings/back | `fluent_contract::public_prelude_builds_first_wave_winui_controls` |
| 13 | done | Generic Flyout 必须可从公开 API 使用 | 构造 anchor/content/open/placement | `fluent_contract::public_prelude_builds_first_wave_winui_controls` |
| 14 | done | RichText runs/link 必须可从公开 API 使用 | 构造 styled/link runs | `fluent_contract::public_prelude_builds_first_wave_winui_controls` |
| 15 | done | WebView token 必须可从公开 API 使用 | 构造 URL WebView | `fluent_contract::public_prelude_builds_first_wave_winui_controls` |
| 16 | done | ToggleButton 必须可从公开 API 使用 | 构造 on/off toggle button | `fluent_contract::public_prelude_builds_first_wave_winui_controls` |
| 17 | done | SplitButton 必须可从公开 API 使用 | 构造 primary + menu split button | `fluent_contract::public_prelude_builds_first_wave_winui_controls` |
| 18 | done | TabView 必须可从公开 API 使用 | 构造 closable tab view | `fluent_contract::public_prelude_builds_first_wave_winui_controls` |
| 19 | done | TreeView 必须可从公开 API 使用 | 构造 branch/leaf tree | `fluent_contract::public_prelude_builds_first_wave_winui_controls` |
| 20 | done | Border/Viewbox 必须可从公开 API 使用 | 构造 border + viewbox | `fluent_contract::public_prelude_builds_first_wave_winui_controls` |
| 21 | done | Tray fluent presenter tokens 必须可从公开 API 使用 | 构造 `TrayMenuPresenterStyle::winui()` | `fluent_contract::tray_menu_style_is_public_and_tokenized` |
| 22 | done | Windows UIA control type 映射需覆盖第一批控件 | 平台 UIA plan 补合同测试 | `uia_contract::first_wave_controls_map_to_windows_uia_control_types` |
| 23 | done | `win_fluent_testkit` 需覆盖第一批控件布局输出 | testkit snapshot 补合同测试 | `layout_contract::layout_snapshot_covers_first_wave_winui_controls` |
| 24 | done | Iced backend 需覆盖第一批控件编译路径 | backend compile test 从 contract view 构造 | `compile_contract::iced_backend_compiles_first_wave_winui_controls` |
| 25 | done | High Contrast 焦点/阴影策略需扩大到第一批控件 | HighContrast backend 编译合同 | `compile_contract::iced_backend_compiles_first_wave_controls_across_theme_and_disabled_states` |
| 26 | done | ThemeTokens 映射需扩大到第一批控件 | Light/Dark/HighContrast backend 编译合同 | `compile_contract::iced_backend_compiles_first_wave_controls_across_theme_and_disabled_states` |
| 27 | done | Disabled/read-only 状态需覆盖 NumberBox/AutoSuggestBox/RadioGroup | disabled/read-only backend 编译合同 | `compile_contract::iced_backend_compiles_first_wave_controls_across_theme_and_disabled_states` |
| 28 | done | ListView 虚拟化语义只有 token，没有可审计布局边界 | layout snapshot 输出 `max_height`/`virtualized` | `layout_contract::layout_snapshot_covers_first_wave_winui_controls` |
| 29 | done | Grid span 目前 backend 近似，需明示/测试限制 | testkit 输出 GridCell row/column/span | `layout_contract::layout_snapshot_covers_first_wave_winui_controls` |
| 30 | done | WebView 仍是接口占位，需记录并测试占位 fallback | backend 编译 WebView placeholder | `compile_contract::iced_backend_compiles_first_wave_winui_controls` |
| 31 | done | ToolTip 只有 top placement，缺 placement API | 新增 `TooltipPlacement` / `tooltip_at` + schema/backend 映射 | `fluent_contract::public_prelude_builds_first_wave_winui_controls` |
| 32 | done | Flyout 缺 light-dismiss/focus 行为合同 | 新增 `FlyoutLightDismiss`/`FlyoutFocusBehavior` + schema/a11y/testkit/backend 合同 | `fluent_contract::public_prelude_builds_first_wave_winui_controls`; `layout_contract::layout_snapshot_covers_first_wave_winui_controls`; `compile_contract::iced_backend_compiles_first_wave_winui_controls` |
| 33 | done | SplitButton 缺 disabled menu item 行为覆盖 | disabled item 进入 schema/testkit，backend 默认菜单触发跳过 disabled 项 | `fluent_contract::public_prelude_builds_first_wave_winui_controls`; `tests::split_button_default_menu_activation_skips_disabled_items` |
| 34 | done | TabView 缺 close button 可访问名称覆盖 | 新增 `TabItem::close_a11y_name`，a11y/UIA plan 覆盖 close button 名称 | `fluent_contract::contract_tree_flows_through_schema_a11y_and_diff`; `uia_contract::first_wave_controls_map_to_windows_uia_control_types` |
| 35 | done | TreeView 缺 expanded/collapsed a11y 状态覆盖 | TreeItem a11y help text 输出 expanded/collapsed，schema/UIA 合同覆盖 | `fluent_contract::contract_tree_flows_through_schema_a11y_and_diff`; `uia_contract::first_wave_controls_map_to_windows_uia_control_types` |
| 36 | done | Border/Viewbox 缺布局尺寸合同测试 | layout snapshot 输出 padding/width/height/stretch | `layout_contract::layout_snapshot_covers_first_wave_winui_controls` |
| 37 | done | Image URI/file source 缺 backend error/fallback 合同 | 新增 `ImageSourceKind` / fallback 行为，schema/testkit/backend 覆盖 Raster/BGRA/Empty | `fluent_contract::image_source_kind_and_fallback_contract_are_public`; `layout_contract::layout_snapshot_covers_first_wave_winui_controls`; `compile_contract::iced_backend_compiles_first_wave_winui_controls` |
| 38 | done | NumberBox 缺 min/max/step clamp 行为 | 构造时 clamp 初始值并归一化非正/非 finite step，backend step action 继续 clamp | `fluent_contract::number_box_clamps_initial_value_and_normalizes_step`; `compile_contract::iced_backend_compiles_first_wave_winui_controls` |
| 39 | done | AutoSuggestBox 缺 keyboard selection 合同 | 新增 `highlighted_index`，schema/testkit/backend 高亮行覆盖 | `fluent_contract::public_prelude_builds_first_wave_winui_controls`; `layout_contract::layout_snapshot_covers_first_wave_winui_controls`; `compile_contract::iced_backend_compiles_first_wave_winui_controls` |
| 40 | done | NavigationView settings/back event 合同不够集中 | contract view 统一断言 settings/back visible 与 action kind | `fluent_contract::public_prelude_builds_first_wave_winui_controls` |
| 41 | done | 托盘菜单只能走 native presenter，Fluent presenter runtime 需独立覆盖 | Fluent tray presenter/submenu runtime window options 与 testkit contract 覆盖 | `fluent_tray_submenu_expands_right_without_moving_root_panel`; `fluent_tray_submenu_flips_left_without_moving_root_panel_near_right_edge`; `layout_contract::layout_snapshot_covers_first_wave_winui_controls` |
| 42 | done | 托盘 Fluent 外观 token 缺完整 public contract | 补 `TrayMenuPresenterStyle` builder API、schema/testkit 完整 style 输出、Windows plan round-trip | `fluent_contract::tray_menu_style_is_public_and_tokenized`; `tray_contract::tray_plan_preserves_fluent_presenter_style_and_nested_submenus` |
| 43 | done | 托盘项高度/字体/padding DPI 合同需覆盖 | schema/testkit 输出 item font/min-height/padding/submenu arrow/hover inset | `fluent_contract::tray_menu_style_is_public_and_tokenized`; `layout_contract::layout_snapshot_covers_first_wave_winui_controls` |
| 44 | done | 托盘 hover 色需覆盖主题混合边界 | 补 hover mix builder/schema/testkit，native blend helper 覆盖边界 | `tray_owner_draw_blend_color_interpolates_channels`; `fluent_contract::tray_menu_style_is_public_and_tokenized` |
| 45 | done | 托盘 light/dark surface/foreground/separator 需覆盖 | 补 light/dark palette builder/schema/testkit/plan round-trip | `tray_contract::tray_plan_preserves_fluent_presenter_style_and_nested_submenus`; `layout_contract::layout_snapshot_covers_first_wave_winui_controls` |
| 46 | done | 托盘 separator metrics 需覆盖 | schema/testkit 输出 separator height/thickness/inset，platform plan 保留 style | `fluent_contract::tray_menu_style_is_public_and_tokenized`; `tray_contract::tray_plan_preserves_fluent_presenter_style_and_nested_submenus` |
| 47 | done | 托盘最大高度/滚动限制需覆盖 root/submenu | root/submenu 面板高度均受 presenter max-height clamp | `fluent_tray_root_and_submenu_heights_are_capped_by_presenter_max_height` |
| 48 | done | 托盘 popup 动画 flags 需覆盖 upward/downward | native TrackPopupMenu vertical positive/negative flags 测试 | `tray_popup_animation_maps_to_native_track_popup_flags` |
| 49 | done | native 托盘 popup DWM 角半径需覆盖更多 radius 边界 | radius 0/small/round DWM corner preference 测试 | `tray_presenter_radius_maps_to_dwm_corner_preference` |
| 50 | done | 托盘 tooltip hover selection 需覆盖 submenu/native 两路 | command/submenu/separator/closed selection tooltip 映射测试 | `tray_menu_hover_tooltips_map_win32_selection_messages` |
| 51 | done | 托盘 submenu round-trip 需覆盖多层 children | 多层 submenu Windows plan + backend subscription round-trip 覆盖 | `tray_contract::tray_plan_preserves_fluent_presenter_style_and_nested_submenus`; `tray_subscription_data_preserves_structured_menu_items` |
| 52 | done | Fluent submenu 独立窗口需覆盖 hide/close/focus 生命周期 | root/submenu 独立窗口关闭时清理对应 runtime 状态 | `closing_fluent_tray_root_window_clears_submenu_lifecycle_state`; `closing_fluent_tray_submenu_window_clears_only_submenu_lifecycle_state` |
| 53 | done | submenu 右边缘翻转需覆盖左边缘/多显示器 | right-edge flip、left-edge/non-zero work area expand-right 均覆盖 | `fluent_tray_submenu_flips_left_without_moving_root_panel_near_right_edge`; `fluent_tray_submenu_expands_right_near_left_edge_on_offset_work_area` |
| 54 | done | root/submenu 独立高度需覆盖滚动上限 | root/submenu panel 独立 content height 与 window height 测试 | `fluent_tray_panels_keep_independent_content_heights` |
| 55 | done | ContextMenuInset 需覆盖底边/顶边 clamp | upper-left/right 与顶部不足时向下打开的 inset 对齐覆盖 | `resolves_context_menu_inset_to_visible_upper_right_of_anchor`; `resolves_context_menu_inset_to_visible_upper_left_near_right_edge`; `resolves_context_menu_inset_below_anchor_when_top_space_is_insufficient` |
| 56 | done | Fluent 4px spacing/token 规则缺 lint/contract | theme contract 固定 spacing 4px grid 与 2px half-step | `theme_contract::fluent_theme_tokens_follow_spacing_typography_touch_and_focus_contracts` |
| 57 | done | Typography scale 缺控件级映射覆盖 | theme contract 固定 caption/body/subtitle/title scale 单调关系 | `theme_contract::fluent_theme_tokens_follow_spacing_typography_touch_and_focus_contracts` |
| 58 | done | Touch target min size 缺控件合同 | theme contract 固定 min touch target/control/icon/round button metrics | `theme_contract::fluent_theme_tokens_follow_spacing_typography_touch_and_focus_contracts` |
| 59 | done | Focus visual stroke 缺统一合同 | theme contract 固定 focus stroke 与 focus/border 区分 | `theme_contract::fluent_theme_tokens_follow_spacing_typography_touch_and_focus_contracts` |
| 60 | done | Localization/i18n 文本方向与 fallback 覆盖不足 | 新增 `TextDirection` / `LocaleId::text_direction()` 与外部 i18n fallback 合同 | `i18n_contract::public_i18n_contract_resolves_locale_fallback_and_text_direction`; `i18n::tests::locale_direction_uses_primary_language_subtag` |
| 61 | done | Keyboard accelerator 与 CommandBar 集成覆盖不足 | `advanced_controls_contract` | Page/Dialog command keyboard schema+a11y；CommandBar group/child contract |
| 62 | done | Screen reader name/hint audit 规则覆盖不足 | `win_fluent_testkit::reports_missing_names_for_extended_interactive_roles` | 扩展 Hyperlink/MenuItem/ProgressBar/RadioButton/Slider/TabItem/TreeItem 名称规则 |
| 63 | done | Empty interactive name 应作为 error 覆盖更多 roles | `win_fluent_testkit::reports_missing_names_for_extended_interactive_roles` | 新增角色空名称均报 error |
| 64 | done | ProgressBar/ProgressRing a11y value 合同不足 | `advanced_controls_contract` | ProgressBar value clamp/help_text；ProgressRing active/inactive help_text |
| 65 | done | Dialog primary/secondary command a11y 合同不足 | `advanced_controls_contract` | primary/secondary command 进入 Dialog a11y tree，含 enabled/keyboard |
| 66 | done | ComboBox selected item/schema/action 合同需集中化 | `advanced_controls_contract` | `selected_item()`、invalid selected 过滤、schema selected_label/a11y selected |
| 67 | done | TextEditor password/secure/read-only 合同需集中化 | `advanced_controls_contract` | secure/read_only/key_bindings schema；read-only 仍 focusable，a11y help_text |
| 68 | done | Slider preview interaction state 合同需集中化 | `advanced_controls_contract` | `preview_active()` schema+a11y value/range/step |
| 69 | done | Expander collapsed/expanded motion 合同需集中化 | `advanced_controls_contract` | schema motion=expand-collapse-reveal；a11y expanded/collapsed |
| 70 | done | BusyOverlay blocks-input/a11y 合同不足 | `advanced_controls_contract` | blocks_input/fade schema；active blocking help_text/name |
| 71 | done | Overlay layer alignment/scrim/blocking 合同不足 | `framework_contract`, `layout_contract` | Overlay scrim clamp、blocking/scrim layer count、schema/a11y/testkit 输出 |
| 72 | done | AdaptiveSwitch resolved-width 合同不足 | `framework_contract` | schema/a11y/diff/testkit 输出 resolved_width + resolved_branch |
| 73 | done | Lazy keyed subtree diff 合同不足 | `framework_contract::lazy_key_changes_are_reported_by_diff` | Lazy key 变更触发 diff Updated |
| 74 | done | PointerRegion pointer/wheel/escape event schema 覆盖不足 | `framework_contract` | move/left/right/double/wheel/escape action kind schema |
| 75 | done | CaptureOverlay DPI/magnifier/handles schema 覆盖不足 | `framework_contract` | background_pixels/cursor/handles/magnifier schema+a11y |
| 76 | done | WindowOptions no-activate/toolwindow/frame 合同需扩充 | `win_fluent_platform_win native_plan/maps_mini_window_options` | toolwindow/no_activate/acrylic/skip_taskbar native plan |
| 77 | done | Window placement 多显示器/DPI 合同需扩充 | `win_fluent_platform_win window_placement/high_dpi/multi_monitor` | work area/monitor/explicit/high-DPI/multi-monitor placement |
| 78 | done | Window command current/logical routing 合同需扩充 | `show_at_window_command_routes_to_logical_window_with_explicit_placement` | ShowAt logical id -> pending window + Explicit placement |
| 79 | done | Subscription merge/batch 行为合同不足 | `subscription::tests` | batch flatten/none discard/singleton unwrap/named-event mapper |
| 80 | done | Task batch/map/cancel 语义合同不足 | `task::tests` | batch flatten、same-message map、Cancel token + backend no-op |
| 81 | done | Clipboard/FileDialog/FolderDialog plan 合同需扩充 | `task::clipboard_and_dialog_tasks_preserve_options_and_mappers` | clipboard write + file/folder options/mapper contract |
| 82 | done | ShellVerb/Protocol registration guard 合同需扩充 | `platform::tests`, platform_win shell/protocol tests | registry-safe verb id + URI scheme guard；shell/protocol plan tests |
| 83 | done | NamedEvent IPC subscription 合同需扩充 | `subscription::named_event...`, platform_win named-event tests | named-event subscription + Windows plan action kind |
| 84 | done | Hotkey registration edge cases 合同需扩充 | platform_win `hotkey` tests | duplicate modifiers bitflag dedupe；invalid F-key/named key errors |
| 85 | done | Screenshot physical/DIP conversion 合同需集中化 | `screenshot::tests` | physical/dip/dpi conversion + invalid scale fallback |
| 86 | done | Visual diff tolerance/artifact 合同需接入 gallery | `win_fluent_gallery::gallery_reference_covers_framework_controls_and_visual_diff_artifacts` | VisualDiffTolerance + PPM artifact bytes in gallery tests |
| 87 | done | Gallery reference views 未覆盖全部新增控件 | `win_fluent_gallery` | gallery now covers Progress/AutoSuggest/Slider/BusyOverlay/Overlay/Adaptive/Lazy/Pointer/Capture |
| 88 | done | Public API boundary 需防 iced/windows 类型泄漏 | `win_fluent_gallery` schema/UIA tests | gallery/main/mini/fixed/settings/capture snapshots assert no iced/windows leakage |
| 89 | done | Product-specific strings 仍出现在 winfluent-rs 测试 fixture | `rg OpenAI/Google/DeepL/... crates` | fixtures neutralized to Provider A/B/C；rg returns no matches |
| 90 | done | ARM64 compile/runtime evidence 与 framework contract 仍分散 | `.github/workflows/winfluent-rs.yml`, `arm64-msix-smoke.yml` | winfluent ARM64 check + ARM64 runtime smoke + app ARM64 MSIX smoke workflows |
| 91 | done | Docs/examples 仍有 ignored snippets，缺编译覆盖 | `cargo test -p win_fluent --doc` | `view.rs` ignored snippets converted to compilable `no_run` doctests |
| 92 | done | Deprecated service_result_* 别名缺迁移测试 | `remaining_contract::deprecated_service_result_aliases...` | deprecated token/builder aliases exported and migration-compiled |
| 93 | done | Icon token set 缺 Fluent glyph contract | `remaining_contract::standard_icons_have_public_fluent_glyph_contract` | public Fluent glyph map + backend consumes `IconToken::resolved_glyph()` |
| 94 | done | StatusBadge 数字/dot 语义不足 | `remaining_contract::status_badge_exposes_text_count_and_dot_semantics` | `StatusBadgeKind::{Text,Count,Dot}` + count/dot a11y/schema |
| 95 | done | SettingsRow trailing/content/a11y 合同不足 | `remaining_contract::settings_row_schema_and_a11y...` | schema `has_content`/`trailing` + UIA description/help_text |
| 96 | done | ResultCard collapse virtualization 与 generic ListView 关系不清 | `remaining_contract::result_list_contract_is_distinct...` | `ListContractKind` distinguishes result-list vs generic ListView |
| 97 | done | ControlState 自动化（VSM 等价）仍未决 | `state::tests`, `remaining_contract::visual_state...` | Common/Focus/Selection visual-state snapshot + automation key |
| 98 | done | ControlTemplate 等价能力仍未决 | `remaining_contract::control_template_is_a_public...` | public `control_template`/`custom_control` API + schema/a11y kind |
| 99 | done | Fluent design token 到控件样式的覆盖率缺量化报告 | `theme::tests`, `remaining_contract::visual_state...` | `ThemeTokenCoverageReport` quantifies categories/colors/control metrics |
| 100 | done | 100 项清单缺自动防回归入口 | `remaining_contract::compare_winui_plan_tracks_all_100_items_as_done` | test reads `compare_winui_plan.md` and asserts items 1-100 are done |

复核命令：

```bash
cargo fmt -- --check
cargo test --workspace
```

## 附：基线来源
- WinUI 3 控件索引：https://learn.microsoft.com/windows/apps/develop/ui/controls/
- 布局面板：https://learn.microsoft.com/windows/apps/develop/ui/layout-panels
- ListView/GridView：https://learn.microsoft.com/windows/apps/develop/ui/controls/listview-and-gridview
- ItemsView / ItemsRepeater、NavigationView、数据绑定、VisualStateManager、System Backdrop、Accessibility 等官方文档（详见研究记录）
- winfluent-rs：`lib/winfluent-rs/crates/win_fluent/src/`（lib.rs / prelude.rs / view.rs / theme.rs / platform.rs 等）
