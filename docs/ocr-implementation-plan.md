# Easydict Win32 — OCR 实现方案

## 一、需求调研

### 1.1 V2EX 帖子需求分析 (v2ex.com/t/910296)

该帖子的核心问题是：**Windows 平台上缺少类似 macOS Bob / Easydict 的集「划词翻译 + OCR 截图翻译」于一体的工具**。用户需求可归纳为：

| 需求 | 说明 |
|------|------|
| **截图 OCR 翻译** | 快捷键截屏 → 识别文字 → 自动翻译，一键完成 |
| **静默截图 OCR** | 截屏后文字直接复制到剪贴板，不弹翻译窗口 |
| **离线 OCR** | 不依赖网络即可识别文字（本地引擎） |
| **多语言识别** | 中文、英文、日文、韩文等常见语言 |
| **与翻译流程无缝集成** | OCR 结果直接进入已有翻译管线，支持多服务并行查询 |
| **全局快捷键触发** | 和划词翻译一样，随时可用 |

社区推荐的 Windows 替代工具（STranslate、Pot Desktop、Capture2Text、Text Grab）均印证了以上需求模式。

### 1.2 macOS Easydict OCR 实现参考

macOS 版 Easydict 的 OCR 功能架构：

```
快捷键 (⌥+S / ⌥+⇧+S)
    ↓
截屏模块 (Swift ScreenCapture)   ← 需要屏幕录制权限
    ↓
Apple Vision Framework (VNRecognizeTextRequest)   ← 离线 OCR
    ├─ 自动语言检测
    ├─ 支持 12 种语言
    └─ 可手动指定识别语言（修正误识别）
    ↓
├─ 标准模式：OCR 文本 → 翻译管线 → 显示翻译结果
└─ 静默模式：OCR 文本 → 复制到剪贴板
```

**关键特性：**
- **两种 OCR 模式**：截图翻译（⌥+S）和静默截图（⌥+⇧+S）
- **系统级离线 OCR**：使用 Apple Vision 的 `VNRecognizeTextRequest`，无需网络
- **语言纠正**：用户可点击"检测到 xxx 语言"按钮手动指定识别语言
- **与翻译无缝集成**：OCR 文本直接送入现有翻译管线

---

## 二、Windows 平台 OCR 技术选型

### 2.1 可用 OCR 引擎对比

| 引擎 | 类型 | 优点 | 缺点 | 推荐度 |
|------|------|------|------|--------|
| **Windows.Media.Ocr** (WinRT) | 系统内置、离线 | 无需额外依赖；26 种语言；CPU 即可运行；Win10+ 可用 | 准确率中等；不支持 NPU 加速 | ★★★★★ **首选** |
| **Windows App SDK TextRecognizer** | 系统 AI | NPU 加速；更高准确率 | 仅 Win11 + App SDK 1.6+；需 Copilot+ PC 硬件 | ★★★ 未来增强 |
| **Tesseract OCR** | 开源离线 | 完全跨平台；100+ 语言；可自定义模型 | 需打包训练数据（~30MB/语言）；准确率依赖预处理 | ★★★★ 备选 |
| **PaddleOCR** | 开源离线 | 高准确率（尤其中文）；支持版面分析 | 依赖较重（Python/C++）；包体大 | ★★★ 高级备选 |
| **云端 OCR** (百度/腾讯/Google) | 在线 | 准确率最高；支持复杂场景 | 需网络；有调用限制/费用；隐私顾虑 | ★★ 可选增强 |

### 2.2 推荐方案：分层架构

```
┌─────────────────────────────────────────────┐
│                 OCR 管理层                    │
│   OcrService (统一接口，引擎切换/降级)        │
├─────────────────────────────────────────────┤
│  Tier 1: Windows.Media.Ocr (默认，离线)      │
│  Tier 2: Tesseract (可选，离线，更多语言)     │
│  Tier 3: 云端 OCR (可选，在线，高准确率)      │
└─────────────────────────────────────────────┘
```

