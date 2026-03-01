# Long Text Translation: Cross-Pollination Analysis

**Date:** 2025-02-24
**Versions:** Easydict Win32 (0.5.x), PDFMathTranslate (1.x)

This document analyzes potential cross-pollination opportunities between Easydict Win32 (Windows desktop app) and PDFMathTranslate (Python CLI tool) for long text/document translation implementations.

---

## Executive Summary

Both projects have mature, production-ready document translation implementations with different architectural philosophies:

| Aspect | Easydict Win32 | PDFMathTranslate |
|--------|----------------|------------------|
| **Chunking Strategy** | Layout-aware semantic blocks | Paragraph-based |
| **Formula Protection** | Hash-based placeholders `[[FORMULA_{id}_{hash}]]` | Numeric placeholders `{vN}` |
| **Layout Detection** | Multi-strategy (Heuristic/ONNX/LLM) | ONNX-first |
| **Parallelization** | SemaphoreSlim with configurable concurrency | ThreadPoolExecutor |
| **Page Range** | C# parser with span-based parsing | Python simple split |
| **Progress** | Callback + checkpoint system | tqdm + callbacks |
| **Caching** | SQLite + deduplication layers | SQLite cache |

---

## Opportunities for Easydict Win32

### 1. Paragraph-Based Chunking (Alternative Mode)

**Current Implementation:**
- Uses PdfPig library for PDF text extraction
- Groups words → lines → paragraphs based on spatial thresholds
- Creates layout-aware blocks with metadata (headers, footers, columns)
- More complex but preserves document structure

**Proposed Enhancement:**
Add a simpler paragraph-based chunking mode as an alternative:

```csharp
public enum ChunkingStrategy
{
    LayoutAware,    // Current default
    ParagraphBased  // New: simpler approach
}

public sealed record LongDocumentTranslationOptions
{
    public ChunkingStrategy ChunkingStrategy { get; init; } = ChunkingStrategy.LayoutAware;
}
```

**Benefits:**
- Simpler code path for basic text-only documents
- Faster processing (no layout inference overhead)
- Better for documents without complex formatting
- Reduces chunk fragmentation

**Trade-offs:**
- Loses layout awareness (headers, footers, columns)
- No spatial metadata preservation
- May produce larger chunks (harder to parallelize)

**Implementation Complexity:** **Medium**

**Files to Modify:**
- `dotnet/src/Easydict.WinUI/Services/LongDocumentTranslationService.cs` (add chunking strategy enum)
- `dotnet/src/Easydict.TranslationService/LongDocument/LongDocumentTranslationService.cs` (add paragraph-based extraction)

**Code Sketch:**
```csharp
private static IEnumerable<SourceDocumentBlock> ExtractParagraphsFromPage(PdfPigPage page)
{
    var text = page.Text;
    var paragraphs = text.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);

    for (var i = 0; i < paragraphs.Length; i++)
    {
        yield return new SourceDocumentBlock
        {
            BlockId = $"p{page.Number}-para{i + 1}",
            BlockType = SourceBlockType.Paragraph,
            Text = paragraphs[i].Trim()
        };
    }
}
```

---

### 2. Numeric Formula Placeholders (`{vN}`)

**Current Implementation:**
```csharp
// Hash-based placeholder system
var token = $"[[FORMULA_{counter}_{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(match.Value)))[..8]}]]";
```

**Proposed Enhancement:**
Adopt simpler numeric placeholders:

```csharp
private static FormulaProtectionResult ProtectFormulaSpansSimple(string text)
{
    var map = new Dictionary<string, FormulaToken>();
    var counter = 0;
    var protectedText = FormulaRegex.Replace(text, match =>
    {
        var token = $"{{v{counter}}}";  // Simpler: {v0}, {v1}, {v2}...
        map[token] = new FormulaToken(match.Value, ClassifyFormulaToken(match.Value));
        counter++;
        return token;
    });

    return new FormulaProtectionResult(protectedText, map);
}

private static string RestoreFormulaSpansSimple(string text, FormulaProtectionResult protection)
{
    var restored = Regex.Replace(text, @"\{v(\d+)\}", match =>
    {
        var index = int.Parse(match.Groups[1].Value);
        var token = $"{{v{index}}}";
        return protection.TokenMap.TryGetValue(token, out var formula) ? formula.RawText : match.Value;
    });

    return restored;
}
```

**Benefits:**
- Simpler, more readable placeholders
- Easier debugging (shorter, predictable format)
- Compatible with PDFMathTranslate format (interoperability)
- No SHA256 hashing overhead

**Trade-offs:**
- Slightly higher collision risk (different formulas with same index if text is reordered)
- Less robust against malicious tampering

**Implementation Complexity:** **Low**

**Files to Modify:**
- `dotnet/src/Easydict.TranslationService/LongDocument/LongDocumentTranslationService.cs` (replace ProtectFormulaSpans/RestoreFormulaSpans)

