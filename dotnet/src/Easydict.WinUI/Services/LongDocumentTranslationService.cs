using System.Text;
using System.Text.RegularExpressions;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services.DocumentExport;
using UglyToad.PdfPig;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;
using UglyToad.PdfPig.Content;
using PdfPigPage = UglyToad.PdfPig.Content.Page;
using CoreLongDocumentTranslationService = Easydict.TranslationService.LongDocument.LongDocumentTranslationService;

namespace Easydict.WinUI.Services;

public enum LongDocumentInputMode
{
    PlainText,
    Markdown,
    Pdf
}

public enum LongDocumentJobState
{
    Completed,
    PartialSuccess,
    Failed
}

public enum LayoutRegionType
{
    Unknown,
    Header,
    Footer,
    Body,
    LeftColumn,
    RightColumn,
    TableLike,
    // ML-detected types (DocLayout-YOLO)
    Figure,
    Table,
    Formula,
    Caption,
    Title,
    IsolatedFormula
}


public enum LayoutRegionSource
{
    Unknown,
    Heuristic,
    BlockIdFallback,
    OnnxModel,
    VisionLLM
}

public sealed class LongDocumentTranslationCheckpoint
{
    public required LongDocumentInputMode InputMode { get; init; }
    public string? SourceFilePath { get; init; }
    public Language? TargetLanguage { get; init; }
    public required List<string> SourceChunks { get; init; }
    public required List<LongDocumentChunkMetadata> ChunkMetadata { get; init; }
    public required Dictionary<int, string> TranslatedChunks { get; init; }
    public required HashSet<int> FailedChunkIndexes { get; init; }
}

public sealed class LongDocumentChunkMetadata
{
    public required int ChunkIndex { get; init; }
    public required int PageNumber { get; init; }
    public required string SourceBlockId { get; init; }
    public required SourceBlockType SourceBlockType { get; init; }
    public bool IsFormulaLike { get; init; }
    public required int OrderInPage { get; init; }
    public required LayoutRegionType RegionType { get; init; }
    public double RegionConfidence { get; init; }
    public LayoutRegionSource RegionSource { get; init; }
    public double ReadingOrderScore { get; init; }
    public BlockRect? BoundingBox { get; init; }
}

public sealed class LongDocumentTranslationResult
{
    public required LongDocumentJobState State { get; init; }
    public required string OutputPath { get; init; }
    public string? BilingualOutputPath { get; init; }
    public required int TotalChunks { get; init; }
    public required int SucceededChunks { get; init; }
    public required IReadOnlyList<int> FailedChunkIndexes { get; init; }
    public required LongDocumentQualityReport QualityReport { get; init; }
    public required LongDocumentTranslationCheckpoint Checkpoint { get; init; }
}

public sealed class LongDocumentTranslationService
{
    private sealed record RetryExecutionSummary(LongDocumentQualityReport? CoreQualityReport, int ReusedByCanonicalCount);

    private sealed record CanonicalTranslationEntry(int ChunkIndex, int PageNumber, string Translation);

    private readonly CoreLongDocumentTranslationService _coreLongDocumentService = new();
    private readonly TranslationCacheService _cacheService = new();

    // Layout detection services (lazy-initialized)
    private LayoutModelDownloadService? _layoutModelDownloadService;
    private DocLayoutYoloService? _docLayoutYoloService;
    private VisionLayoutDetectionService? _visionLayoutDetectionService;
    private LayoutDetectionStrategy? _layoutDetectionStrategy;

    /// <summary>
    /// Gets or creates the layout detection strategy instance.
    /// </summary>
    private LayoutDetectionStrategy GetLayoutDetectionStrategy()
    {
        if (_layoutDetectionStrategy is not null)
            return _layoutDetectionStrategy;

        _layoutModelDownloadService ??= new LayoutModelDownloadService();
        _docLayoutYoloService ??= new DocLayoutYoloService(_layoutModelDownloadService);
        _visionLayoutDetectionService ??= new VisionLayoutDetectionService(new HttpClient());
        _layoutDetectionStrategy = new LayoutDetectionStrategy(
            _docLayoutYoloService, _visionLayoutDetectionService, _layoutModelDownloadService);

        return _layoutDetectionStrategy;
    }

    /// <summary>
    /// Gets the layout model download service for UI status checks.
    /// </summary>
    public LayoutModelDownloadService GetLayoutModelDownloadService()
    {
        _layoutModelDownloadService ??= new LayoutModelDownloadService();
        return _layoutModelDownloadService;
    }