**首个版本聚焦 Tier 1（Windows.Media.Ocr）**，因为：
- 零额外依赖，WinUI 3 应用可直接调用 WinRT API
- 开箱即用，无需用户安装额外组件
- 离线运行，保护隐私
- 26 种语言覆盖绝大多数需求

---

## 三、实现方案

### 3.1 总体架构

```
                        ┌──────────────────────────┐
                        │      HotkeyService       │
                        │  Ctrl+Alt+S → OCR 翻译   │
                        │  Ctrl+Alt+Shift+S → 静默  │
                        └────────────┬─────────────┘
                                     │
                        ┌────────────▼─────────────┐
                        │    ScreenCaptureService   │
                        │  创建覆盖窗口 → 用户框选   │
                        │  → 返回截图 Bitmap         │
                        └────────────┬─────────────┘
                                     │
                        ┌────────────▼─────────────┐
                        │       OcrService          │
                        │  SoftwareBitmap → 文字     │
                        │  语言检测 + 文字行合并      │
                        └────────────┬─────────────┘
                                     │
                     ┌───────────────┼───────────────┐
                     │                               │
           ┌─────────▼────────┐            ┌─────────▼────────┐
           │   标准 OCR 翻译   │            │    静默 OCR       │
           │ MiniWindow 显示   │            │  复制到剪贴板      │
           │ 翻译结果          │            │  Toast 通知        │
           └──────────────────┘            └──────────────────┘
```

### 3.2 模块设计

#### 3.2.1 ScreenCaptureService — 截图服务

**职责**：全屏覆盖 → Snipaste 风格交互（自动窗口检测 + 自由框选） → 返回选区截图

