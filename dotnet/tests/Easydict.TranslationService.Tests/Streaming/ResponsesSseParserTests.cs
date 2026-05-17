using System.Text;
using Easydict.TranslationService.Streaming;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Streaming;

public class ResponsesSseParserTests
{
    private static Stream ToStream(string sse) =>
        new MemoryStream(Encoding.UTF8.GetBytes(sse));

    [Fact]
    public async Task ParseStreamAsync_YieldsDeltas_FromOutputTextDeltaEvents()
    {
        var sse =
            "event: response.output_text.delta\n" +
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hello\"}\n" +
            "\n" +
            "event: response.output_text.delta\n" +
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\" world\"}\n" +
            "\n" +
            "data: [DONE]\n\n";

        var chunks = new List<string>();
        await foreach (var c in ResponsesSseParser.ParseStreamAsync(ToStream(sse)))
            chunks.Add(c);

        chunks.Should().Equal("Hello", " world");
    }

    [Fact]
    public async Task ParseStreamAsync_IgnoresOtherEventTypes()
    {
        var sse =
            "event: response.created\n" +
            "data: {\"type\":\"response.created\",\"response\":{}}\n" +
            "\n" +
            "event: response.output_text.delta\n" +
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"X\"}\n" +
            "\n" +
            "event: response.completed\n" +
            "data: {\"type\":\"response.completed\"}\n\n";

        var chunks = new List<string>();
        await foreach (var c in ResponsesSseParser.ParseStreamAsync(ToStream(sse)))
            chunks.Add(c);

        chunks.Should().Equal("X");
    }

    [Fact]
    public async Task ParseStreamAsync_StopsAtDoneMarker()
    {
        var sse =
            "event: response.output_text.delta\n" +
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"a\"}\n" +
            "\n" +
            "data: [DONE]\n" +
            "\n" +
            "event: response.output_text.delta\n" +
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"should-not-arrive\"}\n";

        var chunks = new List<string>();
        await foreach (var c in ResponsesSseParser.ParseStreamAsync(ToStream(sse)))
            chunks.Add(c);

        chunks.Should().Equal("a");
    }

    [Fact]
    public async Task ParseStreamAsync_TolerantToMalformedJson()
    {
        var sse =
            "event: response.output_text.delta\n" +
            "data: {not-json}\n" +
            "\n" +
            "event: response.output_text.delta\n" +
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"ok\"}\n\n";

        var chunks = new List<string>();
        await foreach (var c in ResponsesSseParser.ParseStreamAsync(ToStream(sse)))
            chunks.Add(c);

        chunks.Should().Equal("ok");
    }
}
