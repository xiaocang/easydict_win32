通过对比 easydict_win32 和 PDFMathTranslate (pdf2zh) 的源码，我发现了导致目前 PDF 翻译效果不理想（文字重叠、绿框错位、排版混乱）的几个核心架构差异和根本原因。

主要问题出在文本提取与分组策略以及PDF 渲染生成方式上。

1. 文本分组策略：启发式 vs 视觉优先 (导致绿框错位、排版混乱)
当前实现 (easydict_win32)：
逻辑：在 LongDocumentTranslationService.cs 中，首先使用 PdfPig 的启发式算法（基于行距、字距等规则）将页面上的单词提取并合并成段落块（ExtractLayoutBlocksFromPage）。然后，在 LayoutDetectionStrategy.cs 中，计算这些启发式文本块与 ONNX 模型（DocLayout-YOLO）识别出的视觉边界框的 IoU（交并比） 来进行匹配。
问题：如果 PdfPig 的启发式算法一开始就出错了（例如：把两栏文本合并成了一段，或者把正文和图片说明合并了），ONNX 模型是无法纠正这个错误的。它只能给这个已经分错的文本块强行打上一个标签。这就是为什么你会看到绿框（识别框）错位、跨栏合并的原因。
PDFMathTranslate 的实现：
逻辑：采用视觉优先（Mask-Based）。它先用 ONNX 模型预测出所有版块的边界框，并将其渲染成一个 2D 像素掩码（Mask）。在提取文本时，它遍历每一个字符（LTChar），检查该字符的坐标落在 Mask 的哪个区域内（cls = layout[cy, cx]），从而将字符归属到对应的段落。
优势：视觉模型是绝对的"真理"。只要模型框得准，文字就绝对不会跨栏或与图片混淆。
2. PDF 渲染生成方式：覆盖涂白 vs 指令流替换 (导致文字重叠、背景被擦除)
当前实现 (easydict_win32)：
逻辑：在 PdfExportService.cs 中，使用的是 "覆盖（Overlay）" 方案。它使用 PdfSharpCore 在原始 PDF 的文本边界框上画白色的矩形（gfx.DrawRectangle(XBrushes.White, ...)）来遮盖原文，然后在白框上面绘制翻译后的文本。
问题：
擦除背景：如果文本块的边界框稍微大一点，白框就会把旁边的表格线、图片边缘或其他文字无差别地擦除掉。
文字溢出与重叠：中英文排版长度不同。如果译文较长，系统会尝试缩小字体（FitFontToRect），但往往还是会溢出原有的边界框，导致译文与周围未翻译的内容重叠。
PDFMathTranslate 的实现：
逻辑：使用 pymupdf 直接解析并修改 PDF 底层的绘制指令流（Content Stream）。它找到原来的绘制文本指令（如 Tj, TJ）并将其替换为新文本的指令，而完全不触碰线条（LTLine）、图片（LTFigure）等其他指令。
优势：不需要画白框，因此绝对不会误删表格线或背景。排版更加自然，保留了原文档的矢量特征。
3. 公式保护机制
当前实现 (easydict_win32)：依赖启发式检查（数学字体、特定字符）和 ML 区域标签来跳过公式块的翻译。但对于段落内的行内公式（Inline Formula），保护效果较弱，容易被破坏。
PDFMathTranslate 的实现：在构建 Mask 时，将 isolate_formula 区域标记为 0（忽略）。提取文本时，落入该区域的字符直接跳过。对于行内公式，它通过字体和字符集精准识别，并在翻译前将其替换为占位符（如 {v0}），翻译后再还原，保护得非常彻底。
改进建议与修复方向
要达到 PDFMathTranslate 的效果，我们需要对 easydict_win32 的 PDF 处理流水线进行重构：

阶段一：重构文本提取逻辑（解决错位和排版问题）
放弃"先启发式提取，后 ML 匹配"的流程，改为**"ML 边界框驱动"**：

