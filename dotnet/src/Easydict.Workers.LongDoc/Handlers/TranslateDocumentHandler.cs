using System.Text.Json;
using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService;
using Easydict.TranslationService.LongDocument;
using Easydict.Workers.LongDoc.Infrastructure;

namespace Easydict.Workers.LongDoc.Handlers;

/// <summary>
/// Handles the "translate_document" request. Streams status / progress /
/// block_translated events back to the host during the long-running operation,
/// and returns the final TranslateDocumentResult on completion.
/// </summary>
internal sealed class TranslateDocumentHandler
{
    private readonly WorkerState _state;
    private readonly IpcEventWriter _writer;

    public TranslateDocumentHandler(WorkerState state, IpcEventWriter writer)
    {
        _state = state;
        _writer = writer;
    }

    public async Task<object?> HandleAsync(string requestId, JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (!_state.IsConfigured)
        {
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams,
                "Worker has not received a configure request yet");
        }

        if (parameters is null)
        {
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams,
                "translate_document requires params");
        }

        TranslateDocumentParams? p;
        try
        {
            p = parameters.Value.Deserialize<TranslateDocumentParams>(
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                });
        }
        catch (JsonException ex)
        {
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams,
                $"translate_document deserialization failed: {ex.Message}");
        }

        if (p is null) throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, "translate_document params was null");

        try
        {
            await _writer.WriteEventAsync(LongDocEvents.Status,
                new StatusEventData { Message = "Initializing translation engine..." },
                requestId);

            using var manager = WorkerTranslationManagerFactory.Build(_state.Settings!);

            // Progress callback — streams every IProgress<T> tick as a "progress" event.
            var progress = new Progress<LongDocumentTranslationProgress>(p =>
            {
                _ = _writer.WriteEventAsync(LongDocEvents.Progress, new ProgressEventData
                {
                    Stage = p.Stage.ToString(),
                    CurrentBlock = p.CurrentBlock,
                    TotalBlocks = p.TotalBlocks,
                    CurrentPage = p.CurrentPage,
                    TotalPages = p.TotalPages,
                    Percentage = p.Percentage,
                    CurrentBlockPreview = p.CurrentBlockPreview,
                }, requestId);
            });

            var pipeline = new WorkerLongDocumentPipeline(
                (request, serviceId, ct) => manager.TranslateAsync(request, ct, serviceId));
            var result = await pipeline.TranslateAsync(
                    p,
                    _state.Settings!,
                    progress,
                    cancellationToken,
                    (block, ct) => _writer.WriteEventAsync(LongDocEvents.BlockTranslated, block, requestId))
                .ConfigureAwait(false);

            await _writer.WriteEventAsync(LongDocEvents.Status,
                new StatusEventData { Message = "Translation worker completed." },
                requestId);

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WorkerHandlerException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Map known translation errors to a structured "service_error".
            if (ex is TranslationException tex)
            {
                throw new WorkerHandlerException(WorkerErrorCodes.ServiceError,
                    tex.Message,
                    new { errorCode = tex.ErrorCode.ToString(), serviceId = tex.ServiceId });
            }
            throw;
        }
    }

}
