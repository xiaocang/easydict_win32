using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Easydict.TranslationService.ContentPreservation;
using Easydict.TranslationService.FormulaProtection;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.LongDocument;

public sealed class LongDocumentTranslationService
{
    private readonly Func<TranslationRequest, string, CancellationToken, Task<TranslationResult>> _translateWithService;
    private readonly Func<SourceDocumentPage, CancellationToken, Task<string?>> _ocrExtractor;
    private readonly IContentPreservationService _preservation = new FormulaPreservationService();

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

        if (options.MaxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxConcurrency), options.MaxConcurrency, "MaxConcurrency must be greater than or equal to 1.");
        }

        var timings = new Dictionary<string, long>();
        var progress = options.Progress;
        var totalPages = source.Pages.Count;

        // Parsing stage
        progress?.Report(new LongDocumentTranslationProgress
        {
            Stage = LongDocumentTranslationStage.Parsing,
            CurrentBlock = 0,
            TotalBlocks = source.Pages.Sum(p => p.Blocks.Count),
            CurrentPage = 0,
            TotalPages = totalPages,
            Percentage = 0
        });

        var ingestSw = Stopwatch.StartNew();
        var ingested = await IngestAsync(source, options, cancellationToken).ConfigureAwait(false);
        ingestSw.Stop();
        timings["ingest"] = ingestSw.ElapsedMilliseconds;

        // Yield control to allow UI thread to process messages
        await Task.Yield();

        // Building IR stage
        progress?.Report(new LongDocumentTranslationProgress
        {
            Stage = LongDocumentTranslationStage.BuildingIr,
            CurrentBlock = 0,
            TotalBlocks = ingested.Pages.Sum(p => p.Blocks.Count),
            CurrentPage = 0,
            TotalPages = totalPages,
            Percentage = 5
        });

        var buildIrSw = Stopwatch.StartNew();
        var ir = await BuildIrAsync(ingested, options, cancellationToken).ConfigureAwait(false);
        buildIrSw.Stop();
        timings["build-ir"] = buildIrSw.ElapsedMilliseconds;

        // Yield control to allow UI thread to process messages
        await Task.Yield();

        // Formula protection stage
        if (options.EnableFormulaProtection)
        {
            progress?.Report(new LongDocumentTranslationProgress
            {
                Stage = LongDocumentTranslationStage.FormulaProtection,
                CurrentBlock = 0,
                TotalBlocks = ir.Blocks.Count,
                CurrentPage = 0,
                TotalPages = totalPages,
                Percentage = 10
            });
        }

        var formulaSw = Stopwatch.StartNew();
        ir = options.EnableFormulaProtection ? await ApplyFormulaProtectionAsync(ir, cancellationToken).ConfigureAwait(false) : ir;
        formulaSw.Stop();
        timings["formula-protection"] = formulaSw.ElapsedMilliseconds;

        // Yield control to allow UI thread to process messages
        await Task.Yield();

        var translateSw = Stopwatch.StartNew();
        var translatedBlocks = await TranslateBlocksAsync(ir, options, cancellationToken).ConfigureAwait(false);
        translateSw.Stop();
        timings["translate"] = translateSw.ElapsedMilliseconds;

        // Yield control to allow UI thread to process messages
        await Task.Yield();

        // Exporting stage
        progress?.Report(new LongDocumentTranslationProgress
        {
            Stage = LongDocumentTranslationStage.Exporting,
            CurrentBlock = ir.Blocks.Count,
            TotalBlocks = ir.Blocks.Count,
            CurrentPage = totalPages,
            TotalPages = totalPages,
            Percentage = 95
        });

        var layoutSw = Stopwatch.StartNew();
        var pages = BuildStructuredOutput(ir, translatedBlocks);
        layoutSw.Stop();
        timings["structured-layout-output"] = layoutSw.ElapsedMilliseconds;

        // Complete
        progress?.Report(new LongDocumentTranslationProgress
        {
            Stage = LongDocumentTranslationStage.Exporting,
            CurrentBlock = ir.Blocks.Count,
            TotalBlocks = ir.Blocks.Count,
            CurrentPage = totalPages,
            TotalPages = totalPages,
            Percentage = 100
        });

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

    private Task<DocumentIr> BuildIrAsync(SourceDocument source, LongDocumentTranslationOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var irBlocks = new List<DocumentBlockIr>();

            foreach (var page in source.Pages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var block in page.Blocks)
                {
                    var blockText = block.Text ?? string.Empty;
                    var irBlockId = $"ir-{page.PageNumber}-{block.BlockId}";
                    var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(blockText)));

                    var blockContext = new BlockContext
                    {
                        Text = blockText,
                        BlockType = block.BlockType,
                        IsFormulaLike = block.IsFormulaLike,
                        DetectedFontNames = block.DetectedFontNames,
                        FormulaCharacters = block.FormulaCharacters,
                        FormulaFontPattern = options?.FormulaFontPattern,
                        FormulaCharPattern = options?.FormulaCharPattern,
                        CharacterLevelProtectedText = block.CharacterLevelProtectedText,
                        CharacterLevelTokens = block.CharacterLevelTokens
                    };
                    var plan = _preservation.Analyze(blockContext);
                    var translationSkipped = plan.SkipTranslation;

                    irBlocks.Add(new DocumentBlockIr
                    {
                        IrBlockId = irBlockId,
                        PageNumber = page.PageNumber,
                        SourceBlockId = block.BlockId,
                        BlockType = MapBlockType(block.BlockType),
                        OriginalText = blockText,
                        ProtectedText = blockText,
                        SourceHash = sourceHash,
                        BoundingBox = block.BoundingBox,
                        ParentIrBlockId = block.ParentBlockId is null ? null : $"ir-{page.PageNumber}-{block.ParentBlockId}",
                        TranslationSkipped = translationSkipped,
                        TextStyle = block.TextStyle,
                        FormulaCharacters = block.FormulaCharacters,
                        CharacterLevelProtectedText = block.CharacterLevelProtectedText,
                        CharacterLevelTokens = block.CharacterLevelTokens
                    });
                }
            }

            return new DocumentIr
            {
                DocumentId = source.DocumentId,
                Blocks = irBlocks
            };
        }, cancellationToken);
    }

    private Task<DocumentIr> ApplyFormulaProtectionAsync(DocumentIr ir, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var blocks = ir.Blocks.Select(block =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (block.TranslationSkipped)
                {
                    return block;
                }

                var blockContext = new BlockContext
                {
                    Text = block.OriginalText,
                    BlockType = MapToSourceBlockType(block.BlockType),
                    IsFormulaLike = false,
                    CharacterLevelProtectedText = block.CharacterLevelProtectedText,
                    CharacterLevelTokens = block.CharacterLevelTokens
                };
                var plan = new ProtectionPlan
                {
                    Mode = PreservationMode.None,
                    SkipTranslation = false
                };
                var protectedBlock = _preservation.Protect(blockContext, plan);

                return block with
                {
                    ProtectedText = protectedBlock.ProtectedText,
                    FormulaTokenMap = protectedBlock.Tokens,
                    SoftProtectedSpans = protectedBlock.SoftSpans,
                    TranslationSkipped = protectedBlock.Plan.SkipTranslation,
                    PreservationContext = blockContext
                };
            }).ToList();

            return ir with { Blocks = blocks };
        }, cancellationToken);
    }

    private async Task<Dictionary<string, TranslatedDocumentBlock>> TranslateBlocksAsync(
        DocumentIr ir,
        LongDocumentTranslationOptions options,
        CancellationToken cancellationToken)
    {
        var result = new ConcurrentDictionary<string, TranslatedDocumentBlock>(StringComparer.Ordinal);

        // Separate skipped blocks (no translation needed) from blocks to translate
        var blocksToTranslate = new List<DocumentBlockIr>();
        foreach (var block in ir.Blocks)
        {
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
                    RetryCount = 0,
                    TextStyle = block.TextStyle,
                    FormulaCharacters = block.FormulaCharacters
                };
            }
            else
            {
                blocksToTranslate.Add(block);
            }
        }

        var totalPages = ir.Blocks.Max(b => b.PageNumber);
        var totalBlocks = blocksToTranslate.Count;
        var completedBlocks = 0;
        var progress = options.Progress;

        // Report initial progress
        progress?.Report(new LongDocumentTranslationProgress
        {
            Stage = LongDocumentTranslationStage.Translating,
            CurrentBlock = 0,
            TotalBlocks = totalBlocks,
            CurrentPage = 0,
            TotalPages = totalPages,
            Percentage = 0,
            CurrentBlockPreview = null
        });

        var concurrency = Math.Max(1, options.MaxConcurrency);
        if (concurrency == 1)
        {
            // Sequential path (default, backward-compatible)
            for (var i = 0; i < blocksToTranslate.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var block = blocksToTranslate[i];
                var translated = await TranslateSingleBlockAsync(block, options, cancellationToken);
                result[block.IrBlockId] = translated;
                completedBlocks++;

                // Report progress after each block
                progress?.Report(new LongDocumentTranslationProgress
                {
                    Stage = LongDocumentTranslationStage.Translating,
                    CurrentBlock = completedBlocks,
                    TotalBlocks = totalBlocks,
                    CurrentPage = block.PageNumber,
                    TotalPages = totalPages,
                    Percentage = (double)completedBlocks / totalBlocks * 100,
                    CurrentBlockPreview = block.OriginalText.Length > 50
                        ? block.OriginalText.Substring(0, 50) + "..."
                        : block.OriginalText
                });
            }
        }
        else
        {
            // Parallel path with semaphore-controlled concurrency
            using var semaphore = new SemaphoreSlim(concurrency, concurrency);
            var tasks = blocksToTranslate.Select(async block =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var translated = await TranslateSingleBlockAsync(block, options, cancellationToken);
                    result[block.IrBlockId] = translated;

                    // Thread-safe progress reporting in parallel path
                    var current = Interlocked.Increment(ref completedBlocks);
                    progress?.Report(new LongDocumentTranslationProgress
                    {
                        Stage = LongDocumentTranslationStage.Translating,
                        CurrentBlock = current,
                        TotalBlocks = totalBlocks,
                        CurrentPage = block.PageNumber,
                        TotalPages = totalPages,
                        Percentage = (double)current / totalBlocks * 100,
                        CurrentBlockPreview = block.OriginalText.Length > 50
                            ? block.OriginalText.Substring(0, 50) + "..."
                            : block.OriginalText
                    });
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }

        return new Dictionary<string, TranslatedDocumentBlock>(result, StringComparer.Ordinal);
    }

    private static string BuildQualityFeedbackError(RestoreOutcome outcome)
    {
        var parts = new List<string>
        {
            $"quality-feedback:{outcome.Status}",
            $"missing={outcome.MissingTokenCount}"
        };

        if (outcome.SoftValidationStatus != SoftValidationStatus.None)
        {
            parts.Add($"soft={outcome.SoftValidationStatus}");
        }

        if (outcome.SoftFailureCount > 0)
        {
            parts.Add($"softFailures={outcome.SoftFailureCount}");
        }

        if (outcome.SyntheticDelimiterStripCount > 0)
        {
            parts.Add($"softStrips={outcome.SyntheticDelimiterStripCount}");
        }

        return string.Join(' ', parts);
    }

    private async Task<TranslatedDocumentBlock> TranslateSingleBlockAsync(
        DocumentBlockIr block,
        LongDocumentTranslationOptions options,
        CancellationToken cancellationToken)
    {
        var retryCount = 0;
        string? lastError = null;
        string translatedText = block.ProtectedText;
        var translationSucceeded = false;

        // Mutated across retries when quality feedback triggers re-protection at a higher demoteLevel.
        var currentProtectedText = block.ProtectedText;
        var currentTokens = block.FormulaTokenMap;
        var currentSoftSpans = block.SoftProtectedSpans;
        var currentRetryAttempt = 0;
        var lastSoftValidationFailed = false;

        for (; retryCount <= options.MaxRetriesPerBlock; retryCount++)
        {
            try
            {
                // Two-tier formula prompt: {vN} = hard, $...$ = soft.
                var customPrompt = options.CustomPrompt;
                if (options.EnableFormulaProtection)
                {
                    var hasHardTokens = currentTokens is { Count: > 0 };
                    var hasSoftMath = currentSoftSpans is { Count: > 0 };
                    var hasExactSoftSpans = false;
                    if (currentSoftSpans is not null)
                    {
                        foreach (var span in currentSoftSpans)
                        {
                            if (span.RequiresExactPreservation) { hasExactSoftSpans = true; break; }
                        }
                    }

                    string? formulaPrompt = null;
                    if (hasHardTokens && hasSoftMath)
                    {
                        formulaPrompt = "This text has formula placeholders ({v0}, {v1}, ...) and inline math ($...$). " +
                            "Keep all {vN} placeholders exactly as-is. " +
                            "For $...$ content: if it is a mathematical formula or technical identifier, keep it unchanged; " +
                            "if it is ordinary text, translate it and remove the $ delimiters.";
                    }
                    else if (hasHardTokens)
                    {
                        formulaPrompt = "Keep all {v0}, {v1}, ... formula placeholders exactly as-is. Do not translate, remove, or modify them.";
                    }
                    else if (hasSoftMath)
                    {
                        formulaPrompt = "Content in $...$ is likely a mathematical formula or technical identifier. " +
                            "If it is math, keep it unchanged. If it is ordinary text, translate it and remove the $ delimiters.";
                    }

                    if (currentRetryAttempt >= 1 && formulaPrompt is not null)
                    {
                        var retryInstruction = lastSoftValidationFailed && hasExactSoftSpans
                            ? "The previous translation attempt changed a protected technical symbol sequence. " +
                              "Copy every technical symbol sequence inside synthetic $...$ verbatim, and do not keep the synthetic $ delimiters in the final output.\n"
                            : "The previous translation attempt lost some protected content. " +
                              "Translate carefully and preserve EVERY {vN} placeholder and every $...$ span exactly as written.\n";
                        formulaPrompt = retryInstruction + formulaPrompt;
                    }

                    if (formulaPrompt is not null)
                    {
                        customPrompt = string.IsNullOrWhiteSpace(customPrompt)
                            ? formulaPrompt
                            : $"{customPrompt}\n{formulaPrompt}";
                    }
                }

                var request = new TranslationRequest
                {
                    Text = currentProtectedText,
                    FromLanguage = options.FromLanguage,
                    ToLanguage = options.ToLanguage,
                    CustomPrompt = customPrompt
                };

                var translated = await _translateWithService(request, options.ServiceId, cancellationToken);
                translatedText = ApplyGlossary(translated.TranslatedText, options.Glossary);
                translatedText = RemoveControlCharacters(translatedText);
                translatedText = TrimLeadingSpacesPerLine(translatedText);
                if (options.EnableFormulaProtection &&
                    (currentTokens is { Count: > 0 } || currentSoftSpans is { Count: > 0 }))
                {
                    var protectedBlock = new ProtectedBlock
                    {
                        OriginalText = block.OriginalText,
                        ProtectedText = currentProtectedText,
                        Tokens = currentTokens ?? Array.Empty<FormulaToken>(),
                        SoftSpans = currentSoftSpans ?? Array.Empty<SoftProtectedSpan>(),
                        Plan = new ProtectionPlan { Mode = PreservationMode.InlineProtected, SkipTranslation = false }
                    };
                    var outcome = _preservation.Restore(translatedText, protectedBlock);
                    translatedText = _preservation.ResolveFallback(outcome, protectedBlock);

                    var hasQualityIssue = outcome.Status != RestoreStatus.FullRestore
                        || outcome.SoftValidationStatus == SoftValidationStatus.Failed;
                    var shouldRetryForQuality = options.EnableQualityFeedbackRetry
                        && retryCount < options.MaxRetriesPerBlock
                        && hasQualityIssue;
                    if (shouldRetryForQuality)
                    {
                        currentRetryAttempt++;
                        var retryContext = (block.PreservationContext ?? new BlockContext
                        {
                            Text = block.OriginalText,
                            BlockType = MapToSourceBlockType(block.BlockType)
                        }) with { RetryAttempt = currentRetryAttempt };
                        var reprotected = _preservation.Protect(retryContext, new ProtectionPlan
                        {
                            Mode = PreservationMode.None,
                            SkipTranslation = false
                        });
                        currentProtectedText = reprotected.ProtectedText;
                        currentTokens = reprotected.Tokens;
                        currentSoftSpans = reprotected.SoftSpans;
                        lastSoftValidationFailed = outcome.SoftValidationStatus == SoftValidationStatus.Failed;
                        lastError = BuildQualityFeedbackError(outcome);
                        Debug.WriteLine($"[LongDoc] Block {block.SourceBlockId}: quality feedback retry #{currentRetryAttempt} " +
                            $"(status={outcome.Status}, missing={outcome.MissingTokenCount}, " +
                            $"softStatus={outcome.SoftValidationStatus}, softFailures={outcome.SoftFailureCount})");
                        continue;
                    }

                    if (hasQualityIssue)
                    {
                        lastError = BuildQualityFeedbackError(outcome);
                        lastSoftValidationFailed = outcome.SoftValidationStatus == SoftValidationStatus.Failed;
                        Debug.WriteLine($"[LongDoc] Block {block.SourceBlockId}: unresolved quality issue " +
                            $"(status={outcome.Status}, missing={outcome.MissingTokenCount}, " +
                            $"softStatus={outcome.SoftValidationStatus}, softFailures={outcome.SoftFailureCount})");
                        break;
                    }
                }
                lastError = null;
                lastSoftValidationFailed = false;
                translationSucceeded = true;
                break;
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine($"[LongDoc] Block {block.SourceBlockId}: cancelled at attempt {retryCount + 1}");
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                var errorType = ex is TranslationException te ? te.ErrorCode.ToString() : ex.GetType().Name;
                Debug.WriteLine($"[LongDoc] Block {block.SourceBlockId}: attempt {retryCount + 1}/{options.MaxRetriesPerBlock + 1} failed ({errorType}): {ex.Message}");
                if (retryCount >= options.MaxRetriesPerBlock)
                {
                    Debug.WriteLine($"[LongDoc] Block {block.SourceBlockId} permanently failed after {retryCount + 1} attempt(s)");
                    translatedText = block.OriginalText;
                }
            }
        }

        var effectiveRetryCount = translationSucceeded
            ? retryCount
            : Math.Min(retryCount, options.MaxRetriesPerBlock);

        return new TranslatedDocumentBlock
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
            LastError = lastError,
            TextStyle = block.TextStyle,
            FormulaCharacters = block.FormulaCharacters
        };
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

    private static SourceBlockType MapToSourceBlockType(BlockType blockType) => blockType switch
    {
        BlockType.Paragraph => SourceBlockType.Paragraph,
        BlockType.Heading => SourceBlockType.Heading,
        BlockType.Caption => SourceBlockType.Caption,
        BlockType.Table => SourceBlockType.TableCell,
        BlockType.Formula => SourceBlockType.Formula,
        _ => SourceBlockType.Unknown
    };


    // --- Delegating wrappers for backward compatibility with existing tests ---
    // The real implementations live in FormulaPreservationService (ContentPreservation layer).

    internal static bool IsFontBasedFormula(IReadOnlyList<string>? fontNames, string? customPattern)
        => FormulaPreservationService.IsFontBasedFormula(fontNames, customPattern);

    internal static bool IsCharacterBasedFormula(string text, string? customPattern)
        => FormulaPreservationService.IsCharacterBasedFormula(text, customPattern);

    internal static bool IsSubscriptDenseFormula(BlockFormulaCharacters? formulaChars)
        => FormulaPreservationService.IsSubscriptDenseFormula(formulaChars);

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

    /// <summary>
    /// Removes Unicode control characters (category C) from translated text,
    /// preserving newline, carriage return, and tab.
    /// Aligned with pdf2zh translator.py:36 remove_control_characters().
    /// </summary>
    internal static string RemoveControlCharacters(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return new string(text.Where(c =>
            !char.IsControl(c) || c == '\n' || c == '\r' || c == '\t').ToArray());
    }

    /// <summary>
    /// Trims leading whitespace on each line of the translated text.
    /// Aligned with pdf2zh converter.py:488 which skips leading spaces after line breaks.
    /// </summary>
    internal static string TrimLeadingSpacesPerLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimStart();
        return string.Join('\n', lines);
    }
}
