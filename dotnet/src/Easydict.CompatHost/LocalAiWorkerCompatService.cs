using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Easydict.SidecarClient;
using Easydict.SidecarClient.Protocol;
using SidecarClientType = Easydict.SidecarClient.SidecarClient;

namespace Easydict.CompatHost;

public sealed class LocalAiWorkerCompatService : ICompatHostLocalAiService
{
    private const string WorkerSubdir = "localai";
    private const string WorkerExeName = "Easydict.Workers.LocalAi.exe";
    private const string EnableOpenVinoEpEnvironmentVariable = "EASYDICT_ENABLE_OPENVINO_EP";
    private const string OpenVinoPackageVersion = "1.21.0";
    private const int HandshakeTimeoutMs = 10_000;
    private const int ConfigureTimeoutMs = 10_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Func<SidecarClientOptions> _optionsFactory;

    public LocalAiWorkerCompatService()
        : this(CreateDefaultOptions)
    {
    }

    internal LocalAiWorkerCompatService(Func<SidecarClientOptions> optionsFactory)
    {
        _optionsFactory = optionsFactory;
    }

    public async Task<LocalModelStatusDto> PrepareModelAsync(
        PrepareModelParams parameters,
        SettingsSnapshot settings,
        Action<IpcEvent> onEvent,
        CancellationToken cancellationToken = default)
    {
        await using var client = new SidecarClientType(_optionsFactory());

        await StartAndConfigureAsync(client, settings, cancellationToken).ConfigureAwait(false);

        void OnEvent(IpcEvent evt)
        {
            if (evt.Event == LocalAiEvents.DownloadProgress)
            {
                onEvent(evt);
            }
        }

        void OnStderrLog(string line)
        {
            Trace.WriteLine($"[CompatHost:LocalAiWorker] {line}");
        }

        client.OnEvent += OnEvent;
        client.OnStderrLog += OnStderrLog;

        try
        {
            var result = await client.SendRequestAsync<LocalModelStatusDto>(
                    LocalAiMethods.PrepareModel,
                    parameters,
                    timeoutMs: 0,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return result ?? throw new CompatHostException(
                IpcErrorCodes.ServiceError,
                "Local AI worker returned null prepare_model result");
        }
        catch (SidecarErrorException ex)
        {
            throw new CompatHostException(ex.Error.Code, ex.Error.Message, ex.Error.Details);
        }
        catch (SidecarException ex)
        {
            throw new CompatHostException(IpcErrorCodes.ServiceError, ex.Message);
        }
        finally
        {
            client.OnEvent -= OnEvent;
            client.OnStderrLog -= OnStderrLog;
        }
    }

    public async Task<LocalAiTranslateResult> TranslateAsync(
        LocalAiTranslateParams parameters,
        SettingsSnapshot settings,
        CancellationToken cancellationToken = default)
    {
        await using var client = new SidecarClientType(_optionsFactory());

        await StartAndConfigureAsync(client, settings, cancellationToken).ConfigureAwait(false);

        void OnStderrLog(string line)
        {
            Trace.WriteLine($"[CompatHost:LocalAiWorker] {line}");
        }

        client.OnStderrLog += OnStderrLog;

        try
        {
            var result = await client.SendRequestAsync<LocalAiTranslateResult>(
                    LocalAiMethods.Translate,
                    parameters,
                    timeoutMs: 0,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return result ?? throw new CompatHostException(
                IpcErrorCodes.ServiceError,
                "Local AI worker returned null translate result");
        }
        catch (SidecarErrorException ex)
        {
            throw new CompatHostException(ex.Error.Code, ex.Error.Message, ex.Error.Details);
        }
        catch (SidecarException ex)
        {
            throw new CompatHostException(IpcErrorCodes.ServiceError, ex.Message);
        }
        finally
        {
            client.OnStderrLog -= OnStderrLog;
        }
    }

    internal static string ResolveLocalAiWorkerPath(string baseDirectory)
    {
        return Path.Combine(baseDirectory, "workers", WorkerSubdir, WorkerExeName);
    }

    internal static Dictionary<string, string> BuildWorkerEnvironment(string baseDirectory)
    {
        var dotnetRoot = Path.Combine(baseDirectory, "dotnet");
        var sharedDir = Path.Combine(baseDirectory, "workers", "shared");
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EASYDICT_WORKER_SHARED_DIR"] = sharedDir,
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            [EnableOpenVinoEpEnvironmentVariable] =
                Environment.GetEnvironmentVariable(EnableOpenVinoEpEnvironmentVariable) ?? string.Empty,
        };

        if (HasBundledDotnetRuntime(dotnetRoot))
        {
            variables["DOTNET_ROOT"] = dotnetRoot;
            variables["DOTNET_ROOT_X64"] = dotnetRoot;
            variables["DOTNET_ROOT_ARM64"] = dotnetRoot;
        }

        if (IsOpenVinoEpPathInjectionEnabled())
        {
            var openVinoRuntimeDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Easydict",
                "runtimes",
                "openvino",
                OpenVinoPackageVersion,
                "win-x64",
                "native");
            variables["EASYDICT_OPENVINO_RUNTIME_DIR"] = openVinoRuntimeDir;

            var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            variables["PATH"] = string.IsNullOrWhiteSpace(existingPath)
                ? openVinoRuntimeDir
                : openVinoRuntimeDir + Path.PathSeparator + existingPath;
        }

        return variables;
    }

