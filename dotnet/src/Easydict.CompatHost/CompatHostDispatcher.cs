using System.Diagnostics;
using System.Text.Json;
using Easydict.SidecarClient.Protocol;

namespace Easydict.CompatHost;

public sealed class CompatHostDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ICompatHostTranslator _translator;
    private readonly ICompatHostOcrRecognizer? _ocrRecognizer;
    private readonly ICompatHostLongDocTranslator? _longDocTranslator;
    private readonly ICompatHostLocalAiService? _localAiService;
    private readonly ICompatHostMdxLookupService? _mdxLookupService;
    private readonly ICompatHostSettingsMigrator? _settingsMigrator;
    private readonly CompatHostRuntimeState _runtimeState;

    public CompatHostDispatcher(
        ICompatHostTranslator translator,
        ICompatHostOcrRecognizer? ocrRecognizer = null,
        ICompatHostLongDocTranslator? longDocTranslator = null,
        ICompatHostLocalAiService? localAiService = null,
        ICompatHostMdxLookupService? mdxLookupService = null,
        ICompatHostSettingsMigrator? settingsMigrator = null,
        CompatHostRuntimeState? runtimeState = null)
    {
        _translator = translator;
        _ocrRecognizer = ocrRecognizer;
        _longDocTranslator = longDocTranslator;
        _localAiService = localAiService;
        _mdxLookupService = mdxLookupService;
        _settingsMigrator = settingsMigrator;
        _runtimeState = runtimeState ?? new CompatHostRuntimeState();
    }

    public async Task<bool> DispatchAsync(
        string jsonLine,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        IpcRequest? request;
        try
        {
            request = JsonLineSerializer.Deserialize<IpcRequest>(jsonLine);
        }
        catch (JsonException ex)
        {
            Trace.WriteLine($"[CompatHost] Malformed JSON on stdin: {ex.Message}");
            return false;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Method))
        {
            Trace.WriteLine("[CompatHost] Missing id/method on inbound request");
            return false;
        }

        try
        {
            switch (request.Method)
            {
                case CompatHostMethods.Translate:
                    var result = await _translator.TranslateAsync(
                            ParseParams<TranslateParams>(request.Params),
                            cancellationToken)
                        .ConfigureAwait(false);
                    await WriteResponseAsync(output, request.Id, result).ConfigureAwait(false);
                    return false;

                case CompatHostMethods.TranslateStream:
                    var streamResult = await _translator.TranslateStreamAsync(
                            ParseParams<TranslateParams>(request.Params),
                            async (chunk, ct) =>
                            {
                                await WriteEventAsync(
                                        output,
                                        request.Id,
                                        IpcEventTypes.TranslateChunk,
                                        new TranslateChunkEventData { Text = chunk })
                                    .ConfigureAwait(false);
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    await WriteEventAsync(
                            output,
                            request.Id,
                            IpcEventTypes.TranslateDone,
                            streamResult)
                        .ConfigureAwait(false);
                    await WriteResponseAsync(output, request.Id, streamResult).ConfigureAwait(false);
                    return false;

                case CompatHostMethods.GrammarCorrect:
                    var grammarResult = await _translator.CorrectGrammarAsync(
                            ParseParams<GrammarCorrectParams>(request.Params),
                            async (chunk, ct) =>
                            {
                                await WriteEventAsync(
                                        output,
                                        request.Id,
                                        IpcEventTypes.GrammarChunk,
                                        new GrammarChunkEventData { Text = chunk })
                                    .ConfigureAwait(false);
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    await WriteEventAsync(
                            output,
                            request.Id,
                            IpcEventTypes.GrammarDone,
                            grammarResult)
                        .ConfigureAwait(false);
                    await WriteResponseAsync(output, request.Id, grammarResult).ConfigureAwait(false);
                    return false;

                case WorkerMethods.Shutdown:
                    await WriteResponseAsync(output, request.Id, new { ok = true }).ConfigureAwait(false);
                    return true;

                case WorkerMethods.Configure:
                    var configureParams = ParseParams<ConfigureParams>(request.Params);
                    _runtimeState.Configure(configureParams.Settings);
                    await WriteResponseAsync(output, request.Id, new ConfigureResult { Ok = true }).ConfigureAwait(false);
                    return false;

                case CompatHostMethods.OcrRecognize:
                    if (_ocrRecognizer is null)
                    {
                        await WriteErrorAsync(
                                output,
                                request.Id,
                                IpcErrorCodes.ServiceError,
                                $"Compat host method is not implemented yet: {request.Method}")
                            .ConfigureAwait(false);
                        return false;
                    }

                    var ocrResult = await _ocrRecognizer.RecognizeAsync(
                            ParseParams<OcrRecognizeParams>(request.Params),
                            _runtimeState.Settings,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await WriteResponseAsync(output, request.Id, ocrResult).ConfigureAwait(false);
                    return false;

                case CompatHostMethods.LongDocTranslate:
                    if (_longDocTranslator is null)
                    {
                        await WriteErrorAsync(
                                output,
                                request.Id,
                                IpcErrorCodes.ServiceError,
                                $"Compat host method is not implemented yet: {request.Method}")
                            .ConfigureAwait(false);
                        return false;
                    }

                    var longDocResult = await _longDocTranslator.TranslateAsync(
                            ParseParams<TranslateDocumentParams>(request.Params),
                            _runtimeState.Settings,
                            evt => WriteForwardedEvent(output, request.Id, evt),
                            cancellationToken)
                        .ConfigureAwait(false);
                    await WriteResponseAsync(output, request.Id, longDocResult).ConfigureAwait(false);
                    return false;

                case CompatHostMethods.LocalAiPrepare:
                    if (_localAiService is null)
                    {
                        await WriteErrorAsync(
                                output,
                                request.Id,
                                IpcErrorCodes.ServiceError,
                                $"Compat host method is not implemented yet: {request.Method}")
                            .ConfigureAwait(false);
                        return false;
                    }

                    var prepareResult = await _localAiService.PrepareModelAsync(
                            ParseParams<PrepareModelParams>(request.Params),
                            _runtimeState.Settings,
                            evt => WriteForwardedEvent(output, request.Id, evt),
                            cancellationToken)
                        .ConfigureAwait(false);
                    await WriteResponseAsync(output, request.Id, prepareResult).ConfigureAwait(false);
                    return false;

                case CompatHostMethods.LocalAiTranslate:
                    if (_localAiService is null)
                    {
                        await WriteErrorAsync(
                                output,
                                request.Id,
                                IpcErrorCodes.ServiceError,
                                $"Compat host method is not implemented yet: {request.Method}")
                            .ConfigureAwait(false);
                        return false;
                    }

                    var localAiResult = await _localAiService.TranslateAsync(
                            ParseParams<LocalAiTranslateParams>(request.Params),
                            _runtimeState.Settings,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await WriteResponseAsync(output, request.Id, localAiResult).ConfigureAwait(false);
                    return false;

                case CompatHostMethods.MdxLookup:
                    if (_mdxLookupService is null)
                    {
                        await WriteErrorAsync(
                                output,
                                request.Id,
                                IpcErrorCodes.ServiceError,
                                $"Compat host method is not implemented yet: {request.Method}")
                            .ConfigureAwait(false);
                        return false;
                    }

                    var mdxResult = await _mdxLookupService.LookupAsync(
                            ParseParams<MdxLookupParams>(request.Params),
                            _runtimeState.Settings,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await WriteResponseAsync(output, request.Id, mdxResult).ConfigureAwait(false);
                    return false;

                case CompatHostMethods.SettingsMigrate:
                    if (_settingsMigrator is null)
                    {
                        await WriteErrorAsync(
                                output,
                                request.Id,
                                IpcErrorCodes.ServiceError,
                                $"Compat host method is not implemented yet: {request.Method}")
                            .ConfigureAwait(false);
                        return false;
                    }

                    var settingsResult = await _settingsMigrator.MigrateAsync(
                            ParseParams<SettingsMigrateParams>(request.Params),
                            cancellationToken)
                        .ConfigureAwait(false);
                    await WriteResponseAsync(output, request.Id, settingsResult).ConfigureAwait(false);
                    return false;

                default:
                    await WriteErrorAsync(
                            output,
                            request.Id,
                            IpcErrorCodes.MethodNotFound,
                            $"Unknown method: {request.Method}")
                        .ConfigureAwait(false);
                    return false;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await WriteErrorAsync(
                    output,
                    request.Id,
                    WorkerErrorCodes.Cancelled,
                    $"Request {request.Id} cancelled")
                .ConfigureAwait(false);
            return false;
        }
        catch (CompatHostException ex)
        {
            await WriteErrorAsync(output, request.Id, ex.Code, ex.Message, ex.Details)
                .ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[CompatHost] Unhandled exception in {request.Method}: {ex}");
            await WriteErrorAsync(
                    output,
                    request.Id,
                    IpcErrorCodes.InternalError,
                    ex.Message,
                    new { exception = ex.GetType().FullName })
                .ConfigureAwait(false);
            return false;
        }
    }

    private static T ParseParams<T>(object? parameters)
    {
        if (parameters is JsonElement element)
        {
            return element.Deserialize<T>(JsonOptions)
                ?? throw new CompatHostException(IpcErrorCodes.InvalidParams, $"{typeof(T).Name} was null");
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(parameters, JsonOptions);
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
            ?? throw new CompatHostException(IpcErrorCodes.InvalidParams, $"{typeof(T).Name} was null");
    }

    private static async Task WriteResponseAsync(TextWriter output, string id, object result)
    {
        await output.WriteLineAsync(JsonLineSerializer.Serialize(new
        {
            id,
            result,
        })).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    private static async Task WriteEventAsync(
        TextWriter output,
        string id,
        string eventName,
        object data)
    {
        await output.WriteLineAsync(JsonLineSerializer.Serialize(new IpcEvent
        {
            Id = id,
            Event = eventName,
            Data = JsonLineSerializer.ToElement(data),
        })).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    private static void WriteForwardedEvent(TextWriter output, string id, IpcEvent evt)
    {
        output.WriteLine(JsonLineSerializer.Serialize(new IpcEvent
        {
            Id = id,
            Event = evt.Event,
            Data = evt.Data,
        }));
        output.Flush();
    }

    private static async Task WriteErrorAsync(
        TextWriter output,
        string id,
        string code,
        string message,
        object? details = null)
    {
        await output.WriteLineAsync(JsonLineSerializer.Serialize(new
        {
            id,
            error = new IpcError
            {
                Code = code,
                Message = message,
                Details = details is null ? null : JsonLineSerializer.ToElement(details),
            },
        })).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }
}

public sealed class CompatHostException : Exception
{
    public string Code { get; }
    public object? Details { get; }

    public CompatHostException(string code, string message, object? details = null)
        : base(message)
    {
        Code = code;
        Details = details;
    }
}
