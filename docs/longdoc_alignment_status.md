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

### M4（优先级 P1）：公式恢复精度提升
- **改动目标**：细化公式 token 类型并增加恢复校验。
- **建议实现**：
  - token 分类：行内公式、显示公式、单位片段。
  - 恢复后做结构校验（括号平衡、分隔符成对、关键符号保留）。
- **验收标准**：
  - 公式破坏率下降；纯公式块保持跳译行为不回退。

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

## 风险与备注

- 对象级替换目前仍依赖 PDF 内容格式前提，无法保证所有 PDF 均成功替换。
- 对于非 ASCII / 复杂编码内容，仍会走 overlay 降级路径。
- 建议在 CI 中引入样本 PDF 回归，以防布局/回填逻辑回退。
- 当前 `RetryMergeStrategy` 采用累计策略（accumulate），后续可按产品需求调整为 latest-only。


## 本轮实现说明

- WinUI PDF 布局块区域推断从固定阈值升级为 `LayoutProfile` 自适应模型。
- 针对双栏文档增加中心分布边界判定，减少正文被错误归类为左右栏。
- 新增 WinUI 反射测试覆盖：
  - `TryPatchPdfLiteralToken` 的 `TJ` 多片段替换
  - 自适应 header/footer 与 table-like 判定
  - 双栏边界判定（left/right/body）
- CI 新增长文档测试门禁，确保 longdoc 相关回归在主流程中可见。
- 已细化“中期改进列表”为 M1~M6 执行计划（含优先级、建议实现与验收标准）。

- 本轮按 roadmap 落地 M1：新增 page-level 回填指标（含 object replace / overlay / structured fallback），并覆盖重试合并逻辑测试。
- 本轮按 roadmap 落地 M2：为 RegionType 增加置信度与来源标签，并补充对应反射测试。
- 本轮按 roadmap 落地 M3：术语复用增加按页窗口优先策略（当前页邻近优先，超窗回退全局）。
