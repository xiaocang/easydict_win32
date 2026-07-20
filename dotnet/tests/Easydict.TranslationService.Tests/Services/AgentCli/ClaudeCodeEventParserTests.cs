using Easydict.TranslationService;
using Easydict.TranslationService.Services.AgentCli;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services.AgentCli;

/// <summary>
/// Tests for ClaudeCodeEventParser using captured `claude -p --output-format
/// stream-json --include-partial-messages` line shapes.
/// </summary>
public class ClaudeCodeEventParserTests
{
    private const string DeltaLine =
        """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"你好"}}}""";

    private const string MessageStartLine =
        """{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_1","role":"assistant"}}}""";

    private const string InitLine =
        """{"type":"system","subtype":"init","session_id":"abc"}""";

    private const string AssistantLine =
        """{"type":"assistant","message":{"content":[{"type":"text","text":"你好"}]}}""";

    private const string SuccessResultLine =
        """{"type":"result","subtype":"success","is_error":false,"result":"你好","usage":{"input_tokens":10,"output_tokens":5}}""";

    private const string ErrorResultLine =
        """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"Something broke"}""";

    [Fact]
    public void TryExtractTextDelta_ExtractsTextFromDeltaEvent()
    {
        ClaudeCodeEventParser.TryExtractTextDelta(DeltaLine, out var delta).Should().BeTrue();
        delta.Should().Be("你好");
    }

    [Theory]
    [InlineData(MessageStartLine)]
    [InlineData(InitLine)]
    [InlineData(AssistantLine)]
    [InlineData(SuccessResultLine)]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("[]")]
    public void TryExtractTextDelta_ReturnsFalseForNonDeltaLines(string line)
    {
        ClaudeCodeEventParser.TryExtractTextDelta(line, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseResult_ParsesSuccessResult()
    {
        var result = ClaudeCodeEventParser.TryParseResult(SuccessResultLine);

        result.Should().NotBeNull();
        result!.IsError.Should().BeFalse();
        result.ResultText.Should().Be("你好");
    }

    [Fact]
    public void TryParseResult_ParsesErrorResult()
    {
        var result = ClaudeCodeEventParser.TryParseResult(ErrorResultLine);

        result.Should().NotBeNull();
        result!.IsError.Should().BeTrue();
        result.ResultText.Should().Be("Something broke");
    }

    [Theory]
    [InlineData(DeltaLine)]
    [InlineData(InitLine)]
    [InlineData("garbage")]
    public void TryParseResult_ReturnsNullForNonResultLines(string line)
    {
        ClaudeCodeEventParser.TryParseResult(line).Should().BeNull();
    }

    [Fact]
    public void ClassifyFailure_UsageLimit_MapsToRateLimited()
    {
        var ex = ClaudeCodeEventParser.ClassifyFailure(
            "claude-code", 1, [], "You've hit your usage limit. Try again later.");

        ex.ErrorCode.Should().Be(TranslationErrorCode.RateLimited);
        ex.ServiceId.Should().Be("claude-code");
    }

    [Fact]
    public void ClassifyFailure_RateLimitEventLine_MapsToRateLimited()
    {
        var controlLines = new[]
        {
            """{"type":"rate_limit_event","rate_limit":{"status":"exceeded"}}""",
        };

        var ex = ClaudeCodeEventParser.ClassifyFailure("claude-code", 1, controlLines, "");

        ex.ErrorCode.Should().Be(TranslationErrorCode.RateLimited);
    }

    [Theory]
    [InlineData("Invalid API key · Please run /login")]
    [InlineData("Not logged in")]
    [InlineData("authentication_failed")]
    [InlineData("OAuth token has expired")]
    public void ClassifyFailure_AuthErrors_MapToInvalidApiKey(string stdErr)
    {
        var ex = ClaudeCodeEventParser.ClassifyFailure("claude-code", 1, [], stdErr);

        ex.ErrorCode.Should().Be(TranslationErrorCode.InvalidApiKey);
    }

    [Fact]
    public void ClassifyFailure_UnknownError_MapsToServiceUnavailableWithDetail()
    {
        var ex = ClaudeCodeEventParser.ClassifyFailure(
            "claude-code", 7, [], "error: unknown option '--no-session-persistence'");

        ex.ErrorCode.Should().Be(TranslationErrorCode.ServiceUnavailable);
        ex.Message.Should().Contain("exit code 7");
        ex.Message.Should().Contain("unknown option");
    }
}
