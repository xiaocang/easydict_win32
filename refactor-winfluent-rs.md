对。`win fluent rs` 的定位可以进一步收敛成：

> **Windows-only、Rust-native、Fluent/WinUI-like 应用框架。上层应用使用自己的 View Token 描述界面；Iced、Win32、COM、Windows API 全部是私有实现细节。**

我会建议公开 crate 名叫：

```text
win-fluent-rs
```

Rust crate 实际可以叫：

```rust
win_fluent
```

应用层只写：

```rust
use win_fluent::prelude::*;
```

不出现：

```rust
iced::Element
iced::Task
iced::Subscription
windows::Win32
HWND
COM
wgpu
winit
```

这条路线比“做一个 Iced 主题库”更重，但它更适合现有 WinUI 项目迁移，也更适合长期沉淀成框架。

---

## 1. 最终架构

```text
EasyDict / 其他 Windows 应用
  ↓
win_fluent public API
  ↓
View Token / Theme Token / Command Token / A11y Token
  ↓
win_fluent runtime
  ↓
win_fluent controls
  ↓
win_fluent backend_iced
  ↓
Iced runtime / widgets / wgpu
  ↓
win_fluent platform_win
  ↓
windows-rs / Win32 / DWM / UIA / IME / Tray / Hotkey
```

Iced 作为内部 backend 是合理的：Iced 现在提供 type-safe reactive 模型、内置 widgets、自定义 widget、async actions、debug/performance tooling，并且它的 `iced_wgpu` backend 支持 DX12；但 Iced 文档也明确说它仍是 experimental software，所以把它封装在 `win_fluent` 后面可以隔离 API 变化。([Docs.rs][1])

对 EasyDict 这种托盘常驻、多窗口、全局热键应用，Iced 的 `daemon` 模式和 `Subscription` 模型很适合内部使用：daemon 可以不默认打开窗口并在所有窗口关闭后继续运行，Subscription 则适合监听热键、剪贴板、主题变化、托盘事件等外部事件流。([Docs.rs][2])

---

## 2. View Token 写法应该长什么样

核心不是返回 `iced::Element`，而是返回自己的 `View<Msg>`。这个 `View` 内部是一个稳定的 token tree。

示例：

```rust
use win_fluent::prelude::*;

#[derive(Debug, Clone)]
enum Msg {
    InputChanged(String),
    Translate,
    Clear,
    Ocr,
    RouteChanged(Route),
}

fn translate_page(state: &AppState) -> View<Msg> {
    page("翻译")
        .content(
            column((
                language_bar()
                    .source(state.source_lang)
                    .target(state.target_lang)
                    .on_source_change(Msg::SourceLangChanged)
                    .on_target_change(Msg::TargetLangChanged),

                text_editor(&state.input)
                    .id("translate.input")
                    .placeholder("输入要翻译的文本")
                    .min_height(140)
                    .on_input(Msg::InputChanged),

                command_bar((
                    primary_button("翻译")
                        .icon(icon::translate())
                        .on_press(Msg::Translate),

                    button("OCR")
                        .icon(icon::camera())
                        .on_press(Msg::Ocr),

                    button("清空")
                        .icon(icon::delete())
                        .on_press(Msg::Clear),
                )),

                service_result_list(&state.results)
                    .on_copy(Msg::CopyResult)
                    .on_speak(Msg::SpeakResult),
            ))
            .padding(24)
            .spacing(16),
        )
        .into_view()
}
```

也可以后续加 macro DSL，但 macro 只能是语法糖：

```rust
view! {
    Page(title: "翻译") {
        Column(spacing: 16, padding: 24) {
            LanguageBar(
                source: state.source_lang,
                target: state.target_lang,
                on_source_change: Msg::SourceLangChanged,
                on_target_change: Msg::TargetLangChanged,
            )

            TextEditor(
                id: "translate.input",
                text: state.input,
                placeholder: "输入要翻译的文本",
                min_height: 140,
                on_input: Msg::InputChanged,
            )

            CommandBar {
                PrimaryButton("翻译", icon: Icons::Translate, on_press: Msg::Translate)
                Button("OCR", icon: Icons::Camera, on_press: Msg::Ocr)
                Button("清空", icon: Icons::Delete, on_press: Msg::Clear)
            }

            ServiceResultList(results: state.results)
        }
    }
}
```

内部结构可以是：

```rust
pub struct View<Message> {
    pub(crate) token: ViewToken<Message>,
}

pub enum ViewToken<Message> {
    Page(PageToken<Message>),
    Text(TextToken),
    Button(ButtonToken<Message>),
    TextEditor(TextEditorToken<Message>),
    CommandBar(CommandBarToken<Message>),
    NavigationView(NavigationViewToken<Message>),
    Dialog(DialogToken<Message>),
    Column(LayoutToken<Message>),
    Row(LayoutToken<Message>),
    Custom(CustomToken<Message>),
}
```

然后内部编译：

```text
ViewToken
  ↓
style resolve
  ↓
layout resolve
  ↓
accessibility tree
  ↓
iced Element tree
  ↓
Iced runtime
```

这带来一个非常重要的收益：**测试、迁移、性能分析都可以围绕 token tree 做，不必每次真的启动窗口。**

---

## 3. 为什么 View Token 对迁移特别重要

对现有 WinUI 项目迁移，View Token 有四个价值。

第一，它是迁移目标。XAML 不需要直接转成 Iced，而是转成 `win_fluent` 的语义 token。例如：

```xml
<Button Content="Translate" />
```

可以迁移成：

```rust
primary_button("Translate").on_press(Msg::Translate)
```

第二，它是测试对象。页面结构、控件属性、accessibility metadata、主题解析结果都可以 snapshot。

第三，它是 backend 隔离层。现在编译到 Iced，将来必要时可以替换成 Direct2D/DirectWrite 自研 backend，而上层 EasyDict 不需要改。

第四，它是性能控制点。你可以在 token 层做 diff、lazy、memo、virtualized list、streaming update coalescing，避免 Iced backend 每次状态变化都做过多重建。

所以 `win fluent rs` 的核心资产不是 “Iced 风格”，而是：

```text
稳定的 Windows Fluent View Token 模型
```

---

## 4. 对 EasyDict Win32 迁移的收益判断

EasyDict Win32 是一个足够复杂的样本：它现在包含 OCR 截图翻译、划词翻译、全局热键、多窗口、TTS、语法纠错、长文档翻译、字典模式、LLM streaming、SQLite 缓存、CJK 字体处理等能力；当前技术栈是 .NET、WinUI 3、C#，测试用 xUnit + FluentAssertions。([GitHub][3])

所以迁移不是“换 UI 框架”，而是：

```text
C# / WinUI 3 app
  ↓
Rust core
  ↓
win_fluent app
  ↓
Iced backend + Windows platform layer
```

### 4.1 内存收益

迁移后最可能改善的是：

```text
后台常驻内存
mini window 内存
冷启动后的 private bytes
长期运行后的内存稳定性
```

原因是新的 UI 层可以避免 .NET runtime、XAML binding、WinUI 控件树、C# 对象分配等一部分开销。但不能承诺“一定大幅下降”，因为 Iced/wgpu 也会带来 GPU backend、shader、texture atlas、font cache 等固定成本。

我会把目标写成 KPI，而不是宣传口号：

| 场景                   | 迁移目标                                      |
| -------------------- | ----------------------------------------- |
| 托盘后台常驻               | private bytes 比 WinUI 版下降 **20%+**，保底不能更差 |
| Mini Window 打开后 idle | private bytes 比 WinUI 版下降 **25–40%**      |
| Main Window 打开后 idle | private bytes 比 WinUI 版下降 **15–30%**      |
| 长时间运行 8 小时           | private bytes 曲线无持续爬升                     |
| OCR / 长文档任务后         | 任务结束后内存能回落到稳定区间                           |