---

### 3. Page Range Parser Enhancements

**Current Implementation (Easydict Win32):**
```csharp
// Already robust with span-based parsing
public static HashSet<int>? Parse(string? pageRange, int totalPages)
{
    // Supports: "1-3,5,7-10", "all"
}
```

**Current Implementation (PDFMathTranslate):**
```python
for p in parsed_args.pages.split(","):
    if "-" in p:
        start, end = p.split("-")
        pages.extend(range(int(start) - 1, int(end)))
    else:
        pages.append(int(p) - 1)
```

**Analysis:**
Easydict Win32 already has a superior implementation with:
- Span-based parsing (better performance)
- Proper bounds checking
- 1-based page numbers (more user-friendly)
- "all" keyword support
- Empty/null handling

**Conclusion:** No changes needed. Easydict Win32's implementation is already better.

---

## Opportunities for PDFMathTranslate

### 1. Multi-Strategy Layout Detection

**Current Implementation:**
- ONNX-first approach
- No fallback to heuristic if ONNX unavailable
- Hard failure if model not downloaded

**Proposed Enhancement:**
Add heuristic fallback strategy:

```python
class LayoutDetectionStrategy(Enum):
    AUTO = "auto"        # Try ONNX, fallback to heuristic
    ONNX = "onnx"        # ONNX only, fail if unavailable
    HEURISTIC = "heuristic"  # Heuristic only

def detect_layout(page, strategy: LayoutDetectionStrategy):
    if strategy == LayoutDetectionStrategy.HEURISTIC:
        return extract_heuristic_blocks(page)

    if strategy in (LayoutDetectionStrategy.AUTO, LayoutDetectionStrategy.ONNX):
        try:
            ml_result = run_onnx_detection(page)
            if ml_result or strategy == LayoutDetectionStrategy.ONNX:
                return ml_result
        except Exception as e:
            if strategy == LayoutDetectionStrategy.ONNX:
                raise
            logger.warning(f"ONNX detection failed: {e}, falling back to heuristic")

    return extract_heuristic_blocks(page)
```

**Benefits:**
- Graceful degradation when ONNX unavailable
- Faster processing for simple documents
- Better user experience (no hard failures)
- Compatible with offline usage

**Trade-offs:**
- Additional code complexity
- Heuristic may misclassify complex layouts

**Implementation Complexity:** **Medium**

**Files to Modify:**
- `pdf2zh/doclayout.py` (add heuristic fallback)
- `pdf2zh/converter.py` (use new strategy)
- `pdf2zh/pdf2zh.py` (add CLI argument)

---

### 2. Checkpoint/Resume System

**Current Implementation:**
- No checkpoint/resume capability
- Failed translations require full re-run
- No progress persistence

**Proposed Enhancement:**
Implement checkpoint system:

```python
@dataclass
class TranslationCheckpoint:
    document_id: str
    total_pages: int
    completed_pages: List[int]
    failed_page_indexes: Dict[int, str]
    timestamp: float

    def save(self, path: str):
        with open(path, 'w') as f:
            json.dump(asdict(self), f)

    @classmethod
    def load(cls, path: str) -> 'TranslationCheckpoint':
        with open(path) as f:
            return cls(**json.load(f))

def translate_with_checkpoint(doc_path: str, checkpoint_path: str, **kwargs):
    # Try to load existing checkpoint
    checkpoint = None
    if os.path.exists(checkpoint_path):
        checkpoint = TranslationCheckpoint.load(checkpoint_path)
        print(f"Resuming from checkpoint: {len(checkpoint.completed_pages)} pages completed")

    # Translate only missing pages
    for page_num in range(total_pages):
        if checkpoint and page_num in checkpoint.completed_pages:
            continue

        try:
            result = translate_page(doc_path, page_num, **kwargs)
            checkpoint.completed_pages.append(page_num)
            checkpoint.save(checkpoint_path)
        except Exception as e:
            checkpoint.failed_page_indexes[page_num] = str(e)
            checkpoint.save(checkpoint_path)
            raise
```

**Benefits:**
- Resume from interruptions (network failure, user cancel)
- Skip already-translated pages
- Better UX for large documents (100+ pages)
- Progress tracking across sessions

**Trade-offs:**
- Disk I/O overhead for checkpoint writes
- Additional code complexity
- Stale checkpoint handling (what if document changes?)

**Implementation Complexity:** **High**

**Files to Modify:**
- `pdf2zh/high_level.py` (add checkpoint orchestration)
- `pdf2zh/converter.py` (add per-page result saving)
- `pdf2zh/pdf2zh.py` (add CLI argument for checkpoint path)

---

### 3. Block-Level Metadata

**Current Implementation:**
```python
class Paragraph:
    def __init__(self, y, x, x0, x1, y0, y1, size, brk):
        # Only spatial and font metadata
```

