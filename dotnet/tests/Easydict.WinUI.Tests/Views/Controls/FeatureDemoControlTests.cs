using Easydict.WinUI.Models;
using Easydict.WinUI.Views.Controls;
using FluentAssertions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Xunit;

namespace Easydict.WinUI.Tests.Views.Controls;

[Trait("Category", "WinUI")]
public sealed class FeatureDemoControlTests : IDisposable
{
    private readonly List<Window> _windows = [];

    public void Dispose()
    {
        foreach (var window in _windows)
        {
            try
            {
                window.Close();
            }
            catch
            {
                // A test may already have closed its window.
            }
        }
    }

    [SkippableFact]
    public void SelectionDemo_ChangesVisibleContentAndCompletesOnlyAtTheFinalStep()
    {
        Skip.IfNot(WinUITestHelper.CanCreateWindow, WinUITestHelper.SkipReason);
        var control = CreateControl(QuickStartDemoKind.SelectionTranslation);
        control.AnimationsEnabledForTest = false;
        var completedCount = 0;
        control.DemoCompleted += (_, _) => completedCount++;

        var sample = FindByAutomationId<TextBlock>(control, "FeatureDemoSelectionSample");
        var action = FindByAutomationId<Button>(control, "FeatureDemoSelectionAction");
        var miniResult = FindByAutomationId<Border>(control, "FeatureDemoMiniTranslation");
        var stage = FindByAutomationId<Grid>(control, "FeatureDemoStage");

        sample.TextHighlighters.Should().BeEmpty("the first step is the plain reading view");
        action.Visibility.Should().Be(Visibility.Collapsed);
        miniResult.Visibility.Should().Be(Visibility.Collapsed);

        control.NextStep();
        control.CurrentStep.Should().Be(1);
        sample.TextHighlighters.Should().ContainSingle("the selected phrase is highlighted");
        stage.Opacity.Should().Be(1, "reduced motion disables the step fade");

        control.NextStep();
        control.CurrentStep.Should().Be(2);
        action.Visibility.Should().Be(Visibility.Visible);
        completedCount.Should().Be(0);

        control.NextStep();
        control.CurrentStep.Should().Be(3);
        miniResult.Visibility.Should().Be(Visibility.Visible);
        completedCount.Should().Be(0);

        control.NextStep();
        control.CurrentStep.Should().Be(4);
        control.IsCompleted.Should().BeTrue();
        sample.TextHighlighters.Should().BeEmpty("the sequence returns to the reading view");
        miniResult.Visibility.Should().Be(Visibility.Collapsed);
        completedCount.Should().Be(1);

        control.NextStep();
        completedCount.Should().Be(1, "completion is raised only once per run");
    }

    [SkippableFact]
    public void ScreenshotDemo_ShowsSelectionThenOcrThenTranslation()
    {
        Skip.IfNot(WinUITestHelper.CanCreateWindow, WinUITestHelper.SkipReason);
        var control = CreateControl(QuickStartDemoKind.ScreenshotTranslation);
        control.AnimationsEnabledForTest = false;
        var selection = FindByAutomationId<Border>(control, "FeatureDemoScreenshotSelection");
        var action = FindByAutomationId<Button>(control, "FeatureDemoScreenshotAction");
        var ocr = FindByAutomationId<Border>(control, "FeatureDemoOcrResult");
        var translation = FindByAutomationId<Border>(control, "FeatureDemoScreenshotTranslation");
        var sourceText = FindByAutomationId<TextBlock>(control, "FeatureDemoScreenshotSourceText");
        var ocrText = FindByAutomationId<TextBlock>(control, "FeatureDemoOcrText");

        selection.Visibility.Should().Be(Visibility.Collapsed);
        action.Visibility.Should().Be(Visibility.Collapsed);
        ocr.Visibility.Should().Be(Visibility.Collapsed);
        translation.Visibility.Should().Be(Visibility.Collapsed);

        control.NextStep();
        selection.Visibility.Should().Be(Visibility.Visible);

        control.NextStep();
        action.Visibility.Should().Be(Visibility.Visible);

        control.NextStep();
        action.Visibility.Should().Be(Visibility.Collapsed);
        ocr.Visibility.Should().Be(Visibility.Visible);
        Grid.GetColumnSpan(ocr).Should().Be(2, "the OCR result fills the result area until translation appears");
        ocrText.Text.Should().Be(sourceText.Text, "the demo OCR result uses the fixed screenshot text");

        control.NextStep();
        translation.Visibility.Should().Be(Visibility.Visible);
        Grid.GetColumnSpan(ocr).Should().Be(1);
        control.IsCompleted.Should().BeTrue();
    }

    [SkippableFact]
    public void PlaybackCanPauseReplayAndStopWithoutCompletingTheDemo()
    {
        Skip.IfNot(WinUITestHelper.CanCreateWindow, WinUITestHelper.SkipReason);
        var control = CreateControl(QuickStartDemoKind.SelectionTranslation);

        control.Play();
        control.IsPlaying.Should().BeTrue();
        control.Pause();
        control.IsPlaying.Should().BeFalse();
        control.CurrentStep.Should().Be(0);

        control.NextStep();
        control.CurrentStep.Should().Be(1);
        control.Replay();
        control.CurrentStep.Should().Be(0);
        control.IsPlaying.Should().BeTrue();
        control.IsCompleted.Should().BeFalse();

        control.Stop();
        control.CurrentStep.Should().Be(0);
        control.IsPlaying.Should().BeFalse();
        control.IsCompleted.Should().BeFalse();
    }

    [SkippableFact]
    public void UnloadingTheControlStopsPlaybackAndResetsTheDemo()
    {
        Skip.IfNot(WinUITestHelper.CanCreateWindow, WinUITestHelper.SkipReason);
        var control = CreateControl(QuickStartDemoKind.ScreenshotTranslation);
        control.NextStep();
        control.Play();
        control.IsPlaying.Should().BeTrue();

        var window = _windows[^1];
        window.Content = null;

        control.IsPlaying.Should().BeFalse();
        control.CurrentStep.Should().Be(0);
    }

    private FeatureDemoControl CreateControl(QuickStartDemoKind kind)
    {
        var control = new FeatureDemoControl { DemoKind = kind };
        var window = new Window { Content = control };
        _windows.Add(window);
        window.Activate();
        return control;
    }

    private static T FindByAutomationId<T>(DependencyObject root, string automationId)
        where T : FrameworkElement
        => TryFindByAutomationId<T>(root, automationId)
            ?? throw new InvalidOperationException($"Could not find WinUI element '{automationId}'.");

    private static T? TryFindByAutomationId<T>(DependencyObject root, string automationId)
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
            var match = TryFindByAutomationId<T>(VisualTreeHelper.GetChild(root, index), automationId);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }
}
