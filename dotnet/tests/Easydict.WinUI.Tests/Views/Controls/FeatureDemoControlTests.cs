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

        var sample = FindByAutomationId<TextBox>(control, "FeatureDemoSelectionSample");
        var stage = FindByAutomationId<Grid>(control, "FeatureDemoStage");
        sample.IsReadOnly.Should().BeTrue();
        sample.SelectionLength.Should().Be(0);
        TryFindByAutomationId<Button>(control, "FeatureDemoSelectionAction").Should().BeNull("the actual popup is a separate native window");
        TryFindByAutomationId<Border>(control, "FeatureDemoMiniTranslation").Should().BeNull("Mini is no longer drawn as an inline card");
        control.NextStep();
        sample.SelectedText.Should().Be(sample.Text);
        stage.Opacity.Should().Be(1, "reduced motion disables the step fade");
        control.NextStep();
        completedCount.Should().Be(0);
        control.NextStep();
        completedCount.Should().Be(0);
        control.NextStep();
        control.IsCompleted.Should().BeTrue();
        sample.SelectionLength.Should().Be(0);
        completedCount.Should().Be(1);
        control.NextStep();
        completedCount.Should().Be(1, "completion is raised only once per run");
    }

    [SkippableFact]
    public void ScreenshotDemo_DescribesTheNativeSequenceWithoutDrawingSubstituteResults()
    {
        Skip.IfNot(WinUITestHelper.CanCreateWindow, WinUITestHelper.SkipReason);
        var control = CreateControl(QuickStartDemoKind.ScreenshotTranslation);
        var source = FindByAutomationId<TextBlock>(control, "FeatureDemoScreenshotSourceText");
        source.Text.Should().NotBeNullOrWhiteSpace();
        TryFindByAutomationId<Border>(control, "FeatureDemoScreenshotSelection").Should().BeNull();
        TryFindByAutomationId<Border>(control, "FeatureDemoOcrResult").Should().BeNull();
        TryFindByAutomationId<Border>(control, "FeatureDemoScreenshotTranslation").Should().BeNull();
        var descriptions = new HashSet<string>();
        for (var step = 0; step < 5; step++)
        {
            control.CurrentStep.Should().Be(step);
            descriptions.Add(FindByAutomationId<TextBlock>(control, "FeatureDemoStageHint").Text);
            if (step < 4) control.NextStep();
        }
        descriptions.Should().HaveCount(5, "each native operation has its own explanation");
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
        var control = new FeatureDemoControl { NativeWindowsEnabledForTest = false, DemoKind = kind };
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