**Proposed Enhancement:**
Add rich metadata for better reconstruction:

```python
@dataclass
class BlockMetadata:
    block_id: str                    # Unique identifier
    page_number: int
    block_type: BlockType            # Heading, Paragraph, Table, Formula
    bounding_box: Tuple[float, float, float, float]
    font_names: List[str]
    is_formula_like: bool
    parent_block_id: Optional[str] = None
    source_hash: str = ""

class BlockType(Enum):
    PARAGRAPH = "paragraph"
    HEADING = "heading"
    CAPTION = "caption"
    TABLE = "table"
    FORMULA = "formula"

def translate_with_metadata(blocks: List[BlockMetadata], **kwargs):
    for block in blocks:
        # Use metadata for translation decisions
        if block.block_type == BlockType.FORMULA:
            continue  # Skip formula blocks

        if block.is_formula_like:
            # Apply formula protection
            text = protect_formulas(block.text)

        result = translate(text, **kwargs)

        # Store with metadata for reconstruction
        yield TranslatedBlock(
            block_id=block.block_id,
            metadata=block,
            translated_text=result
        )
```

**Benefits:**
- Better quality reporting (success/failed/skipped counts)
- Improved reconstruction accuracy
- Enables block-level deduplication
- Better error messages (which specific block failed)

**Trade-offs:**
- More memory usage
- Additional extraction overhead

**Implementation Complexity:** **Medium**

**Files to Modify:**
- `pdf2zh/converter.py` (add BlockMetadata class)
- `pdf2zh/translator.py` (use metadata in translation)

---

### 4. Configurable Concurrency

**Current Implementation:**
```python
with concurrent.futures.ThreadPoolExecutor(max_workers=self.thread) as executor:
    news = list(executor.map(worker, sstk))
```

**Analysis:**
PDFMathTranslate already has configurable thread count via `--thread` argument. This is already equivalent to Easydict Win32's `MaxConcurrency` option.

**Conclusion:** No changes needed. Both implementations are equivalent.

---

## Summary Table

| Opportunity | Target | Complexity | Priority | Impact |
|-------------|--------|------------|----------|--------|
| Paragraph-based chunking | Easydict Win32 | Medium | Low | Medium |
| Numeric formula placeholders | Easydict Win32 | Low | Medium | High |
| Page range parser | Easydict Win32 | N/A | N/A | N/A (already better) |
| Multi-strategy layout detection | PDFMathTranslate | Medium | High | High |
| Checkpoint/resume system | PDFMathTranslate | High | High | Very High |
| Block-level metadata | PDFMathTranslate | Medium | Medium | Medium |
| Configurable concurrency | PDFMathTranslate | N/A | N/A | N/A (already equivalent) |

---

## Implementation Recommendations

### High Priority (Do First)

1. **Numeric Formula Placeholders for Easydict Win32** (Low complexity, High impact)
   - Simpler code
   - Better debugging
   - Format compatibility

2. **Multi-Strategy Layout Detection for PDFMathTranslate** (Medium complexity, High impact)
   - Improves reliability
   - Enables offline usage
   - Better UX

3. **Checkpoint/Resume System for PDFMathTranslate** (High complexity, Very High impact)
   - Critical for large documents
   - Resume from failures
   - Better UX

### Medium Priority

4. **Paragraph-Based Chunking for Easydict Win32** (Medium complexity, Medium impact)
   - Alternative mode for simple documents
   - Faster processing
   - User choice

5. **Block-Level Metadata for PDFMathTranslate** (Medium complexity, Medium impact)
   - Better quality reporting
   - Improved reconstruction

### Low Priority

6. **Page Range Parser** - Easydict Win32 already has superior implementation

---

## Testing Strategy

For each enhancement, implement the following tests:

1. **Unit Tests:**
   - Placeholder replacement (protection/restoration)
   - Page range parsing edge cases
   - Metadata extraction accuracy

2. **Integration Tests:**
   - End-to-end translation with checkpoint resume
   - Layout detection fallback behavior
   - Parallel translation with different concurrency levels

3. **Performance Tests:**
   - Chunking strategy comparison (layout-aware vs paragraph-based)
   - Placeholder replacement overhead
   - Checkpoint save/load performance

---

## Conclusion

Both projects have strong foundations with complementary strengths. The recommended cross-pollination focuses on:

1. **Simplification:** Numeric placeholders reduce complexity while maintaining safety
2. **Reliability:** Multi-strategy layout detection with fallback improves robustness
3. **User Experience:** Checkpoint/resume system enables handling large documents
4. **Flexibility:** Alternative chunking strategies give users choice

The highest-impact, lowest-complexity change is adopting numeric formula placeholders in Easydict Win32. The most valuable (but complex) enhancement is adding checkpoint/resume to PDFMathTranslate.
