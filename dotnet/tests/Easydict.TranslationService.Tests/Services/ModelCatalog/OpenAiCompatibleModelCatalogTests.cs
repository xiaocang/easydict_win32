using Easydict.TranslationService;
using Easydict.TranslationService.Services.ModelCatalog;
using Easydict.TranslationService.Tests.Mocks;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services.ModelCatalog;

/// <summary>
/// Tests for <see cref="OpenAiCompatibleModelCatalog"/>'s tolerant parsing: providers differ
/// in envelope shape, pricing type, and which fields they bother to send.
/// </summary>
public class OpenAiCompatibleModelCatalogTests
{
    [Fact]
    public void Parse_SortsFreeModelsFirst_ThenById()
    {
        var json = """
        {
            "data": [
                { "id": "z/paid-model" },
                { "id": "a/free-model:free" },
                { "id": "b/another-paid" }
            ]
        }
        """;

        var result = OpenAiCompatibleModelCatalog.Parse(json);

        result.Select(e => e.Id).Should().Equal("a/free-model:free", "b/another-paid", "z/paid-model");
        result[0].IsFree.Should().BeTrue();
        result[1].IsFree.Should().BeFalse();
        result[2].IsFree.Should().BeFalse();
    }

    [Fact]
    public void Parse_AcceptsBareArrayEnvelope()
    {
        var json = """[{ "id": "x/model-a" }, { "id": "x/model-b" }]""";

        var result = OpenAiCompatibleModelCatalog.Parse(json);

        result.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_DetectsFreeFromZeroPricing_AsString()
    {
        var json = """
        { "data": [{ "id": "x/model-a", "pricing": { "prompt": "0", "completion": "0" } }] }
        """;

        var result = OpenAiCompatibleModelCatalog.Parse(json);

        result.Single().IsFree.Should().BeTrue();
    }

    [Fact]
    public void Parse_DetectsFreeFromZeroPricing_AsNumber()
    {
        var json = """
        { "data": [{ "id": "x/model-a", "pricing": { "prompt": 0, "completion": 0 } }] }
        """;

        var result = OpenAiCompatibleModelCatalog.Parse(json);

        result.Single().IsFree.Should().BeTrue();
    }

    [Fact]
    public void Parse_NonZeroStringPricing_IsNotFree()
    {
        var json = """
        { "data": [{ "id": "x/model-a", "pricing": { "prompt": "0.0000025", "completion": "0.00001" } }] }
        """;

        var result = OpenAiCompatibleModelCatalog.Parse(json);

        result.Single().IsFree.Should().BeFalse();
    }

    [Fact]
    public void Parse_MissingPricing_IsNotFreeUnlessIdSaysSo()
    {
        var json = """
        { "data": [{ "id": "x/model-a" }, { "id": "x/model-b-free" }, { "id": "x/model-c:free" }] }
        """;

        var result = OpenAiCompatibleModelCatalog.Parse(json);

        result.Single(e => e.Id == "x/model-a").IsFree.Should().BeFalse();
        result.Single(e => e.Id == "x/model-b-free").IsFree.Should().BeTrue();
        result.Single(e => e.Id == "x/model-c:free").IsFree.Should().BeTrue();
    }

    [Fact]
    public void Parse_KnownFreeRouterIds_AreFreeRegardlessOfPricing()
    {
        var json = """
        { "data": [{ "id": "openrouter/free" }, { "id": "orcarouter/free" }] }
        """;

        var result = OpenAiCompatibleModelCatalog.Parse(json);

        result.Should().OnlyContain(e => e.IsFree);
    }

    [Fact]
    public void Parse_ContextLength_ReadsFromRootOrTopProvider()
    {
        var json = """
        {
            "data": [
                { "id": "x/model-a", "context_length": 128000 },
                { "id": "x/model-b", "top_provider": { "context_length": "64000" } },
                { "id": "x/model-c" }
            ]
        }
        """;

        var result = OpenAiCompatibleModelCatalog.Parse(json);

        result.Single(e => e.Id == "x/model-a").ContextLength.Should().Be(128000);
        result.Single(e => e.Id == "x/model-b").ContextLength.Should().Be(64000);
        result.Single(e => e.Id == "x/model-c").ContextLength.Should().BeNull();
    }

    [Fact]
    public void Parse_SkipsEntriesMissingId()
    {
        var json = """
        { "data": [{ "name": "no id here" }, { "id": "x/model-a" }] }
        """;

        var result = OpenAiCompatibleModelCatalog.Parse(json);

        result.Should().ContainSingle().Which.Id.Should().Be("x/model-a");
    }

    [Fact]
    public void Parse_SkipsNonObjectArrayEntries()
    {
        var json = """{ "data": [ "not-an-object", { "id": "x/model-a" } ] }""";

        var result = OpenAiCompatibleModelCatalog.Parse(json);

        result.Should().ContainSingle().Which.Id.Should().Be("x/model-a");
    }

    [Fact]
    public void Parse_MalformedJson_Throws()
    {
        var action = () => OpenAiCompatibleModelCatalog.Parse("not json");

        action.Should().Throw<TranslationException>()
            .Where(e => e.ErrorCode == TranslationErrorCode.InvalidResponse);
    }

    [Fact]
    public void Parse_ObjectWithoutDataArray_Throws()
    {
        var action = () => OpenAiCompatibleModelCatalog.Parse("""{ "error": "nope" }""");

        action.Should().Throw<TranslationException>()
            .Where(e => e.ErrorCode == TranslationErrorCode.InvalidResponse);
    }

    [Fact]
    public async Task FetchAsync_NonSuccessStatus_ThrowsWithBody()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonResponse("""{"error":"unauthorized"}""", System.Net.HttpStatusCode.Unauthorized);
        using var httpClient = new HttpClient(handler);

        var action = () => OpenAiCompatibleModelCatalog.FetchAsync(
            httpClient, "https://example.test/v1/models", apiKey: "key");

        var assertion = await action.Should().ThrowAsync<TranslationException>();
        assertion.Which.ErrorCode.Should().Be(TranslationErrorCode.InvalidApiKey);
    }

    [Fact]
    public async Task FetchAsync_SendsBearerTokenWhenProvided()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonResponse("""{ "data": [] }""");
        using var httpClient = new HttpClient(handler);

        await OpenAiCompatibleModelCatalog.FetchAsync(httpClient, "https://example.test/v1/models", "secret-key");

        handler.LastRequest!.Headers.Authorization!.Parameter.Should().Be("secret-key");
    }

    [Fact]
    public async Task FetchAsync_OmitsAuthorizationWhenApiKeyIsNull()
    {
        var handler = new MockHttpMessageHandler();
        handler.EnqueueJsonResponse("""{ "data": [] }""");
        using var httpClient = new HttpClient(handler);

        await OpenAiCompatibleModelCatalog.FetchAsync(httpClient, "https://example.test/v1/models", apiKey: null);

        handler.LastRequest!.Headers.Authorization.Should().BeNull();
    }
}
