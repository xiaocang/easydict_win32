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
    private bool _disposed;

    public LocalAiWorkerClient(SettingsService settings)
    {
        _settings = settings;
    }

    public string ServiceId => ServiceIdValue;
    public string DisplayName => DisplayNameValue;
    public bool RequiresApiKey => false;
    public bool IsConfigured => true;
    public bool IsStreaming => true;

    public IReadOnlyList<Language> SupportedLanguages { get; } =
        Enum.GetValues<Language>().Where(l => l != Language.Auto).ToArray();

    public event EventHandler<LocalModelStatus>? StatusChanged;

    public bool SupportsLanguagePair(Language from, Language to) => true;

    public Task<Language> DetectLanguageAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult(Language.Auto);

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalAiWorkerClient));

        await using var client = await SpawnConfiguredAsync(cancellationToken).ConfigureAwait(false);
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

        await using var client = await SpawnConfiguredAsync(cancellationToken).ConfigureAwait(false);

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

        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var text))
                {
                    yield return text;
                }
            }

            // Surface any background exception (e.g. SidecarErrorException → TranslationException).
            try { await requestTask.ConfigureAwait(false); }
            catch (SidecarErrorException sex) { throw MapError(sex); }
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
    }

    public async IAsyncEnumerable<string> CorrectGrammarStreamAsync(
        GrammarCorrectionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Reuses the same channel/event pattern as translation streaming with a
        // different method name.
        if (_disposed) throw new ObjectDisposedException(nameof(LocalAiWorkerClient));

        await using var client = await SpawnConfiguredAsync(cancellationToken).ConfigureAwait(false);

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

        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var text))
                {
                    yield return text;
                }
            }

            try { await requestTask.ConfigureAwait(false); }
            catch (SidecarErrorException sex) { throw MapError(sex); }
        }
        finally
        {
            client.OnEvent -= OnEvent;
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

        await using var client = await SpawnConfiguredAsync(cancellationToken).ConfigureAwait(false);
        var providerMode = _settings.LocalAIProvider ?? LocalAiProviderModes.Auto;
        var provider = providerMode == LocalAiProviderModes.Auto
            ? LocalAiProviderModes.WindowsAI // pick a concrete one for prepare
            : providerMode;

        var status = await client.SendRequestAsync<LocalModelStatusDto>(
            LocalAiMethods.PrepareModel,
            new PrepareModelParams { Provider = provider },
            timeoutMs: 0,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return MapStatus(status);
    }

    private async Task<SidecarClient.SidecarClient> SpawnConfiguredAsync(CancellationToken ct)
    {
        var snapshot = WorkerSpawner.BuildSnapshot(_settings);
        return await _spawner.StartAndConfigureAsync(
            WorkerKinds.LocalAi, WorkerSubdir, WorkerExeName, snapshot, ct).ConfigureAwait(false);
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
