using System.Text.Json;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using FluentAssertions;
using LexIndex;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public sealed class LocalDictionaryIndexServiceTests : IDisposable
{
    private readonly string _tempRoot;

    public LocalDictionaryIndexServiceTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "Easydict.WinUI.Tests",
            nameof(LocalDictionaryIndexServiceTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task EnsureIndexAsync_BuildsIndexAndManifest()
    {
        var sourcePath = CreateSourceFile("dict-a.mdx", "seed");
        var dictionary = CreateDictionary("mdx::a", "Dictionary A", sourcePath);
        var service = CreateQueryableService(
            dictionary.ServiceId,
            dictionary.DisplayName,
            sourcePath,
            () => ["apple", "application"]);
        var sut = new LocalDictionaryIndexService(_tempRoot);

        await sut.EnsureIndexAsync(dictionary, service);

        var folderPath = Path.Combine(_tempRoot, Uri.EscapeDataString(dictionary.ServiceId));
        var indexPath = Path.Combine(folderPath, "index.bin");
        var manifestPath = Path.Combine(folderPath, "manifest.json");

        File.Exists(indexPath).Should().BeTrue();
        File.Exists(manifestPath).Should().BeTrue();

        var manifest = JsonSerializer.Deserialize<LocalDictionaryIndexService.ManifestData>(
            await File.ReadAllTextAsync(manifestPath));
        manifest.Should().NotBeNull();
        manifest!.ServiceId.Should().Be(dictionary.ServiceId);
        manifest.SourcePath.Should().Be(dictionary.FilePath);
        manifest.EntryCount.Should().Be(2);
        manifest.IndexFormatVersion.Should().Be(1);
        manifest.NormalizationId.Should().Be(LexIndexBuildOptions.DefaultNormalizationId);

        var results = await sut.CompleteAsync("app", [dictionary.ServiceId], 10);
        results.Select(item => item.Key).Should().Equal("apple", "application");
        results.Select(item => item.DictDisplayName).Should().OnlyContain(name => name == dictionary.DisplayName);
    }

    [Fact]
    public async Task EnsureIndexAsync_RebuildsWhenSourceFingerprintChanges()
    {
        var sourcePath = CreateSourceFile("dict-b.mdx", "seed");
        var dictionary = CreateDictionary("mdx::b", "Dictionary B", sourcePath);
        string[] keys = ["apple"];
        var service = CreateQueryableService(
            dictionary.ServiceId,
            dictionary.DisplayName,
            sourcePath,
            () => keys);
        var sut = new LocalDictionaryIndexService(_tempRoot);

        await sut.EnsureIndexAsync(dictionary, service);
        (await sut.CompleteAsync("ap", [dictionary.ServiceId], 10))
            .Select(item => item.Key)
            .Should()
            .Equal("apple");

        keys = ["apple", "apricot"];
        File.AppendAllText(sourcePath, "|changed|");

        await sut.EnsureIndexAsync(dictionary, service);

        var results = await sut.CompleteAsync("ap", [dictionary.ServiceId], 10);
        results.Select(item => item.Key).Should().Equal("apple", "apricot");
    }

    [Fact]
    public async Task EnsureIndexAsync_SkipsUnconfiguredEncryptedDictionary_UntilKeysBecomeAvailable()
    {
        var sourcePath = CreateSourceFile("dict-c.mdx", "seed");
        var dictionary = CreateDictionary("mdx::c", "Dictionary C", sourcePath);
        var encryptedService = new MdxDictionaryTranslationService(
            dictionary.ServiceId,
            dictionary.DisplayName,
            sourcePath,
            isEncrypted: true);
        var sut = new LocalDictionaryIndexService(_tempRoot);

        await sut.EnsureIndexAsync(dictionary, encryptedService);

        Directory.Exists(Path.Combine(_tempRoot, Uri.EscapeDataString(dictionary.ServiceId))).Should().BeFalse();
        (await sut.CompleteAsync("ap", [dictionary.ServiceId], 10)).Should().BeEmpty();

        var configuredService = CreateQueryableService(
            dictionary.ServiceId,
            dictionary.DisplayName,
            sourcePath,
            () => ["apple", "apartment"]);

        await sut.EnsureIndexAsync(dictionary, configuredService);

        var results = await sut.CompleteAsync("ap", [dictionary.ServiceId], 10);
        results.Select(item => item.Key).Should().Equal("apartment", "apple");
    }

    [Fact]
    public async Task CompleteAsync_MergesServiceResultsInRequestedOrder_AndDeduplicatesKeys()
    {
        var sourceA = CreateSourceFile("dict-d-a.mdx", "a");
        var sourceB = CreateSourceFile("dict-d-b.mdx", "b");
        var dictionaryA = CreateDictionary("mdx::d:a", "Dictionary A", sourceA);
        var dictionaryB = CreateDictionary("mdx::d:b", "Dictionary B", sourceB);
        var serviceA = CreateQueryableService(
            dictionaryA.ServiceId,
            dictionaryA.DisplayName,
            sourceA,
            () => ["apple", "application", "apply"]);
        var serviceB = CreateQueryableService(
            dictionaryB.ServiceId,
            dictionaryB.DisplayName,
            sourceB,
            () => ["apple", "appendix"]);
        var sut = new LocalDictionaryIndexService(_tempRoot);

        await sut.EnsureIndexAsync(dictionaryA, serviceA);
        await sut.EnsureIndexAsync(dictionaryB, serviceB);

        var results = await sut.CompleteAsync("app", [dictionaryB.ServiceId, dictionaryA.ServiceId], 10);

        results.Select(item => item.Key).Should().Equal("appendix", "apple", "application", "apply");
        results[0].DictServiceId.Should().Be(dictionaryB.ServiceId);
        results.Single(item => item.Key == "apple").DictServiceId.Should().Be(dictionaryB.ServiceId);
    }

    [Fact]
    public async Task MatchAsync_UsesWildcardAcrossMultipleIndexes()
    {
        var sourceA = CreateSourceFile("dict-e-a.mdx", "a");
        var sourceB = CreateSourceFile("dict-e-b.mdx", "b");
        var dictionaryA = CreateDictionary("mdx::e:a", "Dictionary A", sourceA);
        var dictionaryB = CreateDictionary("mdx::e:b", "Dictionary B", sourceB);
        var serviceA = CreateQueryableService(
            dictionaryA.ServiceId,
            dictionaryA.DisplayName,
            sourceA,
            () => ["tealight", "teapot"]);
        var serviceB = CreateQueryableService(
            dictionaryB.ServiceId,
            dictionaryB.DisplayName,
            sourceB,
            () => ["teatime", "teatray"]);
        var sut = new LocalDictionaryIndexService(_tempRoot);

        await sut.EnsureIndexAsync(dictionaryA, serviceA);
        await sut.EnsureIndexAsync(dictionaryB, serviceB);

        var results = await sut.MatchAsync("tea*", [dictionaryA.ServiceId, dictionaryB.ServiceId], 10);

        results.Select(item => item.Key).Should().Equal("tealight", "teapot", "teatime", "teatray");
        results.Select(item => item.DictServiceId).Should().Equal(
            dictionaryA.ServiceId,
            dictionaryA.ServiceId,
            dictionaryB.ServiceId,
            dictionaryB.ServiceId);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort test cleanup.
        }
    }

    private string CreateSourceFile(string fileName, string content)
    {
        var sourcePath = Path.Combine(_tempRoot, fileName);
        File.WriteAllText(sourcePath, content);
        return sourcePath;
    }

    private static SettingsService.ImportedMdxDictionary CreateDictionary(
        string serviceId,
        string displayName,
        string filePath)
        => new()
        {
            ServiceId = serviceId,
            DisplayName = displayName,
            FilePath = filePath
        };

    private static MdxDictionaryTranslationService CreateQueryableService(
        string serviceId,
        string displayName,
        string filePath,
        Func<IEnumerable<string>> enumerateKeys)
        => new(
            serviceId,
            displayName,
            filePath,
            query => (query, $"<div>{query}</div>"),
            enumerateKeys);
}
