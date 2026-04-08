using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Easydict.TranslationService.ContentPreservation;
using Easydict.TranslationService.FormulaProtection;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services.DocumentExport;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;
using PdfPigPage = UglyToad.PdfPig.Content.Page;
using CoreLongDocumentTranslationService = Easydict.TranslationService.LongDocument.LongDocumentTranslationService;
using CoreLongDocumentTranslationResult = Easydict.TranslationService.LongDocument.LongDocumentTranslationResult;
using LetterGeometry = Easydict.TranslationService.ContentPreservation.FormulaAwareTextReconstructor.LetterGeometry;

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
    /// <summary>
    /// Optional per-chunk annotations for source-fallback blocks. The default PDF export
    /// pipeline does not currently populate or render these.
    /// </summary>
    public Dictionary<int, IReadOnlyList<WordAnnotation>>? WordAnnotations { get; set; }
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
    public BlockTextStyle? TextStyle { get; init; }
    public BlockFormulaCharacters? FormulaCharacters { get; init; }
    public bool TranslationSkipped { get; init; }
    public bool PreserveOriginalTextInPdfExport { get; init; }
    public int RetryCount { get; set; }
    public string? FallbackText { get; init; }
    public IReadOnlyList<string>? DetectedFontNames { get; init; }
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

public sealed class LongDocumentTranslationService : IDisposable
{
    private sealed record RetryExecutionSummary(LongDocumentQualityReport? CoreQualityReport, int ReusedByCanonicalCount);

    private sealed record CanonicalTranslationEntry(int ChunkIndex, int PageNumber, string Translation);

    private readonly CoreLongDocumentTranslationService _coreLongDocumentService = new(
        translateWithService: (request, serviceId, ct) =>
            TranslationManagerService.Instance.Manager.TranslateAsync(request, ct, serviceId));
    private readonly TranslationCacheService _cacheService = new();

    // Layout detection services (lazy-initialized)
    private LayoutModelDownloadService? _layoutModelDownloadService;
    private DocLayoutYoloService? _docLayoutYoloService;
    private HttpClient? _visionHttpClient;
    private VisionLayoutDetectionService? _visionLayoutDetectionService;
    private LayoutDetectionStrategy? _layoutDetectionStrategy;
    private bool _disposed;

    /// <summary>
    /// Gets or creates the layout detection strategy instance.
    /// </summary>
    private LayoutDetectionStrategy GetLayoutDetectionStrategy()
    {
        if (_layoutDetectionStrategy is not null)
            return _layoutDetectionStrategy;

        _layoutModelDownloadService ??= new LayoutModelDownloadService();
        _docLayoutYoloService ??= new DocLayoutYoloService(_layoutModelDownloadService);
        _visionHttpClient ??= new HttpClient();
        _visionLayoutDetectionService ??= new VisionLayoutDetectionService(_visionHttpClient);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _layoutModelDownloadService?.Dispose();
        _visionHttpClient?.Dispose();
        _cacheService.Dispose();
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
        PdfExportMode pdfExportMode = PdfExportMode.ContentStreamReplacement,
        string? visionEndpoint = null,
        string? visionApiKey = null,
        string? visionModel = null,
        System.IProgress<LongDocumentTranslationProgress>? progress = null)
    {
        var pageRange = string.IsNullOrWhiteSpace(SettingsService.Instance.LongDocPageRange) ? null : SettingsService.Instance.LongDocPageRange;

        // Build source document (now natively async, CPU-bound work is wrapped inside)
        var sourceDocument = await BuildSourceDocumentAsync(
            mode, input, layoutDetection, visionEndpoint, visionApiKey, visionModel,
            onProgress, cancellationToken, pageRange).ConfigureAwait(false);

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
        var coreOptions = CreateCoreTranslationOptions(
            serviceId,
            from,
            to,
            enableOcrFallback: true,
            maxConcurrency,
            formulaFontPattern,
            formulaCharPattern,
            customPrompt,
            progress);
        var coreResult = await _coreLongDocumentService.TranslateAsync(sourceDocument, coreOptions, cancellationToken).ConfigureAwait(false);

        var checkpoint = BuildCheckpointFromCoreResult(
            mode,
            sourceFilePath,
            to,
            sourceDocument,
            coreResult);

        // Try to resolve failed chunks from persistent cache before retrying
        if (SettingsService.Instance.EnableTranslationCache && checkpoint.FailedChunkIndexes.Count > 0)
        {
            await ReadCacheEntriesAsync(checkpoint, serviceId, from, to, cancellationToken).ConfigureAwait(false);
        }

        EnforceTerminologyConsistency(checkpoint);

        // Write successful translations to persistent cache
        if (SettingsService.Instance.EnableTranslationCache)
        {
            await WriteCacheEntriesAsync(checkpoint, serviceId, from, to, cancellationToken).ConfigureAwait(false);
        }

        onProgress?.Invoke("Rendering translated output...");
        return FinalizeResult(checkpoint, outputPath, onProgress, coreResult.QualityReport, outputMode, pdfExportMode);
    }

    public async Task<LongDocumentTranslationResult> RetryFailedChunksAsync(
        LongDocumentTranslationCheckpoint checkpoint,
        Language from,
        Language to,
        string outputPath,
        string serviceId,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default,
        DocumentOutputMode outputMode = DocumentOutputMode.Monolingual,
        System.IProgress<LongDocumentTranslationProgress>? progress = null)
    {
        ValidateCheckpointOrThrow(checkpoint);

        if (checkpoint.FailedChunkIndexes.Count == 0)
        {
            return FinalizeResult(checkpoint, outputPath, onProgress, BuildQualityReportFromCheckpoint(checkpoint), outputMode);
        }

        onProgress?.Invoke($"Retrying {checkpoint.FailedChunkIndexes.Count} failed chunks...");
        var retryCacheService = SettingsService.Instance.EnableTranslationCache ? _cacheService : null;
        var retrySummary = await TranslatePendingChunksAsync(_coreLongDocumentService, checkpoint, from, to, serviceId, onProgress, cancellationToken, retryCacheService, progress).ConfigureAwait(false);
        EnforceTerminologyConsistency(checkpoint);
        var qualityReport = BuildQualityReportFromRetry(checkpoint, retrySummary);

        return FinalizeResult(checkpoint, outputPath, onProgress, qualityReport, outputMode);
    }

