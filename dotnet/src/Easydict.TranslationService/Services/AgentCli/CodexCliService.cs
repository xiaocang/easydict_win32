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
public sealed class CodexCliService : BaseTranslationService, IStreamTranslationService, IGrammarCorrectionService
{
    public const string ServiceIdValue = "codex";
    public const string InstallDocumentationUrl = "https://developers.openai.com/codex/cli";
    public const string DefaultModel = "gpt-5.6-luna";
    public const string DefaultReasoningEffort = "none";

    /// <summary>Fallback aliases used when model discovery is unavailable.</summary>
    public static readonly string[] AvailableModels = ["gpt-5.6-luna", "gpt-5.6-terra", "gpt-5.6-sol"];

    /// <summary>Reasoning effort values accepted by `-c model_reasoning_effort=`.</summary>
    public static readonly string[] AvailableReasoningEfforts = ["none", "minimal", "low", "medium", "high", "xhigh"];

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
    private string _model = DefaultModel;
    private string _reasoningEffort = DefaultReasoningEffort;

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
    /// Configure from user settings. Empty or invalid models use
    /// <see cref="DefaultModel"/>; reasoning defaults to
    /// <see cref="DefaultReasoningEffort"/> ("none").
    /// </summary>
    public void Configure(bool enabled, string? model = null, string? reasoningEffort = null)
    {
        _enabled = enabled;
        _model = AgentCliPromptBuilder.SanitizeModelName(model) ?? DefaultModel;
        var effort = reasoningEffort?.Trim().ToLowerInvariant() ?? DefaultReasoningEffort;
        _reasoningEffort = AvailableReasoningEfforts.Contains(effort)
            ? effort
            : DefaultReasoningEffort;
    }

    protected override void ValidateRequest(TranslationRequest request)
    {
        ValidateEnabled();
        base.ValidateRequest(request);
    }

    private void ValidateEnabled()
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
        await foreach (var chunk in RunStreamAsync(
                           AgentCliPromptBuilder.BuildUserPrompt(request),
                           BaseOpenAIService.TranslationSystemPrompt,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    public async IAsyncEnumerable<string> CorrectGrammarStreamAsync(
        GrammarCorrectionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateEnabled();
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            throw new TranslationException("Text cannot be empty") { ServiceId = ServiceId };
        }

        await foreach (var chunk in RunStreamAsync(
                           GrammarCorrectionPromptResources.BuildUserPrompt(request),
                           GrammarCorrectionPromptResources.GetSystemPrompt(request.IncludeExplanations),
                           cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    private async IAsyncEnumerable<string> RunStreamAsync(
        string userPrompt,
        string systemPrompt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (instructionsFileName, instructionsFilePath) =
            await WriteInstructionsFileAsync(systemPrompt, cancellationToken).ConfigureAwait(false);
        try
        {
            var executable = await AgentCliExecutableLocator
                .LocateAsync(CliName, GetCandidatePaths(), cancellationToken)
                .ConfigureAwait(false)
                ?? throw NotInstalledError();

            var capabilities = await CodexCliCapabilities
                .GetAsync(executable, cancellationToken)
                .ConfigureAwait(false);

            var controlLines = new List<string>();
            string? agentMessage = null;
            string? errorMessage = null;

            var lines = _runner.RunLinesAsync(
                executable,
                BuildArguments(_model, _reasoningEffort, capabilities, instructionsFileName),
                userPrompt,
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

            yield return ValidateCompletedResponse(agentMessage, errorMessage, controlLines);
        }
        finally
        {
            TryDeleteInstructionsFile(instructionsFilePath);
        }
    }

    internal static async Task<(string FileName, string FullPath)> WriteInstructionsFileAsync(
        string instructions,
        CancellationToken cancellationToken)
    {
        // The runner uses Path.GetTempPath() as cwd; a controlled relative name stays .cmd-safe.
        var fileName = $"easydict-codex-{Guid.NewGuid():N}.md";
        var fullPath = Path.Combine(Path.GetTempPath(), fileName);
        await File.WriteAllTextAsync(fullPath, instructions, cancellationToken).ConfigureAwait(false);
        return (fileName, fullPath);
    }

    internal static void TryDeleteInstructionsFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static string ValidateCompletedResponse(
        string? agentMessage,
        string? errorMessage,
        IReadOnlyList<string> controlLines)
    {
        if (errorMessage != null)
        {
            throw CodexCliEventParser.ClassifyFailure(
                ServiceIdValue,
                exitCode: 0,
                controlLines,
                errorMessage);
        }

        if (string.IsNullOrWhiteSpace(agentMessage))
        {
            throw InvalidResponseError();
        }

        return agentMessage;
    }

    /// <summary>
    /// Tries to load the current account's model catalog without starting a turn.
    /// </summary>
    public async Task<IReadOnlyList<string>> DiscoverModelsAsync(
        CancellationToken cancellationToken = default)
    {
        var executable = await AgentCliExecutableLocator
            .LocateAsync(CliName, GetCandidatePaths(), cancellationToken)
            .ConfigureAwait(false)
            ?? throw NotInstalledError();
        var discovered = await AgentCliModelCatalog
            .DiscoverCodexModelsAsync(executable, cancellationToken)
            .ConfigureAwait(false);
        return MergeDiscoveredModels(discovered);
    }

    internal static IReadOnlyList<string> MergeDiscoveredModels(IReadOnlyList<string> discovered)
    {
        return AvailableModels
            .Concat(discovered)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Runs Codex with an operation-specific instructions file, AGENTS.md loading
    /// disabled, stream-JSON output, an ephemeral session, and a read-only sandbox.
    /// The user prompt is read from stdin.
    /// </summary>
    internal static List<string> BuildArguments(
        string model,
        string reasoningEffort,
        CodexCliCapabilities? capabilities = null,
        string? instructionsFileName = null)
    {
        var arguments = new List<string>
        {
            "exec",
            "--json",
            "--skip-git-repo-check",
            "--ephemeral",
            "--ignore-user-config",
            "--ignore-rules",
            "--sandbox", "read-only",
            // The runner already sets the process working directory to the temp
            // folder; "." keeps user-profile paths off the command line.
            "-C", ".",
            "-c", "web_search=disabled",
            "-c", "project_doc_max_bytes=0",
        };

        if (!string.IsNullOrEmpty(instructionsFileName))
        {
            arguments.Add("-c");
            arguments.Add($"model_instructions_file='{instructionsFileName}'");
        }

        if (capabilities?.SupportsStrictConfig != false)
        {
            arguments.Add("--strict-config");
        }

        foreach (var feature in DisabledFeatures)
        {
            if (capabilities is not null && !capabilities.Features.Contains(feature))
            {
                continue;
            }

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

    private static TranslationException InvalidResponseError()
    {
        return new TranslationException(
            "Codex CLI completed without returning a translation. Install the latest Codex CLI and try again.")
        {
            ErrorCode = TranslationErrorCode.InvalidResponse,
            ServiceId = ServiceIdValue,
        };
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
