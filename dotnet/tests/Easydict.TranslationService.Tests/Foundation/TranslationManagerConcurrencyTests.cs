using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Foundation;

public class TranslationManagerConcurrencyTests : IDisposable
{
    private readonly TranslationManager _manager = new();

    [Fact]
    public async Task RegisterAndUnregister_WhileEnumerating_DoesNotThrow()
    {
        using var cts = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            var iterations = 0;
            while (!cts.IsCancellationRequested)
            {
                foreach (var kv in _manager.Services)
                {
                    _ = kv.Value.DisplayName;
                }

                _ = _manager.IsStreamingService("google");
                iterations++;
            }

            return iterations;
        });

        for (var i = 0; i < 1000; i++)
        {
            var id = $"dyn-{i}";
            _manager.RegisterService(new PolicyTestService(id));
            _manager.UnregisterService(id).Should().BeTrue();
        }

        cts.Cancel();
        var loops = await reader;
        loops.Should().BeGreaterThan(0);
        _manager.Services.Keys.Should().NotContain(k => k.StartsWith("dyn-"));
    }

    [Fact]
    public void RegisterService_PreservesInsertionOrder()
    {
        var before = _manager.Services.Keys.ToList();
        _manager.RegisterService(new PolicyTestService("z-last"));
        _manager.RegisterService(new PolicyTestService("a-later"));

        var after = _manager.Services.Keys.ToList();
        after.Take(before.Count).Should().Equal(before);
        after.Skip(before.Count).Should().Equal("z-last", "a-later");
    }

    [Fact]
    public void UnregisterService_UnknownId_ReturnsFalse()
    {
        _manager.UnregisterService("missing").Should().BeFalse();
    }

    [Fact]
    public void RegisterService_SameId_ReplacesInPlace()
    {
        var first = new PolicyTestService("dup");
        var second = new PolicyTestService("dup");
        _manager.RegisterService(first);
        var indexBefore = _manager.Services.Keys.ToList().IndexOf("dup");

        _manager.RegisterService(second);

        _manager.Services["dup"].Should().BeSameAs(second);
        _manager.Services.Keys.ToList().IndexOf("dup").Should().Be(indexBefore);
    }

    public void Dispose() => _manager.Dispose();
}
