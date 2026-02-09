using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for LocalizationService configuration and resource files.
/// These tests verify that localization is properly configured for both
/// MSIX packaged and unpackaged (dotnet run) scenarios.
/// Note: Category="Configuration" to run in CI (Category!=WinUI filter).
/// </summary>
[Trait("Category", "Configuration")]
public class LocalizationServiceTests
{
    private static readonly string ProjectRoot = FindProjectRoot();
    private static readonly string StringsPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Strings");

    /// <summary>
    /// All supported UI languages that must have resource files.
    /// </summary>
    private static readonly string[] SupportedLanguages =
        { "en-US", "zh-CN", "zh-TW", "ja-JP", "ko-KR", "fr-FR", "de-DE",
          "vi-VN", "th-TH", "ar-SA", "id-ID", "it-IT", "ms-MY", "hi-IN", "da-DK" };

    #region Resource File Structure Tests

    [Fact]
    public void StringsFolder_Exists()
    {
        Directory.Exists(StringsPath).Should().BeTrue(
            $"Strings folder should exist at {StringsPath}");
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    [InlineData("zh-TW")]
    [InlineData("ja-JP")]
    [InlineData("ko-KR")]
    [InlineData("fr-FR")]
    [InlineData("de-DE")]
    [InlineData("vi-VN")]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    [InlineData("id-ID")]
    [InlineData("it-IT")]
    [InlineData("ms-MY")]
    [InlineData("hi-IN")]
    [InlineData("da-DK")]
    public void LanguageFolder_Exists(string language)
    {
        var languagePath = Path.Combine(StringsPath, language);
        Directory.Exists(languagePath).Should().BeTrue(
            $"Language folder should exist: {languagePath}");
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    [InlineData("zh-TW")]
    [InlineData("ja-JP")]
    [InlineData("ko-KR")]
    [InlineData("fr-FR")]
    [InlineData("de-DE")]
    [InlineData("vi-VN")]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    [InlineData("id-ID")]
    [InlineData("it-IT")]
    [InlineData("ms-MY")]
    [InlineData("hi-IN")]
    [InlineData("da-DK")]
    public void ResourcesResw_Exists(string language)
    {
        var reswPath = Path.Combine(StringsPath, language, "Resources.resw");
        File.Exists(reswPath).Should().BeTrue(
            $"Resources.resw should exist: {reswPath}");
    }

    [Fact]
    public void AllLanguages_HaveResourceFiles()
    {
        foreach (var lang in SupportedLanguages)
        {
            var reswPath = Path.Combine(StringsPath, lang, "Resources.resw");
            File.Exists(reswPath).Should().BeTrue(
                $"Missing Resources.resw for language: {lang}");
        }
    }

    [Fact]
    public void ResourceFiles_AreValidXml()
    {
        foreach (var lang in SupportedLanguages)
        {
            var reswPath = Path.Combine(StringsPath, lang, "Resources.resw");
            if (File.Exists(reswPath))
            {
                var content = File.ReadAllText(reswPath);
                content.Should().StartWith("<?xml", $"{lang}/Resources.resw should be valid XML");
                content.Should().Contain("<root>", $"{lang}/Resources.resw should have root element");
            }
        }
    }

    #endregion

    #region Key Resource Verification Tests

    [Theory]
    [InlineData("en-US", "StatusReady", "Ready")]
    [InlineData("zh-CN", "StatusReady", "就绪")]
    [InlineData("ja-JP", "StatusReady", "準備完了")]
    public void ResourceFile_ContainsKey(string language, string key, string expectedValue)
    {
        var reswPath = Path.Combine(StringsPath, language, "Resources.resw");
        var content = File.ReadAllText(reswPath);

        content.Should().Contain($"name=\"{key}\"",
            $"{language}/Resources.resw should contain key '{key}'");
        content.Should().Contain($"<value>{expectedValue}</value>",
            $"{language}/Resources.resw should have correct value for '{key}'");
    }

    [Theory]
    [InlineData("AppName")]
    [InlineData("StatusReady")]
    [InlineData("StatusTranslating")]
    [InlineData("InputPlaceholder")]
    [InlineData("Settings")]
    [InlineData("Copy")]
    [InlineData("EnableInternationalServices")]
    [InlineData("EnableInternationalServicesDescription")]
    [InlineData("InternationalServiceUnavailableHint")]
    public void AllLanguages_HaveRequiredKey(string key)
    {
        foreach (var lang in SupportedLanguages)
        {
            var reswPath = Path.Combine(StringsPath, lang, "Resources.resw");
            var content = File.ReadAllText(reswPath);
            content.Should().Contain($"name=\"{key}\"",
                $"{lang}/Resources.resw should contain key '{key}'");
        }
    }

    [Theory]
    [InlineData("en-US", "Bing Translate")]
    [InlineData("zh-CN", "必应翻译")]
    [InlineData("zh-TW", "Bing 翻譯")]
    [InlineData("ja-JP", "Bing翻訳")]
    [InlineData("ko-KR", "Bing 번역")]
    [InlineData("fr-FR", "Bing Traduction")]
    [InlineData("de-DE", "Bing Übersetzer")]
    public void InternationalServiceUnavailableHint_MentionsBingAlternative(string language, string bingName)
    {
        var reswPath = Path.Combine(StringsPath, language, "Resources.resw");
        var content = File.ReadAllText(reswPath);

        // The hint should mention Bing as a regional alternative
        content.Should().Contain($"name=\"InternationalServiceUnavailableHint\"",
            $"{language} should have InternationalServiceUnavailableHint key");
        content.Should().Contain(bingName,
            $"{language} hint should mention Bing Translate ({bingName}) as alternative");
    }

    #endregion

    #region Project Configuration Tests

    [Fact]
    public void Csproj_ReliesOnSdkAutoDiscoveryForPriResources()
    {
        // The .NET SDK auto-discovers resw files in Strings/ folders.
        // Explicit <PRIResource Include> causes duplicate item errors.
        var csprojPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Easydict.WinUI.csproj");
        var doc = XDocument.Load(csprojPath);
        var priResources = doc.Descendants()
            .Where(element => element.Name.LocalName == "PRIResource")
            .ToList();

        priResources.Should().BeEmpty(
            "csproj should NOT have explicit PRIResource Include - SDK auto-discovers resw files");
    }

    [Fact]
    public void Csproj_HasGeneratePriFileEnabled()
    {
        var csprojPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Easydict.WinUI.csproj");
        var content = File.ReadAllText(csprojPath);

        content.Should().Contain("<GeneratePriFile>true</GeneratePriFile>",
            "csproj should have GeneratePriFile=true");
    }

    [Fact]
    public void Csproj_HasAppxGeneratePriEnabled()
    {
        var csprojPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Easydict.WinUI.csproj");
        var content = File.ReadAllText(csprojPath);

        content.Should().Contain("<AppxGeneratePriEnabled>true</AppxGeneratePriEnabled>",
            "csproj should have AppxGeneratePriEnabled=true for unpackaged mode support");
    }

    [Fact]
    public void Csproj_HasGeneratePriConfigXmlFile()
    {
        var csprojPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Easydict.WinUI.csproj");
        var content = File.ReadAllText(csprojPath);

        content.Should().Contain("<GeneratePriConfigXmlFile>true</GeneratePriConfigXmlFile>",
            "csproj should have GeneratePriConfigXmlFile=true");
    }

    [Fact]
    public void Csproj_HasVerifyPriFileTarget()
    {
        var csprojPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Easydict.WinUI.csproj");
        var content = File.ReadAllText(csprojPath);

        content.Should().Contain("Name=\"VerifyPriFile\"",
            "csproj should have VerifyPriFile target for build-time verification");
    }

    [Fact]
    public void Csproj_HasDefaultLanguage()
    {
        var csprojPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Easydict.WinUI.csproj");
        var content = File.ReadAllText(csprojPath);

        content.Should().Contain("<DefaultLanguage>en-US</DefaultLanguage>",
            "csproj should have DefaultLanguage set to en-US");
    }

    #endregion

    #region Package Manifest Tests

    [Fact]
    public void AppxManifest_HasAllLanguageResources()
    {
        var manifestPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Package.appxmanifest");
        var content = File.ReadAllText(manifestPath);

        foreach (var lang in SupportedLanguages)
        {
            var langLower = lang.ToLowerInvariant();
            content.Should().Contain($"Language=\"{langLower}\"",
                $"Package.appxmanifest should declare resource for {lang}");
        }
    }

    [Fact]
    public void AppxManifest_HasResourcesSection()
    {
        var manifestPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Package.appxmanifest");
        var content = File.ReadAllText(manifestPath);

        content.Should().Contain("<Resources>",
            "Package.appxmanifest should have Resources section");
        content.Should().Contain("</Resources>",
            "Package.appxmanifest Resources section should be closed");
    }

    #endregion

    #region Build Script Tests

    [Fact]
    public void PackageScript_VerifiesPriFile()
    {
        var scriptPath = Path.Combine(ProjectRoot, "scripts", "package-and-install.ps1");
        var content = File.ReadAllText(scriptPath);

        content.Should().Contain("resources.pri",
            "Package script should check for resources.pri");
        content.Should().Contain("Test-Path",
            "Package script should use Test-Path to verify resources.pri");
    }

    #endregion

    #region VSCode Configuration Tests

    [Fact]
    public void VscodeTasks_HasUnpackagedBuildTask()
    {
        var tasksPath = Path.Combine(ProjectRoot, "..", ".vscode", "tasks.json");
        if (File.Exists(tasksPath))
        {
            var content = File.ReadAllText(tasksPath);
            content.Should().Contain("WindowsPackageType=None",
                "tasks.json should have unpackaged build configuration");
        }
    }

    #endregion

    #region Helper Methods

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

        // Fallback for test runner
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..");
    }

    #endregion
}

/// <summary>
/// Tests specifically for verifying build mode consistency between
/// MSIX packaged and unpackaged (dotnet run) scenarios.
/// Note: Category="Configuration" to run in CI (Category!=WinUI filter).
/// </summary>
[Trait("Category", "Configuration")]
[Trait("Category", "BuildConfig")]
public class BuildModeConsistencyTests
{
    private static readonly string ProjectRoot = FindProjectRoot();

