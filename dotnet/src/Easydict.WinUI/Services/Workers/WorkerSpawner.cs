using System.Diagnostics;
using System.Text.Json;
using Easydict.SidecarClient;
using Easydict.SidecarClient.Protocol;

namespace Easydict.WinUI.Services.Workers;

/// <summary>
/// Spawns a worker process (long-doc or local-AI), performs the handshake,
/// validates the protocol version, and ships the SettingsSnapshot via the
/// "configure" request. Returns a SidecarClient ready for typed method calls.
/// </summary>
internal sealed class WorkerSpawner
{
    private const int HandshakeTimeoutMs = 10_000;
    private const int ConfigureTimeoutMs = 10_000;

    /// <summary>
    /// Start the worker exe at the resolved path, await its "ready" event,
    /// and send "configure" with the snapshot. Caller owns disposal of the
    /// returned client.
    /// </summary>
    public async Task<SidecarClient.SidecarClient> StartAndConfigureAsync(
        string workerKind,
        string workerSubdir,
        string workerExeName,
        SettingsSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var exePath = ResolveWorkerExePath(workerSubdir, workerExeName);
        if (!File.Exists(exePath))
        {
            throw new WorkerStartFailedException(
                $"Worker executable not found: {exePath}. " +
                "Check that the build pipeline published it into the host package.");
        }

        var options = new SidecarClientOptions
        {
            ExecutablePath = exePath,
            // 0 = no default timeout for long-running ops; per-call timeouts are passed
            // explicitly. Workers themselves report ready inside HandshakeTimeoutMs.
            DefaultTimeoutMs = 0,
            // Workers are published framework-dependent to share a single .NET 8 runtime
            // copy across both workers (instead of bundling the runtime per-worker).
            // The runtime lives at <install>/dotnet, populated at publish time by
            // scripts/Extract-DotnetRuntime.ps1. Point the apphost at it explicitly via
            // every DOTNET_ROOT variant the .NET host loader consults — DOTNET_ROOT for
            // the legacy path, and DOTNET_ROOT_<ARCH> for per-arch hosts on systems where
            // a different arch's runtime is also installed.
            EnvironmentVariables = BuildEnvironmentVariables(workerSubdir),
        };

        var client = new SidecarClient.SidecarClient(options);

        ReadyEventData? ready = null;
        var readyTcs = new TaskCompletionSource<ReadyEventData>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnEvent(IpcEvent evt)
        {
            if (evt.Event != WorkerEvents.Ready || evt.Data is null) return;
            try
            {
                var data = evt.Data.Value.Deserialize<ReadyEventData>(JsonOptions);
                if (data is not null) readyTcs.TrySetResult(data);
            }
            catch (JsonException ex)
            {
                readyTcs.TrySetException(new WorkerStartFailedException(
                    $"Worker emitted malformed ready event: {ex.Message}"));
            }
        }

        void OnProcessExited(int? exitCode)
        {
            readyTcs.TrySetException(new WorkerStartFailedException(
                $"Worker process exited (code={exitCode}) before handshake completed"));
        }

        void OnStderrLog(string line)
        {
            Debug.WriteLine($"[WorkerSpawner:{workerKind}] {line}");
        }

        client.OnEvent += OnEvent;
        client.OnProcessExited += OnProcessExited;
        client.OnStderrLog += OnStderrLog;

        try
        {
            client.Start();

            // Wait for ready or handshake timeout.
            using var timeoutCts = new CancellationTokenSource(HandshakeTimeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            using (linkedCts.Token.Register(() => readyTcs.TrySetException(
                new WorkerStartFailedException($"Worker {workerKind} did not emit ready within {HandshakeTimeoutMs}ms"))))
            {
                ready = await readyTcs.Task.ConfigureAwait(false);
            }

            if (ready.WorkerKind != workerKind)
            {
                throw new WorkerStartFailedException(
                    $"Expected worker kind '{workerKind}' but worker reported '{ready.WorkerKind}'");
            }

            if (ready.ProtocolVersion != WorkerProtocolVersion.Current)
            {
                throw new WorkerVersionMismatchException(
                    $"Worker {workerKind} reports protocol version {ready.ProtocolVersion}; " +
                    $"host expects {WorkerProtocolVersion.Current}");
            }

            // Send configure request.
            var configureResult = await client.SendRequestAsync<ConfigureResult>(
                WorkerMethods.Configure,
                new ConfigureParams { Settings = snapshot },
                timeoutMs: ConfigureTimeoutMs,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (configureResult?.Ok != true)
            {
                throw new WorkerStartFailedException(
                    $"Worker {workerKind} configure request did not return ok=true");
            }

            // Unhook the ready-only event handler; consumers will subscribe to OnEvent themselves.
            client.OnEvent -= OnEvent;
            return client;
        }
        catch
        {
            // Best-effort cleanup on failure path.
            client.OnEvent -= OnEvent;
            client.OnProcessExited -= OnProcessExited;
            try { await client.DisposeAsync(); } catch { /* swallow */ }
            throw;
        }
    }

    /// <summary>
    /// Resolve the worker exe path within the app install directory. Layout:
    ///   {AppContext.BaseDirectory}/workers/{workerSubdir}/{workerExeName}
    /// </summary>
    public static string ResolveWorkerExePath(string workerSubdir, string workerExeName)
    {
        return Path.Combine(AppContext.BaseDirectory, "workers", workerSubdir, workerExeName);
    }

    /// <summary>
    /// Build the DOTNET_ROOT environment block for the worker. Points every variant
    /// at the bundled runtime at &lt;install&gt;/dotnet so the framework-dependent
    /// worker apphost finds it regardless of which probe order the local .NET host
    /// loader uses.
    /// </summary>
    private static Dictionary<string, string> BuildEnvironmentVariables(string workerSubdir)
    {
        var dotnetRoot = Path.Combine(AppContext.BaseDirectory, "dotnet");
        var sharedDir = Path.Combine(AppContext.BaseDirectory, "workers", "shared");
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_ROOT"] = dotnetRoot,
            ["DOTNET_ROOT_X64"] = dotnetRoot,
            ["DOTNET_ROOT_ARM64"] = dotnetRoot,
            ["EASYDICT_WORKER_SHARED_DIR"] = sharedDir,
            // Suppress global telemetry from the worker apphost (the host itself
            // already opts out via its csproj). Worker startup cost is on the
            // critical path of every translate request — skip the network sniff.
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        };

        if (string.Equals(workerSubdir, "localai", StringComparison.OrdinalIgnoreCase))
        {
            var openVinoRuntimeDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Easydict",
                "runtimes",
                "openvino",
                "1.21.0",
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

    /// <summary>
    /// Build a SettingsSnapshot from the current SettingsService values. Reads
    /// decrypted secrets here (DPAPI/AES happens in-process before crossing
    /// the pipe).
    /// </summary>
    public static SettingsSnapshot BuildSnapshot(SettingsService settings)
    {
        return new SettingsSnapshot
        {
            // Cloud LLM provider credentials — pass everything configured; the worker
            // only uses the keys whose service the host selects via translate_document.
            OpenAIApiKey = settings.OpenAIApiKey,
            OpenAIEndpoint = settings.OpenAIEndpoint,
            OpenAIModel = settings.OpenAIModel,
            OpenAITemperature = (float)settings.OpenAITemperature,
            OpenAIApiFormatOverride = settings.OpenAIApiFormatOverride,
            DeepLApiKey = settings.DeepLApiKey,
            DeepLUseFreeApi = settings.DeepLUseFreeApi,
            DeepLUseQualityOptimized = settings.DeepLUseQualityOptimized,
            DeepSeekApiKey = settings.DeepSeekApiKey,
            DeepSeekModel = settings.DeepSeekModel,
            GeminiApiKey = settings.GeminiApiKey,
            GeminiModel = settings.GeminiModel,
            GroqApiKey = settings.GroqApiKey,
            GroqModel = settings.GroqModel,
            ZhipuApiKey = settings.ZhipuApiKey,
            ZhipuModel = settings.ZhipuModel,
            DoubaoApiKey = settings.DoubaoApiKey,
            DoubaoModel = settings.DoubaoModel,
            // GitHubModelsApiKey: SettingsService does not currently expose a key
            // property for github-models (the service uses a different auth flow).
            // When that service is plumbed through the worker, add the corresponding
            // SettingsService property and uncomment.
            GitHubModelsModel = settings.GitHubModelsModel,
            // CaiyunToken: not currently in SettingsService — leave null and surface
            // when caiyun is invoked via the worker.
            NiuTransApiKey = settings.NiuTransApiKey,
            CustomOpenAIApiKey = settings.CustomOpenAIApiKey,
            CustomOpenAIEndpoint = settings.CustomOpenAIEndpoint,
            CustomOpenAIModel = settings.CustomOpenAIModel,
            OllamaEndpoint = settings.OllamaEndpoint,
            OllamaModel = settings.OllamaModel,
            BuiltInAIModel = settings.BuiltInAIModel,
            BuiltInAIApiKey = settings.BuiltInAIApiKey,
            DeviceId = settings.DeviceId,
            DeviceToken = settings.DeviceToken,

            // Local AI
            FoundryLocalEndpoint = settings.FoundryLocalEndpoint,
            FoundryLocalModel = settings.FoundryLocalModel,
            OpenVinoDevice = settings.OpenVinoDevice,
            LocalAIProvider = settings.LocalAIProvider,

            // Network
            ProxyEnabled = settings.ProxyEnabled,
            ProxyUri = settings.ProxyUri,
            ProxyBypassLocal = settings.ProxyBypassLocal,

            // Long-doc specifics
            LongDocMaxConcurrency = settings.LongDocMaxConcurrency,
            LongDocEnableDocumentContextPass = settings.LongDocEnableDocumentContextPass,
            EnableTatrTableStructure = settings.EnableTatrTableStructure,
            FormulaFontPattern = settings.FormulaFontPattern,
            FormulaCharPattern = settings.FormulaCharPattern,
            LongDocCustomPrompt = settings.LongDocCustomPrompt,
            LayoutDetectionMode = settings.LayoutDetectionMode,
            EnableInternationalServices = settings.EnableInternationalServices,
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

/// <summary>Worker failed to emit ready / configure within the handshake window.</summary>
internal sealed class WorkerStartFailedException : Exception
{
    public WorkerStartFailedException(string message) : base(message) { }
}

/// <summary>Worker's reported protocol version doesn't match the host's.</summary>
internal sealed class WorkerVersionMismatchException : Exception
{
    public WorkerVersionMismatchException(string message) : base(message) { }
}
