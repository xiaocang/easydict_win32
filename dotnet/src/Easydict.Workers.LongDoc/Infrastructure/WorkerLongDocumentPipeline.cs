using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using UglyToad.PdfPig.Content;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;
using PdfPigPage = UglyToad.PdfPig.Content.Page;

namespace Easydict.Workers.LongDoc.Infrastructure;

internal sealed class WorkerLongDocumentPipeline
{
    private const double PdfPageWidth = 595;
    private const double PdfPageHeight = 842;
    private const double PdfMargin = 54;
    private const double PdfBodyFontSize = 11;
    private const double PdfHeadingFontSize = 13;

    private static readonly Regex FormulaHeuristicRegex = new(
        @"(\$[^$]+\$|\\([^)]+\\)|\\[[^\]]+\\]|\b\w+\s*=\s*[-+*/^()\w\u221A]+)",
        RegexOptions.Compiled);
    private static readonly Regex NaturalWordRegex = new(@"\b[a-zA-Z]{4,}\b", RegexOptions.Compiled);

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
        var sourceDocument = await BuildSourceDocumentAsync(mode, p.InputPath, p.PageRange, cancellationToken)
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
            EnableDocumentContextPass = settings.LongDocEnableDocumentContextPass ?? true,
            EnableOcrFallback = false,
            EnableQualityFeedbackRetry = true,
            MaxRetriesPerBlock = 1,
            MaxConcurrency = Math.Clamp(settings.LongDocMaxConcurrency ?? 1, 1, 16),
            RequestTimeoutMs = UsesLocalProfile(p.ServiceId) ? 120_000 : 30_000,
            FormulaFontPattern = string.IsNullOrWhiteSpace(settings.FormulaFontPattern) ? null : settings.FormulaFontPattern,
            FormulaCharPattern = string.IsNullOrWhiteSpace(settings.FormulaCharPattern) ? null : settings.FormulaCharPattern,
            PageRange = p.PageRange,
            CustomPrompt = string.IsNullOrWhiteSpace(settings.LongDocCustomPrompt) ? null : settings.LongDocCustomPrompt,
            Progress = progress,
        };
    }

    private static async Task<TranslateDocumentResult> ExportFlatResultAsync(
        WorkerLongDocumentInputMode mode,
        TranslateDocumentParams p,
        Easydict.TranslationService.LongDocument.LongDocumentTranslationResult coreResult,
        Func<BlockTranslatedEventData, CancellationToken, Task>? onBlockTranslated,
        CancellationToken cancellationToken)
    {
        var outputPath = ResolveOutputPath(p.OutputPath, p.InputPath, mode);
        var outputMode = ParseOutputMode(p.OutputMode);
        var orderedBlocks = coreResult.Pages
            .OrderBy(page => page.PageNumber)
            .SelectMany(page => page.Blocks.Select(block => new OrderedTranslatedBlock(page.PageNumber, block)))
            .ToList();

        var failedIndexes = orderedBlocks
            .Select((item, index) => new { item.Block, index })
            .Where(item => !string.IsNullOrWhiteSpace(item.Block.LastError) || string.IsNullOrWhiteSpace(item.Block.TranslatedText))
            .Select(item => item.index)
            .ToList();

        var succeededChunks = Math.Max(0, orderedBlocks.Count - failedIndexes.Count);
        if (succeededChunks == 0)
        {
            throw new WorkerHandlerException(WorkerErrorCodes.ServiceError, "Translation failed for all chunks.");
        }

        if (onBlockTranslated is not null)
        {
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

        var monolingual = ComposeMonolingual(mode, orderedBlocks, failedIndexes);
        if (mode == WorkerLongDocumentInputMode.Pdf)
        {
            WritePdf(outputPath, monolingual, cancellationToken);
        }
        else
        {
            await File.WriteAllTextAsync(outputPath, monolingual, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
        }

        string? bilingualPath = null;
        if (outputMode is WorkerDocumentOutputMode.Bilingual or WorkerDocumentOutputMode.Both)
        {
            bilingualPath = BuildBilingualOutputPath(outputPath);
            var bilingual = ComposeBilingual(mode, orderedBlocks, failedIndexes);
            if (mode == WorkerLongDocumentInputMode.Pdf)
            {
                WritePdf(bilingualPath, bilingual, cancellationToken);
            }
            else
            {
                await File.WriteAllTextAsync(bilingualPath, bilingual, Encoding.UTF8, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (outputMode == WorkerDocumentOutputMode.Bilingual && bilingualPath is not null)
        {
            TryDelete(outputPath);
            outputPath = bilingualPath;
        }

        return new TranslateDocumentResult
        {
            State = failedIndexes.Count == 0 ? "Completed" : "PartiallyCompleted",
            OutputPath = outputPath,
            BilingualOutputPath = bilingualPath,
            TotalChunks = orderedBlocks.Count,
            SucceededChunks = succeededChunks,
            FailedChunkIndexes = failedIndexes,
            QualityReport = JsonSerializer.Serialize(coreResult.QualityReport, JsonOptions),
        };
    }

    private static async Task<SourceDocument> BuildSourceDocumentAsync(
        WorkerLongDocumentInputMode mode,
        string inputPath,
        string? pageRange,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Source file not found.", inputPath);
        }

        return mode switch
        {
            WorkerLongDocumentInputMode.PlainText or WorkerLongDocumentInputMode.Markdown =>
                await BuildSourceDocumentFromTextFileAsync(inputPath, cancellationToken).ConfigureAwait(false),
            WorkerLongDocumentInputMode.Pdf =>
                await Task.Run(() => BuildSourceDocumentFromPdf(inputPath, pageRange, cancellationToken), cancellationToken)
                    .ConfigureAwait(false),
            _ => throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, $"Unsupported inputMode: {mode}"),
        };
    }

    private static async Task<SourceDocument> BuildSourceDocumentFromTextFileAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        var blocks = SplitTextIntoBlocks(text, 1).ToList();

        return new SourceDocument
        {
            DocumentId = Path.GetFileNameWithoutExtension(filePath),
            Pages =
            [
                new SourceDocumentPage
                {
                    PageNumber = 1,
                    Blocks = blocks,
                },
            ],
        };
    }

    private static SourceDocument BuildSourceDocumentFromPdf(
        string filePath,
        string? pageRange,
        CancellationToken cancellationToken)
    {
        using var document = PdfPigDocument.Open(filePath);
        var selectedPages = PageRangeParser.Parse(pageRange, document.NumberOfPages);
        var pages = new List<SourceDocumentPage>();

        for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selectedPages is not null && !selectedPages.Contains(pageNumber))
            {
                continue;
            }

            var page = document.GetPage(pageNumber);
            var blocks = ExtractPdfBlocks(page).ToList();
            pages.Add(new SourceDocumentPage
            {
                PageNumber = pageNumber,
                Blocks = blocks,
                IsScanned = blocks.Count == 0,
            });
        }

        if (pages.Count == 0)
        {
            pages.Add(new SourceDocumentPage
            {
                PageNumber = 1,
                Blocks = [],
                IsScanned = true,
            });
        }

        return new SourceDocument
        {
            DocumentId = Path.GetFileNameWithoutExtension(filePath),
            Pages = pages,
        };
    }

    private static IEnumerable<SourceDocumentBlock> ExtractPdfBlocks(PdfPigPage page)
    {
        var words = page.GetWords()
            .Where(word => word.TextOrientation == TextOrientation.Horizontal && !string.IsNullOrWhiteSpace(word.Text))
            .OrderByDescending(word => word.BoundingBox.Top)
            .ThenBy(word => word.BoundingBox.Left)
            .ToList();

        if (words.Count == 0)
        {
            foreach (var block in SplitTextIntoBlocks(page.Text ?? string.Empty, page.Number))
            {
                yield return block;
            }

            yield break;
        }

        var medianWordHeight = words
            .Select(word => Math.Max(1d, word.BoundingBox.Height))
            .OrderBy(height => height)
            .Skip(words.Count / 2)
            .FirstOrDefault();
        var sameLineThreshold = Math.Max(2.5, medianWordHeight * 0.35);
        var paragraphGapThreshold = Math.Max(8, medianWordHeight * 1.35);

        var lines = new List<PdfWorkerLine>();
        foreach (var word in words)
        {
            var bottom = word.BoundingBox.Bottom;
            var line = lines.FirstOrDefault(item => Math.Abs(item.SeedBottom - bottom) <= sameLineThreshold);
            if (line is null)
            {
                line = new PdfWorkerLine(bottom);
                lines.Add(line);
            }

            line.Words.Add(word);
        }

        var normalizedLines = lines
            .Select(line => line.Normalize())
            .OrderByDescending(line => line.Top)
            .ToList();

        var paragraphs = new List<List<PdfWorkerLine>>();
        List<PdfWorkerLine>? current = null;
        PdfWorkerLine? previous = null;
        foreach (var line in normalizedLines)
        {
            if (current is null ||
                previous is not null && Math.Abs(previous.Bottom - line.Top) > paragraphGapThreshold)
            {
                current = [];
                paragraphs.Add(current);
            }

            current.Add(line);
            previous = line;
        }

        for (var i = 0; i < paragraphs.Count; i++)
        {
            var paragraph = paragraphs[i];
            var text = string.Join("\n", paragraph.Select(line => line.Text)).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var left = paragraph.Min(line => line.Left);
            var right = paragraph.Max(line => line.Right);
            var top = paragraph.Max(line => line.Top);
            var bottom = paragraph.Min(line => line.Bottom);
            var type = GuessBlockType(text);

            yield return new SourceDocumentBlock
            {
                BlockId = $"p{page.Number}-b{i + 1}",
                BlockType = type,
                Text = text,
                IsFormulaLike = type == SourceBlockType.Formula,
                BoundingBox = new BlockRect(left, bottom, Math.Max(1, right - left), Math.Max(1, top - bottom)),
            };
        }
    }

    private static IEnumerable<SourceDocumentBlock> SplitTextIntoBlocks(string text, int pageNumber)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield return new SourceDocumentBlock
            {
                BlockId = $"p{pageNumber}-b1",
                BlockType = SourceBlockType.Paragraph,
                Text = string.Empty,
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
                IsFormulaLike = blockType == SourceBlockType.Formula,
            };
        }
    }

    private static SourceBlockType GuessBlockType(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith('#'))
        {
            return SourceBlockType.Heading;
        }

        if (FormulaHeuristicRegex.IsMatch(trimmed) &&
            NaturalWordRegex.Matches(trimmed).Count <= 2)
        {
            return SourceBlockType.Formula;
        }

        return SourceBlockType.Paragraph;
    }

    private static string ComposeMonolingual(
        WorkerLongDocumentInputMode mode,
        IReadOnlyList<OrderedTranslatedBlock> orderedBlocks,
        IReadOnlyCollection<int> failedIndexes)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < orderedBlocks.Count; i++)
        {
            var block = orderedBlocks[i].Block;
            if (failedIndexes.Contains(i))
            {
                sb.AppendLine(mode == WorkerLongDocumentInputMode.Markdown
                    ? $"> *[Chunk {i + 1} translation failed.]*"
                    : $"[Chunk {i + 1} translation failed.]");
                sb.AppendLine();
                continue;
            }

            var text = block.TranslatedText.Trim();
            if (mode == WorkerLongDocumentInputMode.Markdown &&
                block.BlockType == BlockType.Heading &&
                !text.StartsWith('#'))
            {
                sb.Append("### ");
            }

            sb.AppendLine(text);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string ComposeBilingual(
        WorkerLongDocumentInputMode mode,
        IReadOnlyList<OrderedTranslatedBlock> orderedBlocks,
        IReadOnlyCollection<int> failedIndexes)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < orderedBlocks.Count; i++)
        {
            var block = orderedBlocks[i].Block;
            var source = block.OriginalText.Trim();

            if (mode == WorkerLongDocumentInputMode.Markdown)
            {
                foreach (var line in source.Split('\n'))
                {
                    sb.AppendLine($"> {line}");
                }
            }
            else
            {
                sb.AppendLine(source);
            }

            sb.AppendLine();

            if (failedIndexes.Contains(i))
            {
                sb.AppendLine(mode == WorkerLongDocumentInputMode.Markdown
                    ? $"> *[Chunk {i + 1} translation failed.]*"
                    : $"[Chunk {i + 1} translation failed.]");
            }
            else
            {
                var text = block.TranslatedText.Trim();
                if (mode == WorkerLongDocumentInputMode.Markdown &&
                    block.BlockType == BlockType.Heading &&
                    !text.StartsWith('#'))
                {
                    sb.Append("### ");
                }

                sb.AppendLine(text);
            }

            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static void WritePdf(string outputPath, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkerPdfFontResolver.EnsureInitialized();

        using var document = new PdfDocument();
        document.Info.Title = Path.GetFileNameWithoutExtension(outputPath);

        XGraphics? graphics = null;
        PdfPage? page = null;
        var y = PdfMargin;
        var font = new XFont(SelectPdfFontFamily(text), PdfBodyFontSize, XFontStyle.Regular);
        var headingFont = new XFont(SelectPdfFontFamily(text), PdfHeadingFontSize, XFontStyle.Bold);
        var lineHeight = PdfBodyFontSize * 1.45;

        try
        {
            NewPage();
            foreach (var paragraph in NormalizePdfText(text).Split('\n'))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(paragraph))
                {
                    y += lineHeight;
                    continue;
                }

                var drawFont = paragraph.StartsWith("### ", StringComparison.Ordinal) ? headingFont : font;
                var drawText = paragraph.StartsWith("### ", StringComparison.Ordinal)
                    ? paragraph[4..].TrimStart()
                    : paragraph;

                foreach (var line in WrapPdfLine(graphics!, drawText, drawFont, page!.Width - PdfMargin * 2))
                {
                    if (y + lineHeight > page!.Height - PdfMargin)
                    {
                        NewPage();
                    }

                    graphics!.DrawString(
                        line,
                        drawFont,
                        XBrushes.Black,
                        new XRect(PdfMargin, y, page!.Width - PdfMargin * 2, lineHeight),
                        XStringFormats.TopLeft);
                    y += lineHeight;
                }

                y += lineHeight * 0.35;
            }
        }
        finally
        {
            graphics?.Dispose();
        }

        document.Save(outputPath);

        void NewPage()
        {
            graphics?.Dispose();
            page = document.AddPage();
            page.Width = PdfPageWidth;
            page.Height = PdfPageHeight;
            graphics = XGraphics.FromPdfPage(page);
            y = PdfMargin;
        }
    }

    private static IEnumerable<string> WrapPdfLine(XGraphics graphics, string text, XFont font, double maxWidth)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            yield break;
        }

        var current = new StringBuilder();
        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : $"{current} {word}";
            if (graphics.MeasureString(candidate, font).Width <= maxWidth)
            {
                current.Clear();
                current.Append(candidate);
                continue;
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }

            if (graphics.MeasureString(word, font).Width <= maxWidth)
            {
                current.Append(word);
                continue;
            }

            foreach (var piece in SplitLongWord(graphics, word, font, maxWidth))
            {
                yield return piece;
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static IEnumerable<string> SplitLongWord(XGraphics graphics, string word, XFont font, double maxWidth)
    {
        var current = new StringBuilder();
        foreach (var ch in word)
        {
            var candidate = current.ToString() + ch;
            if (current.Length > 0 && graphics.MeasureString(candidate, font).Width > maxWidth)
            {
                yield return current.ToString();
                current.Clear();
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static string NormalizePdfText(string text)
    {
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private static string SelectPdfFontFamily(string text)
    {
        return text.Any(ch =>
            ch is >= '\u4E00' and <= '\u9FFF' ||
            ch is >= '\u3040' and <= '\u30FF' ||
            ch is >= '\uAC00' and <= '\uD7AF')
            ? "Microsoft YaHei"
            : "Arial";
    }

    private static WorkerLongDocumentInputMode ParseInputMode(string value)
    {
        if (Enum.TryParse<WorkerLongDocumentInputMode>(value, ignoreCase: true, out var mode))
        {
            return mode;
        }

        throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, $"Unsupported inputMode: {value}");
    }

    private static WorkerDocumentOutputMode ParseOutputMode(string value)
    {
        if (value.Equals("TargetOnly", StringComparison.OrdinalIgnoreCase))
        {
            return WorkerDocumentOutputMode.Monolingual;
        }

        return Enum.TryParse<WorkerDocumentOutputMode>(value, ignoreCase: true, out var mode)
            ? mode
            : WorkerDocumentOutputMode.Monolingual;
    }

    private static Language ParseLanguage(string value)
    {
        return Enum.TryParse<Language>(value, ignoreCase: true, out var language)
            ? language
            : Language.Auto;
    }

    private static string ResolveOutputPath(string? outputPath, string inputPath, WorkerLongDocumentInputMode mode)
    {
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            return outputPath;
        }

        var directory = Path.GetDirectoryName(inputPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var extension = mode switch
        {
            WorkerLongDocumentInputMode.Markdown => ".md",
            WorkerLongDocumentInputMode.Pdf => ".pdf",
            _ => ".txt",
        };

        return Path.Combine(directory, $"{name}.translated{extension}");
    }

    private static string BuildBilingualOutputPath(string monolingualPath)
    {
        var directory = Path.GetDirectoryName(monolingualPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(monolingualPath);
        var extension = Path.GetExtension(monolingualPath);
        return Path.Combine(directory, $"{name}-bilingual{extension}");
    }

    private static bool UsesLocalProfile(string serviceId)
    {
        return serviceId.Equals("windows-local-ai", StringComparison.OrdinalIgnoreCase) ||
            serviceId.Equals("foundry-local", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record OrderedTranslatedBlock(int PageNumber, TranslatedDocumentBlock Block);

    private sealed class PdfWorkerLine
    {
        public PdfWorkerLine(double seedBottom)
        {
            SeedBottom = seedBottom;
        }

        public double SeedBottom { get; }
        public List<Word> Words { get; } = [];
        public double Left { get; private set; }
        public double Right { get; private set; }
        public double Top { get; private set; }
        public double Bottom { get; private set; }
        public string Text { get; private set; } = string.Empty;

        public PdfWorkerLine Normalize()
        {
            Words.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));
            Left = Words.Min(word => word.BoundingBox.Left);
            Right = Words.Max(word => word.BoundingBox.Right);
            Top = Words.Max(word => word.BoundingBox.Top);
            Bottom = Words.Min(word => word.BoundingBox.Bottom);
            Text = string.Join(" ", Words.Select(word => word.Text)).Trim();
            return this;
        }
    }

    private enum WorkerLongDocumentInputMode
    {
        PlainText,
        Markdown,
        Pdf,
    }

    private enum WorkerDocumentOutputMode
    {
        Monolingual,
        Bilingual,
        Both,
    }
}
