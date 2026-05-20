using System.Text.Json;
using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services.DocumentExport;

namespace Easydict.Workers.LongDoc.Infrastructure;

internal sealed class WorkerLongDocumentPipeline
{
    private readonly Func<TranslationRequest, string, CancellationToken, Task<TranslationResult>> _translateWithService;

    public WorkerLongDocumentPipeline(
        Func<TranslationRequest, string, CancellationToken, Task<TranslationResult>> translateWithService)
    {
        _translateWithService = translateWithService;
    }

    public async Task<TranslateDocumentResult> TranslateAsync(
        TranslateDocumentParams p,
        SettingsSnapshot settings,
        IProgress<LongDocumentTranslationProgress>? progress,
        CancellationToken cancellationToken,
        Func<BlockTranslatedEventData, CancellationToken, Task>? onBlockTranslated = null)
    {
        var mode = ParseInputMode(p.InputMode);
        using var sourceBuilder = new WorkerLongDocumentSourceDocumentBuilder();
        var sourceDocument = await sourceBuilder.BuildAsync(
                mode,
                p.InputPath,
                ParseLayoutDetectionMode(p.LayoutDetection ?? settings.LayoutDetectionMode),
                p.VisionEndpoint,
                p.VisionApiKey,
                p.VisionModel,
                p.PageRange,
                settings.EnableTatrTableStructure ?? true,
                settings.ProxyEnabled ?? false,
                settings.ProxyUri,
                settings.ProxyBypassLocal ?? false,
                onProgress: null,
                cancellationToken)
            .ConfigureAwait(false);

        if (!sourceDocument.Pages.SelectMany(page => page.Blocks).Any(block => !string.IsNullOrWhiteSpace(block.Text)))
        {
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, "No source text found for translation.");
        }

        var coreService = new LongDocumentTranslationService(translateWithService: _translateWithService);
        var options = BuildOptions(p, settings, progress);
        var coreResult = await coreService.TranslateAsync(sourceDocument, options, cancellationToken).ConfigureAwait(false);

        var result = await ExportFlatResultAsync(
            mode,
            p,
            sourceDocument,
            coreResult,
            onBlockTranslated,
            cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(p.ResultJsonPath))
        {
            await LongDocResultFileStore.WriteAsync(p.ResultJsonPath, result, cancellationToken).ConfigureAwait(false);
            return new TranslateDocumentResult
            {
                State = result.State,
                ResultJsonPath = p.ResultJsonPath,
            };
        }