这里建议主要看 **Private Bytes** 和 **Working Set**。Windows 文档里 working set 是进程最近引用的虚拟地址页集合，包含共享和私有数据；Performance Monitor 也可以用 Process > Private Bytes 观察进程私有提交内存。([Microsoft Learn][4])

最终结论：**内存有改善机会，尤其是后台常驻和 Mini Window；Main Window 的收益取决于列表、文本、图标、字体缓存和 streaming UI 更新是否做得克制。**

### 4.2 CPU 收益

CPU 改善主要来自：

```text
减少 binding / reflection / runtime 层开销
减少 GC 压力
更可控的异步任务调度
更少的临时字符串和对象分配
streaming 翻译结果合并刷新
虚拟化列表
```

但 Iced backend 也有风险：如果每个 streaming token、每个剪贴板事件、每个输入字符都触发全页面重建和重绘，CPU/GPU 可能不降反升。

所以 `win fluent rs` 必须内置这些策略：

```text
1. ViewToken diff
2. Lazy subtree
3. Text output coalescing，例如 16ms/33ms 合并一次 streaming 更新
4. VirtualizedListView
5. ResultCard 局部 invalidation
6. Image/icon cache
7. Font/text layout cache
8. 空闲时不 repaint
```

目标 KPI 可以定成：

| 场景                 | 目标                  |
| ------------------ | ------------------- |
| 托盘后台 idle          | CPU 长期接近 0%，偶发唤醒可解释 |
| Mini Window idle   | CPU < 0.5%          |
| Streaming 翻译输出     | 不因 token 高频更新导致明显卡顿 |
| 10 个服务并行 streaming | UI 仍可输入、滚动、关闭       |
| 长文档翻译进度更新          | 进度刷新限频，不刷爆 UI       |

最终结论：**CPU 收益不是 Rust 自动带来的，而是靠 token diff、刷新限频、虚拟化和缓存做出来的。**

### 4.3 用户 UX 体感收益

用户真正能感觉到的提升主要不是“内存少了 30MB”，而是这些：

```text
1. 热键唤出 Mini Window 更快
2. 托盘常驻更轻，不拖慢系统
3. 划词翻译弹窗更稳定
4. Streaming 输出不卡输入框和滚动
5. OCR 完成后结果窗口出现更快
6. 主窗口冷启动更快
7. 设置页切换更顺
8. 长文档翻译进度不会让 UI 卡住
9. 低配置机器和笔记本电池模式下更稳
```

我会把 UX 指标定成：

| UX 指标                | 目标                           |
| -------------------- | ---------------------------- |
| 冷启动到主窗口首帧            | 比 WinUI 版快 **20–40%**        |
| 全局热键到 Mini Window 首帧 | 目标 < **100ms**，理想 < **60ms** |
| 输入框输入延迟              | 主观无卡顿，P95 < **16ms**         |
| Streaming 结果刷新       | 平滑，不逐 token 抖动               |
| OCR 结果展示             | OCR 完成后窗口/结果区域立即响应           |
| 主题切换                 | 无明显白屏/闪烁                     |
| DPI 切换               | 不模糊、不错位                      |
| 8 小时常驻               | 无内存爬升、无 CPU 空转               |

这些才是用户可见收益。

### 4.4 包体和安装收益

Rust 版本有机会减少分发复杂度：

```text
win_fluent app
  + app exe
  + assets/icons/fonts
  + optional OCR/TTS/platform helper
  + installer
```

相比 WinUI/.NET 应用，理论上可以减少对 .NET runtime、Windows App SDK/XAML 相关发布组件的依赖。不过 wgpu、字体、图标、OCR、TTS、词典资源、语言包也会影响最终大小，所以包体收益必须实测。

我建议目标：

| 包            | 目标                         |
| ------------ | -------------------------- |
| Portable zip | 先支持，用于 nightly 和内测         |
| MSI          | 主发布渠道                      |
| MSIX         | 可选，用于 Store/企业部署           |
| winget       | 稳定后提交 manifest             |
| 自动更新         | 第二阶段再做，不要 MVP 先做复杂 updater |

---

## 5. 打包发布方案

### 5.1 推荐发布矩阵

```text
Dev / nightly:
  portable .zip

Stable:
  signed .msi

Microsoft Store / enterprise:
  optional .msix

CLI install:
  winget manifest
```

`cargo-wix` 很适合做 Rust Windows MSI：它是 Cargo 子命令，可以从 Rust binary project 构建 MSI，并支持通过 Windows SDK 的 SignTool 签名 installer。([GitHub][5])

`cargo-dist` 可以用于更完整的 release pipeline：它负责构建 shippable binaries、tarballs/installers、生成 machine-readable manifests，并可生成 GitHub CI release workflow。([Axodot Dev][6])

MSIX 也可以做，但要小心 Windows 10/11 差异。Microsoft 文档显示 MSIX 可用于打包新应用或现有桌面应用，也支持命令行生成、签名、非 Store 更新；但 Windows 10 和 Windows 11 对一些特性的支持不同，比如 legacy context menu shell extension 是 Windows 11-only，Windows 10 需要替代方案。([Microsoft Learn][7])

winget 发布也可行：Microsoft 文档说明提交包需要创建 YAML manifest，并提交到 Windows Package Manager repository。([Microsoft Learn][8])

### 5.2 EasyDict 具体建议

EasyDict 有全局热键、托盘、OCR、划词、可能还有右键菜单/外壳集成。对这种工具型应用，我建议：

第一阶段：

```text
portable zip
signed MSI
```

第二阶段：

```text
winget
auto updater
```

第三阶段再考虑：

```text
MSIX / Store
```

原因是 MSIX 对 shell integration、文件路径、持久数据、Windows 10 兼容性会带来额外约束。EasyDict 这种工具软件最好先用传统 Win32 分发路线，把功能稳定性放在第一位。

### 5.3 打包成本

| 项目                 | 成本                                      |
| ------------------ | --------------------------------------- |
| Rust release build | 低                                       |
| portable zip       | 低                                       |
| MSI                | 中，需要 WiX、安装目录、快捷方式、卸载、签名                |
| MSIX               | 中高，需要 manifest、签名、权限、Windows 10/11 差异测试 |
| winget             | 中，需要稳定下载地址、manifest、版本规则                |
| 自动更新               | 中高，涉及签名、增量、回滚、安全策略                      |
| 代码签名证书             | 必须考虑，否则 SmartScreen 体验差                 |

我的建议：**MVP 不做 MSIX，不做复杂 updater。先把 signed MSI + portable zip 做扎实。**

---

## 6. 测试框架是否可复用

可以，而且这是 `win fluent rs` 最大的隐性收益之一。

因为上层 UI 是 View Token，不是 Iced Element，所以测试可以复用到所有基于 `win fluent rs` 的应用。

建议做一个：

```rust
win_fluent_testkit
```

里面提供：

```text
render token tree
snapshot token tree
snapshot resolved theme
snapshot accessibility tree
simulate message
simulate keyboard/mouse
golden screenshot
layout assertion
performance harness
```

### 6.1 Token snapshot 测试

页面测试不需要启动窗口：

```rust
#[test]
fn translate_page_snapshot() {
    let state = AppState::demo();
    let view = translate_page(&state);

    win_fluent_testkit::assert_view_snapshot!("translate_page", view);
}
```

这可以检查：

```text
控件层级
id
文本
按钮状态
绑定 message
accessibility name
theme token
layout token
```