    private static async Task StartAndConfigureAsync(
        SidecarClientType client,
        SettingsSnapshot settings,
        CancellationToken cancellationToken)
    {
        var readyTcs = new TaskCompletionSource<ReadyEventData>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnEvent(IpcEvent evt)
        {
            if (evt.Event != WorkerEvents.Ready || evt.Data is null) return;

            try
            {
                var ready = evt.Data.Value.Deserialize<ReadyEventData>(JsonOptions);
                if (ready is not null)
                {
                    readyTcs.TrySetResult(ready);
                }
            }
            catch (JsonException ex)
            {
                readyTcs.TrySetException(new CompatHostException(
                    IpcErrorCodes.ServiceError,
                    $"Local AI worker emitted malformed ready event: {ex.Message}"));
            }
        }

        void OnProcessExited(int? exitCode)
        {
            readyTcs.TrySetException(new CompatHostException(
                IpcErrorCodes.ServiceError,
                $"Local AI worker exited before ready/configure completed: code={exitCode}"));
        }

        void OnStderrLog(string line)
        {
            Trace.WriteLine($"[CompatHost:LocalAiWorker] {line}");
        }

        client.OnEvent += OnEvent;
        client.OnProcessExited += OnProcessExited;
        client.OnStderrLog += OnStderrLog;

        try
        {
            try
            {
                client.Start();
            }
            catch (Win32Exception ex)
            {
                throw new CompatHostException(
                    IpcErrorCodes.ServiceError,
                    $"Failed to start Local AI worker: {ex.Message}");
            }

            ReadyEventData ready;
            try
            {
                ready = await readyTcs.Task
                    .WaitAsync(TimeSpan.FromMilliseconds(HandshakeTimeoutMs), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new CompatHostException(
                    IpcErrorCodes.ServiceError,
                    $"Local AI worker did not emit ready within {HandshakeTimeoutMs}ms");
            }

            if (ready.WorkerKind != WorkerKinds.LocalAi)
            {
                throw new CompatHostException(
                    IpcErrorCodes.ServiceError,
                    $"Expected Local AI worker kind '{WorkerKinds.LocalAi}' but worker reported '{ready.WorkerKind}'");
            }

            if (ready.ProtocolVersion != WorkerProtocolVersion.Current)
            {
                throw new CompatHostException(
                    WorkerErrorCodes.VersionMismatch,
                    $"Local AI worker reports protocol version {ready.ProtocolVersion}; host expects {WorkerProtocolVersion.Current}");
            }

            var configureResult = await client.SendRequestAsync<ConfigureResult>(
                    WorkerMethods.Configure,
                    new ConfigureParams { Settings = settings },
                    timeoutMs: ConfigureTimeoutMs,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (configureResult?.Ok != true)
            {
                throw new CompatHostException(
                    IpcErrorCodes.ServiceError,
                    "Local AI worker configure request did not return ok=true");
            }
        }
        finally
        {
            client.OnEvent -= OnEvent;
            client.OnProcessExited -= OnProcessExited;
            client.OnStderrLog -= OnStderrLog;
        }
    }

    private static SidecarClientOptions CreateDefaultOptions()
    {
        var baseDirectory = AppContext.BaseDirectory;
        return new SidecarClientOptions
        {
            ExecutablePath = ResolveLocalAiWorkerPath(baseDirectory),
            DefaultTimeoutMs = 0,
            EnvironmentVariables = BuildWorkerEnvironment(baseDirectory),
        };
    }

    private static bool IsOpenVinoEpPathInjectionEnabled()
    {
        var value = Environment.GetEnvironmentVariable(EnableOpenVinoEpEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasBundledDotnetRuntime(string dotnetRoot)
    {
        return Directory.Exists(Path.Combine(dotnetRoot, "host", "fxr"))
            && Directory.Exists(Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App"));
    }
}