    public async Task<LongDocumentTranslationResult> TranslateToPdfAsync(
        LongDocumentInputMode mode,
        string input,
        Language from,
        Language to,
        string outputPath,
        string serviceId,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default,
        LayoutDetectionMode layoutDetection = LayoutDetectionMode.Heuristic,
        DocumentOutputMode outputMode = DocumentOutputMode.Monolingual,
        string? visionEndpoint = null,
        string? visionApiKey = null,
        string? visionModel = null)
    {
        var pageRange = string.IsNullOrWhiteSpace(SettingsService.Instance.LongDocPageRange) ? null : SettingsService.Instance.LongDocPageRange;
        var sourceDocument = await BuildSourceDocumentAsync(
            mode, input, layoutDetection, visionEndpoint, visionApiKey, visionModel,
            onProgress, cancellationToken, pageRange);
        var sourceFilePath = input;
        var hasAnySourceText = sourceDocument.Pages
            .SelectMany(page => page.Blocks)
            .Any(block => !string.IsNullOrWhiteSpace(block.Text));
        var hasScannedPages = sourceDocument.Pages.Any(page => page.IsScanned);

        if (!hasAnySourceText && !hasScannedPages)
        {
            throw new InvalidOperationException("No source text found for translation.");
        }

        onProgress?.Invoke("Building long-document IR...");

        var maxConcurrency = Math.Clamp(SettingsService.Instance.LongDocMaxConcurrency, 1, 16);
        var formulaFontPattern = string.IsNullOrWhiteSpace(SettingsService.Instance.FormulaFontPattern) ? null : SettingsService.Instance.FormulaFontPattern;
        var formulaCharPattern = string.IsNullOrWhiteSpace(SettingsService.Instance.FormulaCharPattern) ? null : SettingsService.Instance.FormulaCharPattern;
        var customPrompt = string.IsNullOrWhiteSpace(SettingsService.Instance.LongDocCustomPrompt) ? null : SettingsService.Instance.LongDocCustomPrompt;
        var coreResult = await _coreLongDocumentService.TranslateAsync(sourceDocument, new LongDocumentTranslationOptions
        {
            ServiceId = serviceId,
            FromLanguage = from,
            ToLanguage = to,
            EnableFormulaProtection = true,
            EnableOcrFallback = true,
            MaxRetriesPerBlock = 1,
            MaxConcurrency = maxConcurrency,
            FormulaFontPattern = formulaFontPattern,
            FormulaCharPattern = formulaCharPattern,
            CustomPrompt = customPrompt
        }, cancellationToken);

        var allBlocks = coreResult.Pages
            .SelectMany(page => page.Blocks.Select(block => new
            {
                page.PageNumber,
                Block = block
            }))
            .ToList();

        var orderBySourceBlockId = coreResult.Pages
            .ToDictionary(
                p => p.PageNumber,
                p => p.Blocks.Select((b, index) => new { b.SourceBlockId, index })
                    .ToDictionary(x => x.SourceBlockId, x => x.index, StringComparer.Ordinal));
        var pageBlockCounts = coreResult.Pages.ToDictionary(p => p.PageNumber, p => Math.Max(1, p.Blocks.Count));

        var checkpoint = new LongDocumentTranslationCheckpoint
        {
            InputMode = mode,
            SourceFilePath = sourceFilePath,
            TargetLanguage = to,
            SourceChunks = allBlocks.Select(item => item.Block.OriginalText).ToList(),
            ChunkMetadata = allBlocks
                .Select((item, index) =>
                {
                    var regionInfo = InferRegionInfoFromBlockId(item.Block.SourceBlockId);
                    return new LongDocumentChunkMetadata
                    {
                        ChunkIndex = index,
                        PageNumber = item.PageNumber,
                        SourceBlockId = item.Block.SourceBlockId,
                        SourceBlockType = item.Block.BlockType switch
                        {
                            BlockType.Heading => SourceBlockType.Heading,
                            BlockType.Caption => SourceBlockType.Caption,
                            BlockType.Table => SourceBlockType.TableCell,
                            BlockType.Formula => SourceBlockType.Formula,
                            BlockType.Unknown => SourceBlockType.Unknown,
                            _ => SourceBlockType.Paragraph
                        },
                        IsFormulaLike = item.Block.TranslationSkipped,
                        OrderInPage = orderBySourceBlockId.TryGetValue(item.PageNumber, out var orders) &&
                                      orders.TryGetValue(item.Block.SourceBlockId, out var order)
                            ? order
                            : 0,
                        RegionType = regionInfo.Type,
                        RegionConfidence = regionInfo.Confidence,
                        RegionSource = regionInfo.Source,
                        ReadingOrderScore = CalculateReadingOrderScore(
                        orderBySourceBlockId.TryGetValue(item.PageNumber, out var scoreOrders) &&
                        scoreOrders.TryGetValue(item.Block.SourceBlockId, out var scoreOrder)
                            ? scoreOrder
                            : 0,
                        pageBlockCounts.TryGetValue(item.PageNumber, out var pageCount) ? pageCount : 1),
                        BoundingBox = item.Block.BoundingBox
                    };
                })
                .ToList(),
            TranslatedChunks = allBlocks
                .Select((item, index) => new { item.Block, index })
                .Where(x => string.IsNullOrWhiteSpace(x.Block.LastError))
                .ToDictionary(x => x.index, x => x.Block.TranslatedText),
            FailedChunkIndexes = allBlocks
                .Select((item, index) => new { item.Block, index })
                .Where(x => !string.IsNullOrWhiteSpace(x.Block.LastError))
                .Select(x => x.index)
                .ToHashSet()
        };

        // Try to resolve failed chunks from persistent cache before retrying
        if (SettingsService.Instance.EnableTranslationCache && checkpoint.FailedChunkIndexes.Count > 0)
        {
            await ReadCacheEntriesAsync(checkpoint, serviceId, from, to, cancellationToken);
        }

        EnforceTerminologyConsistency(checkpoint);

        // Write successful translations to persistent cache
        if (SettingsService.Instance.EnableTranslationCache)
        {
            await WriteCacheEntriesAsync(checkpoint, serviceId, from, to, cancellationToken);
        }

        onProgress?.Invoke("Rendering translated output...");
        return FinalizeResult(checkpoint, outputPath, onProgress, coreResult.QualityReport, outputMode);
    }

    public async Task<LongDocumentTranslationResult> RetryFailedChunksAsync(
        LongDocumentTranslationCheckpoint checkpoint,
        Language from,
        Language to,
        string outputPath,
        string serviceId,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default,
        DocumentOutputMode outputMode = DocumentOutputMode.Monolingual)
    {
        ValidateCheckpointOrThrow(checkpoint);

        if (checkpoint.FailedChunkIndexes.Count == 0)
        {
            return FinalizeResult(checkpoint, outputPath, onProgress, BuildQualityReportFromCheckpoint(checkpoint), outputMode);
        }

        onProgress?.Invoke($"Retrying {checkpoint.FailedChunkIndexes.Count} failed chunks...");
        var retryCacheService = SettingsService.Instance.EnableTranslationCache ? _cacheService : null;
        var retrySummary = await TranslatePendingChunksAsync(_coreLongDocumentService, checkpoint, from, to, serviceId, onProgress, cancellationToken, retryCacheService);
        EnforceTerminologyConsistency(checkpoint);
        var qualityReport = BuildQualityReportFromRetry(checkpoint, retrySummary);
        return FinalizeResult(checkpoint, outputPath, onProgress, qualityReport, outputMode);
    }