`insta` 很适合做这种 snapshot 测试；它的文档明确说 snapshot tests 适合测试很大或经常变化的 reference value，并提供 review workflow。([Docs.rs][9])

### 6.2 DSL / macro 编译测试

如果 `win fluent rs` 提供 `view!` macro，就需要 compile-fail 测试，确保错误信息友好。例如：

```rust
view! {
    Button(123)
}
```

应该报出清楚错误，而不是一屏泛型错误。

`trybuild` 正适合测试 Rust 宏和 API 的 compile-fail 场景，它可以编译指定测试文件并比对预期错误输出。([Docs.rs][10])

### 6.3 控件视觉回归测试

可以建立：

```text
win_fluent_gallery
  button
  text_input
  toggle_switch
  combo_box
  navigation_view
  dialog
  result_card
```

每个控件按状态截图：

```text
Light / Dark / High Contrast
Rest / Hover / Pressed / Focused / Disabled
100% / 125% / 150% DPI
English / Chinese / Japanese
```

然后做 golden screenshot diff。

需要注意：GPU screenshot 在不同机器可能有细微差异。更稳的做法是：

```text
优先 snapshot display list / layout tree
其次 screenshot diff
最后人工视觉验收
```

### 6.4 Accessibility 测试

自绘 UI 必须做 accessibility。Microsoft UI Automation 是 Windows accessibility framework，可让屏幕阅读器和自动化脚本访问桌面 UI 元素；如果 `win fluent rs` 自绘 Fluent 控件，就必须提供 UIA tree 或使用 accessibility 抽象。([Microsoft Learn][11])

AccessKit 也值得作为内部参考或依赖，它的目标就是帮助自绘 UI toolkit 为屏幕阅读器和辅助技术提供 accessibility，并用 tree 结构表达 role、name、actions 等信息。([GitHub][12])

测试可以这样：

```rust
#[test]
fn translate_page_accessibility_tree() {
    let state = AppState::demo();
    let tree = win_fluent_testkit::accessibility_tree(translate_page(&state));

    insta::assert_debug_snapshot!(tree);
}
```

### 6.5 性能测试

Rust crate 内部可以用 Criterion 做 layout、diff、theme resolve、token compile、text layout cache 等 microbenchmark；Criterion 文档说明它是统计驱动的 microbenchmark 库，目标是检测和估计性能改进/回归。([Docs.rs][13])

Workspace 测试推荐 `cargo-nextest`，它提供更快的 Rust test runner、CI 支持和更好的 test selection；官网称可比 `cargo test` 快到 3 倍，且支持 per-test isolation 和 CI。([Nexte][14])

整体测试体系可以是：

```text
cargo nextest run
cargo test --doc
cargo insta test
cargo bench
trybuild ui tests
Windows UI smoke tests
golden screenshot tests
WPR/ETW performance runs
```

结论：**测试框架高度可复用，而且 View Token 会让 UI 测试比传统 WinUI/XAML 更容易自动化。**

---

## 7. 迁移成本整体评估

### 7.1 成本最高的不是 Iced，而是框架抽象

你不是直接写 Iced app，而是在做：

```text
public API
View Token
Task abstraction
Subscription abstraction
Window abstraction
Theme system
Control library
Iced adapter
Windows platform adapter
Testkit
Packaging pipeline
```

所以成本比“用 Iced 重写 EasyDict”高，但收益也更长期。

### 7.2 迁移成本分层

| 模块                      | 成本 | 风险                   |
| ----------------------- | -: | -------------------- |
| Rust core：翻译服务、配置、缓存    |  中 | 可控                   |
| View Token API          |  高 | 决定框架生命线              |
| Fluent theme            |  中 | 视觉细节多                |
| 基础控件                    |  中 | Button/Text/Input 可控 |
| TextEditor / IME        |  高 | 中文输入必须稳定             |
| ListView / 虚拟化          |  高 | 性能关键                 |
| Dialog / Flyout / Popup | 中高 | 定位和焦点复杂              |
| 多窗口 / 托盘 / 热键           | 中高 | Windows 工具软件核心       |
| OCR overlay             |  高 | 更像平台功能，不是普通 UI       |
| Accessibility           |  高 | 必须早设计                |
| 打包 / 签名 / 更新            |  中 | 工程化成本                |
| 视觉回归测试                  |  中 | 建立后收益大               |

### 7.3 时间粗估

按一个熟悉 Rust + Windows 的小团队估算：

| 阶段      | 目标                                          |   粗略成本 |
| ------- | ------------------------------------------- | -----: |
| Phase 0 | 现有 WinUI 基线测量、架构验证                          |  1–2 周 |
| Phase 1 | `win_fluent` View Token + Iced adapter MVP  |  3–5 周 |
| Phase 2 | 基础 Fluent 控件 + theme + testkit              |  4–8 周 |
| Phase 3 | EasyDict Mini Window 迁移                     |  2–4 周 |
| Phase 4 | Settings 迁移                                 |  3–5 周 |
| Phase 5 | Main Window 迁移                              |  4–8 周 |
| Phase 6 | OCR overlay / Long Document / MDX rich view | 6–10 周 |
| Phase 7 | MSI、签名、性能回归、视觉回归、a11y                       |  3–6 周 |

如果是一个人做，保守看是 **6–12 个月** 才能达到可替代现有 WinUI 版的质量。
如果是 2–3 人小团队，且先只迁 Mini Window + Settings，**2–3 个月** 可以做出可验证版本，**4–6 个月** 有机会进入主线试用。

---

## 8. 建议的迁移顺序

不要一上来重写整个 EasyDict。顺序应该是：

```text
1. 建立 WinUI 版 baseline
2. 做 win_fluent MVP
3. 迁 Mini Window
4. 迁 Settings
5. 迁 Main Window
6. 迁 OCR overlay
7. 迁 Long Document / MDX rich document
8. 替换发布渠道
```

### 第 1 步：先测 WinUI baseline

必须先有数据，否则无法证明迁移收益。

测这些：

```text
cold start 到首帧
warm start 到首帧
全局热键到 Mini Window 首帧
主窗口 idle private bytes
mini window idle private bytes
后台托盘 idle private bytes
idle CPU
输入框 P95 latency
streaming 输出时 CPU
OCR 任务后内存回落
8 小时常驻内存曲线
安装包大小
安装后磁盘占用
```

### 第 2 步：做 `win_fluent` MVP

MVP 只做：

```text
Application
View
Task
Subscription
Window
Theme
Button
Text
TextInput
TextEditor
ToggleSwitch
ComboBox
ScrollView
CommandBar
Dialog
NavigationView
SettingsRow
ServiceResultCard
```

### 第 3 步：先迁 Mini Window

Mini Window 是最佳验证对象，因为它覆盖：

```text
热键
窗口唤出
置顶
翻译请求
streaming 输出
轻量 UI
主题
剪贴板/划词
```

同时范围比 Main Window 小。

### 第 4 步：迁 Settings

Settings 用来验证控件库：

```text
ToggleSwitch
ComboBox
TextInput
PasswordBox
Slider
Radio
Dialog
NavigationView
SettingsRow
```

### 第 5 步：迁 Main Window

最后再迁主窗口，因为它依赖最多控件和业务状态。

---

## 9. 收益与成本总表

