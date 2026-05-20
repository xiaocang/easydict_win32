using System.Text.Json;
using Easydict.SidecarClient.Protocol;
using Easydict.Workers.LocalAi.Infrastructure;

namespace Easydict.Workers.LocalAi.Handlers;

internal sealed class ListModelsHandler
{
    private readonly WorkerState _state;

    public ListModelsHandler(WorkerState state)
    {
        _state = state;
    }

    public Task<object?> HandleAsync(string requestId, JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (!_state.IsConfigured)
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, "Worker not configured");

        var p = ParseParams(parameters);

        // Only Foundry Local enumerates user-visible models today; PhiSilica is a
        // single canonical model managed by Windows AI; OpenVINO is fixed NLLB-200.
        if (p.Provider != LocalAiProviderModes.FoundryLocal)
        {
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams,
                $"list_models is only supported for FoundryLocal (got: {p.Provider})");
        }

        // FIXME(p1b-follow-up): expose FoundryLocalService.ListAvailableModelsAsync
        // or equivalent. Today the runtime CLI enumeration lives behind private API
        // surface; returning an empty list signals "ask the user to type a model id".
        return Task.FromResult<object?>(new ListModelsResult
        {
            Models = Array.Empty<string>(),
        });
    }

    private static ListModelsParams ParseParams(JsonElement? parameters)
    {
        if (parameters is null)
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, "list_models requires params");
        try
        {
            return parameters.Value.Deserialize<ListModelsParams>(
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
                ?? throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, "list_models params null");
        }
        catch (JsonException ex)
        {
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams,
                $"list_models deserialize failed: {ex.Message}");
        }
    }
}
