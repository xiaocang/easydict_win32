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
    public const string DefaultModel = "sonnet";
    public const string InstallDocumentationUrl = "https://code.claude.com/docs/en/quickstart";

    /// <summary>Common model aliases accepted by the CLI.</summary>
    public static readonly string[] AvailableModels = ["sonnet", "opus", "haiku"];

    internal const string CliName = "claude";

    private readonly AgentCliProcessRunner _runner = new();
    private bool _enabled;
    private string _model = DefaultModel;

    public ClaudeCodeService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => ServiceIdValue;
    public override string DisplayName => "Claude Code";
    public override bool RequiresApiKey => false;
    public override bool IsConfigured => _enabled;
    public override IReadOnlyList<Language> SupportedLanguages => BaseOpenAIService.OpenAILanguages;

    public bool IsStreaming => true;

    public string Model => _model;

    /// <summary>
    /// Configure from user settings. An invalid or empty model falls back to
    /// <see cref="DefaultModel"/>; model names are whitelisted because they are
    /// passed on the CLI command line.
    /// </summary>
    public void Configure(bool enabled, string? model = null)
    {
        _enabled = enabled;
        _model = AgentCliPromptBuilder.SanitizeModelName(model) ?? DefaultModel;
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

        var executable = await AgentCliExecutableLocator
            .LocateAsync(CliName, GetCandidatePaths(), cancellationToken)
            .ConfigureAwait(false)
            ?? throw NotInstalledError();

        var controlLines = new List<string>();
        var deltaSeen = false;
        ClaudeCodeEventParser.ResultInfo? result = null;

        var lines = _runner.RunLinesAsync(
            executable,
            BuildArguments(_model),
            AgentCliPromptBuilder.BuildUserPrompt(request),
            timeout: null,
            cancellationToken);

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
                deltaSeen = true;
                yield return delta;
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
        if (!deltaSeen && result?.ResultText is { Length: > 0 } fullText)
        {
            yield return fullText;
        }
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