| 维度    | 预期收益                    | 成本/风险           | 判断          |
| ----- | ----------------------- | --------------- | ----------- |
| 内存    | 后台/mini window 有明显下降机会  | wgpu 有固定成本，需实测  | 值得验证        |
| CPU   | 可通过限频、diff、虚拟化降低        | 若直接重建全页面可能变差    | 必须框架级控制     |
| 启动速度  | 有机会明显快于 WinUI/.NET UI   | 字体、GPU 初始化要优化   | 值得验证        |
| UX 体感 | 热键响应、streaming、常驻体验可提升  | 控件细节和 IME 是硬仗   | 高价值         |
| 视觉一致性 | 可做到 Fluent-like         | 像素级 WinUI 一致成本高 | 接受“接近”即可    |
| 包体    | MSI/zip 路线更轻更直接         | 签名、更新、MSIX 仍有成本 | 可控          |
| 测试    | View Token 让 UI 测试高度可复用 | 需要自建 testkit    | 长期收益大       |
| 维护    | 上层不受 Iced/WinAPI 影响     | 框架层维护成本高        | 对多应用/长期项目值得 |
| 迁移    | 可渐进迁移                   | 无法自动转换复杂 XAML   | 页面级重写       |

---

## 10. Go / No-Go 标准

我建议设一个 6–8 周的技术验证期。只做：

```text
win_fluent MVP
Mini Window
基础打包
基础 testkit
性能 baseline 对比
```

通过标准：

```text
1. 上层 EasyDict app 不依赖 iced / windows crate
2. Mini Window 功能可用
3. 中文 IME 输入正常
4. 热键唤出稳定
5. Streaming 输出不卡 UI
6. private bytes 不高于 WinUI 版，目标下降 20%+
7. 热键到首帧比 WinUI 版更快
8. MSI 能打包并签名
9. View Token snapshot 测试可跑
10. 控件视觉达到 WinUI-like，而不是粗糙仿制
```

如果这 10 条过不了，建议改成：

```text
Rust core + 继续 WinUI UI
```

如果过了，再继续迁 Settings 和 Main Window。

---

## 11. 最终建议

我认为 `win fluent rs` 值得做，但前提是目标要清楚：

```text
不是 Iced theme library
不是 XAML compatible runtime
不是 WinUI clone
不是跨平台 GUI

而是：
Windows-only Rust Fluent application framework
```

它应该提供：

```text
View Token UI 描述
Fluent theme tokens
WinUI-like controls
Windows shell abstraction
Task / Subscription abstraction
Window abstraction
Tray / Hotkey / Clipboard abstraction
Accessibility tree
Testkit
Packaging templates
```

它应该隐藏：

```text
Iced
wgpu
winit
windows-rs
Win32
COM
HWND
UIA provider details
```

对 EasyDict 的实际迁移收益，我会这样判断：

> **最大收益不是单纯“更省内存”，而是常驻工具的整体体感：更快唤出、更轻后台、更少卡顿、更稳定 streaming、更可测试、更容易长期维护。**

但也要诚实：
**如果只迁一个应用，而且要求完全 WinUI 一致，成本偏高。**
**如果目标是沉淀一个可复用 Windows Rust Fluent 框架，并用 EasyDict 做 reference app，这条路线很有价值。**

---

## 12. 当前实现进度

更新日期：2026-05-30。

当前已在仓库中落地第一版独立框架骨架，目录为：

```text
lib/winfluent-rs/
```

这个位置刻意放在 `lib/` 下，方便后续切换为独立 submodule 引用。框架目录、crate 命名和源码命名均不复用现有应用命名。

### 12.1 已完成内容

已创建 Rust workspace：

```text
lib/winfluent-rs/
├── Cargo.toml
├── README.md
└── crates/
    ├── win_fluent/
    ├── win_fluent_testkit/
    └── win_fluent_gallery/
```

其中：

```text
win_fluent
```

是核心框架 crate，公开入口为：

```rust
use win_fluent::prelude::*;
```

目前已实现这些框架级 token / abstraction：

```text
View / ViewToken
Fluent-like ThemeTokens
CommandToken
Task
Subscription
WindowOptions / WindowCommand
Tray / Hotkey / Clipboard / ShellVerb 平台 token
Accessibility tree
View diff
Lazy subtree token
Streaming text coalescer
```

已实现的基础控件 token 包括：

```text
Page
Text
Button / PrimaryButton
TextEditor
ToggleSwitch
ComboBox
CommandBar
NavigationView
Dialog
Column / Row
ScrollView
SettingsRow
ServiceResultCard
ServiceResultList
Custom
Lazy
```

`win_fluent_testkit` 已提供：

```text
view_snapshot
accessibility_tree
accessibility_snapshot
theme_snapshot
assert_view_snapshot!
```

`win_fluent_gallery` 已提供一个可运行的 token gallery 示例，用于构建代表性控件树并输出 snapshot。

### 12.2 当前实现边界

当前版本是 **framework API + token model MVP**，不是可渲染 GUI runtime。也就是说：

```text
应用层可以用 win_fluent token 描述界面
测试可以 snapshot token tree / a11y tree / theme
diff / lazy / coalescing 的性能控制点已经有最小实现
```

但这些内容还没有实现：

```text
Iced backend adapter
Windows platform adapter
真实窗口运行时
真实控件渲染
真实 UIA provider
真实托盘 / 热键 / 剪贴板 / Shell integration
MSI / zip 打包模板
visual regression pipeline
```

也就是说，当前进度完成的是可编译、可测试、可继续扩展的框架地基；renderer/backend/platform 仍未真正接入外部运行时，但已经开始补私有 adapter 边界。

### 12.3 验证结果

在：

```text
lib/winfluent-rs/
```

已执行：

```bash
cargo fmt --all --check
cargo test --workspace
```

结果：

```text
format check passed
16 tests passed
```

另外已检查源码中没有出现现有应用命名：

```bash
rg -i "easydict" lib\winfluent-rs
```

结果无命中。

此前已检查公开源码中没有泄漏计划隐藏的 backend / platform 细节：

```bash
rg "iced|windows::|Win32|HWND|COM|wgpu|winit" lib\winfluent-rs
```

结果无命中。当前已新增私有 `backend_iced` / `platform_win` adapter 骨架，所以源码内会出现 adapter 文件名；判断标准改为：上层 app API / prelude 不暴露 `iced`、`windows-rs`、`Win32`、`HWND`、`COM` 等类型。

Cargo 构建产物已清理，未保留 `target/`。

### 12.4 下一步建议

下一阶段建议按这个顺序推进：

```text
1. 固化 ViewToken schema 和 snapshot 格式
2. 补齐控件状态模型：hover / pressed / focused / disabled / validation
3. 增加 layout token 和 resolved theme token 的测试输出
4. 接入 Iced adapter，但保持在私有 backend 模块内
5. 接入 Windows platform adapter：window / tray / hotkey / clipboard
6. 做一个最小可运行 demo window
7. 再开始 Mini Window reference app 验证
```

当前 Go / No-Go 标准里的第 1 条已在 API 层满足：

```text
上层应用不需要依赖 iced / windows crate
```

Mini Window、IME、热键、streaming UI、内存和 MSI 等验证项，需要 backend/platform/runtime 接入后继续推进。

### 12.5 本轮推进结果

本轮继续按 12.4 的顺序推进，已完成这些增量：

```text
1. 新增 ViewSchema version=1，view snapshot 改为稳定 schema 输出
2. 新增 ControlState / ValidationState，覆盖 enabled / hovered / pressed / focused / validation
3. TextEditor / Button / ToggleSwitch / ComboBox token 接入 ControlState
4. testkit 增加 layout_snapshot，theme_snapshot 改为更完整的 resolved theme 输出
5. 新增私有 backend_iced adapter skeleton，将 ViewToken 编译为 backend plan
6. 新增私有 platform_win adapter skeleton，将 hotkey / tray / shell verb / subscription token 编译为 Windows registration plan
7. gallery 增加 Mini Window reference：WindowOptions + Mini Window ViewSchema snapshot
```

