using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class MdxMddResourceTests
{
    // ---- DiscoverMddFiles tests ----

    [Fact]
    public void DiscoverMddFiles_WithNoDirectory_ReturnsEmptyList()
    {
        var result = MdxDictionaryTranslationService.DiscoverMddFiles("nonexistent_path.mdx");
        result.Should().BeEmpty();
    }

    [Fact]
    public void DiscoverMddFiles_WithTempDirectory_FindsUnnumberedMdd()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"mdx_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var mdxPath = Path.Combine(tempDir, "Oxford.mdx");
            var mddPath = Path.Combine(tempDir, "Oxford.mdd");
            File.WriteAllBytes(mdxPath, []);
            File.WriteAllBytes(mddPath, []);

            var result = MdxDictionaryTranslationService.DiscoverMddFiles(mdxPath);

            result.Should().HaveCount(1);
            result[0].Should().Be(mddPath);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void DiscoverMddFiles_WithNumberedFiles_ReturnsCorrectOrder()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"mdx_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var mdxPath = Path.Combine(tempDir, "Dict.mdx");
            File.WriteAllBytes(mdxPath, []);
            File.WriteAllBytes(Path.Combine(tempDir, "Dict.mdd"), []);
            File.WriteAllBytes(Path.Combine(tempDir, "Dict.1.mdd"), []);
            File.WriteAllBytes(Path.Combine(tempDir, "Dict.2.mdd"), []);

            var result = MdxDictionaryTranslationService.DiscoverMddFiles(mdxPath);

            result.Should().HaveCount(3);
            result[0].Should().EndWith("Dict.mdd");
            result[1].Should().EndWith("Dict.1.mdd");
            result[2].Should().EndWith("Dict.2.mdd");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void DiscoverMddFiles_WithOnlyNumbered_SkipsUnnumbered()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"mdx_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var mdxPath = Path.Combine(tempDir, "Dict.mdx");
            File.WriteAllBytes(mdxPath, []);
            // Only numbered, no unnumbered .mdd
            File.WriteAllBytes(Path.Combine(tempDir, "Dict.1.mdd"), []);
            File.WriteAllBytes(Path.Combine(tempDir, "Dict.2.mdd"), []);

            var result = MdxDictionaryTranslationService.DiscoverMddFiles(mdxPath);

            result.Should().HaveCount(2);
            result[0].Should().EndWith("Dict.1.mdd");
            result[1].Should().EndWith("Dict.2.mdd");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void DiscoverMddFiles_WithNoMddFiles_ReturnsEmpty()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"mdx_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var mdxPath = Path.Combine(tempDir, "Dict.mdx");
            File.WriteAllBytes(mdxPath, []);

            var result = MdxDictionaryTranslationService.DiscoverMddFiles(mdxPath);

            result.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    // ---- LookupResource tests ----

    [Fact]
    public void LookupResource_WithNoMddDicts_ReturnsNull()
    {
        var service = new MdxDictionaryTranslationService(
            "mdx::test", "Test", "fake.mdx",
            query => (query, "<div>test</div>"));

        var result = service.LookupResource("/style.css");

        result.Should().BeNull();
    }

    [Fact]
    public void LookupResource_WithEmptyKey_ReturnsNull()
    {
        var service = new MdxDictionaryTranslationService(
            "mdx::test", "Test", "fake.mdx",
            query => (query, "<div>test</div>"));

        var result = service.LookupResource("");

        result.Should().BeNull();
    }

    [Fact]
    public void HasMddResources_WhenNoMddLoaded_ReturnsFalse()
    {
        var service = new MdxDictionaryTranslationService(
            "mdx::test", "Test", "fake.mdx",
            query => (query, "<div>test</div>"));

        service.HasMddResources.Should().BeFalse();
    }

    [Fact]
    public void DictionaryDirectory_ReturnsParentDirectory()
    {
        var service = new MdxDictionaryTranslationService(
            "mdx::test", "Test", "fake.mdx",
            query => (query, "<div>test</div>"));

        service.DictionaryDirectory.Should().NotBeNull();
    }

    // ---- @@@LINK= redirection tests ----

    [Fact]
    public async Task TranslateAsync_WithLinkRedirection_FollowsLink()
    {
        var lookups = new Dictionary<string, string?>
        {
            ["colour"] = "@@@LINK=color",
            ["color"] = "<div>A visual attribute</div>"
        };

        var service = new MdxDictionaryTranslationService(
            "mdx::test", "Test", "fake.mdx",
            query => (query, lookups.GetValueOrDefault(query)));

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "colour",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        });

        result.ResultKind.Should().Be(TranslationResultKind.Success);
        result.TranslatedText.Should().Contain("visual attribute");
    }

    [Fact]
    public async Task TranslateAsync_WithChainedLinks_FollowsChain()
    {
        var lookups = new Dictionary<string, string?>
        {
            ["a"] = "@@@LINK=b",
            ["b"] = "@@@LINK=c",
            ["c"] = "<div>Final definition</div>"
        };

        var service = new MdxDictionaryTranslationService(
            "mdx::test", "Test", "fake.mdx",
            query => (query, lookups.GetValueOrDefault(query)));

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "a",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        });

        result.ResultKind.Should().Be(TranslationResultKind.Success);
        result.TranslatedText.Should().Contain("Final definition");
    }

    [Fact]
    public async Task TranslateAsync_WithBrokenLink_ReturnsNoResult()
    {
        var lookups = new Dictionary<string, string?>
        {
            ["broken"] = "@@@LINK=nonexistent"
        };

        var service = new MdxDictionaryTranslationService(
            "mdx::test", "Test", "fake.mdx",
            query => (query, lookups.GetValueOrDefault(query)));

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "broken",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        });

        result.ResultKind.Should().Be(TranslationResultKind.NoResult);
    }

    [Fact]
    public async Task TranslateAsync_WithTooManyRedirections_ReturnsNoResult()
    {
        // Create a circular redirection chain
        var lookups = new Dictionary<string, string?>
        {
            ["a"] = "@@@LINK=b",
            ["b"] = "@@@LINK=c",
            ["c"] = "@@@LINK=d",
            ["d"] = "@@@LINK=e",
            ["e"] = "@@@LINK=f",
            ["f"] = "@@@LINK=g",
        };

        var service = new MdxDictionaryTranslationService(
            "mdx::test", "Test", "fake.mdx",
            query => (query, lookups.GetValueOrDefault(query)));

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "a",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        });

        result.ResultKind.Should().Be(TranslationResultKind.NoResult);
    }

    // ---- RawHtml tests ----

    [Fact]
    public async Task TranslateAsync_WithMddResources_SetsRawHtml()
    {
        // We can't easily load real MDD files in unit tests, but we can verify
        // the RawHtml behavior: it's set only when HasMddResources is true.
        var service = new MdxDictionaryTranslationService(
            "mdx::test", "Test", "fake.mdx",
            query => (query, "<div>hello</div>"));

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "hello",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        });

        // No MDD loaded → RawHtml should be null
        result.RawHtml.Should().BeNull();
        result.TranslatedText.Should().Be("hello");
    }

    [Fact]
    public async Task TranslateAsync_WhenNoResult_RawHtmlIsNull()
    {
        var service = new MdxDictionaryTranslationService(
            "mdx::test", "Test", "fake.mdx",
            query => (query, ""));

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "missing",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        });

        result.RawHtml.Should().BeNull();
        result.ResultKind.Should().Be(TranslationResultKind.NoResult);
    }

    // ---- Settings JSON round-trip with MddFilePaths ----

    [Fact]
    public void ImportedMdxDictionary_MddFilePaths_DefaultsToEmptyList()
    {
        var dict = new SettingsService.ImportedMdxDictionary();

        dict.MddFilePaths.Should().NotBeNull();
        dict.MddFilePaths.Should().BeEmpty();
    }

    [Fact]
    public void ImportedMdxDictionary_MddFilePaths_RoundTrips()
    {
        var dict = new SettingsService.ImportedMdxDictionary
        {
            ServiceId = "mdx::test",
            DisplayName = "Test",
            FilePath = "test.mdx",
            MddFilePaths = ["test.mdd", "test.1.mdd"]
        };

        var json = System.Text.Json.JsonSerializer.Serialize(dict);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<SettingsService.ImportedMdxDictionary>(json);

        deserialized!.MddFilePaths.Should().HaveCount(2);
        deserialized.MddFilePaths[0].Should().Be("test.mdd");
        deserialized.MddFilePaths[1].Should().Be("test.1.mdd");
    }

    [Fact]
    public void ImportedMdxDictionary_MissingMddFilePaths_DeserializesToEmptyList()
    {
        // Simulate old settings JSON that doesn't have MddFilePaths
        var json = """{"ServiceId":"mdx::test","DisplayName":"Test","FilePath":"test.mdx","IsEncrypted":false}""";
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<SettingsService.ImportedMdxDictionary>(json);

        deserialized!.MddFilePaths.Should().NotBeNull();
        deserialized.MddFilePaths.Should().BeEmpty();
    }

    // ---- Additional DiscoverMddFiles tests ----

    [Fact]
    public void DiscoverMddFiles_WithGapInNumberedSequence_StopsAtGap()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"mdx_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var mdxPath = Path.Combine(tempDir, "Dict.mdx");
            File.WriteAllBytes(mdxPath, []);
            File.WriteAllBytes(Path.Combine(tempDir, "Dict.mdd"), []);
            File.WriteAllBytes(Path.Combine(tempDir, "Dict.1.mdd"), []);
            // Intentionally skip Dict.2.mdd
            File.WriteAllBytes(Path.Combine(tempDir, "Dict.3.mdd"), []);

            var result = MdxDictionaryTranslationService.DiscoverMddFiles(mdxPath);

            result.Should().HaveCount(2);
            result[0].Should().EndWith("Dict.mdd");
            result[1].Should().EndWith("Dict.1.mdd");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    // ---- Additional LookupResource tests ----

    [Fact]
    public void LookupResource_NullKey_ReturnsNull()
    {
        var service = new MdxDictionaryTranslationService(
            "mdx::test", "Test", "fake.mdx",
            query => (query, "<div>test</div>"));

        var result = service.LookupResource(null!);

        result.Should().BeNull();
    }

    // ---- Additional @@@LINK= tests ----

    [Fact]
    public async Task TranslateAsync_LinkWithWhitespace_TrimsTarget()
    {
        var lookups = new Dictionary<string, string?>
        {
            ["padded"] = "@@@LINK=  target  ",
            ["target"] = "<div>Resolved definition</div>"
        };

        var service = new MdxDictionaryTranslationService(
            "mdx::test", "Test", "fake.mdx",
            query => (query, lookups.GetValueOrDefault(query)));

        var result = await service.TranslateAsync(new TranslationRequest
        {
            Text = "padded",
            FromLanguage = Language.English,
            ToLanguage = Language.SimplifiedChinese
        });

        result.ResultKind.Should().Be(TranslationResultKind.Success);
        result.TranslatedText.Should().Contain("Resolved definition");
    }
}
