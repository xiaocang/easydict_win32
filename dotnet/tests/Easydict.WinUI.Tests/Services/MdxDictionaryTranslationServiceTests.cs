using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class MdxDictionaryTranslationServiceTests
{
    [Fact]
    public void ToReadableText_ShouldStripTagsAndDecodeEntities()
    {
        var html = "<div>Hello&nbsp;<b>World</b><br/>Line 2 &amp; more<script>alert(1)</script></div>";

        var text = MdxDictionaryTranslationService.ToReadableText(html);

        text.Should().Be("Hello\u00A0World\nLine 2 & more");
    }

    [Fact]
    public void ToReadableText_ShouldDropEmptyLinesAfterCleanup()
    {
        var html = "<p>  A  </p><p> </p><div> B </div>";

        var text = MdxDictionaryTranslationService.ToReadableText(html);

        text.Should().Be("A\nB");
    }

    [Fact]
    public async Task TranslateAsync_WhenDefinitionExists_ReturnsSuccessResult()
    {
        var service = new MdxDictionaryTranslationService(
            "mdx::test",
            "Test Dictionary",
            "fake.mdx",
            query => (query, "<div>hello</div>"));

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        });

        result.ResultKind.Should().Be(TranslationResultKind.Success);
        result.TranslatedText.Should().Be("hello");
        result.InfoMessage.Should().BeNull();
    }

    [Fact]
    public async Task TranslateAsync_WhenDefinitionMissing_ReturnsNoResult()
    {
        var service = new MdxDictionaryTranslationService(
            "mdx::test",
            "Test Dictionary",
            "fake.mdx",
            query => (query, ""));

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        });

        result.ResultKind.Should().Be(TranslationResultKind.NoResult);
        result.InfoMessage.Should().Be("No result found in dictionary: hello");
        result.TranslatedText.Should().BeEmpty();
        result.WordResult.Should().BeNull();
    }

    [Fact]
    public async Task TranslateAsync_WhenTextEmpty_ThrowsTranslationException()
    {
        var service = new MdxDictionaryTranslationService(
            "mdx::test",
            "Test Dictionary",
            "fake.mdx",
            query => (query, "<div>ignored</div>"));

        var act = () => service.TranslateAsync(new TranslationRequest
        {
            Text = "   ",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        });

        await act.Should().ThrowAsync<TranslationException>()
            .WithMessage("Text cannot be empty");
    }

    [Fact]
    public void Constructor_WithMockLookup_SetsProperties()
    {
        var service = new MdxDictionaryTranslationService(
            "mdx::test",
            "Test Dictionary",
            "fake.mdx",
            query => (query, "<div>test</div>"));

        service.ServiceId.Should().Be("mdx::test");
        service.DisplayName.Should().Be("Test Dictionary");
        service.FilePath.Should().Be("fake.mdx");
        service.IsEncrypted.Should().BeFalse();
        service.RequiresApiKey.Should().BeFalse();
        service.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void Constructor_WithMissingFile_ThrowsFileNotFoundException()
    {
        var act = () => new MdxDictionaryTranslationService(
            "mdx::test",
            "Test Dictionary",
            "nonexistent_dictionary.mdx");

        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Constructor_WithMissingFileAndCredentials_ThrowsFileNotFoundException()
    {
        var act = () => new MdxDictionaryTranslationService(
            "mdx::test",
            "Test Dictionary",
            "nonexistent_dictionary.mdx",
            regcode: "dGVzdA==",
            email: "test@example.com");

        act.Should().Throw<FileNotFoundException>();
    }

    // ---- Encryption-related tests ----

    [Fact]
    public void EncryptedService_WithoutCredentials_IsNotConfigured()
    {
        var service = new MdxDictionaryTranslationService(
            "mdx::encrypted",
            "Encrypted Dict",
            "fake.mdx",
            isEncrypted: true);

        service.IsEncrypted.Should().BeTrue();
        service.RequiresApiKey.Should().BeTrue();
        service.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task EncryptedService_WithoutCredentials_ReturnsCredentialsNeededMessage()
    {
        var service = new MdxDictionaryTranslationService(
            "mdx::encrypted",
            "📚 Collins",
            "fake.mdx",
            isEncrypted: true);

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        });

        result.ResultKind.Should().Be(TranslationResultKind.NoResult);
        result.InfoMessage.Should().Contain("encrypted");
        result.InfoMessage.Should().Contain("Settings");
        result.TranslatedText.Should().BeEmpty();
    }

    [Fact]
    public async Task EncryptedService_WithoutCredentials_DoesNotThrowOnEmptyQuery()
    {
        // When unconfigured, the "not configured" message should take precedence
        // over the "text cannot be empty" validation
        var service = new MdxDictionaryTranslationService(
            "mdx::encrypted",
            "📚 Collins",
            "fake.mdx",
            isEncrypted: true);

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "   ",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        });

        result.ResultKind.Should().Be(TranslationResultKind.NoResult);
        result.InfoMessage.Should().Contain("encrypted");
    }

    [Fact]
    public void EncryptedService_SupportedLanguages_ReturnsAllLanguages()
    {
        var service = new MdxDictionaryTranslationService(
            "mdx::encrypted",
            "Encrypted Dict",
            "fake.mdx",
            isEncrypted: true);

        service.SupportedLanguages.Should().NotBeEmpty();
        service.SupportsLanguagePair(Language.English, Language.SimplifiedChinese).Should().BeTrue();
    }

    [Fact]
    public async Task EncryptedService_DetectLanguage_ReturnsAuto()
    {
        var service = new MdxDictionaryTranslationService(
            "mdx::encrypted",
            "Encrypted Dict",
            "fake.mdx",
            isEncrypted: true);

        var lang = await service.DetectLanguageAsync("hello");
        lang.Should().Be(Language.Auto);
    }

    // ---- ToReadableText edge cases ----

    [Fact]
    public void ToReadableText_ScriptBlock_IsRemoved()
    {
        var html = "<script type=\"text/javascript\">var x = 1; alert(x);</script>Hello";

        var text = MdxDictionaryTranslationService.ToReadableText(html);

        text.Should().Be("Hello");
    }

    [Fact]
    public void ToReadableText_StyleBlock_IsRemoved()
    {
        var html = "<style>.cls { color: red; }</style><p>Styled text</p>";

        var text = MdxDictionaryTranslationService.ToReadableText(html);

        text.Should().Be("Styled text");
    }

    [Theory]
    [InlineData("<br>")]
    [InlineData("<br/>")]
    [InlineData("<br />")]
    public void ToReadableText_BrVariants_ConvertToNewline(string brTag)
    {
        var html = $"Line1{brTag}Line2";

        var text = MdxDictionaryTranslationService.ToReadableText(html);

        text.Should().Be("Line1\nLine2");
    }

    [Fact]
    public void ToReadableText_BlockClosingTags_ProduceNewlines()
    {
        var html = "<p>Para</p><div>Block</div><ul><li>Item</li></ul>";

        var text = MdxDictionaryTranslationService.ToReadableText(html);

        text.Should().Be("Para\nBlock\nItem");
    }

    [Fact]
    public void ToReadableText_NbspEntity_DecodedToNonBreakingSpace()
    {
        var html = "word1&nbsp;word2";

        var text = MdxDictionaryTranslationService.ToReadableText(html);

        // &nbsp; decodes to U+00A0 (non-breaking space) via WebUtility.HtmlDecode
        text.Should().Be("word1\u00A0word2");
    }

    [Fact]
    public void ToReadableText_OnlyWhitespaceAndTags_ReturnsEmpty()
    {
        var html = "<div>  </div><p> </p><br/>";

        var text = MdxDictionaryTranslationService.ToReadableText(html);

        text.Should().BeEmpty();
    }

    // ---- TranslateAsync behavior ----

    [Fact]
    public async Task TranslateAsync_InputIsTrimmedBeforeLookup()
    {
        string? capturedQuery = null;
        var service = new MdxDictionaryTranslationService(
            "mdx::test", "Test", "fake.mdx",
            query =>
            {
                capturedQuery = query;
                return (query, "<div>result</div>");
            });

        await service.TranslateAsync(new TranslationRequest
        {
            Text = "  hello  ",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        });

        capturedQuery.Should().Be("hello");
    }

    [Fact]
    public async Task TranslateAsync_SuccessResult_HasExpectedStructure()
    {
        var service = new MdxDictionaryTranslationService(
            "mdx::test", "Test Dict", "fake.mdx",
            query => (query, "<div>definition text</div>"));

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "word",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        });

        result.ServiceName.Should().Be("Test Dict");
        result.OriginalText.Should().Be("word");
        result.WordResult.Should().NotBeNull();
        result.WordResult!.Definitions.Should().HaveCount(1);
        result.WordResult.Definitions[0].PartOfSpeech.Should().Be("dictionary");
        result.WordResult.Definitions[0].Meanings.Should().Contain("definition text");
    }

    [Fact]
    public async Task TranslateAsync_NullDefinition_ReturnsNoResult()
    {
        var service = new MdxDictionaryTranslationService(
            "mdx::test", "Test", "fake.mdx",
            query => (query, null));

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "missing",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        });

        result.ResultKind.Should().Be(TranslationResultKind.NoResult);
        result.WordResult.Should().BeNull();
    }

    [Fact]
    public async Task TranslateAsync_CancellationToken_ThrowsWhenCancelled()
    {
        var service = new MdxDictionaryTranslationService(
            "mdx::test", "Test", "fake.mdx",
            query => (query, "<div>test</div>"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => service.TranslateAsync(
            new TranslationRequest
            {
                Text = "hello",
                FromLanguage = Language.English,
                ToLanguage = Language.SimplifiedChinese
            },
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- Property tests ----

    [Fact]
    public void SupportsLanguagePair_AnyPair_ReturnsTrue()
    {
        var service = new MdxDictionaryTranslationService(
            "mdx::test", "Test", "fake.mdx",
            query => (query, "<div>test</div>"));

        service.SupportsLanguagePair(Language.Japanese, Language.French).Should().BeTrue();
        service.SupportsLanguagePair(Language.Auto, Language.Auto).Should().BeTrue();
    }

    [Fact]
    public void RequiresApiKey_MatchesIsEncrypted()
    {
        var plain = new MdxDictionaryTranslationService(
            "mdx::plain", "Plain", "fake.mdx",
            query => (query, "<div>test</div>"));

        var encrypted = new MdxDictionaryTranslationService(
            "mdx::enc", "Encrypted", "fake.mdx",
            isEncrypted: true);

        plain.RequiresApiKey.Should().BeFalse();
        plain.IsEncrypted.Should().BeFalse();
        encrypted.RequiresApiKey.Should().BeTrue();
        encrypted.IsEncrypted.Should().BeTrue();
    }
}
