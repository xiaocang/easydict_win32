using System.Text;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using UglyToad.PdfPig;
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

public sealed class LongDocumentTranslationCheckpoint
{
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
}

public sealed class LongDocumentTranslationResult
{
    public required LongDocumentJobState State { get; init; }
    public required string OutputPath { get; init; }
    public required int TotalChunks { get; init; }
    public required int SucceededChunks { get; init; }
    public required IReadOnlyList<int> FailedChunkIndexes { get; init; }
    public required LongDocumentTranslationCheckpoint Checkpoint { get; init; }
}

public sealed class LongDocumentTranslationService
{
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

        var checkpoint = new LongDocumentTranslationCheckpoint
        {
            SourceChunks = allBlocks.Select(item => item.Block.OriginalText).ToList(),
            ChunkMetadata = allBlocks
                .Select((item, index) => new LongDocumentChunkMetadata
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
                    IsFormulaLike = item.Block.TranslationSkipped
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
        return FinalizeResult(checkpoint, outputPath, onProgress);
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
            return FinalizeResult(checkpoint, outputPath, onProgress);
        }

        onProgress?.Invoke($"Retrying {checkpoint.FailedChunkIndexes.Count} failed chunks...");
        await TranslatePendingChunksAsync(_coreLongDocumentService, checkpoint, from, to, serviceId, onProgress, cancellationToken);
        EnforceTerminologyConsistency(checkpoint);
        return FinalizeResult(checkpoint, outputPath, onProgress);
    }

    private static async Task TranslatePendingChunksAsync(
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
            return;
        }

        var indexByRetryBlockId = new Dictionary<string, int>(StringComparer.Ordinal);
        var retryPages = new List<SourceDocumentPage>(pendingIndexes.Count);
        var canonicalBySource = BuildCanonicalTranslations(checkpoint);

        for (var i = 0; i < pendingIndexes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkIndex = pendingIndexes[i];
            var sourceText = checkpoint.SourceChunks[chunkIndex];

            if (canonicalBySource.TryGetValue(sourceText, out var canonicalTranslation) &&
                !string.IsNullOrWhiteSpace(canonicalTranslation))
            {
                checkpoint.TranslatedChunks[chunkIndex] = canonicalTranslation;
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
                        IsFormulaLike = metadata.IsFormulaLike
                    }
                ]
            });
        }

        if (retryPages.Count == 0)
        {
            return;
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
    }

    private static LongDocumentTranslationResult FinalizeResult(
        LongDocumentTranslationCheckpoint checkpoint,
        string outputPath,
        Action<string>? onProgress)
    {
        ValidateCheckpointOrThrow(checkpoint);

        var succeededCount = checkpoint.TranslatedChunks.Count;
        if (succeededCount == 0)
        {
            throw new InvalidOperationException("Translation failed for all chunks.");
        }

        onProgress?.Invoke("Generating output PDF...");
        ExportTextPdf(ComposeOutputText(checkpoint), outputPath);

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

    private static string ExtractPdfText(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("PDF file not found.", path);
        }

        var sb = new StringBuilder();
        using var document = PdfDocument.Open(path);
        foreach (var page in document.GetPages())
        {
            sb.AppendLine(page.Text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static SourceDocument BuildSourceDocument(LongDocumentInputMode mode, string input)
    {
        if (mode == LongDocumentInputMode.Manual)
        {
            return new SourceDocument
            {
                DocumentId = "manual-input",
                Pages =
                [
                    new SourceDocumentPage
                    {
                        PageNumber = 1,
                        Blocks =
                        [
                            new SourceDocumentBlock
                            {
                                BlockId = "p1-b1",
                                BlockType = SourceBlockType.Paragraph,
                                Text = input
                            }
                        ]
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
            .Select(page => new SourceDocumentPage
            {
                PageNumber = page.Number,
                IsScanned = string.IsNullOrWhiteSpace(page.Text),
                Blocks =
                [
                    new SourceDocumentBlock
                    {
                        BlockId = $"p{page.Number}-b1",
                        BlockType = SourceBlockType.Paragraph,
                        Text = page.Text
                    }
                ]
            })
            .ToList();

        if (pages.Count == 0)
        {
            pages.Add(new SourceDocumentPage
            {
                PageNumber = 1,
                Blocks =
                [
                    new SourceDocumentBlock
                    {
                        BlockId = "p1-b1",
                        BlockType = SourceBlockType.Paragraph,
                        Text = string.Empty
                    }
                ]
            });
        }

        return new SourceDocument
        {
            DocumentId = Path.GetFileNameWithoutExtension(input),
            Pages = pages
        };
    }

    private static void ExportTextPdf(string text, string outputPath)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var doc = new PdfSharpCore.Pdf.PdfDocument();
        var page = doc.AddPage();
        var gfx = XGraphics.FromPdfPage(page);
        var font = new XFont("Arial", 11);

        const int margin = 40;
        var y = margin;
        var width = page.Width - margin * 2;

        foreach (var line in WrapText(text, 95))
        {
            if (y > page.Height - margin)
            {
                page = doc.AddPage();
                gfx = XGraphics.FromPdfPage(page);
                y = margin;
            }

            gfx.DrawString(line, font, XBrushes.Black, new XRect(margin, y, width, 20), XStringFormats.TopLeft);
            y += 16;
        }

        doc.Save(outputPath);
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
