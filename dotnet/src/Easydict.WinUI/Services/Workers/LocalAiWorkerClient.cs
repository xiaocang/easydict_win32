using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Easydict.SidecarClient;
using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService;
using Easydict.TranslationService.LocalModels;
using Easydict.TranslationService.Models;

namespace Easydict.WinUI.Services.Workers;

/// <summary>
/// Host-side adapter that runs local AI translation in a child worker process.
/// Drop-in replacement for LocalAITranslationService when SettingsService.UseLocalAiWorker
/// is true. Implements the same interface set, so TranslationManager.RegisterService
/// accepts it as the "local" service.
///
/// Lifecycle: each TranslateAsync / TranslateStreamAsync spawns a fresh worker
/// (the worker exits on completion). Cancellation propagates to the worker via a
/// "cancel" request.
/// </summary>
internal sealed class LocalAiWorkerClient : IStreamTranslationService, IGrammarCorrectionService, ILocalModelProvider, IDisposable
{
    private const string WorkerSubdir = "localai";
    private const string WorkerExeName = "Easydict.Workers.LocalAi.exe";

    // Reuse the same service id as the in-proc service so the rest of the app
    // (Settings UI, history, etc.) treats this as the same service.
    internal const string ServiceIdValue = "windows-local-ai";
    internal const string DisplayNameValue = "Windows Local AI";

    private readonly SettingsService _settings;
    private readonly WorkerSpawner _spawner = new();
    private readonly IStreamTranslationService? _fallbackTranslationService;
    private readonly IGrammarCorrectionService? _fallbackGrammarService;
    private readonly ILocalModelProvider? _fallbackModelProvider;
    private readonly Func<CancellationToken, Task<SidecarClient.SidecarClient>>? _spawnOverride;
    private bool _disposed;

    public LocalAiWorkerClient(
        SettingsService settings,
        IStreamTranslationService? fallbackTranslationService = null,
        IGrammarCorrectionService? fallbackGrammarService = null,
        ILocalModelProvider? fallbackModelProvider = null)
    {
        _settings = settings;
        _fallbackTranslationService = fallbackTranslationService;
        _fallbackGrammarService = fallbackGrammarService;
        _fallbackModelProvider = fallbackModelProvider;
    }

    internal LocalAiWorkerClient(
        SettingsService settings,
        IStreamTranslationService? fallbackTranslationService,
        IGrammarCorrectionService? fallbackGrammarService,
        ILocalModelProvider? fallbackModelProvider,
        Func<CancellationToken, Task<SidecarClient.SidecarClient>> spawnOverride)
        : this(settings, fallbackTranslationService, fallbackGrammarService, fallbackModelProvider)
    {
        _spawnOverride = spawnOverride;
    }

    public string ServiceId => ServiceIdValue;
    public string DisplayName => DisplayNameValue;
    public bool RequiresApiKey => false;
    public bool IsConfigured => true;
    public bool IsStreaming => true;

    public IReadOnlyList<Language> SupportedLanguages { get; } =
        Enum.GetValues<Language>().Where(l => l != Language.Auto).ToArray();

    public event EventHandler<LocalModelStatus>? StatusChanged
    {
        add
        {
            if (_fallbackModelProvider is not null)
            {
                _fallbackModelProvider.StatusChanged += value;
            }
        }
        remove
        {
            if (_fallbackModelProvider is not null)
            {
                _fallbackModelProvider.StatusChanged -= value;
            }
        }
    }

    public bool SupportsLanguagePair(Language from, Language to) => true;

