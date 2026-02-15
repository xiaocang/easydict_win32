using System.Text;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.LongDocument;

public sealed class LongDocumentTranslationService
{
    private readonly ITranslationService _translationService;

    public LongDocumentTranslationService(ITranslationService translationService)
    {
        _translationService = translationService;
    }

    public async Task<LongDocumentTranslationResult> TranslateAsync(
        LongDocumentTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        var timings = new List<StageTiming>();
        var retryCount = 0;
        var failedBlocks = new List<FailedDocumentBlock>();

        var (pages, ingestTiming) = DocumentPipelineStopwatch.Measure("ingest", () => SelectSourcePages(request));
        timings.Add(ingestTiming);

        var (ir, irTiming) = DocumentPipelineStopwatch.Measure("build-ir", () => BuildIntermediateRepresentation(pages, request.IsScannedPdf && request.Options.EnableOcrFallback));
        timings.Add(irTiming);

        var (protectedIr, protectTiming) = DocumentPipelineStopwatch.Measure("formula-protection", () => ProtectFormulas(ir));
        timings.Add(protectTiming);

        var (translatedPages, translateTiming) = await DocumentPipelineStopwatch.MeasureAsync("translate", async () =>
            await TranslateBlocksAsync(request, protectedIr, failedBlocks, r => retryCount += r, cancellationToken).ConfigureAwait(false));
        timings.Add(translateTiming);

        var (structuredOutput, outputTiming) = DocumentPipelineStopwatch.Measure("structured-layout-output", () => BuildStructuredOutput(translatedPages));
        timings.Add(outputTiming);

        var failedPages = failedBlocks
            .Select(x => x.PageNumber)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        return new LongDocumentTranslationResult
        {
            IntermediateRepresentation = protectedIr,
            Pages = translatedPages,
            StructuredOutputText = structuredOutput,
            QualityReport = new LongDocumentQualityReport
            {
                FailedPages = failedPages,
                FailedBlocks = failedBlocks,
                RetryCount = retryCount,
                StageTimings = timings,
            },
        };
    }

    private static IReadOnlyList<SourceDocumentPage> SelectSourcePages(LongDocumentTranslationRequest request)
    {
        if (request.IsScannedPdf &&
            request.Options.EnableOcrFallback &&
            request.OcrFallbackPages is { Count: > 0 })
        {
            return request.OcrFallbackPages;
        }

        return request.Pages;
    }

    private static DocumentIr BuildIntermediateRepresentation(IReadOnlyList<SourceDocumentPage> pages, bool usedOcrFallback)
    {
        var irPages = pages
            .OrderBy(p => p.PageNumber)
            .Select(page => new DocumentPageIr
            {
                PageNumber = page.PageNumber,
                Blocks = page.Blocks
                    .OrderBy(b => b.ReadingOrder)
                    .Select(block => new DocumentBlockIr
                    {
                        Id = block.Id,
                        PageNumber = block.PageNumber,
                        ReadingOrder = block.ReadingOrder,
                        BlockType = block.BlockType,
                        Text = block.Text,
                        SourceHash = DocumentBlockIr.ComputeHash(block.Text),
                        Coordinates = block.Coordinates,
                        ParentBlockId = block.ParentBlockId,
                    })
                    .ToList(),
            })
            .ToList();

        return new DocumentIr
        {
            Pages = irPages,
            UsedOcrFallback = usedOcrFallback,
        };
    }

    private static DocumentIr ProtectFormulas(DocumentIr documentIr)
    {
        var pages = new List<DocumentPageIr>();

        foreach (var page in documentIr.Pages)
        {
            var protectedBlocks = new List<DocumentBlockIr>();

            foreach (var block in page.Blocks)
            {
                if (block.BlockType == DocumentBlockType.Formula)
                {
                    protectedBlocks.Add(block with { BlockType = DocumentBlockType.FormulaPlaceholder, Text = "[FORMULA_BLOCK]" });
                    continue;
                }

                var protectedText = FormulaPatterns.DisplayMathRegex().Replace(block.Text, "[FORMULA_BLOCK]");
                protectedText = FormulaPatterns.BracketMathRegex().Replace(protectedText, "[FORMULA_BLOCK]");

                protectedBlocks.Add(block with
                {
                    Text = protectedText,
                    SourceHash = DocumentBlockIr.ComputeHash(block.Text),
                });
            }

            pages.Add(new DocumentPageIr
            {
                PageNumber = page.PageNumber,
                Blocks = protectedBlocks,
            });
        }

        return documentIr with { Pages = pages };
    }

