using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.LongDocument;

public sealed class LongDocumentTranslationService
{
    private static readonly Regex FormulaRegex = new(@"(\$[^$]+\$|\\\([^\)]+\\\)|\\\[[^\]]+\\\]|[A-Za-z]\s*=\s*[^\s]+)", RegexOptions.Compiled);

    private readonly Func<TranslationRequest, string, CancellationToken, Task<TranslationResult>> _translateWithService;
    private readonly Func<SourceDocumentPage, CancellationToken, Task<string?>> _ocrExtractor;

    public LongDocumentTranslationService(
        TranslationManager? manager = null,
        Func<TranslationRequest, string, CancellationToken, Task<TranslationResult>>? translateWithService = null,
        Func<SourceDocumentPage, CancellationToken, Task<string?>>? ocrExtractor = null)
    {
        if (translateWithService is not null)
        {
            _translateWithService = translateWithService;
        }
        else
        {
            var activeManager = manager ?? new TranslationManager();
            _translateWithService = (request, serviceId, ct) => activeManager.TranslateAsync(request, ct, serviceId);
        }

        _ocrExtractor = ocrExtractor ?? ((_, _) => Task.FromResult<string?>(null));
    }

    public async Task<LongDocumentTranslationResult> TranslateAsync(
        SourceDocument source,
        LongDocumentTranslationOptions options,
        CancellationToken cancellationToken = default)
    {
        if (options.MaxRetriesPerBlock < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxRetriesPerBlock), options.MaxRetriesPerBlock, "MaxRetriesPerBlock must be greater than or equal to 0.");
        }

        var timings = new Dictionary<string, long>();

        var ingestSw = Stopwatch.StartNew();
        var ingested = await IngestAsync(source, options, cancellationToken);
        ingestSw.Stop();
        timings["ingest"] = ingestSw.ElapsedMilliseconds;

        var buildIrSw = Stopwatch.StartNew();
        var ir = BuildIr(ingested);
        buildIrSw.Stop();
        timings["build-ir"] = buildIrSw.ElapsedMilliseconds;

        var formulaSw = Stopwatch.StartNew();
        ir = options.EnableFormulaProtection ? ApplyFormulaProtection(ir) : ir;
        formulaSw.Stop();
        timings["formula-protection"] = formulaSw.ElapsedMilliseconds;

        var translateSw = Stopwatch.StartNew();
        var translatedBlocks = await TranslateBlocksAsync(ir, options, cancellationToken);
        translateSw.Stop();
        timings["translate"] = translateSw.ElapsedMilliseconds;

        var layoutSw = Stopwatch.StartNew();
        var pages = BuildStructuredOutput(ir, translatedBlocks);
        layoutSw.Stop();
        timings["structured-layout-output"] = layoutSw.ElapsedMilliseconds;

        var failedBlocks = translatedBlocks.Values
            .Where(block => !string.IsNullOrWhiteSpace(block.LastError))
            .Select(block => new FailedBlockInfo
            {
                IrBlockId = block.IrBlockId,
                SourceBlockId = block.SourceBlockId,
                PageNumber = ir.Blocks.First(b => b.IrBlockId == block.IrBlockId).PageNumber,
                RetryCount = block.RetryCount,
                Error = block.LastError!
            })
            .ToList();

        var translatedCount = translatedBlocks.Values.Count(b => !b.TranslationSkipped && string.IsNullOrWhiteSpace(b.LastError));
        var skippedCount = translatedBlocks.Values.Count(b => b.TranslationSkipped);

        return new LongDocumentTranslationResult
        {
            Ir = ir,
            Pages = pages,
            QualityReport = new LongDocumentQualityReport
            {
                StageTimingsMs = timings,
                TotalBlocks = ir.Blocks.Count,
                TranslatedBlocks = translatedCount,
                SkippedBlocks = skippedCount,
                FailedBlocks = failedBlocks
            }
        };
    }

    private async Task<SourceDocument> IngestAsync(
        SourceDocument source,
        LongDocumentTranslationOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.EnableOcrFallback)
        {
            return source;
        }

        var pages = new List<SourceDocumentPage>(source.Pages.Count);
        foreach (var page in source.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!page.IsScanned || page.Blocks.Count > 0)
            {
                pages.Add(page);
                continue;
            }

            var ocrText = await _ocrExtractor(page, cancellationToken);
            if (string.IsNullOrWhiteSpace(ocrText))
            {
                pages.Add(page);
                continue;
            }

            pages.Add(page with
            {
                IsScanned = false,
                Blocks =
                [
                    new SourceDocumentBlock
                    {
                        BlockId = $"ocr-p{page.PageNumber}",
                        BlockType = SourceBlockType.Paragraph,
                        Text = ocrText,
                        IsFormulaLike = false
                    }
                ]
            });
        }

        return source with { Pages = pages };
    }

    private static DocumentIr BuildIr(SourceDocument source)
    {
        var irBlocks = new List<DocumentBlockIr>();

        foreach (var page in source.Pages)
        {
            foreach (var block in page.Blocks)
            {
                var irBlockId = $"ir-{page.PageNumber}-{block.BlockId}";
                var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(block.Text ?? string.Empty)));

                irBlocks.Add(new DocumentBlockIr
                {
                    IrBlockId = irBlockId,
                    PageNumber = page.PageNumber,
                    SourceBlockId = block.BlockId,
                    BlockType = MapBlockType(block.BlockType),
                    OriginalText = block.Text,
                    ProtectedText = block.Text,
                    SourceHash = sourceHash,
                    BoundingBox = block.BoundingBox,
                    ParentIrBlockId = block.ParentBlockId is null ? null : $"ir-{page.PageNumber}-{block.ParentBlockId}",
                    TranslationSkipped = block.BlockType == SourceBlockType.Formula || block.IsFormulaLike
                });
            }
        }

        return new DocumentIr
        {
            DocumentId = source.DocumentId,
            Blocks = irBlocks
        };
    }

    private static DocumentIr ApplyFormulaProtection(DocumentIr ir)
    {
        var blocks = ir.Blocks.Select(block =>
        {
            if (block.TranslationSkipped)
            {
                return block;
            }

            var protectedText = FormulaRegex.Replace(block.ProtectedText, m => $"[[FORMULA:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(m.Value))).Substring(0, 8)}]]");
            var shouldSkip = protectedText.StartsWith("[[FORMULA:", StringComparison.Ordinal) &&
                             protectedText.EndsWith("]]", StringComparison.Ordinal) &&
                             !protectedText.Contains(' ');

            return block with
            {
                ProtectedText = protectedText,
                TranslationSkipped = shouldSkip
            };
        }).ToList();

        return ir with { Blocks = blocks };
    }

    private async Task<Dictionary<string, TranslatedDocumentBlock>> TranslateBlocksAsync(
        DocumentIr ir,
        LongDocumentTranslationOptions options,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, TranslatedDocumentBlock>(StringComparer.Ordinal);

        foreach (var block in ir.Blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (block.TranslationSkipped)
            {
                result[block.IrBlockId] = new TranslatedDocumentBlock
                {
                    IrBlockId = block.IrBlockId,
                    SourceBlockId = block.SourceBlockId,
                    BlockType = block.BlockType,
                    OriginalText = block.OriginalText,
                    ProtectedText = block.ProtectedText,
                    TranslatedText = block.OriginalText,
                    SourceHash = block.SourceHash,
                    BoundingBox = block.BoundingBox,
                    TranslationSkipped = true,
                    RetryCount = 0
                };
                continue;
            }

            var retryCount = 0;
            string? lastError = null;
            string translatedText = block.ProtectedText;
            var translationSucceeded = false;

            for (; retryCount <= options.MaxRetriesPerBlock; retryCount++)
            {
                try
                {
                    var request = new TranslationRequest
                    {
                        Text = block.ProtectedText,
                        FromLanguage = options.FromLanguage,
                        ToLanguage = options.ToLanguage
                    };

                    var translated = await _translateWithService(request, options.ServiceId, cancellationToken);
                    translatedText = ApplyGlossary(translated.TranslatedText, options.Glossary);
                    lastError = null;
                    translationSucceeded = true;
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    if (retryCount >= options.MaxRetriesPerBlock)
                    {
                        translatedText = block.OriginalText;
                    }
                }
            }

            var effectiveRetryCount = translationSucceeded
                ? retryCount
                : Math.Min(retryCount, options.MaxRetriesPerBlock);

            result[block.IrBlockId] = new TranslatedDocumentBlock
            {
                IrBlockId = block.IrBlockId,
                SourceBlockId = block.SourceBlockId,
                BlockType = block.BlockType,
                OriginalText = block.OriginalText,
                ProtectedText = block.ProtectedText,
                TranslatedText = translatedText,
                SourceHash = block.SourceHash,
                BoundingBox = block.BoundingBox,
                TranslationSkipped = false,
                RetryCount = effectiveRetryCount,
                LastError = lastError
            };
        }

        return result;
    }

    private static IReadOnlyList<TranslatedDocumentPage> BuildStructuredOutput(
        DocumentIr ir,
        Dictionary<string, TranslatedDocumentBlock> translatedBlocks)
    {
        return ir.Blocks
            .GroupBy(b => b.PageNumber)
            .OrderBy(g => g.Key)
            .Select(group => new TranslatedDocumentPage
            {
                PageNumber = group.Key,
                Blocks = group
                    .Select(b => translatedBlocks[b.IrBlockId])
                    .ToList()
            })
            .ToList();
    }

    private static BlockType MapBlockType(SourceBlockType sourceType) => sourceType switch
    {
        SourceBlockType.Paragraph => BlockType.Paragraph,
        SourceBlockType.Heading => BlockType.Heading,
        SourceBlockType.Caption => BlockType.Caption,
        SourceBlockType.TableCell => BlockType.Table,
        SourceBlockType.Formula => BlockType.Formula,
        _ => BlockType.Unknown
    };

    private static string ApplyGlossary(string text, IReadOnlyDictionary<string, string>? glossary)
    {
        if (glossary is null || glossary.Count == 0)
        {
            return text;
        }

        var output = text;
        foreach (var pair in glossary)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }
            output = output.Replace(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase);
        }

        return output;
    }
}
