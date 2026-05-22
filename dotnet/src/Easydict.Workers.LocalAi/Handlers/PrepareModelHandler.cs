using System.Diagnostics;
using System.Text.Json;
using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService.LocalModels;
using Easydict.Workers.LocalAi.Infrastructure;

namespace Easydict.Workers.LocalAi.Handlers;

internal sealed class PrepareModelHandler
{
    private readonly WorkerState _state;
    private readonly IpcEventWriter _writer;

    public PrepareModelHandler(WorkerState state, IpcEventWriter writer)
    {
        _state = state;
        _writer = writer;
    }

    public async Task<object?> HandleAsync(string requestId, JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (!_state.IsConfigured)
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, "Worker not configured");

        var p = ParseParams(parameters);
        var sw = Stopwatch.StartNew();
        Trace.WriteLine(
            $"[LocalAiWorker] prepare_model start. requestId={requestId}, provider={p.Provider}, endpointOverride={!string.IsNullOrWhiteSpace(p.Endpoint)}, modelOverride={p.Model}");

        LocalModelStatus status;
        try
        {
            status = p.Provider switch
            {
                LocalAiProviderModes.WindowsAI => await _state.GetPhiSilica().PrepareAsync(cancellationToken).ConfigureAwait(false),
                LocalAiProviderModes.FoundryLocal => await PrepareFoundryAsync(p, cancellationToken).ConfigureAwait(false),
                LocalAiProviderModes.OpenVINO => await _state.GetOpenVino().PrepareAsync(cancellationToken).ConfigureAwait(false),
                _ => throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams,
                    $"Unknown provider for prepare_model: {p.Provider}"),
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            Trace.WriteLine(
                $"[LocalAiWorker] prepare_model exception. requestId={requestId}, provider={p.Provider}, elapsedMs={sw.ElapsedMilliseconds}, exception={ex.GetType().FullName}, message={ex.Message}");
            throw;
        }

        sw.Stop();
        Trace.WriteLine(
            $"[LocalAiWorker] prepare_model success. requestId={requestId}, provider={p.Provider}, elapsedMs={sw.ElapsedMilliseconds}, state={status.State}, resourceKey={status.ResourceKey}, detailLength={status.DetailMessage?.Length ?? 0}");

        return new LocalModelStatusDto
        {
            State = status.State.ToString(),
            StatusKey = status.ResourceKey,
            Detail = status.DetailMessage,
        };
    }

    private async Task<LocalModelStatus> PrepareFoundryAsync(PrepareModelParams p, CancellationToken ct)
    {
        var svc = _state.GetFoundryLocal();
        if (!string.IsNullOrEmpty(p.Endpoint) || !string.IsNullOrEmpty(p.Model))
        {
            svc.Configure(p.Endpoint, p.Model);
        }
        return await svc.PrepareAsync(ct).ConfigureAwait(false);
    }

    private static PrepareModelParams ParseParams(JsonElement? parameters)
    {
        if (parameters is null)
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, "prepare_model requires params");
        try
        {
            return parameters.Value.Deserialize<PrepareModelParams>(
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
                ?? throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, "prepare_model params null");
        }
        catch (JsonException ex)
        {
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, $"prepare_model deserialize failed: {ex.Message}");
        }
    }
}
