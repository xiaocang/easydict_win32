using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Easydict.TranslationService.LocalModels;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Microsoft Foundry Local provider using its OpenAI-compatible local endpoint.
/// The Foundry Local service chooses a dynamic port, so the endpoint can either
/// be configured explicitly or discovered from <c>foundry service status</c>.
/// </summary>
public sealed class FoundryLocalService : BaseOpenAIService, ILocalModelProvider
{
    public const string ServiceIdValue = "foundry-local";
    public const string DefaultModel = "qwen2.5-0.5b";
    private static readonly TimeSpan EndpointReadyTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan EndpointReadyInitialDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan EndpointReadyMaxDelay = TimeSpan.FromSeconds(1);

    private readonly IFoundryLocalEndpointResolver _endpointResolver;
    private readonly IFoundryLocalRuntimeController? _runtimeController;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private string _configuredEndpoint = "";
    private string? _resolvedEndpoint;
    private string _model = DefaultModel;
    private string? _resolvedModel;

    public FoundryLocalService(
        HttpClient httpClient,
        IFoundryLocalEndpointResolver? endpointResolver = null)
        : base(httpClient)
    {
        _endpointResolver = endpointResolver ?? new FoundryLocalCliEndpointResolver();
        _runtimeController = _endpointResolver as IFoundryLocalRuntimeController;
    }

    public override string ServiceId => ServiceIdValue;

    public override string DisplayName => "Foundry Local";

    public override bool RequiresApiKey => false;

    public override bool IsConfigured => !string.IsNullOrWhiteSpace(_model);

    public override IReadOnlyList<Language> SupportedLanguages => OpenAILanguages;

    public override string Endpoint => _resolvedEndpoint ?? _configuredEndpoint;

    public override string ApiKey => "";

    public override string Model => _resolvedModel ?? _model;

    public event EventHandler<LocalModelStatus>? StatusChanged;

    public void Configure(string? endpoint = null, string? model = null)
    {
        _configuredEndpoint = NormalizeChatCompletionsEndpoint(endpoint);
        _resolvedEndpoint = null;
        _resolvedModel = null;
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        Debug.WriteLine(
            $"[FoundryLocal] Configure: configuredEndpoint={FormatEndpointForLog(_configuredEndpoint)}, model={_model}");
    }

    public LocalModelStatus GetStatus()
    {
        return IsConfigured
            ? new LocalModelStatus(LocalModelState.Ready, FoundryLocalResources.StatusKeys.Ready)
            : new LocalModelStatus(LocalModelState.Failed, FoundryLocalResources.StatusKeys.NotConfigured);
    }

