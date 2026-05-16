using System.Text.RegularExpressions;
using System.Xml.Linq;
using Easydict.WinUI.Services;
using Easydict.WinUI.Views;
using FluentAssertions;
using Microsoft.UI.Xaml;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class SettingsPageSplitTabsTests
{
    private static readonly string ProjectRoot = FindProjectRoot();
    private static readonly string SettingsPageXamlPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "SettingsPage.xaml");
    private static readonly string SettingsPageCodeBehindPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "SettingsPage.xaml.cs");
    private static readonly string SettingsPagePhiSilicaPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "SettingsPage.PhiSilica.cs");
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
        "WindowResultsDescription",
        "LocalAI_Title",
        "LocalAI_ProviderLabel",
        "LocalAI_ProviderAutomationName",
        "LocalAI_Provider_Auto",
        "LocalAI_Provider_WindowsAI",
        "LocalAI_Provider_FoundryLocal",
        "LocalAI_Provider_OpenVINO",
        "LocalAI_Rating_WindowsAI",
        "LocalAI_Rating_WindowsAI_Tooltip",
        "LocalAI_Rating_FoundryLocal",
        "LocalAI_Rating_FoundryLocal_Tooltip",
        "LocalAI_Rating_OpenVINO",
        "LocalAI_Rating_OpenVINO_Tooltip",
        "LocalAI_Description",
        "LocalAI_Description_Auto",
        "LocalAI_Description_WindowsAI",
        "LocalAI_Description_FoundryLocal",
        "LocalAI_Description_OpenVINO",
        "LocalAI_WindowsAISectionTitle",
        "FoundryLocal_ConfigTitle",
        "FoundryLocal_EndpointLabel",
        "FoundryLocal_EndpointPlaceholder",
        "FoundryLocal_ModelLabel",
        "FoundryLocal_ConfigDescription",
        "FoundryLocal_InstallLinkText",
        "FoundryLocal_Status_Ready",
        "FoundryLocal_Status_NotConfigured",
        "FoundryLocal_Status_NotRunning",
        "OpenVINO_ConfigTitle",
        "OpenVINO_ConfigDescription",
        "PhiSilicaModelPrompt_Title",
        "PhiSilicaModelPrompt_Message",
        "PhiSilicaModelPrompt_DownloadNow",
        "PhiSilicaModelPrompt_NotNow",
        "PhiSilicaModelPrompt_Disable",
        "WindowsLocalAI_PrepareButton",
        "PhiSilicaPreparationProgress_Checking",
        "PhiSilicaPreparationProgress_Requesting",
        "PhiSilicaPreparationProgress_Waiting",
        "PhiSilicaPreparationProgress_Finalizing",
        "PhiSilicaPreparationProgress_ReusingExisting",
        "PhiSilicaPreparationProgress_DeliveryOptimizationEstimate",
        "PhiSilicaPreparationProgress_TimeUnknown",
        "PhiSilicaPreparationProgress_WindowsUpdateLink",
        "WindowsLocalAI_Status_UnsupportedWindowsAIBaseline"
    ];

    private static readonly string[] ExpectedServiceConfigurationIconAssets =
    [
        "DeepL",
        "windows-local-ai",
        "Ollama",
        "OpenAI",
        "DeepSeek",
        "Groq",
        "Zhipu",
        "GitHub",
        "Gemini",
        "CustomOpenAI",
        "BuiltInAI",
        "Doubao",
        "Caiyun",
        "NiuTrans",
        "Youdao",
        "Google",
        "Linguee"
    ];

    private static readonly string[] ExpectedThemeVariantIconAssets =
    [
        ServiceIconAssetResolver.GitHubOnLightIconName
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
    public void SettingsPage_ServiceConfiguration_PutsLocalAiBeforeCloudAi()
    {
        var xaml = File.ReadAllText(SettingsPageXamlPath);
        var windowsLocalAiIndex = xaml.IndexOf("x:Name=\"WindowsLocalAIExpander\"", StringComparison.Ordinal);
        var localAiProviderIndex = xaml.IndexOf("x:Name=\"LocalAIProviderCombo\"", StringComparison.Ordinal);
        var openVinoIndex = xaml.IndexOf("x:Name=\"OpenVinoConfigPanel\"", StringComparison.Ordinal);
        var ollamaIndex = xaml.IndexOf("x:Name=\"OllamaEndpointBox\"", StringComparison.Ordinal);
        var openAiIndex = xaml.IndexOf("x:Name=\"OpenAIKeyBox\"", StringComparison.Ordinal);

        windowsLocalAiIndex.Should().BeGreaterOrEqualTo(0);
        localAiProviderIndex.Should().BeGreaterThan(windowsLocalAiIndex);
        openVinoIndex.Should().BeGreaterOrEqualTo(0);
        ollamaIndex.Should().BeGreaterOrEqualTo(0);
        openAiIndex.Should().BeGreaterOrEqualTo(0);

        windowsLocalAiIndex.Should().BeLessThan(openAiIndex);
        openVinoIndex.Should().BeLessThan(openAiIndex);
        ollamaIndex.Should().BeLessThan(openAiIndex);
    }

    [Fact]
    public void SettingsPage_WindowServiceOrder_PutsLocalAiNearTop()
    {
        var openAiOrder = SettingsPage.GetSettingsServiceDisplayOrder("openai", registrationIndex: 0);

        SettingsPage.GetSettingsServiceDisplayOrder("windows-local-ai", registrationIndex: 999)
            .Should().BeLessThan(openAiOrder);
        SettingsPage.GetSettingsServiceDisplayOrder("ollama", registrationIndex: 999)
            .Should().BeLessThan(openAiOrder);
    }

    [Fact]
    public void SettingsPage_LocalAiProviderCombo_ConfiguresLocalAiProvidersTogether()
    {
        var xaml = File.ReadAllText(SettingsPageXamlPath);
        var codeBehind = File.ReadAllText(SettingsPageCodeBehindPath);

        xaml.Should().Contain("x:Name=\"LocalAIProviderCombo\"");
        xaml.Should().Contain("x:Name=\"LocalAIProviderAutoItem\"");
        xaml.Should().Contain("x:Name=\"LocalAIProviderWindowsAIItem\"");
        xaml.Should().Contain("x:Name=\"LocalAIProviderFoundryLocalItem\"");
        xaml.Should().Contain("x:Name=\"LocalAIProviderOpenVINOItem\"");
        xaml.Should().Contain("x:Name=\"LocalAIProviderWindowsAIRatingText\"");
        xaml.Should().Contain("x:Name=\"LocalAIProviderFoundryLocalRatingText\"");
        xaml.Should().Contain("x:Name=\"LocalAIProviderOpenVINORatingText\"");
        xaml.Should().Contain("x:Name=\"WindowsLocalAISectionRatingText\"");
        xaml.Should().Contain("x:Name=\"WindowsLocalAIPrepareProgressPanel\"");
        xaml.Should().Contain("x:Name=\"WindowsLocalAIPrepareProgressText\"");
        xaml.Should().Contain("x:Name=\"WindowsLocalAIPrepareProgressBar\"");
        xaml.Should().Contain("x:Name=\"WindowsLocalAIWindowsUpdateLink\"");
        xaml.Should().Contain("ms-settings:windowsupdate");
        xaml.Should().Contain("x:Name=\"FoundryLocalRatingText\"");
        xaml.Should().Contain("x:Name=\"OpenVinoRatingText\"");
        xaml.Should().Contain("Text=\"★★★★★\"");
        xaml.Should().Contain("Text=\"★★★★\"");
        xaml.Should().Contain("Text=\"★★\"");
        xaml.Should().Contain("ToolTipService.ToolTip");
        xaml.Should().NotContain("Segoe UI Emoji");
        xaml.Should().Contain("x:Name=\"FoundryLocalConfigPanel\"");
        xaml.Should().Contain("x:Name=\"FoundryLocalEndpointBox\"");
        xaml.Should().Contain("x:Name=\"FoundryLocalModelBox\"");
        xaml.Should().Contain("x:Name=\"FoundryLocalInstallLink\"");
        xaml.Should().Contain("Tag=\"Auto\"");
        xaml.Should().Contain("Tag=\"WindowsAI\"");
        xaml.Should().Contain("Tag=\"FoundryLocal\"");
        xaml.Should().Contain("Tag=\"OpenVINO\"");
        codeBehind.Should().Contain("LocalAI_Provider_Auto");
        codeBehind.Should().Contain("LocalAI_Provider_FoundryLocal");
        codeBehind.Should().Contain("LocalAIProviderWindowsAILabelText");
        codeBehind.Should().Contain("LocalAI_Rating_WindowsAI_Tooltip");
        codeBehind.Should().Contain("LocalAI_Rating_FoundryLocal_Tooltip");
        codeBehind.Should().Contain("LocalAI_Rating_OpenVINO_Tooltip");
        codeBehind.Should().Contain("SetLocalAiRating");
        codeBehind.Should().Contain("WindowsLocalAI_PrepareButton");
        codeBehind.Should().Contain("PhiSilicaPreparationProgress_WindowsUpdateLink");
        codeBehind.Should().Contain("FoundryLocal_ConfigDescription");
        codeBehind.Should().Contain("FoundryLocal_InstallLinkText");
        codeBehind.Should().Contain("FoundryLocalService.InstallDocumentationUrl");
        codeBehind.Should().Contain("FoundryLocalEndpointBox.TextChanged += OnSettingChanged");
        codeBehind.Should().Contain("_settings.FoundryLocalEndpoint");
        codeBehind.Should().Contain("LocalAI_Provider_OpenVINO");
        codeBehind.Should().Contain("UpdateLocalAIProviderDescription()");
        codeBehind.Should().Contain("OpenVINO_ConfigDescription");
        File.ReadAllText(SettingsPagePhiSilicaPath).Should().Contain("LocalAI_Description_WindowsAI");
        File.ReadAllText(SettingsPagePhiSilicaPath).Should().Contain("LocalAI_Description_FoundryLocal");
        File.ReadAllText(SettingsPagePhiSilicaPath).Should().Contain("LocalAI_Description_OpenVINO");
        File.ReadAllText(SettingsPagePhiSilicaPath).Should().Contain("ShowPhiSilicaPrepareProgress");
        File.ReadAllText(SettingsPagePhiSilicaPath).Should().Contain("PhiSilicaPreparationProgress_Waiting");
        xaml.Should().NotContain("x:Name=\"OpenVinoExpander\"");
    }

    [Fact]
    public void SettingsPage_PhiSilicaProgressResumesFromSharedCoordinator()
    {
        var codeBehind = File.ReadAllText(SettingsPagePhiSilicaPath);

        codeBehind.Should().Contain("SyncPhiSilicaPreparationProgressFromCoordinator");
        GetMethodBody(codeBehind, "InitializePhiSilicaPanel")
            .Should().Contain("SyncPhiSilicaPreparationProgressFromCoordinator();");
        GetMethodBody(codeBehind, "ShowPhiSilicaPrepareProgress")
            .Should().Contain("PhiSilicaModelPreparationCoordinator.Instance.CreatePreparingSnapshot(resourceKey)");
    }

    [Fact]
    public void WindowsLocalAi_UnavailableStatusPointsToLocalFallback()
    {
        foreach (var languageDir in Directory.GetDirectories(StringsPath))
        {
            var reswPath = Path.Combine(languageDir, "Resources.resw");
            var doc = XDocument.Load(reswPath);
            var hardware = GetResourceValue(doc, "WindowsLocalAI_Status_NotCompatibleHardware");
            var unsupported = GetResourceValue(doc, "WindowsLocalAI_Status_NotSupported");

            hardware.Should().Contain("OpenVINO", $"{languageDir} should point users to the local fallback");
            unsupported.Should().Contain("OpenVINO", $"{languageDir} should point users to the local fallback");
            hardware.Should().Contain("NLLB-200", $"{languageDir} should clarify which local fallback is used");
            unsupported.Should().Contain("NLLB-200", $"{languageDir} should clarify which local fallback is used");
            hardware.Should().NotContain("Ollama");
            unsupported.Should().NotContain("Ollama");
        }
    }

    [Fact]
    public void WindowsLocalAi_ModelPreparationCopySetsSeveralGbExpectation()
    {
        foreach (var languageDir in Directory.GetDirectories(StringsPath))
        {
            var reswPath = Path.Combine(languageDir, "Resources.resw");
            var doc = XDocument.Load(reswPath);

            GetResourceValue(doc, "PhiSilicaModelPrompt_Message")
                .Should().Contain("GB", $"{languageDir} should set a practical first-use download expectation");
            GetResourceValue(doc, "WindowsLocalAI_Status_NotReady")
                .Should().Contain("GB", $"{languageDir} should set a practical Settings prepare expectation");
            GetResourceValue(doc, "PhiSilicaPreparationProgress_Waiting")
                .Should().Contain("GB", $"{languageDir} should keep the size expectation visible during preparation");
        }
    }

    [Fact]
    public void SettingsPage_LocalAiProviderSelectionUsesSaveWorkflow()
    {
        var codeBehind = File.ReadAllText(SettingsPagePhiSilicaPath);
        var method = GetMethodBody(codeBehind, "OnLocalAIProviderChanged");

        method.Should().Contain("UpdateLocalAIProviderPanels()");
        method.Should().Contain("OnSettingChanged(sender, e)");
        method.Should().NotContain("_settings.Save()");
        method.Should().NotContain("ReconfigureServices()");
    }

    [Fact]
    public void SettingsPage_LocalAiProviderSelectionShowsAllConfigsInAutoAndHighlightsFirstAvailable()
    {
        var codeBehind = File.ReadAllText(SettingsPagePhiSilicaPath);
        var method = GetMethodBody(codeBehind, "UpdateLocalAIProviderPanels");

        method.Should().Contain("mode == LocalAIProviderMode.Auto || mode == LocalAIProviderMode.WindowsAI");
        method.Should().Contain("mode == LocalAIProviderMode.Auto || mode == LocalAIProviderMode.FoundryLocal");
        method.Should().Contain("mode == LocalAIProviderMode.Auto || mode == LocalAIProviderMode.OpenVINO");
        method.Should().Contain("UpdateLocalAIProviderPanelEmphasis(mode)");

        var firstAvailableStart = codeBehind.IndexOf(
            "private LocalAIProviderMode? GetFirstAvailableLocalAIProviderMode()",
            StringComparison.Ordinal);
        firstAvailableStart.Should().BeGreaterThanOrEqualTo(0);
        var firstAvailableEnd = codeBehind.IndexOf(
            "private static void SetLocalAIProviderPanelEmphasis",
            firstAvailableStart,
            StringComparison.Ordinal);
        firstAvailableEnd.Should().BeGreaterThan(firstAvailableStart);
        var firstAvailableMethod = codeBehind[firstAvailableStart..firstAvailableEnd];
        firstAvailableMethod.IndexOf("LocalAIProviderMode.WindowsAI", StringComparison.Ordinal)
            .Should()
            .BeLessThan(firstAvailableMethod.IndexOf("LocalAIProviderMode.FoundryLocal", StringComparison.Ordinal));
        firstAvailableMethod.IndexOf("LocalAIProviderMode.FoundryLocal", StringComparison.Ordinal)
            .Should()
            .BeLessThan(firstAvailableMethod.IndexOf("LocalAIProviderMode.OpenVINO", StringComparison.Ordinal));

        codeBehind.Should().Contain("var fontSize = isPrimary ? LocalAIPrimaryTitleFontSize : LocalAISecondaryTitleFontSize");
    }

    [Fact]
    public void WindowsLocalAi_HasServiceIconAssets()
    {
        var iconDir = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Assets", "ServiceIcons");
        foreach (var scale in new[] { 100, 125, 150, 175, 200 })
        {
            var iconPath = Path.Combine(iconDir, $"windows-local-ai.scale-{scale}.png");
            File.Exists(iconPath).Should().BeTrue(
                $"the windows-local-ai service should have a scale-{scale} icon asset");
            new FileInfo(iconPath).Length.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void SettingsPage_ServiceConfigurationUsesServiceIconsWhenAvailable()
    {
        var xaml = File.ReadAllText(SettingsPageXamlPath);
        var codeBehind = File.ReadAllText(SettingsPageCodeBehindPath);
        var iconDir = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Assets", "ServiceIcons");

        codeBehind.Should().Contain("InitializeServiceConfigurationHeaderIcons();");
        codeBehind.Should().Contain("ServiceIconAssetResolver.GetIconUri");
        codeBehind.Should().NotContain("Foreground = title.Foreground");

        foreach (var assetName in ExpectedServiceConfigurationIconAssets)
        {
            File.Exists(Path.Combine(iconDir, $"{assetName}.scale-100.png")).Should().BeTrue(
                $"Settings service configuration references the {assetName} icon asset");

            if (assetName is "Google" or "Linguee")
            {
                xaml.Should().Contain($"Source=\"ms-appx:///Assets/ServiceIcons/{assetName}.png\"");
            }
            else
            {
                xaml.Should().Contain($"Tag=\"{assetName}\"");
            }
        }

        foreach (var assetName in ExpectedThemeVariantIconAssets)
        {
            File.Exists(Path.Combine(iconDir, $"{assetName}.scale-100.png")).Should().BeTrue(
                $"theme-specific service icon asset {assetName} should be available");
        }
    }

    [Fact]
    public void ServiceIconAssetResolver_UsesDarkGitHubIconOnLightTheme()
    {
        ServiceIconAssetResolver.GetIconName("github", ElementTheme.Light)
            .Should().Be(ServiceIconAssetResolver.GitHubOnLightIconName);
        ServiceIconAssetResolver.GetIconName("GitHub", ElementTheme.Default)
            .Should().Be(ServiceIconAssetResolver.GitHubOnLightIconName);
        ServiceIconAssetResolver.GetIconName("GitHub", ElementTheme.Dark)
            .Should().Be("GitHub");
        ServiceIconAssetResolver.GetIconName("OpenAI", ElementTheme.Light)
            .Should().Be("OpenAI");
    }

    [Fact]
    public void SettingsPage_BackNavigationShowsLoadingOverlay()
    {
        var xaml = File.ReadAllText(SettingsPageXamlPath);
        var codeBehind = File.ReadAllText(SettingsPageCodeBehindPath);
        var onBackClick = GetMethodBody(codeBehind, "OnBackClick");

        xaml.Should().Contain("x:Name=\"NavigationLoadingOverlay\"");
        xaml.Should().Contain("x:Name=\"NavigationLoadingRing\"");
        xaml.Should().Contain("Canvas.ZIndex=\"100\"");
        onBackClick.Should().Contain("await ShowNavigationLoadingOverlayAsync()");
        onBackClick.Should().Contain("HideNavigationLoadingOverlay()");
        onBackClick.Should().Contain("Frame.GoBack()");
        codeBehind.Should().Contain("NavigationLoadingRing.IsActive = true");
        codeBehind.Should().Contain("await Task.Delay(50)");
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

    private static string GetResourceValue(XDocument doc, string key)
    {
        var element = doc.Descendants("data")
            .FirstOrDefault(e => e.Attribute("name")?.Value == key);

        element.Should().NotBeNull($"Resources.resw should contain {key}");
        return element!.Element("value")?.Value ?? string.Empty;
    }

    private static string GetMethodBody(string codeBehind, string methodName)
    {
        var start = codeBehind.IndexOf($"private void {methodName}", StringComparison.Ordinal);
        if (start < 0)
        {
            start = codeBehind.IndexOf($"private async void {methodName}", StringComparison.Ordinal);
        }
        start.Should().BeGreaterOrEqualTo(0, $"{methodName} should exist");

        var braceStart = codeBehind.IndexOf('{', start);
        braceStart.Should().BeGreaterThan(start, $"{methodName} should have a body");

        var depth = 0;
        for (var i = braceStart; i < codeBehind.Length; i++)
        {
            if (codeBehind[i] == '{')
            {
                depth++;
            }
            else if (codeBehind[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return codeBehind.Substring(braceStart, i - braceStart + 1);
                }
            }
        }

        throw new InvalidOperationException($"Could not parse {methodName} body.");
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
