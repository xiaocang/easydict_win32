using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Easydict.TranslationService.FormulaProtection;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.LongDocument;

public sealed class LongDocumentTranslationService
{
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

    private static Task<DocumentIr> BuildIrAsync(SourceDocument source, LongDocumentTranslationOptions? options = null, CancellationToken cancellationToken = default)
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

                    var translationSkipped = block.BlockType == SourceBlockType.Formula || block.IsFormulaLike
                        || IsFontBasedFormula(block.DetectedFontNames, options?.FormulaFontPattern)
                        || IsCharacterBasedFormula(blockText, options?.FormulaCharPattern)
                        || IsSubscriptDenseFormula(block.FormulaCharacters);

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
                        FormulaCharacters = block.FormulaCharacters
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

    private static Task<DocumentIr> ApplyFormulaProtectionAsync(DocumentIr ir, CancellationToken cancellationToken = default)
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

                var protection = ProtectFormulaSpans(block.ProtectedText);
                var protectedText = protection.ProtectedText;
                var shouldSkip = IsFormulaOnlyText(protectedText);

                return block with
                {
                    ProtectedText = protectedText,
                    TranslationSkipped = shouldSkip
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

    private async Task<TranslatedDocumentBlock> TranslateSingleBlockAsync(
        DocumentBlockIr block,
        LongDocumentTranslationOptions options,
        CancellationToken cancellationToken)
    {
        var retryCount = 0;
        string? lastError = null;
        string translatedText = block.ProtectedText;
        var translationSucceeded = false;

        for (; retryCount <= options.MaxRetriesPerBlock; retryCount++)
        {
            try
            {
                // If the block contains formula placeholders {v0}, {v1}, ..., inject
                // a prompt instructing the LLM to preserve them — aligned with
                // pdf2zh translator.py: "Keep the formula notation {v*} unchanged."
                var customPrompt = options.CustomPrompt;
                if (options.EnableFormulaProtection && NumericPlaceholderRegex.IsMatch(block.ProtectedText))
                {
                    const string formulaPrompt = "Keep all {v0}, {v1}, ... formula placeholders exactly as-is. Do not translate, remove, or modify them.";
                    customPrompt = string.IsNullOrWhiteSpace(customPrompt)
                        ? formulaPrompt
                        : $"{customPrompt}\n{formulaPrompt}";
                }

                var request = new TranslationRequest
                {
                    Text = block.ProtectedText,
                    FromLanguage = options.FromLanguage,
                    ToLanguage = options.ToLanguage,
                    CustomPrompt = customPrompt
                };

                var translated = await _translateWithService(request, options.ServiceId, cancellationToken);
                translatedText = ApplyGlossary(translated.TranslatedText, options.Glossary);
                // Remove Unicode control characters that LLMs occasionally inject —
                // aligned with pdf2zh translator.py:36 remove_control_characters()
                translatedText = RemoveControlCharacters(translatedText);
                // Trim leading spaces on each line — aligned with pdf2zh converter.py:488
                translatedText = TrimLeadingSpacesPerLine(translatedText);
                var formulaProtection = options.EnableFormulaProtection
                    ? ProtectFormulaSpans(block.OriginalText)
                    : FormulaProtectionResult.Empty;
                translatedText = RestoreFormulaSpans(translatedText, formulaProtection, block.OriginalText);
                lastError = null;
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


    // Level 2: Font-based formula detection
    private static readonly Regex MathFontRegex = new(
        @"CM[^R]|CMSY|CMMI|CMEX|MS\.M|MSAM|MSBM|XY|MT\w*Math|Symbol|Euclid|Mathematica|MathematicalPi|STIX" +
        @"|\bBL|\bRM|\bEU|\bLA|\bRS" +     // pdf2zh: math font abbreviations (word-boundary anchored to avoid "la" in "Regular")
        @"|LINE|LCIRCLE" +                  // pdf2zh: LaTeX drawing fonts
        @"|TeX-|rsfs|txsy|wasy|stmary" +    // pdf2zh: TeX symbol font packages
        @"|\w+Sym\w*|\b\w{1,5}Math\w*",    // generic *Sym* / *Math* (prefix ≤5 chars to avoid "MyCustomMathFont")
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static bool IsFontBasedFormula(IReadOnlyList<string>? fontNames, string? customPattern)
    {
        if (fontNames is null || fontNames.Count == 0) return false;
        var pattern = !string.IsNullOrWhiteSpace(customPattern)
            ? new Regex(customPattern, RegexOptions.IgnoreCase)
            : MathFontRegex;
        var mathFontCount = fontNames.Count(f =>
        {
            // Strip PDF subset prefix (e.g. "ABCDE+CMSY10" → "CMSY10")
            // Aligned with pdf2zh converter.py:196: font.split("+")[-1]
            var name = f;
            var plusIdx = name.IndexOf('+');
            if (plusIdx >= 0 && plusIdx < name.Length - 1)
                name = name[(plusIdx + 1)..];
            return pattern.IsMatch(name);
        });
        // Aligned with pdf2zh vflag(): any math font presence is a strong signal.
        // Lowered from 0.5 to 0.3 to catch blocks with mixed math/text fonts.
        return mathFontCount > fontNames.Count * 0.3;
    }

    // Level 3: Character-based formula detection
    // Expanded to match pdf2zh vflag() Unicode categories:
    // Sm (Math symbols), Lm (Modifier letters), Mn (Non-spacing marks), Sk (Modifier symbols),
    // Greek (0370-03FF), superscripts/subscripts, letterlike symbols, misc math
    private static readonly Regex MathUnicodeRegex = new(
        @"[\u2200-\u22FF\u2100-\u214F\u0370-\u03FF\u2070-\u209F\u00B2\u00B3\u00B9\u2150-\u218F\u27C0-\u27EF\u2980-\u29FF" +
        @"\u02B0-\u02FF" +  // Modifier letters (Lm) — pdf2zh vflag
        @"\u0300-\u036F" +  // Combining diacritical marks (Mn) — pdf2zh vflag
        @"\u02C6-\u02CF" +  // Modifier symbols (Sk subset) — pdf2zh vflag
        @"\u2000-\u200B]",  // General punctuation / spaces (Zs) — pdf2zh vflag
        RegexOptions.Compiled);

    internal static bool IsCharacterBasedFormula(string text, string? customPattern)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var pattern = !string.IsNullOrWhiteSpace(customPattern)
            ? new Regex(customPattern)
            : MathUnicodeRegex;
        var mathCharCount = pattern.Matches(text).Count;
        // Count Unicode replacement characters (\uFFFD) — these often represent unmapped CID
        // glyphs in formula fonts. Aligned with pdf2zh converter.py:197 CID detection.
        mathCharCount += text.Count(c => c == '\uFFFD');
        // Lowered from 0.3 to 0.2 — pdf2zh flags individual math characters,
        // so a lower threshold catches more formula-heavy blocks.
        return text.Length > 0 && (double)mathCharCount / text.Length > 0.2;
    }

    // Level 4: Subscript/superscript density formula detection.
    // If a block contains math font characters and >25% are subscripts/superscripts,
    // it is likely a formula (e.g. "x_1, x_2, ..., x_n = f(y)").
    // Aligned with pdf2zh converter.py:243 child.size < pstk[-1].size * 0.79
    internal static bool IsSubscriptDenseFormula(BlockFormulaCharacters? formulaChars)
    {
        if (formulaChars?.Characters is not { Count: > 0 } chars) return false;
        if (!formulaChars.HasMathFontCharacters) return false;

        var scriptCount = chars.Count(c => c.IsSubscript || c.IsSuperscript);
        return chars.Count >= 3 && (double)scriptCount / chars.Count > 0.25;
    }

    private sealed record FormulaProtectionResult(string ProtectedText, IReadOnlyList<FormulaToken> TokenMap)
    {
        public static FormulaProtectionResult Empty { get; } = new(string.Empty, Array.Empty<FormulaToken>());
    }

    private static readonly Regex NumericPlaceholderRegex = new(@"\{v(\d+)\}", RegexOptions.Compiled);

    private static FormulaProtectionResult ProtectFormulaSpans(string text)
    {
        var protectedText = new FormulaProtector().Protect(text, out var tokens);
        return new FormulaProtectionResult(protectedText, tokens);
    }

    private static bool IsFormulaOnlyText(string protectedText)
    {
        if (string.IsNullOrWhiteSpace(protectedText))
        {
            return false;
        }

        var cleaned = NumericPlaceholderRegex.Replace(protectedText, string.Empty).Trim();
        return cleaned.Length == 0;
    }

    private static string RestoreFormulaSpans(string text, FormulaProtectionResult protection, string originalText)
    {
        return new FormulaRestorer().Restore(text, protection.TokenMap, originalText, useSimplified: false);
    }

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
