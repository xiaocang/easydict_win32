using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class SettingsPageSplitTabsTests
{
    private static readonly string ProjectRoot = FindProjectRoot();
    private static readonly string SettingsPageXamlPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "SettingsPage.xaml");
    private static readonly string SettingsPageCodeBehindPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "SettingsPage.xaml.cs");
    private static readonly string StringsPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Strings");

    private static readonly string[] ExpectedTabs =
    [
        "General",
        "Services",
        "Views",
        "Hotkeys",
        "Advanced",
        "Language",
        "About"
    ];

    private static readonly string[] ExpectedTabResourceKeys =
    [
        "SettingsTab_General",
        "SettingsTab_General_Tooltip",
        "SettingsTab_Services",
        "SettingsTab_Services_Tooltip",
        "SettingsTab_Views",
        "SettingsTab_Views_Tooltip",
        "SettingsTab_Hotkeys",
        "SettingsTab_Hotkeys_Tooltip",
        "SettingsTab_Advanced",
        "SettingsTab_Advanced_Tooltip",
        "SettingsTab_Language",
        "SettingsTab_Language_Tooltip",
        "SettingsTab_About",
        "SettingsTab_About_Tooltip",
        "WindowResults",
        "WindowResultsDescription"
    ];

    [Fact]
    public void SettingsPage_UsesTopSquareTabsInsteadOfFloatingNavRail()
    {
        var xaml = File.ReadAllText(SettingsPageXamlPath);
        var codeBehind = File.ReadAllText(SettingsPageCodeBehindPath);

        xaml.Should().Contain("SettingsTabsHost");
        xaml.Should().Contain("OnSettingsTabClick");
        xaml.Should().Contain("ToolTipService.ToolTip=\"{Binding Tooltip}\"");
        xaml.Should().NotContain("NavSidebar");
        xaml.Should().NotContain("NavIndicators");
        xaml.Should().NotContain("Floating Navigation Sidebar");

        codeBehind.Should().NotContain("InitializeNavigation");
        codeBehind.Should().NotContain("OnScrollViewChanged");
        codeBehind.Should().NotContain("OnNavIconClick");
    }

    [Fact]
    public void SettingsPage_DefinesExpectedTopLevelTabsInOrder()
    {
        var codeBehind = File.ReadAllText(SettingsPageCodeBehindPath);
        var xaml = File.ReadAllText(SettingsPageXamlPath);
        var initializer = GetSettingsTabsInitializer(codeBehind);

        Regex.Matches(initializer, @"Id = SettingsTabId\.(\w+)")
            .Select(match => match.Groups[1].Value)
            .Should()
            .Equal(ExpectedTabs);

        foreach (var tabName in ExpectedTabs)
        {
            codeBehind.Should().Contain($"SettingsTabId.{tabName}");
            xaml.Should().Contain($"x:Name=\"{tabName}TabContent\"");
        }

        codeBehind.Should().NotContain("SettingsTabId.Main");
        codeBehind.Should().NotContain("SettingsTabId.Mini");
        codeBehind.Should().NotContain("SettingsTabId.Fixed");
        xaml.Should().NotContain("x:Name=\"MainTabContent\"");
        xaml.Should().NotContain("x:Name=\"MiniTabContent\"");
        xaml.Should().NotContain("x:Name=\"FixedTabContent\"");
    }

    [Fact]
    public void SettingsPage_ViewsTab_IsDeferredLoaded()
    {
        var xaml = File.ReadAllText(SettingsPageXamlPath);
        var pattern = @"<StackPanel\s+x:Name=""ViewsTabContent""[\s\S]*?x:Load=""False""";

        Regex.IsMatch(xaml, pattern).Should().BeTrue(
            "window result settings should not be built during initial SettingsPage XAML load");
    }

    [Fact]
    public void SettingsPage_CodeBehindLoadsDeferredViewsTabOnDemand()
    {
        var codeBehind = File.ReadAllText(SettingsPageCodeBehindPath);

        codeBehind.Should().Contain("EnsureTabContentLoaded");
        codeBehind.Should().Contain("case SettingsTabId.Views when ViewsTabContent == null:");
        codeBehind.Should().Contain("FindName(nameof(ViewsTabContent))");
        codeBehind.Should().Contain("BindWindowServicePanels");
        codeBehind.Should().NotContain("FindName(nameof(MainTabContent))");
        codeBehind.Should().NotContain("FindName(nameof(MiniTabContent))");
        codeBehind.Should().NotContain("FindName(nameof(FixedTabContent))");
    }

    [Fact]
    public void SettingsPage_DefaultTabIsGeneral()
    {
        var codeBehind = File.ReadAllText(SettingsPageCodeBehindPath);
        var initializer = GetSettingsTabsInitializer(codeBehind);

        codeBehind.Should().Contain("SelectSettingsTab(SettingsTabId.General, resetScroll: false)");
        initializer.Should().Contain("Id = SettingsTabId.General");
        initializer.Should().Contain("IsSelected = true");
    }

    [Fact]
    public void SettingsPage_AboutSection_IsInAboutTab()
    {
        var xaml = File.ReadAllText(SettingsPageXamlPath);
        var generalStart = xaml.IndexOf("x:Name=\"GeneralTabContent\"", StringComparison.Ordinal);
        var aboutTabStart = xaml.IndexOf("x:Name=\"AboutTabContent\"", StringComparison.Ordinal);
        var aboutSectionStart = xaml.IndexOf("x:Name=\"AboutSection\"", StringComparison.Ordinal);
        var advancedStart = xaml.IndexOf("x:Name=\"AdvancedTabContent\"", StringComparison.Ordinal);

        generalStart.Should().BeGreaterOrEqualTo(0);
        aboutTabStart.Should().BeGreaterThan(generalStart);
        aboutSectionStart.Should().BeGreaterThan(aboutTabStart);
        advancedStart.Should().BeGreaterThan(aboutSectionStart);
    }

    [Fact]
    public void AllLanguages_HaveSettingsTabResources()
    {
        foreach (var languageDir in Directory.GetDirectories(StringsPath))
        {
            var reswPath = Path.Combine(languageDir, "Resources.resw");
            File.Exists(reswPath).Should().BeTrue($"{languageDir} should contain Resources.resw");

            var doc = XDocument.Load(reswPath);
            foreach (var key in ExpectedTabResourceKeys)
            {
                var element = doc.Descendants("data")
                    .FirstOrDefault(e => e.Attribute("name")?.Value == key);

                element.Should().NotBeNull($"{languageDir} should contain {key}");
                element!.Element("value")?.Value.Should().NotBeNullOrWhiteSpace(
                    $"{languageDir} should provide a non-empty {key} translation");
            }
        }
    }

    private static string GetSettingsTabsInitializer(string codeBehind)
    {
        var start = codeBehind.IndexOf("private readonly ObservableCollection<SettingsTabItem> _settingsTabs", StringComparison.Ordinal);
        start.Should().BeGreaterOrEqualTo(0, "SettingsPage should keep a declarative tab list");

        var end = codeBehind.IndexOf("];", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "SettingsPage tab list should use collection expression syntax");

        return codeBehind.Substring(start, end - start);
    }

    private static string FindProjectRoot()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "Easydict.Win32.sln")))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..");
    }
}
