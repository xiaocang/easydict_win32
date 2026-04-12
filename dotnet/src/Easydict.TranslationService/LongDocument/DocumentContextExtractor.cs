using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.LongDocument;

/// <summary>
/// Pass 1 of two-pass long-document translation.
///
/// Reads the entire document page-by-page (no truncation), in parallel up to
/// <see cref="LongDocumentTranslationOptions.MaxConcurrency"/>, and produces a
/// <see cref="DocumentContext"/> with:
///   * Glossary — proper nouns / technical terms → chosen translations
///   * Summary — 1-3 sentence document overview
///   * PreservationHints — verbatim source snippets (table cells, code, URLs,
///     identifiers, …) that Pass 2 should NOT translate
///
/// Failure of any single page degrades to an empty contribution from that page;
/// failure of the whole extractor returns <see cref="DocumentContext.Empty"/>
/// and Pass 2 proceeds with its existing per-block behavior.
/// </summary>
public sealed class DocumentContextExtractor
{
    private readonly Func<TranslationRequest, string, CancellationToken, Task<TranslationResult>> _translateWithService;

    public DocumentContextExtractor(
        Func<TranslationRequest, string, CancellationToken, Task<TranslationResult>> translateWithService)
    {
        _translateWithService = translateWithService ?? throw new ArgumentNullException(nameof(translateWithService));
    }

    /// <summary>
    /// Map-reduce extraction. Groups blocks by page, fans out one LLM call per page,
    /// then optionally issues one extra LLM call to merge the per-page summaries into
    /// a single document-level summary. Always returns a non-null context (Empty on failure).
    /// </summary>
    public async Task<DocumentContext> ExtractAsync(
        IReadOnlyList<DocumentBlockIr> blocks,
        LongDocumentTranslationOptions options,
        IProgress<DocumentContextProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (blocks is null) throw new ArgumentNullException(nameof(blocks));
        if (options is null) throw new ArgumentNullException(nameof(options));

        var totalSw = Stopwatch.StartNew();

        // Group blocks by page, preserving reading order within each page.
        var pageGroups = blocks
            .Where(b => !b.TranslationSkipped && !string.IsNullOrWhiteSpace(b.OriginalText))
            .GroupBy(b => b.PageNumber)
            .OrderBy(g => g.Key)
            .Select(g => new PageBatch
            {
                PageNumber = g.Key,
                Text = string.Join("\n\n", g.Select(b => b.OriginalText.Trim()).Where(t => t.Length > 0))
            })
            .Where(p => p.Text.Length > 0)
            .ToArray();

        if (pageGroups.Length == 0)
        {
            totalSw.Stop();
            return DocumentContext.Empty with { ExtractionTimeMs = totalSw.ElapsedMilliseconds };
        }

        progress?.Report(new DocumentContextProgress
        {
            MappedPages = 0,
            TotalPages = pageGroups.Length,
            IsReducing = false
        });

        // MAP — fan out per-page LLM calls bounded by MaxConcurrency.
        var maxConcurrency = Math.Max(1, options.MaxConcurrency);
        using var semaphore = new SemaphoreSlim(maxConcurrency);
        var partials = new PagePartial[pageGroups.Length];
        var completedCount = 0;

        var mapTasks = new Task[pageGroups.Length];
        for (var i = 0; i < pageGroups.Length; i++)
        {
            var idx = i;
            var batch = pageGroups[idx];
            mapTasks[idx] = Task.Run(async () =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    partials[idx] = await MapPageAsync(batch, options, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Single-page failure: contribute empty so the rest still flows.
                    partials[idx] = new PagePartial
                    {
                        PageNumber = batch.PageNumber,
                        Summary = string.Empty,
                        Glossary = new Dictionary<string, string>(),
                        Hints = Array.Empty<string>(),
                        Failed = true,
                    };
                }
                finally
                {
                    semaphore.Release();
                }

                var completed = Interlocked.Increment(ref completedCount);
                progress?.Report(new DocumentContextProgress
                {
                    MappedPages = completed,
                    TotalPages = pageGroups.Length,
                    IsReducing = false
                });
            }, cancellationToken);
        }