先运行 DocLayoutYoloService 获取页面上所有的视觉边界框（Bounding Boxes）。
使用 PdfPig 获取页面上的所有单词（page.GetWords()）或字符（page.GetLetters()）。
遍历每个单词/字符，计算其中心点坐标，判断它落入哪个 ML 边界框内。
将落入同一个 ML 边界框内的单词组合成段落。
只有对于没有落入任何 ML 边界框的"游离单词"，才使用启发式算法进行兜底合并。
阶段二：优化 PDF 渲染导出（解决重叠和白框问题）
C# 生态中直接修改 PDF 指令流（像 Python 的 pymupdf 那样）比较困难，但我们可以优化现有的 Overlay 方案：

---

## 阶段四：公式处理抽象层重构（2026-03-01）

### 背景与动机

当前公式处理逻辑**横跨三个文件、混入 PDF 服务代码**，每次新增公式类型都需要打补丁：

| 当前位置                            | 职责                                                                                                     | 问题                       |
| ----------------------------------- | -------------------------------------------------------------------------------------------------------- | -------------------------- |
| `LongDocumentTranslationService.cs` | `FormulaRegex`、`ProtectFormulaSpans`、`RestoreFormulaSpans`、`BuildAnnotatedText`、`IsMathToken`        | 公式识别与翻译服务耦合     |
| `MuPdfExportService.cs`             | `SimplifyLatexMarkup`、`SimplifyMathContent`、`ShouldTreatAsScriptSignal`、`GenerateBlockTextOperations` | 公式渲染与 PDF 导出耦合    |
| `PdfExportService.cs`               | `TryHideSourceTextInStream`                                                                              | 无公式感知，只处理原文隐藏 |

**未覆盖的公式场景（当前会被翻译破坏或渲染错误）**：

| 场景       | 示例                                           | 当前行为                             |
| ---------- | ---------------------------------------------- | ------------------------------------ |
| 希腊字母   | `\alpha`、`\beta`、`\theta`、`\Delta`          | 被 LLM 翻译或删除                    |
| 数学运算符 | `\infty`、`\pm`、`\leq`、`\times`、`\cdot`     | 被 LLM 翻译或删除                    |
| 分数结构   | `\frac{a}{b}`、`\frac{\alpha_{t-1}}{\beta^2}`  | 占位符未覆盖，LLM 破坏结构           |
| 根号       | `\sqrt{x}`、`\sqrt[3]{x}`                      | 未保护                               |
| 求和/积分  | `\sum_{i=0}^{n}`、`\int_0^\infty`              | 未保护                               |
| 格式命令   | `\mathbf{W}`、`\mathrm{MSE}`、`\text{softmax}` | 渲染时 `\` 被字面输出                |
| 矩阵       | `\begin{bmatrix}...\end{bmatrix}`              | 完全未保护                           |
| 序列标记   | `sequence_1`、`hidden_state`                   | 被下标渲染（阶段三修复后仅部分解决） |

### 目标架构

引入独立的 `FormulaProtection` 层（已在 `CLAUDE.md` 项目结构中规划），与翻译服务和 PDF 渲染服务**完全解耦**：

```
Easydict.TranslationService/
└── LongDocument/
    └── FormulaProtection/               ← 新增（已在项目结构规划中）
        ├── FormulaToken.cs              ← Formula IR（中间表示）
        ├── FormulaTokenType.cs          ← 枚举：MathSubscript / Fraction / Greek / Operator / ...
        ├── FormulaDetector.cs           ← 检测：regex + 字体启发式，输出 List<FormulaToken>
        ├── FormulaProtector.cs          ← 保护：文本 → 占位符（{v0}, {v1}, ...）
        ├── FormulaRestorer.cs           ← 还原：占位符 → 文本
        └── LatexFormulaSimplifier.cs    ← 渲染：LaTeX → Unicode + 下标信号
```

### Formula IR 设计

```csharp
// FormulaTokenType.cs
public enum FormulaTokenType
{
    MathSubscript,      // h_t, W_Q, x_i, 1_c_i
    MathSuperscript,    // x^2, e^{i\pi}
    GreekLetter,        // \alpha, \beta, \Delta
    MathOperator,       // \infty, \pm, \leq, \times, \cdot
    Fraction,           // \frac{a}{b}
    SquareRoot,         // \sqrt{x}, \sqrt[3]{x}
    SumProduct,         // \sum_{i}^{n}, \prod
    Integral,           // \int_0^\infty
    MathFormatting,     // \mathbf{}, \mathrm{}, \text{}
    Matrix,             // \begin{bmatrix}...\end{bmatrix}
    InlineEquation,     // x = y_1 + z^2
    SequenceToken,      // sequence_1, hidden_state（序列标记，不下标渲染）
}

