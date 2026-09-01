using Easydict.TranslationService.Services.ModelCatalog;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public class ModelCatalogCacheServiceTests : IDisposable
{
    private readonly string _cacheDir;
    private readonly ModelCatalogCacheService _service;

    public ModelCatalogCacheServiceTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), $"easydict_test_model_catalog_{Guid.NewGuid():N}");
        _service = new ModelCatalogCacheService(_cacheDir, TimeSpan.FromHours(24));
    }

    public void Dispose()
    {
        try { Directory.Delete(_cacheDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task TryGetFreshAsync_ReturnsNull_WhenNothingCached()
    {
        var result = await _service.TryGetFreshAsync("openrouter");
        result.Should().BeNull();
    }

    [Fact]
    public async Task TryGetAnyAsync_ReturnsNull_WhenNothingCached()
    {
        var result = await _service.TryGetAnyAsync("openrouter");
        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_ThenTryGetFreshAsync_RoundTrips()
    {
        var models = new[]
        {
            new ModelCatalogEntry("openrouter/free", "Free Router", true, null),
            new ModelCatalogEntry("openai/gpt-5.4", "GPT-5.4", false, 200000),
        };

        await _service.SaveAsync("openrouter", models);
        var result = await _service.TryGetFreshAsync("openrouter");

        result.Should().NotBeNull();
        result!.Should().HaveCount(2);
        result[0].Id.Should().Be("openrouter/free");
        result[0].IsFree.Should().BeTrue();
        result[1].ContextLength.Should().Be(200000);
    }

    [Fact]
    public async Task TryGetFreshAsync_ReturnsNull_WhenExpired()
    {
        var expiringSoon = new ModelCatalogCacheService(_cacheDir, TimeSpan.Zero);
        await expiringSoon.SaveAsync("openrouter", new[] { new ModelCatalogEntry("a/b", null, false, null) });

        // TimeSpan.Zero TTL: even an instant later, the entry is stale.
        await Task.Delay(10);
        var result = await expiringSoon.TryGetFreshAsync("openrouter");

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryGetAnyAsync_ReturnsStaleEntry_WhenExpired()
    {
        var expiringSoon = new ModelCatalogCacheService(_cacheDir, TimeSpan.Zero);
        await expiringSoon.SaveAsync("openrouter", new[] { new ModelCatalogEntry("a/b", null, false, null) });
        await Task.Delay(10);

        var fresh = await expiringSoon.TryGetFreshAsync("openrouter");
        var any = await expiringSoon.TryGetAnyAsync("openrouter");

        fresh.Should().BeNull();
        any.Should().NotBeNull();
        any!.Single().Id.Should().Be("a/b");
    }

    [Fact]
    public async Task TryGetFreshAsync_ReturnsNull_WhenCacheFileIsCorrupt()
    {
        Directory.CreateDirectory(_cacheDir);
        await File.WriteAllTextAsync(Path.Combine(_cacheDir, "openrouter.json"), "{ not valid json");

        var result = await _service.TryGetFreshAsync("openrouter");

        result.Should().BeNull();
    }

    [Fact]
    public async Task DifferentServiceIds_DoNotShareCacheEntries()
    {
        await _service.SaveAsync("openrouter", new[] { new ModelCatalogEntry("openrouter/free", null, true, null) });

        var orcarouter = await _service.TryGetFreshAsync("orcarouter");

        orcarouter.Should().BeNull();
    }
}
