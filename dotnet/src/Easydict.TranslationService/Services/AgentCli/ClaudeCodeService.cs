using System.Runtime.CompilerServices;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services.AgentCli;

/// <summary>
/// Translation service backed by the locally installed Claude Code CLI, ported
/// from the macOS Easydict ClaudeCodeService (tisfeng/Easydict PR #1145).
/// Lets Claude subscription users translate without an API key by reusing the
/// CLI's existing credentials. Spawns `claude -p` per query and streams
/// stream-json text deltas. Disabled by default; the user must opt in via
/// Settings after a risk acknowledgment.
/// </summary>
public sealed class ClaudeCodeService : BaseTranslationService, IStreamTranslationService
{
    public const string ServiceIdValue = "claude-code";
    public const string DefaultModel = "haiku";
    public const string InstallDocumentationUrl = "https://code.claude.com/docs/en/quickstart";

    /// <summary>Fallback aliases used when the CLI exposes no model catalog.</summary>
    public static readonly string[] AvailableModels = ["sonnet", "haiku", "opus"];

    internal const string CliName = "claude";
    private static readonly IReadOnlyDictionary<string, string?> ThinkingDisabledEnvironment =
        new Dictionary<string, string?>
        {
            ["MAX_THINKING_TOKENS"] = "0",
        };

    private readonly AgentCliProcessRunner _runner = new();
    private bool _enabled;
    private string _model = DefaultModel;
    private string _executablePath = "";

    public ClaudeCodeService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => ServiceIdValue;
    public override string DisplayName => "Claude Code";
    public override bool RequiresApiKey => false;
    public override bool IsConfigured => _enabled;
    public override IReadOnlyList<Language> SupportedLanguages => BaseOpenAIService.OpenAILanguages;

    public bool IsStreaming => true;

    public string Model => _model;
    public string ExecutablePath => _executablePath;

    /// <summary>
    /// Configure from user settings. An invalid or empty model falls back to
    /// <see cref="DefaultModel"/>. A non-empty executable path is authoritative
    /// and bypasses PATH discovery.
    /// </summary>
    public void Configure(bool enabled, string? model = null, string? executablePath = null)
    {
        _enabled = enabled;
        _model = AgentCliPromptBuilder.SanitizeModelName(model) ?? DefaultModel;

        var normalizedPath = executablePath?.Trim() ?? "";
        if (!string.Equals(_executablePath, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            _executablePath = normalizedPath;
            AgentCliExecutableLocator.InvalidateCache(CliName);
        }
    }

    protected override void ValidateRequest(TranslationRequest request)
    {
        if (!_enabled)
        {
            throw new TranslationException(
                "Claude Code is not enabled. Enable it in Settings (requires the Claude Code CLI installed and signed in).")
            {
                ErrorCode = TranslationErrorCode.InvalidApiKey,
                ServiceId = ServiceId,
            };
        }

        base.ValidateRequest(request);
    }

    protected override async Task<TranslationResult> TranslateInternalAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        var translatedText = CleanupResult(
            await ConsumeStreamAsync(TranslateStreamAsync(request, cancellationToken), cancellationToken));

        return new TranslationResult
        {
            TranslatedText = translatedText,
            OriginalText = request.Text,
            DetectedLanguage = request.FromLanguage,
            TargetLanguage = request.ToLanguage,
            ServiceName = DisplayName,
        };
    }

    public async IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        var executable = await ResolveExecutableAsync(cancellationToken).ConfigureAwait(false);

        var controlLines = new List<string>();
        var textSeen = false;
        ClaudeCodeEventParser.ResultInfo? result = null;

        var lines = _runner.RunLinesAsync(
            executable,
            BuildArguments(_model),
            AgentCliPromptBuilder.BuildUserPrompt(request),
            timeout: null,
            cancellationToken,
            environment: ThinkingDisabledEnvironment);