仍未完成的部分：

```text
1. Iced adapter 还没有引入 iced crate，也没有真实 Element / Task / Subscription 映射
2. Windows platform adapter 还没有调用 windows-rs / Win32 API，也没有真实 tray / hotkey / clipboard 注册
3. Demo window 目前是 token/window plan 输出，不是真实可打开 GUI
4. Mini Window 验证目前是 schema/window token 级验证，尚未验证 IME、热键唤出、streaming 刷新、首帧耗时和内存
```

本轮已执行：

```bash
cargo fmt --all --check
cargo test --workspace
cargo run -p win_fluent_gallery
```

结果：

```text
format check passed
12 tests passed
gallery successfully emitted Control Gallery, Mini Window, layout, and resolved theme snapshots
```

### 12.6 Adapter / demo 继续推进结果

本轮在 12.5 基础上继续推进 adapter 和 demo window：

```text
1. 新增 win_fluent_backend_iced crate，引入 iced 0.14.0
2. Iced adapter 已将 ViewToken 编译为真实 iced::Element
3. Iced adapter 覆盖 Page / Text / Button / TextEditor / ToggleSwitch / ComboBox / CommandBar / NavigationView / Dialog / Layout / ScrollView / SettingsRow / ServiceResultCard / ServiceResultList / Custom
4. 新增 win_fluent_platform_win crate，引入 windows-sys 0.61.2
5. Windows platform adapter 已映射 Hotkey / WindowOptions / TrayMenu / ClipboardFormat / ShellVerb / Subscription
6. Windows adapter 提供 RegisterHotKey / UnregisterHotKey 的 RAII handle，实际 Win32 调用留在平台 crate 内部
7. gallery 输出 Mini Window 的 WindowsWindowPlan，包括 style / ex_style / acrylic / skip_taskbar
8. 新增 win_fluent_iced_demo binary，可由 win_fluent token view 驱动 Iced runtime window
9. 删除 core crate 内旧 backend/platform skeleton，renderer/platform 依赖留在独立 adapter crate
```

仍未完成的部分：

```text
1. Iced demo window 已 compile-check，但尚未人工/自动打开窗口验收
2. Iced adapter 当前是基础控件映射，还没有 Fluent 视觉样式、焦点样式、真实 TextEditor 多行编辑、虚拟化列表、diff 驱动局部刷新
3. Windows platform adapter 已有 hotkey/clipboard/window/tray/shell token 的 native plan 和 hotkey RAII，但还没有托盘图标实际 Shell_NotifyIcon 生命周期、Shell registry 写入、真实窗口创建/定位/DWM acrylic 调用
4. Mini Window 验证仍未完成：IME、热键唤出、streaming 刷新、首帧耗时、private bytes、idle CPU 仍需要真实窗口启动后验证
```

本轮已执行：

```bash
cargo info iced@0.14.0
rustc --version
cargo fmt --all --check
cargo test --workspace
$env:CARGO_INCREMENTAL='0'; cargo test --workspace
cargo check -p win_fluent_backend_iced --bin win_fluent_iced_demo
cargo run -p win_fluent_gallery
```

结果：

```text
rustc 1.92.0
format check passed
16 tests passed
win_fluent_iced_demo compile-check passed
gallery successfully emitted WindowsWindowPlan for Mini Window
```

### 12.7 Demo window / TextEditor 继续推进结果

本轮继续推进真实 demo window 和 Iced adapter：

```text
1. Iced adapter 新增 compile_view_with_text_editors，用于接入真实 iced::widget::text_editor::Content
2. TextEditorToken 现在有两条路径：
   - 提供 editor content 时，编译为真实 iced::widget::text_editor，多行编辑可用
   - 未提供 editor content 时，保持 text_input fallback，方便无状态 snapshot / 简单 view 编译
3. win_fluent_iced_demo 已改为使用真实 Iced TextEditor content 状态，编辑 action 会转换回 win_fluent 的 InputChanged(String)
4. Iced adapter 新增 WindowOptions -> iced::window::Settings 映射
5. win_fluent_iced_demo 现在通过 WindowOptions 驱动窗口 size / min_size / level / frame / resize 配置
6. 已短时启动真实 GUI 进程并验证窗口标题、响应状态、内存读数
```

本轮已执行：

```bash
cargo fmt --all --check
$env:CARGO_INCREMENTAL='0'; cargo test --workspace
cargo build -p win_fluent_backend_iced --bin win_fluent_iced_demo
Start-Process target\debug\win_fluent_iced_demo.exe
```

结果：

```text
format check passed
18 tests passed
win_fluent_iced_demo build passed
GUI smoke passed:
  MainWindowTitle = win_fluent Iced Demo
  Responding = true
  TitleSeenMs = 1792
  PrivateMemoryMB = 5.5
  WorkingSetMB = 23.2
```

仍未完成的部分：

```text
1. TextEditor 的 IME 需要人工输入/自动 UI 层验证，当前只证明真实 Iced TextEditor 路径可编译并可启动窗口
2. WindowOptions 的 CursorOffset / TopRight 仍需 Windows runtime 层结合 cursor / monitor work area 实现
3. Mini Window 还没有接真实热键唤出和 streaming 刷新链路
4. 当前内存读数是 demo window smoke，不是 WinUI baseline 对比，也不是最终 Mini Window KPI
```

### 12.8 Mini Window runtime demo 继续推进结果

本轮新增了真实 Mini Window reference runtime：

```text
1. 新增 win_fluent_mini_demo binary
2. Mini demo 使用 WindowOptions:
   - id = mini
   - size = 420x360
   - min_size = 320x220
   - level = TopMost
   - frame = Acrylic
   - placement = CursorOffset(12, 12)
   - skip_taskbar = true
3. Mini demo 使用真实 Iced TextEditor content，对应 token id = mini.input
4. Mini demo 使用 iced::Task 模拟 streaming translation chunks
5. 新增 --auto-stream-exit 模式：窗口启动后自动 streaming，最后一个 chunk 到达后打印 STREAM_DONE 并退出
6. Mini demo 有 token snapshot test 和 WindowOptions -> Iced settings test
```

本轮已执行：

```bash
cargo fmt --all --check
$env:CARGO_INCREMENTAL='0'; cargo test --workspace
cargo build -p win_fluent_backend_iced --bin win_fluent_mini_demo
.\target\debug\win_fluent_mini_demo.exe --auto-stream-exit
Start-Process target\debug\win_fluent_mini_demo.exe
```

结果：

```text
format check passed
20 tests passed
win_fluent_mini_demo build passed
auto-stream runtime validation passed:
  STREAM_DONE generation=1 bytes=110
  exit=0
  elapsed_ms=2726
GUI smoke passed:
  MainWindowTitle = win_fluent Mini Window
  Responding = true
  TitleSeenMs = 636
  PrivateMemoryMB = 5.0
  WorkingSetMB = 22.7
```

仍未完成的部分：

```text
1. 真实全局热键唤出还未接入 Iced/winit event loop
2. IME 仍需要窗口内人工输入或 UI automation 验证
3. streaming 已验证 runtime Task 链路，但还没有截图/像素/自动 UIA 验证结果区域确实逐步刷新
4. 当前内存读数仍是 Rust/Iced Mini demo smoke，不是和 WinUI Mini Window 的同机 baseline 对比
5. CursorOffset / TopRight 的最终屏幕定位仍需 Windows adapter 结合 cursor / monitor work area 实现
```

### 12.9 Windows placement adapter 继续推进结果

本轮补齐了 Mini Window 定位相关的 Windows platform adapter：