        return result;
    }

    private static LongDocumentTranslationOptions BuildOptions(
        TranslateDocumentParams p,
        SettingsSnapshot settings,
        IProgress<LongDocumentTranslationProgress>? progress)
    {
        return new LongDocumentTranslationOptions
        {
            ServiceId = p.ServiceId,
            FromLanguage = ParseLanguage(p.From),
            ToLanguage = ParseLanguage(p.To),
            EnableFormulaProtection = true,
            EnableDocumentContextPass = (settings.LongDocEnableDocumentContextPass ?? true) &&
                !UsesFoundryLocalLongDocProfile(p.ServiceId, settings.LocalAIProvider),
            EnableOcrFallback = true,
            EnableQualityFeedbackRetry = true,
            MaxRetriesPerBlock = 1,
            MaxConcurrency = UsesFoundryLocalLongDocProfile(p.ServiceId, settings.LocalAIProvider)
                ? 1
                : Math.Clamp(settings.LongDocMaxConcurrency ?? 1, 1, 16),
            RequestTimeoutMs = UsesFoundryLocalLongDocProfile(p.ServiceId, settings.LocalAIProvider)
                ? 120_000
                : 30_000,
            FormulaFontPattern = string.IsNullOrWhiteSpace(settings.FormulaFontPattern) ? null : settings.FormulaFontPattern,
            FormulaCharPattern = string.IsNullOrWhiteSpace(settings.FormulaCharPattern) ? null : settings.FormulaCharPattern,
            PageRange = p.PageRange,
            CustomPrompt = string.IsNullOrWhiteSpace(settings.LongDocCustomPrompt) ? null : settings.LongDocCustomPrompt,
            Progress = progress,
        };
    }

    private static async Task<TranslateDocumentResult> ExportFlatResultAsync(
        LongDocumentInputMode mode,
        TranslateDocumentParams p,
        SourceDocument sourceDocument,
        Easydict.TranslationService.LongDocument.LongDocumentTranslationResult coreResult,
        Func<BlockTranslatedEventData, CancellationToken, Task>? onBlockTranslated,
        CancellationToken cancellationToken)
    {
        var outputPath = ResolveOutputPath(p.OutputPath, p.InputPath, mode);
        var outputMode = ParseOutputMode(p.OutputMode);
        var checkpoint = BuildCheckpointFromCoreResult(
            mode,
            p.InputPath,
            ParseLanguage(p.To),
            sourceDocument,
            coreResult,
            p.PageRange);

        var failedIndexes = checkpoint.FailedChunkIndexes
            .OrderBy(index => index)
            .ToList();

        var succeededChunks = checkpoint.TranslatedChunks.Count;
        if (succeededChunks == 0)
        {
            throw new WorkerHandlerException(WorkerErrorCodes.ServiceError, "Translation failed for all chunks.");
        }

        if (onBlockTranslated is not null)
        {
            var orderedBlocks = coreResult.Pages
                .OrderBy(page => page.PageNumber)
                .SelectMany(page => page.Blocks.Select(block => new OrderedTranslatedBlock(page.PageNumber, block)))
                .ToList();

            for (var i = 0; i < orderedBlocks.Count; i++)
            {
                var item = orderedBlocks[i];
                await onBlockTranslated(new BlockTranslatedEventData
                {
                    ChunkIndex = i,
                    PageNumber = item.PageNumber,
                    SourceBlockId = item.Block.SourceBlockId,
                    TranslatedText = item.Block.TranslatedText,
                    RetryCount = item.Block.RetryCount,
                    LastError = item.Block.LastError,
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

        var exportService = ResolveExportService(mode, ParsePdfExportMode(p.PdfExportMode));
        var exportResult = await Task.Run(
            () => exportService.Export(checkpoint, p.InputPath, outputPath, outputMode),
            cancellationToken).ConfigureAwait(false);

        var qualityReport = coreResult.QualityReport;
        if (exportResult.BackfillMetrics is not null)
        {
            qualityReport = qualityReport with { BackfillMetrics = exportResult.BackfillMetrics };
        }

        return new TranslateDocumentResult
        {
            State = failedIndexes.Count == 0 ? "Completed" : "PartiallyCompleted",
            OutputPath = exportResult.OutputPath,
            BilingualOutputPath = exportResult.BilingualOutputPath,
            TotalChunks = checkpoint.SourceChunks.Count,
            SucceededChunks = succeededChunks,
            FailedChunkIndexes = failedIndexes,
            QualityReport = JsonSerializer.Serialize(qualityReport, JsonOptions),
        };
    }

    private static LongDocumentTranslationCheckpoint BuildCheckpointFromCoreResult(
        LongDocumentInputMode mode,
        string sourceFilePath,
        Language targetLanguage,
        SourceDocument sourceDocument,
        Easydict.TranslationService.LongDocument.LongDocumentTranslationResult coreResult,
        string? pageRange)
    {
        var sourceBlocksByPageAndId = new Dictionary<(int Page, string BlockId), SourceDocumentBlock>();
        foreach (var page in sourceDocument.Pages)
        {
            foreach (var block in page.Blocks)
            {
                sourceBlocksByPageAndId[(page.PageNumber, block.BlockId)] = block;
            }
        }

        var sourceChunks = new List<string>();
        var chunkMetadata = new List<LongDocumentChunkMetadata>();
        var translatedChunks = new Dictionary<int, string>();
        var failedChunkIndexes = new HashSet<int>();

        var chunkIndex = 0;
        foreach (var page in coreResult.Pages.OrderBy(p => p.PageNumber))
        {
            var pageBlockCount = Math.Max(1, page.Blocks.Count);
            for (var orderInPage = 0; orderInPage < page.Blocks.Count; orderInPage++)
            {
                var block = page.Blocks[orderInPage];
                sourceBlocksByPageAndId.TryGetValue((page.PageNumber, block.SourceBlockId), out var sourceBlock);
                var regionInfo = LongDocumentSourceExtraction.InferRegionInfoFromBlockId(block.SourceBlockId);
                var sourceBlockType = MapBlockType(block.BlockType);

                sourceChunks.Add(block.OriginalText);
                chunkMetadata.Add(new LongDocumentChunkMetadata
                {
                    ChunkIndex = chunkIndex,
                    PageNumber = page.PageNumber,
                    SourceBlockId = block.SourceBlockId,
                    SourceBlockType = sourceBlockType,
                    IsFormulaLike = sourceBlock?.IsFormulaLike ?? sourceBlockType == SourceBlockType.Formula,
                    OrderInPage = orderInPage,
                    RegionType = regionInfo.Type,
                    RegionConfidence = regionInfo.Confidence,
                    RegionSource = regionInfo.Source,
                    ReadingOrderScore = LongDocumentSourceExtraction.CalculateReadingOrderScore(orderInPage, pageBlockCount),
                    BoundingBox = block.BoundingBox,
                    TextStyle = block.TextStyle,
                    FormulaCharacters = block.FormulaCharacters,
                    TranslationSkipped = block.TranslationSkipped,
                    PreserveOriginalTextInPdfExport =
                        block.PreserveOriginalTextInPdfExport ||
                        sourceBlockType == SourceBlockType.Formula ||
                        regionInfo.Type is LayoutRegionType.Formula or LayoutRegionType.IsolatedFormula,
                    RetryCount = block.RetryCount,
                    FallbackText = sourceBlock?.FallbackText,
                    DetectedFontNames = sourceBlock?.DetectedFontNames,
                });

                if (string.IsNullOrWhiteSpace(block.LastError))
                {
                    translatedChunks[chunkIndex] = block.TranslatedText;
                }
                else
                {
                    failedChunkIndexes.Add(chunkIndex);
                }

                chunkIndex++;
            }
        }

        return new LongDocumentTranslationCheckpoint
        {
            InputMode = mode,
            SourceFilePath = sourceFilePath,
            TargetLanguage = targetLanguage,
            PageRange = pageRange,
            SourceChunks = sourceChunks,
            ChunkMetadata = chunkMetadata,
            TranslatedChunks = translatedChunks,
            FailedChunkIndexes = failedChunkIndexes,
        };
    }

    private static IDocumentExportService ResolveExportService(LongDocumentInputMode mode, PdfExportMode pdfExportMode)
    {
        return mode switch
        {
            LongDocumentInputMode.Pdf => pdfExportMode == PdfExportMode.ContentStreamReplacement
                ? new MuPdfExportService()
                : new PdfExportService(),
            LongDocumentInputMode.Markdown => new MarkdownExportService(),
            LongDocumentInputMode.PlainText => new PlainTextExportService(),
            _ => throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, $"Unsupported inputMode: {mode}"),
        };
    }

    private static SourceBlockType MapBlockType(BlockType blockType)
    {
        return blockType switch
        {
            BlockType.Heading => SourceBlockType.Heading,
            BlockType.Caption => SourceBlockType.Caption,
            BlockType.Table => SourceBlockType.TableCell,
            BlockType.Formula => SourceBlockType.Formula,
            BlockType.Unknown => SourceBlockType.Unknown,
            _ => SourceBlockType.Paragraph,
        };
    }

    private static LongDocumentInputMode ParseInputMode(string value)
    {
        if (Enum.TryParse<LongDocumentInputMode>(value, ignoreCase: true, out var mode))
        {
            return mode;
        }

        throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, $"Unsupported inputMode: {value}");
    }

    private static DocumentOutputMode ParseOutputMode(string value)
    {
        if (value.Equals("TargetOnly", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentOutputMode.Monolingual;
        }

        return Enum.TryParse<DocumentOutputMode>(value, ignoreCase: true, out var mode)
            ? mode
            : DocumentOutputMode.Monolingual;
    }

    private static PdfExportMode ParsePdfExportMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return PdfExportMode.ContentStreamReplacement;
        }

        if (value.Equals("Reconstruct", StringComparison.OrdinalIgnoreCase))
        {
            return PdfExportMode.ContentStreamReplacement;
        }

        return Enum.TryParse<PdfExportMode>(value, ignoreCase: true, out var mode)
            ? mode
            : PdfExportMode.ContentStreamReplacement;
    }

    private static LayoutDetectionMode ParseLayoutDetectionMode(string? value)
    {
        return NormalizeToken(value) switch
        {
            "" or "auto" => LayoutDetectionMode.Auto,
            "heuristic" => LayoutDetectionMode.Heuristic,
            "onnx" or "onnxlocal" => LayoutDetectionMode.OnnxLocal,
            "vision" or "visionllm" => LayoutDetectionMode.VisionLLM,
            _ => throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, $"Unsupported layoutDetection: {value}"),
        };
    }

    private static Language ParseLanguage(string value)
    {
        return Enum.TryParse<Language>(value, ignoreCase: true, out var language)
            ? language
            : Language.Auto;
    }

    private static string ResolveOutputPath(string? outputPath, string inputPath, LongDocumentInputMode mode)
    {
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            return outputPath;
        }

        var directory = Path.GetDirectoryName(inputPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var extension = mode switch
        {
            LongDocumentInputMode.Markdown => ".md",
            LongDocumentInputMode.Pdf => ".pdf",
            _ => ".txt",
        };

        return Path.Combine(directory, $"{name}.translated{extension}");
    }

    private static bool UsesFoundryLocalLongDocProfile(string serviceId, string? localAIProvider)
    {
        if (serviceId.Equals("foundry-local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!serviceId.Equals("windows-local-ai", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var provider = NormalizeToken(localAIProvider);
        return provider is "" or "auto" or "foundrylocal";
    }

    private static string NormalizeToken(string? value)
    {
        return (value ?? string.Empty)
            .Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record OrderedTranslatedBlock(int PageNumber, TranslatedDocumentBlock Block);

}
