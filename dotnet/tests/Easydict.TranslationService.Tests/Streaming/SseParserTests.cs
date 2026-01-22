using System.Text;
using Easydict.TranslationService.Streaming;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Streaming;

/// <summary>
/// Tests for SSE parser that extracts content from OpenAI-style streaming responses.
/// </summary>
public class SseParserTests
{
    private static Stream CreateSseStream(string content) =>
        new MemoryStream(Encoding.UTF8.GetBytes(content));

    [Fact]
    public async Task ParseStreamAsync_YieldsContent_FromWellFormedDataLines()
    {
        // Arrange
        var sseContent = """
            data: {"choices":[{"delta":{"content":"Hello"}}]}

            """;
        using var stream = CreateSseStream(sseContent);

        // Act
        var chunks = new List<string>();
        await foreach (var chunk in SseParser.ParseStreamAsync(stream))
        {
            chunks.Add(chunk);
        }

        // Assert
        chunks.Should().ContainSingle()
            .Which.Should().Be("Hello");
    }

    [Fact]
    public async Task ParseStreamAsync_YieldsMultipleChunks_InOrder()
    {
        // Arrange
        var sseContent = """
            data: {"choices":[{"delta":{"content":"Hello"}}]}

            data: {"choices":[{"delta":{"content":" "}}]}

            data: {"choices":[{"delta":{"content":"World"}}]}

            """;
        using var stream = CreateSseStream(sseContent);

        // Act
        var chunks = new List<string>();
        await foreach (var chunk in SseParser.ParseStreamAsync(stream))
        {
            chunks.Add(chunk);
        }

        // Assert
        chunks.Should().HaveCount(3);
        chunks[0].Should().Be("Hello");
        chunks[1].Should().Be(" ");
        chunks[2].Should().Be("World");
    }

    [Fact]
    public async Task ParseStreamAsync_IgnoresBlankLines()
    {
        // Arrange
        var sseContent = """

            data: {"choices":[{"delta":{"content":"Hello"}}]}


            data: {"choices":[{"delta":{"content":"World"}}]}

            """;
        using var stream = CreateSseStream(sseContent);

        // Act
        var chunks = new List<string>();
        await foreach (var chunk in SseParser.ParseStreamAsync(stream))
        {
            chunks.Add(chunk);
        }

        // Assert
        chunks.Should().HaveCount(2);
        chunks[0].Should().Be("Hello");
        chunks[1].Should().Be("World");
    }

    [Fact]
    public async Task ParseStreamAsync_IgnoresNonDataLines()
    {
        // Arrange
        var sseContent = """
            event: message
            id: 123
            retry: 1000
            data: {"choices":[{"delta":{"content":"Hello"}}]}
            : this is a comment
            """;
        using var stream = CreateSseStream(sseContent);

        // Act
        var chunks = new List<string>();
        await foreach (var chunk in SseParser.ParseStreamAsync(stream))
        {
            chunks.Add(chunk);
        }

        // Assert
        chunks.Should().ContainSingle()
            .Which.Should().Be("Hello");
    }

    [Fact]
    public async Task ParseStreamAsync_StopsOnDoneMarker()
    {
        // Arrange
        var sseContent = """
            data: {"choices":[{"delta":{"content":"Hello"}}]}

            data: [DONE]

            """;
        using var stream = CreateSseStream(sseContent);

        // Act
        var chunks = new List<string>();
        await foreach (var chunk in SseParser.ParseStreamAsync(stream))
        {
            chunks.Add(chunk);
        }

        // Assert
        chunks.Should().ContainSingle()
            .Which.Should().Be("Hello");
    }

    [Fact]
    public async Task ParseStreamAsync_DoesNotYieldAfterDone()
    {
        // Arrange
        var sseContent = """
            data: {"choices":[{"delta":{"content":"Hello"}}]}

            data: [DONE]

            data: {"choices":[{"delta":{"content":"World"}}]}

            """;
        using var stream = CreateSseStream(sseContent);

        // Act
        var chunks = new List<string>();
        await foreach (var chunk in SseParser.ParseStreamAsync(stream))
        {
            chunks.Add(chunk);
        }

        // Assert
        chunks.Should().ContainSingle()
            .Which.Should().Be("Hello");
    }

