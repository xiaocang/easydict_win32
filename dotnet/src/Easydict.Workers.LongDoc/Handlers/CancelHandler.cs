using System.Text.Json;
using Easydict.SidecarClient.Protocol;
using Easydict.Workers.LongDoc.Infrastructure;

namespace Easydict.Workers.LongDoc.Handlers;

internal sealed class CancelHandler
{
    private readonly RequestDispatcher _dispatcher;

    public CancelHandler(RequestDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public Task<object?> HandleAsync(string requestId, JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (parameters is null)
        {
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams,
                "cancel requires params");
        }

        CancelRequestParams? typed;
        try
        {
            typed = parameters.Value.Deserialize<CancelRequestParams>(
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                });
        }
        catch (JsonException ex)
        {
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams,
                $"cancel params deserialization failed: {ex.Message}");
        }

        if (typed is null || string.IsNullOrEmpty(typed.TargetRequestId))
        {
            throw new WorkerHandlerException(WorkerErrorCodes.InvalidParams,
                "cancel.targetRequestId is required");
        }

        var cancelled = _dispatcher.TryCancel(typed.TargetRequestId);
        return Task.FromResult<object?>(new CancelRequestResult { Cancelled = cancelled });
    }
}
