using System.Text.Json;
using Easydict.TranslationService.LongDocument;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services;

public sealed class RouterQueryModeTests
{
    [Theory]
    [InlineData("openrouter", false)]
    [InlineData("openrouter", true)]
    [InlineData("orcarouter", false)]
    [InlineData("orcarouter", true)]
    public async Task GrammarCorrection_StreamsCorrectionsWithOptionalExplanations(string serviceId, bool explain)
    {
        using var handler = new MockHttpMessageHandler();
        using var client = new HttpClient(handler);
        var service = CreateService(serviceId, client);
        var chunks = explain ? new[] { "This is a pen.", "\n---\n", "Use is with a singular subject." } : new[] { "This is ", "a pen." };
        EnqueueChunks(handler, chunks);

        var request = new GrammarCorrectionRequest
        {
            Text = "This are a pen.", Language = Language.English, IncludeExplanations = explain
        };
        var received = new List<string>();
        await foreach (var chunk in ((IGrammarCorrectionService)service).CorrectGrammarStreamAsync(request))
            received.Add(chunk);

        received.Should().Equal(chunks);
        var result = GrammarCorrectionParser.Parse(string.Concat(received), request.Text, service.DisplayName, 0);
        result.CorrectedText.Should().Be("This is a pen.");
        result.Explanation.Should().Be(explain ? "Use is with a singular subject." : null);
        AssertRouterRequest(handler, service);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var userMessage = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
        userMessage.Should().Contain(request.Text).And.Contain("English");
    }

    [Theory]
    [InlineData("openrouter")]
    [InlineData("orcarouter")]
    public async Task LongDocument_TranslatesParagraphsThroughSelectedRouter(string serviceId)
    {
        using var handler = new MockHttpMessageHandler();
        using var client = new HttpClient(handler);
        var service = CreateService(serviceId, client);
        using var manager = new TranslationManager();
        manager.RegisterService(service);
        EnqueueChunks(handler, "这是文档的第一段。");
        EnqueueChunks(handler, "这是文档的第二段。");

        var pipeline = new LongDocumentTranslationService(translateWithService: (request, id, token) => manager.TranslateAsync(request, token, id));
        var document = new SourceDocument
        {
            DocumentId = "router-document",
            Pages = [new SourceDocumentPage
            {
                PageNumber = 1,
                Blocks = [
                    new SourceDocumentBlock { BlockId = "first", BlockType = SourceBlockType.Paragraph, Text = "This is the first paragraph of a document." },
                    new SourceDocumentBlock { BlockId = "second", BlockType = SourceBlockType.Paragraph, Text = "This is the second paragraph of a document." }
                ]
            }]
        };
        var translated = await pipeline.TranslateAsync(document, new LongDocumentTranslationOptions
        {
            ServiceId = serviceId, FromLanguage = Language.English, ToLanguage = Language.SimplifiedChinese,
            EnableFormulaProtection = false, MaxRetriesPerBlock = 0, MaxConcurrency = 1,
            CustomPrompt = "Preserve product names."
        });

        translated.Pages.Single().Blocks.Select(block => block.TranslatedText)
            .Should().Equal("这是文档的第一段。", "这是文档的第二段。");
        handler.Requests.Should().HaveCount(2);
        AssertRouterRequest(handler, service);
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()
            .Should().Contain("Preserve product names.");
    }

    private static BaseOpenAIService CreateService(string id, HttpClient client)
    {
        if (id == "openrouter")
        {
            var service = new OpenRouterService(client);
            service.Configure("test-router-key", model: "vendor/test-model");
            return service;
        }
        var orca = new OrcaRouterService(client);
        orca.Configure("test-router-key", model: "vendor/test-model");
        return orca;
    }

    private static void EnqueueChunks(MockHttpMessageHandler handler, params string[] chunks)
        => handler.EnqueueStreamingResponse(chunks.Select(content => JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { content } } }
        })));

    private static void AssertRouterRequest(MockHttpMessageHandler handler, BaseOpenAIService service)
    {
        handler.LastRequest!.RequestUri!.AbsoluteUri.Should().Be(service.Endpoint);
        handler.LastRequest.Headers.Authorization!.ToString().Should().Be("Bearer test-router-key");
        handler.LastRequest.Headers.GetValues("X-Title").Should().ContainSingle("Easydict for Windows");
        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        body.RootElement.GetProperty("model").GetString().Should().Be("vendor/test-model");
        body.RootElement.GetProperty("stream").GetBoolean().Should().BeTrue();
    }
}
