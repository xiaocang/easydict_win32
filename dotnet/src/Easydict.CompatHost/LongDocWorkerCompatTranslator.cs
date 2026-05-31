using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Easydict.SidecarClient;
using Easydict.SidecarClient.Protocol;
using SidecarClientType = Easydict.SidecarClient.SidecarClient;

namespace Easydict.CompatHost;

public sealed class LongDocWorkerCompatTranslator : ICompatHostLongDocTranslator
{
    private const string WorkerSubdir = "longdoc";
    private const string WorkerExeName = "Easydict.Workers.LongDoc.exe";
    private const int HandshakeTimeoutMs = 10_000;
    private const int ConfigureTimeoutMs = 10_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Func<SidecarClientOptions> _optionsFactory;

    public LongDocWorkerCompatTranslator()
        : this(CreateDefaultOptions)
    {
    }

    internal LongDocWorkerCompatTranslator(Func<SidecarClientOptions> optionsFactory)
    {
        _optionsFactory = optionsFactory;
    }

    public async Task<TranslateDocumentResult> TranslateAsync(
        TranslateDocumentParams parameters,
        SettingsSnapshot settings,
        Action<IpcEvent> onEvent,
        CancellationToken cancellationToken = default)
    {
        await using var client = new SidecarClientType(_optionsFactory());

        await StartAndConfigureAsync(client, settings, cancellationToken).ConfigureAwait(false);

        void OnEvent(IpcEvent evt)
        {
            if (IsLongDocEvent(evt.Event))
            {
                onEvent(evt);
            }
        }

        void OnStderrLog(string line)
        {
            Trace.WriteLine($"[CompatHost:LongDocWorker] {line}");
        }

        var request = EnsureResultJsonPath(parameters, out var resultJsonPath, out var createdResultPath);

        client.OnEvent += OnEvent;
        client.OnStderrLog += OnStderrLog;

        try
        {
            var result = await client.SendRequestAsync<TranslateDocumentResult>(
                    LongDocMethods.TranslateDocument,
                    request,
                    timeoutMs: 0,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (result is null)
            {
                throw new CompatHostException(
                    IpcErrorCodes.ServiceError,
                    "Long-document worker returned null translate_document result");
            }

            return await HydrateResultAsync(result, cancellationToken).ConfigureAwait(false);
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

            if (createdResultPath)
            {
                TryDeleteResultFile(resultJsonPath);
            }
        }
    }

    internal static string ResolveLongDocWorkerPath(string baseDirectory)
    {
        return Path.Combine(baseDirectory, "workers", WorkerSubdir, WorkerExeName);
    }

    internal static Dictionary<string, string> BuildWorkerEnvironment(string baseDirectory)
    {
        var dotnetRoot = Path.Combine(baseDirectory, "dotnet");
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EASYDICT_WORKER_SHARED_DIR"] = Path.Combine(baseDirectory, "workers", "shared"),
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        };

        if (HasBundledDotnetRuntime(dotnetRoot))
        {
            variables["DOTNET_ROOT"] = dotnetRoot;
            variables["DOTNET_ROOT_X64"] = dotnetRoot;
            variables["DOTNET_ROOT_ARM64"] = dotnetRoot;
        }

        return variables;
    }

    internal static async Task<TranslateDocumentResult> HydrateResultAsync(
        TranslateDocumentResult result,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(result.ResultJsonPath))
        {
            return result;
        }

        if (!File.Exists(result.ResultJsonPath))
        {
            throw new CompatHostException(
                IpcErrorCodes.ServiceError,
                $"Long-document worker result file was not found: {result.ResultJsonPath}");
        }

        try
        {
            return await LongDocResultFileStore.ReadAsync(result.ResultJsonPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            throw new CompatHostException(
                IpcErrorCodes.ServiceError,
                $"Failed to read long-document worker result file: {ex.Message}");
        }
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
                    $"Long-document worker emitted malformed ready event: {ex.Message}"));
            }
        }

        void OnProcessExited(int? exitCode)
        {
            readyTcs.TrySetException(new CompatHostException(
                IpcErrorCodes.ServiceError,
                $"Long-document worker exited before ready/configure completed: code={exitCode}"));
        }

        void OnStderrLog(string line)
        {
            Trace.WriteLine($"[CompatHost:LongDocWorker] {line}");
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
                    $"Failed to start long-document worker: {ex.Message}");
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
                    $"Long-document worker did not emit ready within {HandshakeTimeoutMs}ms");
            }

            if (ready.WorkerKind != WorkerKinds.LongDoc)
            {
                throw new CompatHostException(
                    IpcErrorCodes.ServiceError,
                    $"Expected long-document worker kind '{WorkerKinds.LongDoc}' but worker reported '{ready.WorkerKind}'");
            }

            if (ready.ProtocolVersion != WorkerProtocolVersion.Current)
            {
                throw new CompatHostException(
                    WorkerErrorCodes.VersionMismatch,
                    $"Long-document worker reports protocol version {ready.ProtocolVersion}; host expects {WorkerProtocolVersion.Current}");
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
                    "Long-document worker configure request did not return ok=true");
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
            ExecutablePath = ResolveLongDocWorkerPath(baseDirectory),
            DefaultTimeoutMs = 0,
            EnvironmentVariables = BuildWorkerEnvironment(baseDirectory),
        };
    }

    private static TranslateDocumentParams EnsureResultJsonPath(
        TranslateDocumentParams parameters,
        out string resultJsonPath,
        out bool createdResultPath)
    {
        if (!string.IsNullOrWhiteSpace(parameters.ResultJsonPath))
        {
            resultJsonPath = parameters.ResultJsonPath;
            createdResultPath = false;
            return parameters;
        }

        resultJsonPath = LongDocResultFileStore.CreateTempPath();
        createdResultPath = true;
        return new TranslateDocumentParams
        {
            InputPath = parameters.InputPath,
            OutputPath = parameters.OutputPath,
            InputMode = parameters.InputMode,
            From = parameters.From,
            To = parameters.To,
            ServiceId = parameters.ServiceId,
            OutputMode = parameters.OutputMode,
            PdfExportMode = parameters.PdfExportMode,
            LayoutDetection = parameters.LayoutDetection,
            PageRange = parameters.PageRange,
            VisionEndpoint = parameters.VisionEndpoint,
            VisionApiKey = parameters.VisionApiKey,
            VisionModel = parameters.VisionModel,
            ResultJsonPath = resultJsonPath,
        };
    }

    private static bool IsLongDocEvent(string eventName)
    {
        return eventName is LongDocEvents.Status
            or LongDocEvents.Progress
            or LongDocEvents.BlockTranslated;
    }

    private static void TryDeleteResultFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temp result files are best-effort cleanup only.
        }
    }

    private static bool HasBundledDotnetRuntime(string dotnetRoot)
    {
        return Directory.Exists(Path.Combine(dotnetRoot, "host", "fxr"))
            && Directory.Exists(Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App"));
    }
}
