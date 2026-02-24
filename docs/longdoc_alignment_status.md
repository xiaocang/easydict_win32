# Long Document Pipeline Alignment Status (vs pdf-math-translate style)

## 已完成改动（当前分支）

### 1) OCR fallback 输入契约
- PDF 扫描页在构建 `SourceDocument` 时支持 `IsScanned = true` 且可为空块，避免在 ingest 前被错误阻断。

### 2) 布局与区域语义
- 增加 `LayoutRegionType`。
- `LongDocumentChunkMetadata` 新增：
  - `RegionType`
  - `ReadingOrderScore`
  - `OrderInPage`
  - `BoundingBox`
- PDF 布局抽取路径支持：
  - 词->行->段结构化构建
  - 简单双栏排序启发
  - 区域推断（header/footer/left/right/table/body）

### 3) 回填导出
- 导出优先走 PDF 回填路径（当有源 PDF）
- 回填逻辑：
  - 先尝试对象级文本替换（best effort）
  - 失败自动降级 overlay 绘制
  - 支持字体自适应、行裁剪与省略号

### 4) 质量报告
- `LongDocumentQualityReport` 已使用强类型 `BackfillQualityMetrics` 承载回填指标（替代弱类型字典）。
- `StageTimingsMs` 保持耗时语义，不再混入回填计数。
- 重试结果合成时，保留 core 侧 backfill 指标。

### 5) 公式保护
- 增加 span 级公式 token 保护与恢复，避免行内公式在翻译后被污染。
- “纯公式 token 文本”可跳过翻译，混排段落保持可译。

### 6) 测试
- 新增/扩展：
  - `LongDocumentTranslationServiceTests`
  - `LongDocumentE2EBaselineTests`
  - `LongDocumentTranslationServiceReviewFixTests`

---

## TODO 列表（短期）

1. ~~**对象级替换增强**~~ ✅
   - 对象级替换由单一 literal token patch 扩展到 `TJ` 文本数组场景（多片段文本可合并匹配并回写为 `Tj`）。
   - 目前仍以 ASCII literal 文本为主，复杂编码/字体子集场景继续保留 overlay 降级。

2. ~~**回填指标模型规范化**~~ ✅
   - 已将 `BackfillMetrics` 从 `Dictionary<string,long>` 升级为强类型 `BackfillQualityMetrics`。

3. ~~**重试路径指标合并策略**~~ ✅
   - 已实现 retry 指标合并策略（`core-only` / `checkpoint-only` / `accumulate`）。

4. ~~**布局区域推断鲁棒性**~~ ✅
   - 已引入 `LayoutProfile`（基于页面行几何分布），区域推断改为自适应 header/footer 阈值与双栏边界。
   - table-like 判定补充了制表符、多空格、`|` 分隔及数值矩阵模式，降低误判。

5. ~~**测试执行门禁**~~ ✅
   - CI 新增长文档回归门禁步骤：`FullyQualifiedName~LongDocument` 必跑并阻断失败。

---

## 改进列表（中期）

> 目标：将“可回填、可解释、可回归”的能力从当前实现提升到稳定工程化阶段。

### M1（优先级 P0）：对象级回填可观测性 ✅（本轮已落地）
- **改动目标**：输出页面级回填模式分布（object replace / overlay / structured fallback）。
- **建议实现**：
  - 在 `BackfillQualityMetrics` 基础上补充 page-level 明细结构（如 `Dictionary<int, PageBackfillMetrics>`）。
  - 在导出阶段记录每页三类计数、候选块数、实际渲染块数。
- **验收标准**：
  - 质量报告可追溯到“哪一页为何降级”。
  - UI/日志可直接展示“高风险页 TopN”。
- **当前实现状态**：
  - 已在 `BackfillQualityMetrics` 增加 `PageMetrics`，并在 PDF 回填阶段产出 page-level object/overlay/fallback 分布。
  - 重试路径指标合并已支持 page-level metrics 累加。

### M2（优先级 P0）：结构化布局能力 ✅（本轮已落地）
- **改动目标**：为 `RegionType` 增加置信度与来源标签（heuristic / parser / fallback）。
- **建议实现**：
  - 扩展 chunk metadata：`RegionConfidence`（0~1）+ `RegionSource`。
  - `BuildLayoutProfile` 推断产物带上判定依据（双栏分位跨度、header/footer 阈值命中等）。
- **验收标准**：
  - longdoc checkpoint 中可观察 region 判定来源。
  - 误判样例可通过阈值或来源快速定位。
- **当前实现状态**：
  - `LongDocumentChunkMetadata` 新增 `RegionConfidence` 与 `RegionSource`。
  - 已实现 `InferRegionInfoFromBlockId`，统一输出 `(Type, Confidence, Source)` 并接入 checkpoint 构建。

### M3（优先级 P1）：术语一致性与上下文增强 ✅（本轮已落地）
- **改动目标**：在重试翻译中引入按页/章节的术语记忆窗口。
- **建议实现**：
  - 维护最近 N 页术语表（source->canonical translation）。
  - 重试 prompt 注入局部术语集并设置冲突优先级（当前页 > 章节 > 全局）。
- **验收标准**：
  - 同术语跨页翻译一致性提升（可通过 E2E 基线统计）。
