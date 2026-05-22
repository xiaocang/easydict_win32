using System.Text.Json;
using System.Diagnostics;
using Easydict.SidecarClient.Protocol;
using Easydict.Workers.LocalAi.Infrastructure;

namespace Easydict.Workers.LocalAi.Handlers;

internal sealed class ConfigureHandler
{
    private readonly WorkerState _state;

    public ConfigureHandler(WorkerState state)
    {
        _state = state;
    }

    public Task<object?> HandleAsync(string requestId, JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (parameters is null)
        {
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams,
                "configure requires params");
        }

        ConfigureParams? typed;
        try
        {
            typed = parameters.Value.Deserialize<ConfigureParams>(
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
        catch (JsonException ex)
        {
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams,
                $"configure params deserialization failed: {ex.Message}");
        }

        if (typed?.Settings is null)
        {
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams,
                "configure.settings is required");
        }

        _state.ApplySettings(typed.Settings);
        Trace.WriteLine(
            $"[LocalAiWorker] configure complete. requestId={requestId}, localAIProvider={typed.Settings.LocalAIProvider}, openVinoDevice={typed.Settings.OpenVinoDevice}, foundryEndpointConfigured={!string.IsNullOrWhiteSpace(typed.Settings.FoundryLocalEndpoint)}, foundryModel={typed.Settings.FoundryLocalModel}");
        return Task.FromResult<object?>(new ConfigureResult { Ok = true });
    }
}
