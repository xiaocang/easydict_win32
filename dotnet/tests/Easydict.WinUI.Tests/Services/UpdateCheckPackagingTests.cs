using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public class UpdateCheckPackagingTests
{
    private static readonly string ProjectRoot = FindProjectRoot();

    [Fact]
    public void WinUiProject_UsesExplicitPortableUpdateCheckCompilationSwitch()
    {
        var csproj = File.ReadAllText(Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.WinUI",
            "Easydict.WinUI.csproj"));

        csproj.Should().Contain("<EnableGitHubUpdateCheck Condition=\"'$(EnableGitHubUpdateCheck)' == ''\">false</EnableGitHubUpdateCheck>");
        csproj.Should().Contain("PORTABLE_UPDATE_CHECK");
    }

    [Fact]
    public void ReleaseWorkflow_EnablesUpdateCheckOnlyForPortablePublish()
    {
        var workflow = File.ReadAllText(Path.Combine(
            ProjectRoot,
            "..",
            ".github",
            "workflows",
            "release-publish.yml"));

        var portableStart = workflow.IndexOf("- name: Publish WinUI App (portable)", StringComparison.Ordinal);
        var msixStart = workflow.IndexOf("- name: Publish WinUI App (MSIX)", StringComparison.Ordinal);
        portableStart.Should().BeGreaterThanOrEqualTo(0);
        msixStart.Should().BeGreaterThan(portableStart);

        var portableBlock = workflow[portableStart..msixStart];
        var msixBlock = workflow[msixStart..];
        portableBlock.Should().Contain("-p:EnableGitHubUpdateCheck=true");
        portableBlock.Should().NotContain("-p:EnableGitHubUpdateCheck=false");
        msixBlock.Should().Contain("-p:EnableGitHubUpdateCheck=false");
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
