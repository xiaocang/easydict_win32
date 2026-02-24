using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public class TranslationCacheServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly TranslationCacheService _service;

    public TranslationCacheServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"easydict_test_cache_{Guid.NewGuid():N}.db");
        _service = new TranslationCacheService(_dbPath);
    }

    [Fact]
    public async Task TryGet_ReturnsNull_WhenEmpty()
    {
        var result = await _service.TryGetAsync("google", Language.English, Language.SimplifiedChinese, "abc123");
        result.Should().BeNull();
    }

    [Fact]
    public async Task Set_ThenGet_ReturnsTranslation()
    {
        var hash = TranslationCacheService.ComputeHash("Hello world");
        await _service.SetAsync("google", Language.English, Language.SimplifiedChinese, hash, "Hello world", "你好世界");

        var result = await _service.TryGetAsync("google", Language.English, Language.SimplifiedChinese, hash);
        result.Should().Be("你好世界");
    }

    [Fact]
    public async Task DifferentService_DifferentCacheEntry()
    {
        var hash = TranslationCacheService.ComputeHash("Hello");
        await _service.SetAsync("google", Language.English, Language.SimplifiedChinese, hash, "Hello", "你好(Google)");
        await _service.SetAsync("deepl", Language.English, Language.SimplifiedChinese, hash, "Hello", "你好(DeepL)");

        var googleResult = await _service.TryGetAsync("google", Language.English, Language.SimplifiedChinese, hash);
        var deeplResult = await _service.TryGetAsync("deepl", Language.English, Language.SimplifiedChinese, hash);

        googleResult.Should().Be("你好(Google)");
        deeplResult.Should().Be("你好(DeepL)");
    }

    [Fact]
    public async Task DifferentLanguagePair_DifferentCacheEntry()
    {
        var hash = TranslationCacheService.ComputeHash("Hello");
        await _service.SetAsync("google", Language.English, Language.SimplifiedChinese, hash, "Hello", "你好");
        await _service.SetAsync("google", Language.English, Language.Japanese, hash, "Hello", "こんにちは");

        var zhResult = await _service.TryGetAsync("google", Language.English, Language.SimplifiedChinese, hash);
        var jaResult = await _service.TryGetAsync("google", Language.English, Language.Japanese, hash);

        zhResult.Should().Be("你好");
        jaResult.Should().Be("こんにちは");
    }

    [Fact]
    public async Task Clear_RemovesAllEntries()
    {
        var hash = TranslationCacheService.ComputeHash("Hello");
        await _service.SetAsync("google", Language.English, Language.SimplifiedChinese, hash, "Hello", "你好");

        var countBefore = await _service.GetEntryCountAsync();
        countBefore.Should().Be(1);

        await _service.ClearAsync();

        var countAfter = await _service.GetEntryCountAsync();
        countAfter.Should().Be(0);
    }

    [Fact]
    public async Task GetEntryCount_ReturnsCorrectCount()
    {
        await _service.SetAsync("google", Language.English, Language.SimplifiedChinese,
            TranslationCacheService.ComputeHash("A"), "A", "甲");
        await _service.SetAsync("google", Language.English, Language.SimplifiedChinese,
            TranslationCacheService.ComputeHash("B"), "B", "乙");
        await _service.SetAsync("deepl", Language.English, Language.SimplifiedChinese,
            TranslationCacheService.ComputeHash("A"), "A", "甲");

        var count = await _service.GetEntryCountAsync();
        count.Should().Be(3);
    }

    [Fact]
    public async Task Set_Upsert_UpdatesExistingEntry()
    {
        var hash = TranslationCacheService.ComputeHash("Hello");
        await _service.SetAsync("google", Language.English, Language.SimplifiedChinese, hash, "Hello", "你好v1");
        await _service.SetAsync("google", Language.English, Language.SimplifiedChinese, hash, "Hello", "你好v2");

        var result = await _service.TryGetAsync("google", Language.English, Language.SimplifiedChinese, hash);
        result.Should().Be("你好v2");

        var count = await _service.GetEntryCountAsync();
        count.Should().Be(1);
    }

    [Fact]
    public void ComputeHash_IsDeterministic()
    {
        var h1 = TranslationCacheService.ComputeHash("test string");
        var h2 = TranslationCacheService.ComputeHash("test string");
        h1.Should().Be(h2);
    }

    [Fact]
    public void ComputeHash_DifferentInputs_DifferentHashes()
    {
        var h1 = TranslationCacheService.ComputeHash("Hello");
        var h2 = TranslationCacheService.ComputeHash("World");
        h1.Should().NotBe(h2);
    }

    public void Dispose()
    {
        _service.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
