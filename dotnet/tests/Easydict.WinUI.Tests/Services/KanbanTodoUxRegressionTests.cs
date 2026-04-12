using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Static regression checks for the window/hotkey UX work in
/// `claude/fix-kanban-todos-FUSvR`.
///
/// These tests intentionally validate stable source-level contracts instead of
/// fragile UI automation interactions, so the new UX behavior stays covered by
/// `Easydict.WinUI.Tests`.
/// </summary>
[Trait("Category", "WinUI")]
public class KanbanTodoUxRegressionTests
{
    private static readonly string ProjectRoot = FindProjectRoot();
    private static readonly string StringsPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Strings");
    private static readonly string MainPageXamlPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "MainPage.xaml");
    private static readonly string MiniWindowXamlPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "MiniWindow.xaml");
    private static readonly string FixedWindowXamlPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "FixedWindow.xaml");
    private static readonly string SettingsPageXamlPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "SettingsPage.xaml");
    private static readonly string SettingsPageCodePath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "SettingsPage.xaml.cs");
    private static readonly string ServiceCheckItemPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Models", "ServiceCheckItem.cs");
    private static readonly string ServiceResultItemXamlPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "Controls", "ServiceResultItem.xaml");
    private static readonly string ServiceResultItemPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "Controls", "ServiceResultItem.xaml.cs");
    private static readonly string AppPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "App.xaml.cs");
    private static readonly string MiniWindowServicePath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Services", "MiniWindowService.cs");
    private static readonly string FixedWindowServicePath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Services", "FixedWindowService.cs");
    private static readonly string MiniWindowPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "MiniWindow.xaml.cs");
    private static readonly string FixedWindowPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "FixedWindow.xaml.cs");

    [Fact]
    public void SettingsPage_HotkeysDescription_UsesLocalizedResource()
    {
        var xaml = File.ReadAllText(SettingsPageXamlPath);
        var code = File.ReadAllText(SettingsPageCodePath);

        xaml.Should().Contain("x:Name=\"HotkeysDescriptionText\"",
            "the Settings page should keep a dedicated hotkey description text block");
        code.Should().Contain("HotkeysDescriptionText.Text = loc.GetString(\"HotkeysDescription\")",
            "the restart-required hotkey copy should come from localization resources, not stay hard-coded");
    }

    [Fact]
    public void AllLanguages_HaveHotkeysDescriptionResource()
    {
        foreach (var languageDir in Directory.GetDirectories(StringsPath))
        {
            var reswPath = Path.Combine(languageDir, "Resources.resw");
            File.Exists(reswPath).Should().BeTrue($"{languageDir} should contain Resources.resw");

            var doc = XDocument.Load(reswPath);
            var element = doc.Descendants("data")
                .FirstOrDefault(e => e.Attribute("name")?.Value == "HotkeysDescription");

            element.Should().NotBeNull($"{languageDir} should contain HotkeysDescription");
            element!.Element("value")?.Value.Should().NotBeNullOrWhiteSpace(
                $"{languageDir} should provide a non-empty HotkeysDescription translation");
        }
    }

    [Fact]
    public void SettingsPage_PersistsPerHotkeyEnableFlags()
    {
        var code = File.ReadAllText(SettingsPageCodePath);

        code.Should().Contain("_settings.EnableShowWindowHotkey = ShowHotkeyEnabledToggle.IsOn;");
        code.Should().Contain("_settings.EnableTranslateSelectionHotkey = TranslateHotkeyEnabledToggle.IsOn;");
        code.Should().Contain("_settings.EnableShowMiniWindowHotkey = ShowMiniHotkeyEnabledToggle.IsOn;");
        code.Should().Contain("_settings.EnableShowFixedWindowHotkey = ShowFixedHotkeyEnabledToggle.IsOn;");
        code.Should().Contain("_settings.EnableOcrTranslateHotkey = OcrTranslateHotkeyEnabledToggle.IsOn;");
        code.Should().Contain("_settings.EnableSilentOcrHotkey = SilentOcrHotkeyEnabledToggle.IsOn;");
    }

    [Fact]
    public void SettingsPage_PreservesUserServiceOrder()
    {
        var xaml = File.ReadAllText(SettingsPageXamlPath);
        var code = File.ReadAllText(SettingsPageCodePath);
        var itemCode = File.ReadAllText(ServiceCheckItemPath);

        code.Should().Contain("private static void MoveServiceUp(",
            "the Settings page should expose explicit move-up helpers for service ordering");
        code.Should().Contain("private static void MoveServiceDown(",
            "the Settings page should expose explicit move-down helpers for service ordering");
        code.Should().Contain("collection.Move(i, i - 1);",
            "move-up should reorder the observable collection");
        code.Should().Contain("collection.Move(i, i + 1);",
            "move-down should reorder the observable collection");
        code.Should().Contain("var orderIndex = new Dictionary<string, int>",
            "service population should honor the persisted enabled-service order");
        code.Should().Contain("var ordered = managerOrder",
            "service population should rebuild the view in persisted order");
        xaml.Should().NotContain("x:Name=\"ServiceReorderModeButton\"",
            "the old single global reorder entry should be replaced by per-window entry points");
        xaml.Should().Contain("x:Name=\"MainWindowReorderModeButton\"",
            "main window services should have their own reorder entry point");
        xaml.Should().Contain("x:Name=\"MiniWindowReorderModeButton\"",
            "mini window services should have their own reorder entry point");
        xaml.Should().Contain("x:Name=\"FixedWindowReorderModeButton\"",
            "fixed window services should have their own reorder entry point");
        xaml.Should().Contain("Click=\"OnToggleMainWindowReorderModeClicked\"",
            "the main window reorder entry should toggle that section's reorder mode");
        xaml.Should().Contain("Click=\"OnToggleMiniWindowReorderModeClicked\"",
            "the mini window reorder entry should toggle that section's reorder mode");
        xaml.Should().Contain("Click=\"OnToggleFixedWindowReorderModeClicked\"",
            "the fixed window reorder entry should toggle that section's reorder mode");
        xaml.Should().Contain("Visibility=\"{Binding IsReorderModeEnabled, Converter={StaticResource BoolToVisibilityConverter}}\"",
            "per-row move buttons should only appear while reorder mode is enabled");
        itemCode.Should().Contain("public bool IsReorderModeEnabled",
            "service items should expose reorder-mode visibility state for the move controls");
        code.Should().Contain("private const string ReorderButtonEmoji = \"\\u2195\\uFE0F\";",
            "the new per-window reorder entries should include the requested emoji marker");
        code.Should().Contain("ResetServiceReorderModes();",
            "the page should fall back to the clean default state after load/save");
        code.Should().Contain("EnabledServicesReorderButton",
            "the reorder entry should use localized copy");
        code.Should().Contain("EnabledServicesDoneReorderingButton",
            "the active-state button label should also come from localization");
        AssertPrecedes(xaml, "MainWindowHeaderText", "MainWindowReorderModeButton");
        AssertPrecedes(xaml, "MiniWindowHeaderText", "MiniWindowReorderModeButton");
        AssertPrecedes(xaml, "FixedWindowHeaderText", "FixedWindowReorderModeButton");
    }

    [Fact]
    public void AllLanguages_HaveEnabledServicesReorderResources()
    {
        foreach (var languageDir in Directory.GetDirectories(StringsPath))
        {
            var reswPath = Path.Combine(languageDir, "Resources.resw");
            File.Exists(reswPath).Should().BeTrue($"{languageDir} should contain Resources.resw");

            var doc = XDocument.Load(reswPath);
            var reorderElement = doc.Descendants("data")
                .FirstOrDefault(e => e.Attribute("name")?.Value == "EnabledServicesReorderButton");
            var doneElement = doc.Descendants("data")
                .FirstOrDefault(e => e.Attribute("name")?.Value == "EnabledServicesDoneReorderingButton");

            reorderElement.Should().NotBeNull($"{languageDir} should contain EnabledServicesReorderButton");
            doneElement.Should().NotBeNull($"{languageDir} should contain EnabledServicesDoneReorderingButton");
            reorderElement!.Element("value")?.Value.Should().NotBeNullOrWhiteSpace(
                $"{languageDir} should provide a non-empty EnabledServicesReorderButton translation");
            doneElement!.Element("value")?.Value.Should().NotBeNullOrWhiteSpace(
                $"{languageDir} should provide a non-empty EnabledServicesDoneReorderingButton translation");
        }
    }

    [Fact]
    public void SettingsPage_ScrollNavSync_DoesNotMutateFontSize()
    {
        var code = File.ReadAllText(SettingsPageCodePath);
        var marker = "private void UpdateActiveNavIcon(int activeIndex)";
        var start = code.IndexOf(marker, StringComparison.Ordinal);

        start.Should().BeGreaterOrEqualTo(0, "the settings nav rail should still have an active-state update helper");
        var snippet = code.Substring(start, Math.Min(900, code.Length - start));

        snippet.Should().Contain("AccentFillColorDefaultBrush",
            "the active nav item should still stand out visually");
        snippet.Should().Contain("TextFillColorSecondaryBrush",
            "inactive nav items should still use the secondary styling");
        snippet.Should().NotContain("icon.FontSize = 16",
            "scroll-driven nav highlighting must not resize the Auto-column icon rail and trigger a layout feedback loop");
        snippet.Should().NotContain("icon.FontSize = 14",
            "scroll-driven nav highlighting should keep icon size stable once the rail is created");
    }

    [Fact]
    public void ServiceResultItem_LeavesNoResultRowsVisibleButCollapsedWhenHideEmptyResultsIsEnabled()
    {
        var code = File.ReadAllText(ServiceResultItemPath);
        var marker = "var hideEmpty = SettingsService.Instance.HideEmptyServiceResults";
        var start = code.IndexOf(marker, StringComparison.Ordinal);

        start.Should().BeGreaterOrEqualTo(0, "the hide-empty guard should still exist in ServiceResultItem");
        var snippet = code.Substring(start, Math.Min(520, code.Length - start));

        code.Should().Contain("SettingsService.Instance.HideEmptyServiceResults",
            "the new settings toggle should control no-result row presentation");
        code.Should().Contain("TranslationResultKind.NoResult",
            "only true no-result responses should be collapsed by the hide-empty rule");
        snippet.Should().Contain("_serviceResult.IsExpanded = false;",
            "hide-empty should force the row closed while keeping the service visible in the list");
        snippet.Should().NotContain("this.Visibility = Visibility.Collapsed;",
            "hide-empty should no longer remove the entire service row from the results list");
    }

    [Fact]
    public void ServiceResultItem_UsesChainedInnerScrollForLongContent()
    {
        var xaml = File.ReadAllText(ServiceResultItemXamlPath);
        var code = File.ReadAllText(ServiceResultItemPath);

        xaml.Should().Contain("x:Name=\"ResultContentScrollViewer\"",
            "service results should use an explicit inner scroll container for long content");
        xaml.Should().Contain("VerticalScrollBarVisibility=\"Visible\"",
            "the inner result container should reserve scrollbar width so WebView2 height measurement does not feed back into layout width");
        xaml.Should().Contain("PointerWheelChanged=\"OnResultContentScrollViewerPointerWheelChanged\"",
            "scrolling at the inner boundary should explicitly hand wheel input to the outer results list");
        xaml.Should().Contain("MaxHeight=\"800\"",
            "the inner result container should cap its viewport height before scrolling");
        code.Should().Contain("private void OnResultContentScrollViewerPointerWheelChanged",
            "the inner scroll container should forward edge wheel gestures to the outer results list");
        code.Should().Contain("private static bool TryScrollViewerChain",
            "nested wheel forwarding should flow through a shared helper instead of duplicating one-hop ChangeView logic");
        code.Should().Contain("TryScrollViewerChain(FindAncestorScrollViewer(innerScrollViewer), offsetDelta)",
            "edge-wheel forwarding should continue through the ancestor scroll chain after the inner result viewer hits its boundary");
        code.Should().NotContain("outerScrollViewer.ChangeView(null, targetOffset, null, disableAnimation: true);",
            "the inner result viewer should not stop after a single outer-scroll hop");
        code.Should().Contain("MeasureDictionaryHeightAsync(sender)",
            "dictionary WebView results should keep a lightweight post-navigation sizing pass");
        code.Should().Contain("await Task.Delay(50);",
            "dictionary WebView sizing should wait briefly for the CSS normalization/layout pass to settle");
        code.Should().Contain("sender.DispatcherQueue.TryEnqueue(() =>",
            "dictionary WebView height changes should be deferred out of the navigation callback to avoid re-entrant XAML layout cycles");
        code.Should().NotContain("document.querySelectorAll('*')",
            "dictionary navigation should not walk the entire DOM on the UI thread after every query");
        code.Should().Contain("var targetHeight = height + 8;",
            "dictionary WebView sizing should still derive from measured content height");
        code.Should().Contain("DictWebView.CoreWebView2.WebMessageReceived += OnDictWebViewWebMessageReceived;",
            "the host should subscribe to WebView messages so wheel-boundary events can escape the dictionary surface");
        code.Should().Contain("window.chrome?.webview?.postMessage({",
            "dictionary HTML should signal top/bottom wheel-boundary events back to the host");
        code.Should().Contain("type: 'dict-wheel-boundary'",
            "the wheel relay should use a dedicated message type instead of piggybacking on unrelated messages");
        code.Should().Contain("type: 'dict-wheel-passthrough'",
            "dictionary HTML should also proxy ordinary wheel input back to the host when there is no true internal scroll container");
        code.Should().Contain("private void OnDictWebViewWebMessageReceived",
            "the host control should translate WebView wheel-boundary messages into outer ScrollViewer movement");
        code.Should().Contain("TryScrollViewerChain(hostScrollViewer, deltaY)",
            "the WebView relay should traverse the scroll chain instead of stopping at the first host ScrollViewer");
        code.Should().Contain("currentScrollViewer = FindAncestorScrollViewer(currentScrollViewer);",
            "the shared helper should continue climbing through ancestor ScrollViewers when an intermediate host is already at its boundary");
        code.Should().NotContain("hostScrollViewer.ChangeView(null, targetOffset, null, disableAnimation: true);",
            "the WebView relay should not stop after moving only the first host ScrollViewer");
        code.Should().Contain("typeElement.GetString() is not \"dict-wheel-boundary\" and not \"dict-wheel-passthrough\"",
            "the host should accept both boundary handoff and full passthrough wheel messages from the WebView surface");
        code.Should().NotContain("Math.Min(height + 8, 800)",
            "the WebView should no longer keep its own 800px internal scroll cap");
    }

    [Fact]
    public void ResultHosts_ReserveScrollbarWidth_ForDictionaryContent()
    {
        var mainXaml = File.ReadAllText(MainPageXamlPath);
        var miniXaml = File.ReadAllText(MiniWindowXamlPath);
        var fixedXaml = File.ReadAllText(FixedWindowXamlPath);
        var itemXaml = File.ReadAllText(ServiceResultItemXamlPath);

        mainXaml.Should().Contain("x:Name=\"QuickTranslateContent\"");
        mainXaml.Should().Contain("VerticalScrollBarVisibility=\"Visible\"",
            "the main results surface should reserve scrollbar width so dictionary WebView sizing does not feed back into page width");
        miniXaml.Should().Contain("Grid.Row=\"4\"");
        miniXaml.Should().Contain("VerticalScrollBarVisibility=\"Visible\"",
            "the mini-window results host should reserve scrollbar width for the same reason");
        fixedXaml.Should().Contain("Grid.Row=\"4\"");
        fixedXaml.Should().Contain("VerticalScrollBarVisibility=\"Visible\"",
            "the fixed-window results host should reserve scrollbar width for the same reason");
        itemXaml.Should().Contain("x:Name=\"ResultContentScrollViewer\"");
        itemXaml.Should().Contain("VerticalScrollBarVisibility=\"Visible\"",
            "the inner result container should keep a stable viewport width while the dictionary WebView height is being applied");
    }

    [Fact]
    public void ServiceResultItem_AppliesReadableDictionaryWebViewStyles()
    {
        var code = File.ReadAllText(ServiceResultItemPath);

        code.Should().Contain("padding: 0 8px 12px;",
            "dictionary HTML should keep a little horizontal and bottom breathing room inside the WebView");
        code.Should().Contain("line-height: 1.45;",
            "dictionary body text should render with a slightly more readable line height");
        code.Should().Contain("overflow-x: hidden;",
            "dictionary WebView content should avoid accidental horizontal overflow");
        code.Should().Contain("overflow-y: hidden;",
            "the dictionary document should yield vertical scrolling to the host result container");
        code.Should().Contain("ol, ul {",
            "dictionary definition lists should get consistent spacing");
        code.Should().Contain("li {",
            "dictionary list items should keep a little vertical separation");
        code.Should().Contain("[style*=\"overflow-y\"],",
            "common inline-scroll dictionary containers should be flattened through CSS instead of a DOM-wide JS walk");
        code.Should().Contain("[class*=\"phon\"], [class*=\"pron\"], [class*=\"ipa\"] {",
            "common phonetic markup should get a small readability pass");
        code.Should().Contain("[class*=\"meaning\"], [class*=\"def\"], [class*=\"sense\"], [class*=\"gloss\"] {",
            "common meaning/definition markup should get a slightly roomier line height");
        code.Should().Contain("img, svg, table { max-width: 100% !important; height: auto; }",
            "media-heavy dictionary content should stay inside the result viewport");
        code.Should().Contain("pre { white-space: pre-wrap; overflow-wrap: anywhere; }",
            "long preformatted fragments should wrap instead of forcing awkward horizontal overflow");
    }

    [Fact]
    public void ServiceResultItem_RawHtmlPath_FallsBackToPlainTextWhenWebViewFails()
    {
        var code = File.ReadAllText(ServiceResultItemPath);

        code.Should().Contain("private void ShowRawHtmlPlainTextFallback",
            "MDX dictionary rendering should keep a dedicated plain-text fallback helper");
        code.Should().Contain("ShowRawHtmlPlainTextFallback(_serviceResult.Result, resultTextBrush);",
            "the RawHtml branch should seed visible plain text before WebView2 finishes loading");
        code.Should().Contain("ShowRawHtmlPlainTextFallback(_serviceResult.Result);",
            "navigation and sizing failures should restore the plain-text fallback instead of leaving the result blank");
        code.Should().NotContain("if (!args.IsSuccess) return;",
            "failed WebView2 navigations must not silently exit without restoring visible content");
    }

    [Fact]
    public void AppAndWindowServices_ImplementForegroundToggleContract()
    {
        var appCode = File.ReadAllText(AppPath);
        var miniServiceCode = File.ReadAllText(MiniWindowServicePath);
        var fixedServiceCode = File.ReadAllText(FixedWindowServicePath);
        var miniWindowCode = File.ReadAllText(MiniWindowPath);
        var fixedWindowCode = File.ReadAllText(FixedWindowPath);

        appCode.Should().Contain("if (IsMainWindowVisible && IsMainWindowForeground)",
            "the main window hotkey should hide the foreground window on repeated press");
        appCode.Should().Contain("MiniWindowService.Instance.IsVisible");
        appCode.Should().Contain("MiniWindowService.Instance.IsForeground");
        appCode.Should().Contain("MiniWindowService.Instance.Hide();");
        appCode.Should().Contain("FixedWindowService.Instance.IsVisible");
        appCode.Should().Contain("FixedWindowService.Instance.IsForeground");
        appCode.Should().Contain("FixedWindowService.Instance.Hide();");

        miniServiceCode.Should().Contain("public bool IsForeground => _miniWindow?.IsForeground ?? false;",
            "the service facade should expose mini-window foreground state");
        fixedServiceCode.Should().Contain("public bool IsForeground => _fixedWindow?.IsForeground ?? false;",
            "the service facade should expose fixed-window foreground state");

        miniWindowCode.Should().Contain("public bool IsForeground",
            "the concrete mini window should expose the foreground check");
        fixedWindowCode.Should().Contain("public bool IsForeground",
            "the concrete fixed window should expose the foreground check");
    }

    private static string FindProjectRoot()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var solutionPath = Path.Combine(current, "Easydict.Win32.sln");
            if (File.Exists(solutionPath))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..");
    }

    private static void AssertPrecedes(string content, string firstName, string secondName)
    {
        var firstIndex = content.IndexOf($"x:Name=\"{firstName}\"", StringComparison.Ordinal);
        var secondIndex = content.IndexOf($"x:Name=\"{secondName}\"", StringComparison.Ordinal);

        firstIndex.Should().BeGreaterOrEqualTo(0, $"{firstName} should exist in SettingsPage.xaml");
        secondIndex.Should().BeGreaterOrEqualTo(0, $"{secondName} should exist in SettingsPage.xaml");
        firstIndex.Should().BeLessThan(secondIndex,
            $"{firstName} should be rendered before {secondName} in the section header");
    }
}