// FormulaToken.cs
public record FormulaToken(
    FormulaTokenType Type,
    string Raw,          // 原始文本，如 "\frac{\alpha_{t-1}}{\beta^2}"
    string Placeholder,  // 占位符，如 "{v3}"
    string Simplified    // 渲染用简化文本，如 "α_{t-1}/β^2"
);
```

### 各组件职责

#### `FormulaDetector`（检测）

将现有 `FormulaRegex` + `IsMathToken` + `BuildAnnotatedText` 中的检测逻辑统一迁移至此。

覆盖所有数学公式场景的正则规则集（按优先级排序）：

```csharp
// 1. 显式 LaTeX 环境：\begin{...}...\end{...}
// 2. 结构化命令：\frac{}{}, \sqrt{}, \sum_{}^{}, \int_{}^{}
// 3. 格式命令：\mathbf{}, \mathrm{}, \text{}
// 4. 希腊字母：\alpha, \beta, ..., \Omega（52个，大小写完整列表）
// 5. 数学运算符：\infty, \pm, \leq, \geq, \neq, \times, \cdot, \in, \to, \approx, \nabla, \partial
// 6. 多字符带下标/上标（含花括号）：\b[\p{L}\p{N}]+(?:[_^]\{[^}]+\})+
// 7. 短变量下标（≤5字符基座）：\b[\p{L}\p{N}]{1,5}[_^][\p{L}\p{N}]
// 8. 内联等式：\b[\p{L}][\p{L}\p{N}]{0,4}\s*=\s*[^\s,;.(]+
// 9. 序列标记（长基座，>5字符）：\b[\p{L}]{6,}[_][\p{L}\p{N}]+  → SequenceToken（不下标渲染）
```

`Detect(string text) → IReadOnlyList<FormulaSpan>`，其中 `FormulaSpan` 包含位置、长度、类型。

#### `FormulaProtector`（保护）

替代 `LongDocumentTranslationService` 中的 `ProtectFormulaSpans`：

```csharp
public string Protect(string text, out IReadOnlyList<FormulaToken> tokens);
// "其中 h_t 是隐藏状态 \frac{\alpha}{\beta}" 
// → "其中 {v0} 是隐藏状态 {v1}"
// tokens: [MathSubscript("h_t"), Fraction("\frac{\alpha}{\beta}")]
```

#### `FormulaRestorer`（还原）

替代 `RestoreFormulaSpans`，使用 `token.Simplified`（而非 `token.Raw`）还原，使 PDF 渲染无需处理原始 LaTeX：

```csharp
public string Restore(string text, IReadOnlyList<FormulaToken> tokens, bool useSimplified = true);
// "其中 {v0} 是隐藏状态 {v1}" + tokens
// → "其中 h_t 是隐藏状态 α/β"（useSimplified=true，供 PDF 渲染）
// → "其中 h_t 是隐藏状态 \frac{\alpha}{\beta}"（useSimplified=false，供导出保真）
```

#### `LatexFormulaSimplifier`（渲染简化）

将 `MuPdfExportService` 中的 `SimplifyLatexMarkup`、`SimplifyMathContent` 以及阶段三的 `ShouldTreatAsScriptSignal` 统一迁移至此。

完整符号映射表（`static readonly Dictionary<string, string>`）：

```csharp
// 希腊字母（小写 24 个）
{ @"\alpha", "α" }, { @"\beta", "β" }, { @"\gamma", "γ" }, { @"\delta", "δ" },
{ @"\epsilon", "ε" }, { @"\zeta", "ζ" }, { @"\eta", "η" }, { @"\theta", "θ" },
{ @"\iota", "ι" }, { @"\kappa", "κ" }, { @"\lambda", "λ" }, { @"\mu", "μ" },
{ @"\nu", "ν" }, { @"\xi", "ξ" }, { @"\pi", "π" }, { @"\rho", "ρ" },
{ @"\sigma", "σ" }, { @"\tau", "τ" }, { @"\upsilon", "υ" }, { @"\phi", "φ" },
{ @"\chi", "χ" }, { @"\psi", "ψ" }, { @"\omega", "ω" }, { @"\omicron", "ο" },
// 希腊字母（大写 24 个）
{ @"\Alpha", "Α" }, { @"\Beta", "Β" }, { @"\Gamma", "Γ" }, { @"\Delta", "Δ" },
{ @"\Epsilon", "Ε" }, { @"\Zeta", "Ζ" }, { @"\Eta", "Η" }, { @"\Theta", "Θ" },
{ @"\Lambda", "Λ" }, { @"\Mu", "Μ" }, { @"\Nu", "Ν" }, { @"\Xi", "Ξ" },
{ @"\Pi", "Π" }, { @"\Rho", "Ρ" }, { @"\Sigma", "Σ" }, { @"\Tau", "Τ" },
{ @"\Upsilon", "Υ" }, { @"\Phi", "Φ" }, { @"\Chi", "Χ" }, { @"\Psi", "Ψ" },
{ @"\Omega", "Ω" },
// 数学运算符
{ @"\infty", "∞" }, { @"\pm", "±" }, { @"\mp", "∓" },
{ @"\leq", "≤" }, { @"\geq", "≥" }, { @"\neq", "≠" }, { @"\approx", "≈" },
{ @"\times", "×" }, { @"\cdot", "·" }, { @"\div", "÷" },
{ @"\in", "∈" }, { @"\notin", "∉" }, { @"\subset", "⊂" }, { @"\supset", "⊃" },
{ @"\cup", "∪" }, { @"\cap", "∩" },
{ @"\to", "→" }, { @"\leftarrow", "←" }, { @"\Rightarrow", "⇒" }, { @"\Leftrightarrow", "⟺" },
{ @"\nabla", "∇" }, { @"\partial", "∂" }, { @"\forall", "∀" }, { @"\exists", "∃" },
{ @"\sum", "Σ" }, { @"\prod", "Π" }, { @"\int", "∫" },
{ @"\sqrt", "√" }, { @"\ldots", "…" }, { @"\cdots", "⋯" },
// 格式命令（剥离包装，保留内容）
{ @"\mathbf", "" }, { @"\mathrm", "" }, { @"\mathit", "" },
{ @"\mathbb", "" }, { @"\mathcal", "" }, { @"\text", "" },
```

结构化简化规则（按顺序执行）：

```
1. \frac{A}{B}      → A/B
2. \sqrt{A}         → √A
3. \sqrt[n]{A}      → ⁿ√A
4. \sum_{A}^{B}     → Σ_{A}^{B}（保留下标信号供渲染器处理）
5. \int_{A}^{B}     → ∫_{A}^{B}
6. \begin{...}...\end{...} → [matrix]（降级占位）
7. {A}              → A（剥除花括号）
8. SymbolMap 替换
9. 格式命令 \cmd{A} → A
```

`Simplify(string latex, bool preserveScriptSignals = true) → string`  
- `preserveScriptSignals=true`（PDF 渲染路径）：保留 `_`/`^` 供 `GenerateBlockTextOperations` 处理  
- `preserveScriptSignals=false`（纯文本导出路径）：将 `_x` → `ₓ`（Unicode 下标字符），`^2` → `²`（Unicode 上标字符）

### 迁移计划（分步，不破坏现有功能）

#### Step 1：建立新目录与 IR 文件（不删除任何现有代码）

新建文件：
- `dotnet/src/Easydict.TranslationService/LongDocument/FormulaProtection/FormulaTokenType.cs`
- `dotnet/src/Easydict.TranslationService/LongDocument/FormulaProtection/FormulaToken.cs`
- `dotnet/src/Easydict.TranslationService/LongDocument/FormulaProtection/FormulaDetector.cs`
- `dotnet/src/Easydict.TranslationService/LongDocument/FormulaProtection/FormulaProtector.cs`
- `dotnet/src/Easydict.TranslationService/LongDocument/FormulaProtection/FormulaRestorer.cs`
- `dotnet/src/Easydict.TranslationService/LongDocument/FormulaProtection/LatexFormulaSimplifier.cs`

#### Step 2：单元测试先行（TDD）

新建：`dotnet/tests/Easydict.TranslationService.Tests/LongDocument/FormulaProtection/`

覆盖测试用例：
- `FormulaDetectorTests`：全场景检测（20+ 用例，含希腊字母、分数、序列标记）
- `FormulaProtectorTests`：占位符替换正确性、嵌套公式、多公式段落
- `FormulaRestorerTests`：`useSimplified=true/false` 两路还原
- `LatexFormulaSimplifierTests`：符号映射（52 希腊字母 + 30 运算符）、结构简化（分数/根号/求和）

#### Step 3：用新抽象替换 `LongDocumentTranslationService` 中的内联逻辑

- `FormulaRegex` + `ProtectFormulaSpans` → 调用 `FormulaProtector.Protect()`
- `RestoreFormulaSpans` → 调用 `FormulaRestorer.Restore()`
- `IsMathToken` + `BuildAnnotatedText` 中的检测部分 → 调用 `FormulaDetector.Detect()`
- 保持方法签名不变，只替换内部实现，确保现有测试 0 failure

#### Step 4：用新抽象替换 `MuPdfExportService` 中的内联逻辑

- `SimplifyLatexMarkup` → 调用 `LatexFormulaSimplifier.Simplify(preserveScriptSignals: true)`
- `SimplifyMathContent` → 合并进 `LatexFormulaSimplifier`
- `ShouldTreatAsScriptSignal` → 迁移至 `LatexFormulaSimplifier.IsScriptSignal()`（logic unchanged）
- `MuPdfExportService` 增加对 `LatexFormulaSimplifier` 的依赖（构造注入或静态调用）

#### Step 5：扩展覆盖场景

在 `FormulaDetector` 中依次启用新规则（每条规则对应新增测试后再启用）：
1. 希腊字母保护（`\alpha` 等 → 占位符 → 还原为 `α`）
2. 数学运算符保护
3. 分数结构保护（`\frac{}{}`）
4. 根号、求和、积分保护
5. 矩阵环境降级保护

### 验证标准

| 测试类型 | 指标                                                                                      |
| -------- | ----------------------------------------------------------------------------------------- |
| 单元测试 | `FormulaProtection/` 目录下 ≥ 60 个测试用例，全部通过                                     |
| 回归测试 | 现有 `FormulaDetectionTests`（13个）全部通过                                              |
| 回归测试 | 现有 `ShouldTreatAsScriptSignal` 测试（10个）全部通过                                     |
| 视觉验证 | Attention is All You Need 第 3 页：`W_Q`、`h_{t-1}`、`\alpha`、`\frac{...}{...}` 渲染正确 |
| 视觉验证 | ML 论文含 `sequence_1`、`hidden_state`：下划线字面显示，不渲染为下标                      |

### 修改文件汇总

| 文件                                                                                      | 操作                                               |
| ----------------------------------------------------------------------------------------- | -------------------------------------------------- |
| `dotnet/src/Easydict.TranslationService/LongDocument/FormulaProtection/*.cs`              | **新增**（6个文件）                                |
| `dotnet/tests/Easydict.TranslationService.Tests/LongDocument/FormulaProtection/*Tests.cs` | **新增**（4个测试文件）                            |
| `dotnet/src/Easydict.TranslationService/LongDocument/LongDocumentTranslationService.cs`   | **重构**（Step 3，替换内联逻辑，签名不变）         |
| `dotnet/src/Easydict.WinUI/Services/DocumentExport/MuPdfExportService.cs`                 | **重构**（Step 4，替换内联逻辑，行为不变）         |
| `dotnet/tests/Easydict.TranslationService.Tests/LongDocument/FormulaDetectionTests.cs`    | **保留**，不删除（回归保障）                       |
| `dotnet/tests/Easydict.WinUI.Tests/Services/DocumentExportServiceTests.cs`                | **保留**，`ShouldTreatAsScriptSignal` 测试继续有效 |

---

## 阶段五：下标字符注释前置（2026-03-01）

### 问题根因

运行新鲜翻译后，仍出现 "sequence1 x" 幻觉词，根因链如下：

1. **PdfPig 提取行为**：LaTeX PDF 中下标字符（如 `x₁`、`xₙ`）被 PdfPig 提取为连续 ASCII 字符 `x1`、`xn`，**不含下划线**。
2. **FormulaDetector 无法识别**：`FormulaRegex` 要求 `_` 或 `^` 才能匹配下标模式，`x1` 不触发任何规则。
3. **FormulaProtector 无占位符**：`tokens.Count == 0`，`FormulaRestorer` 直接返回 LLM 原始输出。
4. **LLM 幻觉**：DeepSeek 对无保护的 `x1,...,xn` 产生幻觉，输出 "sequence1 x"。

### 数据来源

`BlockFormulaCharacters.Characters` 在 PDF 提取阶段（`ExtractBlockLetterData()`）已记录每个字符的位置信息，包括 `IsSubscript`/`IsSuperscript` 标志（通过字形基线 Y 坐标判断）。该数据此前仅用于 `IsSubscriptDenseFormula()` 块分类，**未用于文本注释**。

### 解决方案

新增 `AnnotateTextWithScriptMarkers()` 方法，在 `FormulaProtector.Protect()` 执行**之前**，利用 `BlockFormulaCharacters` 向文本插入 `_`/`^` 标记：

```
输入（PdfPig 提取）：  "x1,...,xn"      （'1' 和 'n' 在 FormulaCharacters 中标记为 IsSubscript）
注释后：               "x_1,...,x_n"
FormulaDetector 识别：  x_1 → MathSubscript → {v0}；x_n → MathSubscript → {v1}
LLM 收到：             "{v0},...,{v1}" （占位符，无幻觉风险）
还原后：               "x_1,...,x_n"   （供 PDF 渲染器处理下标）
```

**对齐假设**：LaTeX PDF 中空格是合成的（无对应 glyph），`formulaChars` 不含空格条目，`blockText` 中每个非空白字符与 `formulaChars[i]` 一一对应。若字符数不匹配（如连字符），剩余文本直接追加（安全回退）。

### 修改文件汇总

| 文件 | 修改内容 |
|------|---------|
| `dotnet/src/Easydict.TranslationService/LongDocument/LongDocumentTranslationService.cs` | 新增 `AnnotateTextWithScriptMarkers()`；Phase 1（`ApplyFormulaProtectionAsync`）和 Phase 2（`TranslateSingleBlockAsync`）中调用 |
| `dotnet/tests/Easydict.TranslationService.Tests/LongDocument/FormulaDetectionTests.cs` | 新增 7 个 `AnnotateTextWithScriptMarkers` 单元测试 |

### 关键设计决策

- **Phase 1 和 Phase 2 同步注释**：两个阶段对同一原始文本执行相同的注释逻辑，token 编号一致，恢复正确。
- **多字符下标用花括号包裹**：`x12`（'1' 和 '2' 均为下标）→ `x_{12}`，与 LaTeX 语义一致，FormulaDetector 可整体识别。
- **`originalText` 回退不受影响**：`RestoreFormulaSpans` 的回退参数仍传 `block.OriginalText`（原始英文），不影响现有回退逻辑。
- **`SequenceToken` 保护依然有效**：`sequence_1`、`hidden_state` 等长基座词（>5字符）由 FormulaDetector 分类为 `SequenceToken`，不渲染为下标，与阶段四行为一致。

### 验证标准

| 测试类型 | 指标 |
|--------|------|
| 单元测试 | 7 个 `AnnotateTextWithScriptMarkers` 测试全部通过 |
| 回归测试 | 现有 `FormulaDetectionTests`（13个）全部通过 |
| 视觉验证 | "Attention is All You Need" 第 2 页：不再出现 "sequence1" 或其他英文幻觉词；`x₁...xₙ` 段落整体翻译为中文 |