    public async Task<LocalModelStatus> PrepareAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            Debug.WriteLine("[FoundryLocal] Prepare skipped: service is not configured.");
            return PublishStatus(new LocalModelStatus(
                LocalModelState.Failed,
                FoundryLocalResources.StatusKeys.NotConfigured));
        }

        try
        {
            var shouldWaitForEndpointReady = false;
            var runtimeController = _runtimeController;
            var shouldUseRuntimeController = ShouldUseRuntimeControllerForEndpointLifecycle();
            Debug.WriteLine(
                $"[FoundryLocal] Prepare begin: configuredEndpoint={FormatEndpointForLog(_configuredEndpoint)}, resolvedEndpoint={FormatEndpointForLog(_resolvedEndpoint)}, model={_model}, hasRuntimeController={runtimeController is not null}, useRuntimeLifecycle={shouldUseRuntimeController}");
            if (shouldUseRuntimeController && runtimeController is not null)
            {
                var runtimeStatus = await runtimeController
                    .GetStatusAsync(cancellationToken)
                    .ConfigureAwait(false);
                Debug.WriteLine(
                    $"[FoundryLocal] Prepare runtime status: state={runtimeStatus.State}, endpoint={FormatEndpointForLog(runtimeStatus.Endpoint)}, detail={TrimForLog(runtimeStatus.DetailMessage)}");

                if (runtimeStatus.State == FoundryLocalRuntimeState.NotInstalled)
                {
                    Debug.WriteLine("[FoundryLocal] Prepare stopped: Foundry CLI is not installed.");
                    return PublishStatus(new LocalModelStatus(
                        LocalModelState.NotCompatible,
                        FoundryLocalResources.StatusKeys.NotInstalled,
                        DetailMessage: runtimeStatus.DetailMessage));
                }

                if (runtimeStatus.State == FoundryLocalRuntimeState.NotRunning)
                {
                    Debug.WriteLine("[FoundryLocal] Starting Foundry Local service...");
                    PublishStatus(new LocalModelStatus(
                        LocalModelState.Preparing,
                        FoundryLocalResources.StatusKeys.Starting));
                    await runtimeController.StartServiceAsync(cancellationToken).ConfigureAwait(false);
                    Debug.WriteLine("[FoundryLocal] Foundry Local service reported running after start.");
                }

                Debug.WriteLine($"[FoundryLocal] Loading Foundry Local model: {_model}");
                PublishStatus(new LocalModelStatus(
                    LocalModelState.Preparing,
                    FoundryLocalResources.StatusKeys.LoadingModel));
                await runtimeController.LoadModelAsync(_model, cancellationToken).ConfigureAwait(false);
                Debug.WriteLine($"[FoundryLocal] Model load command completed: {_model}");

                _resolvedEndpoint = null;
                _resolvedModel = null;
                shouldWaitForEndpointReady = true;
            }

            if (shouldWaitForEndpointReady)
            {
                Debug.WriteLine("[FoundryLocal] Waiting for endpoint readiness after runtime start/load.");
                await WaitForEndpointReadyAsync(
                    cancellationToken,
                    useRuntimeEndpoint: shouldUseRuntimeController)
                    .ConfigureAwait(false);
            }
            else
            {
                Debug.WriteLine("[FoundryLocal] Ensuring endpoint/model without runtime lifecycle.");
                await EnsureEndpointAsync(cancellationToken).ConfigureAwait(false);
                await EnsureModelAsync(cancellationToken, requireSuccessfulModelsEndpoint: false).ConfigureAwait(false);
            }

            var status = GetStatus();
            Debug.WriteLine(
                $"[FoundryLocal] Prepare complete: state={status.State}, endpoint={FormatEndpointForLog(Endpoint)}, model={Model}");
            return PublishStatus(status);
        }
        catch (FoundryLocalCliNotFoundException ex)
        {
            Debug.WriteLine($"[FoundryLocal] Prepare failed: CLI not found. {TrimForLog(ex.Message)}");
            return PublishStatus(new LocalModelStatus(
                LocalModelState.NotCompatible,
                FoundryLocalResources.StatusKeys.NotInstalled,
                DetailMessage: ex.Message));
        }
        catch (FoundryLocalCliCommandException ex)
        {
            Debug.WriteLine($"[FoundryLocal] Prepare failed: CLI command error. {TrimForLog(ex.Message)}");
            return PublishStatus(new LocalModelStatus(
                LocalModelState.Failed,
                FoundryLocalResources.StatusKeys.StartFailed,
                DetailMessage: ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine(
                $"[FoundryLocal] Prepare failed: {ex.GetType().Name}: {TrimForLog(ex.Message)}");
            return PublishStatus(new LocalModelStatus(
                LocalModelState.Failed,
                FoundryLocalResources.StatusKeys.NotRunning,
                DetailMessage: ex.Message));
        }
    }

    public async Task<LocalModelStatus> CheckRuntimeStatusAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            Debug.WriteLine("[FoundryLocal] Runtime status skipped: service is not configured.");
            return new LocalModelStatus(LocalModelState.Failed, FoundryLocalResources.StatusKeys.NotConfigured);
        }

        if (_runtimeController is null
            || (!string.IsNullOrWhiteSpace(_configuredEndpoint)
                && !IsLoopbackEndpoint(_configuredEndpoint)))
        {
            Debug.WriteLine(
                $"[FoundryLocal] Runtime status treated as ready without CLI lifecycle: configuredEndpoint={FormatEndpointForLog(_configuredEndpoint)}, hasRuntimeController={_runtimeController is not null}");
            return new LocalModelStatus(LocalModelState.Ready, FoundryLocalResources.StatusKeys.Ready);
        }

        try
        {
            var status = await _runtimeController
                .GetStatusAsync(cancellationToken)
                .ConfigureAwait(false);
            Debug.WriteLine(
                $"[FoundryLocal] Runtime status: state={status.State}, endpoint={FormatEndpointForLog(status.Endpoint)}, detail={TrimForLog(status.DetailMessage)}");

            if (status.State == FoundryLocalRuntimeState.NotInstalled)
            {
                return new LocalModelStatus(
                    LocalModelState.NotCompatible,
                    FoundryLocalResources.StatusKeys.NotInstalled,
                    DetailMessage: status.DetailMessage);
            }

            if (status.State == FoundryLocalRuntimeState.NotRunning)
            {
                return new LocalModelStatus(
                    LocalModelState.NeedsPreparation,
                    FoundryLocalResources.StatusKeys.NotRunning,
                    DetailMessage: status.DetailMessage);
            }

            var endpoint = NormalizeChatCompletionsEndpoint(status.Endpoint);
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                endpoint = NormalizeChatCompletionsEndpoint(
                    await _endpointResolver.ResolveChatCompletionsEndpointAsync(cancellationToken)
                        .ConfigureAwait(false));
            }

            return string.IsNullOrWhiteSpace(endpoint)
                ? new LocalModelStatus(
                    LocalModelState.Failed,
                    FoundryLocalResources.StatusKeys.StartFailed,
                    DetailMessage: "Foundry Local service is running but did not report a local endpoint.")
                : new LocalModelStatus(
                    LocalModelState.Ready,
                    FoundryLocalResources.StatusKeys.Ready,
                    DetailMessage: status.DetailMessage);
        }
        catch (FoundryLocalCliNotFoundException ex)
        {
            Debug.WriteLine($"[FoundryLocal] Runtime status failed: CLI not found. {TrimForLog(ex.Message)}");
            return new LocalModelStatus(
                LocalModelState.NotCompatible,
                FoundryLocalResources.StatusKeys.NotInstalled,
                DetailMessage: ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine(
                $"[FoundryLocal] Runtime status failed: {ex.GetType().Name}: {TrimForLog(ex.Message)}");
            return new LocalModelStatus(
                LocalModelState.Failed,
                FoundryLocalResources.StatusKeys.StartFailed,
                DetailMessage: ex.Message);
        }
    }

    private LocalModelStatus PublishStatus(LocalModelStatus status)
    {
        StatusChanged?.Invoke(this, status);
        return status;
    }

    public override async IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in StreamWithEndpointRefreshAsync(
            () => base.TranslateStreamAsync(request, cancellationToken),
            cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    public override async IAsyncEnumerable<string> CorrectGrammarStreamAsync(
        GrammarCorrectionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in StreamWithEndpointRefreshAsync(
            () => base.CorrectGrammarStreamAsync(request, cancellationToken),
            cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    protected override void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_model))
        {
            throw new TranslationException("Foundry Local model is not configured")
            {
                ErrorCode = TranslationErrorCode.InvalidModel,
                ServiceId = ServiceId,
                DocumentationUrl = FoundryLocalResources.InstallDocumentationUrl
            };
        }

        if (string.IsNullOrWhiteSpace(Endpoint))
        {
            throw new TranslationException(
                "Foundry Local endpoint is not configured. Start Foundry Local or set the endpoint manually.")
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId,
                RecoveryAction = FoundryLocalResources.StartRecoveryAction,
                DocumentationUrl = FoundryLocalResources.InstallDocumentationUrl
            };
        }
    }

    private async Task EnsureEndpointAsync(
        CancellationToken cancellationToken,
        bool useRuntimeEndpoint = false)
    {
        if (!useRuntimeEndpoint && !string.IsNullOrWhiteSpace(_configuredEndpoint))
        {
            Debug.WriteLine(
                $"[FoundryLocal] EnsureEndpoint using configured endpoint: {FormatEndpointForLog(_configuredEndpoint)}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(_resolvedEndpoint))
        {
            Debug.WriteLine(
                $"[FoundryLocal] EnsureEndpoint using cached resolved endpoint: {FormatEndpointForLog(_resolvedEndpoint)}");
            return;
        }

        string? resolvedEndpoint;
        try
        {
            if (_runtimeController is not null)
            {
                var status = await _runtimeController
                    .GetStatusAsync(cancellationToken)
                    .ConfigureAwait(false);
                Debug.WriteLine(
                    $"[FoundryLocal] EnsureEndpoint runtime status: state={status.State}, endpoint={FormatEndpointForLog(status.Endpoint)}");
                if (status.State == FoundryLocalRuntimeState.NotInstalled)
                {
                    throw CreateFoundryLocalNotInstalledException(null);
                }

                if (status.State == FoundryLocalRuntimeState.NotRunning)
                {
                    throw CreateFoundryLocalNotRunningException(status.DetailMessage);
                }

                if (!string.IsNullOrWhiteSpace(status.Endpoint))
                {
                    _resolvedEndpoint = NormalizeChatCompletionsEndpoint(status.Endpoint);
                    Debug.WriteLine(
                        $"[FoundryLocal] EnsureEndpoint resolved from runtime status: {FormatEndpointForLog(_resolvedEndpoint)}");
                    return;
                }
            }

            resolvedEndpoint = await _endpointResolver
                .ResolveChatCompletionsEndpointAsync(cancellationToken)
                .ConfigureAwait(false);
            Debug.WriteLine(
                $"[FoundryLocal] EnsureEndpoint resolved from endpoint resolver: {FormatEndpointForLog(resolvedEndpoint)}");
        }
        catch (FoundryLocalCliNotFoundException ex)
        {
            throw CreateFoundryLocalNotInstalledException(ex);
        }

        if (string.IsNullOrWhiteSpace(resolvedEndpoint))
        {
            throw CreateFoundryLocalNotRunningException(null);
        }

        _resolvedEndpoint = NormalizeChatCompletionsEndpoint(resolvedEndpoint);
        Debug.WriteLine(
            $"[FoundryLocal] EnsureEndpoint normalized endpoint: {FormatEndpointForLog(_resolvedEndpoint)}");
    }

    private TranslationException CreateFoundryLocalNotInstalledException(Exception? inner)
    {
        var message =
            "Foundry Local CLI is not installed or is not available on PATH. Install Foundry Local, then start a local model, or configure the endpoint manually. " +
            $"Install guide: {FoundryLocalResources.InstallDocumentationUrl}";
        return inner is null
            ? new TranslationException(message)
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId,
                RecoveryAction = FoundryLocalResources.InstallRecoveryAction,
                DocumentationUrl = FoundryLocalResources.InstallDocumentationUrl
            }
            : new TranslationException(message, inner)
        {
            ErrorCode = TranslationErrorCode.ServiceUnavailable,
            ServiceId = ServiceId,
            RecoveryAction = FoundryLocalResources.InstallRecoveryAction,
            DocumentationUrl = FoundryLocalResources.InstallDocumentationUrl
        };
    }

    private TranslationException CreateFoundryLocalNotRunningException(
        string? detailMessage,
        Exception? inner = null)
    {
        var message = "Foundry Local service is not running. Start it with the Foundry Local CLI or configure an endpoint.";
        if (!string.IsNullOrWhiteSpace(detailMessage))
        {
            message = $"{message} {detailMessage.Trim()}";
        }

        return inner is null
            ? new TranslationException(message)
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId,
                RecoveryAction = FoundryLocalResources.StartRecoveryAction,
                DocumentationUrl = FoundryLocalResources.InstallDocumentationUrl
            }
            : new TranslationException(message, inner)
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId,
                RecoveryAction = FoundryLocalResources.StartRecoveryAction,
                DocumentationUrl = FoundryLocalResources.InstallDocumentationUrl
            };
    }

    private TranslationException CreateFoundryLocalEndpointUnavailableException(
        TranslationException inner,
        string? detailMessage = null)
    {
        var endpoint = Endpoint;
        var message = string.IsNullOrWhiteSpace(endpoint)
            ? "Foundry Local service is not accepting connections. Start Foundry Local and load the configured model, or configure an endpoint."
            : $"Foundry Local service is not accepting connections at {endpoint}. Start Foundry Local and load the configured model, or configure an endpoint.";
        if (!string.IsNullOrWhiteSpace(detailMessage))
        {
            message = $"{message} {detailMessage.Trim()}";
        }

        return new TranslationException(message, inner)
        {
            ErrorCode = TranslationErrorCode.ServiceUnavailable,
            ServiceId = ServiceId,
            RecoveryAction = FoundryLocalResources.StartRecoveryAction,
            DocumentationUrl = FoundryLocalResources.InstallDocumentationUrl
        };
    }

    private async Task WaitForEndpointReadyAsync(
        CancellationToken cancellationToken,
        bool useRuntimeEndpoint)
    {
        var deadline = DateTimeOffset.UtcNow + EndpointReadyTimeout;
        var delay = EndpointReadyInitialDelay;
        var attempt = 0;

        while (true)
        {
            attempt++;
            Debug.WriteLine(
                $"[FoundryLocal] Endpoint readiness attempt {attempt}: useRuntimeEndpoint={useRuntimeEndpoint}, currentEndpoint={FormatEndpointForLog(Endpoint)}");
            await EnsureEndpointAsync(cancellationToken, useRuntimeEndpoint).ConfigureAwait(false);
            if (await EnsureModelAsync(cancellationToken, requireSuccessfulModelsEndpoint: true).ConfigureAwait(false))
            {
                Debug.WriteLine(
                    $"[FoundryLocal] Endpoint ready after {attempt} attempt(s): endpoint={FormatEndpointForLog(Endpoint)}, model={Model}");
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                Debug.WriteLine(
                    $"[FoundryLocal] Endpoint readiness timed out after {attempt} attempt(s): endpoint={FormatEndpointForLog(Endpoint)}");
                throw new TranslationException(
                    $"Foundry Local endpoint did not become ready within {EndpointReadyTimeout.TotalSeconds:0} seconds.")
                {
                    ErrorCode = TranslationErrorCode.ServiceUnavailable,
                    ServiceId = ServiceId,
                    RecoveryAction = FoundryLocalResources.StartRecoveryAction,
                    DocumentationUrl = FoundryLocalResources.InstallDocumentationUrl
                };
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            if (useRuntimeEndpoint)
            {
                _resolvedEndpoint = null;
            }

            delay = TimeSpan.FromMilliseconds(Math.Min(
                delay.TotalMilliseconds * 2,
                EndpointReadyMaxDelay.TotalMilliseconds));
        }
    }

    private async Task<bool> EnsureModelAsync(
        CancellationToken cancellationToken,
        bool requireSuccessfulModelsEndpoint)
    {
        if (!string.IsNullOrWhiteSpace(_resolvedModel)
            || string.IsNullOrWhiteSpace(_model)
            || string.IsNullOrWhiteSpace(Endpoint))
        {
            Debug.WriteLine(
                $"[FoundryLocal] EnsureModel skipped: resolvedModel={_resolvedModel}, configuredModel={_model}, endpoint={FormatEndpointForLog(Endpoint)}");
            return true;
        }

        var modelsEndpoint = GetModelsEndpoint(Endpoint);
        if (string.IsNullOrWhiteSpace(modelsEndpoint))
        {
            Debug.WriteLine(
                $"[FoundryLocal] EnsureModel skipped: no models endpoint for {FormatEndpointForLog(Endpoint)}");
            return true;
        }

        try
        {
            Debug.WriteLine($"[FoundryLocal] EnsureModel querying: {FormatEndpointForLog(modelsEndpoint)}");
            using var response = await HttpClient.GetAsync(modelsEndpoint, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var nextAction = requireSuccessfulModelsEndpoint
                    ? "waiting for endpoint readiness"
                    : $"continuing with configured model {_model}";
                Debug.WriteLine(
                    $"[FoundryLocal] EnsureModel models endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}; {nextAction}");
                return !requireSuccessfulModelsEndpoint;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            _resolvedModel = TryResolveModelId(json, _model);
            Debug.WriteLine(
                $"[FoundryLocal] EnsureModel resolved model: configured={_model}, resolved={_resolvedModel ?? _model}");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"[FoundryLocal] Failed to resolve model id: {TrimForLog(ex.Message)}");
            return false;
        }
    }

    private async IAsyncEnumerable<string> StreamWithEndpointRefreshAsync(
        Func<IAsyncEnumerable<string>> createStream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var retriedAfterEndpointRefresh = false;

        // The local runtime is prone to aborting long streaming responses under concurrent generation.
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                await EnsureEndpointAsync(cancellationToken).ConfigureAwait(false);
                await EnsureModelAsync(cancellationToken, requireSuccessfulModelsEndpoint: false).ConfigureAwait(false);

                var emittedAnyChunk = false;
                var shouldRetry = false;

                await using var enumerator = createStream().GetAsyncEnumerator(cancellationToken);
                while (true)
                {
                    bool hasNext;
                    string chunk;

                    try
                    {
                        hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                        if (!hasNext)
                        {
                            yield break;
                        }

                        chunk = enumerator.Current;
                    }
                    catch (TranslationException ex) when (!emittedAnyChunk && IsEndpointRefreshableNetworkError(ex))
                    {
                        if (!retriedAfterEndpointRefresh
                            && await TryRefreshEndpointAfterNetworkFailureAsync(ex, cancellationToken).ConfigureAwait(false))
                        {
                            retriedAfterEndpointRefresh = true;
                            shouldRetry = true;
                            break;
                        }

                        var recoveryException = await CreateFoundryLocalRecoveryExceptionForNetworkFailureAsync(ex, cancellationToken)
                            .ConfigureAwait(false);
                        throw recoveryException;
                    }

                    emittedAnyChunk = true;
                    yield return chunk;
                }

                if (!shouldRetry)
                {
                    yield break;
                }
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<bool> TryRefreshEndpointAfterNetworkFailureAsync(
        TranslationException ex,
        CancellationToken cancellationToken)
    {
        var previousEndpoint = Endpoint;
        if (!IsLoopbackEndpoint(previousEndpoint))
        {
            Debug.WriteLine(
                $"[FoundryLocal] Endpoint refresh skipped after {ex.ErrorCode}: endpoint is not loopback ({FormatEndpointForLog(previousEndpoint)})");
            return false;
        }

        try
        {
            var resolvedEndpoint = await _endpointResolver
                .ResolveChatCompletionsEndpointAsync(cancellationToken)
                .ConfigureAwait(false);
            var normalizedEndpoint = NormalizeChatCompletionsEndpoint(resolvedEndpoint);
            if (string.IsNullOrWhiteSpace(normalizedEndpoint))
            {
                Debug.WriteLine($"[FoundryLocal] Endpoint refresh returned no endpoint after {ex.ErrorCode}.");
                return false;
            }

            _resolvedEndpoint = normalizedEndpoint;
            _resolvedModel = null;
            Debug.WriteLine(
                $"[FoundryLocal] Refreshed endpoint after {ex.ErrorCode}: {previousEndpoint} -> {normalizedEndpoint}");
            return true;
        }
        catch (Exception refreshError) when (refreshError is not OperationCanceledException)
        {
            Debug.WriteLine($"[FoundryLocal] Failed to refresh endpoint after network error: {refreshError.Message}");
            return false;
        }
    }

    private async Task<TranslationException> CreateFoundryLocalRecoveryExceptionForNetworkFailureAsync(
        TranslationException ex,
        CancellationToken cancellationToken)
    {
        if (!IsLoopbackEndpoint(Endpoint))
        {
            Debug.WriteLine(
                $"[FoundryLocal] Network failure recovery uses original exception: endpoint is not loopback ({FormatEndpointForLog(Endpoint)})");
            return ex;
        }

        var runtimeController = _runtimeController;
        if (ShouldUseRuntimeControllerForEndpointLifecycle()
            && runtimeController is not null)
        {
            try
            {
                var status = await runtimeController
                    .GetStatusAsync(cancellationToken)
                    .ConfigureAwait(false);
                Debug.WriteLine(
                    $"[FoundryLocal] Network failure runtime status: state={status.State}, endpoint={FormatEndpointForLog(status.Endpoint)}, detail={TrimForLog(status.DetailMessage)}");
                if (status.State == FoundryLocalRuntimeState.NotInstalled)
                {
                    return CreateFoundryLocalNotInstalledException(ex);
                }

                if (status.State == FoundryLocalRuntimeState.NotRunning)
                {
                    return CreateFoundryLocalNotRunningException(status.DetailMessage, ex);
                }

                if (!string.IsNullOrWhiteSpace(status.Endpoint))
                {
                    _resolvedEndpoint = NormalizeChatCompletionsEndpoint(status.Endpoint);
                    Debug.WriteLine(
                        $"[FoundryLocal] Network failure updated endpoint from runtime status: {FormatEndpointForLog(_resolvedEndpoint)}");
                }

                return CreateFoundryLocalEndpointUnavailableException(ex, status.DetailMessage);
            }
            catch (Exception statusError) when (statusError is not OperationCanceledException)
            {
                Debug.WriteLine($"[FoundryLocal] Failed to check service status after network error: {statusError.Message}");
            }
        }

        return CreateFoundryLocalEndpointUnavailableException(ex);
    }

    private static bool IsEndpointRefreshableNetworkError(TranslationException ex)
    {
        return ex.ErrorCode is TranslationErrorCode.NetworkError or TranslationErrorCode.Timeout;
    }

    private bool ShouldUseRuntimeControllerForEndpointLifecycle()
    {
        return _runtimeController is not null
            && (string.IsNullOrWhiteSpace(_configuredEndpoint)
                || IsLoopbackEndpoint(_configuredEndpoint));
    }

    private static string FormatEndpointForLog(string? endpoint)
    {
        return string.IsNullOrWhiteSpace(endpoint) ? "<empty>" : endpoint;
    }

    public static string TrimForLog(string? value, int maxLength = 240)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= maxLength
            ? normalized
            : $"{normalized[..maxLength]}...";
    }

    private static bool IsLoopbackEndpoint(string? endpoint)
    {
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            && uri.IsLoopback;
    }

    internal static string? GetModelsEndpoint(string chatCompletionsEndpoint)
    {
        if (!Uri.TryCreate(chatCompletionsEndpoint, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var basePath = path[..^"/chat/completions".Length];
        var builder = new UriBuilder(uri)
        {
            Path = $"{basePath}/models",
            Query = "",
            Fragment = "",
        };
        return builder.Uri.ToString();
    }

    internal static string? TryResolveModelId(string modelListJson, string configuredModel)
    {
        if (string.IsNullOrWhiteSpace(configuredModel)
            || string.IsNullOrWhiteSpace(modelListJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(modelListJson);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var ids = data.EnumerateArray()
                .Select(model => model.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .ToArray();

            var exact = ids.FirstOrDefault(id =>
                string.Equals(id, configuredModel, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact))
            {
                return exact;
            }

            var aliasPrefix = $"{configuredModel}-instruct-";
            var aliasMatches = ids
                .Where(id => id.StartsWith(aliasPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(GetFoundryDevicePreference)
                .ToArray();

            return aliasMatches.FirstOrDefault();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int GetFoundryDevicePreference(string modelId)
    {
        if (modelId.Contains("openvino-npu", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("-npu", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (modelId.Contains("openvino-gpu", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("-gpu", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (modelId.Contains("-cpu", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 3;
    }

    public static string NormalizeChatCompletionsEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return "";
        }

        var normalized = endpoint.Trim().TrimEnd('/');
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.TrimEnd('/');
            if (path.Equals("/openai/status", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/status", StringComparison.OrdinalIgnoreCase))
            {
                var builder = new UriBuilder(uri)
                {
                    Path = "/v1/chat/completions",
                    Query = "",
                    Fragment = "",
                };
                return builder.Uri.ToString().TrimEnd('/');
            }

            if (path.StartsWith("/openai/load/", StringComparison.OrdinalIgnoreCase))
            {
                var builder = new UriBuilder(uri)
                {
                    Path = "/v1/chat/completions",
                    Query = "",
                    Fragment = "",
                };
                return builder.Uri.ToString().TrimEnd('/');
            }
        }

        if (normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return $"{normalized}/chat/completions";
        }

        return $"{normalized}/v1/chat/completions";
    }
}

public sealed class FoundryLocalCliNotFoundException : Exception
{
    public FoundryLocalCliNotFoundException(Exception inner)
        : base("Foundry Local CLI is not installed or is not available on PATH.", inner)
    {
    }
}

public sealed class FoundryLocalCliCommandException : Exception
{
    public FoundryLocalCliCommandException(string command, int exitCode, string output)
        : base(BuildMessage(command, exitCode, output))
    {
        Command = command;
        ExitCode = exitCode;
        Output = output;
    }

    public string Command { get; }

    public int ExitCode { get; }

    public string Output { get; }

    private static string BuildMessage(string command, int exitCode, string output)
    {
        var detail = string.IsNullOrWhiteSpace(output)
            ? "No output."
            : output.Trim();
        return $"foundry {command} failed with exit code {exitCode}. {detail}";
    }
}

public interface IFoundryLocalEndpointResolver
{
    Task<string?> ResolveChatCompletionsEndpointAsync(CancellationToken cancellationToken);
}

public enum FoundryLocalRuntimeState
{
    NotInstalled,
    NotRunning,
    Running,
}

public sealed record FoundryLocalRuntimeStatus(
    FoundryLocalRuntimeState State,
    string? Endpoint = null,
    string? DetailMessage = null);

public interface IFoundryLocalRuntimeController : IFoundryLocalEndpointResolver
{
    Task<FoundryLocalRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task StartServiceAsync(CancellationToken cancellationToken);

    Task LoadModelAsync(string model, CancellationToken cancellationToken);
}

public sealed class FoundryLocalCliEndpointResolver : IFoundryLocalRuntimeController
{
    private const int CommandTimeoutExitCode = -2;
    private static readonly TimeSpan DefaultStatusCommandTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DefaultStartCommandTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultModelLoadCommandTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ProcessCleanupTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CommandProgressLogInterval = TimeSpan.FromSeconds(2);
    private static readonly Regex UrlRegex = new(
        @"https?://[^\s""'<>]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AnsiEscapeRegex = new(
        @"\x1B\[[0-?]*[ -/]*[@-~]",
        RegexOptions.Compiled);
    private static readonly string[] StatusLineAnchors =
    [
        "Model management service",
        "Foundry Local service",
        "To start the service",
    ];

    private readonly string _executableName;
    private readonly TimeSpan _statusCommandTimeout;
    private readonly TimeSpan _startCommandTimeout;
    private readonly TimeSpan _modelLoadCommandTimeout;

    public FoundryLocalCliEndpointResolver(
        string? executableName = null,
        TimeSpan? statusCommandTimeout = null,
        TimeSpan? startCommandTimeout = null,
        TimeSpan? modelLoadCommandTimeout = null)
    {
        _executableName = string.IsNullOrWhiteSpace(executableName)
            ? "foundry"
            : executableName;
        _statusCommandTimeout = statusCommandTimeout ?? DefaultStatusCommandTimeout;
        _startCommandTimeout = startCommandTimeout ?? DefaultStartCommandTimeout;
        _modelLoadCommandTimeout = modelLoadCommandTimeout ?? DefaultModelLoadCommandTimeout;
    }

    public async Task<FoundryLocalRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var output = await RunFoundryAsync(
                    ["service", "status"],
                    cancellationToken,
                    timeout: _statusCommandTimeout)
                .ConfigureAwait(false);
            return ParseRuntimeStatus(output);
        }
        catch (FoundryLocalCliNotFoundException)
        {
            return new FoundryLocalRuntimeStatus(
                FoundryLocalRuntimeState.NotInstalled,
                DetailMessage: "Foundry Local CLI is not installed or is not available on PATH.");
        }
    }

    public async Task StartServiceAsync(CancellationToken cancellationToken)
    {
        await RunFoundryServiceStartAndWaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task LoadModelAsync(string model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new FoundryLocalCliCommandException(
                "model load",
                exitCode: -1,
                output: "Foundry Local model is not configured.");
        }

        await RunFoundryAsync(
                ["model", "load", model.Trim()],
                cancellationToken,
                throwOnNonZeroExit: true,
                timeout: _modelLoadCommandTimeout)
            .ConfigureAwait(false);
    }

    public async Task<string?> ResolveChatCompletionsEndpointAsync(CancellationToken cancellationToken)
    {
        foreach (var arguments in new[]
        {
            new[] { "service", "status" },
            new[] { "service", "status", "--verbose" },
            new[] { "service", "status", "--json" },
        })
        {
            var output = await RunFoundryAsync(
                    arguments,
                    cancellationToken,
                    timeout: _statusCommandTimeout)
                .ConfigureAwait(false);
            var endpoint = TryExtractEndpoint(output);
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                return endpoint;
            }
        }

        return TryExtractEndpointFromDefaultLogDirectory();
    }

    public static FoundryLocalRuntimeStatus ParseRuntimeStatus(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new FoundryLocalRuntimeStatus(FoundryLocalRuntimeState.NotRunning);
        }

        var endpoint = TryExtractEndpoint(output);
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            return new FoundryLocalRuntimeStatus(FoundryLocalRuntimeState.Running, endpoint);
        }

        if (ContainsMissingCliStatus(output))
        {
            return new FoundryLocalRuntimeStatus(
                FoundryLocalRuntimeState.NotInstalled,
                DetailMessage: TrimOutputForStatus(output));
        }

        if (ContainsNotRunningStatus(output))
        {
            return new FoundryLocalRuntimeStatus(
                FoundryLocalRuntimeState.NotRunning,
                DetailMessage: TrimOutputForStatus(output));
        }

        if (output.Contains("running", StringComparison.OrdinalIgnoreCase))
        {
            return new FoundryLocalRuntimeStatus(
                FoundryLocalRuntimeState.Running,
                DetailMessage: TrimOutputForStatus(output));
        }

        return new FoundryLocalRuntimeStatus(
            FoundryLocalRuntimeState.NotRunning,
            DetailMessage: TrimOutputForStatus(output));
    }

    private static bool ContainsNotRunningStatus(string output)
    {
        return output.Contains("not running", StringComparison.OrdinalIgnoreCase)
            || output.Contains("isn't running", StringComparison.OrdinalIgnoreCase)
            || output.Contains("is not running", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsMissingCliStatus(string output)
    {
        return output.Contains("not recognized", StringComparison.OrdinalIgnoreCase)
            || output.Contains("command not found", StringComparison.OrdinalIgnoreCase)
            || output.Contains("executable file not found", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TrimOutputForStatus(string output)
    {
        var text = string.Join(
                Environment.NewLine,
                output
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(SanitizeStatusLine)
                    .Where(line => !string.IsNullOrWhiteSpace(line)))
            .Trim();
        return string.IsNullOrWhiteSpace(text)
            ? null
            : text.Length <= 512 ? text : text[..512];
    }

    private static string SanitizeStatusLine(string line)
    {
        var text = AnsiEscapeRegex.Replace(line, "").Trim();
        foreach (var anchor in StatusLineAnchors)
        {
            var index = text.IndexOf(anchor, StringComparison.OrdinalIgnoreCase);
            if (index > 0 && !ContainsAsciiLetterOrDigit(text[..index]))
            {
                return text[index..].Trim();
            }
        }

        return text;
    }

    private static bool ContainsAsciiLetterOrDigit(string text)
    {
        return text.Any(ch =>
            ch is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9');
    }

    public static string? TryExtractEndpoint(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var candidates = UrlRegex.Matches(output)
            .Select(match => match.Value.TrimEnd('.', ',', ';', ')', ']'))
            .Select(FoundryLocalService.NormalizeChatCompletionsEndpoint)
            .Where(endpoint => endpoint.Contains("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return candidates.FirstOrDefault(endpoint =>
                endpoint.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                || endpoint.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault();
    }

    public static string? TryExtractLatestEndpoint(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var lines = output.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var endpoint = TryExtractEndpoint(lines[i]);
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                return endpoint;
            }
        }

        return null;
    }

    public static string? TryExtractEndpointFromLogDirectory(string? logDirectory)
    {
        if (string.IsNullOrWhiteSpace(logDirectory) || !Directory.Exists(logDirectory))
        {
            return null;
        }

        foreach (var logPath in Directory.EnumerateFiles(logDirectory, "foundry*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(5))
        {
            try
            {
                using var stream = new FileStream(
                    logPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var endpoint = TryExtractLatestEndpoint(reader.ReadToEnd());
                if (!string.IsNullOrWhiteSpace(endpoint))
                {
                    return endpoint;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    private static string? TryExtractEndpointFromDefaultLogDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return null;
        }

        return TryExtractEndpointFromLogDirectory(Path.Combine(userProfile, ".foundry", "logs"));
    }

    private async Task<string> RunFoundryAsync(
        string[] arguments,
        CancellationToken cancellationToken,
        bool throwOnNonZeroExit = false,
        TimeSpan? timeout = null)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = _executableName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        var command = string.Join(' ', arguments);
        var commandStopwatch = Stopwatch.StartNew();
        try
        {
            Debug.WriteLine(
                $"[FoundryLocal] Running CLI command: foundry {command}; exe={_executableName}; timeout={FormatDuration(timeout)}");
            process.Start();
            Debug.WriteLine(
                $"[FoundryLocal] CLI process started: pid={process.Id}, command=foundry {command}, exe={_executableName}");
        }
        catch (Win32Exception ex)
        {
            throw new FoundryLocalCliNotFoundException(ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = timeout.HasValue
            ? new CancellationTokenSource(timeout.Value)
            : null;
        using var linkedCts = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token);
            var progressTask = LogCommandProgressAsync(command, commandStopwatch, progressCts.Token);
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            await progressCts.CancelAsync().ConfigureAwait(false);
            await progressTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true
            && !cancellationToken.IsCancellationRequested)
        {
            Debug.WriteLine(
                $"[FoundryLocal] CLI command timed out after {FormatDuration(timeout!.Value)}: foundry {command}, pid={process.Id}");
            await KillProcessAsync(process).ConfigureAwait(false);
            var timeoutOutput = await ReadProcessOutputBestEffortAsync(stdoutTask, stderrTask)
                .ConfigureAwait(false);
            throw new FoundryLocalCliCommandException(
                command,
                CommandTimeoutExitCode,
                BuildTimeoutOutput(timeout.Value, timeoutOutput));
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"[FoundryLocal] CLI command canceled: foundry {command}");
            await KillProcessAsync(process).ConfigureAwait(false);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var output = $"{stdout}{Environment.NewLine}{stderr}";
        Debug.WriteLine(
            $"[FoundryLocal] CLI command exited: foundry {command}, exitCode={process.ExitCode}, elapsed={FormatDuration(commandStopwatch.Elapsed)}, stdoutChars={stdout.Length}, stderrChars={stderr.Length}");
        if (throwOnNonZeroExit && process.ExitCode != 0)
        {
            throw new FoundryLocalCliCommandException(
                string.Join(' ', arguments),
                process.ExitCode,
                output);
        }

        return output;
    }

    private async Task RunFoundryServiceStartAndWaitAsync(CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = _executableName,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };
        process.StartInfo.ArgumentList.Add("service");
        process.StartInfo.ArgumentList.Add("start");

        const string command = "service start";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Debug.WriteLine(
                $"[FoundryLocal] Starting CLI command and polling status: foundry {command}; exe={_executableName}; timeout={FormatDuration(_startCommandTimeout)}");
            process.Start();
            Debug.WriteLine(
                $"[FoundryLocal] CLI service start process started: pid={process.Id}, exe={_executableName}");
        }
        catch (Win32Exception ex)
        {
            throw new FoundryLocalCliNotFoundException(ex);
        }

        var pollDelay = TimeSpan.FromMilliseconds(300);
        FoundryLocalRuntimeStatus? lastStatus = null;
        var attempt = 0;

        while (stopwatch.Elapsed < _startCommandTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            lastStatus = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            Debug.WriteLine(
                $"[FoundryLocal] service start poll {attempt}: elapsed={FormatDuration(stopwatch.Elapsed)}, process={FormatProcessState(process)}, state={lastStatus.State}, endpoint={lastStatus.Endpoint ?? "<empty>"}, detail={FoundryLocalService.TrimForLog(lastStatus.DetailMessage, 180)}");

            if (lastStatus.State == FoundryLocalRuntimeState.Running)
            {
                Debug.WriteLine(
                    $"[FoundryLocal] Foundry Local service is running after {FormatDuration(stopwatch.Elapsed)}; startProcess={FormatProcessState(process)}");
                return;
            }

            if (TryGetExitedProcessExitCode(process, out var exitCode) && exitCode != 0)
            {
                throw new FoundryLocalCliCommandException(
                    command,
                    exitCode,
                    $"foundry service start exited with code {exitCode}. Latest status: {FoundryLocalService.TrimForLog(lastStatus.DetailMessage, 180)}");
            }

            var remaining = _startCommandTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(remaining < pollDelay ? remaining : pollDelay, cancellationToken)
                .ConfigureAwait(false);
        }

        Debug.WriteLine(
            $"[FoundryLocal] service start timed out after {FormatDuration(stopwatch.Elapsed)}; process={FormatProcessState(process)}, lastState={lastStatus?.State.ToString() ?? "<unknown>"}, lastDetail={FoundryLocalService.TrimForLog(lastStatus?.DetailMessage, 180)}");
        if (!IsProcessExited(process))
        {
            await KillProcessAsync(process).ConfigureAwait(false);
        }

        throw new FoundryLocalCliCommandException(
            command,
            CommandTimeoutExitCode,
            BuildTimeoutOutput(_startCommandTimeout, $"Latest status: {FoundryLocalService.TrimForLog(lastStatus?.DetailMessage, 180)}"));
    }

    private static async Task LogCommandProgressAsync(
        string command,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(CommandProgressLogInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Debug.WriteLine(
                    $"[FoundryLocal] CLI command still running after {FormatDuration(stopwatch.Elapsed)}: foundry {command}");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string BuildTimeoutOutput(TimeSpan timeout, string output)
    {
        var message = $"Timed out after {FormatDuration(timeout)}.";
        return string.IsNullOrWhiteSpace(output)
            ? message
            : $"{message}{Environment.NewLine}{output.Trim()}";
    }

    private static async Task KillProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                Debug.WriteLine(
                    $"[FoundryLocal] Killing CLI process: pid={process.Id}, hasExited=False");
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            return;
        }
        catch (Win32Exception ex)
        {
            Debug.WriteLine($"[FoundryLocal] Failed to kill timed-out CLI process: {ex.Message}");
            return;
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(ProcessCleanupTimeout)
                .ConfigureAwait(false);
            Debug.WriteLine("[FoundryLocal] CLI process exited after kill request.");
        }
        catch (TimeoutException)
        {
            Debug.WriteLine("[FoundryLocal] Timed-out CLI process did not exit after kill request.");
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static bool IsProcessExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static bool TryGetExitedProcessExitCode(Process process, out int exitCode)
    {
        exitCode = 0;
        try
        {
            if (!process.HasExited)
            {
                return false;
            }

            exitCode = process.ExitCode;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string FormatProcessState(Process process)
    {
        try
        {
            return process.HasExited
                ? $"exited:{process.ExitCode}"
                : $"running:{process.Id}";
        }
        catch (InvalidOperationException)
        {
            return "unavailable";
        }
    }

    private static async Task<string> ReadProcessOutputBestEffortAsync(
        Task<string> stdoutTask,
        Task<string> stderrTask)
    {
        var stdout = await ReadOutputTaskBestEffortAsync(stdoutTask).ConfigureAwait(false);
        var stderr = await ReadOutputTaskBestEffortAsync(stderrTask).ConfigureAwait(false);
        return $"{stdout}{Environment.NewLine}{stderr}";
    }

    private static async Task<string> ReadOutputTaskBestEffortAsync(Task<string> outputTask)
    {
        try
        {
            return await outputTask.WaitAsync(ProcessCleanupTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException)
        {
            return "";
        }
    }

    private static string FormatDuration(TimeSpan? value)
    {
        if (value is null)
        {
            return "<none>";
        }

        return FormatDuration(value.Value);
    }

    private static string FormatDuration(TimeSpan value)
    {
        return value.TotalSeconds >= 1
            ? $"{value.TotalSeconds:0.#}s"
            : $"{value.TotalMilliseconds:0}ms";
    }
}
