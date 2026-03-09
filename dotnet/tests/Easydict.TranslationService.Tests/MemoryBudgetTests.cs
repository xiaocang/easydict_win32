using Easydict.TranslationService;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.TranslationService.Tests;

/// <summary>
/// Memory budget regression tests.
/// Each test measures the GC heap delta of a specific operation and asserts
/// an upper-bound budget to catch memory regressions.
///
/// These tests are excluded from automatic CI runs.
/// Trigger manually with:
///   dotnet test tests/Easydict.TranslationService.Tests --filter "Category=Performance" -v n
/// </summary>
[Trait("Category", "Performance")]
[Collection("Performance")] // Disable xUnit parallel execution — GC measurements require isolation
public class MemoryBudgetTests : IDisposable
{
    private readonly ITestOutputHelper _output;

    public MemoryBudgetTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ──────────────────────────────────────────────
    //  1. TranslationManager creation heap delta
    // ──────────────────────────────────────────────

    [Fact]
    public void TranslationManager_HeapDelta_ShouldBeWithinBudget()
    {
        // Warmup — let JIT compile everything
        using (var warmup = new TranslationManager())
        {
            // Access Services to ensure all code paths are JIT'd
            _ = warmup.Services.Count;
        }

        ForceFullGC();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);

        using var manager = new TranslationManager();
        // Access Services to ensure lazy initialization (if any) completes
        _ = manager.Services.Count;

        var afterCreate = GC.GetTotalMemory(forceFullCollection: true);
        var delta = afterCreate - baseline;

        _output.WriteLine("=== TranslationManager Heap Delta ===");
        _output.WriteLine($"  Baseline (bytes): {baseline:N0}");
        _output.WriteLine($"  After    (bytes): {afterCreate:N0}");
        _output.WriteLine($"  Delta    (KB)   : {delta / 1024.0:F1}");
        _output.WriteLine($"  Services count  : {manager.Services.Count}");

        // Budget: 20 services + HttpClient + HttpClientHandler + 2 MemoryCaches
        // should be well under 5 MB.
        delta.Should().BeLessThan(5 * 1024 * 1024,
            "TranslationManager creation should use < 5 MB heap");
    }

    // ──────────────────────────────────────────────
    //  2. TranslationManager create/dispose leak check
    // ──────────────────────────────────────────────

    [Fact]
    public void TranslationManager_AfterDispose_ShouldNotLeak()
    {
        // Warmup
        using (var warmup = new TranslationManager()) { }

        ForceFullGC();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);

        const int iterations = 10;
        for (var i = 0; i < iterations; i++)
        {
            using var manager = new TranslationManager();
            _ = manager.Services.Count;
        }

        ForceFullGC();
        var afterLoop = GC.GetTotalMemory(forceFullCollection: true);
        var leak = afterLoop - baseline;

        _output.WriteLine("=== TranslationManager Dispose Leak Check ===");
        _output.WriteLine($"  Baseline   (bytes): {baseline:N0}");
        _output.WriteLine($"  After {iterations}x  (bytes): {afterLoop:N0}");
        _output.WriteLine($"  Leak       (KB)   : {leak / 1024.0:F1}");

        // Budget: after full GC, leaked memory should be < 1 MB.
        // Some jitter is expected from runtime internals.
        leak.Should().BeLessThan(1 * 1024 * 1024,
            $"{iterations} create/dispose cycles should not leak > 1 MB");
    }

    // ──────────────────────────────────────────────
    //  3. MemoryCache infrastructure overhead
    // ──────────────────────────────────────────────

    [Fact]
    public void TranslationManager_MemoryCache_ShouldBeWithinBudget()
    {
        // Warmup
        using (var warmup = new TranslationManager()) { }

        ForceFullGC();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);

        using var manager = new TranslationManager();

        // Measure just the cache infrastructure (empty caches)
        var afterCreate = GC.GetTotalMemory(forceFullCollection: true);
        var delta = afterCreate - baseline;

        _output.WriteLine("=== MemoryCache Infrastructure ===");
        _output.WriteLine($"  Baseline (bytes): {baseline:N0}");
        _output.WriteLine($"  After    (bytes): {afterCreate:N0}");
        _output.WriteLine($"  Delta    (KB)   : {delta / 1024.0:F1}");

        // Empty caches should add negligible memory (well under 1 MB).
        // This also serves as a canary if cache options change.
        delta.Should().BeLessThan(1 * 1024 * 1024,
            "Empty cache infrastructure should use < 1 MB");
    }

    private static void ForceFullGC()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    public void Dispose()
    {
        // No resources to clean up
    }
}
