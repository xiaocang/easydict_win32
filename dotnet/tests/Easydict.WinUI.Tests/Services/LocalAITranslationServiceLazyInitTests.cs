using Easydict.OpenVINO.Services;
using Easydict.TranslationService;
using Easydict.WindowsAI.Services;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Asserts that wrapping the local AI sub-providers in Lazy&lt;T&gt; actually defers
/// their construction. A regression here means the lazy-init contract added in
/// TranslationManagerService.ConfigureServices is silently broken — sub-providers
/// would be created at app startup again.
/// </summary>
[Trait("Category", "WinUI")]
public sealed class LocalAITranslationServiceLazyInitTests
{
    [Fact]
    public void Ctor_WithLazyFactories_DoesNotMaterializeInnerProviders()
    {
        var phi = ThrowingLazy<PhiSilicaTranslationService>();
        var foundry = ThrowingLazy<IStreamTranslationService>();
        var openVino = ThrowingLazy<OpenVINOTranslationService>();

        using var svc = new LocalAITranslationService(phi, foundry, openVino);

        // Factories must not fire from the wrapper's constructor.
        phi.IsValueCreated.Should().BeFalse();
        foundry.IsValueCreated.Should().BeFalse();
        openVino.IsValueCreated.Should().BeFalse();

        svc.IsPhiSilicaMaterialized.Should().BeFalse();
        svc.IsFoundryLocalMaterialized.Should().BeFalse();
        svc.IsOpenVinoMaterialized.Should().BeFalse();
    }

    [Fact]
    public void Configure_DoesNotMaterializeInnerProviders()
    {
        var phi = ThrowingLazy<PhiSilicaTranslationService>();
        var foundry = ThrowingLazy<IStreamTranslationService>();
        var openVino = ThrowingLazy<OpenVINOTranslationService>();

        using var svc = new LocalAITranslationService(phi, foundry, openVino);
        svc.Configure(LocalAIProviderMode.WindowsAI);

        svc.IsPhiSilicaMaterialized.Should().BeFalse();
        svc.IsFoundryLocalMaterialized.Should().BeFalse();
        svc.IsOpenVinoMaterialized.Should().BeFalse();
    }

    [Fact]
    public void Dispose_WithoutAccess_DoesNotMaterializeInnerProviders()
    {
        var phi = ThrowingLazy<PhiSilicaTranslationService>();
        var foundry = ThrowingLazy<IStreamTranslationService>();
        var openVino = ThrowingLazy<OpenVINOTranslationService>();

        var svc = new LocalAITranslationService(phi, foundry, openVino);
        svc.Dispose();

        // Dispose must not touch .Value on a never-used Lazy — that would defeat
        // the lazy-init optimization that this Dispose path is designed to preserve.
        phi.IsValueCreated.Should().BeFalse();
        foundry.IsValueCreated.Should().BeFalse();
        openVino.IsValueCreated.Should().BeFalse();
    }

    // Lazy whose factory throws on materialization — proves the wrapper never
    // accesses .Value when it shouldn't.
    private static Lazy<T> ThrowingLazy<T>() =>
        new(() => throw new InvalidOperationException(
            $"Lazy<{typeof(T).Name}> materialized — lazy-init contract broken."));
}
