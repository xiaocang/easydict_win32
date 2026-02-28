通过对比 easydict_win32 和 PDFMathTranslate (pdf2zh) 的源码，我发现了导致目前 PDF 翻译效果不理想（文字重叠、绿框错位、排版混乱）的几个核心架构差异和根本原因。

主要问题出在文本提取与分组策略以及PDF 渲染生成方式上。

1. 文本分组策略：启发式 vs 视觉优先 (导致绿框错位、排版混乱)
当前实现 (easydict_win32)：
逻辑：在 LongDocumentTranslationService.cs 中，首先使用 PdfPig 的启发式算法（基于行距、字距等规则）将页面上的单词提取并合并成段落块（ExtractLayoutBlocksFromPage）。然后，在 LayoutDetectionStrategy.cs 中，计算这些启发式文本块与 ONNX 模型（DocLayout-YOLO）识别出的视觉边界框的 IoU（交并比） 来进行匹配。
问题：如果 PdfPig 的启发式算法一开始就出错了（例如：把两栏文本合并成了一段，或者把正文和图片说明合并了），ONNX 模型是无法纠正这个错误的。它只能给这个已经分错的文本块强行打上一个标签。这就是为什么你会看到绿框（识别框）错位、跨栏合并的原因。
PDFMathTranslate 的实现：
逻辑：采用视觉优先（Mask-Based）。它先用 ONNX 模型预测出所有版块的边界框，并将其渲染成一个 2D 像素掩码（Mask）。在提取文本时，它遍历每一个字符（LTChar），检查该字符的坐标落在 Mask 的哪个区域内（cls = layout[cy, cx]），从而将字符归属到对应的段落。
优势：视觉模型是绝对的“真理”。只要模型框得准，文字就绝对不会跨栏或与图片混淆。
2. PDF 渲染生成方式：覆盖涂白 vs 指令流替换 (导致文字重叠、背景被擦除)
当前实现 (easydict_win32)：
逻辑：在 PdfExportService.cs 中，使用的是 “覆盖（Overlay）” 方案。它使用 PdfSharpCore 在原始 PDF 的文本边界框上画白色的矩形（gfx.DrawRectangle(XBrushes.White, ...)）来遮盖原文，然后在白框上面绘制翻译后的文本。
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
放弃“先启发式提取，后 ML 匹配”的流程，改为**“ML 边界框驱动”**：

先运行 DocLayoutYoloService 获取页面上所有的视觉边界框（Bounding Boxes）。
使用 PdfPig 获取页面上的所有单词（page.GetWords()）或字符（page.GetLetters()）。
遍历每个单词/字符，计算其中心点坐标，判断它落入哪个 ML 边界框内。
将落入同一个 ML 边界框内的单词组合成段落。
只有对于没有落入任何 ML 边界框的“游离单词”，才使用启发式算法进行兜底合并。
阶段二：优化 PDF 渲染导出（解决重叠和白框问题）
C# 生态中直接修改 PDF 指令流（像 Python 的 pymupdf 那样）比较困难，但我们可以优化现有的 Overlay 方案：

精准涂白：不要在整个段落的 Bounding Box 上画一个大白框。而是遍历段落中的每一行（甚至每个单词），只在单词的精确边界框上画小白框。这样可以最大限度地减少对背景、表格线和图片的误伤。
更好的文本折行与缩放：在 PdfExportService.cs 中，改进 FitFontToRect 和 WrapTextByWidth 的逻辑，确保译文严格限制在目标区域内，宁可多折行或缩小字体，也绝不溢出导致重叠。
如果你希望，我们可以先从**阶段一（重构文本提取逻辑）**开始动手，修改 LayoutDetectionStrategy.cs 和 LongDocumentTranslationService.cs，让文本块的划分完全由 DocLayout-YOLO 的视觉模型来主导。需要模型主导 DocLayout-YOLO 模型主导。

---

## 修复实施方案（2026-02-28）

### 已验证：坐标流水线正确，无需修改

```
PDF page (612×792 pt)
  → RenderPdfPageAsync: scale=min(1024/612, 1024/792)=1.293, 渲染 792×1024 px
  → PreprocessImage: scale=1.0，792×1024→1024×1024，padX=116, padY=0
  → ParseDetections: 去除 padding 偏移，输出 792×1024 像素空间坐标
  → DetectionToPdfCoords: 除以 1.293，Y 轴翻转 → 正确 PDF 点坐标 ✓
```

"绿框错位"并非坐标 bug，而是重叠 ML 区域或孤立词启发式分组的视觉伪影。

### 阶段一：文本分组重构（已完成）