    [Fact]
    public async Task ParseStreamAsync_SkipsMalformedJson_WithoutThrowing()
    {
        // Arrange
        var sseContent = """
            data: {"choices":[{"delta":{"content":"Hello"}}]}

            data: {invalid json}

            data: {"choices":[{"delta":{"content":"World"}}]}

            """;
        using var stream = CreateSseStream(sseContent);

        // Act
        var chunks = new List<string>();
        await foreach (var chunk in SseParser.ParseStreamAsync(stream))
        {
            chunks.Add(chunk);
        }

        // Assert
        chunks.Should().HaveCount(2);
        chunks[0].Should().Be("Hello");
        chunks[1].Should().Be("World");
    }

    [Fact]
    public async Task ParseStreamAsync_SkipsEventWithoutDeltaContent()
    {
        // Arrange
        var sseContent = """
            data: {"choices":[{"delta":{"role":"assistant"}}]}

            data: {"choices":[{"delta":{"content":"Hello"}}]}

            data: {"choices":[{"finish_reason":"stop"}]}

            """;
        using var stream = CreateSseStream(sseContent);

        // Act
        var chunks = new List<string>();
        await foreach (var chunk in SseParser.ParseStreamAsync(stream))
        {
            chunks.Add(chunk);
        }

        // Assert
        chunks.Should().ContainSingle()
            .Which.Should().Be("Hello");
    }

    [Fact]
    public async Task ParseStreamAsync_SkipsEventWithEmptyChoices()
    {
        // Arrange
        var sseContent = """
            data: {"choices":[]}

            data: {"choices":[{"delta":{"content":"Hello"}}]}

            data: {"id":"chatcmpl-123","object":"chat.completion.chunk"}

            """;
        using var stream = CreateSseStream(sseContent);

        // Act
        var chunks = new List<string>();
        await foreach (var chunk in SseParser.ParseStreamAsync(stream))
        {
            chunks.Add(chunk);
        }

        // Assert
        chunks.Should().ContainSingle()
            .Which.Should().Be("Hello");
    }

    [Fact]
    public async Task ParseStreamAsync_HandlesEmptyStream()
    {
        // Arrange
        using var stream = CreateSseStream("");

        // Act
        var chunks = new List<string>();
        await foreach (var chunk in SseParser.ParseStreamAsync(stream))
        {
            chunks.Add(chunk);
        }

        // Assert
        chunks.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseStreamAsync_RespectsCancellationToken()
    {
        // Arrange
        var sseContent = """
            data: {"choices":[{"delta":{"content":"Hello"}}]}

            data: {"choices":[{"delta":{"content":"World"}}]}

            data: {"choices":[{"delta":{"content":"!"}}]}

            """;
        using var stream = CreateSseStream(sseContent);
        using var cts = new CancellationTokenSource();

        // Act
        var chunks = new List<string>();
        var exception = await Record.ExceptionAsync(async () =>
        {
            await foreach (var chunk in SseParser.ParseStreamAsync(stream, cts.Token))
            {
                chunks.Add(chunk);
                cts.Cancel(); // Cancel after first chunk
            }
        });

        // Assert
        chunks.Should().ContainSingle()
            .Which.Should().Be("Hello");
        exception.Should().BeOfType<OperationCanceledException>();
    }

    [Fact]
    public async Task ParseStreamAsync_HandlesUnicodeContent()
    {
        // Arrange
        var sseContent = """
            data: {"choices":[{"delta":{"content":"你好"}}]}

            data: {"choices":[{"delta":{"content":"世界"}}]}

            data: {"choices":[{"delta":{"content":"🌍"}}]}

            """;
        using var stream = CreateSseStream(sseContent);

        // Act
        var chunks = new List<string>();
        await foreach (var chunk in SseParser.ParseStreamAsync(stream))
        {
            chunks.Add(chunk);
        }

        // Assert
        chunks.Should().HaveCount(3);
        chunks[0].Should().Be("你好");
        chunks[1].Should().Be("世界");
        chunks[2].Should().Be("🌍");
    }
}
