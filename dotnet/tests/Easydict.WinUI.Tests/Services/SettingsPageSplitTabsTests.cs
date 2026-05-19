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
    private static readonly string SettingsPageFoundryLocalPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "SettingsPage.FoundryLocal.cs");
    private static readonly string AppCodeBehindPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "App.xaml.cs");
    private static readonly string ServiceResultItemXamlPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "Controls", "ServiceResultItem.xaml");
    private static readonly string ServiceResultItemCodeBehindPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "Controls", "ServiceResultItem.xaml.cs");
    private static readonly string MainPageCodeBehindPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "MainPage.xaml.cs");
    private static readonly string MiniWindowCodeBehindPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "MiniWindow.xaml.cs");
    private static readonly string FixedWindowCodeBehindPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Views", "FixedWindow.xaml.cs");
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
        "FoundryLocal_DocsLinkText",
        "FoundryLocal_StartButton",
        "FoundryLocal_Title_Ready",
        "FoundryLocal_Title_Unavailable",
        "FoundryLocal_Status_Checking",
        "FoundryLocal_Status_Ready",
        "FoundryLocal_Status_NotConfigured",
        "FoundryLocal_Status_NotInstalled",
        "FoundryLocal_Status_NotRunning",
        "FoundryLocal_Status_Starting",
        "FoundryLocal_Status_LoadingModel",
        "FoundryLocal_Status_StartFailed",
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
    public void SettingsPage_TabSwitchingUsesInlineLoadingRing()
    {
        var xaml = File.ReadAllText(SettingsPageXamlPath);
        var codeBehind = File.ReadAllText(SettingsPageCodeBehindPath);
        var onSettingsTabClick = GetMethodBody(codeBehind, "OnSettingsTabClick");
        var selectSettingsTabAsync = GetMethodBody(codeBehind, "SelectSettingsTabAsync");

        xaml.Should().Contain("x:Name=\"SettingsTabSwitchRing\"");
        xaml.Should().Contain("<ProgressRing x:Name=\"SettingsTabSwitchRing\"");
        xaml.Should().Contain("Width=\"20\"");
        xaml.Should().Contain("Visibility=\"Collapsed\"");
        xaml.Should().NotContain("x:Name=\"SettingsTabSwitchOverlay\"",
            "tab switching should show a lightweight inline indicator, not a masking overlay");
        onSettingsTabClick.Should().Contain("await SelectSettingsTabAsync(tabId, resetScroll: true);");
        selectSettingsTabAsync.Should().Contain("ShouldShowSettingsTabSwitchProgress(tabId)");
        selectSettingsTabAsync.Should().Contain("ShowSettingsTabSwitchProgress();");
        selectSettingsTabAsync.Should().Contain("await Task.Delay(SettingsTabSwitchIndicatorDelayMs)");
        selectSettingsTabAsync.Should().Contain("HideSettingsTabSwitchProgress();");
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
    public void SettingsPage_KeepsDeferredViewsTabLoadedDuringSettingsSession()
    {
        var codeBehind = File.ReadAllText(SettingsPageCodeBehindPath);
        var initializeSettingsContent = GetMethodBody(codeBehind, "InitializeSettingsContent");
        var selectSettingsTab = GetMethodBody(codeBehind, "SelectSettingsTab");
        var teardownOnUnload = GetMethodBody(codeBehind, "TeardownOnUnload");

        codeBehind.Should().Contain("SettingsTabFastSwitchWarmupOrder",
            "Settings tab contents should be warmed after first paint for fast in-page tab switching");
        initializeSettingsContent.Should().Contain("QueueSettingsTabWarmup(cancellationToken);",
            "the warm-up should be scoped to a live SettingsPage instance");
        selectSettingsTab.Should().Contain("ViewsTabContent.Visibility = tabId == SettingsTabId.Views ? Visibility.Visible : Visibility.Collapsed;");
        selectSettingsTab.Should().NotContain("ReleaseViewsTabContent();",
            "high-frequency tab switches should not rebuild the Views tab after it has been loaded");
        teardownOnUnload.Should().Contain("ReleaseViewsTabContent();",
            "leaving SettingsPage should still release lazily loaded tab content");
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
        xaml.Should().Contain("x:Name=\"FoundryLocalStatusBar\"");
        xaml.Should().Contain("x:Name=\"FoundryLocalStartButton\"");
        xaml.Should().Contain("x:Name=\"FoundryLocalInstallLink\"");
        xaml.Should().Contain("Tag=\"Auto\"");
        xaml.Should().Contain("Tag=\"WindowsAI\"");
        xaml.Should().Contain("Tag=\"FoundryLocal\"");
        xaml.Should().Contain("Tag=\"OpenVINO\"");
        codeBehind.Should().Contain("LocalAIResources.ProviderKeys.Auto");
        codeBehind.Should().Contain("LocalAIResources.ProviderKeys.FoundryLocal");
        codeBehind.Should().Contain("LocalAIProviderWindowsAILabelText");
        codeBehind.Should().Contain("LocalAIResources.RatingTooltipKeys.WindowsAI");
        codeBehind.Should().Contain("LocalAIResources.RatingTooltipKeys.FoundryLocal");
        codeBehind.Should().Contain("LocalAIResources.RatingTooltipKeys.OpenVINO");
        codeBehind.Should().Contain("SetLocalAiRating");
        codeBehind.Should().Contain("PhiSilicaResources.UiKeys.PrepareButton");
        codeBehind.Should().Contain("PhiSilicaResources.ProgressKeys.WindowsUpdateLink");
        codeBehind.Should().Contain("FoundryLocalResources.UiKeys.ConfigDescription");
        codeBehind.Should().Contain("FoundryLocalResources.UiKeys.DocsLinkText");
        codeBehind.Should().Contain("FoundryLocalResources.InstallDocumentationUrl");
        codeBehind.Should().Contain("FoundryLocalEndpointBox.TextChanged += OnSettingChanged");
        codeBehind.Should().Contain("_settings.FoundryLocalEndpoint");
        codeBehind.Should().Contain("LocalAIResources.ProviderKeys.OpenVINO");
        codeBehind.Should().Contain("UpdateLocalAIProviderDescription()");
        codeBehind.Should().Contain("OpenVinoResources.UiKeys.ConfigDescription");
        File.ReadAllText(SettingsPagePhiSilicaPath).Should().Contain("LocalAIResources.DescriptionKeys.WindowsAI");
        File.ReadAllText(SettingsPagePhiSilicaPath).Should().Contain("LocalAIResources.DescriptionKeys.FoundryLocal");
        File.ReadAllText(SettingsPagePhiSilicaPath).Should().Contain("LocalAIResources.DescriptionKeys.OpenVINO");
        File.ReadAllText(SettingsPagePhiSilicaPath).Should().Contain("ShowPhiSilicaPrepareProgress");
        File.ReadAllText(SettingsPagePhiSilicaPath).Should().Contain("PhiSilicaResources.ProgressKeys.Waiting");
        var foundryLocalCode = File.ReadAllText(SettingsPageFoundryLocalPath);
        foundryLocalCode.Should().Contain("GetFoundryLocalStatusAsync");
        foundryLocalCode.Should().Contain("PrepareFoundryLocalAsync");
        foundryLocalCode.Should().NotContain("PersistFoundryLocalSettingsForRuntime");
        foundryLocalCode.Should().NotContain("ReconfigureServices()");
        foundryLocalCode.Should().NotContain("_settings.FoundryLocalEndpoint =");
        foundryLocalCode.Should().NotContain("_settings.FoundryLocalModel =");
        xaml.Should().NotContain("x:Name=\"OpenVinoExpander\"");
    }

    [Fact]
    public void ServiceResultItem_ExposesFoundryLocalRecoveryActions()
    {
        var xaml = File.ReadAllText(ServiceResultItemXamlPath);
        var codeBehind = File.ReadAllText(ServiceResultItemCodeBehindPath);
        var mainPage = File.ReadAllText(MainPageCodeBehindPath);

        xaml.Should().Contain("x:Name=\"FoundryLocalRecoveryPanel\"");
        xaml.Should().Contain("x:Name=\"FoundryLocalStartButton\"");
        xaml.Should().Contain("x:Name=\"FoundryLocalDocsLink\"");
        xaml.Should().Contain("Padding=\"8,2\"");
        xaml.Should().Contain("MinHeight=\"24\"");
        codeBehind.Should().Contain("FoundryLocalStartRequested");
        codeBehind.Should().Contain("FoundryLocalResources.StartRecoveryAction");
        codeBehind.Should().Contain("FoundryLocalResources.InstallRecoveryAction");
        codeBehind.Should().Contain("FoundryLocalResources.InstallDocumentationUrl");
        mainPage.Should().Contain("OnFoundryLocalStartRequested");
        mainPage.Should().Contain("PrepareFoundryLocalAsync");
        mainPage.Should().Contain("FoundryLocalRecoveryCoordinator.StartAndRetryAsync");
        mainPage.Should().Contain("OnServiceQueryRequestedAsync(sender, serviceResult)");
    }

    [Fact]
    public void StreamingResultCompletion_ClearsLoadingStateBeforeSettingResult()
    {
        foreach (var path in new[] { MainPageCodeBehindPath, MiniWindowCodeBehindPath, FixedWindowCodeBehindPath })
        {
            var code = File.ReadAllText(path).Replace("\r\n", "\n");

            code.Should().MatchRegex(
                @"serviceResult\.IsLoading = false;\s+" +
                @"serviceResult\.IsStreaming = false;\s+" +
                @"serviceResult\.StreamingText = """";\s+" +
                @"serviceResult\.Result = result;",
                $"{Path.GetFileName(path)} should not leave a completed streaming result stuck in Translating");
            code.Should().Contain(
                "Streaming service returned an empty response",
                $"{Path.GetFileName(path)} should surface an empty stream as an error instead of a blank completed result");
        }
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
    public void SettingsPage_BackNavigationDefersUnloadTeardown()
    {
        var codeBehind = File.ReadAllText(SettingsPageCodeBehindPath);
        var onPageUnloaded = GetMethodBody(codeBehind, "OnPageUnloaded");
        var queueTeardown = GetMethodBody(codeBehind, "QueueTeardownOnUnload");
        var completeTeardown = GetMethodBody(codeBehind, "CompleteTeardownOnUnloadAsync");

        codeBehind.Should().Contain("DeferredUnloadTeardownDelayMs",
            "SettingsPage should keep the main-window return path responsive before reclaiming tab content");
        onPageUnloaded.Should().Contain("QueueTeardownOnUnload();");
        onPageUnloaded.Should().NotContain("        TeardownOnUnload();",
            "the unload handler should not synchronously walk and clear the full Settings visual tree");
        queueTeardown.Should().Contain("_lifetimeCts.Cancel();",
            "queued warm-up/deferred I/O work should stop immediately after navigation starts");
        completeTeardown.Should().Contain("await Task.Delay(DeferredUnloadTeardownDelayMs)");
        completeTeardown.Should().Contain("DispatcherQueuePriority.Low");
        completeTeardown.Should().Contain("TeardownOnUnload();",
            "the existing release path should still run after the main page has rendered");
    }

    [Fact]
    public void MainPage_BackNavigationDefersThemeChromeRefresh()
    {
        var mainPage = File.ReadAllText(MainPageCodeBehindPath);
        var app = File.ReadAllText(AppCodeBehindPath);
        var onNavigatedTo = GetMethodBody(mainPage, "OnNavigatedTo");
        var onPageLoaded = GetMethodBody(mainPage, "OnPageLoaded");
        var onRootFrameNavigated = GetMethodBody(app, "OnRootFrameNavigated");

        mainPage.Should().Contain("QueueApplyThemeChrome",
            "theme chrome refresh should be coalesced instead of run several times during Settings -> Main navigation");
        onNavigatedTo.Should().Contain("e.NavigationMode == NavigationMode.Back");
        onNavigatedTo.Should().Contain("_deferLoadedThemeChrome = true");
        onNavigatedTo.Should().Contain("DispatcherQueuePriority.Low");
        onPageLoaded.Should().Contain("_deferLoadedThemeChrome");
        onPageLoaded.Should().Contain("QueueApplyThemeChrome(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low)");
        onRootFrameNavigated.Should().Contain("e.NavigationMode == NavigationMode.Back && frame.Content is MainPage");
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
        var prefixes = new[]
        {
            "private void",
            "private async void",
            "private async Task",
            "protected override void"
        };
        var start = prefixes
            .Select(prefix => codeBehind.IndexOf($"{prefix} {methodName}(", StringComparison.Ordinal))
            .Where(index => index >= 0)
            .DefaultIfEmpty(-1)
            .Min();
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
