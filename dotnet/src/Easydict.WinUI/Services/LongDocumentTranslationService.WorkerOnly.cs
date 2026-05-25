using System.Diagnostics;
using Easydict.TranslationService;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services.DocumentExport;

namespace Easydict.WinUI.Services;

public sealed class LongDocumentTranslationResult
{
    public required LongDocumentJobState State { get; init; }
    public required string OutputPath { get; init; }
    public string? BilingualOutputPath { get; init; }
    public required int TotalChunks { get; init; }
    public required int SucceededChunks { get; init; }
    public required IReadOnlyList<int> FailedChunkIndexes { get; init; }
    public required LongDocumentQualityReport QualityReport { get; init; }
    public LongDocumentTranslationCheckpoint? Checkpoint { get; init; }
}

public sealed class LongDocumentTranslationService : IDisposable
{
    private LayoutModelDownloadService? _layoutModelDownloadService;
    private bool _disposed;

    /// <summary>
    /// Gets the layout model download service for UI status checks.
    /// </summary>
    public LayoutModelDownloadService GetLayoutModelDownloadService()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _layoutModelDownloadService ??= new LayoutModelDownloadService();
        return _layoutModelDownloadService;
    }

    public async Task<LongDocumentTranslationResult> TranslateToPdfAsync(
        LongDocumentInputMode mode,
        string input,
        Language from,
        Language to,
        string outputPath,
        string serviceId,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default,
        LayoutDetectionMode layoutDetection = LayoutDetectionMode.Heuristic,
        DocumentOutputMode outputMode = DocumentOutputMode.Monolingual,
        PdfExportMode pdfExportMode = PdfExportMode.ContentStreamReplacement,
        string? visionEndpoint = null,
        string? visionApiKey = null,
        string? visionModel = null,
        IProgress<LongDocumentTranslationProgress>? progress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!SettingsService.Instance.UseLongDocWorker)
        {
            Debug.WriteLine("[LongDocWorker] Worker-only build ignores disabled UseLongDocWorker setting.");
        }

        try
        {
            using var worker = new Workers.LongDocWorkerClient(SettingsService.Instance);
            return await worker.TranslateToPdfAsync(
                mode, input, from, to, outputPath, serviceId, onProgress, cancellationToken,
                layoutDetection, outputMode, pdfExportMode, visionEndpoint, visionApiKey, visionModel,
                progress).ConfigureAwait(false);
        }
        catch (Exception ex) when (Workers.LongDocWorkerClient.CanFallbackToInProc(ex))
        {
            throw new TranslationException(
                "Long-document worker is required in this build and could not start. Repair or reinstall the package.",
                ex)
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = serviceId,
            };
        }
    }

    public Task<LongDocumentTranslationResult> RetryFailedChunksAsync(
        LongDocumentTranslationCheckpoint checkpoint,
        Language from,
        Language to,
        string outputPath,
        string serviceId,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default,
        DocumentOutputMode outputMode = DocumentOutputMode.Monolingual,
        IProgress<LongDocumentTranslationProgress>? progress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Task.FromException<LongDocumentTranslationResult>(new TranslationException(
            "Retry requires an in-process checkpoint, which is not emitted by the worker-only release build. Re-run the document translation.")
        {
            ErrorCode = TranslationErrorCode.ServiceUnavailable,
            ServiceId = serviceId,
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _layoutModelDownloadService?.Dispose();
    }
}
