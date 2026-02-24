using System.Text;
using Easydict.TranslationService.LongDocument;

namespace Easydict.WinUI.Services.DocumentExport;

/// <summary>
/// Markdown export service: outputs translated long documents as .md files.
/// Supports monolingual, bilingual (interleaved blocks with formatting), and both modes.
/// </summary>
public sealed class MarkdownExportService : IDocumentExportService
{
    public IReadOnlyList<string> SupportedExtensions => [".md"];

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
        var monolingualText = ComposeMonolingualMarkdown(checkpoint);
        File.WriteAllText(outputPath, monolingualText, Encoding.UTF8);

        // 2. Handle bilingual mode
        string? bilingualPath = null;
        if (outputMode is DocumentOutputMode.Bilingual or DocumentOutputMode.Both)
        {
            bilingualPath = BuildBilingualOutputPath(outputPath);
            var bilingualText = ComposeBilingualMarkdown(checkpoint);
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

    internal static string ComposeMonolingualMarkdown(LongDocumentTranslationCheckpoint checkpoint)
    {
        var sb = new StringBuilder();
        var metadataByChunkIndex = checkpoint.ChunkMetadata.ToDictionary(m => m.ChunkIndex);

        var orderedChunkIndexes = Enumerable.Range(0, checkpoint.SourceChunks.Count)
            .OrderBy(index => metadataByChunkIndex[index].PageNumber)
            .ThenBy(index => metadataByChunkIndex[index].OrderInPage)
            .ThenBy(index => index)
            .ToList();

        int? currentPage = null;
        foreach (var chunkIndex in orderedChunkIndexes)
        {
            var metadata = metadataByChunkIndex[chunkIndex];

            // Page header for multi-page documents
            if (currentPage != metadata.PageNumber && checkpoint.ChunkMetadata.Select(m => m.PageNumber).Distinct().Count() > 1)
            {
                if (currentPage != null) sb.AppendLine();
                sb.AppendLine($"## Page {metadata.PageNumber}");
                sb.AppendLine();
                currentPage = metadata.PageNumber;
            }

            if (checkpoint.TranslatedChunks.TryGetValue(chunkIndex, out var translated) && !string.IsNullOrWhiteSpace(translated))
            {
                // Preserve heading style if the source block was a heading
                if (metadata.SourceBlockType == SourceBlockType.Heading && !translated.StartsWith('#'))
                {
                    sb.AppendLine($"### {translated}");
                }
                else
                {
                    sb.AppendLine(translated);
                }

                sb.AppendLine();
            }
            else if (checkpoint.FailedChunkIndexes.Contains(chunkIndex))
            {
                sb.AppendLine($"> *[Chunk {chunkIndex + 1} translation failed.]*");
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

    internal static string ComposeBilingualMarkdown(LongDocumentTranslationCheckpoint checkpoint)
    {
        var sb = new StringBuilder();
        var metadataByChunkIndex = checkpoint.ChunkMetadata.ToDictionary(m => m.ChunkIndex);

        var orderedChunkIndexes = Enumerable.Range(0, checkpoint.SourceChunks.Count)
            .OrderBy(index => metadataByChunkIndex[index].PageNumber)
            .ThenBy(index => metadataByChunkIndex[index].OrderInPage)
            .ThenBy(index => index)
            .ToList();

        int? currentPage = null;
        foreach (var chunkIndex in orderedChunkIndexes)
        {
            var metadata = metadataByChunkIndex[chunkIndex];

            // Page header for multi-page documents
            if (currentPage != metadata.PageNumber && checkpoint.ChunkMetadata.Select(m => m.PageNumber).Distinct().Count() > 1)
            {
                if (currentPage != null) sb.AppendLine();
                sb.AppendLine($"## Page {metadata.PageNumber}");
                sb.AppendLine();
                currentPage = metadata.PageNumber;
            }

            // Original text in blockquote
            var source = checkpoint.SourceChunks[chunkIndex];
            foreach (var line in source.Split('\n'))
            {
                sb.AppendLine($"> {line}");
            }

            sb.AppendLine();

            // Translated text
            if (checkpoint.TranslatedChunks.TryGetValue(chunkIndex, out var translated) && !string.IsNullOrWhiteSpace(translated))
            {
                if (metadata.SourceBlockType == SourceBlockType.Heading && !translated.StartsWith('#'))
                {
                    sb.AppendLine($"### {translated}");
                }
                else
                {
                    sb.AppendLine(translated);
                }
            }
            else if (checkpoint.FailedChunkIndexes.Contains(chunkIndex))
            {
                sb.AppendLine($"> *[Chunk {chunkIndex + 1} translation failed.]*");
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
