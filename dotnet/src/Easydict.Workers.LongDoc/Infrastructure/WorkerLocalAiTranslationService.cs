using System.Diagnostics;
using System.Text.Json;
using Easydict.SidecarClient;
using Easydict.SidecarClient.Protocol;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;

namespace Easydict.Workers.LongDoc.Infrastructure;

/// <summary>
/// Long-doc worker adapter for the host's "windows-local-ai" service id.
/// It starts the sibling LocalAi worker per request so OpenVINO/WindowsAI native
/// state remains outside both WinUI and the long-doc process.
/// </summary>
internal sealed class WorkerLocalAiTranslationService : ITranslationService
{
    private const string WorkerSubdir = "localai";
    private const string WorkerExeName = "Easydict.Workers.LocalAi.exe";
    private const int HandshakeTimeoutMs = 10_000;
    private const int ConfigureTimeoutMs = 10_000;

    private readonly SettingsSnapshot _snapshot;

    public WorkerLocalAiTranslationService(SettingsSnapshot snapshot)
    {
        _snapshot = snapshot;
    }

    public string ServiceId => "windows-local-ai";
    public string DisplayName => "Windows Local AI";
    public bool RequiresApiKey => false;
    public bool IsConfigured => true;

    public IReadOnlyList<Language> SupportedLanguages { get; } =
        Enum.GetValues<Language>().Where(language => language != Language.Auto).ToArray();

    public bool SupportsLanguagePair(Language from, Language to) => true;

