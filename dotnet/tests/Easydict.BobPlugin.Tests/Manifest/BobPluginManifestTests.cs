using Easydict.BobPlugin.Manifest;
using FluentAssertions;
using Xunit;

namespace Easydict.BobPlugin.Tests.Manifest;

public class BobPluginManifestTests
{
    [Fact]
    public void Parse_ReadsIdentityAndCategory()
    {
        var manifest = BobPluginManifest.Parse("""
            {
              "identifier": "com.example.translator",
              "category": "translate",
              "version": "1.2.3",
              "name": "Example",
              "summary": "A plugin.",
              "author": "Someone",
              "homepage": "https://example.invalid"
            }
            """);

        manifest.Identifier.Should().Be("com.example.translator");
        manifest.Category.Should().Be("translate");
        manifest.Version.Should().Be("1.2.3");
        manifest.Name.Should().Be("Example");
        manifest.IsTranslatePlugin.Should().BeTrue();
    }

    [Fact]
    public void Parse_IgnoresUnknownFields()
    {
        var manifest = BobPluginManifest.Parse("""
            {
              "identifier": "com.example.x",
              "category": "translate",
              "somethingNewer": { "nested": [1, 2, 3] }
            }
            """);

        manifest.Identifier.Should().Be("com.example.x");
    }

    [Fact]
    public void Parse_DefaultsNameToIdentifierAndVersionToZero()
    {
        var manifest = BobPluginManifest.Parse("""{"identifier":"com.example.x","category":"translate"}""");

        manifest.Name.Should().Be("com.example.x");
        manifest.Version.Should().Be("0.0.0");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("""{"category":"translate"}""")]
    [InlineData("""{"identifier":"com.example.x"}""")]
    public void Parse_RejectsUnusableManifests(string json)
    {
        var act = () => BobPluginManifest.Parse(json);

        act.Should().Throw<BobPluginException>()
            .Which.Code.Should().Be(BobPluginError.InvalidManifest);
    }

    [Fact]
    public void IsTranslatePlugin_IsFalseForOtherCategories()
    {
        BobPluginManifest.Parse("""{"identifier":"com.example.x","category":"ocr"}""")
            .IsTranslatePlugin.Should().BeFalse();
    }

    [Fact]
    public void Parse_ReadsTextAndMenuOptions()
    {
        var manifest = BobPluginManifest.Parse("""
            {
              "identifier": "com.example.x",
              "category": "translate",
              "options": [
                {
                  "identifier": "apiKey",
                  "type": "text",
                  "title": "API key",
                  "textConfig": { "type": "secure", "placeholderText": "sk-...", "height": 40 }
                },
                {
                  "identifier": "model",
                  "type": "menu",
                  "title": "Model",
                  "defaultValue": "fast",
                  "menuValues": [ { "title": "Fast", "value": "fast" }, { "value": "accurate" } ]
                },
                { "type": "text", "title": "Has no identifier" }
              ]
            }
            """);

        manifest.Options.Should().HaveCount(2);

        var apiKey = manifest.Options[0];
        apiKey.Type.Should().Be(BobPluginOptionType.Text);
        apiKey.IsSecure.Should().BeTrue();
        apiKey.TextConfig!.PlaceholderText.Should().Be("sk-...");
        apiKey.TextConfig.Height.Should().Be(40);

        var model = manifest.Options[1];
        model.Type.Should().Be(BobPluginOptionType.Menu);
        model.DefaultValue.Should().Be("fast");
        model.IsSecure.Should().BeFalse();
        model.MenuValues.Should().HaveCount(2);
        model.MenuValues[1].Title.Should().Be("accurate", "a menu value without a title falls back to its value");

        manifest.SecureOptions.Select(o => o.Identifier).Should().Equal("apiKey");
    }
}
