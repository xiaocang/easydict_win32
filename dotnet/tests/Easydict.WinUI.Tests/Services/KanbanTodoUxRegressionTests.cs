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
    private static readonly string SettingsPageXamlPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "SettingsPage.xaml");
    private static readonly string SettingsPageCodePath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "SettingsPage.xaml.cs");
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
        var code = File.ReadAllText(SettingsPageCodePath);

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
    }

    [Fact]
    public void ServiceResultItem_CollapsesNoResultRowsWhenHideEmptyResultsIsEnabled()
    {
        var code = File.ReadAllText(ServiceResultItemPath);

        code.Should().Contain("SettingsService.Instance.HideEmptyServiceResults",
            "the new settings toggle should control no-result row collapsing");
        code.Should().Contain("TranslationResultKind.NoResult",
            "only true no-result responses should be collapsed");
        code.Should().Contain("this.Visibility = Visibility.Collapsed;",
            "no-result rows should be removed from layout instead of merely dimmed");
        code.Should().Contain("this.Visibility = Visibility.Visible;",
            "rows should become visible again when they later have content or an error");
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
}
