# Easydict vs PDFMathTranslate (pdf2zh) 架构对比

## 一、整体架构对比

```
                    pdf2zh                                    Easydict
┌─────────────────────────────────────┐  ┌─────────────────────────────────────┐
│         pdfminer (解析)              │  │          PdfPig (解析)               │
│  逐字符流式处理 LTChar              │  │  page.GetWords() → 词级提取          │
│  保留原始 CID/font/matrix           │  │  丢失原始 CID 编码和字体对象        │
├─────────────────────────────────────┤  ├─────────────────────────────────────┤
│         DocLayout ONNX              │  │     DocLayout ONNX / Vision API     │
│  渲染像素 → YOLO 检测 → 2D mask     │  │  渲染像素 → YOLO 检测 → bbox 列表   │
│  每个字符查 mask[y,x] 获取类别      │  │  每个词匹配 bbox → 分配区域标签     │
├─────────────────────────────────────┤  ├─────────────────────────────────────┤
│      字符级段落构建 + 公式分组       │  │    词→行→段落 层级分组              │
│  sstk/pstk 文字栈 + vstk 公式栈     │  │  lines → paragraphs (间距阈值)      │
│  括号追踪、角标检测、字体检测       │  │  FormulaRegex + 字体/Unicode/角标   │
├─────────────────────────────────────┤  ├─────────────────────────────────────┤
│     ThreadPoolExecutor 并行翻译     │  │   sequential / SemaphoreSlim 并行   │
│     {v*} 占位符 + 翻译缓存          │  │   {v*} 占位符 + SHA256 缓存         │
├─────────────────────────────────────┤  ├─────────────────────────────────────┤
│     PyMuPDF 内容流直接替换          │  │  PdfSharpCore overlay 叠加          │
│  字符级定位 + 原始字体重用          │  │  块级定位 + 系统字体                │
│  嵌入 Noto/Tiro 字体               │  │  运行时查找系统字体                 │
│  双语对照 (mono + dual PDF)         │  │  单 PDF 覆盖 (overlay)              │
└─────────────────────────────────────┘  └─────────────────────────────────────┘
```

---

## 二、各阶段详细对比

### 1. PDF 解析 & 文本提取

