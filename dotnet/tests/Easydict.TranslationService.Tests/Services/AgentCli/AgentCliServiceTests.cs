using Easydict.TranslationService.Models;
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
    }

    [Fact]
    public void Codex_ServiceIdentity()
    {
        var service = CreateCodexService();

        service.ServiceId.Should().Be("codex");
        service.DisplayName.Should().Be("Codex");
        service.RequiresApiKey.Should().BeFalse();
        service.IsStreaming.Should().BeTrue();
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
        service.Model.Should().Be("");
        service.ReasoningEffort.Should().Be("");
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
        arguments.Should().Contain("--include-partial-messages");
        arguments.Should().Contain("--no-session-persistence");
        arguments.Should().Contain("--strict-mcp-config");
        arguments.Should().ContainInOrder("--model", "sonnet");
        arguments.Should().ContainInOrder("--tools", "");
        arguments.Should().ContainInOrder("--setting-sources", "");
        arguments.Should().Contain("--system-prompt");

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
    public void Codex_BuildArguments_MirrorsUpstreamFlagSet()
    {
        var arguments = CodexCliService.BuildArguments("gpt-5.2", "low");

        arguments.Should().ContainInOrder("exec", "--json", "--skip-git-repo-check", "--ephemeral");
        arguments.Should().ContainInOrder("--sandbox", "read-only");
        arguments.Should().ContainInOrder("-C", ".");
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
        arguments.Should().NotContain("-c");
        arguments.TakeLast(2).Should().Equal("--", "-");
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

    [Fact]
    public void PromptBuilder_BuildCombinedPrompt_PrependsSystemPrompt()
    {
        var request = new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese,
        };

        var prompt = AgentCliPromptBuilder.BuildCombinedPrompt(request);

        prompt.Should().Contain("translation expert");
        prompt.Should().Contain("hello");
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
}