`LayoutDetectionStrategy.cs` 中的 `ExtractBlocksByMlRegions()` 已改为以 ML 区域为主驱动：通过中心点包含判断将单词归属到视觉边界框，与 pdf2zh 的 Mask-Based 方法一致。无游离词时不再回退到启发式合并。

### 阶段二：PDF 渲染修复方案

#### 方案：`3 Tr` 不可见模式 + 重绘

利用 PDF 文本渲染模式运算符（`Tr`）：
- `3 Tr` = 不可见（既不填充也不描边）
- `0 Tr` = 填充（正常显示）

在内容流中找到原文本运算符，在其前后注入 `3 Tr … 0 Tr`，使原文变为不可见，而不擦除任何其他内容。再由 `XGraphics.Append` 在同一边界框位置绘制译文，无需画白色矩形。

优雅降级：若原文在内容流中找不到（如 CID 编码字体），保留现有白框覆盖路径。

#### 实施细节

**新增方法 `TryHideSourceTextInStream`**（`PdfExportService.cs`）

```
1. 通过现有反射链获取内容流字节（CreateSingleContent → Stream → Value）
2. 以 Latin-1（ISO 8859-1）解码字节，正确处理 > 0x7F 的编码字节
3. 调用 FindTextOperatorRange(content, sourceText) → 返回匹配运算符的 (start, end)，或 (-1,-1)
4. 若找到：在 start 前插入 "3 Tr "，在 end 后插入 " 0 Tr"
5. 通过反射（SetValue）写回修改后的字节
```

**`FindTextOperatorRange` 查找逻辑**：
- 字面量形式：搜索 `(escapedSource) Tj` → 返回整个 token+运算符的跨度
- TJ 数组形式：搜索 `[(…)] TJ`，其解码字面量拼合等于 sourceText → 返回整个括号+TJ 的跨度
- 非 ASCII Latin 字符：编码为 PDFDocEncoding（字节值 0x80–0xFF）后再搜索
- 均不匹配则返回 `(-1, -1)`

**修改 `OverlayBlockInfo` 记录**（新增字段）：

```csharp
public bool SourceHidden { get; init; }  // true = 3 Tr 注入成功
```

**修改 `ExportPdfWithCoordinateBackfill` 两轮渲染循环**：

- **第 1 轮（白色背景）**：若 `block.SourceHidden == true`，跳过 `DrawRectangle(XBrushes.White, …)`
- **第 2 轮（文字绘制）**：若 `block.SourceHidden == true`，`maxVisibleLines` 额外宽松 2 行，允许微量向下溢出至自然空白区，模拟 pdf2zh 的自然排版行为

**降级优化：逐字白框遮盖**

对 `TryHideSourceTextInStream` 返回 false 的块（CID 编码字体、复杂内容流），改进白框路径：

新增辅助方法 `BuildPerLetterEraseRects`：
- 读取 `metadata.FormulaCharacters?.Characters`（存储块内所有字形矩形）
- 将每个字母的 PDF 字形矩形转换为页面相对 XRect（同 `ToPageRect` 的翻转逻辑）
- 返回矩形列表，若无字母数据则返回 `null`

第 1 轮中：若 `block.SourceHidden == false` 且有逐字矩形可用，则为每个字形绘制独立的窄白框，而非每行一个大白框，避免擦除字形间隙处的表格线或分栏线。

#### 修改文件汇总

| 文件 | 修改内容 |
|------|--------|
| `dotnet/src/Easydict.WinUI/Services/DocumentExport/PdfExportService.cs` | 新增 `TryHideSourceTextInStream`、`FindTextOperatorRange`；修改 `OverlayBlockInfo`、`ExportPdfWithCoordinateBackfill` 两轮循环；新增 `BuildPerLetterEraseRects` |

其余文件（`LayoutDetectionStrategy.cs`、`LongDocumentTranslationService.cs`、公式保护流水线、CJK 字体嵌入、双语 PDF 生成）不变。

#### 验证步骤

1. **构建**：`dotnet build dotnet/src/Easydict.WinUI/Easydict.WinUI.csproj -c Debug`
2. **单元测试**：`dotnet test dotnet/tests/Easydict.WinUI.Tests` — 现有 `TryPatchPdfLiteralToken`、`TryPatchPdfArrayTextToken`、`ExtractPdfLiteralStrings` 测试必须继续通过
3. **视觉验证**：翻译含表格的英文学术 PDF，验证表格线完整、分栏线可见、译文位置正确
4. **降级验证**：翻译含中文原文的 PDF，应回退到白框覆盖路径，无报错
5. **Sidecar JSON**：`.backfill_issues.json` 应对使用 Tr=3 路径的块显示新的 `"invisible-hidden"` 类型