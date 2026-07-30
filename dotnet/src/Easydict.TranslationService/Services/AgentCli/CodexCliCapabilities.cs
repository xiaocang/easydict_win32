using System.Collections.Concurrent;

namespace Easydict.TranslationService.Services.AgentCli;

/// <summary>
/// Capabilities required to invoke Codex as an isolated, tool-free translator.
/// </summary>
internal sealed record CodexCliCapabilities(
    IReadOnlySet<string> Features,
    bool SupportsStrictConfig)
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<CodexCliCapabilities>>> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly string[] RequiredExecOptions =
    [
        "--json",
        "--skip-git-repo-check",
        "--ephemeral",
        "--ignore-user-config",
        "--ignore-rules",
        "--sandbox",
        "--cd",
        "--config",
        "--disable",
    ];

    public static async Task<CodexCliCapabilities> GetAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{executablePath}|{File.GetLastWriteTimeUtc(executablePath).Ticks}";
        var lazy = _cache.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<CodexCliCapabilities>>(
                () => ProbeAsync(executablePath),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _cache.TryRemove(cacheKey, out _);
            throw;
        }
    }

    internal static CodexCliCapabilities Parse(string execHelp, string featureList)
    {
        var features = ParseFeatureNames(featureList);
        if (RequiredExecOptions.Any(
                option => !execHelp.Contains(option, StringComparison.Ordinal))
            || !features.Contains("shell_tool"))
        {
            throw CreateUpdateRequiredException();
        }

        return new CodexCliCapabilities(
            features,
            SupportsStrictConfig: execHelp.Contains("--strict-config", StringComparison.Ordinal));
    }

    internal static IReadOnlySet<string> ParseFeatureNames(string output)
    {
        var features = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var name = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(name))
            {
                features.Add(name);
            }
        }

        return features;
    }

    internal static TranslationException CreateUpdateRequiredException(Exception? inner = null)
    {
        const string Message =
            "Codex CLI must be updated before it can run safely. Install the latest Codex CLI and try again.";
        return inner is null
            ? new TranslationException(Message)
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = CodexCliService.ServiceIdValue,
                RecoveryAction = "install-latest-codex",
                DocumentationUrl = CodexCliService.InstallDocumentationUrl,
            }
            : new TranslationException(Message, inner)
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = CodexCliService.ServiceIdValue,
                RecoveryAction = "install-latest-codex",
                DocumentationUrl = CodexCliService.InstallDocumentationUrl,
            };
    }

    private static async Task<CodexCliCapabilities> ProbeAsync(string executablePath)
    {
        var runner = new AgentCliProcessRunner();
        try
        {
            var execHelp = await runner.RunToEndAsync(
                executablePath,
                ["exec", "--help"],
                "",
                ProbeTimeout,
                CancellationToken.None).ConfigureAwait(false);
            var featureList = await runner.RunToEndAsync(
                executablePath,
                ["-c", "model_reasoning_effort=" + CodexCliService.DefaultReasoningEffort, "features", "list"],
                "",
                ProbeTimeout,
                CancellationToken.None).ConfigureAwait(false);
            return Parse(execHelp, featureList);
        }
        catch (AgentCliProcessException ex)
        {
            throw CreateUpdateRequiredException(ex);
        }
        catch (TimeoutException ex)
        {
            throw CreateUpdateRequiredException(ex);
        }
    }
}
