using Easydict.WinUI.Services;

namespace Easydict.WinUI.Services.DocumentExport;

/// <summary>
/// Resolves the text that PDF exporters should render for a checkpoint chunk.
/// Failed chunks may still render their source text in PDF output while remaining retryable.
/// </summary>
internal static class PdfExportCheckpointTextResolver
{
    internal static bool TryGetRenderableText(
        LongDocumentTranslationCheckpoint checkpoint,
        int chunkIndex,
        out string text,
        out bool usesSourceFallback)
    {
        text = string.Empty;
        usesSourceFallback = false;

        if (chunkIndex < 0 || chunkIndex >= checkpoint.SourceChunks.Count)
        {
            return false;
        }

        if (checkpoint.TranslatedChunks.TryGetValue(chunkIndex, out var translated) &&
            !string.IsNullOrWhiteSpace(translated))
        {
            text = translated;
            return true;
        }

        if (!checkpoint.FailedChunkIndexes.Contains(chunkIndex))
        {
            return false;
        }

        var metadata = chunkIndex < checkpoint.ChunkMetadata.Count
            ? checkpoint.ChunkMetadata[chunkIndex]
            : null;

        var source = metadata?.FallbackText;
        if (string.IsNullOrWhiteSpace(source))
        {
            source = checkpoint.SourceChunks[chunkIndex];
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        text = source;
        usesSourceFallback = true;
        return true;
    }
}