```text
1. 新增 WindowsPoint / WindowsRect / ResolvedWindowPlacement runtime model
2. 新增 WindowsPlatformAdapter::resolve_window_placement_for，用纯函数固化 CursorOffset / TopRight / Center / Explicit 的坐标语义
3. 新增 WindowsPlatformAdapter::resolve_window_placement，Windows runtime 通过 GetCursorPos + MonitorFromPoint + GetMonitorInfoW 获取 cursor 和 monitor work area
4. 新增 WindowsPlatformAdapter::plan_window_with_resolved_placement，把 WindowOptions -> WindowsWindowPlan 和 resolved placement 合并
5. gallery 输出 Mini Window 的 resolved placement，便于 demo / smoke 记录当前桌面上的实际落点
```

本轮已执行：

```bash
cargo fmt --all --check
$env:CARGO_INCREMENTAL='0'; cargo test --workspace
cargo run -p win_fluent_gallery
cargo run -p win_fluent_backend_iced --bin win_fluent_mini_demo -- --auto-stream-exit
Start-Process target\debug\win_fluent_mini_demo.exe
rg -i "easydict" lib/winfluent-rs/crates lib/winfluent-rs/README.md
rg "iced::|windows::|Win32|HWND|COM|wgpu|winit" lib/winfluent-rs/crates/win_fluent/src/prelude.rs lib/winfluent-rs/crates/win_fluent/src/view.rs lib/winfluent-rs/crates/win_fluent/src/window.rs lib/winfluent-rs/crates/win_fluent/src/platform.rs
```

结果：

```text
format check passed
25 tests passed
新增 placement 覆盖：
  - CursorOffset near edge clamps inside work area
  - TopRight uses monitor work area and margins
  - Explicit placement stays unclamped
  - Windows runtime resolves current cursor monitor work area
gallery placement snapshot:
  WindowsWindowPlan id=mini style=0x80000000 ex_style=0x00000088 size=420x360 min=Some(320)xSome(220) visible=true skip_taskbar=true acrylic=true placement=420x360@972,552 work=1920x1032@0,0
auto-stream runtime validation passed:
  STREAM_DONE generation=1 bytes=110
GUI smoke passed:
  MainWindowTitle = win_fluent Mini Window
  Responding = true
  PrivateMemoryMB = 5.0
  WorkingSetMB = 22.5
boundary checks passed:
  no Easydict-specific names in winfluent-rs crates / README
  no iced / Windows implementation types leaked through core win_fluent public API files
```

仍未完成的部分：

```text
1. Iced runtime 仍需把 WindowsPlatformAdapter 的 resolved placement 接进实际 window spawn / move 流程；当前已固化 Windows adapter 语义和 gallery smoke 输出
2. 真实全局热键唤出还未接入 Iced/winit event loop
3. IME 仍需要窗口内人工输入或 UI automation 验证
4. streaming 已验证 runtime Task 链路，但还没有截图/像素/自动 UIA 验证结果区域确实逐步刷新
5. 当前内存读数仍是 Rust/Iced Mini demo smoke，不是和 WinUI Mini Window 的同机 baseline 对比
```

### 12.10 Iced Mini Window placement runtime 继续推进结果

本轮把 12.9 固化的 Windows placement 语义接进了 Iced Mini Window runtime：

```text
1. win_fluent_backend_iced 新增 IcedAdapter::window_settings_with_position(options, point)
2. win_fluent_mini_demo 在 Windows 上启动前调用 WindowsPlatformAdapter::resolve_window_placement(options)
3. Mini demo 将 resolved placement 转成 iced::window::Position::Specific
4. Mini demo 新增 --print-placement，用于只解析并打印当前 Windows runtime placement，不启动窗口
5. Mini demo 测试覆盖 resolved placement -> Iced Specific position 的 runtime settings 链路
```

本轮已执行：

```bash
cargo fmt --all --check
$env:CARGO_INCREMENTAL='0'; cargo test --workspace
cargo run -p win_fluent_backend_iced --bin win_fluent_mini_demo -- --print-placement
cargo run -p win_fluent_backend_iced --bin win_fluent_mini_demo -- --auto-stream-exit
Start-Process target\debug\win_fluent_mini_demo.exe
rg -i "easydict" lib/winfluent-rs/crates lib/winfluent-rs/README.md
rg "iced::|windows::|Win32|HWND|COM|wgpu|winit" lib/winfluent-rs/crates/win_fluent/src/prelude.rs lib/winfluent-rs/crates/win_fluent/src/view.rs lib/winfluent-rs/crates/win_fluent/src/window.rs lib/winfluent-rs/crates/win_fluent/src/platform.rs
```

结果：

```text
format check passed
27 tests passed
print-placement runtime validation passed:
  PLACEMENT width=420 height=360 x=731 y=492 work=1440x852@0,0
auto-stream runtime validation passed:
  STREAM_DONE generation=1 bytes=110
Win32 window rectangle placement smoke passed:
  SetCursorPos(1400, 820)
  reported placement: x=1020 y=492 work=1440x852@0,0
  GetWindowRect: left=1020 top=492 width=420 height=360
  DeltaX = 0
  DeltaY = 0
  MainWindowTitle = win_fluent Mini Window
  Responding = true
  PrivateMemoryMB = 7.6
  WorkingSetMB = 25.3
boundary checks passed:
  no Easydict-specific names in winfluent-rs crates / README
  no iced / Windows implementation types leaked through core win_fluent public API files
```

仍未完成的部分：

```text
1. 真实全局热键唤出还未接入 Iced/winit event loop
2. IME 仍需要窗口内人工输入或 UI automation 验证
3. streaming 已验证 runtime Task 链路，但还没有截图/像素/自动 UIA 验证结果区域确实逐步刷新
4. 当前内存读数仍是 Rust/Iced Mini demo smoke，不是和 WinUI Mini Window 的同机 baseline 对比
```

### 12.11 Mini Window visible streaming 验证继续推进结果

本轮补齐了 Mini Window 的可见渲染和 streaming 结果区域截图验证：

```text
1. win_fluent_backend_iced 的 Page token 现在渲染 full-window light surface，并设置默认深色文本色
2. Mini demo 新增 --auto-stream-stay，支持自动 streaming 后保持窗口打开，便于外部截图 / UI 验证
3. Mini demo 新增 --stream-delay-ms=N，默认 90ms，验证时可放慢 streaming chunk，范围限制为 1..=5000ms
4. Mini demo 将 TextEditor 高度约束为 56 logical units，避免高 DPI 下输入框占满 420x360 窗口
5. Mini demo 将 ServiceResultList 移到 CommandBar 前面，确保 streaming 结果区域处于首屏可见范围
6. 新增单元覆盖：auto-stream-stay 不自动退出、stream delay 参数边界、Mini snapshot 固化 max_height=56
```

本轮已执行：

```bash
cargo fmt --all --check
$env:CARGO_INCREMENTAL='0'; cargo test --workspace
cargo build -p win_fluent_backend_iced --bin win_fluent_mini_demo
cargo run -p win_fluent_backend_iced --bin win_fluent_mini_demo -- --auto-stream-exit
Start-Process target\debug\win_fluent_mini_demo.exe -- --auto-stream-stay --stream-delay-ms=1000
PrintWindow before/after capture + bitmap diff
rg -i "easydict" lib/winfluent-rs/crates lib/winfluent-rs/README.md
rg "iced::|windows::|Win32|HWND|COM|wgpu|winit" lib/winfluent-rs/crates/win_fluent/src/prelude.rs lib/winfluent-rs/crates/win_fluent/src/view.rs lib/winfluent-rs/crates/win_fluent/src/window.rs lib/winfluent-rs/crates/win_fluent/src/platform.rs
```

