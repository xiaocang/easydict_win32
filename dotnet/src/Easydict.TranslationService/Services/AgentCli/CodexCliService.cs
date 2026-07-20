using System.Runtime.CompilerServices;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services.AgentCli;

/// <summary>
/// Translation service backed by the locally installed OpenAI Codex CLI, ported
/// from the macOS Easydict CodexCLIService (tisfeng/Easydict PR #1168).
/// Lets ChatGPT subscription users translate without an API key by reusing the
/// CLI's existing credentials. Spawns `codex exec --json` per query in a
/// read-only sandbox with agent features disabled; the prompt is written to
/// stdin. Codex emits the assistant text as a single item.completed event, so
/// the stream yields one chunk. Disabled by default; the user must opt in via
/// Settings after a risk acknowledgment.
/// </summary>
public sealed class CodexCliService : BaseTranslationService, IStreamTranslationService
{
    public const string ServiceIdValue = "codex";
    public const string InstallDocumentationUrl = "https://developers.openai.com/codex/cli";

    /// <summary>Reasoning effort values accepted by `-c model_reasoning_effort=`.</summary>
    public static readonly string[] AvailableReasoningEfforts = ["minimal", "low", "medium", "high"];

    internal const string CliName = "codex";

    // Agent features disabled on every invocation to keep the CLI a pure
    // translator (mirrors the upstream macOS flag set).
    private static readonly string[] DisabledFeatures =
    [
        "shell_tool",
        "shell_snapshot",
        "browser_use",
        "browser_use_external",
        "in_app_browser",
        "computer_use",
        "image_generation",
        "apps",
        "plugins",
        "hooks",
        "multi_agent",
        "skill_mcp_dependency_install",
        "tool_call_mcp_elicitation",
        "tool_suggest",
        "workspace_dependencies",
    ];

    private readonly AgentCliProcessRunner _runner = new();
    private bool _enabled;
    private string _model = "";
    private string _reasoningEffort = "";

    public CodexCliService(HttpClient httpClient) : base(httpClient) { }

    public override string ServiceId => ServiceIdValue;
    public override string DisplayName => "Codex";
    public override bool RequiresApiKey => false;
    public override bool IsConfigured => _enabled;
    public override IReadOnlyList<Language> SupportedLanguages => BaseOpenAIService.OpenAILanguages;

    public bool IsStreaming => true;

    public string Model => _model;
    public string ReasoningEffort => _reasoningEffort;

    /// <summary>
    /// Configure from user settings. Empty model uses the CLI default; model
    /// names are whitelisted and the reasoning effort is restricted to known
    /// values because both are passed on the CLI command line.
    /// </summary>
    public void Configure(bool enabled, string? model = null, string? reasoningEffort = null)
    {
        _enabled = enabled;
        _model = AgentCliPromptBuilder.SanitizeModelName(model) ?? "";
        var effort = reasoningEffort?.Trim().ToLowerInvariant() ?? "";
        _reasoningEffort = AvailableReasoningEfforts.Contains(effort) ? effort : "";
    }

    protected override void ValidateRequest(TranslationRequest request)
    {
        if (!_enabled)
        {
            throw new TranslationException(
                "Codex is not enabled. Enable it in Settings (requires the Codex CLI installed and signed in).")
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
        string? agentMessage = null;
        string? errorMessage = null;

        var lines = _runner.RunLinesAsync(
            executable,
            BuildArguments(_model, _reasoningEffort),
            AgentCliPromptBuilder.BuildCombinedPrompt(request),
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
                throw CodexCliEventParser.ClassifyFailure(ServiceId, ex.ExitCode, controlLines, ex.StdErr);
            }
            catch (TimeoutException ex)
            {
                throw new TranslationException("Codex CLI timed out", ex)
                {
                    ErrorCode = TranslationErrorCode.Timeout,
                    ServiceId = ServiceId,
                };
            }

            controlLines.Add(line);
            // Keep the latest agent message; a turn normally produces exactly one.
            agentMessage = CodexCliEventParser.TryExtractAgentMessage(line) ?? agentMessage;
            errorMessage = CodexCliEventParser.TryExtractErrorMessage(line) ?? errorMessage;
        }

        if (agentMessage is null && errorMessage != null)
        {
            throw CodexCliEventParser.ClassifyFailure(ServiceId, exitCode: 0, controlLines, errorMessage);
        }

        if (!string.IsNullOrEmpty(agentMessage))
        {
            yield return agentMessage;
        }
    }

    /// <summary>
    /// CLI arguments mirroring the upstream macOS implementation: JSONL output,
    /// ephemeral session, read-only sandbox, neutral working directory, agent
    /// features disabled, optional model and reasoning effort, and a trailing
    /// `-` so the prompt is read from stdin.
    /// </summary>
    internal static List<string> BuildArguments(string model, string reasoningEffort)
    {
        var arguments = new List<string>
        {
            "exec",
            "--json",
            "--skip-git-repo-check",
            "--ephemeral",
            "--sandbox", "read-only",
            // The runner already sets the process working directory to the temp
            // folder; "." keeps user-profile paths (which may contain cmd.exe
            // metacharacters) off the command line.
            "-C", ".",
        };

        foreach (var feature in DisabledFeatures)
        {
            arguments.Add("--disable");
            arguments.Add(feature);
        }

        if (!string.IsNullOrEmpty(model))
        {
            arguments.Add("-m");
            arguments.Add(model);
        }

        if (!string.IsNullOrEmpty(reasoningEffort))
        {
            arguments.Add("-c");
            arguments.Add($"model_reasoning_effort={reasoningEffort}");
        }

        arguments.Add("--");
        arguments.Add("-");
        return arguments;
    }

    internal static IReadOnlyList<string> GetCandidatePaths()
    {
        var paths = new List<string>();

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
        {
            paths.Add(Path.Combine(userProfile, ".local", "bin", "codex.exe"));
            paths.Add(Path.Combine(userProfile, ".codex", "bin", "codex.exe"));
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            // npm global install shim.
            paths.Add(Path.Combine(appData, "npm", "codex.cmd"));
        }

        return paths;
    }

    private TranslationException NotInstalledError()
    {
        return new TranslationException(
            $"Codex CLI not found. Install it ({InstallDocumentationUrl}) and sign in, then try again.")
        {
            ErrorCode = TranslationErrorCode.ServiceUnavailable,
            ServiceId = ServiceId,
        };
    }
}