    private static async Task<RetryExecutionSummary> TranslatePendingChunksAsync(
        CoreLongDocumentTranslationService coreLongDocumentService,
        LongDocumentTranslationCheckpoint checkpoint,
        Language from,
        Language to,
        string serviceId,
        Action<string>? onProgress,
        CancellationToken cancellationToken,
        TranslationCacheService? cacheService = null)
    {
        var pendingIndexes = checkpoint.FailedChunkIndexes.Count > 0
            ? checkpoint.FailedChunkIndexes.OrderBy(i => i).ToList()
            : Enumerable.Range(0, checkpoint.SourceChunks.Count).ToList();
        var metadataByChunkIndex = checkpoint.ChunkMetadata
            .ToDictionary(m => m.ChunkIndex);

        checkpoint.FailedChunkIndexes.Clear();

        if (pendingIndexes.Count == 0)
        {
            return new RetryExecutionSummary(null, 0);
        }

        var indexByRetryBlockId = new Dictionary<string, int>(StringComparer.Ordinal);
        var retryPages = new List<SourceDocumentPage>(pendingIndexes.Count);
        var canonicalBySource = BuildCanonicalTranslationsBySource(checkpoint);
        var reusedByCanonical = 0;

        for (var i = 0; i < pendingIndexes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkIndex = pendingIndexes[i];
            var sourceText = checkpoint.SourceChunks[chunkIndex];

            if (TryGetCanonicalTranslationForChunk(checkpoint, canonicalBySource, chunkIndex, sourceText, out var canonicalTranslation))
            {
                checkpoint.TranslatedChunks[chunkIndex] = canonicalTranslation;
                reusedByCanonical++;
                continue;
            }

            // Try persistent cache before sending to API
            if (cacheService != null && !string.IsNullOrWhiteSpace(sourceText))
            {
                var hash = TranslationCacheService.ComputeHash(sourceText);
                try
                {
                    var cached = await cacheService.TryGetAsync(serviceId, from, to, hash, cancellationToken);
                    if (cached != null)
                    {
                        checkpoint.TranslatedChunks[chunkIndex] = cached;
                        continue;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine($"[LongDoc] Cache lookup failed for chunk {chunkIndex}: {ex.Message}");
                }
            }

            if (!metadataByChunkIndex.TryGetValue(chunkIndex, out var metadata))
            {
                throw new InvalidOperationException($"Missing chunk metadata for chunk index {chunkIndex}.");
            }

            var pageNumber = metadata.PageNumber;
            var blockId = $"retry-{chunkIndex}-{metadata.SourceBlockId}";
            indexByRetryBlockId[blockId] = chunkIndex;

            retryPages.Add(new SourceDocumentPage
            {
                PageNumber = pageNumber,
                Blocks =
                [
                    new SourceDocumentBlock
                    {
                        BlockId = blockId,
                        BlockType = metadata.SourceBlockType,
                        Text = checkpoint.SourceChunks[chunkIndex],
                        IsFormulaLike = metadata.IsFormulaLike,
                        BoundingBox = metadata.BoundingBox
                    }
                ]
            });
        }

        if (retryPages.Count == 0)
        {
            return new RetryExecutionSummary(null, reusedByCanonical);
        }

        var retrySource = new SourceDocument
        {
            DocumentId = "retry-failed-chunks",
            Pages = retryPages
        };

        var retryConcurrency = Math.Clamp(SettingsService.Instance.LongDocMaxConcurrency, 1, 16);
        var retryFormulaFontPattern = string.IsNullOrWhiteSpace(SettingsService.Instance.FormulaFontPattern) ? null : SettingsService.Instance.FormulaFontPattern;
        var retryFormulaCharPattern = string.IsNullOrWhiteSpace(SettingsService.Instance.FormulaCharPattern) ? null : SettingsService.Instance.FormulaCharPattern;
        var retryCustomPrompt = string.IsNullOrWhiteSpace(SettingsService.Instance.LongDocCustomPrompt) ? null : SettingsService.Instance.LongDocCustomPrompt;
        var retryResult = await coreLongDocumentService.TranslateAsync(retrySource, new LongDocumentTranslationOptions
        {
            ServiceId = serviceId,
            FromLanguage = from,
            ToLanguage = to,
            EnableFormulaProtection = true,
            EnableOcrFallback = true,
            MaxRetriesPerBlock = 1,
            MaxConcurrency = retryConcurrency,
            FormulaFontPattern = retryFormulaFontPattern,
            FormulaCharPattern = retryFormulaCharPattern,
            CustomPrompt = retryCustomPrompt
        }, cancellationToken);

        foreach (var translatedBlock in retryResult.Pages.SelectMany(page => page.Blocks))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!indexByRetryBlockId.TryGetValue(translatedBlock.SourceBlockId, out var chunkIndex))
            {
                continue;
            }

            onProgress?.Invoke($"Translating chunk {chunkIndex + 1}/{checkpoint.SourceChunks.Count}...");

            if (!string.IsNullOrWhiteSpace(translatedBlock.LastError) || string.IsNullOrWhiteSpace(translatedBlock.TranslatedText))
            {
                checkpoint.FailedChunkIndexes.Add(chunkIndex);
                continue;
            }

            checkpoint.TranslatedChunks[chunkIndex] = translatedBlock.TranslatedText.Trim();
        }

        foreach (var chunkIndex in pendingIndexes)
        {
            if (!checkpoint.TranslatedChunks.ContainsKey(chunkIndex) && !checkpoint.FailedChunkIndexes.Contains(chunkIndex))
            {
                checkpoint.FailedChunkIndexes.Add(chunkIndex);
            }
        }