    internal static LongDocumentTranslationCheckpoint BuildCheckpointFromCoreResult(
        LongDocumentInputMode mode,
        string sourceFilePath,
        Language targetLanguage,
        SourceDocument sourceDocument,
        CoreLongDocumentTranslationResult coreResult)
    {
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
        var sourceBlocksByPageAndId = sourceDocument.Pages
            .SelectMany(page => page.Blocks.Select(block => new
            {
                page.PageNumber,
                Block = block
            }))
            .ToDictionary(
                x => (x.PageNumber, x.Block.BlockId),
                x => x.Block);

        return new LongDocumentTranslationCheckpoint
        {
            InputMode = mode,
            SourceFilePath = sourceFilePath,
            TargetLanguage = targetLanguage,
            SourceChunks = allBlocks.Select(item => item.Block.OriginalText).ToList(),
            ChunkMetadata = allBlocks
                .Select((item, index) =>
                {
                    var regionInfo = InferRegionInfoFromBlockId(item.Block.SourceBlockId);
                    sourceBlocksByPageAndId.TryGetValue((item.PageNumber, item.Block.SourceBlockId), out var sourceBlock);
                    var sourceBlockType = item.Block.BlockType switch
                    {
                        BlockType.Heading => SourceBlockType.Heading,
                        BlockType.Caption => SourceBlockType.Caption,
                        BlockType.Table => SourceBlockType.TableCell,
                        BlockType.Formula => SourceBlockType.Formula,
                        BlockType.Unknown => SourceBlockType.Unknown,
                        _ => SourceBlockType.Paragraph
                    };
                    return new LongDocumentChunkMetadata
                    {
                        ChunkIndex = index,
                        PageNumber = item.PageNumber,
                        SourceBlockId = item.Block.SourceBlockId,
                        SourceBlockType = sourceBlockType,
                        IsFormulaLike = sourceBlock?.IsFormulaLike ?? sourceBlockType == SourceBlockType.Formula,
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
                        BoundingBox = item.Block.BoundingBox,
                        TextStyle = item.Block.TextStyle,
                        FormulaCharacters = item.Block.FormulaCharacters,
                        TranslationSkipped = item.Block.TranslationSkipped,
                        PreserveOriginalTextInPdfExport =
                            item.Block.PreserveOriginalTextInPdfExport ||
                            sourceBlockType == SourceBlockType.Formula ||
                            regionInfo.Type is LayoutRegionType.Formula or LayoutRegionType.IsolatedFormula,
                        RetryCount = item.Block.RetryCount,
                        FallbackText = sourceBlock?.FallbackText,
                        DetectedFontNames = sourceBlock?.DetectedFontNames
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
    }

    private static async Task<RetryExecutionSummary> TranslatePendingChunksAsync(
        CoreLongDocumentTranslationService coreLongDocumentService,
        LongDocumentTranslationCheckpoint checkpoint,
        Language from,
        Language to,
        string serviceId,
        Action<string>? onProgress,
        CancellationToken cancellationToken,
        TranslationCacheService? cacheService = null,
        System.IProgress<LongDocumentTranslationProgress>? progress = null)
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
                    var cached = await cacheService.TryGetAsync(serviceId, from, to, hash, cancellationToken).ConfigureAwait(false);
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
                        BoundingBox = metadata.BoundingBox,
                        TextStyle = metadata.TextStyle
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
        var retryOptions = CreateCoreTranslationOptions(
            serviceId,
            from,
            to,
            enableOcrFallback: false,
            retryConcurrency,
            retryFormulaFontPattern,
            retryFormulaCharPattern,
            retryCustomPrompt,
            progress);
        var retryResult = await coreLongDocumentService.TranslateAsync(retrySource, retryOptions, cancellationToken).ConfigureAwait(false);

        foreach (var translatedBlock in retryResult.Pages.SelectMany(page => page.Blocks))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!indexByRetryBlockId.TryGetValue(translatedBlock.SourceBlockId, out var chunkIndex))
            {
                continue;
            }

            onProgress?.Invoke($"Translating chunk {chunkIndex + 1}/{checkpoint.SourceChunks.Count}...");

            if (metadataByChunkIndex.TryGetValue(chunkIndex, out var metadata))
                metadata.RetryCount = Math.Max(metadata.RetryCount, translatedBlock.RetryCount);

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

    internal static LongDocumentTranslationOptions CreateCoreTranslationOptions(
        string serviceId,
        Language from,
        Language to,
        bool enableOcrFallback,
        int maxConcurrency,
        string? formulaFontPattern,
        string? formulaCharPattern,
        string? customPrompt,
        System.IProgress<LongDocumentTranslationProgress>? progress = null)
    {
        return new LongDocumentTranslationOptions
        {
            ServiceId = serviceId,
            FromLanguage = from,
            ToLanguage = to,
            EnableFormulaProtection = true,
            EnableOcrFallback = enableOcrFallback,
            EnableQualityFeedbackRetry = true,
            MaxRetriesPerBlock = 1,
            MaxConcurrency = maxConcurrency,
            FormulaFontPattern = formulaFontPattern,
            FormulaCharPattern = formulaCharPattern,
            CustomPrompt = customPrompt,
            Progress = progress
        };
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
                await _cacheService.SetAsync(serviceId, from, to, hash, source, translated, ct).ConfigureAwait(false);
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
                var cached = await _cacheService.TryGetAsync(serviceId, from, to, hash, ct).ConfigureAwait(false);
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

    private static IDocumentExportService ResolveExportService(
        string? sourceFilePath,
        PdfExportMode pdfExportMode = PdfExportMode.ContentStreamReplacement)
    {
        var ext = Path.GetExtension(sourceFilePath)?.ToLowerInvariant();
        return ext switch
        {
            ".pdf" => pdfExportMode switch
            {
                PdfExportMode.ContentStreamReplacement => new MuPdfExportService(),
                _ => new PdfExportService(),
            },
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
        DocumentOutputMode outputMode = DocumentOutputMode.Monolingual,
        PdfExportMode pdfExportMode = PdfExportMode.ContentStreamReplacement)
    {
        ValidateCheckpointOrThrow(checkpoint);

        var succeededCount = checkpoint.TranslatedChunks.Count;
        if (succeededCount == 0)
        {
            throw new InvalidOperationException("Translation failed for all chunks.");
        }

        onProgress?.Invoke("Generating output document...");

        var exportService = ResolveExportService(checkpoint.SourceFilePath, pdfExportMode);
        var exportResult = exportService.Export(checkpoint, checkpoint.SourceFilePath!, outputPath, outputMode);

        if (exportResult.BackfillMetrics != null)
        {
            qualityReport = qualityReport with { BackfillMetrics = exportResult.BackfillMetrics };
        }

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

    /// <summary>
    /// Legacy helper that calls the LLM to identify difficult words in source-fallback blocks.
    /// The default PDF export path no longer invokes or renders these annotations.
    /// </summary>
    private static async Task AnnotateSourceFallbackBlocksAsync(
        LongDocumentTranslationCheckpoint checkpoint,
        string serviceId,
        Language targetLanguage,
        CancellationToken ct)
    {
        var fallbackChunkIndexes = checkpoint.FailedChunkIndexes
            .Where(i => !checkpoint.TranslatedChunks.ContainsKey(i)
                && i >= 0 && i < checkpoint.SourceChunks.Count
                && !string.IsNullOrWhiteSpace(checkpoint.SourceChunks[i]))
            .ToList();

        if (fallbackChunkIndexes.Count == 0)
            return;

        var annotations = new Dictionary<int, IReadOnlyList<WordAnnotation>>();

        // Process each fallback block individually to keep responses focused
        foreach (var chunkIndex in fallbackChunkIndexes)
        {
            ct.ThrowIfCancellationRequested();
            var sourceText = checkpoint.SourceChunks[chunkIndex];
            if (sourceText.Length < 10) // Skip very short texts
                continue;

            try
            {
                var prompt = $"For the following English text, identify up to 8 uncommon or technical words " +
                    $"that a {targetLanguage.GetDisplayName()} reader might not know. " +
                    $"For each word, provide a very short translation (1-3 characters preferred). " +
                    $"Return ONLY a JSON array: [{{\"word\": \"example\", \"translation\": \"示例\"}}]. " +
                    $"Skip common words (the, is, have, with, for, from, etc.). " +
                    $"If no difficult words, return [].";

                var request = new TranslationRequest
                {
                    Text = sourceText,
                    FromLanguage = Language.English,
                    ToLanguage = targetLanguage,
                    CustomPrompt = prompt
                };

                var result = await TranslationManagerService.Instance.Manager
                    .TranslateAsync(request, ct, serviceId);

                var parsed = ParseWordAnnotations(result.TranslatedText);
                if (parsed.Count > 0)
                {
                    annotations[chunkIndex] = parsed;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LongDoc] Word annotation failed for chunk {chunkIndex}: {ex.Message}");
                // Non-critical: skip annotation for this block
            }
        }

        if (annotations.Count > 0)
        {
            checkpoint.WordAnnotations = annotations;
        }
    }

    internal static List<WordAnnotation> ParseWordAnnotations(string llmResponse)
    {
        var result = new List<WordAnnotation>();
        if (string.IsNullOrWhiteSpace(llmResponse))
            return result;

        try
        {
            // Extract JSON array from response (LLM might wrap it in markdown code blocks)
            var text = llmResponse.Trim();
            var jsonStart = text.IndexOf('[');
            var jsonEnd = text.LastIndexOf(']');
            if (jsonStart < 0 || jsonEnd <= jsonStart)
                return result;

            var json = text[jsonStart..(jsonEnd + 1)];
            var items = System.Text.Json.JsonSerializer.Deserialize<List<WordAnnotationDto>>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (items is null)
                return result;

            foreach (var item in items)
            {
                if (!string.IsNullOrWhiteSpace(item.Word) && !string.IsNullOrWhiteSpace(item.Translation))
                {
                    result.Add(new WordAnnotation(item.Word.Trim(), item.Translation.Trim()));
                }
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            Debug.WriteLine($"[LongDoc] Failed to parse word annotations JSON: {ex.Message}");
        }

        return result;
    }

    private sealed record WordAnnotationDto
    {
        public string? Word { get; init; }
        public string? Translation { get; init; }
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

    private static readonly Regex FormulaHeuristicRegex = new(@"(\$[^$]+\$|\\([^)]+\\)|\\[[^\]]+\\]|\b\w+\s*=\s*[-+*/^()\w\u221A]+)", RegexOptions.Compiled);

    private static Task<SourceDocument> BuildSourceDocumentBasicAsync(LongDocumentInputMode mode, string input, string? pageRange = null)
    {
        if (!File.Exists(input))
        {
            throw new FileNotFoundException("Source file not found.", input);
        }

        if (mode is LongDocumentInputMode.PlainText or LongDocumentInputMode.Markdown)
        {
            return Task.FromResult(BuildSourceDocumentFromTextFile(input));
        }

        return BuildSourceDocumentFromPdfAsync(input, pageRange);
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

    private static Task<SourceDocument> BuildSourceDocumentFromPdfAsync(string input, string? pageRange = null)
    {
        return Task.Run(() =>
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
        });
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
            return await BuildSourceDocumentBasicAsync(mode, input, pageRange).ConfigureAwait(false);
        }

        if (!File.Exists(input))
        {
            throw new FileNotFoundException("Source file not found.", input);
        }

        var strategy = GetLayoutDetectionStrategy();

        // For Auto mode, check if ONNX is available; if not, fall back to async heuristic
        if (layoutDetection == LayoutDetectionMode.Auto && !strategy.IsOnnxDownloaded)
        {
            return await BuildSourceDocumentBasicAsync(mode, input, pageRange).ConfigureAwait(false);
        }

        onProgress?.Invoke("Analyzing page layouts with ML model...");

        return await Task.Run(async () =>
        {
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
                        visionEndpoint, visionApiKey, visionModel, ct).ConfigureAwait(false);

                    if (enhancedBlocks.Count > 0)
                    {
                        // ML-driven blocks already carry correct BlockIds and IsFormulaLike flags
                        // (set by LayoutDetectionStrategy.ExtractBlocksByMlRegions → GroupWordsIntoBlocks).
                        pages.Add(new SourceDocumentPage
                        {
                            PageNumber = page.Number,
                            IsScanned = false,
                            Blocks = enhancedBlocks.Select(eb => eb.Block).ToList()
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
        }, ct).ConfigureAwait(false);
    }

    // --- PDF export methods removed; see PdfExportService ---

    private static IEnumerable<SourceDocumentBlock> ExtractLayoutBlocksFromPage(PdfPigPage page)
    {
        var pageWidth = (double)page.Width;
        var allWords = page.GetWords()
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .ToList();

        // Separate rotated/vertical words from normal words based on PdfPig's orientation signal.
        // This avoids partially translating rotated sidebars based on shape heuristics.
        var rotatedWords = new List<Word>();
        var normalWords = new List<Word>();
        foreach (var word in allWords)
        {
            if (word.TextOrientation != TextOrientation.Horizontal)
            {
                rotatedWords.Add(word);
            }
            else
            {
                normalWords.Add(word);
            }
        }

        var words = normalWords
            .OrderByDescending(word => word.BoundingBox.Top)
            .ThenBy(word => word.BoundingBox.Left)
            .ToList();

        if (words.Count == 0 && rotatedWords.Count == 0)
        {
            yield break;
        }

        var medianWordHeight = words.Count > 0
            ? words
                .Select(w => Math.Max(1d, w.BoundingBox.Height))
                .OrderBy(h => h)
                .Skip(words.Count / 2)
                .FirstOrDefault()
            : 10d;

        var sameLineThreshold = Math.Max(2.5, medianWordHeight * 0.35);
        // Lowered from 1.8 to 1.3 — more aggressive paragraph splitting reduces
        // long merged paragraphs that overflow their bounding boxes in PDF output.
        var paragraphGapThreshold = Math.Max(8, medianWordHeight * 1.3);

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

        // Split lines at large intra-line gaps to preserve column structure
        // (e.g., author grids where names sit at the same Y separated by large gaps)
        lines = SplitLinesAtColumnGaps(lines, medianWordHeight, pageWidth);

        var orderedLines = OrderLinesByLayout(lines, Convert.ToDecimal(page.Width));
        var paragraphs = BuildParagraphsWithGridCellMerging(orderedLines, paragraphGapThreshold, sameLineThreshold);
        var layoutProfile = BuildLayoutProfile(orderedLines, (double)page.Width, (double)page.Height);

        var blockIndex = 0;
        for (var i = 0; i < paragraphs.Count; i++)
        {
            var linesInBlock = paragraphs[i];
            var left = linesInBlock.Min(l => l.Left);
            var right = linesInBlock.Max(l => l.Right);
            var top = linesInBlock.Max(l => l.Top);
            var bottom = linesInBlock.Min(l => l.Bottom);

            // Combined pass: collect font names, text style, and formula character data from letters
            var (blockFontNames, textStyle, formulaChars) = ExtractBlockLetterData(page, linesInBlock, left, right, top, bottom);

            // Character-level formula detection via CharacterParagraphBuilder
            var (charProtectedText, charTokens) = BuildCharacterLevelProtection(page, left, right, top, bottom);

            var (blockText, blockFallbackText) = BuildBlockText(page, linesInBlock, left, right, top, bottom, formulaChars, charProtectedText);
            if (string.IsNullOrWhiteSpace(blockText))
            {
                continue;
            }

            var regionType = InferRegionType(layoutProfile, left, right, top, bottom, blockText);
            var type = regionType == LayoutRegionType.TableLike
                ? SourceBlockType.TableCell
                : GuessBlockType(blockText);
            var regionTag = regionType switch
            {
                LayoutRegionType.Header => "header",
                LayoutRegionType.Footer => "footer",
                LayoutRegionType.LeftColumn => "left",
                LayoutRegionType.RightColumn => "right",
                LayoutRegionType.TableLike => "table",
                _ => "body"
            };

            blockIndex++;
            yield return new SourceDocumentBlock
            {
                BlockId = $"p{page.Number}-{regionTag}-b{blockIndex}",
                BlockType = type,
                Text = blockText,
                FallbackText = blockFallbackText,
                IsFormulaLike = type == SourceBlockType.Formula,
                BoundingBox = new BlockRect(left, bottom, Math.Max(1, right - left), Math.Max(1, top - bottom)),
                DetectedFontNames = blockFontNames.Count > 0 ? blockFontNames : null,
                TextStyle = textStyle,
                FormulaCharacters = formulaChars,
                CharacterLevelProtectedText = charProtectedText,
                CharacterLevelTokens = charTokens
            };
        }

        // Emit rotated sidebar text as separate blocks with RotationAngle != 0 (skipped during backfill)
        if (rotatedWords.Count > 0)
        {
            foreach (var orientationGroup in rotatedWords.GroupBy(w => w.TextOrientation))
            {
                var rotationAngle = orientationGroup.Key switch
                {
                    TextOrientation.Rotate90 => 90,
                    TextOrientation.Rotate270 => -90,
                    TextOrientation.Rotate180 => 180,
                    _ => -90
                };

                // Group rotated words by horizontal proximity (they share similar X positions)
                var rotatedGroups = new List<List<Word>>();
                var sortedRotated = orientationGroup.OrderBy(w => w.BoundingBox.Left).ThenByDescending(w => w.BoundingBox.Top).ToList();

                foreach (var word in sortedRotated)
                {
                    var added = false;
                    foreach (var group in rotatedGroups)
                    {
                        var groupLeft = group.Min(w => w.BoundingBox.Left);
                        var groupRight = group.Max(w => w.BoundingBox.Right);
                        // Words in the same rotated text block share similar X coordinates
                        if (Math.Abs(word.BoundingBox.Left - groupLeft) < medianWordHeight * 2 ||
                            Math.Abs(word.BoundingBox.Right - groupRight) < medianWordHeight * 2)
                        {
                            group.Add(word);
                            added = true;
                            break;
                        }
                    }

                    if (!added)
                    {
                        rotatedGroups.Add([word]);
                    }
                }

                foreach (var group in rotatedGroups)
                {
                    // Sort bottom-to-top for rotated text (read order for -90° rotation)
                    var sorted = rotationAngle switch
                    {
                        90 => group.OrderByDescending(w => w.BoundingBox.Top).ToList(),
                        180 => group.OrderByDescending(w => w.BoundingBox.Right).ToList(),
                        _ => group.OrderBy(w => w.BoundingBox.Bottom).ToList()
                    };
                    var blockText = string.Join(" ", sorted.Select(w => w.Text)).Trim();
                    if (string.IsNullOrWhiteSpace(blockText))
                    {
                        continue;
                    }

                    var left = sorted.Min(w => w.BoundingBox.Left);
                    var right = sorted.Max(w => w.BoundingBox.Right);
                    var top = sorted.Max(w => w.BoundingBox.Top);
                    var bottom = sorted.Min(w => w.BoundingBox.Bottom);

                    blockIndex++;
                    yield return new SourceDocumentBlock
                    {
                        BlockId = $"p{page.Number}-sidebar-b{blockIndex}",
                        BlockType = SourceBlockType.Paragraph,
                        Text = blockText,
                        IsFormulaLike = false,
                        BoundingBox = new BlockRect(left, bottom, Math.Max(1, right - left), Math.Max(1, top - bottom)),
                        TextStyle = new BlockTextStyle
                        {
                            FontSize = Math.Clamp(right - left, 6, 12), // Rotated: width ≈ font size
                            RotationAngle = rotationAngle
                        }
                    };
                }
            }
        }
    }

    /// <summary>
    /// Groups a pre-filtered list of horizontal words into <see cref="SourceDocumentBlock"/>s
    /// for a single layout region (e.g., one ML-detected bounding box).
    /// Unlike <see cref="ExtractLayoutBlocksFromPage"/>, this method does NOT apply
    /// column-gap splitting or multi-column ordering — the caller is responsible for
    /// constraining which words are passed in.
    /// </summary>
    /// <param name="regionWords">Horizontal words already filtered to this region.</param>
    /// <param name="page">PdfPig page for letter-level font/style extraction.</param>
    /// <param name="pageNumber">1-based page number for BlockId generation.</param>
    /// <param name="regionTag">Region tag embedded in the BlockId (e.g., "body", "title").</param>
    /// <param name="blockIndex">Counter incremented for each emitted block (shared across regions on the same page).</param>
    internal static List<SourceDocumentBlock> GroupWordsIntoBlocks(
        List<Word> regionWords,
        PdfPigPage page,
        int pageNumber,
        string regionTag,
        ref int blockIndex)
    {
        var result = new List<SourceDocumentBlock>();

        if (regionWords.Count == 0)
            return result;

        var medianWordHeight = regionWords
            .Select(w => Math.Max(1d, w.BoundingBox.Height))
            .OrderBy(h => h)
            .Skip(regionWords.Count / 2)
            .FirstOrDefault(defaultValue: 10d);

        var sameLineThreshold = Math.Max(2.5, medianWordHeight * 0.35);
        // Lowered from 1.8 to 1.3 — more aggressive paragraph splitting reduces
        // long merged paragraphs that overflow their bounding boxes in PDF output.
        var paragraphGapThreshold = Math.Max(8, medianWordHeight * 1.3);

        // Sort top-to-bottom, left-to-right
        var sorted = regionWords
            .OrderByDescending(w => w.BoundingBox.Top)
            .ThenBy(w => w.BoundingBox.Left)
            .ToList();

        // Group words into text lines by Y proximity
        var lines = new List<PdfTextLine>();
        foreach (var word in sorted)
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

        // Within a single ML region, columns are already delimited — no column-gap splitting needed.
        // Use simple top-to-bottom order instead of the multi-column OrderLinesByLayout heuristic.
        var paragraphs = BuildParagraphsWithGridCellMerging(lines, paragraphGapThreshold, sameLineThreshold);

        foreach (var linesInBlock in paragraphs)
        {
            var left = linesInBlock.Min(l => l.Left);
            var right = linesInBlock.Max(l => l.Right);
            var top = linesInBlock.Max(l => l.Top);
            var bottom = linesInBlock.Min(l => l.Bottom);

            var (blockFontNames, textStyle, formulaChars) = ExtractBlockLetterData(page, linesInBlock, left, right, top, bottom);

            // Character-level formula detection via CharacterParagraphBuilder
            var (charProtectedText, charTokens) = BuildCharacterLevelProtection(page, left, right, top, bottom);

            var (blockText, blockFallbackText) = BuildBlockText(page, linesInBlock, left, right, top, bottom, formulaChars, charProtectedText);
            if (string.IsNullOrWhiteSpace(blockText))
                continue;

            var type = GuessBlockType(blockText);

            blockIndex++;
            result.Add(new SourceDocumentBlock
            {
                BlockId = $"p{pageNumber}-{regionTag}-b{blockIndex}",
                BlockType = type,
                Text = blockText,
                FallbackText = blockFallbackText,
                IsFormulaLike = type == SourceBlockType.Formula,
                BoundingBox = new BlockRect(left, bottom, Math.Max(1, right - left), Math.Max(1, top - bottom)),
                DetectedFontNames = blockFontNames.Count > 0 ? blockFontNames : null,
                TextStyle = textStyle,
                FormulaCharacters = formulaChars,
                CharacterLevelProtectedText = charProtectedText,
                CharacterLevelTokens = charTokens
            });
        }

        return result;
    }

    internal readonly record struct SyntheticPdfLine(
        double Top,
        double Bottom,
        double Left,
        double Right,
        string Text,
        bool IsColumnSplitFragment = false);

    private static (string blockText, string? fallbackText) BuildBlockText(
        PdfPigPage page,
        IReadOnlyList<PdfTextLine> linesInBlock,
        double left,
        double right,
        double top,
        double bottom,
        BlockFormulaCharacters? formulaChars,
        string? characterLevelProtectedText)
    {
        var lineTexts = new List<string>(linesInBlock.Count);
        for (var i = 0; i < linesInBlock.Count; i++)
        {
            lineTexts.Add(linesInBlock[i].Text);
        }

        var fallbackText = string.Join("\n", lineTexts).Trim();

        if (!FormulaAwareTextReconstructor.ShouldUseLetterBasedBlockText(lineTexts, formulaChars, characterLevelProtectedText))
        {
            return (fallbackText, null);
        }

        var letters = ExtractLetterGeometry(page, left, right, top, bottom);
        if (letters.Count == 0)
        {
            return (fallbackText, null);
        }

        // First attempt: default threshold
        var reconstructed = FormulaAwareTextReconstructor.Reconstruct(letters);
        if (!string.IsNullOrWhiteSpace(reconstructed) &&
            FormulaAwareTextReconstructor.IsReconstructionQualityAcceptable(reconstructed, fallbackText))
        {
            var fallback = reconstructed != fallbackText ? fallbackText : null;
            return (reconstructed, fallback);
        }

        // Second attempt: lower threshold for tighter word-gap detection (preserves script markers)
        reconstructed = FormulaAwareTextReconstructor.Reconstruct(letters, wordGapScale: 0.5);
        if (!string.IsNullOrWhiteSpace(reconstructed) &&
            FormulaAwareTextReconstructor.IsReconstructionQualityAcceptable(reconstructed, fallbackText))
        {
            var fallback = reconstructed != fallbackText ? fallbackText : null;
            return (reconstructed, fallback);
        }

        // Final fallback: PdfPig's original text (correct spacing, no script markers)
        return (fallbackText, null);
    }

    private static List<LetterGeometry> ExtractLetterGeometry(
        PdfPigPage page,
        double left,
        double right,
        double top,
        double bottom)
    {
        var letters = new List<LetterGeometry>();
        foreach (var letter in page.Letters)
        {
            if (letter.TextOrientation != TextOrientation.Horizontal)
            {
                continue;
            }

            var box = letter.GlyphRectangle;
            if (box.Left < left - 1 || box.Right > right + 1 ||
                box.Bottom < bottom - 1.5 || box.Top > top + 1.5)
            {
                continue;
            }

            letters.Add(new LetterGeometry(
                Value: letter.Value,
                Left: box.Left,
                Right: box.Right,
                Bottom: box.Bottom,
                Top: box.Top,
                BaselineY: letter.StartBaseLine.Y,
                PointSize: letter.PointSize,
                FontName: StripSubsetPrefix(letter.FontName ?? string.Empty)));
        }

        return letters;
    }


    /// <summary>
    /// Shared regex from MathPatterns — single source of truth.
    /// </summary>
    private static readonly Regex MathFontRegex = new(
        MathPatterns.MathFontPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Combined single-pass extraction of font names, text style, and formula character data
    /// from PdfPig letters within a block's bounding region. This avoids iterating page.Letters
    /// multiple times per block.
    /// </summary>
    internal static (List<string> FontNames, BlockTextStyle? TextStyle, BlockFormulaCharacters? FormulaCharacters)
        ExtractBlockLetterData(
            PdfPigPage page, List<PdfTextLine> linesInBlock,
            double left, double right, double top, double bottom)
    {
        var fontNames = new List<string>();
        try
        {
            var fontSizes = new List<double>();
            var boldCount = 0;
            var italicCount = 0;
            var totalLetters = 0;
            var colorR = new List<double>();
            var colorG = new List<double>();
            var colorB = new List<double>();
            var formulaChars = new List<FormulaCharacterInfo>();
            var hasMathFont = false;

            foreach (var letter in page.Letters)
            {
                var lbox = letter.GlyphRectangle;
                if (lbox.Left < left - 1 || lbox.Right > right + 1 ||
                    lbox.Bottom < bottom - 1 || lbox.Top > top + 1)
                {
                    continue;
                }

                totalLetters++;

                // Font names — strip PDF subset prefix (e.g. "ABCDE+CMSY10" → "CMSY10")
                // Aligned with pdf2zh converter.py:196: font.split("+")[-1]
                var fontName = letter.FontName ?? string.Empty;
                var plusIdx = fontName.IndexOf('+');
                if (plusIdx >= 0 && plusIdx < fontName.Length - 1)
                    fontName = fontName[(plusIdx + 1)..];
                if (!string.IsNullOrWhiteSpace(fontName))
                {
                    fontNames.Add(fontName);
                }

                // Font size
                if (letter.PointSize > 0)
                {
                    fontSizes.Add(letter.PointSize);
                }

                // Bold/italic
                if (FontNameLooksBold(fontName))
                    boldCount++;
                if (FontNameLooksItalic(fontName))
                    italicCount++;

                // Color
                try
                {
                    var color = letter.Color;
                    if (color != null)
                    {
                        var rgb = color.ToRGBValues();
                        colorR.Add(rgb.r);
                        colorG.Add(rgb.g);
                        colorB.Add(rgb.b);
                    }
                }
                catch
                {
                    // PatternColor or unsupported color space — skip
                }

                // Formula character data
                var isMathFont = !string.IsNullOrWhiteSpace(fontName) && MathFontRegex.IsMatch(fontName);
                if (isMathFont)
                    hasMathFont = true;

                formulaChars.Add(new FormulaCharacterInfo(
                    Value: letter.Value,
                    FontName: fontName,
                    PointSize: letter.PointSize,
                    GlyphLeft: lbox.Left,
                    GlyphBottom: lbox.Bottom,
                    GlyphWidth: Math.Max(0, lbox.Width),
                    GlyphHeight: Math.Max(0, lbox.Height),
                    IsMathFont: isMathFont,
                    IsSubscript: false,
                    IsSuperscript: false));
            }

            if (totalLetters == 0)
                return (fontNames, null, null);

            // Median font size
            fontSizes.Sort();
            var medianFontSize = fontSizes.Count > 0
                ? fontSizes[fontSizes.Count / 2]
                : 0;
            if (medianFontSize > 0)
            {
                medianFontSize = Math.Round(medianFontSize * 2, MidpointRounding.AwayFromZero) / 2d;
            }

            // Majority vote for bold/italic
            var halfLetters = totalLetters / 2;
            var isBold = boldCount > halfLetters;
            var isItalic = italicCount > halfLetters;

            // Average color
            var avgR = colorR.Count > 0 ? (byte)Math.Clamp(colorR.Average() * 255, 0, 255) : (byte)0;
            var avgG = colorG.Count > 0 ? (byte)Math.Clamp(colorG.Average() * 255, 0, 255) : (byte)0;
            var avgB = colorB.Count > 0 ? (byte)Math.Clamp(colorB.Average() * 255, 0, 255) : (byte)0;

            // Alignment
            var blockWidth = Math.Max(1, right - left);
            var alignment = DetectAlignment(linesInBlock, left, blockWidth);

            // Line spacing
            var lineSpacing = 0d;
            var linePositions = new List<BlockLinePosition>();
            if (linesInBlock.Count > 1)
            {
                var baselines = linesInBlock
                    .OrderByDescending(l => l.Top)
                    .Select(l => l.Bottom)
                    .ToList();

                var gaps = new List<double>();
                for (var g = 0; g < baselines.Count - 1; g++)
                {
                    var gap = Math.Abs(baselines[g] - baselines[g + 1]);
                    if (gap > 0.5) gaps.Add(gap);
                }

                if (gaps.Count > 0)
                {
                    gaps.Sort();
                    lineSpacing = gaps[gaps.Count / 2];
                }
            }

            foreach (var line in linesInBlock.OrderByDescending(l => l.Top))
            {
                linePositions.Add(new BlockLinePosition(line.Bottom, line.Left, line.Right));
            }

            var textStyle = new BlockTextStyle
            {
                FontSize = medianFontSize,
                IsBold = isBold,
                IsItalic = isItalic,
                ColorR = avgR,
                ColorG = avgG,
                ColorB = avgB,
                Alignment = alignment,
                LineSpacing = lineSpacing,
                LinePositions = linePositions.Count > 0 ? linePositions : null
            };

            // Build formula characters only if math fonts were found
            BlockFormulaCharacters? formulaData = null;
            if (hasMathFont)
            {
                // Post-pass: compute median baseline and size, then mark subscripts/superscripts
                var baselineYs = formulaChars
                    .Where(c => c.PointSize > 0)
                    .Select(c => c.GlyphBottom)
                    .OrderBy(y => y)
                    .ToList();
                var medianBaselineY = baselineYs.Count > 0 ? baselineYs[baselineYs.Count / 2] : 0;

                var sizeThreshold = medianFontSize * 0.8;
                var updatedChars = new List<FormulaCharacterInfo>(formulaChars.Count);
                foreach (var fc in formulaChars)
                {
                    var isSmall = fc.PointSize > 0 && fc.PointSize < sizeThreshold;
                    var isSub = isSmall && fc.GlyphBottom < medianBaselineY - 0.5;
                    var isSup = isSmall && fc.GlyphBottom > medianBaselineY + 0.5;
                    updatedChars.Add(fc with { IsSubscript = isSub, IsSuperscript = isSup });
                }

                formulaData = new BlockFormulaCharacters
                {
                    Characters = updatedChars,
                    MedianTextFontSize = medianFontSize,
                    MedianBaselineY = medianBaselineY,
                    HasMathFontCharacters = true
                };
            }

            return (fontNames, textStyle, formulaData);
        }
        catch
        {
            return (fontNames, null, null);
        }
    }

    /// <summary>
    /// Builds character-level formula protection using CharacterParagraphBuilder.
    /// Converts PdfPig Letters to lightweight CharInfo, runs character-level detection,
    /// and returns protected text with confidence-based two-tier output:
    /// - High-confidence groups → {vN} placeholder (hard protection)
    /// - Low-confidence groups → $reconstructed_latex$ (soft protection, LLM decides)
    /// </summary>
    internal static (string? ProtectedText, IReadOnlyList<FormulaToken>? Tokens)
        BuildCharacterLevelProtection(PdfPigPage page, double left, double right, double top, double bottom)
    {
        try
        {
            // Build CharInfo array from PdfPig Letters within the block bounds
            var charInfos = new List<CharInfo>();
            foreach (var letter in page.Letters)
            {
                var lbox = letter.GlyphRectangle;
                if (lbox.Left < left - 1 || lbox.Right > right + 1 ||
                    lbox.Bottom < bottom - 1 || lbox.Top > top + 1)
                    continue;

                var fontName = letter.FontName ?? string.Empty;
                var plusIdx = fontName.IndexOf('+');
                if (plusIdx >= 0 && plusIdx < fontName.Length - 1)
                    fontName = fontName[(plusIdx + 1)..];

                var tm = letter.TextOrientation switch
                {
                    TextOrientation.Rotate90 or TextOrientation.Rotate270
                        => TransformationMatrix.FromValues(0, 1, -1, 0, lbox.Left, lbox.Bottom),
                    _ => TransformationMatrix.Identity
                };

                charInfos.Add(new CharInfo
                {
                    Text = letter.Value,
                    CharacterCode = 0,
                    Cid = 0,
                    Font = null!,
                    FontName = fontName,
                    FontSize = letter.PointSize,
                    PointSize = letter.PointSize,
                    TextMatrix = tm,
                    CurrentTransformationMatrix = TransformationMatrix.Identity,
                    X0 = lbox.Left,
                    Y0 = lbox.Bottom,
                    X1 = lbox.Right,
                    Y1 = lbox.Top,
                });
            }

            if (charInfos.Count == 0)
                return (null, null);

            var charResult = CharacterParagraphBuilder.Build(charInfos);

            if (charResult.AllFormulaGroups.Count == 0)
                return (null, null);

            // Determine confidence per FormulaVariableGroup and build two-tier output
            var hardTokens = new List<FormulaToken>();
            var hardCounter = 0;

            // Build a mapping from original {vN} → replacement text for each group
            var groupReplacements = new Dictionary<int, string>();

            foreach (var g in charResult.AllFormulaGroups)
            {
                // Min confidence across characters in the group
                var groupConfidence = CharacterParagraphBuilder.FormulaConfidence.High;
                foreach (var ch in g.Characters)
                {
                    var conf = CharacterParagraphBuilder.GetFormulaConfidence(ch, 0, 1);
                    if (conf < groupConfidence) groupConfidence = conf;
                }

                if (groupConfidence == CharacterParagraphBuilder.FormulaConfidence.High)
                {
                    // Hard protection: {vN} placeholder
                    var raw = string.Concat(g.Characters.Select(c => c.Text));
                    var placeholder = $"{{v{hardCounter}}}";
                    hardTokens.Add(new FormulaToken(FormulaTokenType.InlineMath, raw, placeholder, raw));
                    groupReplacements[g.Index] = placeholder;
                    hardCounter++;
                }
                else
                {
                    // Soft protection: reconstruct as $latex$
                    var charTextInfos = g.Characters.Select(c => new CharTextInfo(
                        c.Text, c.PointSize, c.Y0,
                        MathFontRegex.IsMatch(StripSubsetPrefix(c.FontName))
                    )).ToList();
                    var latex = FormulaLatexReconstructor.ReconstructLatex(charTextInfos);
                    groupReplacements[g.Index] = $"${latex}$";
                }
            }

            // Rebuild paragraph texts with the two-tier replacements
            var paragraphTexts = new List<string>();
            foreach (var para in charResult.Paragraphs)
            {
                var text = para.ProtectedText;
                // Replace each {vN} with the appropriate replacement (hard or soft)
                foreach (var (originalIndex, replacement) in groupReplacements)
                {
                    text = text.Replace($"{{v{originalIndex}}}", replacement);
                }
                paragraphTexts.Add(text);
            }

            // Renumber hard placeholders to be sequential {v0}, {v1}, ...
            var protectedText = string.Join("\n", paragraphTexts);

            return (protectedText, hardTokens.Count > 0 ? hardTokens : null);
        }
        catch
        {
            // Character-level analysis is best-effort — fall back to regex on any error
            return (null, null);
        }
    }

    private static string StripSubsetPrefix(string fontName)
    {
        var plusIdx = fontName.IndexOf('+');
        return plusIdx >= 0 && plusIdx < fontName.Length - 1 ? fontName[(plusIdx + 1)..] : fontName;
    }

    internal static bool FontNameLooksBold(string fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
        {
            return false;
        }

        return fontName.Contains("Bold", StringComparison.OrdinalIgnoreCase) ||
               fontName.Contains("Black", StringComparison.OrdinalIgnoreCase) ||
               fontName.Contains("Heavy", StringComparison.OrdinalIgnoreCase) ||
               fontName.Contains("SemiBold", StringComparison.OrdinalIgnoreCase) ||
               fontName.Contains("Semibold", StringComparison.OrdinalIgnoreCase) ||
               fontName.Contains("Demi", StringComparison.OrdinalIgnoreCase) ||
               fontName.Contains("CMBX", StringComparison.OrdinalIgnoreCase) ||
               fontName.Contains("CMSSBX", StringComparison.OrdinalIgnoreCase) ||
               fontName.EndsWith("-B", StringComparison.OrdinalIgnoreCase) ||
               fontName.EndsWith("#B", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool FontNameLooksItalic(string fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
        {
            return false;
        }

        return fontName.Contains("Italic", StringComparison.OrdinalIgnoreCase) ||
               fontName.Contains("Oblique", StringComparison.OrdinalIgnoreCase) ||
               fontName.Contains("Slanted", StringComparison.OrdinalIgnoreCase) ||
               fontName.Contains("CMTI", StringComparison.OrdinalIgnoreCase) ||
               fontName.Contains("CMSL", StringComparison.OrdinalIgnoreCase) ||
               fontName.EndsWith("-I", StringComparison.OrdinalIgnoreCase) ||
               fontName.EndsWith("#I", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Detects text alignment from line positions within a block.
    /// Compares line left edges and right edges to determine L/C/R alignment.
    /// </summary>
    private static Easydict.TranslationService.LongDocument.TextAlignment DetectAlignment(List<PdfTextLine> lines, double blockLeft, double blockWidth)
    {
        if (lines.Count <= 1)
            return Easydict.TranslationService.LongDocument.TextAlignment.Left;

        const double tolerance = 3.0; // points

        var leftAligned = lines.Count(l => Math.Abs(l.Left - blockLeft) <= tolerance);
        var rightAligned = lines.Count(l => Math.Abs(l.Right - (blockLeft + blockWidth)) <= tolerance);
        var centerAligned = lines.Count(l =>
            Math.Abs(l.CenterX - (blockLeft + blockWidth / 2)) <= tolerance);

        if (centerAligned > lines.Count / 2)
            return Easydict.TranslationService.LongDocument.TextAlignment.Center;
        if (rightAligned > leftAligned && rightAligned > lines.Count / 2)
            return Easydict.TranslationService.LongDocument.TextAlignment.Right;

        return Easydict.TranslationService.LongDocument.TextAlignment.Left;
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
            // For small sets (e.g., title/author grids), preserve left-to-right ordering within the same row.
            return lines.OrderByDescending(l => l.Top).ThenBy(l => l.Left).ToList();
        }

        var width = (double)pageWidth;
        var mid = width / 2;
        var leftLines = lines.Where(l => l.CenterX < mid * 0.92).ToList();
        var rightLines = lines.Where(l => l.CenterX > mid * 1.08).ToList();

        // If many rows have multiple aligned cells at the same Y (common for author grids),
        // prefer row-wise ordering: same row left->right, then top->bottom.
        if (LooksLikeRowAlignedGrid(lines, width))
        {
            return OrderLinesRowWise(lines);
        }

        var isTwoColumn = leftLines.Count >= lines.Count * 0.25 && rightLines.Count >= lines.Count * 0.25;
        if (!isTwoColumn)
        {
            return lines.OrderByDescending(l => l.Top).ThenBy(l => l.Left).ToList();
        }

        var ordered = new List<PdfTextLine>(lines.Count);
        ordered.AddRange(leftLines.OrderByDescending(l => l.Top).ThenBy(l => l.Left));
        ordered.AddRange(rightLines.OrderByDescending(l => l.Top).ThenBy(l => l.Left));

        var remaining = lines.Except(ordered).OrderByDescending(l => l.Top).ThenBy(l => l.Left);
        ordered.AddRange(remaining);
        return ordered;
    }

    private static bool LooksLikeRowAlignedGrid(IReadOnlyList<PdfTextLine> lines, double pageWidth)
    {
        if (lines.Count < 6)
        {
            return false;
        }

        var heights = lines.Select(l => Math.Max(1d, l.Top - l.Bottom)).OrderBy(v => v).ToList();
        var medianHeight = heights[heights.Count / 2];
        var rowTol = Math.Max(2.5, medianHeight * 0.35);

        var rows = GroupIntoRows(lines, rowTol);
        if (rows.Count < 3)
        {
            return false;
        }

        var multiCellRows = rows.Count(r => r.Count >= 2);
        if (multiCellRows < 2)
        {
            return false;
        }

        var wideRows = rows
            .Where(r => r.Count >= 2)
            .Count(r => (r.Max(x => x.Right) - r.Min(x => x.Left)) > pageWidth * 0.45);

        var ratio = (double)multiCellRows / Math.Max(1, rows.Count);
        return ratio >= 0.20 && wideRows >= 1;
    }

    private static List<PdfTextLine> OrderLinesRowWise(IReadOnlyList<PdfTextLine> lines)
    {
        var heights = lines.Select(l => Math.Max(1d, l.Top - l.Bottom)).OrderBy(v => v).ToList();
        var medianHeight = heights[heights.Count / 2];
        var rowTol = Math.Max(2.5, medianHeight * 0.35);
        var rows = GroupIntoRows(lines, rowTol);
        var ordered = new List<PdfTextLine>(lines.Count);
        foreach (var row in rows)
        {
            ordered.AddRange(row.OrderBy(l => l.Left));
        }
        return ordered;
    }

    private static List<List<PdfTextLine>> GroupIntoRows(IReadOnlyList<PdfTextLine> lines, double rowTolerance)
    {
        var rows = new List<List<PdfTextLine>>();
        foreach (var line in lines.OrderByDescending(l => l.Top).ThenBy(l => l.Left))
        {
            var placed = false;
            foreach (var row in rows)
            {
                var rowTop = row[0].Top;
                if (Math.Abs(rowTop - line.Top) <= rowTolerance)
                {
                    row.Add(line);
                    placed = true;
                    break;
                }
            }
            if (!placed)
            {
                rows.Add([line]);
            }
        }
        return rows;
    }

    private static List<List<PdfTextLine>> BuildParagraphs(
        IReadOnlyList<PdfTextLine> lines,
        double paragraphGapThreshold,
        double sameRowThreshold)
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
            // If two items share nearly the same Y (same baseline row), treat them as separate cells.
            // This is critical for author grids / multi-column rows: left->right cells must not be merged.
            var sameRow = Math.Abs(prev.Top - line.Top) <= sameRowThreshold;
            var gap = Math.Abs(prev.Bottom - line.Top);
            var horizontalOffset = Math.Abs(prev.Left - line.Left);
            var shouldMergeFormulaContinuation = ShouldMergeFormulaContinuation(prev.Text, line.Text, gap, paragraphGapThreshold, sameRow);
            var shouldSplit =
                !shouldMergeFormulaContinuation &&
                (sameRow ||
                (prev.IsColumnSplitFragment && !sameRow) ||
                gap > paragraphGapThreshold ||
                horizontalOffset > Math.Max(30, prev.Width * 0.6));

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

    internal static List<List<PdfTextLine>> BuildParagraphsWithGridCellMerging(
        IReadOnlyList<PdfTextLine> lines,
        double paragraphGapThreshold,
        double sameRowThreshold)
    {
        if (lines.Count == 0)
        {
            return [];
        }

        var rows = GroupIntoRows(lines, sameRowThreshold)
            .Select(row => row.OrderBy(l => l.Left).ToList())
            .ToList();

        var paragraphs = new List<List<PdfTextLine>>();
        var nonGridBuffer = new List<PdfTextLine>();

        var index = 0;
        while (index < rows.Count)
        {
            if (TryDetectGridWindow(rows, index, paragraphGapThreshold, out var gridEndExclusive))
            {
                if (nonGridBuffer.Count > 0)
                {
                    paragraphs.AddRange(BuildParagraphs(nonGridBuffer, paragraphGapThreshold, sameRowThreshold));
                    nonGridBuffer.Clear();
                }

                var gridRows = rows.GetRange(index, gridEndExclusive - index);
                paragraphs.AddRange(BuildGridCellParagraphs(gridRows, paragraphGapThreshold));
                index = gridEndExclusive;
                continue;
            }

            nonGridBuffer.AddRange(rows[index]);
            index++;
        }

        if (nonGridBuffer.Count > 0)
        {
            paragraphs.AddRange(BuildParagraphs(nonGridBuffer, paragraphGapThreshold, sameRowThreshold));
        }

        return paragraphs;
    }

    private static bool TryDetectGridWindow(
        IReadOnlyList<List<PdfTextLine>> rows,
        int startIndex,
        double paragraphGapThreshold,
        out int endExclusive)
    {
        endExclusive = startIndex;
        if (startIndex < 0 || startIndex >= rows.Count || rows[startIndex].Count < 2)
        {
            return false;
        }

        var maxVerticalGap = paragraphGapThreshold * 1.2;
        var index = startIndex;
        while (index < rows.Count)
        {
            if (index > startIndex)
            {
                var previous = rows[index - 1];
                var current = rows[index];
                var prevBottom = previous.Min(l => l.Bottom);
                var currentTop = current.Max(l => l.Top);
                var verticalGap = Math.Abs(prevBottom - currentTop);
                if (verticalGap > maxVerticalGap)
                {
                    break;
                }
            }

            index++;
        }

        var candidateRows = rows.Skip(startIndex).Take(index - startIndex).ToList();
        var multiCellRows = candidateRows.Count(row => row.Count >= 2);
        if (candidateRows.Count < 2 || multiCellRows < 2)
        {
            return false;
        }

        var multiCellRatio = multiCellRows / (double)candidateRows.Count;
        if (multiCellRatio < 0.5)
        {
            return false;
        }

        if (!HasStableGridColumnAnchors(candidateRows))
        {
            return false;
        }

        endExclusive = index;
        return true;
    }

    private static bool HasStableGridColumnAnchors(IReadOnlyList<List<PdfTextLine>> candidateRows)
    {
        var multiCellRows = candidateRows
            .Where(row => row.Count >= 2)
            .Select(row => row.OrderBy(line => line.Left).ToList())
            .ToList();
        if (multiCellRows.Count < 2)
        {
            return false;
        }

        var anchorGroup = multiCellRows
            .GroupBy(row => row.Count)
            .Where(group => group.Count() >= 2)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault();
        if (anchorGroup is null)
        {
            return false;
        }

        var stableRows = anchorGroup.ToList();
        var cellWidths = stableRows
            .SelectMany(row => row.Select(line => Math.Max(1d, line.Width)))
            .OrderBy(width => width)
            .ToList();
        var medianCellWidth = cellWidths.Count > 0 ? cellWidths[cellWidths.Count / 2] : 30d;
        var anchorTolerance = Math.Max(10d, Math.Min(24d, medianCellWidth * 0.25d));

        var stableColumns = 0;
        for (var columnIndex = 0; columnIndex < anchorGroup.Key; columnIndex++)
        {
            var anchors = stableRows
                .Select(row => row[columnIndex].Left)
                .OrderBy(anchor => anchor)
                .ToList();
            if (anchors[^1] - anchors[0] <= anchorTolerance)
            {
                stableColumns++;
            }
        }

        return stableColumns >= 2;
    }

    private static bool ShouldMergeFormulaContinuation(
        string previousText,
        string currentText,
        double verticalGap,
        double paragraphGapThreshold,
        bool sameRow)
    {
        if (!sameRow && verticalGap > Math.Max(6d, paragraphGapThreshold * 0.6d))
        {
            return false;
        }

        return FormulaAwareTextReconstructor.LooksLikeFormulaContinuationText(currentText) ||
            FormulaAwareTextReconstructor.PreviousLineLikelyExpectsFormulaTail(previousText);
    }

    internal static List<List<string>> BuildParagraphTextsForTesting(
        IReadOnlyList<SyntheticPdfLine> lines,
        double paragraphGapThreshold,
        double sameRowThreshold)
    {
        var pdfLines = lines
            .Select(PdfTextLine.CreateSynthetic)
            .ToList();

        return BuildParagraphsWithGridCellMerging(pdfLines, paragraphGapThreshold, sameRowThreshold)
            .Select(paragraph => paragraph.Select(line => line.Text).ToList())
            .ToList();
    }

    private static List<List<PdfTextLine>> BuildGridCellParagraphs(
        IReadOnlyList<List<PdfTextLine>> gridRows,
        double paragraphGapThreshold)
    {
        var cells = new List<GridCellAccumulator>();
        var maxVerticalGap = paragraphGapThreshold * 1.2;

        foreach (var row in gridRows)
        {
            foreach (var segment in row.OrderBy(l => l.Left))
            {
                GridCellAccumulator? bestCell = null;
                var bestOverlap = 0d;
                foreach (var cell in cells)
                {
                    if (Math.Abs(cell.LastBottom - segment.Top) > maxVerticalGap)
                    {
                        continue;
                    }

                    var overlapLeft = Math.Max(cell.Left, segment.Left);
                    var overlapRight = Math.Min(cell.Right, segment.Right);
                    var overlapWidth = Math.Max(0, overlapRight - overlapLeft);
                    var denominator = Math.Max(1, Math.Min(cell.Width, segment.Width));
                    var overlapRatio = overlapWidth / denominator;
                    if (overlapRatio >= 0.35 && overlapRatio > bestOverlap)
                    {
                        bestOverlap = overlapRatio;
                        bestCell = cell;
                    }
                }

                if (bestCell is null)
                {
                    cells.Add(new GridCellAccumulator(segment));
                }
                else
                {
                    bestCell.Add(segment);
                }
            }
        }

        return cells
            .OrderByDescending(cell => cell.FirstTop)
            .ThenBy(cell => cell.FirstLeft)
            .Select(cell => cell.Segments.OrderByDescending(s => s.Top).ToList())
            .ToList();
    }

    /// <summary>
    /// Splits lines at large intra-line horizontal gaps to preserve column structure.
    /// For example, author grids in academic papers where names at the same Y coordinate
    /// are separated by large gaps should become separate lines (and thus separate blocks).
    /// </summary>
    private static List<PdfTextLine> SplitLinesAtColumnGaps(List<PdfTextLine> lines, double medianWordHeight, double pageWidth)
    {
        var result = new List<PdfTextLine>(lines.Count);

        foreach (var line in lines)
        {
            // Need at least 2 words to have a gap to split on
            if (line.Words.Count < 2)
            {
                result.Add(line);
                continue;
            }

            var sortedWords = line.Words.OrderBy(w => w.BoundingBox.Left).ToList();

            var wordBoxes = new List<(double Left, double Right)>(sortedWords.Count);
            for (var i = 0; i < sortedWords.Count; i++)
            {
                wordBoxes.Add((sortedWords[i].BoundingBox.Left, sortedWords[i].BoundingBox.Right));
            }

            var likelyMultiColumnLine =
                (line.Words.Count <= 6 && line.Width >= pageWidth * 0.45) ||
                line.Text.Contains('@');
            var splitIndices = FindColumnSplitIndices(wordBoxes, medianWordHeight, likelyMultiColumnLine);

            if (splitIndices.Count == 0)
            {
                result.Add(line);
                continue;
            }

            // Split into sub-lines at each split point
            var start = 0;
            foreach (var splitAfter in splitIndices)
            {
                var subLine = new PdfTextLine(line.Top);
                subLine.IsColumnSplitFragment = true;
                for (var i = start; i <= splitAfter; i++)
                {
                    subLine.Words.Add(sortedWords[i]);
                }
                result.Add(subLine.Normalize());
                start = splitAfter + 1;
            }

            // Add remaining words as last sub-line
            if (start < sortedWords.Count)
            {
                var lastSubLine = new PdfTextLine(line.Top);
                lastSubLine.IsColumnSplitFragment = true;
                for (var i = start; i < sortedWords.Count; i++)
                {
                    lastSubLine.Words.Add(sortedWords[i]);
                }
                result.Add(lastSubLine.Normalize());
            }
        }

        return result;
    }

    internal static IReadOnlyList<int> FindColumnSplitIndices(
        IReadOnlyList<(double Left, double Right)> wordBoxes,
        double medianWordHeight)
    {
        return FindColumnSplitIndices(wordBoxes, medianWordHeight, aggressive: true);
    }

    private static IReadOnlyList<int> FindColumnSplitIndices(
        IReadOnlyList<(double Left, double Right)> wordBoxes,
        double medianWordHeight,
        bool aggressive)
    {
        if (wordBoxes.Count < 2)
        {
            return [];
        }

        var gaps = new List<double>(wordBoxes.Count - 1);
        for (var i = 0; i < wordBoxes.Count - 1; i++)
        {
            var gap = wordBoxes[i + 1].Left - wordBoxes[i].Right;
            gaps.Add(Math.Max(0, gap));
        }

        var sortedGaps = gaps.OrderBy(g => g).ToList();
        var medianGap = sortedGaps[sortedGaps.Count / 2];
        var relativeMultiplier = aggressive ? 2.5 : 3.0;
        var gapThreshold = Math.Max(medianGap * relativeMultiplier, medianWordHeight * 1.5);
        var absoluteGapThreshold = aggressive
            ? Math.Max(28, medianWordHeight * 3)
            : Math.Max(50, medianWordHeight * 4);

        var splitIndices = new List<int>();
        for (var i = 0; i < gaps.Count; i++)
        {
            if (gaps[i] > gapThreshold || gaps[i] > absoluteGapThreshold)
            {
                splitIndices.Add(i);
            }
        }

        return splitIndices;
    }

    internal sealed class PdfTextLine(double top)
    {
        public double Top { get; } = top;
        public bool IsColumnSplitFragment { get; set; }
        public List<Word> Words { get; } = [];
        public double Left { get; private set; }
        public double Right { get; private set; }
        public double Bottom { get; private set; }
        public double Width => Right - Left;
        public double CenterX => Left + Width / 2;
        public string Text { get; private set; } = string.Empty;

        internal static PdfTextLine CreateSynthetic(SyntheticPdfLine line)
        {
            return new PdfTextLine(line.Top)
            {
                Bottom = line.Bottom,
                Left = line.Left,
                Right = line.Right,
                Text = line.Text,
                IsColumnSplitFragment = line.IsColumnSplitFragment
            };
        }

        public PdfTextLine Normalize()
        {
            var sorted = Words.OrderBy(w => w.BoundingBox.Left).ToList();
            Left = sorted.Min(w => w.BoundingBox.Left);
            Right = sorted.Max(w => w.BoundingBox.Right);
            Bottom = sorted.Min(w => w.BoundingBox.Bottom);
            Text = BuildAnnotatedText(sorted);
            return this;
        }

        /// <summary>
        /// Builds the line text, inserting _ or ^ signals before words that appear to be
        /// subscripts or superscripts relative to the line's median baseline.
        /// Consecutive sub/superscript words are grouped into _{group} / ^{group} notation,
        /// which both formula protection and SimplifyLatexMarkup understand.
        /// </summary>
        private static string BuildAnnotatedText(List<Word> sorted)
        {
            if (sorted.Count == 0)
                return string.Empty;

            // Single word: use letter-level PointSize analysis to detect intra-word
            // subscripts/superscripts — e.g. $h_t$ is often extracted as one PdfPig
            // Word with text "ht" where 't' has a smaller PointSize and lower baseline.
            if (sorted.Count == 1)
                return BuildAnnotatedTextFromLetters(sorted[0].Letters);

            // Compute the median word bottom (baseline proxy) and median word height.
            // These let us detect words at distinctly lower/higher positions (sub/super).
            var wordHeights = sorted.Select(w => w.BoundingBox.Height).OrderBy(h => h).ToList();
            var wordBottoms = sorted.Select(w => w.BoundingBox.Bottom).OrderBy(b => b).ToList();
            var medianHeight = wordHeights[wordHeights.Count / 2];
            var medianBottom = wordBottoms[wordBottoms.Count / 2];

            // Need a meaningful height to distinguish sizes; bail out if degenerate.
            if (medianHeight <= 0)
                return string.Join(" ", sorted.Select(w => w.Text));

            // A word is "small" if its bounding box height is noticeably less than the median.
            // A small word below the median baseline is a subscript; above is a superscript.
            var smallThreshold = medianHeight * 0.85;
            var posThreshold = Math.Max(0.3, medianHeight * 0.15); // aligned with BuildAnnotatedTextFromLetters

            // Prefer PointSize over BoundingBox.Height for size discrimination when available.
            // PointSize is the actual font size in pt, so even 't' at 7pt (subscript) vs 'h'
            // at 10pt is correctly classified even when their glyph bounding box heights happen
            // to be similar (both letters have ascenders).
            var wordPointSizes = sorted
                .Select(w => w.Letters.Count > 0 ? w.Letters.Average(l => l.PointSize) : 0.0)
                .ToList();
            var validPtSizes = wordPointSizes.Where(p => p > 0).OrderBy(p => p).ToList();
            var medianPointSize = validPtSizes.Count > 0 ? validPtSizes[validPtSizes.Count / 2] : 0.0;

            // Classify each word as normal, subscript, or superscript.
            // hasVariation[i] = true when a word has mixed PointSizes (fused base+subscript,
            // e.g. "x1" where 'x' is 10pt and '1' is 7pt). Such words are delegated to
            // BuildAnnotatedTextFromLetters rather than tagged as a whole-word subscript.
            var tags = new bool[sorted.Count]; // true = subscript
            var sups = new bool[sorted.Count]; // true = superscript
            var hasVariation = new bool[sorted.Count];
            for (var i = 0; i < sorted.Count; i++)
            {
                if (sorted[i].Letters.Count > 1)
                {
                    var ptSizes = sorted[i].Letters.Select(l => l.PointSize).Where(p => p > 0).ToList();
                    if (ptSizes.Count >= 2 && ptSizes.Max() / ptSizes.Min() >= 1.10)
                        hasVariation[i] = true;
                }

                var h = sorted[i].BoundingBox.Height;
                var bot = sorted[i].BoundingBox.Bottom;
                // Use PointSize when available; fall back to BoundingBox.Height otherwise.
                var isSmall = (medianPointSize > 0 && wordPointSizes[i] > 0)
                    ? wordPointSizes[i] < medianPointSize * 0.87
                    : h < smallThreshold;
                tags[i] = !hasVariation[i] && isSmall && bot < medianBottom - posThreshold; // isSub
                sups[i] = !hasVariation[i] && isSmall && bot > medianBottom + posThreshold; // isSup
            }

            var sb = new System.Text.StringBuilder();
            var idx = 0;
            while (idx < sorted.Count)
            {
                if (idx == 0)
                {
                    // First word never gets a connector prefix.
                    sb.Append(sorted[0].Text);
                    idx++;
                    continue;
                }

                if (!tags[idx] && !sups[idx])
                {
                    sb.Append(' ');
                    var w = sorted[idx];
                    // Fused base+subscript word (e.g. "x1"): delegate to letter-level annotation.
                    if (hasVariation[idx])
                        sb.Append(BuildAnnotatedTextFromLetters(w.Letters));
                    else
                        sb.Append(w.Text);
                    idx++;
                    continue;
                }

                // Start of a sub/super run — collect consecutive words of the same type.
                var isSub = tags[idx];
                var runEnd = idx;
                while (runEnd + 1 < sorted.Count
                    && ((isSub && tags[runEnd + 1]) || (!isSub && sups[runEnd + 1])))
                {
                    runEnd++;
                }

                // Concatenate all words in the run (no spaces between them).
                var runText = string.Concat(
                    Enumerable.Range(idx, runEnd - idx + 1).Select(k => sorted[k].Text));

                // Only annotate as sub/super if the run text looks like a mathematical token:
                // letters, digits, or basic operators (+, -, =, ., ,).
                // Footnote markers (†, ‡, *, §, ¶, etc.) are NOT math tokens — skip annotation
                // to avoid confusing the LLM with unparseable ^† signals that the formula
                // protection regex won't protect.
                if (!MathPatterns.IsMathToken(runText))
                {
                    sb.Append(' ').Append(runText);
                    idx = runEnd + 1;
                    continue;
                }

                // Emit single-char shorthand or braced group for multi-char.
                var signal = isSub ? '_' : '^';
                if (runText.Length == 1)
                    sb.Append(signal).Append(runText);
                else
                    sb.Append(signal).Append('{').Append(runText).Append('}');

                idx = runEnd + 1;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Detects sub/superscripts within a single PdfPig <see cref="Word"/> by comparing
        /// per-letter <see cref="Letter.PointSize"/> and <see cref="Letter.StartBaseLine"/>.
        /// This handles the common math-PDF case where the base letter and its subscript
        /// (e.g. 'h' and 't' in $h_t$) are fused into one Word by PdfPig's word extractor.
        /// </summary>
        private static string BuildAnnotatedTextFromLetters(IReadOnlyList<Letter> letters)
        {
            if (letters.Count == 0)
                return string.Empty;
            if (letters.Count == 1)
                return letters[0].Value;

            var pointSizes = letters.Select(l => l.PointSize).ToList();
            var validSizes = pointSizes.Where(s => s > 0).OrderBy(s => s).ToList();

            // No meaningful font-size variation → plain word, return as-is.
            if (validSizes.Count < 2 || validSizes[validSizes.Count - 1] / validSizes[0] < 1.10)
                return string.Concat(letters.Select(l => l.Value));

            var medianSize = validSizes[validSizes.Count / 2];
            var medianBaseline = letters
                .Select(l => l.StartBaseLine.Y)
                .OrderBy(y => y)
                .Skip(letters.Count / 2)
                .First();
            var posThreshold = Math.Max(0.5, medianSize * 0.15);

            var letterSubs = new bool[letters.Count];
            var letterSups = new bool[letters.Count];
            for (var i = 0; i < letters.Count; i++)
            {
                var ps = pointSizes[i];
                if (ps <= 0 || ps >= medianSize * 0.87)
                    continue;
                var baseline = letters[i].StartBaseLine.Y;
                letterSubs[i] = baseline < medianBaseline - posThreshold;
                letterSups[i] = baseline > medianBaseline + posThreshold;
            }

            var sb = new System.Text.StringBuilder();
            var idx = 0;
            while (idx < letters.Count)
            {
                if (idx == 0 || (!letterSubs[idx] && !letterSups[idx]))
                {
                    sb.Append(letters[idx].Value);
                    idx++;
                    continue;
                }

                var isSub = letterSubs[idx];
                var runEnd = idx;
                while (runEnd + 1 < letters.Count &&
                    ((isSub && letterSubs[runEnd + 1]) || (!isSub && letterSups[runEnd + 1])))
                    runEnd++;

                var runText = string.Concat(
                    Enumerable.Range(idx, runEnd - idx + 1).Select(k => letters[k].Value));

                if (!MathPatterns.IsMathToken(runText))
                {
                    sb.Append(runText);
                }
                else
                {
                    var signal = isSub ? '_' : '^';
                    sb.Append(runText.Length == 1
                        ? $"{signal}{runText}"
                        : $"{signal}{{{runText}}}");
                }

                idx = runEnd + 1;
            }

            return sb.ToString();
        }
    }

    private sealed class GridCellAccumulator
    {
        public GridCellAccumulator(PdfTextLine segment)
        {
            Segments.Add(segment);
            Left = segment.Left;
            Right = segment.Right;
            FirstLeft = segment.Left;
            FirstTop = segment.Top;
            LastBottom = segment.Bottom;
        }

        public List<PdfTextLine> Segments { get; } = [];
        public double Left { get; private set; }
        public double Right { get; private set; }
        public double Width => Right - Left;
        public double FirstTop { get; }
        public double FirstLeft { get; }
        public double LastBottom { get; private set; }

        public void Add(PdfTextLine segment)
        {
            Segments.Add(segment);
            Left = Math.Min(Left, segment.Left);
            Right = Math.Max(Right, segment.Right);
            LastBottom = segment.Bottom;
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

    internal static SourceBlockType GuessBlockType(string text)
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
            BlockIssues = MergeBlockIssues(previous.BlockIssues, current.BlockIssues),
            RetryMergeStrategy = "accumulate"
        };
    }

    private static IReadOnlyList<BackfillBlockIssue>? MergeBlockIssues(
        IReadOnlyList<BackfillBlockIssue>? previous,
        IReadOnlyList<BackfillBlockIssue>? current)
    {
        if (previous is null or { Count: 0 } && current is null or { Count: 0 })
        {
            return null;
        }

        var merged = new List<BackfillBlockIssue>();
        if (previous is { Count: > 0 })
            merged.AddRange(previous);
        if (current is { Count: > 0 })
            merged.AddRange(current);

        return merged.Count > 0 ? merged : null;
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
        if (string.IsNullOrWhiteSpace(checkpoint.SourceFilePath))
        {
            throw new InvalidOperationException("Checkpoint source file path is required for export.");
        }

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
