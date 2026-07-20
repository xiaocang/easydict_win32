using Easydict.TranslationService.Services.AgentCli;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services.AgentCli;

/// <summary>
/// Tests for CodexCliEventParser using captured `codex exec --json` line shapes.
/// </summary>
public class CodexCliEventParserTests
{
    private const string ThreadStartedLine =
        """{"type":"thread.started","thread_id":"t1"}""";

    private const string AgentMessageLine =
        """{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"Hello world"}}""";

    private const string ReasoningItemLine =
        """{"type":"item.completed","item":{"id":"item_1","type":"reasoning","text":"thinking..."}}""";

    private const string TurnCompletedLine =
        """{"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":0,"output_tokens":20}}""";

    private const string TurnFailedLine =
        """{"type":"turn.failed","error":{"message":"stream disconnected"}}""";

    private const string ErrorStringLine =
        """{"type":"error","error":"boom"}""";

    private const string ErrorMessageLine =
        """{"type":"error","message":"boom via message"}""";

    private const string ErrorItemLine =
        """{"type":"item.completed","item":{"type":"error","text":"item level failure"}}""";

    [Fact]
    public void TryExtractAgentMessage_ExtractsTextFromAgentMessage()
    {
        CodexCliEventParser.TryExtractAgentMessage(AgentMessageLine).Should().Be("Hello world");
    }

    [Theory]
    [InlineData(ThreadStartedLine)]
    [InlineData(ReasoningItemLine)]
    [InlineData(TurnCompletedLine)]
    [InlineData(TurnFailedLine)]
    [InlineData("not json")]
    [InlineData("")]
    public void TryExtractAgentMessage_ReturnsNullForOtherLines(string line)
    {
        CodexCliEventParser.TryExtractAgentMessage(line).Should().BeNull();
    }

    [Fact]
    public void TryExtractErrorMessage_ParsesTurnFailedObjectError()
    {
        CodexCliEventParser.TryExtractErrorMessage(TurnFailedLine).Should().Be("stream disconnected");
    }

    [Fact]
    public void TryExtractErrorMessage_ParsesErrorStringForm()
    {
        CodexCliEventParser.TryExtractErrorMessage(ErrorStringLine).Should().Be("boom");
    }

    [Fact]
    public void TryExtractErrorMessage_ParsesErrorMessageField()
    {
        CodexCliEventParser.TryExtractErrorMessage(ErrorMessageLine).Should().Be("boom via message");
    }

    [Fact]
    public void TryExtractErrorMessage_ParsesErrorItem()
    {
        CodexCliEventParser.TryExtractErrorMessage(ErrorItemLine).Should().Be("item level failure");
    }

    [Theory]
    [InlineData(ThreadStartedLine)]
    [InlineData(AgentMessageLine)]
    [InlineData(TurnCompletedLine)]
    [InlineData("garbage")]
    public void TryExtractErrorMessage_ReturnsNullForNonErrorLines(string line)
    {
        CodexCliEventParser.TryExtractErrorMessage(line).Should().BeNull();
    }

    [Theory]
    [InlineData("Not signed in. Run `codex login` to authenticate.")]
    [InlineData("401 Unauthorized")]
    [InlineData("OPENAI_API_KEY is not set")]
    public void ClassifyFailure_AuthErrors_MapToInvalidApiKey(string stdErr)
    {
        var ex = CodexCliEventParser.ClassifyFailure("codex", 1, [], stdErr);

        ex.ErrorCode.Should().Be(TranslationErrorCode.InvalidApiKey);
        ex.ServiceId.Should().Be("codex");
    }

    [Theory]
    [InlineData("429 Too Many Requests")]
    [InlineData("You have hit your usage limit")]
    [InlineData("insufficient_quota")]
    public void ClassifyFailure_QuotaErrors_MapToRateLimited(string stdErr)
    {
        var ex = CodexCliEventParser.ClassifyFailure("codex", 1, [], stdErr);

        ex.ErrorCode.Should().Be(TranslationErrorCode.RateLimited);
    }

    [Fact]
    public void ClassifyFailure_UsesErrorEventsFromControlLines()
    {
        var controlLines = new[]
        {
            ThreadStartedLine,
            """{"type":"turn.failed","error":{"message":"usage limit reached for your plan"}}""",
        };

        var ex = CodexCliEventParser.ClassifyFailure("codex", 1, controlLines, "");

        ex.ErrorCode.Should().Be(TranslationErrorCode.RateLimited);
    }

    [Fact]
    public void ClassifyFailure_UnknownError_MapsToServiceUnavailableWithDetail()
    {
        var controlLines = new[] { TurnFailedLine };

        var ex = CodexCliEventParser.ClassifyFailure("codex", 2, controlLines, "");

        ex.ErrorCode.Should().Be(TranslationErrorCode.ServiceUnavailable);
        ex.Message.Should().Contain("exit code 2");
        ex.Message.Should().Contain("stream disconnected");
    }
}