**交互设计参考 [Snipaste](https://zh.snipaste.com/)**，实现与其一致的截图取图体验。

```csharp
namespace Easydict.WinUI.Services;

public sealed class ScreenCaptureService
{
    /// <summary>
    /// 启动截图流程（Snipaste 风格交互），用户框选后返回截图和区域信息。
    /// 如果用户按 Esc 取消，返回 null。
    /// </summary>
    public Task<ScreenCaptureResult?> CaptureRegionAsync();
}

public record ScreenCaptureResult
{
    /// <summary>截图位图 (BGRA8 格式)</summary>
    public SoftwareBitmap Bitmap { get; init; }

    /// <summary>截取区域在屏幕上的位置（物理像素）</summary>
    public Rect ScreenRect { get; init; }
}
```

**实现方案**：

1. **抓取全屏**：使用 Win32 `BitBlt` + GDI 捕获整个虚拟桌面（所有显示器），冻结为静态背景图
2. **覆盖窗口**：创建 Win32 原生全屏无边框顶层窗口（`WS_EX_TOPMOST | WS_EX_TOOLWINDOW`），显示冻结的桌面截图 + 半透明暗色遮罩
3. **Snipaste 风格交互**（详见 3.2.3 节）：
   - 鼠标悬浮时自动检测窗口/元素区域并高亮
   - 单击确认自动检测的区域，或拖拽自由框选
   - 选区确认后显示控制手柄，可拖拽调整
   - 放大镜 + 十字线辅助精确定位
   - 键盘方向键微调选区边界
4. **裁切返回**：根据选区坐标从冻结的全屏截图中裁切出目标区域
5. **多显示器支持**：创建一个横跨虚拟桌面的覆盖窗口
6. **DPI 感知**：使用物理像素坐标，处理 Per-Monitor V2 DPI 差异

**关键设计决策**：
- 使用 Win32 原生窗口（而非 WinUI 3 窗口）作为覆盖层，避免 WinUI 3 窗口创建延迟和焦点问题
- 绘制使用 GDI+ 或 Direct2D，保证高帧率流畅渲染
- 参考 Snipaste 的交互模式，同时参考 PowerToys Text Extractor / Text Grab 的截图实现

#### 3.2.2 OcrService — OCR 识别服务

**职责**：图像 → 识别文字 + 语言检测

```csharp
namespace Easydict.WinUI.Services;

public sealed class OcrService
{
    /// <summary>
    /// 对图像进行 OCR 识别。
    /// </summary>
    /// <param name="bitmap">待识别图像</param>
    /// <param name="preferredLanguage">用户指定的识别语言（null 表示自动检测）</param>
    public Task<OcrResult> RecognizeAsync(
        SoftwareBitmap bitmap,
        OcrLanguage? preferredLanguage = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取当前系统支持的 OCR 语言列表。
    /// </summary>
    public IReadOnlyList<OcrLanguage> GetAvailableLanguages();
}

public record OcrResult
{
    /// <summary>识别出的完整文本（行已合并）</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>识别出的各行文本（保留原始行结构）</summary>
    public IReadOnlyList<OcrLine> Lines { get; init; } = [];

    /// <summary>检测到的语言</summary>
    public OcrLanguage? DetectedLanguage { get; init; }

    /// <summary>识别角度（图像旋转补偿）</summary>
    public double? TextAngle { get; init; }
}

public record OcrLine
{
    public string Text { get; init; } = string.Empty;
    public Rect BoundingRect { get; init; }
}

public record OcrLanguage
{
    public string Tag { get; init; } = string.Empty;       // e.g. "zh-Hans-CN"
    public string DisplayName { get; init; } = string.Empty; // e.g. "简体中文"
}
```

**实现要点**：

1. **Windows.Media.Ocr 调用流程**：
   ```
   SoftwareBitmap
     → OcrEngine.TryCreateFromLanguage(language)
     → engine.RecognizeAsync(bitmap)
     → OcrResult { Lines: [ OcrLine { Words: [ OcrWord ] } ] }
   ```

2. **语言自动检测策略**：
   - 首先尝试 `OcrEngine.TryCreateFromUserProfileLanguages()`
   - 如果识别结果为空或置信度低，遍历已安装的其他 OCR 语言重试
   - 用户可手动指定语言覆盖自动检测

3. **文本行合并**：
   - 按 `OcrLine` 的 Y 坐标分组（容差阈值 = 行高 × 0.5）
   - 同一行内按 X 坐标排序
   - 行间用换行符连接
   - 处理中日韩文本不加空格、西文单词间加空格的差异

4. **图像预处理（可选增强）**：
   - 二值化提高对比度
   - 自动旋转校正（利用 `OcrResult.TextAngle`）

#### 3.2.3 ScreenCaptureWindow — 截图覆盖窗口（Snipaste 风格）

**职责**：提供 Snipaste 风格的截图区域选择交互

**参考**: [Snipaste](https://zh.snipaste.com/) 的截图交互是 Windows 平台上公认体验最好的截图工具之一。OCR 截图模块的交互设计完全对标 Snipaste，包括自动窗口检测、自由框选、放大镜、选区调整等核心体验。

##### 视觉布局

```
┌──────────────────────────────────────────────────────────┐
│  ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░ │  ← 半透明暗色遮罩 (冻结桌面)
│  ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░ │
│  ░░░░░░░░┌━━━━━━━━━━━━━━━━━━━━━┐░░░░░░░░░░░░░░░░░░░░░ │
│  ░░░░░░░░┃                     ┃░░░░░░░░░░░░░░░░░░░░░ │  ← 选区 (清晰原始画面，边框高亮)
│  ░░░░░░░░┃    选区区域          ┃░░░░░░░░░░░░░░░░░░░░░ │
│  ░░░░░░░░┃                     ┃░░░░░░░░░░░░░░░░░░░░░ │
│  ░░░░░░░░┗━━━━━━━━━━━━━━━━━━━━━┛░░░░░░░░░░░░░░░░░░░░░ │
│  ░░░░░░░░  640 × 320              ░░░░░░░░░░░░░░░░░░░░ │  ← 选区尺寸提示
│  ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░ │
│  ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░ ┌──────────┐░░░ │
│  ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░ │ 🔍 放大镜 │░░░ │  ← 放大镜 + 十字线 + 坐标/颜色
│  ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░ │  (255,128) │░░░ │
│  ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░ │  #FF8040   │░░░ │
│  ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░ └──────────┘░░░ │
└──────────────────────────────────────────────────────────┘
```

##### 完整交互流程（对标 Snipaste）

**阶段一：进入截图模式**
1. 快捷键触发 → 用 GDI `BitBlt` 冻结当前桌面为静态背景图
2. 创建全屏覆盖窗口，显示冻结画面 + 半透明暗色遮罩（约 40% 不透明度）
3. 鼠标变为十字光标（Crosshair）
4. 右下角显示放大镜面板（跟随鼠标移动）

**阶段二：自动窗口/元素检测（鼠标悬浮）**
5. 鼠标移动时，自动检测鼠标下方的窗口/UI 元素区域并实时高亮
   - 使用 `EnumWindows` 在截图开始时构建窗口层次快照（Z-Order + Rect）
   - 使用 `ChildWindowFromPointEx` 递归查找子窗口
   - 检测到的区域用高亮边框标识，区域内去掉遮罩显示清晰原始画面
6. **鼠标滚轮**：在父窗口/子窗口层级间切换，精确选择不同粒度的元素
7. **单击**：确认自动检测到的区域作为选区 → 跳到阶段四

**阶段三：自由框选（鼠标拖拽）**
8. 按住鼠标左键拖拽：从按下点开始绘制矩形选区
   - 选区内去掉遮罩，显示清晰原始画面
   - 选区边框用醒目颜色（如 Snipaste 的绿色/蓝色边框）
   - 选区左上角或下方显示实时尺寸 `W × H`
9. 释放鼠标：选区确定 → 跳到阶段四

**阶段四：选区确认与调整**
10. 选区确定后，四边和四角显示 **8 个控制手柄**（拖拽调整选区大小）
11. 鼠标在选区内部拖拽 → 整体移动选区
12. **键盘微调**：
    - `↑↓←→`：移动选区（1px/次）
    - `Ctrl + ↑↓←→`：扩大选区对应边界（1px/次）
    - `Shift + ↑↓←→`：缩小选区对应边界（1px/次）
13. **确认截图**：
    - `Enter` 或 双击选区：确认选区，执行 OCR
    - 选区下方可显示工具条（包含「确认」「取消」按钮）

**取消操作**：
- `Esc`：随时取消截图
- 右键单击：取消截图
- 在选区外单击（未开始拖拽时）：取消当前选区，回到检测模式

##### 放大镜面板

放大镜是 Snipaste 精确截图的关键交互，跟随鼠标右下角显示：

```
┌─────────────────┐
│  ╬ ╬ ╬ ╬ ╬ ╬ ╬ │  ← 鼠标周围 ~11×11 像素区域，放大 8~10 倍
│  ╬ ╬ ╬ ╬ ╬ ╬ ╬ │     中心十字线标记精确鼠标位置
│  ╬ ╬ ╬ ┼ ╬ ╬ ╬ │     显示像素网格
│  ╬ ╬ ╬ ╬ ╬ ╬ ╬ │
│  ╬ ╬ ╬ ╬ ╬ ╬ ╬ │
├─────────────────┤
│  (1920, 1080)   │  ← 当前鼠标坐标（物理像素）
│  #FF8040  ████  │  ← 鼠标所在像素的颜色值 + 色块预览
└─────────────────┘
```

**放大镜实现要点**：
- 从冻结的全屏截图中取鼠标周围 11×11 像素区域
- 使用 `StretchBlt` 或手动逐像素放大至 ~90×90 显示区域
- 在放大图上绘制像素网格线和十字准心
- 面板跟随鼠标，当靠近屏幕边缘时自动切换到对侧显示

##### 窗口自动检测实现

```csharp
/// <summary>
/// 在截图开始时构建窗口层次快照。
/// 参考 Snipaste / QQ 截图的窗口检测方案。
/// </summary>
public class WindowDetector
{
    private readonly List<WindowInfo> _windowSnapshots = new();

    /// <summary>
    /// 枚举所有可见窗口及其子窗口，按 Z-Order 排序，缓存 Rect 信息。
    /// 在截图开始时调用一次，后续使用快照数据（不受下层窗口变化影响）。
    /// </summary>
    public void TakeSnapshot()
    {
        _windowSnapshots.Clear();
        // EnumWindows 按 Z-Order 从前到后枚举
        EnumWindows((hwnd, lParam) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (IsOwnWindow(hwnd)) return true; // 过滤自己的覆盖窗口

            GetWindowRect(hwnd, out var rect);
            var info = new WindowInfo { Hwnd = hwnd, Rect = rect };

            // 递归收集子窗口
            EnumChildWindows(hwnd, (childHwnd, _) =>
            {
                if (!IsWindowVisible(childHwnd)) return true;
                GetWindowRect(childHwnd, out var childRect);
                info.Children.Add(new WindowInfo { Hwnd = childHwnd, Rect = childRect });
                return true;
            }, IntPtr.Zero);

            _windowSnapshots.Add(info);
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>
    /// 根据鼠标位置查找最匹配的窗口/元素区域。
    /// 支持 depth 参数控制检测粒度（滚轮切换父/子窗口层级）。
    /// </summary>
    public Rectangle? FindRegionAtPoint(Point screenPoint, int depth = 0);
}
```

##### 选区调整手柄

```
     ●─────────────────●─────────────────●
     │                                   │
     │                                   │
     ●             选区区域              ●    ← 8 个控制手柄
     │                                   │        (4 角 + 4 边中点)
     │                                   │
     ●─────────────────●─────────────────●
```

- 角上的手柄：对角线方向缩放
- 边上的手柄：单边方向缩放
- 选区内拖拽：整体平移
- 手柄大小约 8×8 像素，鼠标靠近时光标变为对应方向的缩放箭头

##### 截图模式下的完整快捷键表

| 快捷键 | 功能 | 对标 Snipaste |
|--------|------|--------------|
| `Esc` | 取消截图 | ✓ |
| 右键 | 取消截图 | ✓ |
| `Enter` | 确认选区，执行 OCR | ✓ (Snipaste: 复制) |
| `↑↓←→` | 移动选区 1px | ✓ |
| `Ctrl + ↑↓←→` | 扩大选区对应边界 1px | ✓ |
| `Shift + ↑↓←→` | 缩小选区对应边界 1px | ✓ |
| 鼠标滚轮 | 切换窗口检测层级（父/子） | ✓ |
| `Ctrl + A` | 选区设为全屏 | ✓ |
| `Tab` | 切换窗口检测/元素检测模式 | ✓ (Snipaste Pro) |

#### 3.2.4 OcrTranslateService — OCR 翻译编排

**职责**：串联截图 → OCR → 翻译的完整流程

```csharp
namespace Easydict.WinUI.Services;

public sealed class OcrTranslateService
{
    private readonly ScreenCaptureService _capture;
    private readonly OcrService _ocr;

    /// <summary>
    /// 执行截图 OCR 翻译。
    /// 截图 → OCR → 在 MiniWindow 中显示并翻译。
    /// </summary>
    public async Task OcrTranslateAsync()
    {
        // 1. 截图
        var captureResult = await _capture.CaptureRegionAsync();
        if (captureResult is null) return; // 用户取消

        // 2. OCR
        var ocrResult = await _ocr.RecognizeAsync(captureResult.Bitmap);
        if (string.IsNullOrWhiteSpace(ocrResult.Text)) return; // 无文字

        // 3. 在 MiniWindow 显示并触发翻译
        MiniWindowService.Instance.ShowWithText(ocrResult.Text);
    }

    /// <summary>
    /// 执行静默截图 OCR。
    /// 截图 → OCR → 复制到剪贴板。
    /// </summary>
    public async Task SilentOcrAsync()
    {
        var captureResult = await _capture.CaptureRegionAsync();
        if (captureResult is null) return;

        var ocrResult = await _ocr.RecognizeAsync(captureResult.Bitmap);
        if (string.IsNullOrWhiteSpace(ocrResult.Text)) return;

        // 复制到剪贴板
        ClipboardService.SetText(ocrResult.Text);

        // 显示 Toast 通知（可选）
        ToastService.Show("OCR 完成", ocrResult.Text);
    }
}
```

#### 3.2.5 快捷键集成

在 `HotkeyService` 中新增两个快捷键：

| ID | 默认快捷键 | 功能 |
|----|-----------|------|
| 7 | `Ctrl+Alt+S` | OCR 截图翻译 |
| 8 | `Ctrl+Alt+Shift+S` | 静默 OCR（结果到剪贴板） |

与 macOS 版 Easydict 的 `⌥+S` / `⌥+⇧+S` 对应，保持快捷键语义一致。

#### 3.2.6 设置项

在 `SettingsService` 中新增：

```csharp
// OCR 设置
public string OcrTranslateHotkey { get; set; } = "Ctrl+Alt+S";
public string SilentOcrHotkey { get; set; } = "Ctrl+Alt+Shift+S";
public string OcrLanguage { get; set; } = "auto";  // "auto" 或语言 tag 如 "zh-Hans-CN"
```

设置 UI 中在「行为」或新增「OCR」分区中展示。

---

### 3.3 文件结构

```
dotnet/src/Easydict.WinUI/
├── Services/
│   ├── OcrService.cs                    # OCR 识别服务 (Windows.Media.Ocr 封装)
│   ├── ScreenCaptureService.cs          # 截图服务（编排截图流程）
│   ├── OcrTranslateService.cs           # OCR 翻译编排（截图→OCR→翻译/剪贴板）
│   └── ScreenCapture/                   # Snipaste 风格截图模块
│       ├── ScreenCaptureWindow.cs       # Win32 原生覆盖窗口（非 WinUI 3 窗口）
│       ├── WindowDetector.cs            # 窗口/元素自动检测（EnumWindows 快照）
│       ├── SelectionRenderer.cs         # 选区绘制（遮罩、高亮、边框、手柄）
│       ├── MagnifierRenderer.cs         # 放大镜面板绘制（放大像素 + 十字线 + 颜色）
│       └── SelectionState.cs            # 选区状态机（检测→框选→调整→确认）
├── Models/
│   ├── OcrResult.cs                     # OCR 识别结果模型
│   └── ScreenCaptureResult.cs           # 截图结果模型
```

### 3.4 关键实现细节

#### 3.4.1 Windows.Media.Ocr 调用示例

```csharp
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

public async Task<OcrResult> RecognizeAsync(
    SoftwareBitmap bitmap,
    OcrLanguage? preferredLanguage = null,
    CancellationToken cancellationToken = default)
{
    // 确保 BGRA8 格式（OcrEngine 要求）
    if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
    {
        bitmap = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    // 创建 OCR 引擎
    OcrEngine engine;
    if (preferredLanguage is not null)
    {
        var lang = new Windows.Globalization.Language(preferredLanguage.Tag);
        engine = OcrEngine.TryCreateFromLanguage(lang)
            ?? throw new InvalidOperationException($"OCR language not available: {preferredLanguage.Tag}");
    }
    else
    {
        engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("No OCR languages available on this system");
    }

    // 执行识别
    var winOcrResult = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);

    // 转换结果
    var lines = winOcrResult.Lines.Select(line => new OcrLine
    {
        Text = line.Text,
        BoundingRect = GetLineBoundingRect(line)
    }).ToList();

    return new OcrResult
    {
        Text = string.Join(Environment.NewLine, lines.Select(l => l.Text)),
        Lines = lines,
        TextAngle = winOcrResult.TextAngle,
        DetectedLanguage = DetectLanguageFromResult(winOcrResult)
    };
}
```

#### 3.4.2 屏幕截图方案

**推荐方案**：Win32 `BitBlt` + GDI

```csharp
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

/// <summary>
/// 使用 Win32 GDI 捕获指定屏幕区域。
/// 兼容性最好，支持所有 Windows 10+ 版本。
/// </summary>
public static Bitmap CaptureScreen(Rectangle region)
{
    var hdc = GetDC(IntPtr.Zero);
    var memDc = CreateCompatibleDC(hdc);
    var hBitmap = CreateCompatibleBitmap(hdc, region.Width, region.Height);
    var oldBitmap = SelectObject(memDc, hBitmap);

    BitBlt(memDc, 0, 0, region.Width, region.Height,
           hdc, region.X, region.Y, SRCCOPY);

    SelectObject(memDc, oldBitmap);
    var bitmap = Image.FromHbitmap(hBitmap);
    DeleteObject(hBitmap);
    DeleteDC(memDc);
    ReleaseDC(IntPtr.Zero, hdc);

    return bitmap;
}
```

**Bitmap → SoftwareBitmap 转换**：
```csharp
using Windows.Graphics.Imaging;

public static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(System.Drawing.Bitmap gdiBitmap)
{
    using var stream = new InMemoryRandomAccessStream();
    gdiBitmap.Save(stream.AsStream(), System.Drawing.Imaging.ImageFormat.Png);
    stream.Seek(0);

    var decoder = await BitmapDecoder.CreateAsync(stream);
    return await decoder.GetSoftwareBitmapAsync(
        BitmapPixelFormat.Bgra8,
        BitmapAlphaMode.Premultiplied);
}
```

#### 3.4.3 多显示器支持

```csharp
// 获取虚拟桌面总尺寸（包含所有显示器）
var virtualLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
var virtualTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
var virtualWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
var virtualHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

// 创建覆盖窗口横跨整个虚拟桌面
SetWindowPos(hwnd, HWND_TOPMOST,
    virtualLeft, virtualTop, virtualWidth, virtualHeight,
    SWP_SHOWWINDOW);
```

---

## 四、与现有架构的集成

### 4.1 SidecarClient 的角色

当前 `Easydict.SidecarClient` 主要为未来的外部进程通信准备。对于 OCR 的首个版本，**建议直接在 WinUI 进程内调用 `Windows.Media.Ocr`**，原因：

1. `Windows.Media.Ocr` 是 WinRT API，在 WinUI 3 进程中可直接调用，无需 IPC
2. 避免引入外部进程增加部署复杂度
3. OCR 操作耗时短（通常 <500ms），不会阻塞 UI（在后台线程执行）

**何时引入 SidecarClient**：
- 需要 Tesseract / PaddleOCR 等外部引擎时（通过 Python/C++ sidecar 提供）
- 需要复杂图像预处理管线时（如 GPU 加速去噪）
- 这是 Tier 2/3 的增强方案，不影响首个版本

### 4.2 集成到 App.xaml.cs

```csharp
// In InitializeServices():
_ocrTranslateService = new OcrTranslateService();

// Add hotkey events
_hotkeyService.OnOcrTranslate += async () => await _ocrTranslateService.OcrTranslateAsync();
_hotkeyService.OnSilentOcr += async () => await _ocrTranslateService.SilentOcrAsync();
```

### 4.3 集成到现有翻译流程

OCR 识别的文本通过 `MiniWindowService.ShowWithText(ocrText)` 送入翻译管线，复用已有的：
- 语言自动检测
- 多服务并行翻译
- 流式翻译显示
- 翻译结果展示 UI

无需修改翻译服务层代码。

---

## 五、分阶段实施计划

### Phase 1：核心 OCR + Snipaste 风格截图（MVP）

**目标**：Snipaste 风格截图 → OCR → 翻译的完整流程跑通

1. **ScreenCaptureWindow**（Snipaste 风格核心交互）：
   - 全屏 GDI `BitBlt` 截图 + Win32 覆盖窗口
   - 自动窗口检测（`EnumWindows` 快照 + `ChildWindowFromPointEx`）
   - 鼠标悬浮高亮 + 单击确认 / 拖拽自由框选
   - 选区尺寸提示
   - 放大镜 + 十字线 + 坐标显示
   - 8 个控制手柄拖拽调整选区
   - 键盘方向键微调（`↑↓←→` / `Ctrl+方向` / `Shift+方向`）
   - `Esc` / 右键取消，`Enter` / 双击确认
2. **OcrService**：封装 `Windows.Media.Ocr`，支持自动语言检测
3. **OcrTranslateService**：编排截图→OCR→MiniWindow 翻译
4. **HotkeyService 扩展**：注册 `Ctrl+Alt+S` 触发 OCR 翻译
5. **SettingsService 扩展**：OCR 快捷键设置

### Phase 2：静默 OCR + 设置 UI

1. **静默 OCR 模式**：`Ctrl+Alt+Shift+S` → OCR → 剪贴板
2. **设置页面**：OCR 语言选择、快捷键配置
3. **语言手动选择**：在 MiniWindow 中显示"检测到 XX 语言"按钮，可切换
4. **滚轮切换窗口层级**：在自动检测模式下滚轮切换父/子窗口

### Phase 3：体验优化

1. **截图交互增强**：
   - `Tab` 键切换窗口检测/元素检测模式
   - `Ctrl+A` 全屏选区
   - 选区历史记忆（记住上次截图区域）
2. **OCR 结果展示**：
   - 在截图上叠加识别结果框（可选）
   - 识别文本可编辑修正
3. **多引擎支持**：
   - 通过 SidecarClient 接入 Tesseract
   - 引擎选择设置
4. **性能优化**：
   - 预初始化 OcrEngine
   - 截图覆盖窗口复用（隐藏而非销毁）

---

## 六、风险和注意事项

| 风险 | 应对 |
|------|------|
| `Windows.Media.Ocr` 不支持某些语言 | 检查 `OcrEngine.AvailableRecognizerLanguages`，在设置中提示安装语言包 |
| 截图窗口在某些 DPI 配置下错位 | 使用物理像素坐标，Per-Monitor V2 DPI 感知；多显示器独立处理 DPI |
| 全屏截图在 DWM 合成关闭时失败 | 降级到 `PrintWindow` 方案 |
| 截图覆盖窗口与游戏/全屏应用冲突 | 在全屏应用检测到时提示用户 |
| OCR 对截图中的小字/低对比度文字识别差 | Phase 3 添加图像预处理（锐化、二值化） |
| WinUI 3 窗口创建有延迟 | 截图覆盖窗口使用 Win32 原生窗口（非 WinUI 3），GDI/GDI+ 绘制 |
| 窗口自动检测对 UWP/WinUI 3 应用不准 | `ChildWindowFromPointEx` 对现代应用可能返回顶层窗口；可用 UI Automation 辅助获取子元素（Phase 3） |
| 放大镜在高 DPI 下模糊 | 从物理像素级别的原始截图取数据，不经 DPI 缩放 |
| 多显示器不同 DPI 时截图拼接错位 | 每个显示器独立 `BitBlt`，按物理像素偏移拼接到虚拟桌面坐标系 |

---

## 七、参考资料

- **[Snipaste — 截图交互设计参考](https://zh.snipaste.com/)**：截图取图交互完全对标 Snipaste，包括自动窗口检测、放大镜、选区调整手柄、键盘微调等
- [Snipaste 使用技巧大全 — 少数派](https://sspai.com/post/85542)
- [Windows 上最好的免费截图标注工具：Snipaste — 少数派](https://sspai.com/post/34962)
- [macOS Easydict OCR 实现](https://github.com/tisfeng/Easydict)
- [Windows.Media.Ocr API 文档](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine)
- [Windows App SDK TextRecognizer](https://learn.microsoft.com/en-us/windows/ai/apis/text-recognition)
- [Text Grab (开源 Windows OCR 工具)](https://github.com/TheJoeFin/Text-Grab)
- [Pot Desktop OCR 架构](https://github.com/pot-app/pot-desktop)
- [STranslate (Windows 翻译+OCR)](https://github.com/ZGGSONG/STranslate)
- [QQ 截图窗口自动识别原理](https://www.cnblogs.com/findumars/p/5786055.html)
- [V2EX 讨论: Windows 类 Bob 翻译软件](https://www.v2ex.com/t/910296)