结果：

```text
format check passed
29 tests passed
auto-stream runtime validation passed:
  STREAM_DONE generation=1 bytes=110
PrintWindow visible streaming validation passed:
  Before = C:\tmp\winfluent-mini-validation\mini-before-stream-printwindow-result-first.png
  After  = C:\tmp\winfluent-mini-validation\mini-after-stream-printwindow-result-first.png
  MainWindowTitle = win_fluent Mini Window
  Rect = 1020,492 420x360
  Responding = true
  ChangedPixels = 3519
  TotalDelta = 1305222
  ResultRegionTop = 126
  ResultRegionChangedPixels = 3519
  ResultRegionDelta = 1304993
  PrivateMemoryMB = 7.6
  WorkingSetMB = 25.4
visual inspection:
  before image shows Demo Provider (Streaming) with "Press Translate to start..."
  after image shows Demo Provider (Streaming) with "Streaming translation for:"
boundary checks passed:
  no Easydict-specific names in winfluent-rs crates / README
  no iced / Windows implementation types leaked through core win_fluent public API files
```

仍未完成的部分：

```text
1. 真实全局热键唤出还未接入 Iced/winit event loop
2. IME 仍需要窗口内人工输入或 UI automation 验证
3. 当前内存读数仍是 Rust/Iced Mini demo smoke，不是和 WinUI Mini Window 的同机 baseline 对比
4. 当前截图验证使用 Win32 PrintWindow；UIA 可访问性树验证仍未建立
```

### 12.12 Windows global hotkey message loop 继续推进结果

本轮补齐了 Windows platform adapter 的全局热键消息接收链路：

```text
1. 新增 WindowsHotkeyEvent，包含 token id、native_id、modifiers、virtual_key
2. 新增 WindowsPlatformAdapter::wait_for_hotkey_event(handles, timeout)，通过 PeekMessageW 轮询当前线程 WM_HOTKEY，并把 native id 映射回 token id
3. 新增 WindowsPlatformAdapter::send_hotkey_input_for_probe，用 SendInput 发送受控的 synthetic hotkey input，用于 runtime probe
4. 新增 win_fluent_hotkey_probe binary：
   - 注册 Ctrl+Alt+Shift+F24
   - 发送同一组合键的 synthetic input
   - 等待 WM_HOTKEY
   - 打印 HOTKEY_REGISTERED / HOTKEY_RECEIVED
5. 单元测试新增：
   - native WM_HOTKEY message -> token id 映射
   - 注册 hotkey 后接收当前线程 WM_HOTKEY message
```

本轮已执行：

```bash
cargo fmt --all --check
$env:CARGO_INCREMENTAL='0'; cargo test --workspace
cargo run -p win_fluent_platform_win --bin win_fluent_hotkey_probe
rg -i "easydict" lib/winfluent-rs/crates lib/winfluent-rs/README.md
rg "iced::|windows::|Win32|HWND|COM|wgpu|winit" lib/winfluent-rs/crates/win_fluent/src/prelude.rs lib/winfluent-rs/crates/win_fluent/src/view.rs lib/winfluent-rs/crates/win_fluent/src/window.rs lib/winfluent-rs/crates/win_fluent/src/platform.rs
```

结果：

```text
format check passed
31 tests passed
hotkey probe passed:
  HOTKEY_REGISTERED id=hotkey-probe native_id=11702 modifiers=0x7 vk=0x87
  HOTKEY_RECEIVED id=hotkey-probe native_id=11702 modifiers=0x7 vk=0x87
boundary checks passed:
  no Easydict-specific names in winfluent-rs crates / README
  no iced / Windows implementation types leaked through core win_fluent public API files
```

仍未完成的部分：

```text
1. Windows adapter 已验证 RegisterHotKey -> WM_HOTKEY 接收链路，但尚未接进 Iced/winit runtime 的事件分发
2. Mini Window 还没有通过真实热键触发 show/focus/toggle
3. IME 仍需要窗口内人工输入或 UI automation 验证
4. 当前内存读数仍是 Rust/Iced Mini demo smoke，不是和 WinUI Mini Window 的同机 baseline 对比
5. UIA 可访问性树验证仍未建立
```

### 12.13 Iced Mini hotkey subscription 继续推进结果

本轮把 Windows global hotkey adapter 接进了 Iced Mini demo 的 runtime 消息分发，并把 bridge 从 demo bin 抽到了 Iced backend API：

```text
1. Iced backend 新增 IcedHotkeyEvent，统一表达 Pressed / Error
2. IcedAdapter 新增 hotkey_subscription(Hotkey)，由 backend 负责创建 Iced Subscription
3. backend subscription 在独立 OS thread 注册 Ctrl+Alt+Shift+F24，并轮询 WindowsPlatformAdapter::wait_for_hotkey_event
4. Mini demo 只调用 IcedAdapter::hotkey_subscription(mini_hotkey()).map(map_hotkey_event)，不再手写 Windows 注册/轮询逻辑
5. Mini update 收到 HotkeyPressed 后进入同一条 start_translation 流程，复用现有流式结果更新路径
6. 新增 --hotkey-probe-exit smoke 模式：
   - 启动 Mini Iced window
   - 延迟发送 synthetic Ctrl+Alt+Shift+F24
   - 收到热键后触发流式翻译
   - 流式完成后打印 HOTKEY_STREAM_DONE 并退出
7. 新增热键错误、probe 发送失败、probe 超时的退出路径，避免 smoke 卡死
8. 单元测试新增：
   - backend hotkey subscription data 可以 round-trip 回 token Hotkey
   - hotkey probe 模式会启用 hotkey，但不会初始 auto-stream
   - HotkeyPressed 会增加计数并启动 streaming translation
```

本轮已执行：

```bash
cargo fmt --all
cargo fmt --all --check
$env:CARGO_INCREMENTAL='0'; cargo test --workspace
cargo run -p win_fluent_backend_iced --bin win_fluent_mini_demo -- --hotkey-probe-exit
rg -i "easydict" lib/winfluent-rs/crates lib/winfluent-rs/README.md
rg "iced::|windows::|Win32|HWND|COM|wgpu|winit" lib/winfluent-rs/crates/win_fluent/src/prelude.rs lib/winfluent-rs/crates/win_fluent/src/view.rs lib/winfluent-rs/crates/win_fluent/src/window.rs lib/winfluent-rs/crates/win_fluent/src/platform.rs
```

结果：

```text
format check passed
34 tests passed
Mini hotkey smoke passed:
  HOTKEY_PROBE_SENT
  HOTKEY_TRIGGERED id=mini.translate count=1
  HOTKEY_STREAM_DONE hotkeys=1 generation=1 bytes=110
boundary checks passed:
  no Easydict-specific names in winfluent-rs crates / README
  no iced / Windows implementation types leaked through core win_fluent public API files
```

仍未完成的部分：

```text
1. Mini demo 已验证真实 global hotkey -> Iced update -> streaming action，但还没有实现 show/focus/toggle 窗口生命周期
2. IME 仍需要窗口内人工输入或 UI automation 验证
3. 当前内存读数仍是 Rust/Iced Mini demo smoke，不是和 WinUI Mini Window 的同机 baseline 对比
4. UIA 可访问性树验证仍未建立
```

### 12.14 Mini Window lifecycle smoke 继续推进结果

本轮补齐了 Mini demo 的窗口生命周期 smoke，验证热键不仅能进入 Iced update，还能驱动窗口从 minimized 状态恢复并继续执行流式翻译：

