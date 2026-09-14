using System.Text.Json;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.TextActions;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Foundation;

public class TextActionTests
{
    private static TextAction Url(string template, string id = "t") => new()
    {
        Id = id,
        Title = id,
        Type = TextActionType.OpenUrl,
        UrlTemplate = template
    };

    private static readonly TextActionContext SampleContext =
        new("hello world & more #1", "你好 世界", Language.English, Language.SimplifiedChinese);

    [Fact]
    public void TryBuildUri_EncodesTextAndLanguages()
    {
        var action = Url("https://example.com/s?q={encodedText}&t={encodedTranslation}&f={from}&l={to}");

        TextActionUrlBuilder.TryBuildUri(action, SampleContext, out var uri, out var error).Should().BeTrue(error);

        uri!.AbsoluteUri.Should().Be(
            "https://example.com/s?q=hello%20world%20%26%20more%20%231&t=%E4%BD%A0%E5%A5%BD%20%E4%B8%96%E7%95%8C&f=en&l=zh");
    }

    [Fact]
    public void TryBuildUri_TranslationFallsBackToText()
    {
        var action = Url("https://example.com/?q={encodedTranslation}");
        var context = new TextActionContext("abc", null, Language.Auto, Language.English);

        TextActionUrlBuilder.TryBuildUri(action, context, out var uri, out _).Should().BeTrue();

        uri!.Query.Should().Be("?q=abc");
    }

    [Theory]
    [InlineData("javascript:alert({encodedText})")]
    [InlineData("file:///C:/{encodedText}")]
    [InlineData("ftp://host/{encodedText}")]
    [InlineData("")]
    [InlineData("https://example.com/no-placeholder")]
    [InlineData("https://example.com/?q={encodedText}&x={unknown}")]
    public void ValidateTemplate_RejectsInvalidTemplates(string template)
    {
        TextActionUrlBuilder.ValidateTemplate(template).Should().NotBeNull();
        TextActionUrlBuilder.TryBuildUri(Url(template), SampleContext, out var uri, out var error).Should().BeFalse();
        uri.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void TryBuildUri_RejectsRunServiceAction()
    {
        var action = TextActionValidator.ForService("google", "Google");

        TextActionUrlBuilder.TryBuildUri(action, SampleContext, out _, out var error).Should().BeFalse();
        error.Should().NotBeNull();
    }

    [Fact]
    public void Defaults_AreUniqueAndValid()
    {
        TextActionCatalog.Defaults.Select(a => a.Id).Should().OnlyHaveUniqueItems();
        foreach (var action in TextActionCatalog.Defaults)
        {
            TextActionValidator.Validate(action).Should().BeNull(action.Id);
            action.IsBuiltIn.Should().BeTrue();
            TextActionUrlBuilder.TryBuildUri(action, SampleContext, out var uri, out _).Should().BeTrue(action.Id);
            uri!.Scheme.Should().Be("https");
        }

        TextActionCatalog.Defaults.Count(a => a.ShowOnPopButton).Should().Be(1, "only one search button is on the pop-up by default");
    }

    [Fact]
    public void Merge_KeepsSavedOrderAndEdits_AppendsMissingBuiltIns()
    {
        var saved = new List<TextAction>
        {
            TextActionValidator.ForService("bob:x:y", "My Plugin", source: "bob"),
            TextActionCatalog.Defaults[2] with { Title = "GH", IsEnabled = false },
            Url("https://example.com/?q={encodedText}", "custom")
        };

        var merged = TextActionCatalog.Merge(saved);

        merged.Select(a => a.Id).Take(3).Should().Equal("service:bob:x:y", "github", "custom");
        merged.Single(a => a.Id == "github").Title.Should().Be("GH");
        merged.Select(a => a.Id).Should().Contain(TextActionCatalog.Defaults.Select(d => d.Id));
        merged.Select(a => a.Id).Should().OnlyHaveUniqueItems();
        TextActionCatalog.Merge(null).Should().BeEquivalentTo(TextActionCatalog.Defaults);
        TextActionCatalog.Merge(new List<TextAction>()).Should().BeEquivalentTo(TextActionCatalog.Defaults);
    }

    [Fact]
    public void Normalize_KeepsUserRemovals_DropsDuplicates()
    {
        var saved = new List<TextAction>
        {
            Url("https://a/{encodedText}", "custom"),
            Url("https://b/{encodedText}", "custom"),
            new TextAction { Id = " ", Title = "blank", Type = TextActionType.OpenUrl, UrlTemplate = "https://c/{encodedText}" }
        };

        var normalized = TextActionCatalog.Normalize(saved);

        normalized.Should().ContainSingle().Which.UrlTemplate.Should().Be("https://a/{encodedText}");
        TextActionCatalog.Normalize(null).Should().BeEquivalentTo(TextActionCatalog.Defaults);
        TextActionCatalog.Normalize(new List<TextAction>()).Should().BeEmpty("a user who deleted every action keeps an empty list");
    }

    [Fact]
    public void Validator_ChecksTypeSpecificFields()
    {
        TextActionValidator.Validate(null).Should().NotBeNull();
        TextActionValidator.Validate(new TextAction { Id = "", Title = "t", Type = TextActionType.OpenUrl, UrlTemplate = "https://a/{encodedText}" }).Should().NotBeNull();
        TextActionValidator.Validate(new TextAction { Id = "x", Title = "t", Type = TextActionType.RunService }).Should().NotBeNull();
        TextActionValidator.Validate(TextActionValidator.ForService("youdao", "Youdao")).Should().BeNull();
        TextActionValidator.ServiceIdFromActionId("service:bob:a:b").Should().Be("bob:a:b");
        TextActionValidator.ServiceIdFromActionId("google").Should().BeNull();
    }

    [Fact]
    public void TextAction_RoundTripsThroughJson_WithEnumNames()
    {
        var actions = new List<TextAction>
        {
            TextActionCatalog.Defaults[0],
            TextActionValidator.ForService("bob:x:y", "Plugin", source: "bob")
        };

        var json = JsonSerializer.Serialize(actions);
        var restored = JsonSerializer.Deserialize<List<TextAction>>(json);

        json.Should().Contain("\"OpenUrl\"").And.Contain("\"RunService\"");
        restored.Should().BeEquivalentTo(actions);
    }
}
