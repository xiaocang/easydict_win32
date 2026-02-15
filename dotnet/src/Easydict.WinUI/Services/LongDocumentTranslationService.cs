using System.Text;
using System.Text.RegularExpressions;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using CoreLongDocumentTranslationService = Easydict.TranslationService.LongDocument.LongDocumentTranslationService;

namespace Easydict.WinUI.Services;

public enum LongDocumentInputMode
{
    Manual,
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
    TableLike
}


public enum LayoutRegionSource
{
    Unknown,
    Heuristic,
    BlockIdFallback
}

public sealed class LongDocumentTranslationCheckpoint
{
    public required LongDocumentInputMode InputMode { get; init; }
    public string? SourcePdfPath { get; init; }
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
    public required int TotalChunks { get; init; }
    public required int SucceededChunks { get; init; }
    public required IReadOnlyList<int> FailedChunkIndexes { get; init; }
    public required LongDocumentQualityReport QualityReport { get; init; }
    public required LongDocumentTranslationCheckpoint Checkpoint { get; init; }
}

public sealed class LongDocumentTranslationService
{
    private sealed record RetryExecutionSummary(LongDocumentQualityReport? CoreQualityReport, int ReusedByCanonicalCount);
    private sealed record BackfillRenderingMetrics(
        int CandidateBlocks,
        int RenderedBlocks,
        int MissingBoundingBoxBlocks,
        int ShrinkFontBlocks,
        int TruncatedBlocks,
        int ObjectReplaceBlocks,
        int OverlayModeBlocks,
        int StructuredFallbackBlocks,
        IReadOnlyDictionary<int, BackfillPageMetrics>? PageMetrics)
    {
        public static BackfillRenderingMetrics Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, null);
    }

    private sealed class PageBackfillAccumulator
    {
        public int CandidateBlocks { get; set; }
        public int RenderedBlocks { get; set; }
        public int MissingBoundingBoxBlocks { get; set; }
        public int ShrinkFontBlocks { get; set; }
        public int TruncatedBlocks { get; set; }
        public int ObjectReplaceBlocks { get; set; }
        public int OverlayModeBlocks { get; set; }
        public int StructuredFallbackBlocks { get; set; }
    }

    private readonly CoreLongDocumentTranslationService _coreLongDocumentService = new();

    public async Task<LongDocumentTranslationResult> TranslateToPdfAsync(
        LongDocumentInputMode mode,
        string input,
        Language from,
        Language to,
        string outputPath,
        string serviceId,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var sourceDocument = BuildSourceDocument(mode, input);
        var sourcePdfPath = mode == LongDocumentInputMode.Pdf ? input : null;
        var hasAnySourceText = sourceDocument.Pages
            .SelectMany(page => page.Blocks)
            .Any(block => !string.IsNullOrWhiteSpace(block.Text));
        var hasScannedPages = sourceDocument.Pages.Any(page => page.IsScanned);

        if (!hasAnySourceText && !hasScannedPages)
        {
            throw new InvalidOperationException("No source text found for translation.");
        }

        onProgress?.Invoke("Building long-document IR...");

        var coreResult = await _coreLongDocumentService.TranslateAsync(sourceDocument, new LongDocumentTranslationOptions
        {
            ServiceId = serviceId,
            FromLanguage = from,
            ToLanguage = to,
            EnableFormulaProtection = true,
            EnableOcrFallback = true,
            MaxRetriesPerBlock = 1
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
            SourcePdfPath = sourcePdfPath,
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

        EnforceTerminologyConsistency(checkpoint);

        onProgress?.Invoke("Rendering translated output...");
        return FinalizeResult(checkpoint, outputPath, onProgress, coreResult.QualityReport);
    }

    public async Task<LongDocumentTranslationResult> RetryFailedChunksAsync(
        LongDocumentTranslationCheckpoint checkpoint,
        Language from,
        Language to,
        string outputPath,
        string serviceId,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateCheckpointOrThrow(checkpoint);

        if (checkpoint.FailedChunkIndexes.Count == 0)
        {
            return FinalizeResult(checkpoint, outputPath, onProgress, BuildQualityReportFromCheckpoint(checkpoint));
        }

        onProgress?.Invoke($"Retrying {checkpoint.FailedChunkIndexes.Count} failed chunks...");
        var retrySummary = await TranslatePendingChunksAsync(_coreLongDocumentService, checkpoint, from, to, serviceId, onProgress, cancellationToken);
        EnforceTerminologyConsistency(checkpoint);
        var qualityReport = BuildQualityReportFromRetry(checkpoint, retrySummary);
        return FinalizeResult(checkpoint, outputPath, onProgress, qualityReport);
    }

    private static async Task<RetryExecutionSummary> TranslatePendingChunksAsync(
        CoreLongDocumentTranslationService coreLongDocumentService,
        LongDocumentTranslationCheckpoint checkpoint,
        Language from,
        Language to,
        string serviceId,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
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
        var canonicalBySource = BuildCanonicalTranslations(checkpoint);
        var reusedByCanonical = 0;

        for (var i = 0; i < pendingIndexes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkIndex = pendingIndexes[i];
            var sourceText = checkpoint.SourceChunks[chunkIndex];

            if (canonicalBySource.TryGetValue(sourceText, out var canonicalTranslation) &&
                !string.IsNullOrWhiteSpace(canonicalTranslation))
            {
                checkpoint.TranslatedChunks[chunkIndex] = canonicalTranslation;
                reusedByCanonical++;
                continue;
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

        var retryResult = await coreLongDocumentService.TranslateAsync(retrySource, new LongDocumentTranslationOptions
        {
            ServiceId = serviceId,
            FromLanguage = from,
            ToLanguage = to,
            EnableFormulaProtection = true,
            EnableOcrFallback = true,
            MaxRetriesPerBlock = 1
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

    private static LongDocumentTranslationResult FinalizeResult(
        LongDocumentTranslationCheckpoint checkpoint,
        string outputPath,
        Action<string>? onProgress,
        LongDocumentQualityReport qualityReport)
    {
        ValidateCheckpointOrThrow(checkpoint);

        var succeededCount = checkpoint.TranslatedChunks.Count;
        if (succeededCount == 0)
        {
            throw new InvalidOperationException("Translation failed for all chunks.");
        }

        onProgress?.Invoke("Generating output PDF...");
        BackfillRenderingMetrics backfillMetrics;
        if (checkpoint.InputMode == LongDocumentInputMode.Pdf &&
            !string.IsNullOrWhiteSpace(checkpoint.SourcePdfPath) &&
            File.Exists(checkpoint.SourcePdfPath))
        {
            backfillMetrics = ExportPdfWithCoordinateBackfill(checkpoint, checkpoint.SourcePdfPath, outputPath);
        }
        else
        {
            backfillMetrics = ExportStructuredPdf(checkpoint, outputPath);
        }

        qualityReport = MergeBackfillMetrics(qualityReport, backfillMetrics);

        var state = checkpoint.FailedChunkIndexes.Count switch
        {
            0 => LongDocumentJobState.Completed,
            _ => LongDocumentJobState.PartialSuccess
        };

        onProgress?.Invoke(state == LongDocumentJobState.Completed
            ? $"Completed: {outputPath}"
            : $"Partially completed: {succeededCount}/{checkpoint.SourceChunks.Count} chunks. You can retry failed chunks.");

        return new LongDocumentTranslationResult
        {
            State = state,
            OutputPath = outputPath,
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

    private static SourceDocument BuildSourceDocument(LongDocumentInputMode mode, string input)
    {
        if (mode == LongDocumentInputMode.Manual)
        {
            var manualBlocks = SplitManualTextIntoBlocks(input, 1).ToList();
            return new SourceDocument
            {
                DocumentId = "manual-input",
                Pages =
                [
                    new SourceDocumentPage
                    {
                        PageNumber = 1,
                        Blocks = manualBlocks
                    }
                ]
            };
        }

        if (!File.Exists(input))
        {
            throw new FileNotFoundException("PDF file not found.", input);
        }

        using var document = PdfDocument.Open(input);
        var pages = document.GetPages()
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

    private static BackfillRenderingMetrics ExportPdfWithCoordinateBackfill(LongDocumentTranslationCheckpoint checkpoint, string sourcePdfPath, string outputPath)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using var doc = PdfReader.Open(sourcePdfPath, PdfDocumentOpenMode.Modify);
        var metadataByChunkIndex = checkpoint.ChunkMetadata.ToDictionary(m => m.ChunkIndex);

        var candidateBlocks = 0;
        var renderedBlocks = 0;
        var missingBoundingBoxBlocks = 0;
        var shrinkFontBlocks = 0;
        var truncatedBlocks = 0;
        var objectReplaceBlocks = 0;
        var overlayModeBlocks = 0;
        var pageMetrics = new Dictionary<int, PageBackfillAccumulator>();

        foreach (var chunkIndex in Enumerable.Range(0, checkpoint.SourceChunks.Count))
        {
            if (!checkpoint.TranslatedChunks.TryGetValue(chunkIndex, out var translated) || string.IsNullOrWhiteSpace(translated))
            {
                continue;
            }

            var metadata = metadataByChunkIndex[chunkIndex];
            if (metadata.SourceBlockType == SourceBlockType.Formula || metadata.IsFormulaLike)
            {
                continue;
            }

            candidateBlocks++;
            var perPage = GetOrCreatePageBackfill(pageMetrics, metadata.PageNumber);
            perPage.CandidateBlocks++;

            if (metadata.BoundingBox is null)
            {
                missingBoundingBoxBlocks++;
                perPage.MissingBoundingBoxBlocks++;
                continue;
            }

            var pageIndex = metadata.PageNumber - 1;
            if (pageIndex < 0 || pageIndex >= doc.Pages.Count)
            {
                missingBoundingBoxBlocks++;
                perPage.MissingBoundingBoxBlocks++;
                continue;
            }

            var page = doc.Pages[pageIndex];
            var sourceText = checkpoint.SourceChunks[chunkIndex];
            if (TryReplacePdfTextObject(page, sourceText, translated))
            {
                renderedBlocks++;
                objectReplaceBlocks++;
                perPage.RenderedBlocks++;
                perPage.ObjectReplaceBlocks++;
                continue;
            }

            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            var box = metadata.BoundingBox.Value;

            var drawX = Math.Max(0, box.X);
            var drawY = Math.Max(0, page.Height.Point - (box.Y + box.Height));
            var drawWidth = Math.Max(40, box.Width);
            var drawHeight = Math.Max(14, box.Height);

            var rect = new XRect(drawX, drawY, drawWidth, drawHeight);
            var baseFont = PickFont(metadata.SourceBlockType, metadata.IsFormulaLike);
            var font = FitFontToRect(gfx, translated, baseFont, rect.Width, rect.Height);
            if (font.Size < baseFont.Size)
            {
                shrinkFontBlocks++;
                perPage.ShrinkFontBlocks++;
            }

            var wrappedLines = WrapTextByWidth(gfx, translated, font, rect.Width).ToList();
            var maxVisibleLines = Math.Max(1, (int)Math.Floor(rect.Height / 14d));
            if (wrappedLines.Count > maxVisibleLines)
            {
                wrappedLines = wrappedLines.Take(maxVisibleLines).ToList();
                var last = wrappedLines[^1];
                wrappedLines[^1] = last.Length > 1 ? $"{last.TrimEnd('.', ' ')}…" : "…";
                truncatedBlocks++;
                perPage.TruncatedBlocks++;
            }

            var lineY = rect.Y;
            foreach (var line in wrappedLines)
            {
                gfx.DrawString(line, font, XBrushes.Black, new XRect(rect.X, lineY, rect.Width, 20), XStringFormats.TopLeft);
                lineY += 14;
            }

            renderedBlocks++;
            overlayModeBlocks++;
            perPage.RenderedBlocks++;
            perPage.OverlayModeBlocks++;
        }

        doc.Save(outputPath);

        return new BackfillRenderingMetrics(
            candidateBlocks,
            renderedBlocks,
            missingBoundingBoxBlocks,
            shrinkFontBlocks,
            truncatedBlocks,
            objectReplaceBlocks,
            overlayModeBlocks,
            0,
            BuildPageBackfillMetrics(pageMetrics));
    }

    private static BackfillRenderingMetrics ExportStructuredPdf(LongDocumentTranslationCheckpoint checkpoint, string outputPath)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var doc = new PdfSharpCore.Pdf.PdfDocument();
        var metadataByChunkIndex = checkpoint.ChunkMetadata.ToDictionary(m => m.ChunkIndex);
        var groupedChunks = Enumerable.Range(0, checkpoint.SourceChunks.Count)
            .OrderBy(index => metadataByChunkIndex[index].PageNumber)
            .ThenBy(index => metadataByChunkIndex[index].OrderInPage)
            .ThenBy(index => index)
            .GroupBy(index => metadataByChunkIndex[index].PageNumber);

        foreach (var pageGroup in groupedChunks)
        {
            var page = doc.AddPage();
            var gfx = XGraphics.FromPdfPage(page);

            try
            {
                const int margin = 40;
                var y = margin;
                var width = page.Width - margin * 2;

                var headingFont = new XFont("Arial", 14, XFontStyle.Bold);
                gfx.DrawString($"Page {pageGroup.Key}", headingFont, XBrushes.Black, new XRect(margin, y, width, 24), XStringFormats.TopLeft);
                y += 24;

                foreach (var chunkIndex in pageGroup)
                {
                    var metadata = metadataByChunkIndex[chunkIndex];
                    var content = checkpoint.TranslatedChunks.TryGetValue(chunkIndex, out var translated)
                        ? translated
                        : $"[Chunk {chunkIndex + 1} translation failed. Retry required.]";

                    var font = PickFont(metadata.SourceBlockType, metadata.IsFormulaLike);
                    foreach (var line in WrapText(content, 95))
                    {
                        if (y > page.Height - margin)
                        {
                            gfx.Dispose();
                            page = doc.AddPage();
                            gfx = XGraphics.FromPdfPage(page);
                            y = margin;
                        }

                        gfx.DrawString(line, font, XBrushes.Black, new XRect(margin, y, width, 20), XStringFormats.TopLeft);
                        y += 16;
                    }

                    y += 8;
                }
            }
            finally
            {
                gfx.Dispose();
            }
        }

        doc.Save(outputPath);

        var structuredPageMetrics = groupedChunks.ToDictionary(
            group => group.Key,
            group => new BackfillPageMetrics
            {
                CandidateBlocks = 0,
                RenderedBlocks = 0,
                MissingBoundingBoxBlocks = 0,
                ShrinkFontBlocks = 0,
                TruncatedBlocks = 0,
                ObjectReplaceBlocks = 0,
                OverlayModeBlocks = 0,
                StructuredFallbackBlocks = group.Count()
            });

        return new BackfillRenderingMetrics(0, 0, 0, 0, 0, 0, 0, checkpoint.SourceChunks.Count, structuredPageMetrics);
    }

    private static bool TryReplacePdfTextObject(PdfPage page, string sourceText, string translatedText)
    {
        if (string.IsNullOrWhiteSpace(sourceText) || string.IsNullOrWhiteSpace(translatedText))
        {
            return false;
        }

        if (!IsAscii(sourceText) || !IsAscii(translatedText))
        {
            return false;
        }

        try
        {
            var createSingleContent = page.Contents.GetType().GetMethod("CreateSingleContent");
            if (createSingleContent is null)
            {
                return false;
            }

            var contentStream = createSingleContent.Invoke(page.Contents, null);
            if (contentStream is null)
            {
                return false;
            }

            var streamProperty = contentStream.GetType().GetProperty("Stream");
            var streamValue = streamProperty?.GetValue(contentStream);
            if (streamValue is null)
            {
                return false;
            }

            var valueProperty = streamValue.GetType().GetProperty("Value");
            var raw = valueProperty?.GetValue(streamValue) as byte[];
            if (raw is null || raw.Length == 0)
            {
                return false;
            }

            var content = Encoding.ASCII.GetString(raw);
            if (!TryPatchPdfLiteralToken(content, sourceText, translatedText, out var patched))
            {
                return false;
            }

            valueProperty?.SetValue(streamValue, Encoding.ASCII.GetBytes(patched));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryPatchPdfLiteralToken(string content, string sourceText, string translatedText, out string patched)
    {
        patched = content;

        var escapedSource = EscapePdfLiteralString(sourceText);
        var sourceToken = $"({escapedSource})";
        var idx = content.IndexOf(sourceToken, StringComparison.Ordinal);
        if (idx >= 0)
        {
            if (translatedText.Length > sourceText.Length)
            {
                return false;
            }

            var padded = translatedText.PadRight(sourceText.Length);
            var escapedTranslated = EscapePdfLiteralString(padded);
            var targetToken = $"({escapedTranslated})";

            patched = content.Remove(idx, sourceToken.Length).Insert(idx, targetToken);
            return true;
        }

        if (!TryPatchPdfArrayTextToken(content, sourceText, translatedText, out patched))
        {
            return false;
        }

        return true;
    }

    private static bool TryPatchPdfArrayTextToken(string content, string sourceText, string translatedText, out string patched)
    {
        patched = content;
        var normalizedSource = NormalizePdfTextForMatch(sourceText);
        if (string.IsNullOrWhiteSpace(normalizedSource))
        {
            return false;
        }

        foreach (Match match in Regex.Matches(content, @"\[(?<body>.*?)\]\s*TJ", RegexOptions.Singleline))
        {
            var bodyGroup = match.Groups["body"];
            if (!bodyGroup.Success)
            {
                continue;
            }

            var extracted = ExtractPdfLiteralStrings(bodyGroup.Value);
            if (extracted.Count == 0)
            {
                continue;
            }

            var combined = string.Concat(extracted.Select(item => item.Value));
            if (!string.Equals(NormalizePdfTextForMatch(combined), normalizedSource, StringComparison.Ordinal))
            {
                continue;
            }

            var targetLength = Math.Max(sourceText.Length, combined.Length);
            if (translatedText.Length > targetLength)
            {
                return false;
            }

            var padded = translatedText.PadRight(targetLength);
            var escapedTranslated = EscapePdfLiteralString(padded);
            var replacement = $"({escapedTranslated}) Tj";
            patched = content.Remove(match.Index, match.Length).Insert(match.Index, replacement);
            return true;
        }

        return false;
    }

    private static List<(int Start, int Length, string Value)> ExtractPdfLiteralStrings(string content)
    {
        var items = new List<(int Start, int Length, string Value)>();

        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '(')
            {
                continue;
            }

            var (length, value) = ParsePdfLiteralString(content, i);
            if (length <= 0)
            {
                continue;
            }

            items.Add((i, length, value));
            i += length - 1;
        }

        return items;
    }

    private static (int Length, string Value) ParsePdfLiteralString(string content, int startIndex)
    {
        var builder = new StringBuilder();
        var nesting = 0;
        var escaped = false;

        for (var index = startIndex; index < content.Length; index++)
        {
            var current = content[index];

            if (index == startIndex)
            {
                nesting = 1;
                continue;
            }

            if (escaped)
            {
                builder.Append(current);
                escaped = false;
                continue;
            }

            if (current == '\\')
            {
                escaped = true;
                continue;
            }

            if (current == '(')
            {
                nesting++;
                builder.Append(current);
                continue;
            }

            if (current == ')')
            {
                nesting--;
                if (nesting == 0)
                {
                    return (index - startIndex + 1, builder.ToString());
                }

                builder.Append(current);
                continue;
            }

            builder.Append(current);
        }

        return (0, string.Empty);
    }

    private static string NormalizePdfTextForMatch(string text)
    {
        return string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
    }

    private static string EscapePdfLiteralString(string text)
    {
        return text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
    }

    private static bool IsAscii(string text)
    {
        return text.All(c => c <= 0x7F);
    }

    private static XFont PickFont(SourceBlockType sourceBlockType, bool isFormulaLike)
    {
        if (sourceBlockType == SourceBlockType.Heading)
        {
            return new XFont("Arial", 14, XFontStyle.Bold);
        }

        if (sourceBlockType == SourceBlockType.Formula || isFormulaLike)
        {
            return new XFont("Consolas", 11, XFontStyle.Italic);
        }

        return new XFont("Arial", 11);
    }

    private static XFont FitFontToRect(XGraphics gfx, string text, XFont baseFont, double width, double height)
    {
        var size = baseFont.Size;
        while (size >= 8)
        {
            var candidate = new XFont(baseFont.Name, size, baseFont.Style);
            var lines = WrapTextByWidth(gfx, text, candidate, width).ToList();
            var maxLines = Math.Max(1, (int)Math.Floor(height / 14d));
            if (lines.Count <= maxLines)
            {
                return candidate;
            }

            size -= 0.5;
        }

        return new XFont(baseFont.Name, 8, baseFont.Style);
    }

    private static IEnumerable<SourceDocumentBlock> SplitManualTextIntoBlocks(string? text, int pageNumber)
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

    private static IEnumerable<SourceDocumentBlock> ExtractLayoutBlocksFromPage(Page page)
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

        var orderedLines = OrderLinesByLayout(lines, page.Width);
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

            yield return new SourceDocumentBlock
            {
                BlockId = $"p{page.Number}-{regionTag}-b{i + 1}",
                BlockType = type,
                Text = blockText,
                IsFormulaLike = type == SourceBlockType.Formula,
                BoundingBox = new BlockRect(left, bottom, Math.Max(1, right - left), Math.Max(1, top - bottom))
            };
        }
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

    private static IEnumerable<string> WrapText(string text, int maxChars)
    {
        foreach (var paragraph in text.Split('\n'))
        {
            var p = paragraph.TrimEnd('\r');
            if (p.Length <= maxChars)
            {
                yield return p;
                continue;
            }

            var start = 0;
            while (start < p.Length)
            {
                var len = Math.Min(maxChars, p.Length - start);
                yield return p.Substring(start, len);
                start += len;
            }
        }
    }

    private static IEnumerable<string> WrapTextByWidth(XGraphics gfx, string text, XFont font, double maxWidth)
    {
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrEmpty(paragraph))
            {
                yield return string.Empty;
                continue;
            }

            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                yield return string.Empty;
                continue;
            }

            var line = words[0];
            for (var i = 1; i < words.Length; i++)
            {
                var candidate = $"{line} {words[i]}";
                if (gfx.MeasureString(candidate, font).Width <= maxWidth)
                {
                    line = candidate;
                }
                else
                {
                    yield return line;
                    line = words[i];
                }
            }

            yield return line;
        }
    }

    private static PageBackfillAccumulator GetOrCreatePageBackfill(
        IDictionary<int, PageBackfillAccumulator> pageMetrics,
        int pageNumber)
    {
        if (!pageMetrics.TryGetValue(pageNumber, out var metrics))
        {
            metrics = new PageBackfillAccumulator();
            pageMetrics[pageNumber] = metrics;
        }

        return metrics;
    }

    private static IReadOnlyDictionary<int, BackfillPageMetrics>? BuildPageBackfillMetrics(
        IReadOnlyDictionary<int, PageBackfillAccumulator> pageMetrics)
    {
        if (pageMetrics.Count == 0)
        {
            return null;
        }

        return pageMetrics.ToDictionary(
            entry => entry.Key,
            entry => new BackfillPageMetrics
            {
                CandidateBlocks = entry.Value.CandidateBlocks,
                RenderedBlocks = entry.Value.RenderedBlocks,
                MissingBoundingBoxBlocks = entry.Value.MissingBoundingBoxBlocks,
                ShrinkFontBlocks = entry.Value.ShrinkFontBlocks,
                TruncatedBlocks = entry.Value.TruncatedBlocks,
                ObjectReplaceBlocks = entry.Value.ObjectReplaceBlocks,
                OverlayModeBlocks = entry.Value.OverlayModeBlocks,
                StructuredFallbackBlocks = entry.Value.StructuredFallbackBlocks
            });
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

    private static LongDocumentQualityReport MergeBackfillMetrics(LongDocumentQualityReport baseReport, BackfillRenderingMetrics metrics)
    {
        var backfill = new BackfillQualityMetrics
        {
            CandidateBlocks = metrics.CandidateBlocks,
            RenderedBlocks = metrics.RenderedBlocks,
            MissingBoundingBoxBlocks = metrics.MissingBoundingBoxBlocks,
            ShrinkFontBlocks = metrics.ShrinkFontBlocks,
            TruncatedBlocks = metrics.TruncatedBlocks,
            ObjectReplaceBlocks = metrics.ObjectReplaceBlocks,
            OverlayModeBlocks = metrics.OverlayModeBlocks,
            StructuredFallbackBlocks = metrics.StructuredFallbackBlocks,
            PageMetrics = metrics.PageMetrics,
            RetryMergeStrategy = baseReport.BackfillMetrics?.RetryMergeStrategy
        };

        return new LongDocumentQualityReport
        {
            StageTimingsMs = new Dictionary<string, long>(baseReport.StageTimingsMs, StringComparer.Ordinal),
            BackfillMetrics = backfill,
            TotalBlocks = baseReport.TotalBlocks,
            TranslatedBlocks = baseReport.TranslatedBlocks,
            SkippedBlocks = baseReport.SkippedBlocks,
            FailedBlocks = baseReport.FailedBlocks
        };
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
            return current with { RetryMergeStrategy = "core-only", PageMetrics = MergePageBackfillMetrics(null, current.PageMetrics) };
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

    private static Dictionary<string, string> BuildCanonicalTranslations(LongDocumentTranslationCheckpoint checkpoint)
    {
        var canonical = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in checkpoint.TranslatedChunks.OrderBy(item => item.Key))
        {
            if (entry.Key < 0 || entry.Key >= checkpoint.SourceChunks.Count)
            {
                continue;
            }

            var source = checkpoint.SourceChunks[entry.Key];
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(entry.Value))
            {
                continue;
            }

            canonical.TryAdd(source, entry.Value.Trim());
        }

        return canonical;
    }

    private static void EnforceTerminologyConsistency(LongDocumentTranslationCheckpoint checkpoint)
    {
        var canonicalBySource = BuildCanonicalTranslations(checkpoint);

        for (var i = 0; i < checkpoint.SourceChunks.Count; i++)
        {
            if (!checkpoint.TranslatedChunks.ContainsKey(i))
            {
                continue;
            }

            var source = checkpoint.SourceChunks[i];
            if (canonicalBySource.TryGetValue(source, out var canonical) && !string.IsNullOrWhiteSpace(canonical))
            {
                checkpoint.TranslatedChunks[i] = canonical;
            }
        }
    }
}