        try
        {
            await Task.WhenAll(mapTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        // REDUCE — merge glossaries, hints, and summaries.
        progress?.Report(new DocumentContextProgress
        {
            MappedPages = pageGroups.Length,
            TotalPages = pageGroups.Length,
            IsReducing = true
        });

        var glossary = MergeGlossaries(partials);
        var hints = MergeHints(partials);
        var summary = await ReduceSummariesAsync(partials, options, cancellationToken).ConfigureAwait(false);

        totalSw.Stop();

        return new DocumentContext
        {
            Summary = summary,
            Glossary = glossary,
            PreservationHints = hints,
            ExtractionTimeMs = totalSw.ElapsedMilliseconds,
        };
    }

    // ---- MAP ----

    private async Task<PagePartial> MapPageAsync(
        PageBatch batch,
        LongDocumentTranslationOptions options,
        CancellationToken cancellationToken)
    {
        var request = new TranslationRequest
        {
            Text = batch.Text,
            FromLanguage = options.FromLanguage,
            ToLanguage = options.ToLanguage,
            BypassCache = true,
            CustomPrompt = MapPagePrompt,
        };

        var result = await _translateWithService(request, options.ServiceId, cancellationToken).ConfigureAwait(false);

        // The LLM is asked to NOT translate and instead return JSON. Models still
        // sometimes wrap output in ```json fences or add prose around it; tolerate that.
        var parsed = TryParsePagePartial(result.TranslatedText, batch.PageNumber);
        return parsed ?? new PagePartial
        {
            PageNumber = batch.PageNumber,
            Summary = string.Empty,
            Glossary = new Dictionary<string, string>(),
            Hints = Array.Empty<string>(),
            Failed = true,
        };
    }

    internal static PagePartial? TryParsePagePartial(string raw, int pageNumber)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var json = StripCodeFence(raw.Trim());

        // Find first '{' and last '}' as a coarse JSON boundary in case the model added prose.
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        json = json[start..(end + 1)];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var summary = root.TryGetProperty("summary", out var sEl) && sEl.ValueKind == JsonValueKind.String
                ? sEl.GetString() ?? string.Empty
                : string.Empty;

            var glossary = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("glossary", out var gEl) && gEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in gEl.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.String) continue;
                    var k = prop.Name?.Trim();
                    var v = prop.Value.GetString()?.Trim();
                    if (string.IsNullOrEmpty(k) || string.IsNullOrEmpty(v)) continue;
                    glossary[k] = v;
                }
            }

            var hints = new List<string>();
            if (root.TryGetProperty("preservation_hints", out var hEl) && hEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in hEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var s = item.GetString()?.Trim();
                    if (string.IsNullOrEmpty(s) || s.Length < 3) continue;
                    hints.Add(s);
                }
            }

            return new PagePartial
            {
                PageNumber = pageNumber,
                Summary = summary.Trim(),
                Glossary = glossary,
                Hints = hints,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string StripCodeFence(string s)
    {
        // Strip ```json ... ``` or ``` ... ``` if present.
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = s.IndexOf('\n');
            if (nl > 0) s = s[(nl + 1)..];
            if (s.EndsWith("```", StringComparison.Ordinal)) s = s[..^3];
        }
        return s.Trim();
    }

    // ---- REDUCE ----

    private static IReadOnlyDictionary<string, string> MergeGlossaries(PagePartial[] partials)
    {
        // For each source term, count distinct target renderings across pages.
        // Pick the rendering that appears in the most pages; tie-break by earliest page.
        var perTerm = new Dictionary<string, Dictionary<string, (int Count, int FirstPage)>>(StringComparer.Ordinal);
        foreach (var p in partials)
        {
            if (p is null || p.Failed || p.Glossary is null) continue;
            foreach (var (src, tgt) in p.Glossary)
            {
                if (!perTerm.TryGetValue(src, out var byTarget))
                {
                    byTarget = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
                    perTerm[src] = byTarget;
                }
                if (byTarget.TryGetValue(tgt, out var stat))
                    byTarget[tgt] = (stat.Count + 1, stat.FirstPage);
                else
                    byTarget[tgt] = (1, p.PageNumber);
            }
        }

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (src, byTarget) in perTerm)
        {
            var best = byTarget
                .OrderByDescending(kv => kv.Value.Count)
                .ThenBy(kv => kv.Value.FirstPage)
                .First();
            merged[src] = best.Key;
        }
        return merged;
    }

    private static IReadOnlyList<string> MergeHints(PagePartial[] partials)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var p in partials)
        {
            if (p is null || p.Failed || p.Hints is null) continue;
            foreach (var hint in p.Hints)
            {
                if (string.IsNullOrWhiteSpace(hint)) continue;
                var trimmed = hint.Trim();
                if (trimmed.Length < 3) continue;
                if (seen.Add(trimmed)) ordered.Add(trimmed);
            }
        }
        return ordered;
    }

    private async Task<string> ReduceSummariesAsync(
        PagePartial[] partials,
        LongDocumentTranslationOptions options,
        CancellationToken cancellationToken)
    {
        var nonEmpty = partials
            .Where(p => p is not null && !p.Failed && !string.IsNullOrWhiteSpace(p.Summary))
            .ToArray();

        if (nonEmpty.Length == 0) return string.Empty;
        if (nonEmpty.Length == 1) return nonEmpty[0].Summary;

        // Build numbered list of per-page summaries for the reduce call.
        var sb = new StringBuilder();
        for (var i = 0; i < nonEmpty.Length; i++)
        {
            sb.Append(i + 1).Append(". ").AppendLine(nonEmpty[i].Summary);
        }

        var request = new TranslationRequest
        {
            Text = sb.ToString(),
            FromLanguage = options.FromLanguage,
            ToLanguage = options.ToLanguage,
            BypassCache = true,
            CustomPrompt = ReduceSummaryPrompt,
        };

        try
        {
            var result = await _translateWithService(request, options.ServiceId, cancellationToken).ConfigureAwait(false);
            var merged = result.TranslatedText?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(merged)) return merged;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fall through to concat fallback.
        }

        // Fallback: first sentence of each partial, capped at 3 sentences.
        var sentences = new List<string>();
        foreach (var p in nonEmpty)
        {
            var firstDot = p.Summary.IndexOfAny(new[] { '.', '。', '！', '!', '?', '？' });
            sentences.Add(firstDot > 0 ? p.Summary[..(firstDot + 1)].Trim() : p.Summary.Trim());
            if (sentences.Count >= 3) break;
        }
        return string.Join(' ', sentences);
    }

    // ---- Prompts ----

    internal const string MapPagePrompt = """
Do NOT translate the document text. Analyze it and respond with a single JSON object (no prose, no markdown fences) with exactly these three fields:

"summary": a 1-3 sentence overview of this page's content, topic, domain, and terminology style.

"glossary": an object mapping source-language terms to chosen target-language renderings. Include proper nouns, place names, person names, product / model names, and technical terms. Pick ONE consistent rendering per term. Example: {"Transformer": "Transformer", "self-attention": "自注意力"}.

"preservation_hints": an array of verbatim source-text snippets that should NOT be translated in the second pass. Include items like:
  * tabular data: EVERY header cell and EVERY data row of any table on this page (numeric benchmark tables, hyperparameter tables, model comparison tables — list each cell value as its own entry, and also list the column header row verbatim)
  * code fragments, command lines, file paths
  * URLs and email addresses
  * identifiers, variable names, hyperparameter lists
  * proper nouns and product names that should stay verbatim
  * short fragments that look like noise / garbled text
  * any standalone snippet whose translation would degrade quality
Each entry must be a verbatim substring of the source so the second pass can match by Contains/Equals. Do not paraphrase. Do not add quote marks. If there are no items in a category, omit them.

Do NOT include section or subsection headings (short standalone lines that label a structural part of the document, typically beginning with a numeric index like "1", "2.3", or with a common part-name word) — those should always be translated.

Return ONLY the JSON object, nothing else.
""";

    internal const string ReduceSummaryPrompt = """
The numbered list below contains partial summaries of consecutive pages of the same document. Merge them into a single 1-3 sentence summary that covers the document as a whole — its topic, domain, and terminology style. Do not list the pages individually. Respond with the merged summary text only, no JSON, no prose around it.
""";

    // ---- Internal types ----

    internal sealed record PageBatch
    {
        public required int PageNumber { get; init; }
        public required string Text { get; init; }
    }

    internal sealed record PagePartial
    {
        public required int PageNumber { get; init; }
        public required string Summary { get; init; }
        public required IReadOnlyDictionary<string, string> Glossary { get; init; }
        public required IReadOnlyList<string> Hints { get; init; }
        public bool Failed { get; init; }
    }
}

/// <summary>
/// Sub-progress for the document-context stage. Reported every time a page map call
/// completes, and once when the reduce call begins.
/// </summary>
public sealed record DocumentContextProgress
{
    public required int MappedPages { get; init; }
    public required int TotalPages { get; init; }
    public required bool IsReducing { get; init; }
}
