using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.WinUI.Services;
using Easydict.WinUI.Views.Controls;
using FluentAssertions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Xunit;

namespace Easydict.WinUI.Tests.Views.Controls;

[Trait("Category", "WinUI")]
public sealed class ServiceResultItemAiRefinementDemoTests
{
    [SkippableFact]
    public async Task LiveCandidates_RebuildWordRunsWhenTranslationTextIsUnchanged()
    {
        Skip.IfNot(WinUITestHelper.CanCreateWindow, WinUITestHelper.SkipReason);
        var settings = SettingsService.Instance;
        var previousContext = settings.AiContextLabEnabled;
        var previousWords = settings.AiWordRefinementLabEnabled;
        settings.AiContextLabEnabled = true;
        settings.AiWordRefinementLabEnabled = true;
        using var client = new HttpClient(new WordResponseHandler());
        var service = new MagpieService(client);
        service.Configure("http://127.0.0.1:4545/v1/chat/completions", "", "test-model");
        var translation = new TranslationResult
        {
            OriginalText = "A quiet space.", TranslatedText = "安静的空间。",
            TargetLanguage = Language.SimplifiedChinese, ServiceName = "Magpie"
        };
        using var state = AiRefinementDemoState.CreateLive(service, translation, true, true)!;
        var row = new ServiceQueryResult { ServiceId = "magpie", Result = translation, AiRefinementDemo = state };
        var card = new ServiceResultItem();
        var window = new Window { Content = card };
        window.Activate();
        try
        {
            card.ServiceResult = row;
            await FlushDispatcherAsync(card.DispatcherQueue);
            FindByNameOrNull<StackPanel>(card, "DemoSourcePanel").Should().BeNull();
            var header = FindByName<Border>(card, "HeaderBar");
            FindByAutomationId<Button>(header, "AiRefinementDemoContextDisclosure").Should().NotBeNull();
            var candidates = (Flyout)card.FindName("DemoCandidateFlyout");
            candidates.IsOpen.Should().BeFalse();
            var text = FindByAutomationId<RichTextBlock>(card, "AiRefinementDemoInteractiveTranslation");
            text.Blocks.OfType<Microsoft.UI.Xaml.Documents.Paragraph>().Single().Inlines.Should().HaveCount(1);
            await state.PrefetchCandidatesAsync();
            await FlushDispatcherAsync(card.DispatcherQueue);
            var runs = text.Blocks.OfType<Microsoft.UI.Xaml.Documents.Paragraph>().Single().Inlines.OfType<Microsoft.UI.Xaml.Documents.Run>().ToArray();
            runs.Should().HaveCount(2);
            runs[0].TextDecorations.Should().Be(Windows.UI.Text.TextDecorations.None);
            await state.SelectWordAsync("0");
            await FlushDispatcherAsync(card.DispatcherQueue);
            candidates.IsOpen.Should().BeTrue();
            (await state.ApplyCandidateAsync("0", "宁静的环境")).Should().BeTrue();
            await FlushDispatcherAsync(card.DispatcherQueue);
            row.Result!.TranslatedText.Should().Be("宁静的环境。");
        }
        finally
        {
            card.Cleanup();
            window.Close();
            settings.AiContextLabEnabled = previousContext;
            settings.AiWordRefinementLabEnabled = previousWords;
        }
    }

