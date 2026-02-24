# Long Document Translation

> This document contains detailed architecture and implementation information for the Long Document Translation system. For general project information, see [CLAUDE.md](CLAUDE.md).

## Overview

The Long Document Translation system is a sophisticated document processing pipeline that handles PDF, plain text, and Markdown documents with advanced features including layout-aware chunking, ML-based layout detection, formula protection, parallel translation, and bilingual export capabilities. The system rivals desktop PDF translation tools like PDFMathTranslate with production-ready quality and comprehensive feature coverage.

## Service Architecture

```
LongDocumentTranslationService            # Core orchestration (Easydict.TranslationService)
├── CoreLongDocumentTranslationService   # Core library implementation
├── DocumentIr                            # Intermediate representation with blocks
├── SourceDocument                        # Input document abstraction
└── TranslatedDocumentPage                # Output page model

WinUI LongDocumentTranslationService      # UI layer integration
├── PDF processing (PdfPig)
├── OCR fallback (WindowsOcrService)
└── Export coordination

Layout Detection Services                 # ML-based layout analysis
├── LayoutDetectionStrategy               # Strategy pattern: Heuristic/OnnxLocal/VisionLLM/Auto
├── DocLayoutYoloService                  # ONNX Runtime inference
├── VisionLayoutDetectionService          # GPT-4V/Gemini Vision integration
└── LayoutModelDownloadService            # Model download and caching

Formula Protection System                 # Three-level detection
├── Block-level detection (SourceBlockType.Formula)
├── Font-based detection (>50% math fonts threshold)
└── Character-based detection (>30% Unicode math symbols)

Document Export Pipeline                  # Output generation
├── IDocumentExportService                # Pluggable export interface
├── PdfExportService                      # PDF with coordinate backfill
├── MarkdownExportService                 # Markdown with structure preservation
└── PlainTextExportService                # Plain text export

Translation Cache System                  # SQLite-based persistence
└── TranslationCacheService               # SHA256 deduplication

CJK Font Support                          # Font management
├── FontDownloadService                   # Noto Sans CJK download
└── CjkFontResolver                       # PdfSharpCore font resolver
```

## Core Components

**LongDocumentTranslationService** (`Services/LongDocumentTranslationService.cs`)
- WinUI layer service that orchestrates document translation workflow
- Handles PDF, plain text, and Markdown input formats
- Integrates with `CoreLongDocumentTranslationService` for core logic
- Manages export coordination and UI progress reporting

**CoreLongDocumentTranslationService** (`Easydict.TranslationService/LongDocument/`)
- Core library implementation independent of WinUI
- Contains `LongDocumentModels.cs` with data models:
  - `SourceDocument`: Input document abstraction (PDF/Text/Markdown)
  - `DocumentIr`: Intermediate representation with blocks and metadata
  - `TranslatedDocumentPage`: Output page with translated content
  - `BackfillQualityMetrics`: Quality reporting with page-level details
- Pipeline stages: Ingest → Build IR → Formula Protection → Translate → Structured Output

## ML Layout Detection System

**LayoutDetectionStrategy** (`Services/LayoutDetectionStrategy.cs`)

Four detection modes:
- **Heuristic**: Line spacing analysis, quartile-based block detection (no ML required)
- **OnnxLocal**: DocLayout-YOLO model inference for high accuracy
- **VisionLLM**: GPT-4V, Gemini 2.0 Flash, and other vision models
- **Auto**: Prefers ONNX, falls back to heuristic if unavailable

**DocLayoutYoloService** (`Services/DocLayoutYoloService.cs`)