| 维度 | pdf2zh | Easydict |
|------|--------|----------|
| **解析库** | pdfminer (Python) | PdfPig (C# .NET) |
| **提取粒度** | 逐字符 `LTChar`（含 CID 编码、font 对象、变换矩阵） | 词级 `page.GetWords()` + 字符级 `page.Letters` |
| **坐标系** | PDF 原生坐标（左下角原点） | PdfPig 坐标（左下角原点，通过 BoundingBox） |
| **字体信息** | 完整保留：`font` 对象、`fontid` 映射、`cid`、`fontname`、`fontsize`、`matrix` | 部分保留：`letter.FontName`、`letter.FontSize`、`letter.GlyphRectangle` |
| **关键差异** | 保留原始 CID 编码，可直接写回 PDF content stream | CID 编码丢失，无法直接操作 content stream |

**pdf2zh 的字符流** (`converter.py:81-115`):
```python
# render_char 重载，保留原始 CID 和 font 对象
item.cid = cid      # hack 插入原字符编码
item.font = font     # hack 插入原字符字体
```

**Easydict 的词级提取** (`LongDocumentTranslationService.cs:890-960`):
```csharp
var words = page.GetWords()
    .Where(word => !string.IsNullOrWhiteSpace(word.Text))
    .OrderByDescending(word => word.BoundingBox.Top)
    .ThenBy(word => word.BoundingBox.Left)
    .ToList();
```

### 2. 布局检测

| 维度 | pdf2zh | Easydict |
|------|--------|----------|
| **模型** | DocLayout YOLO ONNX (单一) | DocLayout ONNX / Vision API / Heuristic (三策略) |
| **输出格式** | 2D numpy array `box[h,w]` — 每个像素一个类别 ID | bbox 列表 — 每个检测框一个类别标签 |
| **字符→区域映射** | `cls = layout[cy, cx]` — O(1) 像素查找 | 两阶段词分配：先可翻译区域，再排除区域 — O(N×M) |
| **排除类别** | `abandon, figure, table, isolate_formula, formula_caption` | `Figure, Table, Formula, Abandon` |

**pdf2zh 的像素级 mask** (`high_level.py:134-157`):
```python
box = np.ones((pix.height, pix.width))  # 全 1 初始化
# 先画可翻译区域 (i+2)
for d in page_layout.boxes:
    if name not in vcls:
        box[y0:y1, x0:x1] = i + 2
# 再画排除区域 (0)
for d in page_layout.boxes:
    if name in vcls:
        box[y0:y1, x0:x1] = 0
layout[pageno] = box
```

**Easydict 的 bbox 匹配** (`LongDocumentTranslationService.cs:835-885`):
```csharp
// Phase 1: assign to translatable regions
// Phase 2: exclude from non-translatable regions
// Words not assigned to any region use heuristic fallback
```

**核心差异**: pdf2zh 的 mask 方法天然支持同一行内不同区域的像素级分割（例如公式和文字交替出现），而 Easydict 的 bbox 匹配在区域重叠时需要额外的优先级逻辑。

### 3. 段落构建 & 公式检测

| 维度 | pdf2zh | Easydict |
|------|--------|----------|
| **构建粒度** | 字符级：逐字符追踪 `xt_cls`（上一字符类别），同类连续字符合并为段落 | 词级→行级→段落级：词按 Y 坐标分行，行按间距分段 |
| **公式检测** | 逐字符 4 级检测 + 括号追踪 | 块级 5 级检测（Round 1-3 改进后） |
| **公式保留** | 原始字符完整保存在 `vstk[]`，翻译后逐字符重新定位 | 正则替换为 `{v*}` 占位符，翻译后文本还原 |
| **段落边界** | 布局类别变化 (`cls != xt_cls`) 触发新段落 | 垂直间距 > `medianWordHeight * 1.3` 触发新段落 |
| **换行检测** | `child.x1 < xt.x0` → 段内换行标记 `brk=True` | 行间 Y 坐标差异自动处理 |

**pdf2zh 的字符级公式检测** (`converter.py:241-246`):
```python
if (
    cls == 0                                                # 1. 排除区域
    or (cls == xt_cls and len(sstk[-1].strip()) > 1
        and child.size < pstk[-1].size * 0.79)             # 2. 角标
    or vflag(child.fontname, child.get_text())              # 3. 公式字体/字符
    or (child.matrix[0] == 0 and child.matrix[3] == 0)     # 4. 垂直字体
):
    cur_v = True  # 当前字符属于公式
```

**Easydict 的块级公式检测** (`LongDocumentTranslationService.cs:257-260`):
```csharp
var translationSkipped =
    block.BlockType == SourceBlockType.Formula || block.IsFormulaLike
    || IsFontBasedFormula(block.DetectedFontNames, ...)     // Level 2
    || IsCharacterBasedFormula(blockText, ...)               // Level 3
    || IsSubscriptDenseFormula(block.FormulaCharacters);     // Level 4
```

**关键差异**: pdf2zh 能在同一行内精确分割文字和公式（如 "where $f(x)$ is defined"），并在翻译后将公式字符按原始字体和位置重新排列。Easydict 只能在块级判断整个段落是否为公式，无法处理行内公式的精确定位。

### 4. 翻译 & 公式保护

| 维度 | pdf2zh | Easydict |
|------|--------|----------|
| **占位符** | `{v0}`, `{v1}`, ... — 由字符级公式分组生成 | `{v0}`, `{v1}`, ... — 由 FormulaRegex 正则匹配生成 |
| **跳过逻辑** | 空串或纯占位符 `re.match(r"^\{v\d+\}$", s)` 不翻译 | `IsFormulaOnlyText()` — 清理占位符后空串则跳过 |
| **翻译 prompt** | "Keep the formula notation {v*} unchanged." | "Keep formula placeholders {v0}, {v1}, ... unchanged." |
| **并发** | `ThreadPoolExecutor(max_workers=thread)` | `SemaphoreSlim(concurrency)` |
| **缓存** | 基于 (translator, lang_in, lang_out, model) 的文件缓存 | 基于 SHA256(sourceText) 的内存缓存 |
| **后处理** | `remove_control_characters()` | `RemoveControlCharacters()` + `TrimLeadingSpacesPerLine()` |

### 5. PDF 输出 & 文字渲染 ⭐ 最大差异

| 维度 | pdf2zh | Easydict |
|------|--------|----------|
| **输出库** | PyMuPDF (fitz) | PdfSharpCore |
| **渲染方式** | **内容流替换** — 清空原页面内容流，写入新的 PDF 操作符 | **Overlay 叠加** — 在原 PDF 上覆盖白色矩形 + 新文字 |
| **原文处理** | 原始内容流中文字操作符被过滤掉（只保留图形/图片操作符） | `TryHideSourceTextInStream` (3 Tr) 隐藏，或白色矩形遮挡 |
| **字符定位** | 逐字符精确定位 — 每个字符有独立的 `(font, size, x, y)` | 块级定位 — 整块文本从 BoundingBox 左上角开始排版 |
| **字体选择** | 两种固定字体：`tiro`（拉丁）+ `noto`（CJK/其他） | 动态系统字体查找：`PickBestFont` → `FindFontForLanguage` |
| **字体嵌入** | 嵌入 Noto Sans + Tiro 字体到每页 | 依赖系统已安装字体（PdfSharpCore 不嵌入） |
| **公式渲染** | 公式字符用**原始字体**在**原始相对位置**重新排列，带纵向偏移修正 `vfix` | 公式文本从占位符还原为原始文本串，作为普通文本渲染 |
| **换行** | 基于段落边界 (`x0`, `x1`)，到达右边界自动换行 | `WrapTextByWidth` / `WrapTextByWidths` 基于字符串宽度测量 |
| **行高** | 动态缩减：`while (lidx+1)*size*lh > height: lh -= 0.05` | 动态缩减：同样逻辑，但以块高度为约束 |
| **双语对照** | 生成两份 PDF：mono (仅翻译) + dual (原文+翻译交替页) | 单份 PDF，翻译覆盖在原文上方 |

**pdf2zh 的字符级 PDF 操作符生成** (`converter.py:384-527`):
```python
def gen_op_txt(font, size, x, y, rtxt):
    return f"/{font} {size:f} Tf 1 0 0 1 {x:f} {y:f} Tm [<{rtxt}>] TJ "

# 逐字符遍历翻译结果
while ptr < len(new):
    vy_regex = re.match(r"\{\s*v([\d\s]+)\}", new[ptr:])
    if vy_regex:  # 公式：用原始字符重新排列
        for vch in var[vid]:
            ops_vals.append({
                "type": OpType.TEXT,
                "font": self.fontid[vch.font],   # 原始字体！
                "size": vch.size,                  # 原始大小！
                "x": x + vch.x0 - var[vid][0].x0, # 原始相对位置！
                "dy": fix + vch.y0 - var[vid][0].y0,
                "rtxt": raw_string(self.fontid[vch.font], chr(vch.cid)),
            })
    else:  # 文字：选择 tiro 或 noto 字体
        if self.fontmap["tiro"].to_unichr(ord(ch)) == ch:
            fcur_ = "tiro"
        else:
            fcur_ = self.noto_name
```

**Easydict 的 Overlay 方式** (`PdfExportService.cs:440-630`):
```csharp
// 1. 尝试隐藏原文 (3 Tr content stream patching)
var sourceHidden = TryHideSourceTextInStream(page, sourceText);

// 2. 如果失败，画白色矩形遮挡
if (!block.SourceHidden)
    gfx.DrawRectangle(XBrushes.White, rect);

// 3. 画翻译文本
gfx.DrawString(line, font, brush, x, y);
```

**内容流替换 vs Overlay 的影响**:

| 特征 | 内容流替换 (pdf2zh) | Overlay (Easydict) |
|------|---------------------|---------------------|
| 公式渲染精度 | ⭐⭐⭐⭐⭐ 原始字体+位置 | ⭐⭐ 文本还原，普通字体 |
| 文件大小 | 较小（替换不增加） | 较大（叠加层增加） |
| PDF 可搜索性 | 翻译文本可搜索 | 翻译文本可搜索，原文隐藏 |
| 图形保真度 | ⭐⭐⭐⭐ 非文字操作符原样保留 | ⭐⭐⭐ 白色矩形可能遮挡邻近图形 |
| 复杂 PDF 兼容性 | 可能因操作符过滤出问题 | 更安全，不修改原始内容 |
| 实现复杂度 | 极高（需理解 PDF 操作符） | 中等（标准图形 API） |

### 6. 字体处理

| 维度 | pdf2zh | Easydict |
|------|--------|----------|
| **CJK 字体** | SourceHanSerif (思源宋体) CN/TW/JP/KR | 系统字体（SimSun / MS Gothic / Malgun Gothic 等） |
| **拉丁字体** | Tiro (PDF 内嵌的原始拉丁字体) | 系统字体（Segoe UI / Arial 等） |
| **其他语言** | GoNotoKurrent-Regular (覆盖阿拉伯/印度/俄语等) | 按语言查找系统字体 |
| **字体嵌入** | 通过 PyMuPDF `page.insert_font()` 嵌入到 PDF | 依赖系统字体，不嵌入 |
| **字体子集化** | `doc.subset_fonts(fallback=True)` 减小文件体积 | 无 |
| **字宽计算** | `font.char_width(cid) * size` 或 `noto.char_lengths(ch, size)` | `gfx.MeasureString(text, font).Width` |

---

## 三、数据流对比图

### pdf2zh 数据流
```
PDF 文件
  │
  ▼
pdfminer PDFParser → PDFDocument → PDFPage
  │
  ▼
PDFPageInterpreterEx.execute()
  │ ─── 过滤文字操作符(T*), 保留图形操作符 → ops_base
  │
  ▼
PDFConverterEx.render_char() ──── 逐字符 LTChar (cid, font, matrix, size)
  │
  ▼
TranslateConverter.receive_layout()
  ├── Section A: 逐字符构建段落
  │   ├── layout[cy,cx] → 类别查找
  │   ├── vflag() → 公式字体/字符检测
  │   ├── 角标检测 (size < 0.79 × parent)
  │   ├── 括号追踪 (vbkt counter)
  │   └── sstk[] (文字段落) + vstk[] → var[] (公式字符组)
  │
  ├── Section B: 并行翻译
  │   ├── ThreadPoolExecutor
  │   ├── 跳过空串/纯公式段落
  │   └── translator.translate(s) → news[]
  │
  └── Section C: 字符级排版
      ├── 逐字符遍历翻译结果
      ├── 公式：原始字符+字体重新定位
      ├── 文字：tiro/noto 字体选择
      ├── 换行：基于 x0/x1 边界
      ├── 行高：动态缩减
      └── gen_op_txt() → PDF 操作符字符串
  │
  ▼
PyMuPDF doc_zh.update_stream(xref, ops_new)
  │ ─── ops = "q {ops_base}Q 1 0 0 1 x0 y0 cm BT {ops_new} ET"
  │
  ▼
mono.pdf + dual.pdf
```

### Easydict 数据流
```
PDF 文件
  │
  ▼
PdfPig PdfDocument.Open() → page.GetWords() / page.Letters
  │
  ▼
ExtractLayoutBlocksFromPage()
  ├── 词 → 行分组 (Y 坐标阈值)
  ├── 行分裂 (列间距检测)
  ├── 行排序 (布局感知)
  └── 段落构建 (间距阈值 1.3×)
  │
  ▼ (可选 ML)
BuildSourceDocumentAsync()
  ├── ONNX/Vision/Heuristic 布局检测
  └── 两阶段词分配 → 区域标签
  │
  ▼
ExtractBlockLetterData() ── 字符级数据采样
  ├── 字体名 (去子集前缀)
  ├── 文本样式 (粗体/斜体/大小)
  └── 公式字符信息 (角标/数学字体)
  │
  ▼
BuildIrAsync()
  ├── 5 级公式检测 → TranslationSkipped
  └── FormulaProtection → {v*} 占位符
  │
  ▼
TranslateBlocksAsync()
  ├── sequential / SemaphoreSlim 并行
  ├── 公式 prompt 注入
  ├── RemoveControlCharacters()
  ├── TrimLeadingSpacesPerLine()
  └── RestoreFormulaSpans()
  │
  ▼
PdfExportService.ExportPdfWithCoordinateBackfill()
  ├── PdfSharpCore 打开原 PDF
  ├── TryHideSourceTextInStream() → "3 Tr" 隐藏
  ├── DrawRectangle(White) → 白色遮挡 (fallback)
  ├── PickBestFont() → 字体选择+缩放
  ├── WrapTextByWidth() → 文本换行
  ├── 动态行高/字号缩减
  └── gfx.DrawString() → Overlay 渲染
  │
  ▼
translated.pdf (单文件)
```

---

## 四、优劣势总结

### pdf2zh 优势
1. **公式渲染精度极高** — 原始字体+CID+位置完美还原
2. **行内公式处理** — 能在同一行内精确分割文字和公式
3. **文件体积小** — 替换内容流不增加体积
4. **字体嵌入** — 不依赖目标系统安装字体
5. **双语对照** — 原生支持 mono + dual 输出

### pdf2zh 劣势
1. **依赖 Python 生态** — pdfminer + PyMuPDF + numpy + cv2
2. **复杂 PDF 兼容性风险** — 操作符过滤可能破坏特殊 PDF
3. **单一布局检测** — 只支持 ONNX，无 fallback
4. **无增量处理** — 无法处理超大 PDF 的内存问题

### Easydict 优势
1. **更安全的 PDF 处理** — Overlay 不修改原始内容
2. **三级布局检测** — ONNX → Vision API → Heuristic 逐级降级
3. **更好的可扩展性** — .NET 生态，WinUI 集成
4. **翻译服务丰富** — 15+ 服务，含流式翻译
5. **缓存系统** — SHA256 块级缓存

### Easydict 劣势
1. **公式渲染精度低** — 无法使用原始数学字体，行内公式变为纯文本
2. **字体不嵌入** — 依赖系统字体，跨平台可能缺字
3. **白色矩形遮挡** — 可能遮挡邻近图形
4. **无双语对照** — 只有 overlay 模式

---

## 五、可改进方向（按优先级）

### P0 — 架构级改进（大工程）
1. **字符级 content stream 操作** — 用 PdfSharpCore 或新库实现类似 pdf2zh 的内容流替换，需要解析 PDF 操作符
2. **字体嵌入** — 将 Noto/SourceHan 字体嵌入到输出 PDF

### P1 — 功能改进（中等工程）
3. **双语对照输出** — 生成 mono + dual PDF
4. **行内公式保留** — 在提取阶段保留字符级公式信息，渲染时重新定位

### P2 — 质量微调（已完成 Round 1-3）
5. ✅ MathFontRegex 扩展
6. ✅ 括号分组保护
7. ✅ CID 字符检测
8. ✅ 字体前缀剥离
9. ✅ 控制字符清理
10. ✅ 角标密度检测
11. ✅ 语言行高扩展
12. ✅ 行首空格清理

---

## 六、C# 版 pdfminer + PyMuPDF 实现方案（深度调研完成）

### 问题：实现一个 C# 版本的 pdfminer 可以完成多少实现对齐？

### 核心发现：对齐度从 80% 提升到 95%+

**之前的瓶颈分析（已过时）：** PDF 输出层无 PyMuPDF 等价物
**新发现：** [MuPDF.NET](https://github.com/ArtifexSoftware/MuPDF.NET) 是 Artifex 官方 C# 绑定，与 PyMuPDF API **1:1 对应**，彻底消除输出层瓶颈。

### 1. pdf2zh 对 pdfminer 的核心依赖（15 个模块，30+ 类/函数）

### 1. PdfPig 源码 fork 完成

PdfPig 源码已作为 git submodule 添加到 `lib/PdfPig/`。

### 2. PdfPig 内部 API 深度调研结果

**关键发现：PdfPig 内部已具备 pdfminer 90%+ 的能力，只是没有暴露为公共 API。**

#### 已有公共 API（无需修改）

| API | 文件 | 用途 |
|-----|------|------|
| `Page.Operations` | `Content/Page.cs:84` | `IReadOnlyList<IGraphicsStateOperation>` — 已解析的内容流操作符！ |
| `Page.Letters` | `Content/Page.cs:54` | 所有提取的字符 |
| `Page.Dictionary` | `Content/Page.cs:27` | 原始 PDF 页面字典 |
| `Letter.GetFont()` | `Content/Letter.cs:232` | 返回 `IFont?` — 字体对象已可访问 |
| `IFont.ReadCharacterCode()` | `PdfFonts/IFont.cs:31` | 读取字符编码 |
| `IFont.TryGetUnicode()` | `PdfFonts/IFont.cs:36` | 编码→Unicode |
| `IFont.GetBoundingBox()` | `PdfFonts/IFont.cs:41` | 字形度量 |
| `IFont.GetFontMatrix()` | `PdfFonts/IFont.cs:46` | 字体变换矩阵 |
| `IFont.TryGetPath()` | `PdfFonts/IFont.cs:71` | 字形轮廓！ |
| `IGraphicsStateOperation.Write(Stream)` | `Graphics/Operations/IGraphicsStateOperation.cs:19` | **可将操作符写回 PDF 内容流！** |
| `IGraphicsStateOperation.Operator` | 同上:13 | 操作符名称（"Tj", "Tf", "BT" 等） |

**重大发现：`Page.Operations` + `IGraphicsStateOperation.Write()` 组合意味着我们已经可以：**
1. 枚举所有内容流操作符
2. 过滤文字 vs 图形操作符（类似 pdf2zh 的 `execute()`）
3. 通过 `Write(Stream)` 重建内容流字符串

#### 需要 fork 修改的部分

| 修改 | 文件 | 当前状态 | 需要做的 |
|------|------|----------|---------|
| **Letter 添加 CharacterCode** | `Content/Letter.cs` | `RenderGlyph()` 有 `int code` 参数但未存储 | 添加 `public int CharacterCode { get; }` 属性 |
| **Letter 添加 TextMatrix** | `Content/Letter.cs` | `RenderGlyph()` 有 `textMatrix` 参数但未存储 | 添加 `public TransformationMatrix TextMatrix { get; }` |
| **Letter 添加 CTM** | `Content/Letter.cs` | `RenderGlyph()` 有 `transformationMatrix` 参数但未存储 | 添加 `public TransformationMatrix CurrentTransformationMatrix { get; }` |
| **IFont 添加 GetCid()** | `PdfFonts/IFont.cs` | `Type0Font.CMap.ConvertToCid()` 是 internal | 添加 `int GetCid(int characterCode)` 到 IFont（非 CID 字体返回 characterCode） |
| **Type0Font 检测** | `PdfFonts/IFont.cs` | `Type0Font` 是 internal | 添加 `bool IsCidFont { get; }` 到 IFont |

#### CID 丢失的精确位置

```
BaseStreamProcessor.ShowText() (Graphics/BaseStreamProcessor.cs:216-320)
  ↓ line 257
  int code = font.ReadCharacterCode(bytes, out codeLength)   ← 原始字符编码
  ↓ line 259
  font.TryGetUnicode(code, out unicode)                       ← CID 在此被转换为 Unicode
  ↓ line 293-303
  RenderGlyph(..., code, unicode, ...)                        ← code 传入但未存到 Letter
  ↓
  ContentStreamProcessor.RenderGlyph() (Graphics/ContentStreamProcessor.cs:88-182)
  ↓ line 164-177
  new Letter(unicode, ..., font, ...)                         ← 只保存 unicode，丢失 code
```

**Type0Font.TryGetUnicode() 内部流程** (`PdfFonts/Composite/Type0Font.cs:99-141`):
```
characterCode → CMap.ConvertToCid(characterCode) → CID        ← CID 在此计算
CID → ucs2CMap.TryConvertToUnicode(CID) → unicode             ← 但只返回 unicode
```

### 3. MuPDF.NET — PyMuPDF 的官方 C# 绑定

**彻底消除了之前分析中 "PDF 输出层无等价物" 的瓶颈。**

| PyMuPDF (Python) | MuPDF.NET (C#) | pdf2zh 用途 |
|----------|------------|------------|
| `Document(stream=bytes)` | `new Document(bytes)` | 加载 PDF |
| `doc.page_count` | `doc.PageCount` | 页数 |
| `page.get_pixmap()` | `page.GetPixmap()` | 渲染为像素（ML 布局检测） |
| `page.insert_font(name, path)` | `page.InsertFont(name, path)` | 嵌入字体 |
| `doc.get_new_xref()` | `doc.GetNewXref()` | 创建新 XRef 对象 |
| `doc.update_object(xref, "<<>>")` | `doc.UpdateObject(xref, "<<>>")` | 初始化 XRef |
| `doc.update_stream(xref, data)` | `doc.UpdateStream(xref, data)` | 替换流内容 |
| `doc.xref_get_key(xref, key)` | `doc.XrefGetKey(xref, key)` | 读取 XRef 字典键 |
| `doc.xref_set_key(xref, key, val)` | `doc.XrefSetKey(xref, key, val)` | 设置 XRef 字典键 |
| `page.set_contents(xref)` | `page.SetContents(xref)` | 设置页面内容流 |
| `doc.insert_file(other)` | `doc.InsertFile(other)` | 插入另一文档的页面 |
| `doc.move_page(src, dst)` | `doc.MovePage(src, dst)` | 移动页面位置 |
| `doc.subset_fonts(fallback=True)` | `doc.SubsetFonts()` | 字体子集化 |
| `fitz.Font(name, path)` | `new Font(name, path)` | 创建字体对象 |
| `font.has_glyph(cp)` | `font.HasGlyph(cp)` | 检测字形 |
| `font.char_lengths(char, size)` | `font.CharLengths(char, size)` | 字符前进宽度 |

**许可证：** AGPL-3.0 / Artifex Community License。与 Easydict GPL-3.0 兼容（桌面应用不触发 AGPL 的网络使用条款）。
**NuGet:** `MuPDF.NET` v3.2.12，`.NET 8+`。

### 4. 新架构设计

```
              READ (PdfPig fork)                      WRITE (MuPDF.NET)
         ┌───────────────────────┐            ┌────────────────────────────┐
         │  PdfDocument.Open()   │            │  new Document(bytes)       │
         │      ↓                │            │      ↓                     │
         │  page.Operations      │──ops_base──│  doc.GetNewXref()          │
         │  (IGraphicsStateOp[]) │  (图形ops) │  doc.UpdateObject(xref)    │
         │      ↓                │            │  doc.UpdateStream(xref,    │
         │  page.Letters         │            │    ops_base + ops_new)     │
         │  + CharacterCode (新) │──字符级──→ │  page.SetContents(xref)    │
         │  + GetFont() → IFont  │   数据     │      ↓                     │
         │  + GetCid() (新)      │            │  page.InsertFont(          │
         │      ↓                │            │    "noto", noto_path)      │
         │  ContentStream        │            │      ↓                     │
         │  Interpreter          │            │  gen_op_txt() → PDF ops    │
         │  (自定义过滤层)        │            │  "/{font} {sz} Tf          │
         │                       │            │   1 0 0 1 {x} {y} Tm      │
         │  DocLayout ONNX       │            │   [<{hex}>] TJ"           │
         │  (现有布局检测)        │            │      ↓                     │
         └───────────────────────┘            │  doc.SubsetFonts()         │
                                              │  doc.Save(output)          │
                                              │      ↓                     │
                                              │  Dual PDF:                 │
                                              │  doc_en.InsertFile(doc_zh) │
                                              │  doc_en.MovePage() 交错    │
                                              └────────────────────────────┘
```
### 5. 对齐度评估（更新：PdfPig fork + MuPDF.NET）

| pdf2zh 功能 | C# 对齐度 | 难度 | 方案 |
|-------------|----------|------|------|
| **PDF 文档解析** | 100% | ✅ 已完成 | PdfPig |
| **内容流操作符解析** | 100% | ✅ 已有 | `Page.Operations` 已是公共 API |
| **操作符过滤重建** | 100% | 低 | `IGraphicsStateOperation.Write(Stream)` 已支持 |
| **CID 编码保留** | 95% | 低 | fork 添加 `Letter.CharacterCode`（6 行改动） |
| **字体对象访问** | 95% | ✅ 已有 | `Letter.GetFont()` → `IFont` 已是公共 API |
| **字符级布局树** | 95% | 低 | fork 添加 `Letter.TextMatrix`/`CTM` |
| **矩阵运算** | 100% | ✅ 已有 | PdfPig `TransformationMatrix` |
| **双向字体映射** | 95% | 低 | fork 暴露 `IFont.IsCidFont`/`GetCid()` |
| **内容流替换** | 100% | 低 | MuPDF.NET `UpdateStream()` + `SetContents()` |
| **XRef 操作** | 100% | 低 | MuPDF.NET `GetNewXref()`/`UpdateObject()`/`XrefSetKey()` |
| **字体嵌入** | 100% | 低 | MuPDF.NET `InsertFont()` |
| **双语 PDF 输出** | 100% | 低 | MuPDF.NET `InsertFile()` + `MovePage()` |
| **字体子集化** | 100% | 低 | MuPDF.NET `SubsetFonts()` |
| **页面渲染** | 100% | 低 | MuPDF.NET `GetPixmap()` (可替代现有 Windows API) |

**综合对齐度：95%+**（之前评估 80-85%，MuPDF.NET 消除了所有 "❌ 难以对齐" 的瓶颈）

### 6. PdfPig Fork 具体修改清单

所有修改在 `lib/PdfPig/` submodule 中进行。

#### 6.1 Letter 类添加 CharacterCode + 矩阵

**文件：** `lib/PdfPig/src/UglyToad.PdfPig/Content/Letter.cs`

```csharp
// 新增属性
public int CharacterCode { get; }                            // 原始 PDF 字符编码
public TransformationMatrix TextMatrix { get; }              // 文字矩阵 Tm
public TransformationMatrix CurrentTransformationMatrix { get; }  // CTM
```

**修改私有构造函数** (line 160-198)：添加三个参数并赋值。

**修改 RenderGlyph 调用链：**
- `ContentStreamProcessor.RenderGlyph()` (`Graphics/ContentStreamProcessor.cs:88`) — 已接收 `code`, `textMatrix`, `transformationMatrix` 参数，只需传递到 Letter 构造函数

#### 6.2 IFont 添加 CID 支持

**文件：** `lib/PdfPig/src/UglyToad.PdfPig/PdfFonts/IFont.cs`

```csharp
// 新增方法
bool IsCidFont { get; }                  // 是否为 CID 字体（Type0Font）
int GetCid(int characterCode);           // 获取 CID（非 CID 字体返回 characterCode）
```

**实现：**
- `Type0Font` (`PdfFonts/Composite/Type0Font.cs`): `IsCidFont => true`, `GetCid => CMap.ConvertToCid(code)`
- 其他字体: `IsCidFont => false`, `GetCid => characterCode`

#### 6.3 暴露 ResourceStore 字体映射

**文件：** `lib/PdfPig/src/UglyToad.PdfPig/Content/IResourceStore.cs`

```csharp
// 新增方法
IReadOnlyDictionary<NameToken, IFont> GetFontMap();  // 返回 fontName→IFont 映射
```

### 7. 实现路线图（更新版）

```
Phase 1: PdfPig Fork 修改                    约 3 天
├── Letter 添加 CharacterCode/TextMatrix/CTM（~30 行改动）
├── IFont 添加 IsCidFont/GetCid()（~15 行改动）
├── IResourceStore 暴露字体映射（~10 行改动）
├── 更新 .csproj 从 NuGet 引用改为 ProjectReference
└── 单元测试验证 CID 保留正确

Phase 2: ContentStreamInterpreter           约 1 周
├── 新类 ContentStreamInterpreter（Easydict.TranslationService 中）
├── 操作符过滤（保留图形 ops，过滤文字 ops）
│   → 遍历 page.Operations，按 op.Operator 分类
│   → 非文字 ops 写入 MemoryStream → ops_base
│   → 文字 ops: "BT"/"ET"/"Tj"/"TJ"/"Tf"/"Tm"/"Td" 等
├── CTM 矩阵追踪（遇到 "cm" 操作符时累积）
├── Form XObject 处理（遇到 "Do" 时递归处理）
└── 输出: ops_base (byte[]) + 字符列表 (List<CharInfo>)

Phase 3: 字符级段落构建 + 公式检测        约 1 周
├── 像素级 layout[cy,cx] 分类查找（复用现有 DocLayout ONNX）
├── vflag() 公式字体/字符检测
│   → letter.GetFont().Details.Name 匹配数学字体正则
│   → letter.CharacterCode 匹配公式 Unicode 范围
├── 角标检测（child.PointSize < parent.PointSize * 0.79）
├── 括号追踪（vbkt 计数器）
├── sstk[]/vstk[] 段落/公式分组
└── 对比 pdf2zh 输出验证正确性

Phase 4: MuPDF.NET PDF 输出               约 1.5 周
├── 添加 MuPDF.NET NuGet 包
├── 字体嵌入: page.InsertFont("noto", notoPath)
├── gen_op_txt(): 生成翻译文字的 PDF 操作符
│   → "/{fontId} {size:f} Tf 1 0 0 1 {x:f} {y:f} Tm [<{hexCid}>] TJ"
├── 公式字符: 使用原始字体 CID 重新定位
├── 内容流替换:
│   → xref = doc.GetNewXref()
│   → doc.UpdateObject(xref, "<<>>")
│   → doc.UpdateStream(xref, ops_base + ops_new)
│   → page.SetContents(xref)
├── 字体子集化: doc.SubsetFonts()
└── Dual PDF: doc_en.InsertFile(doc_zh) + MovePage() 交错

Phase 5: 集成 + 替换现有 PdfSharpCore     约 1 周
├── 新增 MuPdfExportService 替代 PdfExportService
├── 保留 PdfSharpCore 作为 fallback（渐进迁移）
├── 与现有翻译管线整合
├── 大量 PDF 回归测试
└── 性能对比（MuPDF.NET vs PdfSharpCore overlay）
```

**总计：约 4 周核心开发**（比之前估算的 6 周减少 30%，因为 MuPDF.NET 消除了大量变通代码）

### 8. 关键文件清单（实现状态）

| 文件 | 操作 | 说明 | 状态 |
|------|------|------|------|
| `lib/PdfPig/src/UglyToad.PdfPig/Content/Letter.cs` | 修改 | 添加 CharacterCode/TextMatrix/CTM | ✅ Phase 1 |
| `lib/PdfPig/src/UglyToad.PdfPig/PdfFonts/IFont.cs` | 修改 | 添加 IsCidFont/GetCid() | ✅ Phase 1 |
| `lib/PdfPig/src/UglyToad.PdfPig/PdfFonts/Composite/Type0Font.cs` | 修改 | 实现 IsCidFont/GetCid() | ✅ Phase 1 |
| `lib/PdfPig/src/UglyToad.PdfPig/Graphics/ContentStreamProcessor.cs` | 修改 | 传递 code/matrices 到 Letter | ✅ Phase 1 |
| `lib/PdfPig/src/UglyToad.PdfPig/Content/IResourceStore.cs` | 修改 | 添加 GetFontMap() | ✅ Phase 1 |
| `lib/PdfPig/src/UglyToad.PdfPig/Content/ResourceStore.cs` | 修改 | 实现 GetFontMap() | ✅ Phase 1 |
| `lib/PdfPig/src/UglyToad.PdfPig/Util/StackDictionary.cs` | 修改 | 添加 GetAllEntries() | ✅ Phase 1 |
| `dotnet/src/Easydict.WinUI/Easydict.WinUI.csproj` | 修改 | NuGet→ProjectReference + MuPDF.NET | ✅ Phase 1+4 |
| `dotnet/src/Easydict.WinUI/Services/ContentStreamInterpreter.cs` | 新增 | 操作符过滤 + CharInfo + 工具方法 | ✅ Phase 2 |
| `dotnet/src/Easydict.WinUI/Services/CharacterParagraphBuilder.cs` | 新增 | 字符级段落构建 + 公式检测 | ✅ Phase 3 |
| `dotnet/src/Easydict.WinUI/Services/DocumentExport/MuPdfExportService.cs` | 新增 | MuPDF.NET 内容流替换输出 | ✅ Phase 4 |
| `dotnet/src/Easydict.WinUI/Services/DocumentExport/IDocumentExportService.cs` | 修改 | 添加 PdfExportMode 枚举 | ✅ Phase 5 |
| `dotnet/src/Easydict.WinUI/Services/LongDocumentTranslationService.cs` | 修改 | 集成 MuPdfExportService | ✅ Phase 5 |
| `dotnet/tests/.../ContentStreamInterpreterTests.cs` | 新增 | 操作符过滤测试 | ✅ Phase 2 |
| `dotnet/tests/.../CharacterParagraphBuilderTests.cs` | 新增 | 段落构建测试 | ✅ Phase 3 |

### 9. 验证方案

1. **CID 保留测试**：用含 CJK 字体的 PDF，提取 Letter.CharacterCode，验证 IFont.GetCid() 返回正确 CID
2. **操作符过滤测试**：对比 page.Operations 过滤结果与 pdf2zh 的 ops_base 输出
3. **内容流往返测试**：page.Operations → Write(Stream) → 与原始内容流比较
4. **MuPDF.NET 集成测试**：UpdateStream + InsertFont + Save → 验证输出 PDF 可正常打开
5. **端到端对比**：同一 PDF 分别用 pdf2zh 和新 C# 管线翻译，对比输出质量
6. **现有测试**：`dotnet test` 确保所有现有测试通过

### 10. 结论

| 维度 | 旧评估 | 新评估 |
|------|--------|--------|
| **总体对齐度** | 80-85% | **95%+** |
| **最大瓶颈** | PDF 输出层无等价物 | **已消除** (MuPDF.NET) |
| **PdfPig 改动量** | ~200 行 | **~55 行**（大部分 API 已公开） |
| **推荐方案** | Fork PdfPig + PdfSharpCore | **Fork PdfPig + MuPDF.NET** |
| **开发周期** | ~6 周 | **~4 周** |
| **剩余差距** | XRef/字体子集化/Form XObject | **仅 Form XObject 复杂场景** |

**核心结论**：
1. PdfPig 的公共 API 比之前分析的更强大（`Page.Operations`、`Letter.GetFont()`、`IGraphicsStateOperation.Write()` 已公开），fork 改动量极小
2. MuPDF.NET 提供了与 PyMuPDF 完全对等的 C# API，彻底消除了 PDF 输出层瓶颈
3. PdfPig (READ) + MuPDF.NET (WRITE) 的组合可以实现与 pdf2zh 95%+ 的架构对齐
