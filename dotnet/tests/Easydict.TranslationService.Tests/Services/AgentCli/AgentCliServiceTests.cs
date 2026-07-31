using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Services.AgentCli;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services.AgentCli;

/// <summary>
/// Tests for ClaudeCodeService / CodexCliService configuration, argument
/// construction, and prompt building. No CLI processes are spawned.
/// </summary>
public class AgentCliServiceTests
{
    private readonly HttpClient _httpClient = new();

    private ClaudeCodeService CreateClaudeService() => new(_httpClient);

    private CodexCliService CreateCodexService() => new(_httpClient);

    [Fact]
    public void ClaudeCode_ServiceIdentity()
    {
        var service = CreateClaudeService();

        service.ServiceId.Should().Be("claude-code");
        service.DisplayName.Should().Be("Claude Code");
        service.RequiresApiKey.Should().BeFalse();
        service.IsStreaming.Should().BeTrue();
        service.Should().BeAssignableTo<IGrammarCorrectionService>();
    }

    [Fact]
    public void Codex_ServiceIdentity()
    {
        var service = CreateCodexService();

        service.ServiceId.Should().Be("codex");
        service.DisplayName.Should().Be("Codex");
        service.RequiresApiKey.Should().BeFalse();
        service.IsStreaming.Should().BeTrue();
        service.Should().BeAssignableTo<IGrammarCorrectionService>();
    }

    [Fact]
    public void ClaudeCode_IsConfigured_FollowsEnabledFlag()
    {
        var service = CreateClaudeService();

        service.IsConfigured.Should().BeFalse();
        service.Configure(enabled: true);
        service.IsConfigured.Should().BeTrue();
        service.Configure(enabled: false);
        service.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void ClaudeCode_Configure_InvalidModelFallsBackToDefault()
    {
        var service = CreateClaudeService();

        service.Configure(enabled: true, model: "bad model & name");
        service.Model.Should().Be(ClaudeCodeService.DefaultModel);

        service.Configure(enabled: true, model: "opus");
        service.Model.Should().Be("opus");

        service.Configure(enabled: true, model: "  ");
        service.Model.Should().Be(ClaudeCodeService.DefaultModel);
    }

    [Fact]
    public void Codex_Configure_NormalizesModelAndEffort()
    {
        var service = CreateCodexService();

        service.Configure(enabled: true, model: " gpt-5.2-codex ", reasoningEffort: "HIGH");
        service.Model.Should().Be("gpt-5.2-codex");
        service.ReasoningEffort.Should().Be("high");

        service.Configure(enabled: true, model: "bad|model", reasoningEffort: "extreme");
        service.Model.Should().Be(CodexCliService.DefaultModel);
        service.ReasoningEffort.Should().Be(CodexCliService.DefaultReasoningEffort);
    }

    [Fact]
    public void AgentCli_Defaults_PreferFastModelsWithThinkingDisabled()
    {
        var claude = CreateClaudeService();
        var codex = CreateCodexService();

        claude.Model.Should().Be("haiku");
        codex.Model.Should().Be("gpt-5.6-luna");
        codex.ReasoningEffort.Should().Be("none");
        CodexCliService.BuildArguments(codex.Model, codex.ReasoningEffort)
            .Should().ContainInOrder("-c", "model_reasoning_effort=none");
    }

    [Fact]
    public void ClaudeCode_Configure_StoresCustomExecutablePath()
    {
        var service = CreateClaudeService();

        service.Configure(enabled: true, executablePath: @" C:\tools\claude.exe ");

        service.ExecutablePath.Should().Be(@"C:\tools\claude.exe");
    }

    [Fact]
    public async Task ClaudeCode_TranslateAsync_WhenDisabled_ThrowsConfigurationError()
    {
        var service = CreateClaudeService();
        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
        };

        var act = () => service.TranslateAsync(request);

        var ex = await act.Should().ThrowAsync<TranslationException>();
        ex.Which.ErrorCode.Should().Be(TranslationErrorCode.InvalidApiKey);
    }

