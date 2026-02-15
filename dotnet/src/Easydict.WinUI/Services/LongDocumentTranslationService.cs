using System.Text;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using UglyToad.PdfPig;

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
    public required Dictionary<int, string> TranslatedChunks { get; init; }
    public required HashSet<int> FailedChunkIndexes { get; init; }
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
    private const int ChunkSize = 1200;

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
        var sourceText = mode switch
        {
            LongDocumentInputMode.Manual => input,
            LongDocumentInputMode.Pdf => ExtractPdfText(input),
            _ => input
        };

        if (string.IsNullOrWhiteSpace(sourceText))
        {
            throw new InvalidOperationException("No source text found for translation.");
        }

        onProgress?.Invoke("Preparing chunks...");
        var chunks = SplitChunks(sourceText, ChunkSize);
        var checkpoint = new LongDocumentTranslationCheckpoint
        {
            SourceChunks = chunks,
            TranslatedChunks = new Dictionary<int, string>(),
            FailedChunkIndexes = new HashSet<int>()
        };

        await TranslatePendingChunksAsync(checkpoint, from, to, serviceId, onProgress, cancellationToken);
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
        if (checkpoint.FailedChunkIndexes.Count == 0)
        {
            return FinalizeResult(checkpoint, outputPath, onProgress);
        }

        onProgress?.Invoke($"Retrying {checkpoint.FailedChunkIndexes.Count} failed chunks...");
        await TranslatePendingChunksAsync(checkpoint, from, to, serviceId, onProgress, cancellationToken);
        return FinalizeResult(checkpoint, outputPath, onProgress);
    }

    private static async Task TranslatePendingChunksAsync(
        LongDocumentTranslationCheckpoint checkpoint,
        Language from,
        Language to,
        string serviceId,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        var manager = TranslationManagerService.Instance.Manager;
        var pendingIndexes = checkpoint.FailedChunkIndexes.Count > 0
            ? checkpoint.FailedChunkIndexes.OrderBy(i => i).ToList()
            : Enumerable.Range(0, checkpoint.SourceChunks.Count).ToList();

        checkpoint.FailedChunkIndexes.Clear();

        for (var i = 0; i < pendingIndexes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkIndex = pendingIndexes[i];
            onProgress?.Invoke($"Translating chunk {chunkIndex + 1}/{checkpoint.SourceChunks.Count} (pending {i + 1}/{pendingIndexes.Count})...");

            try
            {
                var request = new TranslationRequest
                {
                    Text = checkpoint.SourceChunks[chunkIndex],
                    FromLanguage = from,
                    ToLanguage = to
                };

                var result = await manager.TranslateAsync(request, cancellationToken, serviceId);
                if (string.IsNullOrWhiteSpace(result.TranslatedText))
                {
                    checkpoint.FailedChunkIndexes.Add(chunkIndex);
                    continue;
                }

                checkpoint.TranslatedChunks[chunkIndex] = result.TranslatedText.Trim();
            }
            catch
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

        for (var i = 0; i < checkpoint.SourceChunks.Count; i++)
        {
            if (checkpoint.TranslatedChunks.TryGetValue(i, out var translated))
            {
                sb.AppendLine(translated);
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($"[Chunk {i + 1} translation failed. Retry required.]");
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

    private static List<string> SplitChunks(string text, int chunkSize)
    {
        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var len = Math.Min(chunkSize, text.Length - start);
            chunks.Add(text.Substring(start, len));
            start += len;
        }

        return chunks;
    }

    private static void ExportTextPdf(string text, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

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
}
