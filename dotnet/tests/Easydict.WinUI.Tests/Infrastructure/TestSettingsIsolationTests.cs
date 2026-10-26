using System.Reflection;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Infrastructure;

public sealed class TestSettingsIsolationTests
{
    [Fact]
    public void SingletonUsesTestProcessDirectoryBeforeAnySettingsAccess()
    {
        var configuredDirectory = Environment.GetEnvironmentVariable(
            TestSettingsIsolation.SettingsDirectoryEnvironmentVariable);
        configuredDirectory.Should().NotBeNullOrWhiteSpace();

        if (TestSettingsIsolation.AutomaticallyAllocatedDirectory is { } automaticDirectory)
        {
            Path.GetFullPath(configuredDirectory!).Should().Be(Path.GetFullPath(automaticDirectory));
        }
        else if (Environment.GetEnvironmentVariable("EASYDICT_TEST_EXPECT_AUTO_SETTINGS_ISOLATION") == "1")
        {
            TestSettingsIsolation.AutomaticallyAllocatedDirectory.Should().NotBeNullOrWhiteSpace(
                "the verification run intentionally starts without EASYDICT_SETTINGS_DIR");
        }

        Path.GetFullPath(SettingsService.ResolveSettingsDirectory())
            .Should().Be(Path.GetFullPath(configuredDirectory!));

        var instance = SettingsService.Instance;
        var settingsFilePath = typeof(SettingsService)
            .GetField("_settingsFilePath", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance)
            .Should().BeOfType<string>().Subject;

        Path.GetFullPath(settingsFilePath)
            .Should().Be(Path.GetFullPath(Path.Combine(configuredDirectory!, "settings.json")));
    }
}