        await using var enumerator = lines.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            string line;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    break;
                line = enumerator.Current;
            }
            catch (AgentCliProcessException ex)
            {
                throw ClaudeCodeEventParser.ClassifyFailure(ServiceId, ex.ExitCode, controlLines, ex.StdErr);
            }
            catch (TimeoutException ex)
            {
                throw new TranslationException("Claude Code CLI timed out", ex)
                {
                    ErrorCode = TranslationErrorCode.Timeout,
                    ServiceId = ServiceId,
                };
            }

            if (ClaudeCodeEventParser.TryExtractTextDelta(line, out var delta))
            {
                if (!string.IsNullOrEmpty(delta))
                {
                    textSeen = true;
                    yield return delta;
                }
            }
            else
            {
                controlLines.Add(line);
                result = ClaudeCodeEventParser.TryParseResult(line) ?? result;
            }
        }

        if (result is { IsError: true })
        {
            throw ClaudeCodeEventParser.ClassifyFailure(
                ServiceId, exitCode: 0, controlLines, result.ResultText ?? "");
        }

        // Older CLIs without --include-partial-messages emit no deltas;
        // fall back to the full text from the final result event.
        if (!textSeen && !string.IsNullOrWhiteSpace(result?.ResultText))
        {
            yield return result.ResultText;
            yield break;
        }

        if (!textSeen)
        {
            throw InvalidResponseError();
        }
    }

    /// <summary>
    /// Tries the CLI's non-inference metadata output. Current Claude versions
    /// expose aliases in --help; an empty result keeps the fallback list.
    /// </summary>
    public async Task<IReadOnlyList<string>> DiscoverModelsAsync(
        CancellationToken cancellationToken = default)
    {
        var executable = await ResolveExecutableAsync(cancellationToken).ConfigureAwait(false);
        var discovered = await AgentCliModelCatalog
            .DiscoverClaudeModelsAsync(executable, cancellationToken)
            .ConfigureAwait(false);
        return discovered.Count > 0 ? discovered : AvailableModels;
    }

    /// <summary>
    /// CLI arguments mirroring the upstream macOS implementation: stream-json
    /// output with partial messages, and token-reduction flags that disable
    /// tools, MCP servers, plugins, and session persistence. The prompt itself
    /// is written to stdin, so `-p` carries no inline prompt argument.
    /// </summary>
    internal static List<string> BuildArguments(string model)
    {
        var arguments = new List<string>
        {
            "-p",
            "--verbose",
            "--output-format", "stream-json",
            "--include-partial-messages",
            "--no-session-persistence",
            "--tools", "",
            "--strict-mcp-config",
            "--setting-sources", "",
            "--system-prompt", BaseOpenAIService.TranslationSystemPrompt,
        };

        if (!string.IsNullOrEmpty(model))
        {
            arguments.Add("--model");
            arguments.Add(model);
        }

        return arguments;
    }

    internal static IReadOnlyList<string> GetCandidatePaths()
    {
        var paths = new List<string>();

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
        {
            // Native installer location.
            paths.Add(Path.Combine(userProfile, ".local", "bin", "claude.exe"));
            paths.Add(Path.Combine(userProfile, ".claude", "local", "claude.exe"));
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            // npm global install shim.
            paths.Add(Path.Combine(appData, "npm", "claude.cmd"));
        }

        return paths;
    }

    private async Task<string> ResolveExecutableAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_executablePath))
        {
            if (!Path.IsPathFullyQualified(_executablePath) || !File.Exists(_executablePath))
            {
                throw new TranslationException(
                    $"Configured Claude Code executable was not found: {_executablePath}")
                {
                    ErrorCode = TranslationErrorCode.ServiceUnavailable,
                    ServiceId = ServiceId,
                };
            }

            return Path.GetFullPath(_executablePath);
        }

        return await AgentCliExecutableLocator
            .LocateAsync(CliName, GetCandidatePaths(), cancellationToken)
            .ConfigureAwait(false)
            ?? throw NotInstalledError();
    }

    private TranslationException InvalidResponseError()
    {
        return new TranslationException(
            "Claude Code CLI completed without returning a translation. Update the CLI and try again.")
        {
            ErrorCode = TranslationErrorCode.InvalidResponse,
            ServiceId = ServiceId,
        };
    }

    private TranslationException NotInstalledError()
    {
        return new TranslationException(
            $"Claude Code CLI not found. Install it ({InstallDocumentationUrl}) and sign in, then try again.")
        {
            ErrorCode = TranslationErrorCode.ServiceUnavailable,
            ServiceId = ServiceId,
        };
    }
}
