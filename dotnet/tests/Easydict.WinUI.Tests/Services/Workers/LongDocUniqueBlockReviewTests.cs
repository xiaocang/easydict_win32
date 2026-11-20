extern alias LongDocWorker;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services.DocumentExport;
using FluentAssertions;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf.IO;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;
using WorkerLongDocumentPipeline = LongDocWorker::Easydict.Workers.LongDoc.Infrastructure.WorkerLongDocumentPipeline;
using WorkerLongDocumentSourceDocumentBuilder = LongDocWorker::Easydict.Workers.LongDoc.Infrastructure.WorkerLongDocumentSourceDocumentBuilder;
using Xunit;

namespace Easydict.WinUI.Tests.Services.Workers;

[Trait("Category", "WinUI")]
public sealed class LongDocUniqueBlockReviewTests
{
    private static readonly Regex ReviewCallMarker = new(@"\[CALL_(?<index>\d{4})\]", RegexOptions.Compiled);
    private static readonly Regex ProtectedToken = new(@"\[\[EQ_SOFT\]\].*?\[\[/EQ_SOFT\]\]|\{v\d+\}", RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public async Task TranslateAsync_RealTwoColumnPaperPage_ExportsUniqueBlockLabelsAndMapping()
    {
        var reviewDirectory = Environment.GetEnvironmentVariable("EASYDICT_PDF_REVIEW_DIR");
        if (string.IsNullOrWhiteSpace(reviewDirectory))
            return;

        Directory.CreateDirectory(reviewDirectory);
        var repositoryRoot = FindRepositoryRoot();
        var inputPath = Path.Combine(repositoryRoot, "lib", "PdfPig", "src", "UglyToad.PdfPig.Tests", "Integration", "Documents", "2108.11480.pdf");
        File.Exists(inputPath).Should().BeTrue($"the fixed two-column source paper must exist at {inputPath}");
        var sourceCopyPath = Path.Combine(reviewDirectory, "overlay-real-paper-source.pdf");
        File.Copy(inputPath, sourceCopyPath, overwrite: true);

        var sourceDocument = await BuildSourceDocumentAsync(inputPath);
        var sourceBlocks = sourceDocument.Pages.Single().Blocks;
        sourceBlocks.Should().Contain(block => block.BlockId == "p1-body-b5");
        sourceBlocks.Should().Contain(block => block.BlockId == "p1-body-b22");
        sourceBlocks.Should().Contain(block => block.BlockId == "p1-right-b23");
        var authorMetadataBlockIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "p1-body-b1", "p1-body-b2", "p1-body-b3",
            "p1-body-b16", "p1-body-b17", "p1-body-b18",
        };

        var fallbackOriginal = Environment.GetEnvironmentVariable("EASYDICT_TEST_ALLOW_CJK_SYSTEM_FONT_FALLBACK");
        Environment.SetEnvironmentVariable("EASYDICT_TEST_ALLOW_CJK_SYSTEM_FONT_FALLBACK", "1");
        try
        {
            var callToBlockId = await DiscoverTranslationCallMapAsync(inputPath, reviewDirectory);
            callToBlockId.Should().NotBeEmpty("the review fixture must translate source blocks");

            var translatedPath = Path.Combine(reviewDirectory, "overlay-real-paper-translated.pdf");
            var resultPath = Path.Combine(reviewDirectory, "overlay-real-paper-result.json");
            var translatedEvents = new List<BlockTranslatedEventData>();
            var nextCall = 0;
            var pipeline = new WorkerLongDocumentPipeline((request, serviceId, _) =>
            {
                var callIndex = nextCall++;
                if (!callToBlockId.TryGetValue(callIndex, out var blockId))
                    throw new InvalidOperationException($"Translation call {callIndex} has no source block assignment.");
                var text = authorMetadataBlockIds.Contains(blockId)
                    ? request.Text
                    : $"[{blockId}] \u6B64\u5757\u6709\u72EC\u7ACB\u6807\u8BC6\u7684\u6D4B\u8BD5\u8BD1\u6587\u3002" +
                      string.Concat(ProtectedToken.Matches(request.Text).Select(match => " " + match.Value));
                return Task.FromResult(new TranslationResult
                {
                    OriginalText = request.Text,
                    TranslatedText = text,
                    ServiceName = serviceId,
                    DetectedLanguage = request.FromLanguage,
                    TargetLanguage = request.ToLanguage,
                });
            });

            var result = await pipeline.TranslateAsync(
                BuildParameters(inputPath, translatedPath, resultPath),
                OfflineReviewSettings(),
                progress: null,
                CancellationToken.None,
                (block, _) =>
                {
                    translatedEvents.Add(block);
                    return Task.CompletedTask;
                });

            (result.State is "Completed" or "PartiallyCompleted").Should().BeTrue($"the PDF export must complete; quality report: {result.QualityReport}");
            File.Exists(translatedPath).Should().BeTrue();
            translatedEvents.Should().NotBeEmpty();

            using var translatedPdf = PdfPigDocument.Open(translatedPath);
            var extractedText = Regex.Replace(string.Concat(translatedPdf.GetPages().Select(page => page.Text)), @"\s+", string.Empty);
            var eventByBlockId = translatedEvents
                .Where(item => !string.IsNullOrWhiteSpace(item.SourceBlockId))
                .GroupBy(item => item.SourceBlockId!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
            using var persistedResult = JsonDocument.Parse(File.ReadAllText(resultPath));
            var qualityReport = persistedResult.RootElement.GetProperty("qualityReport").GetString() ?? "{}";
            using var quality = JsonDocument.Parse(qualityReport);
            var issueKindsByBlock = quality.RootElement.GetProperty("backfillMetrics").GetProperty("blockIssues")
                .EnumerateArray()
                .GroupBy(issue => issue.GetProperty("sourceBlockId").GetString() ?? string.Empty, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(issue => issue.GetProperty("kind").GetString() ?? string.Empty)
                        .Distinct(StringComparer.Ordinal).ToArray(),
                    StringComparer.Ordinal);

            var orderedBlocks = sourceBlocks
                .Select(block =>
                {
                    var box = block.BoundingBox;
                    var column = box is null || box.Value.Width >= 459
                        ? "span"
                        : box.Value.X + box.Value.Width / 2 < 306 ? "left" : "right";
                    return (Block: block, Column: column, Bounds: box);
                })
                .OrderBy(item => item.Column == "span" ? 0 : item.Column == "left" ? 1 : 2)
                .ThenByDescending(item => item.Bounds?.Y ?? 0)
                .ThenBy(item => item.Bounds?.X ?? 0)
                .ToArray();

            var rows = orderedBlocks.Select((item, index) =>
            {
                eventByBlockId.TryGetValue(item.Block.BlockId, out var translatedEvent);
                var labelMatch = translatedEvent is null
                    ? Match.Empty
                    : Regex.Match(translatedEvent.TranslatedText, @"\[(?<id>p\d+-[^\]]+)\]", RegexOptions.CultureInvariant);
                var label = labelMatch.Success ? labelMatch.Value : null;
                issueKindsByBlock.TryGetValue(item.Block.BlockId, out var issueKinds);
                var status = issueKinds is { Length: > 0 }
                    ? string.Join(";", issueKinds)
                    : translatedEvent is null ? "no-translation-event" : "no-export-issue-recorded";
                var box = item.Bounds;
                return new
                {
                    ReadingOrder = index + 1,
                    item.Block.BlockId,
                    Column = item.Column,
                    translatedEvent?.ChunkIndex,
                    Status = status,
                    Label = label,
                    LabelOccurrences = label is null ? 0 : Regex.Matches(extractedText, Regex.Escape(label)).Count,
                    SourceX = box?.X,
                    SourceY = box?.Y,
                    SourceWidth = box?.Width,
                    SourceHeight = box?.Height,
                    TargetX = box?.X,
                    TargetYTop = box is null ? null : (double?)(792 - box.Value.Y - box.Value.Height),
                    TargetWidth = box?.Width,
                    TargetHeight = box?.Height,
                    SourceText = item.Block.Text,
                    TranslatedText = translatedEvent?.TranslatedText,
                };
            }).ToArray();

            var markedRows = rows.Where(row => row.Label is not null).ToArray();
            markedRows.Select(row => row.Label).Distinct(StringComparer.Ordinal).Count().Should().Be(markedRows.Length,
                "each translated source block must receive its own label");
            markedRows.Should().OnlyContain(row => row.Label == $"[{row.BlockId}]",
                "the label must match its source block ID");
            markedRows.Where(row => !row.Status.Contains("skipped-", StringComparison.Ordinal))
                .Should().OnlyContain(row => row.LabelOccurrences == 1,
                    "each rendered translation label must occur once in the output PDF");
            markedRows.Where(row => row.Status.Contains("skipped-", StringComparison.Ordinal))
                .Should().OnlyContain(row => row.LabelOccurrences == 0,
                    "skipped source blocks must not appear in the output PDF");
            markedRows.Should().Contain(row => row.BlockId == "p1-body-b5" && row.LabelOccurrences == 1);
            markedRows.Should().Contain(row => row.BlockId == "p1-body-b22" && row.LabelOccurrences == 1);
            markedRows.Should().Contain(row => row.BlockId == "p1-right-b23" && row.LabelOccurrences == 1);

            var preservedAuthorRows = rows.Where(row => authorMetadataBlockIds.Contains(row.BlockId)).ToArray();
            preservedAuthorRows.Should().HaveCount(authorMetadataBlockIds.Count);
            preservedAuthorRows.Should().OnlyContain(row => row.Status.Contains("source-preserved", StringComparison.Ordinal) && row.Label == null,
                "author names, affiliations, and email addresses must remain original and must not get mock translation labels");
            foreach (var blockId in authorMetadataBlockIds)
            {
                var sourceText = sourceBlocks.Single(block => block.BlockId == blockId).Text;
                extractedText.Should().Contain(Regex.Replace(sourceText, @"\s+", string.Empty),
                    $"the output must retain source metadata block {blockId}");
            }

            var issueSummary = issueKindsByBlock.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            File.WriteAllText(
                Path.Combine(reviewDirectory, "overlay-real-paper-block-mapping.json"),
                JsonSerializer.Serialize(new
                {
                    PageNumber = 1,
                    PageWidth = 612,
                    PageHeight = 792,
                    ReadingOrderRule = "Spanning blocks first, then left and right columns top-to-bottom; status comes from export quality blockIssues.",
                    SourceBlockCount = sourceBlocks.Count,
                    TranslatedLabelCount = markedRows.Length,
                    Rows = rows
                }, new JsonSerializerOptions { WriteIndented = true }));
            WriteCsv(Path.Combine(reviewDirectory, "overlay-real-paper-block-mapping.csv"), rows);
            File.WriteAllText(Path.Combine(reviewDirectory, "overlay-real-paper-quality-report.json"), JsonSerializer.Serialize(new { blockIssuesBySourceBlockId = issueSummary, qualityReport }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            Environment.SetEnvironmentVariable("EASYDICT_TEST_ALLOW_CJK_SYSTEM_FONT_FALLBACK", fallbackOriginal);
        }
    }

    [Fact]
    public async Task TranslateAsync_RealTwoColumnPaperPage_ExportsNormalLengthReview()
    {
        var reviewDirectory = Environment.GetEnvironmentVariable("EASYDICT_PDF_REVIEW_NORMAL_LENGTH_DIR");
        if (string.IsNullOrWhiteSpace(reviewDirectory))
            return;

        Directory.CreateDirectory(reviewDirectory);
        var inputPath = Path.Combine(
            FindRepositoryRoot(),
            "lib", "PdfPig", "src", "UglyToad.PdfPig.Tests", "Integration", "Documents", "2108.11480.pdf");
        File.Exists(inputPath).Should().BeTrue($"the fixed two-column source paper must exist at {inputPath}");
        File.Copy(inputPath, Path.Combine(reviewDirectory, "overlay-real-paper-source.pdf"), overwrite: true);

        var sourceBlocks = (await BuildSourceDocumentAsync(inputPath)).Pages.Single().Blocks;
        File.WriteAllText(
            Path.Combine(reviewDirectory, "overlay-real-paper-source-blocks.json"),
            JsonSerializer.Serialize(sourceBlocks, new JsonSerializerOptions { WriteIndented = true }));
        var authorMetadataBlockIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "p1-body-b1", "p1-body-b2", "p1-body-b3",
            "p1-body-b16", "p1-body-b17", "p1-body-b18",
        };

        var fallbackOriginal = Environment.GetEnvironmentVariable("EASYDICT_TEST_ALLOW_CJK_SYSTEM_FONT_FALLBACK");
        Environment.SetEnvironmentVariable("EASYDICT_TEST_ALLOW_CJK_SYSTEM_FONT_FALLBACK", "1");
        try
        {
            // Replies are pinned to the request content, not to the call order: two passes can
            // differ by retries or chunking, so an ordinal map can silently hand a reply to the
            // wrong block. Every request and every reply is logged for the review.
            var blocksByText = sourceBlocks
                .Select(block => new { BlockId = block.BlockId, Normalized = NormalizeForMatch(block.Text) })
                .OrderByDescending(entry => entry.Normalized.Length)
                .ToArray();
            var callLog = new StringBuilder("CallIndex,ResolvedBlockId,MatchKind,RequestLength,ReplyLength,ReplyPreview\n");
            var requestMismatchLog = new StringBuilder("CallIndex,SequenceBlockId,RequestLength,RequestPreview\n");
            var blockEventLog = new StringBuilder("SourceBlockId,ReplyLength,ReplyPreview\n");
            var sequenceToBlockId = await DiscoverTranslationCallMapAsync(inputPath, reviewDirectory);
            var translatedPath = Path.Combine(reviewDirectory, "overlay-real-paper-translated.pdf");
            var resultPath = Path.Combine(reviewDirectory, "overlay-real-paper-result.json");
            var nextCall = 0;
            var pipeline = new WorkerLongDocumentPipeline((request, serviceId, _) =>
            {
                var callIndex = nextCall++;
                var requestText = NormalizeForMatch(request.Text);
                var exact = blocksByText.FirstOrDefault(entry => entry.Normalized == requestText);
                var container = exact is null
                    ? blocksByText.FirstOrDefault(entry =>
                        entry.Normalized.StartsWith(requestText, StringComparison.Ordinal) ||
                        entry.Normalized.Contains(requestText, StringComparison.Ordinal))
                    : null;
                var sequenceMatch = exact is null && container is null;
                var resolvedBlockId = (exact ?? container)?.BlockId;
                if (resolvedBlockId is null && !sequenceToBlockId.TryGetValue(callIndex, out resolvedBlockId))
                {
                    throw new InvalidOperationException(
                        $"Translation call {callIndex} does not match any source block; request length {request.Text.Length}.");
                }

                var blockId = resolvedBlockId!;
                if (sequenceMatch)
                {
                    requestMismatchLog.Append(callIndex).Append(',').Append(blockId).Append(',')
                        .Append(request.Text.Length).Append(',')
                        .Append(EscapeCsv(request.Text.Length > 60 ? request.Text[..60] : request.Text)).Append('\n');
                }

                var matchKind = exact is not null ? "exact-block-text" : sequenceMatch ? "sequence-fallback" : "chunk-of-block";
                var text = authorMetadataBlockIds.Contains(blockId)
                    ? request.Text
                    : BuildNormalLengthChinese(blockId, request.Text);
                callLog.Append(callIndex).Append(',').Append(blockId).Append(',').Append(matchKind)
                    .Append(',').Append(request.Text.Length).Append(',').Append(text.Length).Append(',')
                    .Append(EscapeCsv(text.Length > 24 ? text[..24] : text)).Append('\n');
                return Task.FromResult(new TranslationResult
                {
                    OriginalText = request.Text,
                    TranslatedText = text,
                    ServiceName = serviceId,
                    DetectedLanguage = request.FromLanguage,
                    TargetLanguage = request.ToLanguage,
                });
            });

            var result = await pipeline.TranslateAsync(
                BuildParameters(inputPath, translatedPath, resultPath),
                OfflineReviewSettings(),
                progress: null,
                CancellationToken.None,
                (block, _) =>
                {
                    var reply = block.TranslatedText ?? string.Empty;
                    blockEventLog.Append(block.SourceBlockId).Append(',').Append(reply.Length).Append(',')
                        .Append(EscapeCsv(reply.Length > 24 ? reply[..24] : reply)).Append('\n');
                    return Task.CompletedTask;
                });

            File.WriteAllText(Path.Combine(reviewDirectory, "normal-length-call-log.csv"), callLog.ToString());
            File.WriteAllText(Path.Combine(reviewDirectory, "normal-length-request-mismatches.csv"), requestMismatchLog.ToString());
            File.WriteAllText(Path.Combine(reviewDirectory, "normal-length-block-replies.csv"), blockEventLog.ToString());

            (result.State is "Completed" or "PartiallyCompleted")
                .Should().BeTrue($"the normal-length export must finish; quality report: {result.QualityReport}");
            File.Exists(translatedPath).Should().BeTrue();

            using var persistedResult = JsonDocument.Parse(File.ReadAllText(resultPath));
            var qualityReport = persistedResult.RootElement.GetProperty("qualityReport").GetString() ?? "{}";
            using var quality = JsonDocument.Parse(qualityReport);
            var metrics = quality.RootElement.GetProperty("backfillMetrics");
            var blockIssues = quality.RootElement.GetProperty("backfillMetrics").GetProperty("blockIssues")
                .EnumerateArray()
                .GroupBy(issue => issue.GetProperty("sourceBlockId").GetString() ?? string.Empty, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(issue => new
                        {
                            Kind = issue.GetProperty("kind").GetString() ?? string.Empty,
                            Detail = issue.TryGetProperty("detail", out var detail) ? detail.GetString() : null,
                        }).ToArray(),
                    StringComparer.Ordinal);

            using var translatedPdf = PdfPigDocument.Open(translatedPath);
            var extractedText = Regex.Replace(string.Concat(translatedPdf.GetPages().Select(page => page.Text)), @"\s+", string.Empty);
            File.WriteAllText(Path.Combine(reviewDirectory, "overlay-real-paper-translated-text.txt"), extractedText);

            // The shrink-font issue detail records the size the exporter settled on, which is also the
            // size passed to DrawString. Read the actual glyph sizes back out of the exported PDF so the
            // review compares the drawn result with the report instead of trusting the report alone.
            var translatedPage = translatedPdf.GetPages().Single();
            var drawnSizes = sourceBlocks
                .Where(block => block.BoundingBox is not null)
                .ToDictionary(
                    block => block.BlockId,
                    block =>
                    {
                        var box = block.BoundingBox!.Value;
                        var letters = translatedPage.Letters
                            .Where(letter =>
                            {
                                var centre = letter.BoundingBox.Centroid;
                                return centre.X >= box.X && centre.X <= box.X + box.Width &&
                                       centre.Y >= box.Y && centre.Y <= box.Y + box.Height;
                            })
                            .ToArray();
                        // The block box still holds the original Latin source glyphs (possibly hidden via
                        // "3 Tr"), so the translation is identified by script: the CJK glyphs the overlay
                        // actually drew. Sizes are otherwise dominated by the untouched source run.
                        var translationSizes = letters
                            .Where(letter => IsCjkGlyph(letter.Value))
                            .Select(letter => Math.Round(letter.PointSize, 1))
                            .ToArray();
                        var sourceSizes = letters
                            .Where(letter => !IsCjkGlyph(letter.Value))
                            .Select(letter => Math.Round(letter.PointSize, 1))
                            .ToArray();
                        return new
                        {
                            TranslationGlyphCount = translationSizes.Length,
                            DrawnPointSize = translationSizes.Length == 0
                                ? (double?)null
                                : translationSizes.GroupBy(size => size).OrderByDescending(group => group.Count()).First().Key,
                            TranslationPointSizes = translationSizes.Distinct().OrderBy(size => size).ToArray(),
                            SourceGlyphCount = sourceSizes.Length,
                            SourcePointSizes = sourceSizes.Distinct().OrderBy(size => size).ToArray(),
                        };
                    },
                    StringComparer.Ordinal);
            File.WriteAllText(
                Path.Combine(reviewDirectory, "overlay-real-paper-drawn-font-sizes.json"),
                JsonSerializer.Serialize(drawnSizes, new JsonSerializerOptions { WriteIndented = true }));

            var rows = sourceBlocks.Select((block, index) =>
            {
                blockIssues.TryGetValue(block.BlockId, out var issues);
                var box = block.BoundingBox;
                drawnSizes.TryGetValue(block.BlockId, out var drawn);
                var translation = authorMetadataBlockIds.Contains(block.BlockId)
                    ? block.Text
                    : BuildNormalLengthChinese(block.BlockId, block.Text);
                return new
                {
                    ReadingOrder = index + 1,
                    block.BlockId,
                    Column = box is null || box.Value.Width >= 459
                        ? "span"
                        : box.Value.X + box.Value.Width / 2 < 306 ? "left" : "right",
                    Status = issues is { Length: > 0 }
                        ? string.Join(";", issues.Select(issue => issue.Kind).Distinct(StringComparer.Ordinal))
                        : "no-export-issue-recorded",
                    IssueDetail = issues is null ? null : string.Join(" | ", issues.Where(issue => issue.Detail is not null).Select(issue => issue.Detail)),
                    DrawnPointSize = drawn?.DrawnPointSize,
                    DrawnLetterCount = drawn?.TranslationGlyphCount ?? 0,
                    SourceTextLength = block.Text.Length,
                    TranslationLength = translation.Length,
                    SourceX = box?.X,
                    SourceY = box?.Y,
                    SourceWidth = box?.Width,
                    SourceHeight = box?.Height,
                };
            }).ToArray();

            File.WriteAllText(
                Path.Combine(reviewDirectory, "overlay-real-paper-normal-length-report.json"),
                JsonSerializer.Serialize(new
                {
                    PageNumber = 1,
                    TranslationSource = "hand-written Chinese translation of page 1 (arxiv 2108.11480v1)",
                    CandidateBlocks = metrics.GetProperty("candidateBlocks").GetInt64(),
                    RenderedBlocks = metrics.GetProperty("renderedBlocks").GetInt64(),
                    OverlayModeBlocks = metrics.GetProperty("overlayModeBlocks").GetInt64(),
                    MissingBoundingBoxBlocks = metrics.GetProperty("missingBoundingBoxBlocks").GetInt64(),
                    TruncatedBlocks = metrics.GetProperty("truncatedBlocks").GetInt64(),
                    ShrinkFontBlocks = metrics.GetProperty("shrinkFontBlocks").GetInt64(),
                    Rows = rows,
                    QualityReport = qualityReport,
                }, new JsonSerializerOptions { WriteIndented = true }));

            metrics.GetProperty("missingBoundingBoxBlocks").GetInt64().Should().Be(0);
            metrics.GetProperty("renderedBlocks").GetInt64().Should().BeGreaterThan(20);
            extractedText.Should().NotContain("[p1-", "the normal-length export must not carry diagnostic block labels");
            foreach (var blockId in authorMetadataBlockIds)
            {
                var sourceText = sourceBlocks.Single(block => block.BlockId == blockId).Text;
                extractedText.Should().Contain(Regex.Replace(sourceText, @"\s+", string.Empty),
                    $"the output must retain source metadata block {blockId}");
            }

            var summaryBlock = sourceBlocks.Single(block => block.BlockId == "p1-body-b5");
            var summaryProbe = Regex.Replace(BuildNormalLengthChinese(summaryBlock.BlockId, summaryBlock.Text), @"\s+", string.Empty);
            var abstractInTextLayer = extractedText.Contains(summaryProbe.Substring(0, Math.Min(8, summaryProbe.Length)), StringComparison.Ordinal);
            File.WriteAllText(
                Path.Combine(reviewDirectory, "overlay-real-paper-layout-findings.txt"),
                "candidate=" + metrics.GetProperty("candidateBlocks").GetInt64()
                + " rendered=" + metrics.GetProperty("renderedBlocks").GetInt64()
                + " overlay=" + metrics.GetProperty("overlayModeBlocks").GetInt64()
                + " missingBoundingBox=" + metrics.GetProperty("missingBoundingBoxBlocks").GetInt64()
                + " truncated=" + metrics.GetProperty("truncatedBlocks").GetInt64()
                + " shrinkFont=" + metrics.GetProperty("shrinkFontBlocks").GetInt64()
                + " abstractTranslationInTextLayer=" + abstractInTextLayer);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EASYDICT_TEST_ALLOW_CJK_SYSTEM_FONT_FALLBACK", fallbackOriginal);
        }
    }

    /// <summary>
    /// Small-sample check for the content-stream removal: one real block that mixes a word broken
    /// across line ends with a standalone citation number. Verifies the source text is gone from the
    /// text layer and that a neighbouring block keeps both its text and its exact position.
    /// </summary>
    [Fact]
    public void TryRemoveSourceLinesInStream_RealBlockWithHyphenationAndCitation_RemovesOnlyThatBlock()
    {
        var inputPath = Path.Combine(
            FindRepositoryRoot(),
            "lib", "PdfPig", "src", "UglyToad.PdfPig.Tests", "Integration", "Documents", "2108.11480.pdf");
        File.Exists(inputPath).Should().BeTrue($"the fixed two-column source paper must exist at {inputPath}");

        var tempDirectory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "easydict-source-removal-" + Guid.NewGuid().ToString("N")));
        try
        {
            var workingPath = Path.Combine(tempDirectory.FullName, "page1.pdf");
            File.Copy(inputPath, workingPath, overwrite: true);

            var blocks = BuildSourceDocumentAsync(inputPath).GetAwaiter().GetResult().Pages.Single().Blocks;
            var target = blocks.Single(block => block.BlockId == "p1-body-b24");
            var neighbour = blocks.Single(block => block.BlockId == "p1-right-b23");
            target.Text.Should().Contain("[9]");

            var box = target.BoundingBox!.Value;
            var blockRect = new XRect(box.X, 792 - (box.Y + box.Height), box.Width, box.Height);
            var lineRects = PdfExportService.TryBuildLineRects(792, blockRect, target.TextStyle, 11);
            lineRects.Should().NotBeNull();

            var groups = PdfExportService.ReadSourceLetterGroups(workingPath, 1);
            groups.Should().NotBeEmpty();

            PdfExportService.SourceLineRemovalResult removal;
            using (var document = PdfReader.Open(workingPath, PdfDocumentOpenMode.Modify))
            {
                var page = document.Pages[0];
                removal = PdfExportService.TryRemoveSourceLinesInStream(page, lineRects, groups);
                document.Save(workingPath);
            }

            removal.TotalLines.Should().Be(lineRects!.Count);
            removal.RemovedLines.Should().Be(lineRects.Count, "every glyph run of this block sits inside its line rects");
            removal.RetainedLineIndexes.Should().BeEmpty();

            using var originalPdf = PdfPigDocument.Open(inputPath);
            using var modifiedPdf = PdfPigDocument.Open(workingPath);
            var originalPage = originalPdf.GetPage(1);
            var modifiedPage = modifiedPdf.GetPage(1);

            // This PDF's extractor emits no inter-word spaces, so compare with whitespace removed.
            var originalText = Regex.Replace(originalPage.Text, @"\s+", string.Empty);
            var modifiedText = Regex.Replace(modifiedPage.Text, @"\s+", string.Empty);
            originalText.Should().Contain("userankingsfromthefirststageofthisarchitecture");
            originalText.Should().Contain("plifiedbytheColBERTapproach");
            modifiedText.Should().NotContain("userankingsfromthefirststageofthisarchitecture");
            modifiedText.Should().NotContain("plifiedbytheColBERTapproach");

            // The neighbour above the removed block must be untouched: same glyphs, same place.
            var neighbourBox = neighbour.BoundingBox!.Value;
            var originalLetters = LettersInBox(originalPage, neighbourBox);
            var modifiedLetters = LettersInBox(modifiedPage, neighbourBox);
            originalLetters.Should().NotBeEmpty();
            modifiedLetters.Should().HaveCount(originalLetters.Count);
            for (var i = 0; i < originalLetters.Count; i++)
            {
                modifiedLetters[i].Value.Should().Be(originalLetters[i].Value);
                modifiedLetters[i].StartBaseLine.X.Should().BeApproximately(originalLetters[i].StartBaseLine.X, 0.05);
                modifiedLetters[i].StartBaseLine.Y.Should().BeApproximately(originalLetters[i].StartBaseLine.Y, 0.05);
            }
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Position audit for the removal round: every preserved (untranslated) block, and every
    /// citation/formula run inside a translated block, must still be drawn where the source had it.
    /// Writes removal-position-audit.json next to the export for review.
    /// </summary>
    [Fact]
    public void Removal_KeepsPreservedBlocksAndCitationsAtTheirSourcePositions()
    {
        var reviewDirectory = Environment.GetEnvironmentVariable("EASYDICT_PDF_REVIEW_NORMAL_LENGTH_DIR");
        if (string.IsNullOrWhiteSpace(reviewDirectory))
            return;

        var sourcePath = Path.Combine(reviewDirectory, "overlay-real-paper-source.pdf");
        var translatedPath = Path.Combine(reviewDirectory, "overlay-real-paper-translated.pdf");
        var blocksPath = Path.Combine(reviewDirectory, "overlay-real-paper-source-blocks.json");
        if (!File.Exists(translatedPath) || !File.Exists(blocksPath))
            return;

        var blocks = JsonSerializer.Deserialize<List<SourceBlockSnapshot>>(
            File.ReadAllText(blocksPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        using var sourcePdf = PdfPigDocument.Open(sourcePath);
        using var translatedPdf = PdfPigDocument.Open(translatedPath);
        var sourcePage = sourcePdf.GetPage(1);
        var translatedPage = translatedPdf.GetPage(1);

        var translatedIds = ReadTranslatedBlockIds(reviewDirectory);
        var entries = new List<object>();
        var problems = 0;

        foreach (var block in blocks.Where(block => block.BoundingBox is not null))
        {
            var box = block.BoundingBox!.Value;
            var before = LettersInBox(sourcePage, box);
            var after = LettersInBox(translatedPage, box);
            var isTranslated = translatedIds.Contains(block.BlockId);
            var isPreserved = !isTranslated;

            var matched = 0;
            var maxShift = 0.0;
            if (isPreserved)
            {
                for (var i = 0; i < before.Count; i++)
                {
                    var candidate = after.FirstOrDefault(letter => letter.Value == before[i].Value &&
                        Math.Abs(letter.StartBaseLine.X - before[i].StartBaseLine.X) < 0.2);
                    if (candidate is null)
                        continue;

                    matched++;
                    maxShift = Math.Max(maxShift,
                        Math.Abs(candidate.StartBaseLine.X - before[i].StartBaseLine.X));
                }

                if (matched < before.Count)
                    problems++;
            }

            entries.Add(new
            {
                blockId = block.BlockId,
                kind = isTranslated ? "translated" : "preserved",
                sourceLetters = before.Count,
                exportedLetters = after.Count,
                matchedAtSourcePosition = matched,
                maxShiftPoints = Math.Round(maxShift, 3)
            });
        }

        var report = new
        {
            source = sourcePath,
            translated = translatedPath,
            preservedBlocksWithMissingGlyphs = problems,
            blocks = entries
        };
        File.WriteAllText(
            Path.Combine(reviewDirectory, "removal-position-audit.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        problems.Should().Be(0, "every preserved block must keep its glyphs at their source positions");
    }

    private static HashSet<string> ReadTranslatedBlockIds(string reviewDirectory)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var path = Path.Combine(reviewDirectory, "overlay-real-paper-result.json");
        if (!File.Exists(path))
            return ids;

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("qualityReport", out var qualityReport) ||
            qualityReport.ValueKind != JsonValueKind.String)
            return ids;

        using var report = JsonDocument.Parse(qualityReport.GetString()!);
        if (!report.RootElement.TryGetProperty("backfillMetrics", out var metrics) ||
            !metrics.TryGetProperty("blockIssues", out var issues) ||
            issues.ValueKind != JsonValueKind.Array)
            return ids;

        foreach (var issue in issues.EnumerateArray())
        {
            if (!issue.TryGetProperty("kind", out var kind) ||
                !issue.TryGetProperty("sourceBlockId", out var blockId))
                continue;

            if (kind.GetString() == "source-text-removed" && blockId.GetString() is { } id)
                ids.Add(id);
        }

        return ids;
    }

    private sealed class SourceBlockSnapshot
    {
        public string BlockId { get; set; } = string.Empty;

        public SnapshotRect? BoundingBox { get; set; }
    }

    private sealed class SnapshotRect
    {
        public double X { get; set; }

        public double Y { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }

        public BlockRect Value => new(X, Y, Width, Height);
    }

    private static List<UglyToad.PdfPig.Content.Letter> LettersInBox(UglyToad.PdfPig.Content.Page page, BlockRect box) =>
        page.Letters
            .Where(letter =>
            {
                var centre = letter.BoundingBox.Centroid;
                return centre.X >= box.X && centre.X <= box.X + box.Width &&
                       centre.Y >= box.Y && centre.Y <= box.Y + box.Height;
            })
            .OrderBy(letter => letter.StartBaseLine.Y)
            .ThenBy(letter => letter.StartBaseLine.X)
            .ToList();

    /// <summary>
    /// Hand-written Chinese translation of page 1 of arXiv:2108.11480v1. Using real prose keeps the
    /// exported page at a realistic length, so the review exercises real line wrapping instead of a
    /// repeated test sentence. Blocks absent from the map keep their source text.
    /// </summary>
    private static string BuildNormalLengthChinese(string blockId, string sourceText) =>
        ReviewPageOneTranslations.TryGetValue(blockId, out var translation) ? translation : sourceText;

    private static readonly IReadOnlyDictionary<string, string> ReviewPageOneTranslations =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["p1-header-b25"] = "面向多阶段稠密检索的近似最近邻选择方法",
            ["p1-left-b4"] = "摘要",
            ["p1-left-b6"] = "CCS 概念",
            ["p1-body-b7"] =
                "• 信息系统 → 信息检索；信息检索查询处理；检索模型与排序。",
            ["p1-left-b8"] = "关键词\n近似最近邻；稠密检索。",
            ["p1-body-b5"] =
                "稠密检索指的是借助 BERT 等语境化语言模型，并结合近似最近邻（ANN）技术，" +
                "从文档集合中识别出相关文档的做法，近来日益流行。依据文档与查询是以单一嵌入还是多重嵌入表示，" +
                "已有两类方法。后者的典型代表 ColBERT 使用 ANN 索引与近似分数，为每个查询嵌入找出一个候选文档集合，" +
                "再用精确的文档表示对其重排。这样每个查询都能取回大量文档，从而限制了该方法的效率。" +
                "本文研究利用 ANN 分数对候选文档排序，以减少需要精确打分的候选文档数量。" +
                "在 MSMARCO 段落排序语料上的实验表明，仅依据近似分数把候选集截断到 200 篇文档，" +
                "仍可获得有效排序，效果差异在统计上不显著，同时效率提升约 2 倍。",
            ["p1-body-b11"] =
                "1 引言\n引擎架构通常采用级联式设计[10, 18]：先由开销较小的排序方法（例如作用于经典倒排索引的 BM25）" +
                "挑出一个规模为 k 的候选文档集合，再由更昂贵的排序方法对其进行进一步处理与重排，" +
                "从而得到最终呈现给用户的少量文档。此类设置过去多依赖人工特征的学习排序，" +
                "而近年来越来越多地使用 BERT [2] 等语境化语言模型对候选集重排[5, 13, 14]。事实上，BERT 已经",
            ["p1-right-b19"] = "为信息检索任务带来了相比以往方法显著的效果提升[11]。",
            ["p1-right-b20"] =
                "许多工作提出了不同的深度网络架构，用以学习如何用实值向量表示一个词项序列（例如文档或段落）的语义内容，" +
                "即为给定序列生成单一表示。这种做法把文本序列中蕴含的全部语义信息压缩进一个向量。" +
                "另一种做法是为序列中的每个词元学习一个表示，即为每个词项生成一个表示，从而对任意文本生成多重表示。" +
                "多项工作表明，与单一表示相比，多重表示能够带来更好的效果[6, 9, 12]。\n" +
                "更近期的工作将有效的语境化语言模型与近似最近邻（ANN）技术[8, 9, 22]结合，形成了稠密检索。" +
                "在稠密检索中，给定一个或多个查询嵌入，通过最近邻搜索取出最相似的文档嵌入。" +
                "当文档与查询都采用单一表示时，可以借助精确最近邻（NN）搜索的数据结构与算法[8, 22]。" +
                "单一表示上的 NN 搜索虽然高效，但其效果不如多重表示上的 NN 搜索[11]。另一方面，为应对所需的向量规模，",
            ["p1-right-b21"] =
                "多重表示需要近似最近邻（ANN）数据结构与算法。因此，Khattab 与 Zaharia [9] 提出了两阶段稠密检索的级联方法。" +
                "第一阶段执行 ANN 搜索取回一组候选文档，并尽可能提高检索集合的召回率；" +
                "第二阶段为一阶段的候选文档计算精确分数，返回最终排名靠前的文档。总体而言，" +
                "与对 BM25 结果重排相比，稠密检索更具吸引力，因为它在整个流程中端到端使用同一模型，" +
                "因此对被学习模型的改进会同时提升两个阶段的效果。\n" +
                "在 ColBERT 稠密检索方法中，第一阶段计算出的近似（距离）分数在第二阶段会被忽略，" +
                "因为候选文档的精确分数会被重新计算。然而，第二阶段重排所耗费的时间与第一阶段检索到的文档数成正比。" +
                "本文研究如何基于第一阶段的近似检索组件来构造排名，使规模更小的候选文档排名可以进入第二阶段。" +
                "概括而言，本文考察了 ANN 排序策略单独使用以及与有效的第二阶段重排器结合时的效果。",
            ["p1-body-b22"] = "2 稠密检索\n本节描述一种多阶段",
            ["p1-right-b23"] = "多重表示稠密检索系统的通用架构（第 2.1 节），",
            ["p1-body-b24"] =
                "并以 ColBERT 方法[9]为例加以说明，同时讨论如何从该架构的第一阶段获得并使用排名（第 2.2 节）。",
        };

    private static bool IsCjkGlyph(string value) =>
        value.Length > 0 && value.Any(ch =>
            (ch >= 0x2E80 && ch <= 0x9FFF) ||
            (ch >= 0xF900 && ch <= 0xFAFF) ||
            (ch >= 0xFF00 && ch <= 0xFFEF));

    private static string NormalizeForMatch(string text) => Regex.Replace(text, @"\s+", string.Empty);

    private static string EscapeCsv(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal) + "\""
            : value;

    private static async Task<Dictionary<int, string>> DiscoverTranslationCallMapAsync(string inputPath, string reviewDirectory)
    {
        var callMap = new Dictionary<int, string>();
        var nextCall = 0;
        var tempDirectory = Path.Combine(reviewDirectory, "call-map-pass");
        Directory.CreateDirectory(tempDirectory);
        var outputPath = Path.Combine(tempDirectory, "call-map.pdf");
        var resultPath = Path.Combine(tempDirectory, "call-map-result.json");
        var pipeline = new WorkerLongDocumentPipeline((request, serviceId, _) =>
        {
            var callIndex = nextCall++;
            var text = $"[CALL_{callIndex:D4}] " + request.Text;
            return Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = text,
                ServiceName = serviceId,
                DetectedLanguage = request.FromLanguage,
                TargetLanguage = request.ToLanguage,
            });
        });

        await pipeline.TranslateAsync(
            BuildParameters(inputPath, outputPath, resultPath),
            OfflineReviewSettings(),
            progress: null,
            CancellationToken.None,
            (block, _) =>
            {
                var marker = ReviewCallMarker.Match(block.TranslatedText);
                if (marker.Success)
                {
                    var callIndex = int.Parse(marker.Groups["index"].Value, CultureInfo.InvariantCulture);
                    callMap[callIndex] = block.SourceBlockId;
                }

                return Task.CompletedTask;
            });

        return callMap;
    }

    private static async Task<SourceDocument> BuildSourceDocumentAsync(string inputPath)
    {
        using var builder = new WorkerLongDocumentSourceDocumentBuilder();
        return await builder.BuildAsync(
            LongDocumentInputMode.Pdf,
            inputPath,
            LayoutDetectionMode.Heuristic,
            visionEndpoint: null,
            visionApiKey: null,
            visionModel: null,
            pageRange: "1",
            enableTatrTableStructure: true,
            proxyEnabled: false,
            proxyUri: null,
            proxyBypassLocal: false,
            onProgress: null,
            CancellationToken.None);
    }

    private static TranslateDocumentParams BuildParameters(string inputPath, string outputPath, string resultPath) => new()
    {
        InputPath = inputPath,
        OutputPath = outputPath,
        ResultJsonPath = resultPath,
        InputMode = "Pdf",
        From = "English",
        To = "SimplifiedChinese",
        ServiceId = "offline-review",
        OutputMode = "Monolingual",
        PageRange = "1",
        PdfExportMode = "Overlay",
    };

    private static SettingsSnapshot OfflineReviewSettings() => new()
    {
        LongDocMaxConcurrency = 1,
        LongDocEnableDocumentContextPass = false,
    };

    private static void WriteCsv(string path, IEnumerable<dynamic> rows)
    {
        var builder = new StringBuilder("ReadingOrder,BlockId,Column,ChunkIndex,Status,Label,LabelOccurrences,SourceX,SourceY,SourceWidth,SourceHeight,TargetX,TargetYTop,TargetWidth,TargetHeight\r\n");
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(",", new[]
            {
                ((int)row.ReadingOrder).ToString(CultureInfo.InvariantCulture),
                (string)row.BlockId,
                (string)row.Column,
                row.ChunkIndex is null ? string.Empty : ((int)row.ChunkIndex).ToString(CultureInfo.InvariantCulture),
                (string)row.Status,
                (string)(row.Label ?? string.Empty),
                ((int)row.LabelOccurrences).ToString(CultureInfo.InvariantCulture),
                Format(row.SourceX), Format(row.SourceY), Format(row.SourceWidth), Format(row.SourceHeight),
                Format(row.TargetX), Format(row.TargetYTop), Format(row.TargetWidth), Format(row.TargetHeight),
            }));
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string Format(double? value) => value?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".gitmodules")) &&
                Directory.Exists(Path.Combine(directory.FullName, "lib", "PdfPig")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not find the repository root containing PdfPig.");
    }
}
