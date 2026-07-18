using Easydict.UIAutomation.Tests.Infrastructure;
using FluentAssertions;
using FlaUI.Core.Definitions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using System.Drawing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;

namespace Easydict.UIAutomation.Tests.Tests;

[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public class SettingsPageTests : IDisposable
{
    private readonly record struct MemorySample(double WorkingSetMb, double PrivateMb, double PagedMb);
    private readonly record struct SettingsTabSwitchCase(string TabAutomationId, string ExpectedSelectedTab);

    private readonly AppLauncher _launcher;
    private readonly ITestOutputHelper _output;
    private readonly string _abMode;
    private static readonly TimeSpan ImmediateMouseResponseBudget = TimeSpan.FromSeconds(1);

    public SettingsPageTests(ITestOutputHelper output)
    {
        _output = output;
        _abMode = ResolveAbMode();
        _output.WriteLine($"[MemoryLoop] A/B mode={_abMode}");
        _launcher = new AppLauncher();
        _launcher.LaunchAuto(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void SettingsPage_ShouldOpenFromMainWindow()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);


        var settingsButton = WaitForSettingsButton(window, TimeSpan.FromSeconds(10));

        if (settingsButton != null)
        {
            ClickElement(settingsButton, "SettingsPage.SettingsButton");
            WaitForSettingsScrollViewer(window, TimeSpan.FromSeconds(15))
                .Should()
                .NotBeNull("Settings should open inside the main window");

            var path = ScreenshotHelper.CaptureWindow(window, "05_settings_page");
            _output.WriteLine($"Screenshot saved: {path}");

            var result = VisualRegressionHelper.CompareWithBaseline(path, "05_settings_page");
            if (result != null)
            {
                _output.WriteLine(result.ToString());
                result.Passed.Should().BeTrue(result.ToString());
            }
        }
        else
        {
            _output.WriteLine("SettingsButton not found - capturing window for inspection");
            DumpButtonDiagnostics(window, "SettingsPage_ShouldOpenFromMainWindow");
            ScreenshotHelper.CaptureWindow(window, "05_settings_button_not_found");
        }
    }

    [Fact]
    public void SettingsPage_TtsVoiceRefresh_ShouldRemainUsable()
    {
        var window = _launcher.GetMainWindow();
        ScreenshotHelper.TrySetWindowPhysicalBounds(
                window,
                new Rectangle(0, 0, 600, 700))
            .Should()
            .BeTrue("the TTS screenshot requires a deterministic on-screen window");
        Thread.Sleep(2000);

        var settingsButton = WaitForSettingsButton(window, TimeSpan.FromSeconds(10));
        settingsButton.Should().NotBeNull("SettingsButton must exist on the main window");
        ClickElement(settingsButton!, "TtsVoice.SettingsButton");

        var scrollViewer = WaitForSettingsScrollViewer(window, TimeSpan.FromSeconds(15));
        scrollViewer.Should().NotBeNull("Settings should open before testing TTS voices");
        var generalTab = Retry.WhileNull(
            () => FindRenderedByAutomationId(window, "SettingsTab_General"),
            TimeSpan.FromSeconds(10)).Result;
        generalTab.Should().NotBeNull("the General settings tab should be available");
        window.SetForeground();
        ClickElementWithMouse(generalTab!, "TtsVoice.GeneralTab");
        Retry.WhileNull(
                () => FindRenderedByAutomationId(
                    window,
                    "SettingsGeneralBehaviorHeader",
                    scrollViewer),
                TimeSpan.FromSeconds(10))
            .Result
            .Should()
            .NotBeNull("clicking General should reveal its settings content");
        Thread.Sleep(800);

        var viewportBounds = scrollViewer!.BoundingRectangle;
        AutomationElement? refreshButton = null;
        var voiceCombo = ScrollHelper.ScrollToFind(
            scrollViewer,
            startPercent: 70,
            () =>
            {
                var combo = FindRenderedByAutomationId(window, "TtsVoiceCombo", scrollViewer);
                var refresh = FindRenderedByAutomationId(
                    window,
                    "TtsVoiceRefreshButton",
                    scrollViewer);
                if (combo?.IsOffscreen != false || refresh?.IsOffscreen != false)
                {
                    return null;
                }

                var comboBounds = combo.BoundingRectangle;
                var refreshBounds = refresh.BoundingRectangle;
                if (comboBounds.Left < viewportBounds.Left ||
                    comboBounds.Right > viewportBounds.Right ||
                    comboBounds.Top < viewportBounds.Top ||
                    comboBounds.Bottom > viewportBounds.Bottom ||
                    refreshBounds.Left < viewportBounds.Left ||
                    refreshBounds.Right > viewportBounds.Right ||
                    refreshBounds.Top < viewportBounds.Top ||
                    refreshBounds.Bottom > viewportBounds.Bottom)
                {
                    return null;
                }

                refreshButton = refresh;
                return combo;
            },
            _output.WriteLine);
        voiceCombo.Should().NotBeNull("the TTS voice selector should be visible");
        refreshButton.Should().NotBeNull("the TTS voice refresh button should be visible");
        voiceCombo!.IsOffscreen.Should().BeFalse("the TTS voice selector must be user-visible");
        refreshButton!.IsOffscreen.Should().BeFalse("the refresh button must be user-visible");
        var voiceComboBounds = voiceCombo!.BoundingRectangle;
        var refreshButtonBounds = refreshButton!.BoundingRectangle;
        _output.WriteLine(
            $"TTS row bounds: combo={voiceComboBounds}, refresh={refreshButtonBounds}");
        var voiceComboCenterY = voiceComboBounds.Top + voiceComboBounds.Height / 2;
        var refreshButtonCenterY = refreshButtonBounds.Top + refreshButtonBounds.Height / 2;
        Math.Abs(voiceComboCenterY - refreshButtonCenterY)
            .Should()
            .BeLessOrEqualTo(2, "the TTS voice selector and refresh button should be centered on the same row");
        var captureWindowBounds = ScreenshotHelper.GetWindowPhysicalBounds(window);
        ScreenshotHelper.TrySetWindowPhysicalBounds(
                window,
                new Rectangle(
                    captureWindowBounds.Left,
                    -500,
                    captureWindowBounds.Width,
                    captureWindowBounds.Height))
            .Should()
            .BeTrue("the TTS settings row must be positioned inside the physical capture");
        Thread.Sleep(500);
        voiceCombo = Retry.WhileNull(
            () => FindRenderedByAutomationId(window, "TtsVoiceCombo", scrollViewer),
            TimeSpan.FromSeconds(10)).Result;
        refreshButton = Retry.WhileNull(
            () => FindRenderedByAutomationId(window, "TtsVoiceRefreshButton", scrollViewer),
            TimeSpan.FromSeconds(10)).Result;
        voiceCombo.Should().NotBeNull("the shifted TTS voice selector must remain visible");
        refreshButton.Should().NotBeNull("the shifted TTS refresh button must remain visible");
        var alignmentPath = ScreenshotHelper.CaptureElementsPhysical(
            window,
            "18_settings_tts_voice_alignment",
            40,
            voiceCombo!,
            refreshButton!);
        _output.WriteLine($"Screenshot saved: {alignmentPath}");

        ClickElementWithMouse(refreshButton!, "TtsVoice.RefreshButton");

        var refreshedVoiceCombo = Retry.WhileNull(
            () =>
            {
                var currentCombo = FindRenderedByAutomationId(
                    window, "TtsVoiceCombo", scrollViewer);
                var currentRefresh = FindRenderedByAutomationId(
                    window, "TtsVoiceRefreshButton", scrollViewer);
                return currentCombo?.IsEnabled == true && currentRefresh?.IsEnabled == true
                    ? currentCombo
                    : null;
            },
            TimeSpan.FromSeconds(15)).Result;
        refreshedVoiceCombo
            .Should()
            .NotBeNull("voice refresh should always restore enabled controls");

        Thread.Sleep(800);
        ScrollHelper.ScrollToPercent(scrollViewer!, 100, _output.WriteLine);
        Thread.Sleep(800);

        var voiceComboControl = refreshedVoiceCombo!.AsComboBox();
        voiceComboControl.Should().NotBeNull("the TTS voice selector should expose ComboBox");
        voiceComboControl!.Expand();
        Thread.Sleep(400);
        var voiceItems = voiceComboControl.Items;
        voiceItems.Should().HaveCountGreaterThan(
            1,
            "Windows should expose Auto plus at least one installed TTS voice");
        var selectedIndex = Array.FindIndex(
            voiceItems,
            item => item.Patterns.SelectionItem.PatternOrDefault?.IsSelected.Value == true);
        voiceComboControl.Select(selectedIndex == 0 ? 1 : 0);

        Retry.WhileNull(
                () => FindRenderedByAutomationId(window, "SaveButton"),
                TimeSpan.FromSeconds(5))
            .Result
            .Should()
            .NotBeNull("changing the selected TTS voice must reveal Save Settings");

        var previewButton = Retry.WhileNull(
            () => FindRenderedByAutomationId(window, "TtsVoicePreviewButton", scrollViewer),
            TimeSpan.FromSeconds(10)).Result;
        previewButton.Should().NotBeNull("a fixed-example TTS playback button should be available");
        previewButton!.Name.Should().NotBeNullOrWhiteSpace(
            "the TTS preview button should have a localized accessible name");
        ClickElement(previewButton, "TtsVoice.PreviewButton");
        Thread.Sleep(500);


    }

    [Fact]
    public void SettingsPage_TtsChange_ShouldRequireSaveAndDiscardCleanly()
    {
        var window = _launcher.GetMainWindow();
        window.SetForeground();
        Thread.Sleep(2000);

        var settingsButton = WaitForSettingsButton(window, TimeSpan.FromSeconds(10));
        settingsButton.Should().NotBeNull("SettingsButton must exist on the main window");
        ClickElement(settingsButton!, "TtsSave.SettingsButton");

        var scrollViewer = WaitForSettingsScrollViewer(window, TimeSpan.FromSeconds(15));
        scrollViewer.Should().NotBeNull("Settings should open before changing TTS");
        var generalTab = Retry.WhileNull(
            () => FindRenderedByAutomationId(window, "SettingsTab_General"),
            TimeSpan.FromSeconds(10)).Result;
        generalTab.Should().NotBeNull("the General settings tab should be available");
        ClickElementWithMouse(generalTab!, "TtsSave.GeneralTab");

        var speedSlider = ScrollHelper.ScrollToFind(
            scrollViewer!,
            70,
            () => FindRenderedByAutomationId(window, "TtsSpeedSlider", scrollViewer),
            _output.WriteLine);
        speedSlider.Should().NotBeNull("the TTS speed slider should be rendered");

        var rangeValue = speedSlider!.Patterns.RangeValue.PatternOrDefault;
        rangeValue.Should().NotBeNull("the TTS speed slider should expose RangeValue");
        var originalValue = rangeValue!.Value.Value;
        var targetValue = Math.Abs(originalValue - rangeValue.Minimum.Value) < 0.01
            ? rangeValue.Maximum.Value
            : rangeValue.Minimum.Value;
        rangeValue.SetValue(targetValue);

        Retry.WhileNull(
                () => FindRenderedByAutomationId(window, "SaveButton"),
                TimeSpan.FromSeconds(5))
            .Result
            .Should()
            .NotBeNull("changing TTS must reveal Save Settings");

        var backButton = WaitForBackButton(window, TimeSpan.FromSeconds(10));
        backButton.Should().NotBeNull("settings should expose a back button");
        ClickElement(backButton!, "TtsSave.BackButton");

        var discardButton = Retry.WhileNull(
            () => FindRenderedByAutomationId(window, "SecondaryButton"),
            TimeSpan.FromSeconds(10)).Result;
        discardButton.Should().NotBeNull("discarding unsaved TTS changes should be offered");
        ClickElement(discardButton!, "TtsSave.DiscardButton");
        Thread.Sleep(1500);
        window = _launcher.GetMainWindow();

        var reopenedSettingsButton = WaitForSettingsButton(window, TimeSpan.FromSeconds(10));
        reopenedSettingsButton.Should().NotBeNull("discard should return to the main page");
        ClickElementWithMouse(reopenedSettingsButton!, "TtsSave.ReopenSettingsButton");

        var reopenedScrollViewer = WaitForSettingsScrollViewer(window, TimeSpan.FromSeconds(15));
        reopenedScrollViewer.Should().NotBeNull("settings should reopen after discarding");
        var reopenedGeneralTab = Retry.WhileNull(
            () => FindRenderedByAutomationId(window, "SettingsTab_General"),
            TimeSpan.FromSeconds(10)).Result;
        reopenedGeneralTab.Should().NotBeNull("the General tab should remain available");
        ClickElementWithMouse(reopenedGeneralTab!, "TtsSave.ReopenedGeneralTab");

        var reopenedSpeedSlider = ScrollHelper.ScrollToFind(
            reopenedScrollViewer!,
            70,
            () => FindRenderedByAutomationId(
                window,
                "TtsSpeedSlider",
                reopenedScrollViewer),
            _output.WriteLine);
        reopenedSpeedSlider.Should().NotBeNull("the TTS speed slider should render after reopening");
        reopenedSpeedSlider!.Patterns.RangeValue.Pattern.Value.Value
            .Should()
            .BeApproximately(
                originalValue,
                0.01,
                "discarded TTS changes must not become effective");
    }

    [Fact]
    public void SettingsPage_ShouldShowServiceConfiguration()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        var settingsButton = WaitForSettingsButton(window, TimeSpan.FromSeconds(10));

        if (settingsButton != null)
        {
            settingsButton.Click();
            WaitForSettingsScrollViewer(window, TimeSpan.FromSeconds(15))
                .Should()
                .NotBeNull("Settings should open inside the main window");

            // Try to scroll down to see service configuration
            var scrollViewer = window.FindFirstDescendant(cf => cf.ByAutomationId("MainScrollViewer"));
            if (scrollViewer != null)
            {
                // Scroll to Enabled Services section (~12%)
                ScrollHelper.ScrollToPercent(scrollViewer, 12, _output.WriteLine);

                var path = ScreenshotHelper.CaptureWindow(window, "06_settings_services");
                _output.WriteLine($"Screenshot saved: {path}");

                // Scroll to API keys area (~50%)
                ScrollHelper.ScrollToPercent(scrollViewer, 50, _output.WriteLine);

                path = ScreenshotHelper.CaptureWindow(window, "07_settings_api_keys");
                _output.WriteLine($"Screenshot saved: {path}");
            }
        }
        else
        {
            _output.WriteLine("SettingsButton not found");
            DumpButtonDiagnostics(window, "SettingsPage_ShouldShowServiceConfiguration");
        }
    }

    [Fact]
    public void SettingsPage_ShouldAcceptImmediateScrollAfterContentVisible()
    {
        var window = _launcher.GetMainWindow();
        window.SetForeground();
        Thread.Sleep(2000);

        var settingsButton = WaitForSettingsButton(window, TimeSpan.FromSeconds(10));
        settingsButton.Should().NotBeNull("SettingsButton must exist on main window");

        ClickElement(settingsButton!, "ImmediateScroll.SettingsButton");

        var scrollViewer = WaitForSettingsScrollViewer(window, TimeSpan.FromSeconds(15));
        scrollViewer.Should().NotBeNull("MainScrollViewer should be visible as soon as Settings content is interactive");
        window.SetForeground();
        Thread.Sleep(250);

        var scrollPattern = scrollViewer!.Patterns.Scroll.PatternOrDefault;
        scrollPattern.Should().NotBeNull("Settings MainScrollViewer should expose ScrollPattern");
        scrollPattern!.VerticallyScrollable.Value.Should().BeTrue("Settings should be scrollable for immediate wheel input");

        scrollPattern.SetScrollPercent(-1, 0);
        Thread.Sleep(100);
        ScrollHelper.TryGetVerticalScrollPercent(scrollViewer, out var before);
        MoveMouseToScrollGutter(scrollViewer);
        Mouse.Click(GetScrollGutterPoint(scrollViewer));

        var wheelDelta = before > 80 ? 4 : -4;
        var wheelScrolled = WaitForVerticalPercentChange(
            scrollViewer,
            before,
            TimeSpan.FromSeconds(2),
            pollIntervalMs: 150,
            onTick: () => Mouse.Scroll(wheelDelta));
        if (!wheelScrolled)
        {
            var targetPercent = before > 50 ? 0 : Math.Min(100, before + 20);
            scrollPattern.SetScrollPercent(-1, targetPercent);
        }

        (wheelScrolled || WaitForVerticalPercentChange(scrollViewer, before, TimeSpan.FromSeconds(2)))
            .Should().BeTrue("Settings scrolling should be handled immediately after content becomes visible");
    }

    [Fact]
    public void SettingsPage_ShouldLoadUnwarmedTabWhenClickedImmediately()
    {
        var window = _launcher.GetMainWindow();
        window.SetForeground();
        Thread.Sleep(2000);

        var settingsButton = WaitForSettingsButton(window, TimeSpan.FromSeconds(10));
        settingsButton.Should().NotBeNull("SettingsButton must exist on main window");

        ClickElement(settingsButton!, "ImmediateTab.SettingsButton");

        WaitForSettingsScrollViewer(window, TimeSpan.FromSeconds(15))
            .Should().NotBeNull("Settings content should become visible before tab interaction");

        var viewsTab = Retry.WhileNull(
                () => FindVisibleByAutomationId(window, "SettingsTab_Views"),
                TimeSpan.FromSeconds(10))
            .Result;
        viewsTab.Should().NotBeNull("Views tab should be available immediately after Settings opens");

        ClickElement(viewsTab!, "ImmediateTab.Views");

        Retry.WhileNull(
                () => FindVisibleByAutomationId(window, "MainWindowReorderModeButton"),
                TimeSpan.FromSeconds(15))
            .Result
            .Should()
            .NotBeNull("clicking the preloaded Views tab should show its content");
    }

    [Fact]
    public void SettingsPage_FirstEntry_ShouldAcceptImmediatePhysicalMouseTabClickAndShowReaction()
    {
        var window = _launcher.GetMainWindow();
        window.SetForeground();
        Thread.Sleep(2000);

        var settingsButton = WaitForSettingsButton(window, TimeSpan.FromSeconds(10));
        settingsButton.Should().NotBeNull("SettingsButton must exist on main window");

        ClickElementWithMouse(settingsButton!, "FirstEntryMouse.SettingsButton");

        WaitForSettingsScrollViewer(window, TimeSpan.FromSeconds(15))
            .Should()
            .NotBeNull("Settings content should become visible before immediate tab interaction");

        var hotkeysTab = Retry.WhileNull(
                () => FindVisibleByAutomationId(window, "SettingsTab_Hotkeys"),
                TimeSpan.FromSeconds(5))
            .Result;
        hotkeysTab.Should().NotBeNull("Hotkeys tab should be physically clickable on first Settings entry");

        var hotkeysPoint = MoveMouseToElement(hotkeysTab!, "FirstEntryMouse.HotkeysTab");
        var reactionTimer = Stopwatch.StartNew();
        Mouse.Click(hotkeysPoint);

        var visibleHotkeyBox = Retry.WhileNull(
                () => FindVisibleByAutomationId(window, "ShowHotkeyBox"),
                ImmediateMouseResponseBudget)
            .Result;
        reactionTimer.Stop();

        visibleHotkeyBox
            .Should()
            .NotBeNull("the first physical mouse click inside Settings must produce visible tab content within 1s");
        reactionTimer.Elapsed
            .Should()
            .BeLessThanOrEqualTo(ImmediateMouseResponseBudget, "mouse input must never feel unresponsive for more than 1s");

        _output.WriteLine($"[FirstEntryMouse] Hotkeys tab reaction in {reactionTimer.ElapsedMilliseconds}ms");
        Retry.WhileNull(
                () =>
                {
                    var hotkeyBox = FindVisibleHotkeyTextBox(window, "ShowHotkeyBox");
                    return string.IsNullOrWhiteSpace(hotkeyBox?.Text) ? null : hotkeyBox;
                },
                TimeSpan.FromSeconds(5))
            .Result
            .Should()
            .NotBeNull("the Hotkeys tab should finish loading its editable settings after the immediate visual response");
        ScreenshotHelper.CaptureWindow(window, "settings_first_entry_immediate_mouse_tab_reaction");
    }

    [Fact]
    public void SettingsPage_LoadedTabs_ShouldSwitchWithinOneSecond()
    {
        var window = _launcher.GetMainWindow();
        window.SetForeground();
        Thread.Sleep(2000);

        var settingsButton = WaitForSettingsButton(window, TimeSpan.FromSeconds(10));
        settingsButton.Should().NotBeNull("SettingsButton must exist on main window");

        ClickElementWithMouse(settingsButton!, "TabSwitchBudget.SettingsButton");

        var scrollViewer = WaitForSettingsScrollViewer(window, TimeSpan.FromSeconds(15));
        scrollViewer
            .Should()
            .NotBeNull("Settings content should become visible before measuring tab switching");

        var tabCases = new[]
        {
            new SettingsTabSwitchCase("SettingsTab_Services", "Services"),
            new SettingsTabSwitchCase("SettingsTab_Views", "Views"),
            new SettingsTabSwitchCase("SettingsTab_Hotkeys", "Hotkeys"),
            new SettingsTabSwitchCase("SettingsTab_Advanced", "Advanced"),
            new SettingsTabSwitchCase("SettingsTab_Language", "Language"),
            new SettingsTabSwitchCase("SettingsTab_About", "About"),
            new SettingsTabSwitchCase("SettingsTab_General", "General"),
        };

        foreach (var tabCase in tabCases)
        {
            var tab = Retry.WhileNull(
                    () => FindVisibleByAutomationId(window, tabCase.TabAutomationId),
                    TimeSpan.FromSeconds(5))
                .Result;
            tab.Should().NotBeNull($"{tabCase.TabAutomationId} should be visible after Settings loads");

            ClickElementWithMouse(tab!, $"TabSwitchBudget.{tabCase.TabAutomationId}");
            var selected = WaitForSelectedSettingsTab(
                scrollViewer!,
                tabCase.ExpectedSelectedTab,
                ImmediateMouseResponseBudget,
                out var elapsed);

            selected
                .Should()
                .NotBeNull($"{tabCase.TabAutomationId} should become the selected Settings tab within 1s");
            elapsed
                .Should()
                .BeLessThanOrEqualTo(
                    ImmediateMouseResponseBudget,
                    $"{tabCase.TabAutomationId} must be interactive within 1s after Settings loading completes");

            _output.WriteLine(
                $"[TabSwitchBudget] {tabCase.TabAutomationId} selected in {elapsed.TotalMilliseconds:F0}ms");
        }
    }

    [Fact]
    public void SettingsPage_OpenBackLoop_ShouldSupportMemoryMarkerCollection()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);
        var baseline = CaptureAppProcessMemory($"{_abMode}_baseline");

        var iterations = ResolveLoopIterations();
        var idleDelayMsAfterBack = ResolveIdleDelayMsAfterBack();
        _output.WriteLine($"[MemoryLoop] Iterations={iterations}");
        _output.WriteLine($"[MemoryLoop] IdleAfterBackMs={idleDelayMsAfterBack}");
        var immediateBackSamples = new List<MemorySample>();
        var settledBackSamples = new List<MemorySample>();
        for (var i = 1; i <= iterations; i++)
        {
            var settingsButton = WaitForSettingsButton(window, TimeSpan.FromSeconds(10));
            if (settingsButton == null)
            {
                DumpButtonDiagnostics(window, $"SettingsPage_OpenBackLoop iteration={i}");
                ScreenshotHelper.CaptureWindow(window, $"memory_loop_missing_settings_button_iter_{i}");
            }
            settingsButton.Should().NotBeNull($"iteration {i}: settings button should be available");
            ClickElement(settingsButton!, $"MemoryLoop.SettingsButton iteration={i}");
            WaitForSettingsScrollViewer(window, TimeSpan.FromSeconds(15))
                .Should()
                .NotBeNull($"iteration {i}: Settings should open inside the main window");
            _ = CaptureAppProcessMemory($"{_abMode}_iter_{i}_after_open");

            _output.WriteLine($"[MemoryLoop] Iteration {i}: opened Settings page. Check Debug Output for [Memory] SettingsPage markers.");

            var backButton = WaitForBackButton(window, TimeSpan.FromSeconds(15));
            backButton.Should().NotBeNull($"iteration {i}: should find the Settings back button");
            ClickElement(backButton!, $"MemoryLoop.BackButton iteration={i}");
            Thread.Sleep(1200);
            var backSample = CaptureAppProcessMemory($"{_abMode}_iter_{i}_after_back");
            if (backSample.HasValue)
            {
                immediateBackSamples.Add(backSample.Value);
            }

            if (idleDelayMsAfterBack > 0)
            {
                Thread.Sleep(idleDelayMsAfterBack);
                var settledSample = CaptureAppProcessMemory($"{_abMode}_iter_{i}_after_back_idle");
                if (settledSample.HasValue)
                {
                    settledBackSamples.Add(settledSample.Value);
                }
            }

            _output.WriteLine($"[MemoryLoop] Iteration {i}: navigated back to Main page.");
        }

        EmitMemorySummary("ImmediateBack", baseline, immediateBackSamples);
        if (settledBackSamples.Count > 0)
        {
            EmitMemorySummary("SettledBack", baseline, settledBackSamples);
        }
    }

    private AutomationElement? WaitForSettingsButton(Window window, TimeSpan timeout)
    {
        return Retry.WhileNull(
            () => TryFindSettingsButton(window),
            timeout).Result;
    }

    private AutomationElement? WaitForBackButton(Window window, TimeSpan timeout)
    {
        return Retry.WhileNull(
            () => TryFindBackButton(window),
            timeout).Result;
    }

    private AutomationElement? WaitForSettingsScrollViewer(Window window, TimeSpan timeout)
    {
        return Retry.WhileNull(
            () => FindVisibleByAutomationId(window, "MainScrollViewer"),
            timeout).Result;
    }

    private static AutomationElement? FindVisibleByAutomationId(Window window, string automationId)
    {
        try
        {
            var element = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            return element != null && IsOnScreenOrUnknown(element)
                ? element
                : null;
        }
        catch (COMException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static AutomationElement? FindRenderedByAutomationId(
        Window window,
        string automationId,
        AutomationElement? viewport = null)
    {
        try
        {
            var viewportBounds = (viewport ?? window).BoundingRectangle;
            AutomationElement? largestElement = null;
            var largestArea = 0d;

            foreach (var element in window.FindAllDescendants(
                         cf => cf.ByAutomationId(automationId)))
            {
                var bounds = element.BoundingRectangle;
                var area = bounds.Width * bounds.Height;
                if (bounds.Width > 1 &&
                    bounds.Height > 1 &&
                    bounds.Right > viewportBounds.Left &&
                    bounds.Left < viewportBounds.Right &&
                    bounds.Bottom > viewportBounds.Top &&
                    bounds.Top < viewportBounds.Bottom &&
                    area > largestArea)
                {
                    largestElement = element;
                    largestArea = area;
                }
            }

            return largestElement;
        }
        catch (COMException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static TextBox? FindVisibleHotkeyTextBox(Window window, string automationId)
    {
        try
        {
            var container = FindVisibleByAutomationId(window, automationId);
            if (container == null)
                return null;

            var textBoxElement = container.ControlType == ControlType.Edit
                ? container
                : container.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit));

            return textBoxElement != null && IsOnScreenOrUnknown(textBoxElement)
                ? textBoxElement.AsTextBox()
                : null;
        }
        catch (COMException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (FlaUI.Core.Exceptions.MethodNotSupportedException)
        {
            return null;
        }
    }

    private static AutomationElement? TryFindSettingsButton(Window window)
    {
        try
        {
            return window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsButton"))
                ?? window.FindFirstDescendant(cf => cf.ByName("SettingsButton"))
                ?? window.FindFirstDescendant(cf => cf.ByName("Settings"))
                ?? FindTopRightLikelySettingsButton(window);
        }
        catch (COMException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static AutomationElement? TryFindBackButton(Window window)
    {
        try
        {
            return window.FindFirstDescendant(cf => cf.ByAutomationId("BackButton"))
                ?? window.FindFirstDescendant(cf => cf.ByName("BackButton"))
                ?? window.FindFirstDescendant(cf => cf.ByName("Back"))
                ?? FindTopLeftBackButton(window);
        }
        catch (COMException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static AutomationElement? FindTopLeftBackButton(Window window)
    {
        var bounds = window.BoundingRectangle;
        var buttons = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
        return buttons
            .Where(button =>
                IsOnScreenOrUnknown(button) &&
                button.BoundingRectangle.Width > 5 &&
                button.BoundingRectangle.Height > 5 &&
                button.BoundingRectangle.Left >= bounds.Left &&
                button.BoundingRectangle.Top >= bounds.Top &&
                button.BoundingRectangle.Left <= bounds.Left + 120 &&
                button.BoundingRectangle.Top <= bounds.Top + 180)
            .OrderBy(button => button.BoundingRectangle.Left + button.BoundingRectangle.Top)
            .FirstOrDefault();
    }

    // Some auto-generated WinUI subtrees expose Button control type without the
    // IsOffscreen property; reading it throws PropertyNotSupportedException. Treat
    // those as on-screen so geometry filters below decide visibility.
    private static bool IsOnScreenOrUnknown(AutomationElement element)
    {
        try
        {
            return !element.IsOffscreen;
        }
        catch (FlaUI.Core.Exceptions.PropertyNotSupportedException)
        {
            return true;
        }
    }

    private static bool WaitForVerticalPercentChange(
        AutomationElement scrollViewer,
        double initialPercent,
        TimeSpan timeout,
        int pollIntervalMs = 100,
        Action? onTick = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            onTick?.Invoke();
            Thread.Sleep(pollIntervalMs);
            if (ScrollHelper.TryGetVerticalScrollPercent(scrollViewer, out var current)
                && Math.Abs(current - initialPercent) > 0.5)
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadSelectedSettingsTab(AutomationElement scrollViewer)
    {
        try
        {
            const string prefix = "SelectedSettingsTab:";
            var helpText = scrollViewer.Properties.HelpText.ValueOrDefault;
            return helpText != null && helpText.StartsWith(prefix, StringComparison.Ordinal)
                ? helpText[prefix.Length..]
                : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static string? WaitForSelectedSettingsTab(
        AutomationElement scrollViewer,
        string expectedTab,
        TimeSpan timeout,
        out TimeSpan elapsed)
    {
        var stopwatch = Stopwatch.StartNew();
        string? selectedTab = null;
        while (stopwatch.Elapsed <= timeout)
        {
            selectedTab = ReadSelectedSettingsTab(scrollViewer);
            if (selectedTab == expectedTab)
            {
                stopwatch.Stop();
                elapsed = stopwatch.Elapsed;
                return selectedTab;
            }

            Thread.Sleep(25);
        }

        stopwatch.Stop();
        elapsed = stopwatch.Elapsed;
        return selectedTab == expectedTab ? selectedTab : null;
    }

    private static void MoveMouseToScrollGutter(AutomationElement element)
    {
        Mouse.MoveTo(GetScrollGutterPoint(element));
    }

    private static Point GetScrollGutterPoint(AutomationElement element)
    {
        var bounds = element.BoundingRectangle;
        if (bounds.Width > 48 && bounds.Height > 48)
        {
            return new Point(
                bounds.Right - 24,
                bounds.Top + bounds.Height / 2);
        }

        try
        {
            return element.GetClickablePoint();
        }
        catch
        {
            return new Point(
                bounds.Left + bounds.Width / 2,
                bounds.Top + bounds.Height / 2);
        }
    }

    private static AutomationElement? FindTopRightLikelySettingsButton(Window window)
    {
        var bounds = window.BoundingRectangle;
        var headerTopLimit = bounds.Top + 220;
        var rightLimit = bounds.Right - 160;

        var buttons = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
        return buttons
            .Where(button =>
                button.BoundingRectangle.Top <= headerTopLimit &&
                button.BoundingRectangle.Right >= rightLimit &&
                button.BoundingRectangle.Width <= 60 &&
                button.BoundingRectangle.Height <= 60)
            .OrderByDescending(button => button.BoundingRectangle.Right)
            .FirstOrDefault();
    }

    private void DumpButtonDiagnostics(Window window, string context)
    {
        var bounds = window.BoundingRectangle;
        _output.WriteLine($"[{context}] Window bounds: {bounds}");

        var buttons = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .Take(20)
            .ToList();
        _output.WriteLine($"[{context}] Found {buttons.Count} button(s), logging up to 20:");

        for (var i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            _output.WriteLine(
                $"  [{i}] Name='{button.Name}', AutomationId='{button.AutomationId}', Class='{button.ClassName}', Bounds={button.BoundingRectangle}");
        }
    }

    private void ClickElement(AutomationElement element, string context)
    {
        try
        {
            var invokePattern = element.Patterns.Invoke.PatternOrDefault;
            if (invokePattern != null)
            {
                invokePattern.Invoke();
                return;
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[{context}] Invoke pattern failed: {ex.Message}");
        }

        element.Click();
    }

    private void ClickElementWithMouse(AutomationElement element, string context)
    {
        var point = MoveMouseToElement(element, context);
        Mouse.Click(point);
    }

    private Point MoveMouseToElement(AutomationElement element, string context)
    {
        var point = GetClickablePoint(element, context);
        _output.WriteLine($"[{context}] Physical mouse click at {point}, bounds={element.BoundingRectangle}");
        Mouse.MoveTo(point);
        return point;
    }

    private Point GetClickablePoint(AutomationElement element, string context)
    {
        try
        {
            return element.GetClickablePoint();
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[{context}] GetClickablePoint failed: {ex.Message}; using element center");
            var bounds = element.BoundingRectangle;
            return new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        }
    }

    [Fact]
    public void SettingsPage_OpenAndReturn_ShouldRestoreMainWindowMemory()
    {
        var window = _launcher.GetMainWindow();
        Thread.Sleep(2000);

        var settingsButton = WaitForSettingsButton(window, TimeSpan.FromSeconds(10));
        settingsButton.Should().NotBeNull("SettingsButton must exist on main window before the memory isolation check");

        OpenSettingsAndReturn(window, "MemoryIsolation.Prime1");
        Thread.Sleep(1500);
        OpenSettingsAndReturn(window, "MemoryIsolation.Prime2");
        Thread.Sleep(1500);
        OpenSettingsAndReturn(window, "MemoryIsolation.Prime3");
        Thread.Sleep(1500);

        var baseline = CaptureSettledAppProcessMemory("isolation_baseline", TimeSpan.FromSeconds(8));
        baseline.Should().NotBeNull("the app process memory must be observable before opening Settings");

        OpenSettingsAndReturn(window, "MemoryIsolation.Checked");

        var afterReturn = CaptureSettledAppProcessMemory("isolation_after_return", TimeSpan.FromSeconds(10));
        afterReturn.Should().NotBeNull("the app process memory must be observable after returning to MainPage");

        var toleranceMb = ResolveMemoryIsolationToleranceMb();
        var privateDelta = afterReturn!.Value.PrivateMb - baseline!.Value.PrivateMb;
        var workingSetDelta = afterReturn.Value.WorkingSetMb - baseline.Value.WorkingSetMb;

        _output.WriteLine(
            $"[MemoryIsolation] BaselinePrivate={baseline.Value.PrivateMb:F1}MB " +
            $"AfterPrivate={afterReturn.Value.PrivateMb:F1}MB DeltaPrivate={privateDelta:+0.0;-0.0;0.0}MB " +
            $"BaselineWS={baseline.Value.WorkingSetMb:F1}MB AfterWS={afterReturn.Value.WorkingSetMb:F1}MB " +
            $"DeltaWS={workingSetDelta:+0.0;-0.0;0.0}MB Tolerance={toleranceMb:F1}MB");

        privateDelta
            .Should()
            .BeLessThanOrEqualTo(
                toleranceMb,
                "opening Settings and returning to MainPage should not retain Settings/window-control memory");
    }

    private void OpenSettingsAndReturn(Window window, string context)
    {
        var settingsButton = WaitForSettingsButton(window, TimeSpan.FromSeconds(10));
        settingsButton.Should().NotBeNull($"{context}: SettingsButton must exist on MainPage");

        ClickElement(settingsButton!, $"{context}.SettingsButton");
        WaitForSettingsScrollViewer(window, TimeSpan.FromSeconds(15))
            .Should()
            .NotBeNull($"{context}: Settings content should be visible before returning to MainPage");

        var backButton = WaitForBackButton(window, TimeSpan.FromSeconds(15));
        backButton.Should().NotBeNull($"{context}: Settings back button must be available");
        ClickElement(backButton!, $"{context}.BackButton");

        WaitForSettingsButton(window, TimeSpan.FromSeconds(15))
            .Should()
            .NotBeNull($"{context}: MainPage should be visible after returning from Settings");
    }

    private MemorySample? CaptureAppProcessMemory(string marker)
    {
        try
        {
            var pid = _launcher.Application.ProcessId;
            using var process = Process.GetProcessById(pid);
            process.Refresh();
            var sample = new MemorySample(
                ToMb(process.WorkingSet64),
                ToMb(process.PrivateMemorySize64),
                ToMb(process.PagedMemorySize64));
            _output.WriteLine(
                $"[MemoryLoop][{marker}] PID={pid} WS={sample.WorkingSetMb:F1}MB Private={sample.PrivateMb:F1}MB Paged={sample.PagedMb:F1}MB");
            return sample;
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[MemoryLoop][{marker}] Failed to read process memory: {ex.Message}");
            return null;
        }
    }

    private MemorySample? CaptureSettledAppProcessMemory(string marker, TimeSpan settleTimeout)
    {
        var deadline = DateTime.UtcNow + settleTimeout;
        MemorySample? best = null;
        MemorySample? previous = null;
        var stableSamples = 0;

        while (DateTime.UtcNow < deadline)
        {
            var sample = CaptureAppProcessMemory($"{marker}_sample");
            if (sample.HasValue)
            {
                best = !best.HasValue || sample.Value.PrivateMb < best.Value.PrivateMb
                    ? sample.Value
                    : best.Value;

                if (previous.HasValue &&
                    Math.Abs(sample.Value.PrivateMb - previous.Value.PrivateMb) <= 0.5 &&
                    Math.Abs(sample.Value.WorkingSetMb - previous.Value.WorkingSetMb) <= 1.0)
                {
                    stableSamples++;
                    if (stableSamples >= 2 && DateTime.UtcNow >= deadline - TimeSpan.FromSeconds(2))
                    {
                        return sample;
                    }
                }
                else
                {
                    stableSamples = 0;
                }

                previous = sample;
            }

            Thread.Sleep(500);
        }

        if (best.HasValue)
        {
            _output.WriteLine(
                $"[MemoryLoop][{marker}] Settling timeout reached; using lowest observed private memory sample.");
        }

        return best;
    }

    private void EmitMemorySummary(string phase, MemorySample? baseline, IReadOnlyList<MemorySample> afterBackSamples)
    {
        if (!baseline.HasValue || afterBackSamples.Count == 0)
        {
            _output.WriteLine($"[MemoryLoop][{_abMode}][{phase}] Summary unavailable (missing baseline or back samples).");
            return;
        }

        var firstBack = afterBackSamples[0];
        var lastBack = afterBackSamples[^1];
        var peakBack = afterBackSamples.Max(s => s.WorkingSetMb);

        var deltaWs = lastBack.WorkingSetMb - baseline.Value.WorkingSetMb;
        var deltaPrivate = lastBack.PrivateMb - baseline.Value.PrivateMb;
        var deltaPaged = lastBack.PagedMb - baseline.Value.PagedMb;

        var halfIndex = afterBackSamples.Count / 2;
        double tailSlope = 0;
        if (afterBackSamples.Count - halfIndex > 1)
        {
            tailSlope = (afterBackSamples[^1].WorkingSetMb - afterBackSamples[halfIndex].WorkingSetMb)
                / (afterBackSamples.Count - halfIndex - 1);
        }

        _output.WriteLine(
            $"[MemoryLoop][{_abMode}][{phase}] Summary: BaselineWS={baseline.Value.WorkingSetMb:F1}MB FirstBackWS={firstBack.WorkingSetMb:F1}MB LastBackWS={lastBack.WorkingSetMb:F1}MB PeakBackWS={peakBack:F1}MB");
        _output.WriteLine(
            $"[MemoryLoop][{_abMode}][{phase}] Delta: WS={deltaWs:+0.0;-0.0;0.0}MB Private={deltaPrivate:+0.0;-0.0;0.0}MB Paged={deltaPaged:+0.0;-0.0;0.0}MB TailSlopeWS={tailSlope:+0.00;-0.00;0.00}MB/iter");
    }

    private static double ToMb(long bytes) => bytes / 1024d / 1024d;

    private static int ResolveLoopIterations()
    {
        var value = Environment.GetEnvironmentVariable("EASYDICT_UIA_MEMORY_LOOP_ITERATIONS");
        if (!int.TryParse(value, out var iterations))
        {
            return 5;
        }

        return Math.Clamp(iterations, 1, 50);
    }

    private static int ResolveIdleDelayMsAfterBack()
    {
        var value = Environment.GetEnvironmentVariable("EASYDICT_UIA_MEMORY_IDLE_MS_AFTER_BACK");
        if (!int.TryParse(value, out var delayMs))
        {
            return 1500;
        }

        return Math.Clamp(delayMs, 0, 10000);
    }

    private static double ResolveMemoryIsolationToleranceMb()
    {
        var value = Environment.GetEnvironmentVariable("EASYDICT_UIA_SETTINGS_MEMORY_TOLERANCE_MB");
        if (!double.TryParse(value, out var toleranceMb))
        {
            return 8;
        }

        return Math.Clamp(toleranceMb, 0, 64);
    }

    private static string ResolveAbMode()
    {
        var value = Environment.GetEnvironmentVariable("EASYDICT_UIA_MEMORY_AB_MODE");
        return string.Equals(value, "B", StringComparison.OrdinalIgnoreCase) ? "B" : "A";
    }

    public void Dispose()
    {
        _launcher.Dispose();
    }
}
