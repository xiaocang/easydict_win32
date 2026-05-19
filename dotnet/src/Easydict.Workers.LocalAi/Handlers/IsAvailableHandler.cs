using System.Text.Json;
using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService.LocalModels;
using Easydict.Workers.LocalAi.Infrastructure;

namespace Easydict.Workers.LocalAi.Handlers;

internal sealed class IsAvailableHandler
{
    private readonly WorkerState _state;

    public IsAvailableHandler(WorkerState state)
    {
        _state = state;
    }

    public Task<object?> HandleAsync(string requestId, JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (!_state.IsConfigured)
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, "Worker not configured");

        var p = ParseParams(parameters);
        LocalModelStatus status = p.Provider switch
        {
            LocalAiProviderModes.WindowsAI => _state.GetPhiSilica().GetStatus(),
            LocalAiProviderModes.FoundryLocal => _state.GetFoundryLocal().GetStatus(),
            LocalAiProviderModes.OpenVINO => _state.GetOpenVino().GetStatus(),
            _ => throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams,
                $"Unknown provider for is_available: {p.Provider}"),
        };

        return Task.FromResult<object?>(new IsAvailableResult
        {
            Available = status.State == LocalModelState.Ready,
            State = status.State.ToString(),
            Detail = status.DetailMessage,
        });
    }

    private static IsAvailableParams ParseParams(JsonElement? parameters)
    {
        if (parameters is null)
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, "is_available requires params");
        try
        {
            return parameters.Value.Deserialize<IsAvailableParams>(
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
                ?? throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, "is_available params null");
        }
        catch (JsonException ex)
        {
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams,
                $"is_available deserialize failed: {ex.Message}");
        }
    }
}