- **当前实现状态**：
  - 术语复用从“全局首个命中”升级为“按页窗口优先”（默认 ±2 页），并保留全局回退。
  - `EnforceTerminologyConsistency` 与重试路径复用逻辑已共享该策略。

### M4（优先级 P1）：公式恢复精度提升 ✅（本轮已落地）
- **改动目标**：细化公式 token 类型并增加恢复校验。
- **建议实现**：
  - token 分类：行内公式、显示公式、单位片段。
  - 恢复后做结构校验（括号平衡、分隔符成对、关键符号保留）。
- **验收标准**：
  - 公式破坏率下降；纯公式块保持跳译行为不回退。
- **当前实现状态**：
  - 公式 token 新增类型分类：`InlineMath` / `DisplayMath` / `UnitFragment`。
  - 公式恢复新增校验：若存在未恢复 token 或分隔符不平衡，则回退原文，避免输出破损公式。

### M5（优先级 P1）：E2E 基线资产扩展
- **改动目标**：补齐样本集与快照对比脚本。
- **建议实现**：
  - 新增多栏论文、表格密集页、扫描件、公式密集页样本。
  - 增加关键指标快照（overlay 占比、missing bbox、truncated）。
- **验收标准**：
  - CI 可对 longdoc 关键场景给出稳定回归结论。

### M6（优先级 P2）：UI 展示改进
- **改动目标**：WinUI 展示 `BackfillMetrics` 摘要与告警。
- **建议实现**：
  - 结果页展示：对象替换成功率、overlay 比例、缺 bbox 比例、截断计数。
  - 阈值触发告警（例如 overlay > 60%）。
- **验收标准**：
  - 用户可快速判断“结果可读性风险”。

---

## 上游对标差距分析（vs PDFMathTranslate，桌面端适用）

