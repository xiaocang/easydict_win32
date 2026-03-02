using System.Text;
using Easydict.TranslationService.LongDocument;

namespace Easydict.WinUI.Services.DocumentExport;

/// <summary>
/// Plain text export service: outputs translated long documents as .txt files.
/// Supports monolingual, bilingual (interleaved blocks), and both modes.
/// </summary>
public sealed class PlainTextExportService : IDocumentExportService
{
    public IReadOnlyList<string> SupportedExtensions => [".txt"];

    public DocumentExportResult Export(
        LongDocumentTranslationCheckpoint checkpoint,
        string sourceFilePath,
        string outputPath,
        DocumentOutputMode outputMode = DocumentOutputMode.Monolingual)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        // 1. Always generate monolingual output first
        var monolingualText = ComposeMonolingualText(checkpoint);
        File.WriteAllText(outputPath, monolingualText, Encoding.UTF8);

        // 2. Handle bilingual mode
        string? bilingualPath = null;
        if (outputMode is DocumentOutputMode.Bilingual or DocumentOutputMode.Both)
        {
            bilingualPath = BuildBilingualOutputPath(outputPath);
            var bilingualText = ComposeBilingualText(checkpoint);
            File.WriteAllText(bilingualPath, bilingualText, Encoding.UTF8);
        }

        // 3. Bilingual-only: delete intermediate monolingual file
        if (outputMode == DocumentOutputMode.Bilingual && bilingualPath != null)
        {
            try { File.Delete(outputPath); } catch { /* best-effort cleanup */ }
            return new DocumentExportResult
            {
                OutputPath = bilingualPath,
                BilingualOutputPath = bilingualPath
            };
        }

        // 4. Both or Monolingual
        return new DocumentExportResult
        {
            OutputPath = outputPath,
            BilingualOutputPath = bilingualPath
        };
    }

    internal static string ComposeMonolingualText(LongDocumentTranslationCheckpoint checkpoint)
    {
        var sb = new StringBuilder();
        var metadataByChunkIndex = checkpoint.ChunkMetadata.ToDictionary(m => m.ChunkIndex);

        var orderedChunkIndexes = Enumerable.Range(0, checkpoint.SourceChunks.Count)
            .OrderBy(index => metadataByChunkIndex[index].PageNumber)
            .ThenBy(index => metadataByChunkIndex[index].OrderInPage)
            .ThenBy(index => index)
            .ToList();

        foreach (var chunkIndex in orderedChunkIndexes)
        {
            if (checkpoint.TranslatedChunks.TryGetValue(chunkIndex, out var translated) && !string.IsNullOrWhiteSpace(translated))
            {
                sb.AppendLine(translated);
                sb.AppendLine();
            }
            else if (checkpoint.FailedChunkIndexes.Contains(chunkIndex))
            {
                sb.AppendLine($"[Chunk {chunkIndex + 1} translation failed.]");
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

    internal static string ComposeBilingualText(LongDocumentTranslationCheckpoint checkpoint)
    {
        var sb = new StringBuilder();
        var metadataByChunkIndex = checkpoint.ChunkMetadata.ToDictionary(m => m.ChunkIndex);

        var orderedChunkIndexes = Enumerable.Range(0, checkpoint.SourceChunks.Count)
            .OrderBy(index => metadataByChunkIndex[index].PageNumber)
            .ThenBy(index => metadataByChunkIndex[index].OrderInPage)
            .ThenBy(index => index)
            .ToList();

        foreach (var chunkIndex in orderedChunkIndexes)
        {
            var source = checkpoint.SourceChunks[chunkIndex];
            sb.AppendLine(source);
            sb.AppendLine();

            if (checkpoint.TranslatedChunks.TryGetValue(chunkIndex, out var translated) && !string.IsNullOrWhiteSpace(translated))
            {
                sb.AppendLine(translated);
            }
            else if (checkpoint.FailedChunkIndexes.Contains(chunkIndex))
            {
                sb.AppendLine($"[Chunk {chunkIndex + 1} translation failed.]");
            }

            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    internal static string BuildBilingualOutputPath(string monolingualPath)
    {
        var dir = Path.GetDirectoryName(monolingualPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(monolingualPath);
        var ext = Path.GetExtension(monolingualPath);
        return Path.Combine(dir, $"{name}-bilingual{ext}");
    }
}