    public Task<Language> DetectLanguageAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult(Language.Auto);

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalAiWorkerClient));

        if (ShouldBypassWorkerForOpenVino() && _fallbackTranslationService is not null)
        {
            Debug.WriteLine("[LocalAiWorker] Bypassing worker for OpenVINO TranslateAsync.");
            return await _fallbackTranslationService.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
        }

        SidecarClient.SidecarClient client;
        try
        {
            client = await SpawnConfiguredAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (CanFallbackToInProc(ex) && _fallbackTranslationService is not null)
        {
            Debug.WriteLine($"[LocalAiWorker] Falling back to in-proc TranslateAsync: {ex.Message}");
            return await _fallbackTranslationService.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
        }

        await using var clientLease = client.ConfigureAwait(false);
        try
        {
            var result = await client.SendRequestAsync<LocalAiTranslateResult>(
                LocalAiMethods.Translate,
                BuildParams(request),
                timeoutMs: 0,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result is null)
            {
                throw new TranslationException("Worker returned null result")
                {
                    ErrorCode = TranslationErrorCode.Unknown,
                    ServiceId = ServiceId,
                };
            }

            return new TranslationResult
            {
                TranslatedText = result.TranslatedText,
                OriginalText = request.Text,
                ServiceName = result.ServiceName,
                TimingMs = result.TimingMs,
                DetectedLanguage = Enum.TryParse<Language>(result.DetectedLanguage, out var lang) ? lang : Language.Auto,
                TargetLanguage = request.ToLanguage,
            };
        }
        catch (SidecarErrorException sex)
        {
            throw MapError(sex);
        }
        catch (SidecarProcessExitedException pex) when (CanFallbackToInProc(pex) && _fallbackTranslationService is not null)
        {
            Debug.WriteLine($"[LocalAiWorker] Falling back to in-proc TranslateAsync after worker exit: {pex.Message}");
            return await _fallbackTranslationService.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (SidecarProcessExitedException pex)
        {
            throw new TranslationException(
                $"Local AI worker exited unexpectedly (code={pex.ExitCode})", pex)
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId,
            };
        }
    }

    public async IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalAiWorkerClient));

        var fallbackTranslationService = _fallbackTranslationService;
        if (ShouldBypassWorkerForOpenVino() && fallbackTranslationService is not null)
        {
            Debug.WriteLine("[LocalAiWorker] Bypassing worker for OpenVINO TranslateStreamAsync.");
            await foreach (var chunk in fallbackTranslationService
                               .TranslateStreamAsync(request, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return chunk;
            }

            yield break;
        }

        SidecarClient.SidecarClient? client = null;
        Exception? fallbackException = null;
        try
        {
            client = await SpawnConfiguredAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (CanFallbackToInProc(ex) && fallbackTranslationService is not null)
        {
            fallbackException = ex;
        }

        if (client is null)
        {
            if (fallbackTranslationService is null)
            {
                throw fallbackException ?? new WorkerStartFailedException("Local AI worker did not start.");
            }

            Debug.WriteLine($"[LocalAiWorker] Falling back to in-proc TranslateStreamAsync: {fallbackException?.Message}");
            await foreach (var chunk in fallbackTranslationService
                               .TranslateStreamAsync(request, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return chunk;
            }

            yield break;
        }

        await using var clientLease = client.ConfigureAwait(false);

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });

        void OnEvent(IpcEvent evt)
        {
            if (evt.Event != LocalAiEvents.Chunk || evt.Data is null) return;
            try
            {
                var chunk = evt.Data.Value.Deserialize<ChunkEventData>(JsonOptions);
                if (chunk is not null && !string.IsNullOrEmpty(chunk.Text))
                {
                    channel.Writer.TryWrite(chunk.Text);
                }
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"[LocalAiWorker] malformed chunk: {ex.Message}");
            }
        }

        client.OnEvent += OnEvent;

        // Drive the request in the background; the foreground loop pulls from the channel.
        var requestTask = Task.Run(async () =>
        {
            try
            {
                    _ = await client.SendRequestAsync<TranslateStreamResult>(
                        LocalAiMethods.TranslateStream,
                        BuildParams(request),
                        timeoutMs: 0,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, cancellationToken);

        var emittedAnyChunk = false;
        var fallbackAfterWorkerExit = false;
        try
        {
            while (true)
            {
                string? textToYield;
                try
                {
                    if (!await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }

                    if (!channel.Reader.TryRead(out textToYield))
                    {
                        continue;
                    }

                    emittedAnyChunk = true;
                }
                catch (SidecarProcessExitedException pex) when (CanFallbackToInProc(pex) && fallbackTranslationService is not null && !emittedAnyChunk)
                {
                    Debug.WriteLine($"[LocalAiWorker] Falling back to in-proc TranslateStreamAsync after worker exit: {pex.Message}");
                    fallbackAfterWorkerExit = true;
                    break;
                }
                catch (SidecarProcessExitedException pex)
                {
                    throw new TranslationException(
                        $"Local AI worker exited unexpectedly (code={pex.ExitCode})", pex)
                    {
                        ErrorCode = TranslationErrorCode.ServiceUnavailable,
                        ServiceId = ServiceId,
                    };
                }

                yield return textToYield;
            }

            // Surface any background exception (e.g. SidecarErrorException → TranslationException).
            try { await requestTask.ConfigureAwait(false); }
            catch (SidecarErrorException sex) { throw MapError(sex); }
            catch (SidecarProcessExitedException pex) when (CanFallbackToInProc(pex) && fallbackTranslationService is not null && !emittedAnyChunk)
            {
                Debug.WriteLine($"[LocalAiWorker] Falling back to in-proc TranslateStreamAsync after worker exit: {pex.Message}");
                fallbackAfterWorkerExit = true;
            }
            catch (SidecarProcessExitedException pex)
            {
                throw new TranslationException(
                    $"Local AI worker exited unexpectedly (code={pex.ExitCode})", pex)
                {
                    ErrorCode = TranslationErrorCode.ServiceUnavailable,
                    ServiceId = ServiceId,
                };
            }
        }
        finally
        {
            client.OnEvent -= OnEvent;
        }

        if (fallbackAfterWorkerExit && fallbackTranslationService is not null)
        {
            await foreach (var chunk in fallbackTranslationService
                               .TranslateStreamAsync(request, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
    }

    public async IAsyncEnumerable<string> CorrectGrammarStreamAsync(
        GrammarCorrectionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Reuses the same channel/event pattern as translation streaming with a
        // different method name.
        if (_disposed) throw new ObjectDisposedException(nameof(LocalAiWorkerClient));

        var fallbackGrammarService = _fallbackGrammarService;
        if (ShouldBypassWorkerForOpenVino() && fallbackGrammarService is not null)
        {
            Debug.WriteLine("[LocalAiWorker] Bypassing worker for OpenVINO CorrectGrammarStreamAsync.");
            await foreach (var chunk in fallbackGrammarService
                               .CorrectGrammarStreamAsync(request, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return chunk;
            }

            yield break;
        }

        SidecarClient.SidecarClient? client = null;
        Exception? fallbackException = null;
        try
        {
            client = await SpawnConfiguredAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (CanFallbackToInProc(ex) && fallbackGrammarService is not null)
        {
            fallbackException = ex;
        }

        if (client is null)
        {
            if (fallbackGrammarService is null)
            {
                throw fallbackException ?? new WorkerStartFailedException("Local AI worker did not start.");
            }

            Debug.WriteLine($"[LocalAiWorker] Falling back to in-proc CorrectGrammarStreamAsync: {fallbackException?.Message}");
            await foreach (var chunk in fallbackGrammarService
                               .CorrectGrammarStreamAsync(request, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return chunk;
            }

            yield break;
        }

        await using var clientLease = client.ConfigureAwait(false);

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });

        void OnEvent(IpcEvent evt)
        {
            if (evt.Event != LocalAiEvents.Chunk || evt.Data is null) return;
            try
            {
                var chunk = evt.Data.Value.Deserialize<ChunkEventData>(JsonOptions);
                if (chunk is not null && !string.IsNullOrEmpty(chunk.Text))
                {
                    channel.Writer.TryWrite(chunk.Text);
                }
            }
            catch (JsonException) { }
        }

        client.OnEvent += OnEvent;

        var requestTask = Task.Run(async () =>
        {
            try
            {
                _ = await client.SendRequestAsync<TranslateStreamResult>(
                    LocalAiMethods.GrammarStream,
                    new LocalAiTranslateParams
                    {
                        Text = request.Text,
                        FromLanguage = request.Language.ToString(),
                        ToLanguage = request.Language.ToString(),
                        ProviderMode = _settings.LocalAIProvider ?? LocalAiProviderModes.Auto,
                    },
                    timeoutMs: 0,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, cancellationToken);

        var emittedAnyChunk = false;
        var fallbackAfterWorkerExit = false;
        try
        {
            while (true)
            {
                string? textToYield;
                try
                {
                    if (!await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }

                    if (!channel.Reader.TryRead(out textToYield))
                    {
                        continue;
                    }

                    emittedAnyChunk = true;
                }
                catch (SidecarProcessExitedException pex) when (CanFallbackToInProc(pex) && fallbackGrammarService is not null && !emittedAnyChunk)
                {
                    Debug.WriteLine($"[LocalAiWorker] Falling back to in-proc CorrectGrammarStreamAsync after worker exit: {pex.Message}");
                    fallbackAfterWorkerExit = true;
                    break;
                }
                catch (SidecarProcessExitedException pex)
                {
                    throw new TranslationException(
                        $"Local AI worker exited unexpectedly (code={pex.ExitCode})", pex)
                    {
                        ErrorCode = TranslationErrorCode.ServiceUnavailable,
                        ServiceId = ServiceId,
                    };
                }

                yield return textToYield;
            }

            try { await requestTask.ConfigureAwait(false); }
            catch (SidecarErrorException sex) { throw MapError(sex); }
            catch (SidecarProcessExitedException pex) when (CanFallbackToInProc(pex) && fallbackGrammarService is not null && !emittedAnyChunk)
            {
                Debug.WriteLine($"[LocalAiWorker] Falling back to in-proc CorrectGrammarStreamAsync after worker exit: {pex.Message}");
                fallbackAfterWorkerExit = true;
            }
            catch (SidecarProcessExitedException pex)
            {
                throw new TranslationException(
                    $"Local AI worker exited unexpectedly (code={pex.ExitCode})", pex)
                {
                    ErrorCode = TranslationErrorCode.ServiceUnavailable,
                    ServiceId = ServiceId,
                };
            }
        }
        finally
        {
            client.OnEvent -= OnEvent;
        }

        if (fallbackAfterWorkerExit && fallbackGrammarService is not null)
        {
            await foreach (var chunk in fallbackGrammarService
                               .CorrectGrammarStreamAsync(request, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
    }

    public bool SupportsGrammarCorrection(Language language) => true;

    public LocalModelStatus GetStatus()
    {
        // Synchronous call would block on a worker spawn; we conservatively report
        // Ready so the Settings UI doesn't gray out. Real availability comes via
        // PrepareAsync which spawns the worker async.
        return new LocalModelStatus(LocalModelState.Ready, ResourceKey: string.Empty);
    }

    public async Task<LocalModelStatus> PrepareAsync(CancellationToken cancellationToken)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalAiWorkerClient));

        if (ShouldBypassWorkerForOpenVino() && _fallbackModelProvider is not null)
        {
            Debug.WriteLine("[LocalAiWorker] Bypassing worker for OpenVINO PrepareAsync.");
            return await _fallbackModelProvider.PrepareAsync(cancellationToken).ConfigureAwait(false);
        }

        SidecarClient.SidecarClient client;
        try
        {
            client = await SpawnConfiguredAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (CanFallbackToInProc(ex) && _fallbackModelProvider is not null)
        {
            Debug.WriteLine($"[LocalAiWorker] Falling back to in-proc PrepareAsync: {ex.Message}");
            return await _fallbackModelProvider.PrepareAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var clientLease = client.ConfigureAwait(false);
        var providerMode = _settings.LocalAIProvider ?? LocalAiProviderModes.Auto;
        var provider = providerMode == LocalAiProviderModes.Auto
            ? LocalAiProviderModes.WindowsAI // pick a concrete one for prepare
            : providerMode;

        LocalModelStatusDto? status;
        try
        {
            status = await client.SendRequestAsync<LocalModelStatusDto>(
                LocalAiMethods.PrepareModel,
                new PrepareModelParams { Provider = provider },
                timeoutMs: 0,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (SidecarProcessExitedException ex) when (CanFallbackToInProc(ex) && _fallbackModelProvider is not null)
        {
            Debug.WriteLine($"[LocalAiWorker] Falling back to in-proc PrepareAsync after worker exit: {ex.Message}");
            return await _fallbackModelProvider.PrepareAsync(cancellationToken).ConfigureAwait(false);
        }

        return MapStatus(status);
    }

    private async Task<SidecarClient.SidecarClient> SpawnConfiguredAsync(CancellationToken ct)
    {
        if (_spawnOverride is not null)
        {
            return await _spawnOverride(ct).ConfigureAwait(false);
        }

        var snapshot = WorkerSpawner.BuildSnapshot(_settings);
        return await _spawner.StartAndConfigureAsync(
            WorkerKinds.LocalAi, WorkerSubdir, WorkerExeName, snapshot, ct).ConfigureAwait(false);
    }

    private bool ShouldBypassWorkerForOpenVino()
    {
        return string.Equals(_settings.LocalAIProvider, LocalAiProviderModes.OpenVINO, StringComparison.OrdinalIgnoreCase);
    }

    private LocalAiTranslateParams BuildParams(TranslationRequest request)
    {
        return new LocalAiTranslateParams
        {
            Text = request.Text,
            FromLanguage = request.FromLanguage.ToString(),
            ToLanguage = request.ToLanguage.ToString(),
            ProviderMode = _settings.LocalAIProvider ?? LocalAiProviderModes.Auto,
        };
    }

    private TranslationException MapError(SidecarErrorException sex)
    {
        var code = sex.Error.Code switch
        {
            WorkerErrorCodes.Cancelled => TranslationErrorCode.Unknown,
            WorkerErrorCodes.ModelMissing => TranslationErrorCode.InvalidModel,
            WorkerErrorCodes.ServiceError => TranslationErrorCode.ServiceUnavailable,
            _ => TranslationErrorCode.Unknown,
        };
        return new TranslationException(sex.Error.Message, sex)
        {
            ErrorCode = code,
            ServiceId = ServiceId,
        };
    }

    internal static bool CanFallbackToInProc(Exception ex)
    {
        return ex is WorkerStartFailedException
            or WorkerVersionMismatchException
            or FileNotFoundException
            or SidecarProcessExitedException;
    }

    private static LocalModelStatus MapStatus(LocalModelStatusDto? dto)
    {
        if (dto is null) return new LocalModelStatus(LocalModelState.Failed, ResourceKey: "WorkerReturnedNoStatus");
        var state = Enum.TryParse<LocalModelState>(dto.State, out var s) ? s : LocalModelState.Failed;
        return new LocalModelStatus(state, dto.StatusKey ?? string.Empty, DetailMessage: dto.Detail);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public void Dispose()
    {
        _disposed = true;
    }
}