- Runs DocLayout-YOLO ONNX model for PDF page layout detection
- Supports 10 layout types: Title, PlainText, Figure, Table, Caption, IsolatedFormula, EmbeddedFormula, List, Header, Footer
- Implements Non-Maximum Suppression (NMS) for overlapping detections
- Native library resolver for `onnxruntime.dll`
- Stores models in `%LocalAppData%\Easydict\Models\`

**VisionLayoutDetectionService** (`Services/VisionLayoutDetectionService.cs`)

- Uses vision LLMs for layout detection as an alternative to ONNX
- Converts PDF pages to images for vision analysis
- Parses OpenAI-compatible structured responses
- Supports multiple vision models (GPT-4o, Gemini 2.0 Flash, etc.)

**LayoutModelDownloadService** (`Services/LayoutModelDownloadService.cs`)

- Downloads and caches ONNX runtime and DocLayout-YOLO model
- Progress reporting with retries and fallback URLs
- Primary: GitHub releases, Fallback: Hugging Face mirrors

## Formula Protection System

Three-tier detection hierarchy:

**Level 1 - Layout Detection**: Blocks explicitly marked as `SourceBlockType.Formula` by ML layout detection

**Level 2 - Font-Based Detection**: Blocks where >50% of characters use math fonts (CM, CMSY, MS.M, Symbol, etc.)

**Level 3 - Character-Based Detection**: Blocks where >30% of characters are Unicode math symbols (≠, ∑, ∫, √, etc.)

**Key Features**:
- **Numeric Placeholders**: `{v0}`, `{v1}`, `{v2}` format replaces formulas during translation
- **Balanced Validation**: Checks for matching delimiters before replacement
- **Fallback Mechanism**: Restores original text if translation corrupts formula placeholders
- **Mixed Content Handling**: Preserves formulas within translated text (e.g., "where {v0} is the value")

```csharp
// Formula detection regex examples
private static readonly Regex FormulaRegex = new(
    @"(\$[^$]+\$|\\\([^\)]+\\\)|\\\[[^\]]+\\\]|[A-Za-z]\s*=\s*[^\s]+)");

// Numeric placeholder format
private static readonly Regex NumericPlaceholderRegex = new(
    @"\{v(\d+)\}", RegexOptions.Compiled);
