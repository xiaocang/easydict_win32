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

1. **对象级回填可观测性**
   - 新增回填模式分布（object replace / overlay / fallback）页面级报告。

2. **结构化布局能力**
   - 为 `RegionType` 增加置信度和来源标签（heuristic / parser / fallback）。

3. **术语一致性与上下文增强**
   - 在长文档重试中引入更明确的术语记忆窗口（按页/按章节）。

4. **公式恢复精度提升**
   - 细化公式 token 类型（行内公式、显示公式、单位片段）并增加恢复校验。

5. **E2E 基线资产扩展**
   - 添加多栏论文、表格密集页、扫描件、公式密集页等标准样本及快照对比脚本。

6. **UI 展示改进**
   - 在 WinUI 显示 `BackfillMetrics` 摘要与告警（missing bbox、truncated、overlay 占比）。

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
