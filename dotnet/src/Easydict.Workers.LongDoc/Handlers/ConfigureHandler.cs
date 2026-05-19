using System.Text.Json;
using Easydict.SidecarClient.Protocol;
using Easydict.Workers.LongDoc.Infrastructure;

namespace Easydict.Workers.LongDoc.Handlers;

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
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams, "configure requires params");
        }

        ConfigureParams? typed;
        try
        {
            typed = parameters.Value.Deserialize<ConfigureParams>(
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                });
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
        return Task.FromResult<object?>(new ConfigureResult { Ok = true });
    }
}