```

**Settings**:
- `EnableFormulaProtection` (default: true)
- `FormulaFontPattern` (default: "CM|CMSY|MS-M|Symbol|Latin-Modern-Math|XITS|Asana-Math")
- `FormulaCharPattern` (default: Unicode math symbols range)

## Document Export Pipeline

**IDocumentExportService** (`Services/DocumentExport/IDocumentExportService.cs`)

Export modes (via `DocumentOutputMode` enum):
- **Monolingual**: Translated-only output
- **Bilingual**: Original + translated interleaved (PDF: side-by-side pages, MD/TXT: block-level)
- **Both**: Generate both monolingual and bilingual outputs

**PdfExportService** (`Services/DocumentExport/PdfExportService.cs`)

- **Coordinate Backfill**: Overlays translations at original text positions using bounding boxes
- **Structured Export**: New page-based layout for better formatting control
- **Bilingual PDF**: Interleaved pages (original → translated → original → translated)
- **CJK Font Support**: Noto Sans fonts with automatic fallback (SC/TC/JP/KR)
- **Bookmark Preservation**: Maintains PDF navigation structure from original
- **Font Fitting Algorithm**: Reduces font size to prevent truncation
- **CJK Line Height**: 1.4x for Chinese/Japanese, 1.3x for Korean

Rendering strategies:
1. **Object Replacement**: Direct content stream modification for ASCII text
2. **Overlay**: Draw over existing content when object replacement fails
3. **Structured Fallback**: New page creation when both above fail

**PlainTextExportService** & **MarkdownExportService**

- Preserve document structure with block-level output
- Bilingual format shows original and translated blocks
- Markdown heading and list preservation
- Clean text formatting without layout artifacts

## Translation Cache System

**TranslationCacheService** (`Services/TranslationCacheService.cs`)

- **SQLite-based**: Persistent storage with proper indexing on `(SourceTextHash, ServiceId, FromLang, ToLang)`
- **SHA256 Deduplication**: Cache keys based on source text hash
- **Service/Language Specific**: Separate caching per translation service and language pair
- **Hit Tracking**: Records last access timestamp for popular translations
- **Atomic Operations**: Thread-safe concurrent access with WAL mode
- **Cache Size Tracking**: Monitors database size and provides statistics

Settings:
- `EnableTranslationCache` (default: true)
- `ClearTranslationCache` action for manual cache clearing

## CJK Font Support

**FontDownloadService** (`Services/FontDownloadService.cs`)

- Downloads Google Noto Sans CJK fonts (SC, TC, JP, KR)
- GitHub mirror fallback URLs for reliability
- Retry logic with progress reporting
- Stores fonts in `%LocalAppData%\Easydict\Fonts\`

**CjkFontResolver** (`Services/DocumentExport/CjkFontResolver.cs`)

- Custom `IPdfSharpFontResolver` implementation for PdfSharpCore
- Loads fonts from disk into PDF rendering pipeline
- Falls back to system fonts for non-CJK text
- Thread-safe registration with `GlobalFontSettings.FontResolver`

## Parallel Translation

Configurable concurrency implementation:

```csharp
var concurrency = Math.Max(1, Math.Min(options.MaxConcurrency, 16));
if (concurrency == 1)
{
    // Sequential path for backward compatibility
    foreach (var block in blocksToTranslate)
    {
        var translated = await TranslateSingleBlockAsync(block, options, cancellationToken);
    }
}
else
{
    // Parallel path with semaphore throttling
    using var semaphore = new SemaphoreSlim(concurrency, concurrency);
    var tasks = blocksToTranslate.Select(async block =>
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            return await TranslateSingleBlockAsync(block, options, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    });
    var results = await Task.WhenAll(tasks);
}
```

Settings:
- `MaxConcurrency` (default: 4, range: 1-16)

## Additional Features

**Page Range Selection**: User-configurable page ranges (e.g., "1-5, 8, 10-12")
- Setting: `LongDocPageRange` (default: empty = all pages)

**Custom LLM Prompts**: User-defined translation instructions
- Setting: `LongDocCustomPrompt` (default: empty = use system prompt)

**Retry Logic**: Per-block retry with configurable max attempts
- Setting: `MaxRetriesPerBlock` (default: 3)

**Quality Metrics**: Comprehensive reporting including:
- Stage timings (Ingest, Build-IR, Formula Protection, Translate, Structured Output)
- Block counts (Total, Translated, Skipped, Failed)
- Failed block info (page number, retry count, error details)
- Rendering metrics (candidate blocks, rendered blocks, missing bounding boxes, truncated blocks)

## Settings Reference

All long document settings in `SettingsService`:

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `LayoutDetection` | enum | `Auto` | Detection mode: Heuristic/OnnxLocal/VisionLLM/Auto |
| `EnableFormulaProtection` | bool | `true` | Enable three-tier formula detection |
| `FormulaFontPattern` | string | "CM\|CMSY\|..." | Regex for math font detection |
| `FormulaCharPattern` | string | Unicode math symbols | Regex for math symbol detection |
| `MaxConcurrency` | int | `4` | Parallel translation threads (1-16) |
| `MaxRetriesPerBlock` | int | `3` | Retry attempts for failed blocks |
| `LongDocPageRange` | string | empty | Page range to translate (e.g., "1-5, 8") |
| `LongDocCustomPrompt` | string | empty | Custom translation prompt |
| `DocumentOutputMode` | enum | `Monolingual` | Output mode: Monolingual/Bilingual/Both |
| `EnableTranslationCache` | bool | `true` | Enable persistent translation cache |
| `EnableOcrFallback` | bool | `true` | OCR for scanned PDFs |

## Testing

Test coverage in `tests/Easydict.TranslationService.Tests/LongDocument/`:

- **LongDocumentTranslationServiceTests.cs**: Core functionality (21 tests)
- **FormulaDetectionTests.cs**: Formula protection and restoration logic
- **ParallelTranslationTests.cs**: Parallel execution with various concurrency levels
- **LongDocumentE2EBaselineTests.cs**: End-to-end integration tests
- **DocumentExportServiceTests.cs**: Export functionality for PDF/TXT/MD
- **DocLayoutYoloServiceTests.cs**: ML model detection accuracy
- **LayoutModelDownloadServiceTests.cs**: Model downloading and caching

```bash
# Run long document tests
dotnet test tests/Easydict.TranslationService.Tests/LongDocument/

# Run specific test class
dotnet test --filter "FullyQualifiedName~FormulaDetectionTests"
```

## Flow Summary

1. **Input**: User provides PDF/Text/Markdown file
2. **Ingestion**: Document parsed, OCR fallback if needed for scanned PDFs
3. **Layout Detection**: ML-based (ONNX/Vision) or heuristic block detection
4. **Formula Protection**: Three-tier detection with numeric placeholder replacement
5. **Parallel Translation**: Blocks translated concurrently with semaphore throttling
6. **Formula Restoration**: Placeholders replaced with original formulas
7. **Export**: PDF/Markdown/Text output in monolingual or bilingual format
8. **Quality Report**: Metrics displayed to user (timings, success rates, rendering issues)