> 参考项目：[PDFMathTranslate](https://github.com/Byaidu/PDFMathTranslate)（Python，AGPL-3.0）
> 对标版本：v1.9.11
> 分析日期：2026-02-24
>
> 以下仅列出**适用于桌面端**的差距项，已排除 Gradio Web UI、Flask/Celery REST API、MCP Server、Docker 部署等纯服务端/Web 特性。

---

### G1（P0）：双语/对照 PDF 输出 ✅

| 维度 | PDFMathTranslate | Easydict Win32 |
|------|-----------------|----------------|
| 输出模式 | 每次翻译产出 **两份** PDF：单语 (`*-mono.pdf`) + 双语对照 (`*-dual.pdf`) | `DocumentOutputMode`：Monolingual / Bilingual / Both |
| 双语实现 | `doc_en.move_page()` 交错插入原文页与译文页 | `ExportBilingualPdf` 交错插入原文页与译文覆盖页 |
| 导出格式 | PDF | PDF / Markdown / Plain Text 三种导出器 |

**已实现**：
- `IDocumentExportService` 接口 + 三种导出器（`PdfExportService` / `MarkdownExportService` / `PlainTextExportService`）
- `DocumentOutputMode` 枚举：Monolingual / Bilingual / Both
- 双语 PDF：原文页与译文页交错排列，直接复制原始页面保持完整布局
- 设置页面：输出模式选择 ComboBox
- 本地化：15 语言 × 6 条

---

### G2（P0）：ML 布局检测（DocLayout-YOLO） ✅

| 维度 | PDFMathTranslate | Easydict Win32 |
|------|-----------------|----------------|
| 检测方式 | **DocLayout-YOLO ONNX 模型**推理（HuggingFace 下载） | **DocLayout-YOLO ONNX 模型**推理（HuggingFace + GitHub Releases 双源下载） + 启发式回退 |
| 检测类别 | Text、Figure、Table、Formula、Caption、Isolated Formula 等 | Title、PlainText、Figure、Table、Formula、Caption、TableCaption、TableFootnote、IsolatedFormula、FormulaCation（10 类） + 启发式 6 类 |
| 置信度 | 模型输出 confidence（阈值 0.25） | ONNX 模型 confidence（阈值 0.25） + 启发式 `RegionConfidence` |
| 图表/公式区域保护 | Figures/Tables/Formulas 标记 “abandoned”（整块跳过） | ML 检测 Figure/Formula/IsolatedFormula 标记 `IsFormulaLike = true` 跳过翻译 |
| 模式选择 | 固定使用 ML | 四种模式：Auto（ONNX → 启发式回退）/ OnnxLocal / VisionLLM / Heuristic |
| Vision LLM | 无 | 支持 GPT-4V / Gemini Vision 等视觉大模型布局检测（结构化 JSON prompt） |
| 模型管理 | 随 pip 包安装 | 首次使用时下载（`LayoutModelDownloadService`），支持 HuggingFace → GitHub Releases 双源回退 |

**已实现**：
- `LayoutDetectionMode` 枚举：`Heuristic` / `OnnxLocal` / `VisionLLM` / `Auto`
- `LayoutModelDownloadService`：ONNX Runtime 原生 DLL + DocLayout-YOLO 模型首次使用时下载至 `%LocalAppData%\Easydict\Models\`，多源回退 + 指数退避重试 + 进度报告
- `DocLayoutYoloService`：ONNX 推理服务，letterbox 预处理（1024×1024）、YOLO 输出解析、NMS 后处理、`NativeLibrary.SetDllImportResolver` 动态加载
- `VisionLayoutDetectionService`：视觉大模型布局检测，结构化 JSON prompt，百分比坐标输出
- `LayoutDetectionStrategy`：策略编排，PDF 页面渲染（`Windows.Data.Pdf` WinRT），IoU 匹配（阈值 0.3）合并 ML 检测与启发式文本块
- `LongDocumentTranslationService`：扩展 `LayoutRegionType`（+6 ML 类型）、`LayoutRegionSource`（+OnnxModel / VisionLLM），新增异步 `BuildSourceDocumentAsync`
- 设置页面：布局检测模式选择、ONNX 模型下载/删除/进度 UI、Vision LLM 服务选择
- 本地化：en-US / zh-CN 共 24 条新增字符串
- NuGet 依赖：`Microsoft.ML.OnnxRuntime.Managed 1.21.0`（仅托管 API，原生 DLL 按需下载）
- 测试：`DocLayoutYoloServiceTests`、`LayoutDetectionStrategyTests`、`LayoutModelDownloadServiceTests`（含 VisionLayoutDetection 响应解析测试）

---

### G3（P1）：三级公式检测 ✅

| 维度 | PDFMathTranslate | Easydict Win32 |
|------|-----------------|----------------|
| 第 1 级：布局级 | DocLayout-YOLO 检测整块公式区域 → 整块跳过 | DocLayout-YOLO + `SourceBlockType.Formula` + `IsFormulaLike` |
| 第 2 级：字体级 | 正则匹配数学字体名：`CM[^R]|MS.M|XY|MT` 等 | `IsFontBasedFormula` — PdfPig `Letter.FontName` + 正则（CMSY/CMMI/CMEX/Symbol/Mathematica/STIX 等） |
| 第 3 级：字符级 | Unicode 分类分析（数学符号、希腊字母、修饰符） | `IsCharacterBasedFormula` — Unicode 数学符号/希腊字母/上下标范围，>30% 阈值 |
| 占位符机制 | `{vN}` 占位 | `[[FORMULA_N_HASH]]` 占位 + 类型分类（InlineMath/DisplayMath/UnitFragment） |
| 用户可配 | `--vfont`（字体正则）、`--vchar`（字符正则） | `FormulaFontPattern` / `FormulaCharPattern` 设置页自定义正则 |

**已实现**：
- `SourceDocumentBlock.DetectedFontNames`：PdfPig 提取每个块内字母的字体名列表
- `IsFontBasedFormula`：数学字体占比 >50% 标记为公式（Level 2）
- `IsCharacterBasedFormula`：数学 Unicode 字符占比 >30% 标记为公式（Level 3）
- `BuildIr` 合并三级检测结果：Layout + Font + Character
- 设置页面：Formula Detection section（字体正则 + 字符正则 TextBox）
- 本地化：15 语言 × 5 条
- 测试：`FormulaDetectionTests`（字体检测、字符检测、自定义模式覆盖）

---

### G4（P1）：并行翻译 ✅

| 维度 | PDFMathTranslate | Easydict Win32 |
|------|-----------------|----------------|
| 并发模型 | 默认 **4 线程**（`--thread` 可配） | `SemaphoreSlim` + `Task.WhenAll`，默认 **4 并发**（`LongDocMaxConcurrency` 可配 1-16） |
| 粒度 | 页内文本段并行翻译 | 块级并行翻译 |
| 向后兼容 | — | `MaxConcurrency = 1` 时走顺序路径 |

**已实现**：
- `LongDocumentTranslationOptions.MaxConcurrency`：并发度配置（默认 1，WinUI 默认 4）
- `ConcurrentDictionary` + `SemaphoreSlim` + `Task.WhenAll` 并行翻译
- 顺序路径保持为 `MaxConcurrency = 1` 的向后兼容
- 设置页面：NumberBox（1-16）
- 本地化：15 语言 × 3 条
- 测试：`ParallelTranslationTests`（7 个测试）

---

### G5（P1）：CJK/多语言字体嵌入 ✅

| 维度 | PDFMathTranslate | Easydict Win32 |
|------|-----------------|----------------|
| CJK 字体 | 按目标语言下载 SourceHanSerif 区域变体（CN/JP/KR/TW） | 按目标语言下载 Noto Sans CJK 变体（SC/TC/JP/KR） |
| 字体加载 | PyMuPDF `page.insert_font()` | PdfSharpCore `IFontResolver` 自定义实现（`CjkFontResolver`） |
| 行高适配 | `LANG_LINEHEIGHT_MAP` 按目标语言调整（中文 1.4、俄文 0.8 等） | `LineHeightMultipliers`：zh 1.4、ja 1.4、ko 1.3 |
| 字体子集化 | 默认启用 | 暂无（全量字体嵌入） |
| 回退字体 | GoNotoKurrent | Arial（CJK 字体不可用时回退） |

**已实现**：
- `FontDownloadService`：4 种 CJK 字体按需下载（NotoSansSC/TC/JP/KR），GitHub 源
- `CjkFontResolver`：PdfSharpCore `IFontResolver` 实现，全局注册，动态加载字体文件
- `PdfExportService` 扩展：`ResolveFontFamily` / `GetLineHeight` / `EnsureCjkFontSetup`
- `LongDocumentTranslationCheckpoint.TargetLanguage`：传递目标语言至导出管线
- 设置页面：CJK Font section（下载/删除按钮 + 进度条）
- 本地化：15 语言 × 5 条
- 测试：`FontDownloadServiceTests` + `PdfExportServiceFontTests`

---

### G6（P1）：持久化翻译缓存 ✅

| 维度 | PDFMathTranslate | Easydict Win32 |
|------|-----------------|----------------|
| 缓存粒度 | **段落级**（每段翻译结果独立缓存） | **段落级**（SHA256 哈希 → 翻译结果，`TranslationCacheService`） |
| 存储 | SQLite（Peewee ORM）+ WAL 模式 | SQLite（`Microsoft.Data.Sqlite`）|
| 缓存键 | `(translate_engine, engine_params, original_text)` 复合唯一键 | `(service_id, from_lang, to_lang, source_hash)` UNIQUE 约束 |
| 命中统计 | 无 | `hit_count` + `last_used_utc` 追踪 |
| 绕过缓存 | `--ignore-cache` CLI 参数 | `EnableTranslationCache` 设置开关 |

**已实现**：
- `TranslationCacheService`：SQLite 持久化缓存，UPSERT 语义，SHA256 源文本哈希
- `WriteCacheEntriesAsync`：翻译完成后自动写入缓存
- `ReadCacheEntriesAsync`：翻译前查询缓存，命中则跳过 API 调用（P2 修复）
- `TranslatePendingChunksAsync` 缓存集成：重试路径自动查缓存
- `EnableTranslationCache` 设置：开/关切换
- 设置页面：Translation Cache section（开关 + 清除缓存按钮 + 缓存条目计数）
- NuGet：`Microsoft.Data.Sqlite 9.0.0`
- 本地化：15 语言 × 4 条
- 测试：`TranslationCacheServiceTests`（8 个测试）

---

### G7（P2）：页范围选择 ✅

| 维度 | PDFMathTranslate | Easydict Win32 |
|------|-----------------|----------------|
| 选择方式 | `--pages` 参数：All / First / First 5 / 自定义范围（如 `1-5,8,10-12`） | `LongDocPageRange` 设置 + `PageRangeParser`（如 `1-3,5,7-10`） |
| GUI 支持 | Gradio 下拉 + 自定义输入框 | 设置页 TextBox + 格式说明 |

**已实现**：
- `PageRangeParser`：解析页范围字符串为页码集合（支持 `1-3,5,7-10`、`all`、空值）
- `BuildSourceDocumentAsync` / `BuildSourceDocumentFromPdf`：ML 和启发式路径均支持页范围过滤
- `LongDocPageRange` 设置：设置页 TextBox + 格式说明
- 本地化：15 语言 × 3 条
- 测试：`PageRangeParserTests`（12 个测试）

---

### G8（P2）：URL 输入支持

| 维度 | PDFMathTranslate | Easydict Win32 |
|------|-----------------|----------------|
| 输入源 | 本地文件 **+** URL（自动下载 PDF） | 仅本地文件（文件选择器） |

**差距说明**：
- 用户常从 arXiv、学术网站直接获取 PDF 链接。
- 建议：在手动输入模式增加 URL 粘贴支持，自动下载后进入翻译流程。

---

### G9（P2）：源字体保留与匹配

| 维度 | PDFMathTranslate | Easydict Win32 |
|------|-----------------|----------------|
| 字体保留 | 通过 `page.insert_font()` 插入字体，引用源 PDF 资源字典 | 对象级替换保留源字体，overlay 使用固定 Arial |
| CID 字体 | 支持 4 位十六进制编码 | 不支持 |
| 源字体匹配 | 通过 PDF xref 关联字体 | 无 |

**差距说明**：
- overlay 模式下字体风格与原文差异较大（学术论文常用 Times/Computer Modern，overlay 用 Arial）。
- 建议：overlay 渲染时尝试从源 PDF 提取字体名并匹配（PdfPig 可获取 `Letter.FontName`），至少做到衬线/无衬线的区分。

---

### G10（P2）：批量目录处理

| 维度 | PDFMathTranslate | Easydict Win32 |
|------|-----------------|----------------|
| 方式 | `--dir` 递归遍历目录下所有 PDF，逐一翻译 | 文件选择器多选 → 队列逐个处理 |
| 自动化 | CLI 可脚本化调用 | GUI 操作为主 |

**差距说明**：
- 当前队列功能已基本覆盖此需求，但缺少 CLI 入口。
- 建议：为长文档翻译增加 CLI 模式（便于自动化脚本集成）。

---

### G11（P3）：PDF/A 兼容模式

| 维度 | PDFMathTranslate | Easydict Win32 |
|------|-----------------|----------------|
| 支持 | `--compatible` 模式预转 PDF/A 格式 | 无 |

**差距说明**：
- 部分 PDF 因加密、权限限制或格式异常导致文本提取失败。
- PDF/A 预转换可提升兼容性。
- 优先级较低，视用户反馈决定是否引入。

---

### G12（P3）：PDF 压缩与优化

| 维度 | PDFMathTranslate | Easydict Win32 |
|------|-----------------|----------------|
| 压缩 | Deflate 压缩 + garbage collection level 3 | 无主动压缩 |
| 文件大小 | 输出文件接近或小于输入 | 输出文件可能显著大于输入（overlay 叠加） |

**差距说明**：
- overlay 模式在每个文本区域叠加白色背景 + 新文本层，累积导致文件膨胀。
- 建议：导出完成后对 PDF 执行压缩（PdfSharp 支持 `PdfDocument.Save` 时压缩选项）。

---

### G13（P2）：自定义 LLM 翻译 Prompt ✅

| 维度 | PDFMathTranslate | Easydict Win32 |
|------|-----------------|----------------|
| 自定义 Prompt | `--prompt` CLI 参数 | `LongDocCustomPrompt` 设置 + 设置页多行 TextBox |
| 作用范围 | LLM 翻译服务 | OpenAI、DeepSeek、Gemini 等 LLM 服务（`BaseOpenAIService` + `GeminiService`） |

**已实现**：
- `TranslationRequest.CustomPrompt`：可选属性，追加到 LLM 系统 prompt
- `BaseOpenAIService.BuildChatMessages`：若 `CustomPrompt` 非空，追加 "Additional instructions"
- `GeminiService`：同样支持 CustomPrompt 注入到 systemInstruction
- `LongDocCustomPrompt` 设置：多行 TextBox + 说明
- 本地化：15 语言 × 3 条

---

### G14（P2）：PDF 书签/目录保留 ✅

| 维度 | PDFMathTranslate | Easydict Win32 |
|------|-----------------|----------------|
| 书签保留 | `doc.get_toc()` / `doc.set_toc()` | 单语 PDF：PdfSharpCore Modify 模式自动保留；双语 PDF：`CopyBookmarksForBilingual` 递归复制 |
| 页码映射 | 双语页码翻倍 | 源页码 N → 双语页码 2N-1（交错页映射） |

**已实现**：
- 单语 PDF：`ExportPdfWithCoordinateBackfill` 使用 `PdfDocumentOpenMode.Modify`，自动保留源 PDF 书签
- 双语 PDF：`CopyBookmarksForBilingual` 递归复制书签树，自动映射页码
- `CopyOutlineLevel` / `FindOutlinePageIndex`：递归遍历 + 页对象引用匹配

---

### G15（P3）：字体子集化

| 维度 | PDFMathTranslate | Easydict Win32 |
|------|-----------------|----------------|
| 字体子集化 | 默认启用（`--skip-subset-fonts` 禁用） | 暂无（全量字体嵌入 ~16MB/语言） |

**差距说明**：
- 全量 CJK 字体嵌入导致输出 PDF 体积较大。
- PdfSharpCore 不原生支持字体子集化，需第三方库或手动实现。
- 优先级较低，视用户反馈决定。

---

### G16（P2）：表格专门处理

| 维度 | PDFMathTranslate | Easydict Win32 |
|------|-----------------|----------------|
| 表格处理 | 识别表格结构，保留格式 | DocLayout-YOLO 检测表格区域，标记为 `TableLike`，但无结构保留 |

**差距说明**：
- 当前表格区域被识别但仍作为普通文本块翻译，可能破坏表格结构。
- 建议：表格块保持原始布局，仅翻译单元格内文本。

---

### 差距汇总矩阵

| 编号 | 差距项 | 优先级 | 难度 | 影响范围 |
|------|--------|--------|------|----------|
| G1 | 双语/对照 PDF 输出 ✅ | P0 | 中 | 输出质量 |
| G2 | ML 布局检测（DocLayout-YOLO） ✅ | P0 | 高 | 布局准确度 |
| G3 | 三级公式检测 ✅ | P1 | 中 | 公式保护 |
| G4 | 并行翻译 ✅ | P1 | 低 | 性能 |
| G5 | CJK/多语言字体嵌入 ✅ | P1 | 中 | 输出可读性（关键） |
| G6 | 持久化翻译缓存 ✅ | P1 | 中 | 断点续传/API 节省 |
| G7 | 页范围选择 ✅ | P2 | 低 | 用户体验 |
| G8 | URL 输入支持 | P2 | 低 | 用户体验 |
| G9 | 源字体保留与匹配 | P2 | 中 | 输出质量 |
| G10 | 批量目录处理 / CLI 入口 | P2 | 低 | 自动化 |
| G11 | PDF/A 兼容模式 | P3 | 低 | 兼容性 |
| G12 | PDF 压缩与优化 | P3 | 低 | 文件大小 |
| G13 | 自定义 LLM 翻译 Prompt ✅ | P2 | 低 | 翻译质量 |
| G14 | PDF 书签/目录保留 ✅ | P2 | 中 | 输出质量 |
| G15 | 字体子集化 | P3 | 中 | 文件大小 |
| G16 | 表格专门处理 | P2 | 中 | 输出质量 |

---

### 已对齐项（无差距或 Easydict 已实现等效能力）

| 能力 | Easydict 实现 | 备注 |
|------|--------------|------|
| 翻译服务插件架构 | 15+ 服务，`BaseTranslationService` 继承体系 | 服务数量与 PDFMathTranslate 相当 |
| LLM 流式翻译 | `IStreamTranslationService` + SSE 解析 | 已支持 |
| 公式占位符保护 | `[[FORMULA_N_HASH]]` + 类型分类 + 恢复校验 | 已对齐核心机制（M4 完成） |
| 布局区域推断 | `LayoutProfile` + 自适应阈值 + 双栏检测 | 启发式已成熟（M2 完成） |
| ML 布局检测 | DocLayout-YOLO ONNX + VisionLLM + 四模式策略 | G2 已完成：ONNX 本地下载优先 + 视觉大模型 + IoU 合并 |
| 术语一致性 | 按页窗口优先 + 全局回退 | 已对齐（M3 完成） |
| 质量报告 | `BackfillQualityMetrics` + page-level 明细 | 已对齐（M1 完成） |
| OCR 回退 | `WindowsOcrService` 集成 | 已支持 |
| Checkpoint/重试 | 内存 checkpoint + 失败块重试 + SQLite 段落缓存 | 核心机制已有 + SQLite 持久缓存（G6 完成） |
| 双语输出 | Monolingual + Bilingual 交错页 + Both | Monolingual / Bilingual / Both 三种模式（G1 完成） |
| 三级公式检测 | Layout + Font + Character | Layout + Font (PdfPig) + Character (Unicode)，用户可配正则（G3 完成） |
| 并行翻译 | 默认 4 线程 | SemaphoreSlim + Task.WhenAll，默认 4 并发（G4 完成） |
| CJK 字体嵌入 | SourceHanSerif + 行高适配 | Noto Sans CJK + IFontResolver + 行高适配（G5 完成） |
| 文件去重 | SHA256 哈希 + dedup index | 已支持 |
| 取消操作 | `CancellationTokenSource` + 任务级取消 | 已支持 |

---

## 风险与备注

- 对象级替换目前仍依赖 PDF 内容格式前提，无法保证所有 PDF 均成功替换。
- 对于非 ASCII / 复杂编码内容，仍会走 overlay 降级路径。
- 建议在 CI 中引入样本 PDF 回归，以防布局/回填逻辑回退。
- 当前 `RetryMergeStrategy` 采用累计策略（accumulate），后续可按产品需求调整为 latest-only。
- ~~G5（CJK 字体嵌入）虽列为 P1，但实际是**阻塞非拉丁语系输出可用性**的关键项，建议优先处理。~~ ✅ 已完成
- ~~G2（ML 布局检测）技术难度最高，需评估 ONNX Runtime .NET 包大小对分发包的影响。~~ ✅ 已解决：NuGet 仅引入托管 API（~1-2MB），原生 DLL（~15MB）+ 模型（~25MB）按需下载至 `%LocalAppData%`。


## 本轮实现说明

- WinUI PDF 布局块区域推断从固定阈值升级为 `LayoutProfile` 自适应模型。
- 针对双栏文档增加中心分布边界判定，减少正文被错误归类为左右栏。
- 新增 WinUI 反射测试覆盖：
  - `TryPatchPdfLiteralToken` 的 `TJ` 多片段替换
  - 自适应 header/footer 与 table-like 判定
  - 双栏边界判定（left/right/body）
- CI 新增长文档测试门禁，确保 longdoc 相关回归在主流程中可见。
- 已细化”中期改进列表”为 M1~M6 执行计划（含优先级、建议实现与验收标准）。

- 本轮按 roadmap 落地 M1：新增 page-level 回填指标（含 object replace / overlay / structured fallback），并覆盖重试合并逻辑测试。
- 本轮按 roadmap 落地 M2：为 RegionType 增加置信度与来源标签，并补充对应反射测试。
- 本轮按 roadmap 落地 M3：术语复用增加按页窗口优先策略（当前页邻近优先，超窗回退全局）。
- 本轮按 roadmap 落地 M4：细化公式 token 类型并加入恢复校验（未恢复 token / 分隔符失衡回退原文）。

### P1 实现（G4 并行翻译 + G5 CJK 字体 + G3 公式检测 + G6 缓存 + G1 双语输出）

- **G1 双语输出**：`IDocumentExportService` 接口 + 3 种导出器（PDF/Markdown/Text），`DocumentOutputMode` 枚举（Monolingual/Bilingual/Both），双语 PDF 交错页实现
- **G4 并行翻译**：`SemaphoreSlim` + `Task.WhenAll` + `ConcurrentDictionary`，设置页 NumberBox（1-16），顺序路径向后兼容
- **G5 CJK 字体**：`FontDownloadService` 按需下载 Noto Sans CJK，`CjkFontResolver` 实现 PdfSharpCore `IFontResolver`，语言级行高适配
- **G3 三级公式检测**：`DetectedFontNames` 从 PdfPig `Letter.FontName` 提取，`IsFontBasedFormula`（>50% 数学字体）+ `IsCharacterBasedFormula`（>30% Unicode 数学字符），用户可配正则
- **G6 持久化缓存**：`TranslationCacheService` 基于 `Microsoft.Data.Sqlite`，段落级 SHA256 缓存键，UPSERT + 命中统计，设置页开关 + 清除按钮
- **本地化**：15 语言 × 17 条（G4: 3 + G5: 5 + G3: 5 + G6: 4）
- **测试**：`ParallelTranslationTests`（7）+ `FontDownloadServiceTests`（7）+ `FormulaDetectionTests`（11）+ `TranslationCacheServiceTests`（8）

### G2 实现（ONNX 本地下载优先 + 视觉大模型布局检测）

- **核心架构**：四模式策略（Auto / OnnxLocal / VisionLLM / Heuristic），Auto 模式优先使用 ONNX 本地模型，不可用时自动回退启发式。
- **模型管理**：`LayoutModelDownloadService` 实现首次使用时下载，HuggingFace → GitHub Releases 双源回退，指数退避重试，进度报告。
- **ONNX 推理**：`DocLayoutYoloService` 实现 DocLayout-YOLO 10 类检测（letterbox 1024×1024 预处理、YOLO 输出解析、NMS 后处理）。
- **原生库加载**：使用 `NativeLibrary.SetDllImportResolver` 将 ONNX Runtime 原生 DLL 从下载目录动态加载，避免打包体积膨胀。
- **视觉大模型**：`VisionLayoutDetectionService` 支持 GPT-4V / Gemini Vision 等，通过结构化 JSON prompt 输出百分比坐标区域。
- **策略编排**：`LayoutDetectionStrategy` 负责 PDF 页面渲染（`Windows.Data.Pdf` WinRT）、ML 检测、IoU 匹配（阈值 0.3）合并 ML 与启发式结果。
- **ML 区域保护**：Figure / Formula / IsolatedFormula 检测结果标记 `IsFormulaLike = true`，跳过翻译。
- **设置 UI**：模式选择 ComboBox、ONNX 模型下载/删除/进度条、Vision LLM 服务选择（条件可见）。
- **NuGet 策略**：仅引入 `Microsoft.ML.OnnxRuntime.Managed`（托管 API），原生 DLL 按需下载至 `%LocalAppData%\Easydict\Models\`。
- **新增文件**：16 files changed, 2195 insertions — 含 6 个新服务文件、3 个测试文件、2 个本地化文件、设置 UI 扩展。

---

## ~~下一 P0 任务：G1 — 双语/对照 PDF 输出~~ ✅ 已完成

> 优先级：P0 | 难度：中 | 影响：输出质量（学术论文核心需求）

### 背景

学术论文翻译场景中，用户常需原文/译文对照阅读。当前仅输出单语 PDF（翻译后文本替换/覆盖），无法满足对照需求。PDFMathTranslate 每次翻译产出两份 PDF：单语 (`*-mono.pdf`) + 双语对照 (`*-dual.pdf`)。

### 目标

1. 新增 `PdfOutputMode` 枚举：`Monolingual`（默认，兼容现有行为）/ `Bilingual`（交错页）/ `Both`（同时输出两种）
2. 双语模式：原文页与译文页交错排列（第 1 页原文 → 第 1 页译文 → 第 2 页原文 → 第 2 页译文 → ...）
3. 输出文件命名：`{name}-translated.pdf`（单语）、`{name}-bilingual.pdf`（双语）
4. 设置页面：增加输出模式选择
5. 保持向后兼容：默认行为不变

### 实现计划

#### Step 1：定义 `PdfOutputMode` 枚举

**文件**: `dotnet/src/Easydict.TranslationService/LongDocument/PdfOutputMode.cs`（新建）

```csharp
public enum PdfOutputMode
{
    Monolingual,   // 仅输出译文 PDF（默认，兼容现有行为）
    Bilingual,     // 交错页：原文页 + 译文页 交替排列
    Both           // 同时输出 Monolingual + Bilingual 两个文件
}
```

#### Step 2：扩展 `LongDocumentTranslationOptions` 和 `LongDocumentTranslationResult`

**文件**: `dotnet/src/Easydict.TranslationService/LongDocument/LongDocumentModels.cs`

- `LongDocumentTranslationOptions` 新增 `PdfOutputMode OutputMode { get; init; } = PdfOutputMode.Monolingual;`

**文件**: `dotnet/src/Easydict.WinUI/Services/LongDocumentTranslationService.cs`

- `LongDocumentTranslationResult` 新增 `string? BilingualOutputPath { get; init; }`（Bilingual/Both 模式时有值）
- `TranslateToPdfAsync` 签名新增 `PdfOutputMode outputMode = PdfOutputMode.Monolingual` 参数

#### Step 3：实现双语 PDF 导出方法

**文件**: `dotnet/src/Easydict.WinUI/Services/LongDocumentTranslationService.cs`

新增 `ExportBilingualPdf` 私有方法：

```
ExportBilingualPdf(checkpoint, sourcePdfPath, bilingualOutputPath) → BackfillRenderingMetrics
```

**核心逻辑**：
1. 使用 `PdfReader.Open(sourcePdfPath, PdfDocumentOpenMode.Import)` 以只读模式打开源 PDF
2. 创建新 `PdfSharpCore.Pdf.PdfDocument`
3. 遍历源 PDF 每一页（pageIndex = 0..N-1）：
   a. **插入原文页**：`newDoc.AddPage(sourceDoc.Pages[pageIndex])` — 直接复制原始页面
   b. **插入译文页**：创建新页面（与原始页面同尺寸），使用现有 overlay 逻辑渲染翻译文本
4. 保存为 `bilingualOutputPath`

**关键设计决策**：
- 采用**交错页**策略（而非左右分栏），因为：
  - 保持原始页面布局完整不变
  - 实现简单——直接复制原始页面，无需重新排版
  - 与 PDFMathTranslate 行为一致
  - 支持任意页面尺寸（A4、Letter、自定义）
- 译文页使用现有的 coordinate backfill 逻辑（对象替换 + overlay 降级），不额外创建新的渲染路径
- 对于非 PDF 输入（Text/URL），Bilingual 模式不生效（无原文页可交错），静默回退 Monolingual

#### Step 4：修改 `FinalizeResult` 支持多输出模式

**文件**: `dotnet/src/Easydict.WinUI/Services/LongDocumentTranslationService.cs`

修改 `FinalizeResult` 方法逻辑：

```
FinalizeResult(checkpoint, outputPath, outputMode, onProgress, qualityReport)
├── outputMode == Monolingual:
│   └── 现有逻辑不变（ExportPdfWithCoordinateBackfill 或 ExportStructuredPdf）
├── outputMode == Bilingual:
│   ├── ExportPdfWithCoordinateBackfill → monoOutputPath（仍需生成用于 backfill metrics）
│   └── ExportBilingualPdf → bilingualOutputPath
│   └── result.OutputPath = bilingualOutputPath
├── outputMode == Both:
│   ├── ExportPdfWithCoordinateBackfill → monoOutputPath
│   └── ExportBilingualPdf → bilingualOutputPath
│   └── result.OutputPath = monoOutputPath, result.BilingualOutputPath = bilingualOutputPath
```

输出路径推导规则：
- `outputPath` 保持原有逻辑（`{name}-translated-{timestamp}.pdf`）
- `bilingualOutputPath` = `outputPath.Replace("-translated-", "-bilingual-")`

#### Step 5：UI 设置集成

**文件**: `dotnet/src/Easydict.WinUI/Services/SettingsService.cs`
- 新增 `PdfOutputMode` 设置项（string，默认 `"Monolingual"`）

**文件**: `dotnet/src/Easydict.WinUI/Views/SettingsPage.xaml` + `.cs`
- 在"Layout Detection"分组附近增加"PDF Output Mode"ComboBox
- 选项：`Monolingual (Translation Only)` / `Bilingual (Interleaved Pages)` / `Both`

**文件**: `dotnet/src/Easydict.WinUI/Views/MainPage.xaml.cs`
- 读取设置中的 `PdfOutputMode`，传递给 `TranslateToPdfAsync`
- `Both` 模式完成后，进度提示显示两个输出路径

**文件**: `dotnet/src/Easydict.WinUI/Strings/en-US/Resources.resw` + `zh-CN/Resources.resw`
- 新增 6 条本地化字符串：
  - `SettingsLayoutPdfOutputMode` / `SettingsLayoutPdfOutputModeDesc`
  - `PdfOutputModeMonolingual` / `PdfOutputModeBilingual` / `PdfOutputModeBoth`
  - `LongDocBilingualOutputReady`

#### Step 6：测试

**文件**: `dotnet/tests/Easydict.WinUI.Tests/Services/` 新增测试

1. **BilingualPdfExportTests**：
   - `ExportBilingualPdf_InterleavesOriginalAndTranslatedPages` — 验证页面数 = 原始页数 × 2
   - `ExportBilingualPdf_OriginalPagesPreserved` — 验证奇数页（原文）内容未被修改
   - `ExportBilingualPdf_TranslatedPagesHaveContent` — 验证偶数页（译文）有渲染内容

2. **FinalizeResult 扩展测试**：
   - `FinalizeResult_MonolingualMode_SingleOutput` — 默认模式验证
   - `FinalizeResult_BilingualMode_ProducesBilingualOutput` — Bilingual 模式输出路径验证
   - `FinalizeResult_BothMode_ProducesTwoOutputs` — Both 模式双路径验证
   - `FinalizeResult_NonPdfInput_BilingualFallsBackToMono` — 非 PDF 输入回退验证

3. **OutputPath 命名测试**：
   - 验证 `-translated-` → `-bilingual-` 替换规则

### 验收标准

1. 默认行为（Monolingual）完全不变 — 向后兼容
2. Bilingual 模式输出交错页 PDF：第 2k-1 页为原文，第 2k 页为译文（k = 1, 2, ...）
3. Both 模式同时输出两个文件，UI 显示两个路径
4. 非 PDF 输入（Text/URL）在 Bilingual/Both 模式下静默回退 Monolingual
5. 设置页面可选择输出模式，保存后重启生效
6. 所有新增逻辑有测试覆盖

### 依赖分析

- **无新 NuGet 依赖**：PdfSharpCore 已有，`PdfReader.Open(PdfDocumentOpenMode.Import)` + `AddPage()` 可直接复制页面
- **无破坏性变更**：所有新参数有默认值，`LongDocumentTranslationResult.BilingualOutputPath` 为可空
- **工作量评估**：~400-600 行新增代码（含测试）