    private async Task<IReadOnlyList<TranslatedDocumentPage>> TranslateBlocksAsync(
        LongDocumentTranslationRequest request,
        DocumentIr ir,
        List<FailedDocumentBlock> failedBlocks,
        Action<int> retryCounter,
        CancellationToken cancellationToken)
    {
        var result = new List<TranslatedDocumentPage>();

        foreach (var page in ir.Pages)
        {
            var translatedBlocks = new List<TranslatedDocumentBlock>();

            foreach (var block in page.Blocks.OrderBy(x => x.ReadingOrder))
            {
                if (block.BlockType is DocumentBlockType.Formula or DocumentBlockType.FormulaPlaceholder)
                {
                    translatedBlocks.Add(ToTranslatedBlock(block, block.Text));
                    continue;
                }

                var translatedText = await TranslateWithRetryAsync(
                    block,
                    request,
                    failedBlocks,
                    retryCounter,
                    cancellationToken).ConfigureAwait(false);

                if (request.Options.EnableGlossaryConsistency && request.Options.Glossary is { Count: > 0 })
                {
                    translatedText = EnforceGlossary(translatedText, request.Options.Glossary);
                }

                translatedBlocks.Add(ToTranslatedBlock(block, translatedText));
            }

            result.Add(new TranslatedDocumentPage
            {
                PageNumber = page.PageNumber,
                Blocks = translatedBlocks,
            });
        }

        return result;
    }

    private async Task<string> TranslateWithRetryAsync(
        DocumentBlockIr block,
        LongDocumentTranslationRequest request,
        List<FailedDocumentBlock> failedBlocks,
        Action<int> retryCounter,
        CancellationToken cancellationToken)
    {
        var maxAttempts = request.Options.MaxRetriesPerBlock + 1;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var translationRequest = new TranslationRequest
                {
                    Text = block.Text,
                    FromLanguage = request.FromLanguage,
                    ToLanguage = request.ToLanguage,
                    TimeoutMs = request.Options.TimeoutMs,
                };

                var translated = await _translationService.TranslateAsync(translationRequest, cancellationToken).ConfigureAwait(false);
                return translated.TranslatedText;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                retryCounter(1);
            }
            catch (Exception ex)
            {
                failedBlocks.Add(new FailedDocumentBlock(block.PageNumber, block.Id, attempt, ex.Message));
                return block.Text;
            }
        }

        return block.Text;
    }

    private static TranslatedDocumentBlock ToTranslatedBlock(DocumentBlockIr block, string translatedText)
    {
        return new TranslatedDocumentBlock
        {
            Id = block.Id,
            PageNumber = block.PageNumber,
            ReadingOrder = block.ReadingOrder,
            BlockType = block.BlockType,
            SourceText = block.Text,
            SourceHash = block.SourceHash,
            TranslatedText = translatedText,
            Coordinates = block.Coordinates,
            ParentBlockId = block.ParentBlockId,
        };
    }

    private static string BuildStructuredOutput(IReadOnlyList<TranslatedDocumentPage> pages)
    {
        var sb = new StringBuilder();

        foreach (var page in pages.OrderBy(x => x.PageNumber))
        {
            sb.AppendLine($"## Page {page.PageNumber}");

            var lookup = page.Blocks.ToDictionary(x => x.Id, x => x);
            foreach (var block in page.Blocks.OrderBy(x => x.ReadingOrder))
            {
                if (block.BlockType == DocumentBlockType.Caption &&
                    !string.IsNullOrEmpty(block.ParentBlockId) &&
                    lookup.TryGetValue(block.ParentBlockId, out var parent))
                {
                    sb.AppendLine($"- Caption (for {parent.BlockType}:{parent.Id}): {block.TranslatedText}");
                    continue;
                }

                if (block.BlockType == DocumentBlockType.Paragraph)
                {
                    sb.AppendLine($"- Paragraph: {block.TranslatedText}");
                    continue;
                }

                sb.AppendLine($"- {block.BlockType}: {block.TranslatedText}");
            }

            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }

    private static string EnforceGlossary(string text, IReadOnlyDictionary<string, string> glossary)
    {
        var normalized = text;
        foreach (var item in glossary)
        {
            normalized = normalized.Replace(item.Key, item.Value, StringComparison.OrdinalIgnoreCase);
        }

        return normalized;
    }
}