    [Fact]
    public void BothBuildModes_ShouldGeneratePri()
    {
        // This test verifies the csproj is configured to generate PRI
        // in both packaged and unpackaged modes
        var csprojPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Easydict.WinUI.csproj");
        var content = File.ReadAllText(csprojPath);

        // These properties ensure PRI generation in both modes
        content.Should().Contain("<GeneratePriFile>true</GeneratePriFile>");
        content.Should().Contain("<AppxGeneratePriEnabled>true</AppxGeneratePriEnabled>");

        // The comment should explain the purpose
        content.Should().Contain("packaged and unpackaged modes",
            "csproj should document that PRI is generated for both modes");
    }

    [Fact]
    public void Csproj_HasBypassFrameworkInstallChecks()
    {
        var csprojPath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Easydict.WinUI.csproj");
        var content = File.ReadAllText(csprojPath);

        content.Should().Contain("<BypassFrameworkInstallChecks>true</BypassFrameworkInstallChecks>",
            "csproj should have BypassFrameworkInstallChecks for build compatibility");
    }

    [Fact]
    public void LocalizationService_HasPackageDetection()
    {
        var servicePath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Services", "LocalizationService.cs");
        var content = File.ReadAllText(servicePath);

        content.Should().Contain("IsRunningAsPackaged",
            "LocalizationService should detect packaged vs unpackaged mode");
    }

    [Fact]
    public void LocalizationService_HasDiagnosticLogging()
    {
        var servicePath = Path.Combine(ProjectRoot, "src", "Easydict.WinUI", "Services", "LocalizationService.cs");
        var content = File.ReadAllText(servicePath);

        content.Should().Contain("Debug.WriteLine",
            "LocalizationService should have diagnostic logging");
        content.Should().Contain("ResourceMap has",
            "LocalizationService should log resource count for debugging");
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