    [Fact]
    public async Task Codex_TranslateAsync_WhenDisabled_ThrowsConfigurationError()
    {
        var service = CreateCodexService();
        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
        };

        var act = () => service.TranslateAsync(request);

        var ex = await act.Should().ThrowAsync<TranslationException>();
        ex.Which.ErrorCode.Should().Be(TranslationErrorCode.InvalidApiKey);
    }

    [Fact]
    public void ClaudeCode_BuildArguments_ContainsTokenReductionFlagsAndModel()
    {
        var arguments = ClaudeCodeService.BuildArguments("sonnet");

        arguments.Should().ContainInOrder("-p", "--verbose", "--output-format", "stream-json");
        arguments.Should().Contain("--safe-mode");
        arguments.Should().Contain("--include-partial-messages");
        arguments.Should().Contain("--no-session-persistence");
        arguments.Should().Contain("--strict-mcp-config");
        arguments.Should().ContainInOrder("--model", "sonnet");
        arguments.Should().ContainInOrder("--tools", "");
        arguments.Should().ContainInOrder("--setting-sources", "");
        arguments.Should().ContainInOrder("--system-prompt", BaseOpenAIService.TranslationSystemPrompt);

        // The prompt itself must never be on the command line — it goes to stdin.
        arguments.Should().NotContain(arg => arg.Contains("Translate the following"));
    }

    [Fact]
    public void ClaudeCode_BuildArguments_OmitsModelWhenEmpty()
    {
        var arguments = ClaudeCodeService.BuildArguments("");

        arguments.Should().NotContain("--model");
    }

    [Fact]
    public void AgentCli_BuildArguments_UseOperationSpecificPromptSources()
    {
        var systemPrompt = GrammarCorrectionPromptResources.GetSystemPrompt(includeExplanations: true);

        ClaudeCodeService.BuildArguments("haiku", systemPrompt)
            .Should().ContainInOrder(
                "--system-prompt",
                AgentCliPromptBuilder.BuildSystemPromptArgument(systemPrompt));
        CodexCliService.BuildArguments("gpt-5.6-luna", "none", instructionsFileName: "grammar.md")
            .Should().ContainInOrder("-c", "model_instructions_file='grammar.md'");
    }

    [Fact]
    public void Codex_BuildArguments_MirrorsUpstreamFlagSet()
    {
        var arguments = CodexCliService.BuildArguments(
            "gpt-5.2", "low", instructionsFileName: "translation.md");

        arguments.Should().ContainInOrder("exec", "--json", "--skip-git-repo-check", "--ephemeral");
        arguments.Should().Contain("--ignore-user-config");
        arguments.Should().Contain("--ignore-rules");
        arguments.Should().Contain("--strict-config");
        arguments.Should().ContainInOrder("--sandbox", "read-only");
        arguments.Should().ContainInOrder("-C", ".");
        arguments.Should().ContainInOrder("-c", "web_search=disabled");
        arguments.Should().ContainInOrder("-c", "project_doc_max_bytes=0");
        arguments.Should().ContainInOrder("-c", "model_instructions_file='translation.md'");
        arguments.Should().ContainInOrder("--disable", "shell_tool");
        arguments.Should().ContainInOrder("-m", "gpt-5.2");
        arguments.Should().ContainInOrder("-c", "model_reasoning_effort=low");
        arguments.TakeLast(2).Should().Equal("--", "-");
    }

    [Fact]
    public void Codex_BuildArguments_OmitsOptionalFlagsWhenEmpty()
    {
        var arguments = CodexCliService.BuildArguments("", "");

        arguments.Should().NotContain("-m");
        arguments.Should().NotContain(argument =>
            argument.StartsWith("model_instructions_file=", StringComparison.Ordinal));
        arguments.Should().NotContain(arg => arg.StartsWith("model_reasoning_effort=", StringComparison.Ordinal));
        arguments.Should().ContainInOrder("-c", "web_search=disabled");
        arguments.TakeLast(2).Should().Equal("--", "-");
    }

    [Fact]
    public void Codex_BuildArguments_UsesOnlyFeaturesReportedByInstalledCli()
    {
        var capabilities = new CodexCliCapabilities(
            new HashSet<string>(["shell_tool", "browser_use"], StringComparer.Ordinal),
            SupportsStrictConfig: false);

        var arguments = CodexCliService.BuildArguments("gpt-5.6-luna", "", capabilities);

        arguments.Should().ContainInOrder("--disable", "shell_tool");
        arguments.Should().ContainInOrder("--disable", "browser_use");
        arguments.Should().NotContain("shell_snapshot");
        arguments.Should().Contain("--ignore-rules");
        arguments.Should().NotContain("--strict-config");
    }

    [Fact]
    public void CodexCapabilities_Parse_RequiresIsolationFlags()
    {
        var act = () => CodexCliCapabilities.Parse(
            "--ephemeral",
            "shell_tool stable true");

        var ex = act.Should().Throw<TranslationException>().Which;
        ex.RecoveryAction.Should().Be("install-latest-codex");
        ex.DocumentationUrl.Should().Be(CodexCliService.InstallDocumentationUrl);
    }

    [Fact]
    public void CodexCapabilities_Parse_ReturnsSupportedFeatures()
    {
        var capabilities = CodexCliCapabilities.Parse(
            "--json --skip-git-repo-check --ephemeral --ignore-user-config --ignore-rules --sandbox --cd --config --disable --strict-config",
            "shell_tool stable true\nbrowser_use stable true");

        capabilities.Features.Should().BeEquivalentTo("shell_tool", "browser_use");
        capabilities.SupportsStrictConfig.Should().BeTrue();
    }

    [Fact]
    public void ErrorFormatter_RedactsSecretsBeforeDisplay()
    {
        var detail = AgentCliErrorFormatter.BuildDetail(
            [],
            "OPENAI_API_KEY=sk-secretvalue123456789");

        detail.Should().Contain("[redacted]");
        detail.Should().NotContain("secretvalue");
    }

    [Fact]
    public void ModelCatalog_ParseClaudeHelp_ReturnsDocumentedAliases()
    {
        const string Help = """
            --model <model> Model alias (e.g. 'fable', 'opus', or 'sonnet')
              -n, --name
            """;

        AgentCliModelCatalog.ParseClaudeHelp(Help)
            .Should().Equal("fable", "opus", "sonnet");
    }

    [Fact]
    public void ClaudeCode_MergeDiscoveredModels_PreservesHaikuFallback()
    {
        ClaudeCodeService.MergeDiscoveredModels(["fable", "opus", "sonnet"])
            .Should().Equal("fable", "opus", "sonnet", "haiku");
    }

    [Fact]
    public void ModelCatalog_ParseCodexCatalog_ReturnsVisibleModelSlugs()
    {
        const string Catalog =
            """{"models":[{"slug":"sol","visibility":"list"},{"slug":"hidden","visibility":"hide"},{"id":"terra"}]}""";

        AgentCliModelCatalog.ParseCodexCatalog(Catalog)
            .Should().Equal("sol", "terra");
    }

    [Fact]
    public void Codex_MergeDiscoveredModels_PreservesRequestedDefaults()
    {
        CodexCliService.MergeDiscoveredModels(["gpt-5.5"])
            .Should().Equal("gpt-5.6-luna", "gpt-5.6-terra", "gpt-5.6-sol", "gpt-5.5");
    }

    [Fact]
    public void Codex_ValidateCompletedResponse_EmptySuccess_ThrowsInvalidResponse()
    {
        var act = () => CodexCliService.ValidateCompletedResponse(null, null, []);

        var ex = act.Should().Throw<TranslationException>().Which;
        ex.ErrorCode.Should().Be(TranslationErrorCode.InvalidResponse);
        ex.ServiceId.Should().Be(CodexCliService.ServiceIdValue);
    }

    [Fact]
    public async Task ClaudeCode_TranslateAsync_UsesCustomExecutablePath()
    {
        var (directory, executable) = await CreateClaudeShimAsync("translated");
        try
        {
            var service = CreateClaudeService();
            service.Configure(enabled: true, model: "haiku", executablePath: executable);

            var result = await service.TranslateAsync(CreateTranslationRequest());

            result.TranslatedText.Should().Be("translated");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ClaudeCode_TranslateAsync_EmptySuccessfulResponse_ThrowsInvalidResponse()
    {
        var (directory, executable) = await CreateClaudeShimAsync("");
        try
        {
            var service = CreateClaudeService();
            service.Configure(enabled: true, model: "haiku", executablePath: executable);

            var act = () => service.TranslateAsync(CreateTranslationRequest());

            var ex = await act.Should().ThrowAsync<TranslationException>();
            ex.Which.ErrorCode.Should().Be(TranslationErrorCode.InvalidResponse);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ClaudeCode_CorrectGrammarStreamAsync_UsesStreamJson()
    {
        var (directory, executable) = await CreateClaudeShimAsync("corrected");
        try
        {
            var service = CreateClaudeService();
            service.Configure(enabled: true, model: "haiku", executablePath: executable);
            var chunks = new List<string>();

            await foreach (var chunk in service.CorrectGrammarStreamAsync(new GrammarCorrectionRequest
                           {
                               Text = "This are wrong.",
                               Language = Language.English,
                               IncludeExplanations = true,
                           }))
            {
                chunks.Add(chunk);
            }

            string.Concat(chunks).Should().Be("corrected");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PromptBuilder_BuildUserPrompt_IncludesLanguagesAndText()
    {
        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
        };

        var prompt = AgentCliPromptBuilder.BuildUserPrompt(request);

        prompt.Should().Contain("hello");
        prompt.Should().Contain("Translate the following");
        prompt.Should().NotContain("translation expert");
    }

    [Fact]
    public void PromptBuilder_BuildUserPrompt_FoldsCustomPromptIntoStdinText()
    {
        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
            CustomPrompt = "Prefer formal tone",
        };

        var prompt = AgentCliPromptBuilder.BuildUserPrompt(request);

        prompt.Should().Contain("Prefer formal tone");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("sonnet", "sonnet")]
    [InlineData(" claude-sonnet-4-5 ", "claude-sonnet-4-5")]
    [InlineData("openai/gpt-5.2:latest", "openai/gpt-5.2:latest")]
    [InlineData("bad model", null)]
    [InlineData("model&calc", null)]
    [InlineData("model\"quote", null)]
    public void PromptBuilder_SanitizeModelName_WhitelistsSafeNames(string? input, string? expected)
    {
        AgentCliPromptBuilder.SanitizeModelName(input).Should().Be(expected);
    }

    private static TranslationRequest CreateTranslationRequest()
    {
        return new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
        };
    }

    private static async Task<(string Directory, string Executable)> CreateClaudeShimAsync(
        string resultText)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easydict-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var json =
            "{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"" + resultText + "\"}";

        if (OperatingSystem.IsWindows())
        {
            var executable = Path.Combine(directory, "claude.cmd");
            await File.WriteAllTextAsync(
                executable,
                $"@echo off\r\nif not \"%MAX_THINKING_TOKENS%\"==\"0\" exit /b 9\r\necho {json}\r\n");
            return (directory, executable);
        }

        var shellExecutable = Path.Combine(directory, "claude");
        await File.WriteAllTextAsync(
            shellExecutable,
            $"#!/bin/sh\n[ \"$MAX_THINKING_TOKENS\" = \"0\" ] || exit 9\nprintf '%s\\n' '{json}'\n");
        File.SetUnixFileMode(
            shellExecutable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (directory, shellExecutable);
    }

}