        return new RetryExecutionSummary(retryResult.QualityReport, reusedByCanonical);
    }

    private async Task WriteCacheEntriesAsync(
        LongDocumentTranslationCheckpoint checkpoint,
        string serviceId, Language from, Language to,
        CancellationToken ct)
    {
        try
        {
            foreach (var (chunkIndex, translated) in checkpoint.TranslatedChunks)
            {
                ct.ThrowIfCancellationRequested();
                if (chunkIndex < 0 || chunkIndex >= checkpoint.SourceChunks.Count)
                    continue;
                var source = checkpoint.SourceChunks[chunkIndex];
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translated))
                    continue;
                var hash = TranslationCacheService.ComputeHash(source);
                await _cacheService.SetAsync(serviceId, from, to, hash, source, translated, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LongDoc] Cache write failed: {ex.Message}");
        }
    }

    private async Task ReadCacheEntriesAsync(
        LongDocumentTranslationCheckpoint checkpoint,
        string serviceId, Language from, Language to,
        CancellationToken ct)
    {
        try
        {
            var resolved = new List<int>();
            foreach (var chunkIndex in checkpoint.FailedChunkIndexes)
            {
                ct.ThrowIfCancellationRequested();
                if (chunkIndex < 0 || chunkIndex >= checkpoint.SourceChunks.Count)
                    continue;
                var source = checkpoint.SourceChunks[chunkIndex];
                if (string.IsNullOrWhiteSpace(source))
                    continue;
                var hash = TranslationCacheService.ComputeHash(source);
                var cached = await _cacheService.TryGetAsync(serviceId, from, to, hash, ct);
                if (cached != null)
                {
                    checkpoint.TranslatedChunks[chunkIndex] = cached;
                    resolved.Add(chunkIndex);
                }
            }
            foreach (var idx in resolved)
            {
                checkpoint.FailedChunkIndexes.Remove(idx);
            }
            if (resolved.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[LongDoc] Cache read resolved {resolved.Count} chunks");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LongDoc] Cache read failed: {ex.Message}");
        }
    }

    private static IDocumentExportService ResolveExportService(string? sourceFilePath)
    {
        var ext = Path.GetExtension(sourceFilePath)?.ToLowerInvariant();
        return ext switch
        {
            ".pdf" => new PdfExportService(),
            ".md" => new MarkdownExportService(),
            ".txt" => new PlainTextExportService(),
            _ => throw new NotSupportedException($"Unsupported file format: {ext}")
        };
    }

    private static LongDocumentTranslationResult FinalizeResult(
        LongDocumentTranslationCheckpoint checkpoint,
        string outputPath,
        Action<string>? onProgress,
        LongDocumentQualityReport qualityReport,
        DocumentOutputMode outputMode = DocumentOutputMode.Monolingual)
    {
        ValidateCheckpointOrThrow(checkpoint);

        var succeededCount = checkpoint.TranslatedChunks.Count;
        if (succeededCount == 0)
        {
            throw new InvalidOperationException("Translation failed for all chunks.");
        }

        onProgress?.Invoke("Generating output document...");

        var exportService = ResolveExportService(checkpoint.SourceFilePath);
        var exportResult = exportService.Export(checkpoint, checkpoint.SourceFilePath!, outputPath, outputMode);

        var state = checkpoint.FailedChunkIndexes.Count switch
        {
            0 => LongDocumentJobState.Completed,
            _ => LongDocumentJobState.PartialSuccess
        };

        var primaryPath = exportResult.OutputPath;
        onProgress?.Invoke(state == LongDocumentJobState.Completed
            ? exportResult.BilingualOutputPath != null && exportResult.BilingualOutputPath != primaryPath
                ? $"Completed: {primaryPath} + {exportResult.BilingualOutputPath}"
                : $"Completed: {primaryPath}"
            : $"Partially completed: {succeededCount}/{checkpoint.SourceChunks.Count} chunks. You can retry failed chunks.");

        return new LongDocumentTranslationResult
        {
            State = state,
            OutputPath = primaryPath,
            BilingualOutputPath = exportResult.BilingualOutputPath,
            TotalChunks = checkpoint.SourceChunks.Count,
            SucceededChunks = succeededCount,
            FailedChunkIndexes = checkpoint.FailedChunkIndexes.OrderBy(i => i).ToList(),
            QualityReport = qualityReport,
            Checkpoint = checkpoint
        };
    }

    private static string ComposeOutputText(LongDocumentTranslationCheckpoint checkpoint)
    {
        var sb = new StringBuilder();

        var metadataByChunkIndex = checkpoint.ChunkMetadata
            .ToDictionary(m => m.ChunkIndex);

        var orderedChunkIndexes = Enumerable.Range(0, checkpoint.SourceChunks.Count)
            .OrderBy(index => metadataByChunkIndex[index].PageNumber)
            .ThenBy(index => metadataByChunkIndex[index].OrderInPage)
            .ThenBy(index => index)
            .ToList();

        int? currentPage = null;

        foreach (var chunkIndex in orderedChunkIndexes)
        {
            var metadata = metadataByChunkIndex[chunkIndex];
            if (currentPage != metadata.PageNumber)
            {
                currentPage = metadata.PageNumber;
                sb.AppendLine($"=== Page {currentPage} ===");
                sb.AppendLine();
            }

            if (checkpoint.TranslatedChunks.TryGetValue(chunkIndex, out var translated))
            {
                if (metadata?.SourceBlockType == SourceBlockType.Formula || metadata?.IsFormulaLike == true)
                {
                    sb.AppendLine($"[Formula] {translated}");
                }
                else
                {
                    sb.AppendLine(translated);
                }
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($"[Chunk {chunkIndex + 1} translation failed. Retry required.]");
                sb.AppendLine();
            }
        }

        return sb.ToString().Trim();
    }

    private static readonly Regex FormulaHeuristicRegex = new(@"(\$[^$]+\$|\\([^)]+\\)|\\[[^\]]+\\]|\b\w+\s*=\s*[-+*/^()\w]+)", RegexOptions.Compiled);

    private static SourceDocument BuildSourceDocument(LongDocumentInputMode mode, string input, string? pageRange = null)
    {
        if (!File.Exists(input))
        {
            throw new FileNotFoundException("Source file not found.", input);
        }

        if (mode is LongDocumentInputMode.PlainText or LongDocumentInputMode.Markdown)
        {
            return BuildSourceDocumentFromTextFile(input);
        }

        return BuildSourceDocumentFromPdf(input, pageRange);
    }

    private static SourceDocument BuildSourceDocumentFromTextFile(string filePath)
    {
        var text = File.ReadAllText(filePath);
        var blocks = SplitTextIntoBlocks(text, 1).ToList();

        return new SourceDocument
        {
            DocumentId = Path.GetFileNameWithoutExtension(filePath),
            Pages =
            [
                new SourceDocumentPage
                {
                    PageNumber = 1,
                    Blocks = blocks
                }
            ]
        };
    }

    private static IEnumerable<SourceDocumentBlock> SplitTextIntoBlocks(string text, int pageNumber)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield return new SourceDocumentBlock
            {
                BlockId = $"p{pageNumber}-b1",
                BlockType = SourceBlockType.Paragraph,
                Text = string.Empty
            };

            yield break;
        }

        var normalized = text.Replace("\r\n", "\n");
        var rawBlocks = normalized
            .Split("\n\n", StringSplitOptions.TrimEntries)
            .Where(block => !string.IsNullOrWhiteSpace(block))
            .ToList();

        if (rawBlocks.Count == 0)
        {
            rawBlocks.Add(normalized.Trim());
        }

        for (var i = 0; i < rawBlocks.Count; i++)
        {
            var blockText = rawBlocks[i].Trim();
            var blockType = GuessBlockType(blockText);

            yield return new SourceDocumentBlock
            {
                BlockId = $"p{pageNumber}-b{i + 1}",
                BlockType = blockType,
                Text = blockText,
                IsFormulaLike = blockType == SourceBlockType.Formula
            };
        }
    }

    private static SourceDocument BuildSourceDocumentFromPdf(string input, string? pageRange = null)
    {
        using var document = PdfPigDocument.Open(input);
        var allPdfPages = document.GetPages().ToList();
        var selectedPages = PageRangeParser.Parse(pageRange, allPdfPages.Count);
        var pages = allPdfPages
            .Where(page => selectedPages == null || selectedPages.Contains(page.Number))
            .Select(page =>
            {
                var blocks = ExtractLayoutBlocksFromPage(page).ToList();
                var scanned = string.IsNullOrWhiteSpace(page.Text) || blocks.Count == 0;
                return new SourceDocumentPage
                {
                    PageNumber = page.Number,
                    IsScanned = scanned,
                    Blocks = scanned ? [] : blocks
                };
            })
            .ToList();

        if (pages.Count == 0)
        {
            pages.Add(new SourceDocumentPage
            {
                PageNumber = 1,
                IsScanned = true,
                Blocks = []
            });
        }

        return new SourceDocument
        {
            DocumentId = Path.GetFileNameWithoutExtension(input),
            Pages = pages
        };
    }

    /// <summary>
    /// Async version of BuildSourceDocument that supports ML layout detection.
    /// Text/Markdown modes always use heuristic (no PDF pages to analyze).
    /// Falls back to heuristic when ML detection is set to Heuristic.
    /// </summary>
    private async Task<SourceDocument> BuildSourceDocumentAsync(
        LongDocumentInputMode mode,
        string input,
        LayoutDetectionMode layoutDetection,
        string? visionEndpoint,
        string? visionApiKey,
        string? visionModel,
        Action<string>? onProgress,
        CancellationToken ct,
        string? pageRange = null)
    {
        // Text/Markdown modes don't have PDF pages for ML layout detection
        if (mode is LongDocumentInputMode.PlainText or LongDocumentInputMode.Markdown ||
            layoutDetection == LayoutDetectionMode.Heuristic)
        {
            return BuildSourceDocument(mode, input, pageRange);
        }

        if (!File.Exists(input))
        {
            throw new FileNotFoundException("Source file not found.", input);
        }

        var strategy = GetLayoutDetectionStrategy();

        // For Auto mode, check if ONNX is available; if not, fall back to sync heuristic
        if (layoutDetection == LayoutDetectionMode.Auto && !strategy.IsOnnxDownloaded)
        {
            return BuildSourceDocument(mode, input, pageRange);
        }

        onProgress?.Invoke("Analyzing page layouts with ML model...");

        using var document = PdfPigDocument.Open(input);
        var pdfPages = document.GetPages().ToList();
        var selectedPages = PageRangeParser.Parse(pageRange, pdfPages.Count);
        var pages = new List<SourceDocumentPage>();

        for (var i = 0; i < pdfPages.Count; i++)
        {
            var page = pdfPages[i];

            // Skip pages not in the selected range
            if (selectedPages != null && !selectedPages.Contains(page.Number))
                continue;
            var pageText = page.Text;
            var scanned = string.IsNullOrWhiteSpace(pageText);

            if (scanned)
            {
                pages.Add(new SourceDocumentPage
                {
                    PageNumber = page.Number,
                    IsScanned = true,
                    Blocks = []
                });
                continue;
            }

            // First extract heuristic blocks (always needed for text content)
            var heuristicBlocks = ExtractLayoutBlocksFromPage(page).ToList();
            if (heuristicBlocks.Count == 0)
            {
                pages.Add(new SourceDocumentPage
                {
                    PageNumber = page.Number,
                    IsScanned = true,
                    Blocks = []
                });
                continue;
            }

            // Try ML-enhanced detection
            try
            {
                var enhancedBlocks = await strategy.DetectAndExtractAsync(
                    page, input, i, layoutDetection,
                    visionEndpoint, visionApiKey, visionModel, ct);

                if (enhancedBlocks.Count > 0)
                {
                    // Use enhanced blocks: update BlockId region tags and apply ML region types
                    var mlBlocks = enhancedBlocks.Select((eb, blockIdx) =>
                    {
                        var regionTag = eb.RegionType switch
                        {
                            LayoutRegionType.Header => "header",
                            LayoutRegionType.Footer => "footer",
                            LayoutRegionType.LeftColumn => "left",
                            LayoutRegionType.RightColumn => "right",
                            LayoutRegionType.TableLike or LayoutRegionType.Table => "table",
                            LayoutRegionType.Figure => "figure",
                            LayoutRegionType.Formula or LayoutRegionType.IsolatedFormula => "formula",
                            LayoutRegionType.Caption => "caption",
                            LayoutRegionType.Title => "title",
                            _ => "body"
                        };

                        return eb.Block with
                        {
                            BlockId = $"p{page.Number}-{regionTag}-b{blockIdx + 1}"
                        };
                    }).ToList();

                    pages.Add(new SourceDocumentPage
                    {
                        PageNumber = page.Number,
                        IsScanned = false,
                        Blocks = mlBlocks
                    });
                    continue;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LongDoc] ML detection failed for page {page.Number}: {ex.Message}");
            }

            // Fallback: use heuristic blocks
            pages.Add(new SourceDocumentPage
            {
                PageNumber = page.Number,
                IsScanned = false,
                Blocks = heuristicBlocks
            });
        }

        if (pages.Count == 0)
        {
            pages.Add(new SourceDocumentPage
            {
                PageNumber = 1,
                IsScanned = true,
                Blocks = []
            });
        }

        return new SourceDocument
        {
            DocumentId = Path.GetFileNameWithoutExtension(input),
            Pages = pages
        };
    }

    // --- PDF export methods removed; see PdfExportService ---

    private static IEnumerable<SourceDocumentBlock> ExtractLayoutBlocksFromPage(PdfPigPage page)
    {
        var words = page.GetWords()
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .OrderByDescending(word => word.BoundingBox.Top)
            .ThenBy(word => word.BoundingBox.Left)
            .ToList();

        if (words.Count == 0)
        {
            yield break;
        }

        var medianWordHeight = words
            .Select(w => Math.Max(1d, w.BoundingBox.Height))
            .OrderBy(h => h)
            .Skip(words.Count / 2)
            .FirstOrDefault();

        var sameLineThreshold = Math.Max(2.5, medianWordHeight * 0.35);
        var paragraphGapThreshold = Math.Max(8, medianWordHeight * 1.8);

        var lines = new List<PdfTextLine>();
        foreach (var word in words)
        {
            var box = word.BoundingBox;
            var line = lines.FirstOrDefault(l => Math.Abs(l.Top - box.Top) <= sameLineThreshold);
            if (line is null)
            {
                line = new PdfTextLine(box.Top);
                lines.Add(line);
            }

            line.Words.Add(word);
        }

        lines = lines
            .Select(l => l.Normalize())
            .OrderByDescending(l => l.Top)
            .ToList();

        var orderedLines = OrderLinesByLayout(lines, Convert.ToDecimal(page.Width));
        var paragraphs = BuildParagraphs(orderedLines, paragraphGapThreshold);
        var layoutProfile = BuildLayoutProfile(orderedLines, (double)page.Width, (double)page.Height);

        for (var i = 0; i < paragraphs.Count; i++)
        {
            var linesInBlock = paragraphs[i];
            var blockText = string.Join("\n", linesInBlock.Select(l => l.Text)).Trim();
            if (string.IsNullOrWhiteSpace(blockText))
            {
                continue;
            }

            var left = linesInBlock.Min(l => l.Left);
            var right = linesInBlock.Max(l => l.Right);
            var top = linesInBlock.Max(l => l.Top);
            var bottom = linesInBlock.Min(l => l.Bottom);

            var type = GuessBlockType(blockText);
            var regionType = InferRegionType(layoutProfile, left, right, top, bottom, blockText);
            var regionTag = regionType switch
            {
                LayoutRegionType.Header => "header",
                LayoutRegionType.Footer => "footer",
                LayoutRegionType.LeftColumn => "left",
                LayoutRegionType.RightColumn => "right",
                LayoutRegionType.TableLike => "table",
                _ => "body"
            };

            // Collect font names from letters within this block's bounding region
            var blockFontNames = CollectFontNamesForBlock(page, left, right, top, bottom);

            yield return new SourceDocumentBlock
            {
                BlockId = $"p{page.Number}-{regionTag}-b{i + 1}",
                BlockType = type,
                Text = blockText,
                IsFormulaLike = type == SourceBlockType.Formula,
                BoundingBox = new BlockRect(left, bottom, Math.Max(1, right - left), Math.Max(1, top - bottom)),
                DetectedFontNames = blockFontNames.Count > 0 ? blockFontNames : null
            };
        }
    }

    private static List<string> CollectFontNamesForBlock(PdfPigPage page, double left, double right, double top, double bottom)
    {
        var fontNames = new List<string>();
        try
        {
            foreach (var letter in page.Letters)
            {
                var lbox = letter.GlyphRectangle;
                if (lbox.Left >= left - 1 && lbox.Right <= right + 1 &&
                    lbox.Bottom >= bottom - 1 && lbox.Top <= top + 1)
                {
                    if (!string.IsNullOrWhiteSpace(letter.FontName))
                    {
                        fontNames.Add(letter.FontName);
                    }
                }
            }
        }
        catch
        {
            // PdfPig may throw on some PDFs; ignore and return what we have
        }
        return fontNames;
    }

    private static LayoutRegionType InferRegionType(LayoutProfile profile, double left, double right, double top, double bottom, string blockText)
    {
        var centerX = (left + right) / 2d;
        var blockHeight = Math.Max(1, top - bottom);
        var blockWidth = Math.Max(1, right - left);

        if (top >= profile.HeaderTopThreshold)
        {
            return LayoutRegionType.Header;
        }

        if (bottom <= profile.FooterBottomThreshold)
        {
            return LayoutRegionType.Footer;
        }

        var looksLikeTable = blockText.Contains('\t') || blockText.Contains("  ") ||
                             blockText.Contains('|') || Regex.IsMatch(blockText, @"\b\d+(\.\d+)?\b\s+\b\d+(\.\d+)?\b");
        if (looksLikeTable || (blockWidth > profile.PageWidth * 0.8 && blockHeight < profile.PageHeight * 0.1))
        {
            return LayoutRegionType.TableLike;
        }

        if (profile.IsTwoColumn)
        {
            if (centerX <= profile.LeftColumnBoundary)
            {
                return LayoutRegionType.LeftColumn;
            }

            if (centerX >= profile.RightColumnBoundary)
            {
                return LayoutRegionType.RightColumn;
            }
        }
        else
        {
            if (centerX < profile.PageWidth * 0.46)
            {
                return LayoutRegionType.LeftColumn;
            }

            if (centerX > profile.PageWidth * 0.54)
            {
                return LayoutRegionType.RightColumn;
            }
        }

        return LayoutRegionType.Body;
    }

    private sealed record LayoutProfile(
        double PageWidth,
        double PageHeight,
        bool IsTwoColumn,
        double LeftColumnBoundary,
        double RightColumnBoundary,
        double HeaderTopThreshold,
        double FooterBottomThreshold);

    private static LayoutProfile BuildLayoutProfile(IReadOnlyList<PdfTextLine> lines, double pageWidth, double pageHeight)
    {
        if (lines.Count == 0)
        {
            return new LayoutProfile(pageWidth, pageHeight, false, pageWidth * 0.45, pageWidth * 0.55, pageHeight * 0.92, pageHeight * 0.08);
        }

        var centers = lines.Select(l => l.CenterX).OrderBy(v => v).ToList();
        var p25 = centers[(int)Math.Floor((centers.Count - 1) * 0.25)];
        var p75 = centers[(int)Math.Floor((centers.Count - 1) * 0.75)];
        var span = p75 - p25;
        var isTwoColumn = span > pageWidth * 0.22;

        var headerTop = Math.Max(pageHeight * 0.88, lines.Max(l => l.Top) - pageHeight * 0.05);
        var footerBottom = Math.Min(pageHeight * 0.12, lines.Min(l => l.Bottom) + pageHeight * 0.05);

        return new LayoutProfile(
            pageWidth,
            pageHeight,
            isTwoColumn,
            isTwoColumn ? p25 : pageWidth * 0.45,
            isTwoColumn ? p75 : pageWidth * 0.55,
            headerTop,
            footerBottom);
    }

    private static List<PdfTextLine> OrderLinesByLayout(IReadOnlyList<PdfTextLine> lines, decimal pageWidth)
    {
        if (lines.Count < 8)
        {
            return lines.OrderByDescending(l => l.Top).ToList();
        }

        var width = (double)pageWidth;
        var mid = width / 2;
        var leftLines = lines.Where(l => l.CenterX < mid * 0.92).ToList();
        var rightLines = lines.Where(l => l.CenterX > mid * 1.08).ToList();

        var isTwoColumn = leftLines.Count >= lines.Count * 0.25 && rightLines.Count >= lines.Count * 0.25;
        if (!isTwoColumn)
        {
            return lines.OrderByDescending(l => l.Top).ToList();
        }

        var ordered = new List<PdfTextLine>(lines.Count);
        ordered.AddRange(leftLines.OrderByDescending(l => l.Top));
        ordered.AddRange(rightLines.OrderByDescending(l => l.Top));

        var remaining = lines.Except(ordered).OrderByDescending(l => l.Top);
        ordered.AddRange(remaining);
        return ordered;
    }

    private static List<List<PdfTextLine>> BuildParagraphs(IReadOnlyList<PdfTextLine> lines, double paragraphGapThreshold)
    {
        var paragraphs = new List<List<PdfTextLine>>();
        foreach (var line in lines)
        {
            if (paragraphs.Count == 0)
            {
                paragraphs.Add([line]);
                continue;
            }

            var current = paragraphs[^1];
            var prev = current[^1];
            var gap = Math.Abs(prev.Bottom - line.Top);
            var horizontalOffset = Math.Abs(prev.Left - line.Left);
            var shouldSplit = gap > paragraphGapThreshold || horizontalOffset > Math.Max(30, prev.Width * 0.6);

            if (shouldSplit)
            {
                paragraphs.Add([line]);
            }
            else
            {
                current.Add(line);
            }
        }

        return paragraphs;
    }

    private sealed class PdfTextLine(double top)
    {
        public double Top { get; } = top;
        public List<Word> Words { get; } = [];
        public double Left { get; private set; }
        public double Right { get; private set; }
        public double Bottom { get; private set; }
        public double Width => Right - Left;
        public double CenterX => Left + Width / 2;
        public string Text { get; private set; } = string.Empty;

        public PdfTextLine Normalize()
        {
            var sorted = Words.OrderBy(w => w.BoundingBox.Left).ToList();
            Left = sorted.Min(w => w.BoundingBox.Left);
            Right = sorted.Max(w => w.BoundingBox.Right);
            Bottom = sorted.Min(w => w.BoundingBox.Bottom);
            Text = string.Join(" ", sorted.Select(w => w.Text));
            return this;
        }
    }

    private static double CalculateReadingOrderScore(int orderInPage, int pageBlockCount)
    {
        if (pageBlockCount <= 1)
        {
            return 1d;
        }

        var denominator = Math.Max(1, pageBlockCount - 1);
        var normalized = 1d - Math.Clamp(orderInPage / (double)denominator, 0d, 1d);
        return Math.Round(normalized, 4, MidpointRounding.AwayFromZero);
    }

    private static (LayoutRegionType Type, double Confidence, LayoutRegionSource Source) InferRegionInfoFromBlockId(string sourceBlockId)
    {
        if (sourceBlockId.Contains("-header-", StringComparison.OrdinalIgnoreCase))
        {
            return (LayoutRegionType.Header, 0.92d, LayoutRegionSource.Heuristic);
        }

        if (sourceBlockId.Contains("-footer-", StringComparison.OrdinalIgnoreCase))
        {
            return (LayoutRegionType.Footer, 0.92d, LayoutRegionSource.Heuristic);
        }

        if (sourceBlockId.Contains("-left-", StringComparison.OrdinalIgnoreCase))
        {
            return (LayoutRegionType.LeftColumn, 0.80d, LayoutRegionSource.Heuristic);
        }

        if (sourceBlockId.Contains("-right-", StringComparison.OrdinalIgnoreCase))
        {
            return (LayoutRegionType.RightColumn, 0.80d, LayoutRegionSource.Heuristic);
        }

        if (sourceBlockId.Contains("-table-", StringComparison.OrdinalIgnoreCase))
        {
            return (LayoutRegionType.TableLike, 0.88d, LayoutRegionSource.Heuristic);
        }

        // ML-detected region types (from ONNX or Vision LLM)
        if (sourceBlockId.Contains("-figure-", StringComparison.OrdinalIgnoreCase))
        {
            return (LayoutRegionType.Figure, 0.90d, LayoutRegionSource.OnnxModel);
        }

        if (sourceBlockId.Contains("-formula-", StringComparison.OrdinalIgnoreCase))
        {
            return (LayoutRegionType.Formula, 0.90d, LayoutRegionSource.OnnxModel);
        }

        if (sourceBlockId.Contains("-caption-", StringComparison.OrdinalIgnoreCase))
        {
            return (LayoutRegionType.Caption, 0.85d, LayoutRegionSource.OnnxModel);
        }

        if (sourceBlockId.Contains("-title-", StringComparison.OrdinalIgnoreCase))
        {
            return (LayoutRegionType.Title, 0.88d, LayoutRegionSource.OnnxModel);
        }

        if (sourceBlockId.Contains("-body-", StringComparison.OrdinalIgnoreCase))
        {
            return (LayoutRegionType.Body, 0.72d, LayoutRegionSource.BlockIdFallback);
        }

        return (LayoutRegionType.Unknown, 0.35d, LayoutRegionSource.Unknown);
    }

    private static SourceBlockType GuessBlockType(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return SourceBlockType.Unknown;
        }

        if (FormulaHeuristicRegex.IsMatch(text))
        {
            return SourceBlockType.Formula;
        }

        if (text.Length < 80 && text.All(c => !char.IsLetter(c) || char.IsUpper(c)))
        {
            return SourceBlockType.Heading;
        }

        return SourceBlockType.Paragraph;
    }

    private static IReadOnlyDictionary<int, BackfillPageMetrics>? MergePageBackfillMetrics(
        IReadOnlyDictionary<int, BackfillPageMetrics>? previous,
        IReadOnlyDictionary<int, BackfillPageMetrics>? current)
    {
        if (previous is null && current is null)
        {
            return null;
        }

        if (previous is null)
        {
            return current!.ToDictionary(entry => entry.Key, entry => entry.Value);
        }

        if (current is null)
        {
            return previous.ToDictionary(entry => entry.Key, entry => entry.Value);
        }

        var merged = previous.ToDictionary(entry => entry.Key, entry => entry.Value);
        foreach (var (pageNumber, currentPage) in current)
        {
            if (!merged.TryGetValue(pageNumber, out var previousPage))
            {
                merged[pageNumber] = currentPage;
                continue;
            }

            merged[pageNumber] = new BackfillPageMetrics
            {
                CandidateBlocks = previousPage.CandidateBlocks + currentPage.CandidateBlocks,
                RenderedBlocks = previousPage.RenderedBlocks + currentPage.RenderedBlocks,
                MissingBoundingBoxBlocks = previousPage.MissingBoundingBoxBlocks + currentPage.MissingBoundingBoxBlocks,
                ShrinkFontBlocks = previousPage.ShrinkFontBlocks + currentPage.ShrinkFontBlocks,
                TruncatedBlocks = previousPage.TruncatedBlocks + currentPage.TruncatedBlocks,
                ObjectReplaceBlocks = previousPage.ObjectReplaceBlocks + currentPage.ObjectReplaceBlocks,
                OverlayModeBlocks = previousPage.OverlayModeBlocks + currentPage.OverlayModeBlocks,
                StructuredFallbackBlocks = previousPage.StructuredFallbackBlocks + currentPage.StructuredFallbackBlocks
            };
        }

        return merged;
    }

    private static LongDocumentQualityReport BuildQualityReportFromCheckpoint(LongDocumentTranslationCheckpoint checkpoint)
    {
        var metadataByIndex = checkpoint.ChunkMetadata.ToDictionary(m => m.ChunkIndex);
        var failedBlocks = checkpoint.FailedChunkIndexes
            .Select(index => metadataByIndex[index])
            .Select(metadata => new FailedBlockInfo
            {
                IrBlockId = $"checkpoint-{metadata.ChunkIndex}",
                SourceBlockId = metadata.SourceBlockId,
                PageNumber = metadata.PageNumber,
                RetryCount = 0,
                Error = "Translation failed or missing translated text."
            })
            .ToList();

        return new LongDocumentQualityReport
        {
            StageTimingsMs = new Dictionary<string, long>(),
            BackfillMetrics = null,
            TotalBlocks = checkpoint.SourceChunks.Count,
            TranslatedBlocks = checkpoint.TranslatedChunks.Count,
            SkippedBlocks = checkpoint.ChunkMetadata.Count(m => m.SourceBlockType == SourceBlockType.Formula || m.IsFormulaLike),
            FailedBlocks = failedBlocks
        };
    }

    private static BackfillQualityMetrics? MergeRetryBackfillMetrics(
        BackfillQualityMetrics? previous,
        BackfillQualityMetrics? current)
    {
        if (previous is null && current is null)
        {
            return null;
        }

        if (previous is null)
        {
            var coreMetrics = current!;
            return coreMetrics with
            {
                RetryMergeStrategy = "core-only",
                PageMetrics = MergePageBackfillMetrics(null, coreMetrics.PageMetrics)
            };
        }

        if (current is null)
        {
            return previous with { RetryMergeStrategy = "checkpoint-only", PageMetrics = MergePageBackfillMetrics(previous.PageMetrics, null) };
        }

        return new BackfillQualityMetrics
        {
            CandidateBlocks = previous.CandidateBlocks + current.CandidateBlocks,
            RenderedBlocks = previous.RenderedBlocks + current.RenderedBlocks,
            MissingBoundingBoxBlocks = previous.MissingBoundingBoxBlocks + current.MissingBoundingBoxBlocks,
            ShrinkFontBlocks = previous.ShrinkFontBlocks + current.ShrinkFontBlocks,
            TruncatedBlocks = previous.TruncatedBlocks + current.TruncatedBlocks,
            ObjectReplaceBlocks = previous.ObjectReplaceBlocks + current.ObjectReplaceBlocks,
            OverlayModeBlocks = previous.OverlayModeBlocks + current.OverlayModeBlocks,
            StructuredFallbackBlocks = previous.StructuredFallbackBlocks + current.StructuredFallbackBlocks,
            PageMetrics = MergePageBackfillMetrics(previous.PageMetrics, current.PageMetrics),
            RetryMergeStrategy = "accumulate"
        };
    }

    private static LongDocumentQualityReport BuildQualityReportFromRetry(
        LongDocumentTranslationCheckpoint checkpoint,
        RetryExecutionSummary retrySummary)
    {
        var fallback = BuildQualityReportFromCheckpoint(checkpoint);
        if (retrySummary.CoreQualityReport is null)
        {
            return fallback;
        }

        var timings = new Dictionary<string, long>(retrySummary.CoreQualityReport.StageTimingsMs, StringComparer.Ordinal)
        {
            ["retry-canonical-reuse"] = retrySummary.ReusedByCanonicalCount
        };

        var backfill = MergeRetryBackfillMetrics(fallback.BackfillMetrics, retrySummary.CoreQualityReport.BackfillMetrics);

        return new LongDocumentQualityReport
        {
            StageTimingsMs = timings,
            BackfillMetrics = backfill,
            TotalBlocks = fallback.TotalBlocks,
            TranslatedBlocks = fallback.TranslatedBlocks,
            SkippedBlocks = fallback.SkippedBlocks,
            FailedBlocks = fallback.FailedBlocks
        };
    }

    private static void ValidateCheckpointOrThrow(LongDocumentTranslationCheckpoint checkpoint)
    {
        if (checkpoint.ChunkMetadata.Count != checkpoint.SourceChunks.Count)
        {
            throw new InvalidOperationException("Checkpoint metadata count does not match source chunk count.");
        }

        var expectedIndexes = Enumerable.Range(0, checkpoint.SourceChunks.Count).ToHashSet();
        var actualIndexes = checkpoint.ChunkMetadata.Select(m => m.ChunkIndex).ToHashSet();
        if (!expectedIndexes.SetEquals(actualIndexes))
        {
            throw new InvalidOperationException("Checkpoint metadata indexes are incomplete or duplicated.");
        }
    }

    private static Dictionary<string, List<CanonicalTranslationEntry>> BuildCanonicalTranslationsBySource(LongDocumentTranslationCheckpoint checkpoint)
    {
        var metadataByChunkIndex = checkpoint.ChunkMetadata.ToDictionary(m => m.ChunkIndex);
        var canonical = new Dictionary<string, List<CanonicalTranslationEntry>>(StringComparer.Ordinal);

        foreach (var entry in checkpoint.TranslatedChunks.OrderBy(item => item.Key))
        {
            if (entry.Key < 0 || entry.Key >= checkpoint.SourceChunks.Count ||
                !metadataByChunkIndex.TryGetValue(entry.Key, out var metadata))
            {
                continue;
            }

            var source = checkpoint.SourceChunks[entry.Key];
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(entry.Value))
            {
                continue;
            }

            if (!canonical.TryGetValue(source, out var values))
            {
                values = [];
                canonical[source] = values;
            }

            values.Add(new CanonicalTranslationEntry(entry.Key, metadata.PageNumber, entry.Value.Trim()));
        }

        return canonical;
    }

    private static bool TryGetCanonicalTranslationForChunk(
        LongDocumentTranslationCheckpoint checkpoint,
        IReadOnlyDictionary<string, List<CanonicalTranslationEntry>> canonicalBySource,
        int chunkIndex,
        string sourceText,
        out string canonicalTranslation)
    {
        canonicalTranslation = string.Empty;
        if (string.IsNullOrWhiteSpace(sourceText) ||
            !canonicalBySource.TryGetValue(sourceText, out var candidates) ||
            candidates.Count == 0)
        {
            return false;
        }

        var metadataByChunkIndex = checkpoint.ChunkMetadata.ToDictionary(m => m.ChunkIndex);
        if (!metadataByChunkIndex.TryGetValue(chunkIndex, out var targetMetadata))
        {
            return false;
        }

        const int pageWindow = 2;

        var best = candidates
            .Where(c => c.ChunkIndex != chunkIndex)
            .Select(c => new
            {
                Entry = c,
                Distance = Math.Abs(c.PageNumber - targetMetadata.PageNumber)
            })
            .OrderBy(x => x.Distance)
            .ThenByDescending(x => x.Entry.ChunkIndex)
            .FirstOrDefault(x => x.Distance <= pageWindow)
            ?.Entry;

        if (best is null)
        {
            best = candidates
                .Where(c => c.ChunkIndex != chunkIndex)
                .OrderByDescending(c => c.ChunkIndex)
                .FirstOrDefault();
        }

        if (best is null || string.IsNullOrWhiteSpace(best.Translation))
        {
            return false;
        }

        canonicalTranslation = best.Translation;
        return true;
    }

    private static void EnforceTerminologyConsistency(LongDocumentTranslationCheckpoint checkpoint)
    {
        var canonicalBySource = BuildCanonicalTranslationsBySource(checkpoint);

        for (var i = 0; i < checkpoint.SourceChunks.Count; i++)
        {
            if (checkpoint.FailedChunkIndexes.Contains(i))
            {
                continue;
            }

            var source = checkpoint.SourceChunks[i];
            if (TryGetCanonicalTranslationForChunk(checkpoint, canonicalBySource, i, source, out var canonical))
            {
                checkpoint.TranslatedChunks[i] = canonical;
            }
        }
    }
}
