using Easydict.TranslationService;
using Easydict.TranslationService.LocalModels;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public sealed class LongDocumentServiceSupportTests
{
    [Fact]
    public void IsReadyForSelection_TreatsReadyLocalModelAsReadyWithoutServiceTestStatus()
    {
        var service = new FakeLocalModelTranslationService(
            "windows-local-ai",
            LocalModelState.Ready);

        var ready = LongDocumentServiceSupport.IsReadyForSelection(
            service,
            new Dictionary<string, bool>());

        ready.Should().BeTrue("local model readiness is tracked by provider status, not by service test state");
    }

    [Fact]
    public void IsReadyForSelection_TreatsUnpreparedLocalModelAsNotReady()
    {
        var service = new FakeLocalModelTranslationService(
            "windows-local-ai",
            LocalModelState.NeedsPreparation);

        var ready = LongDocumentServiceSupport.IsReadyForSelection(
            service,
            new Dictionary<string, bool>
            {
                [service.ServiceId] = true,
            });

        ready.Should().BeFalse("stale service test state must not hide current local model preparation state");
    }

    [Fact]
    public void IsReadyForSelection_UsesPersistedTestStatusForNonLocalServices()
    {
        var service = new FakeTranslationService("google");

        LongDocumentServiceSupport.IsReadyForSelection(service, new Dictionary<string, bool>())
            .Should().BeFalse();

        LongDocumentServiceSupport.IsReadyForSelection(
                service,
                new Dictionary<string, bool> { [service.ServiceId] = true })
            .Should().BeTrue();
    }

    private class FakeTranslationService : ITranslationService
    {
        public FakeTranslationService(string serviceId)
        {
            ServiceId = serviceId;
        }

        public string ServiceId { get; }
        public string DisplayName => ServiceId;
        public bool RequiresApiKey => false;
        public bool IsConfigured { get; init; } = true;
        public IReadOnlyList<Language> SupportedLanguages { get; } =
        [
            Language.English,
            Language.SimplifiedChinese,
        ];

        public bool SupportsLanguagePair(Language from, Language to)
        {
            return to != Language.Auto
                && (from == Language.Auto || SupportedLanguages.Contains(from))
                && SupportedLanguages.Contains(to);
        }

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = request.Text,
                DetectedLanguage = request.FromLanguage,
                TargetLanguage = request.ToLanguage,
                ServiceName = DisplayName,
            });
        }

        public Task<Language> DetectLanguageAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Language.Auto);
        }
    }

    private sealed class FakeLocalModelTranslationService : FakeTranslationService, ILocalModelProvider
    {
        private readonly LocalModelState _state;

        public FakeLocalModelTranslationService(string serviceId, LocalModelState state)
            : base(serviceId)
        {
            _state = state;
        }

        public event EventHandler<LocalModelStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public LocalModelStatus GetStatus()
        {
            return new LocalModelStatus(_state, "Test_Status");
        }

        public Task<LocalModelStatus> PrepareAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(GetStatus());
        }
    }
}