    public Task<Language> DetectLanguageAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult(Language.Auto);

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var client = await StartAndConfigureAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await client.SendRequestAsync<LocalAiTranslateResult>(
                LocalAiMethods.Translate,
                new LocalAiTranslateParams
                {
                    Text = request.Text,
                    FromLanguage = request.FromLanguage.ToString(),
                    ToLanguage = request.ToLanguage.ToString(),
                    ProviderMode = _snapshot.LocalAIProvider ?? LocalAiProviderModes.Auto,
                },
                timeoutMs: 0,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result is null)
            {
                throw new TranslationException("Local AI worker returned null result")
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
                DetectedLanguage = Enum.TryParse<Language>(
                    result.DetectedLanguage,
                    ignoreCase: true,
                    out var detected)
                    ? detected
                    : Language.Auto,
                TargetLanguage = request.ToLanguage,
            };
        }
        catch (SidecarErrorException ex)
        {
            throw new TranslationException(ex.Error.Message, ex)
            {
                ErrorCode = ex.Error.Code switch
                {
                    WorkerErrorCodes.ModelMissing => TranslationErrorCode.InvalidModel,
                    WorkerErrorCodes.ServiceError => TranslationErrorCode.ServiceUnavailable,
                    WorkerErrorCodes.Cancelled => TranslationErrorCode.Unknown,
                    _ => TranslationErrorCode.Unknown,
                },
                ServiceId = ServiceId,
            };
        }
        catch (SidecarProcessExitedException ex)
        {
            throw new TranslationException($"Local AI worker exited unexpectedly (code={ex.ExitCode})", ex)
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId,
            };
        }
    }

    private async Task<SidecarClient.SidecarClient> StartAndConfigureAsync(CancellationToken cancellationToken)
    {
        var exePath = ResolveLocalAiWorkerPath();
        if (!File.Exists(exePath))
        {
            throw new TranslationException($"Local AI worker executable not found: {exePath}")
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId,
            };
        }

        var client = new SidecarClient.SidecarClient(new SidecarClientOptions
        {
            ExecutablePath = exePath,
            DefaultTimeoutMs = 0,
            WorkingDirectory = Path.GetDirectoryName(exePath),
            EnvironmentVariables = BuildEnvironmentVariables(),
        });

        var readyTcs = new TaskCompletionSource<ReadyEventData>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnEvent(IpcEvent evt)
        {
            if (evt.Event != WorkerEvents.Ready || evt.Data is null) return;
            try
            {
                var ready = evt.Data.Value.Deserialize<ReadyEventData>(JsonOptions);
                if (ready is not null) readyTcs.TrySetResult(ready);
            }
            catch (JsonException ex)
            {
                readyTcs.TrySetException(ex);
            }
        }

        void OnProcessExited(int? exitCode)
        {
            readyTcs.TrySetException(new TranslationException(
                $"Local AI worker exited before ready (code={exitCode})")
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId,
            });
        }

        void OnStderrLog(string line)
        {
            Trace.WriteLine($"[LongDocLocalAi] {line}");
        }

        client.OnEvent += OnEvent;
        client.OnProcessExited += OnProcessExited;
        client.OnStderrLog += OnStderrLog;

        try
        {
            client.Start();

            using var timeoutCts = new CancellationTokenSource(HandshakeTimeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            using (linkedCts.Token.Register(() => readyTcs.TrySetException(
                       new TranslationException($"Local AI worker did not emit ready within {HandshakeTimeoutMs}ms")
                       {
                           ErrorCode = TranslationErrorCode.ServiceUnavailable,
                           ServiceId = ServiceId,
                       })))
            {
                var ready = await readyTcs.Task.ConfigureAwait(false);
                if (!string.Equals(ready.WorkerKind, WorkerKinds.LocalAi, StringComparison.Ordinal))
                {
                    throw new TranslationException(
                        $"Expected LocalAi worker but worker reported '{ready.WorkerKind}'")
                    {
                        ErrorCode = TranslationErrorCode.ServiceUnavailable,
                        ServiceId = ServiceId,
                    };
                }

                if (ready.ProtocolVersion != WorkerProtocolVersion.Current)
                {
                    throw new TranslationException(
                        $"Local AI worker protocol mismatch: {ready.ProtocolVersion} != {WorkerProtocolVersion.Current}")
                    {
                        ErrorCode = TranslationErrorCode.ServiceUnavailable,
                        ServiceId = ServiceId,
                    };
                }
            }

            var configureResult = await client.SendRequestAsync<ConfigureResult>(
                WorkerMethods.Configure,
                new ConfigureParams { Settings = _snapshot },
                timeoutMs: ConfigureTimeoutMs,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (configureResult?.Ok != true)
            {
                throw new TranslationException("Local AI worker configure request did not return ok=true")
                {
                    ErrorCode = TranslationErrorCode.ServiceUnavailable,
                    ServiceId = ServiceId,
                };
            }

            client.OnEvent -= OnEvent;
            client.OnProcessExited -= OnProcessExited;
            return client;
        }
        catch
        {
            client.OnEvent -= OnEvent;
            client.OnProcessExited -= OnProcessExited;
            try { await client.DisposeAsync().ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private static string ResolveLocalAiWorkerPath()
    {
        var longDocDir = AppContext.BaseDirectory;
        var workersDir = Directory.GetParent(longDocDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName
                         ?? longDocDir;
        return Path.Combine(workersDir, WorkerSubdir, WorkerExeName);
    }

    private static Dictionary<string, string> BuildEnvironmentVariables()
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CopyEnvironmentVariable(variables, "DOTNET_ROOT");
        CopyEnvironmentVariable(variables, "DOTNET_ROOT_X64");
        CopyEnvironmentVariable(variables, "DOTNET_ROOT_ARM64");
        CopyEnvironmentVariable(variables, "EASYDICT_WORKER_SHARED_DIR");
        CopyEnvironmentVariable(variables, "EASYDICT_ENABLE_OPENVINO_EP");
        CopyEnvironmentVariable(variables, "EASYDICT_OPENVINO_RUNTIME_DIR");
        CopyEnvironmentVariable(variables, "PATH");
        variables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        return variables;
    }

    private static void CopyEnvironmentVariable(Dictionary<string, string> variables, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(value))
        {
            variables[name] = value;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