```text
1. Mini demo 新增 --lifecycle-probe-exit
2. lifecycle probe 启动后通过 iced_window::latest 获取当前窗口 id，并打印 raw window id
3. probe 先调用 iced_window::minimize(id, true)，再用 iced_window::is_minimized(id) 验证 minimized=Some(true)
4. 窗口最小化验证通过后，发送 synthetic Ctrl+Alt+Shift+F24
5. HotkeyPressed 进入 Mini update 后：
   - 调用 iced_window::minimize(id, false) 恢复窗口
   - 调用 iced_window::gain_focus(id) 请求前台 focus
   - 启动同一条 streaming translation 流程
6. probe 再用 iced_window::is_minimized(id) 验证 restored minimized=Some(false)
7. 只有窗口最小化、热键触发、streaming 完成、窗口恢复全部成立时才打印 WINDOW_LIFECYCLE_DONE 并退出
8. 新增单元覆盖：
   - lifecycle probe 模式启用 hotkey 和退出检查，但不自动 streaming
   - lifecycle minimized/restored 状态会被记录到 MiniState
```

本轮已执行：

```bash
cargo fmt --all
cargo fmt --all --check
$env:CARGO_INCREMENTAL='0'; cargo test --workspace
cargo run -p win_fluent_backend_iced --bin win_fluent_mini_demo -- --lifecycle-probe-exit
rg -i "easydict" lib/winfluent-rs/crates lib/winfluent-rs/README.md
rg "iced::|windows::|Win32|HWND|COM|wgpu|winit" lib/winfluent-rs/crates/win_fluent/src/prelude.rs lib/winfluent-rs/crates/win_fluent/src/view.rs lib/winfluent-rs/crates/win_fluent/src/window.rs lib/winfluent-rs/crates/win_fluent/src/platform.rs
```

结果：

```text
format check passed
36 tests passed
Mini lifecycle smoke passed:
  WINDOW_LIFECYCLE_FOUND id=Id(1)
  WINDOW_RAW_ID raw_id=983930
  WINDOW_MINIMIZED minimized=Some(true) verified=true
  HOTKEY_PROBE_SENT
  HOTKEY_TRIGGERED id=mini.translate count=1
  HOTKEY_STREAM_DONE hotkeys=1 generation=1 bytes=110
  WINDOW_RESTORED minimized=Some(false) verified=true
  WINDOW_LIFECYCLE_DONE minimized_seen=true restored_seen=true hotkey_stream_done=true
boundary checks passed:
  no Easydict-specific names in winfluent-rs crates / README
  no iced / Windows implementation types leaked through core win_fluent public API files
```

仍未完成的部分：

```text
1. Mini lifecycle smoke 已验证 minimized -> global hotkey -> restore/focus request -> streaming，但 focus 本身没有独立可查询状态断言
2. IME 仍需要窗口内人工输入或 UI automation 验证
3. 当前内存读数仍是 Rust/Iced Mini demo smoke，不是和 WinUI Mini Window 的同机 baseline 对比
4. UIA 可访问性树验证仍未建立
```

## 13. 下一 milestone 补充事项

这两个点需要纳入后续 milestone，但不阻塞当前 token/schema/backend adapter/Mini smoke 的推进：

### 13.1 Fluent UI visual parity

```text
scope 判断：
  属于 win-fluent-rs 的质量目标，但应作为后续视觉对齐 milestone 单独推进。

输入依据：
  现有 screenshot/ 目录下的应用截图作为视觉参考，包括：
    - screenshot/main-window.png
    - screenshot/all-windows.png
    - screenshot/light-dark-mode.png
    - screenshot/mouse-selection.png
    - screenshot/ocr-screenshot.png
    - screenshot/settings.png

目标：
  1. 提炼 WinUI/Fluent-like 视觉 token：
     - color roles
     - typography scale
     - spacing/radius/elevation
     - acrylic / mica-like surface 表达
     - light/dark theme 对照
  2. 对齐主要窗口的视觉结构：
     - Main Window
     - Mini Window
     - Fixed Window
     - Settings
     - OCR / mouse selection 相关浮层
  3. 建立 golden screenshot / screenshot diff 流程：
     - 先做结构和布局断言
     - 再做宽容阈值的截图 diff
     - 避免把 GPU/backend 字体抗锯齿差异当成失败

非目标：
  1. 不做逐像素复刻 WinUI。
  2. 不把 screenshot 里的 EasyDict 品牌/业务文案写进 win_fluent core。
  3. 不让 core API 暴露 Iced/wgpu/winit/Win32 类型。
```

### 13.2 ARM64 compatibility and cross compile

```text
scope 判断：
  属于平台兼容和发布工程 milestone，不应只靠当前 x64 Windows smoke 判断完成。

目标：
  1. 确认 crate graph 支持 Windows x64 和 Windows ARM64：
     - x86_64-pc-windows-msvc
     - aarch64-pc-windows-msvc
  2. 明确每个 backend/platform 依赖的 ARM64 状态：
     - iced / winit / tiny-skia / wgpu 或 softbuffer backend
     - windows-sys / windows-rs bindings
     - global hotkey
     - tray / shell verb
     - UIA
     - IME
     - screenshot / capture / DWM effects
  3. 建立 cross compile gate：
     - cargo check --target x86_64-pc-windows-msvc
     - cargo check --target aarch64-pc-windows-msvc
     - 后续 CI matrix 覆盖两个 target
  4. 建立 runtime gate：
     - x64 本机 smoke
     - ARM64 Windows 设备或 ARM64 VM smoke
     - 至少覆盖 Mini Window、hotkey、window placement、theme、IME/UIA 中的关键路径
  5. 明确产物策略：
     - x64 / arm64 分架构包
     - MSIX / winget / standalone zip 的架构命名和签名策略

风险：
  1. cross compile 只能证明编译期兼容，不能证明 Windows ARM64 runtime 行为。
  2. 全局热键、IME、UIA、窗口特效必须有 ARM64 真机或 VM runtime evidence。
  3. 如果后续启用 wgpu/DX12 backend，需要单独验证 ARM64 GPU/driver 差异。
```

[1]: https://docs.rs/iced/latest/iced/ "iced - Rust"
[2]: https://docs.rs/iced/latest/iced/fn.daemon.html "daemon in iced - Rust"
[3]: https://github.com/xiaocang/easydict_win32 "GitHub - xiaocang/easydict_win32: Easy to look up words or translate text. Windows port of tisfeng/Easydict. · GitHub"
[4]: https://learn.microsoft.com/en-us/windows/win32/procthread/process-working-set?utm_source=chatgpt.com "Process Working Set - Win32 apps"
[5]: https://github.com/volks73/cargo-wix "GitHub - volks73/cargo-wix: A cargo subcommand to build Windows installers for rust projects using the WiX Toolset · GitHub"
[6]: https://axodotdev.github.io/cargo-dist/ "cargo-dist"
[7]: https://learn.microsoft.com/en-us/windows/msix/overview "What is MSIX? - MSIX | Microsoft Learn"
[8]: https://learn.microsoft.com/en-us/windows/package-manager/package/ "Submit packages to Windows Package Manager | Microsoft Learn"
[9]: https://docs.rs/insta "insta - Rust"
[10]: https://docs.rs/trybuild "trybuild - Rust"
[11]: https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-uiautomationoverview?utm_source=chatgpt.com "UI Automation Overview - Win32 apps"
[12]: https://github.com/AccessKit/accesskit "GitHub - AccessKit/accesskit: Accessibility infrastructure for UI toolkits · GitHub"
[13]: https://docs.rs/criterion/latest/criterion/?utm_source=chatgpt.com "criterion - Rust"
[14]: https://nexte.st/ "Home - cargo-nextest"