    private sealed class WordResponseHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var messages = body.RootElement.GetProperty("messages");
            using var input = System.Text.Json.JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
            var translation = input.RootElement.GetProperty("TranslatedText").GetString()!;
            var json = messages[0].GetProperty("content").GetString()!.Contains("MUST start", StringComparison.Ordinal)
                ? "宁静的环境。"
                : System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    new { Text = translation[..^1], Alternatives = new[] { "宁静的环境" } },
                    new { Text = "。", Alternatives = Array.Empty<string>() }
                });
            var chunk = System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = json } } } });
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("data: " + chunk + "\n\ndata: [DONE]\n\n", System.Text.Encoding.UTF8, "text/event-stream")
            };
        }
    }

    [SkippableFact]
    public async Task WordLayerWaitsForStreamingAndRequiresExplicitAiCapability()
    {
        Skip.IfNot(WinUITestHelper.CanCreateWindow, WinUITestHelper.SkipReason);

        using var state = new AiRefinementDemoState(
            "A quiet workspace helps ideas grow.",
            supportsAiExtensions: true,
            contextFeatureEnabled: true,
            wordRefinementEnabled: true);
        var result = new ServiceQueryResult
        {
            ServiceId = "openai",
            ServiceDisplayName = "AI demo (offline)",
            AiRefinementDemo = state,
            IsExpanded = true,
            IsLoading = true,
            IsStreaming = true,
            StreamingText = "安静的空间，"
        };
        var card = new ServiceResultItem();
        var window = new Window { Content = card };
        window.Activate();

        try
        {
            card.ServiceResult = result;
            var interactiveText = FindByAutomationId<RichTextBlock>(card, "AiRefinementDemoInteractiveTranslation");
            var contextDisclosure = FindByAutomationId<Button>(card, "AiRefinementDemoContextDisclosure");
            var candidateStatus = FindByName<TextBlock>(card, "DemoCandidateStatusText");

            interactiveText.Visibility.Should().Be(Visibility.Collapsed);
            contextDisclosure.Visibility.Should().Be(Visibility.Collapsed);
            candidateStatus.Visibility.Should().Be(Visibility.Collapsed);

            result.Result = new TranslationResult
            {
                OriginalText = state.SourceText,
                TranslatedText = state.TranslationText,
                TargetLanguage = Language.SimplifiedChinese,
                ServiceName = result.ServiceDisplayName,
                TimingMs = 240
            };
            result.IsStreaming = false;
            result.IsLoading = false;
            result.StreamingText = string.Empty;
            await FlushDispatcherAsync(card.DispatcherQueue);

            interactiveText.Visibility.Should().Be(Visibility.Visible);
            contextDisclosure.Visibility.Should().Be(Visibility.Visible);
            candidateStatus.Visibility.Should().Be(Visibility.Collapsed);

            await state.PrefetchCandidatesAsync();
            await state.SelectWordAsync("growth");
            state.ApplyCandidate("growth", "蓬勃发展").Should().BeTrue();
            state.ApplyContext("Use a formal tone.");
            await FlushDispatcherAsync(card.DispatcherQueue);

            var contextConflictPanel = FindByName<Border>(card, "DemoContextConflictPanel");
            var contextConflictText = FindByName<TextBlock>(card, "DemoContextConflictText");
            contextConflictPanel.Visibility.Should().Be(Visibility.Visible);
            contextConflictText.Text.Should().Contain("蓬勃发展").And.Contain("稳步发展");
            state.ResolveContextConflict(useSuggestedWording: true).Should().BeTrue();
            await FlushDispatcherAsync(card.DispatcherQueue);
            state.TranslationText.Should().Contain("稳步发展");
            contextConflictPanel.Visibility.Should().Be(Visibility.Collapsed);

            using var ordinaryServiceState = new AiRefinementDemoState(
                state.SourceText,
                supportsAiExtensions: false,
                contextFeatureEnabled: true,
                wordRefinementEnabled: true);
            result.AiRefinementDemo = ordinaryServiceState;
            await FlushDispatcherAsync(card.DispatcherQueue);

            interactiveText.Visibility.Should().Be(Visibility.Collapsed);
            contextDisclosure.Visibility.Should().Be(Visibility.Collapsed);
            candidateStatus.Visibility.Should().Be(Visibility.Collapsed);
            contextConflictPanel.Visibility.Should().Be(Visibility.Collapsed);
        }
        finally
        {
            card.Cleanup();
            window.Close();
        }
    }

    [SkippableFact]
    public async Task ContextOnlyFeatureUpdatesPlainTranslationTextWithoutWordLayer()
    {
        Skip.IfNot(WinUITestHelper.CanCreateWindow, WinUITestHelper.SkipReason);

        using var state = new AiRefinementDemoState(
            "A quiet workspace helps ideas grow.",
            supportsAiExtensions: true,
            contextFeatureEnabled: true,
            wordRefinementEnabled: false);
        var result = new ServiceQueryResult
        {
            ServiceId = "openai",
            ServiceDisplayName = "AI demo (offline)",
            AiRefinementDemo = state,
            IsExpanded = true,
            IsLoading = false,
            IsStreaming = false,
            Result = new TranslationResult
            {
                OriginalText = state.SourceText,
                TranslatedText = state.TranslationText,
                TargetLanguage = Language.SimplifiedChinese,
                ServiceName = "AI demo (offline)",
                TimingMs = 240
            }
        };
        var card = new ServiceResultItem();
        var window = new Window { Content = card };
        window.Activate();

        try
        {
            card.ServiceResult = result;
            await FlushDispatcherAsync(card.DispatcherQueue);

            var plainText = FindByName<TextBlock>(card, "ResultText");
            var interactiveText = FindByAutomationId<RichTextBlock>(card, "AiRefinementDemoInteractiveTranslation");
            var contextDisclosure = FindByAutomationId<Button>(card, "AiRefinementDemoContextDisclosure");
            var candidateStatus = FindByName<TextBlock>(card, "DemoCandidateStatusText");

            plainText.Text.Should().Be("安静的空间，让想法自由生长。");
            contextDisclosure.Visibility.Should().Be(Visibility.Visible);
            interactiveText.Visibility.Should().Be(Visibility.Collapsed);
            candidateStatus.Visibility.Should().Be(Visibility.Collapsed);

            state.ApplyContext("Use a formal tone.");
            await FlushDispatcherAsync(card.DispatcherQueue);

            var formalTranslation = "宁静的工作空间，使创意持续成长。";
            state.TranslationText.Should().Be(formalTranslation);
            result.Result!.TranslatedText.Should().Be(formalTranslation);
            plainText.Text.Should().Be(formalTranslation);
            interactiveText.Visibility.Should().Be(Visibility.Collapsed);
            candidateStatus.Visibility.Should().Be(Visibility.Collapsed);
        }
        finally
        {
            card.Cleanup();
            window.Close();
        }
    }

    private static T FindByName<T>(DependencyObject root, string name)
        where T : FrameworkElement =>
        FindByNameOrNull<T>(root, name)
        ?? throw new InvalidOperationException($"Could not find WinUI element named '{name}'.");

    private static T? FindByNameOrNull<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        if (root is FrameworkElement namedRoot && namedRoot.FindName(name) is T namedElement) return namedElement;
        if (root is FrameworkElement element
            && element is T typed
            && element.Name == name)
        {
            return typed;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var match = FindByNameOrNull<T>(VisualTreeHelper.GetChild(root, index), name);
            if (match is not null) return match;
        }

        return null;
    }

    private static async Task FlushDispatcherAsync(DispatcherQueue dispatcher)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() => completion.TrySetResult(true)))
        {
            throw new InvalidOperationException("Could not enqueue a WinUI dispatcher barrier.");
        }

        await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static T FindByAutomationId<T>(DependencyObject root, string automationId)
        where T : FrameworkElement =>
        FindByAutomationIdOrNull<T>(root, automationId)
        ?? throw new InvalidOperationException($"Could not find WinUI element '{automationId}'.");

    private static T? FindByAutomationIdOrNull<T>(DependencyObject root, string automationId)
        where T : FrameworkElement
    {
        if (root is FrameworkElement element
            && element is T typed
            && AutomationProperties.GetAutomationId(element) == automationId)
        {
            return typed;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var match = FindByAutomationIdOrNull<T>(VisualTreeHelper.GetChild(root, index), automationId);
            if (match is not null) return match;
        }

        return null;
    }
}
