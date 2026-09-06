using System.Text.Json;
using Easydict.UIAutomation.Tests.Infrastructure;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static Easydict.UIAutomation.Tests.Tests.SavedItemsVisualTests;

namespace Easydict.UIAutomation.Tests.Tests;

[Trait("Category", "UIAutomation")]
[Collection("UIAutomation")]
public sealed class LiveQueryFavoriteTests(ITestOutputHelper output) : IDisposable
{
    private readonly string? _previousDiagnostics = Environment.GetEnvironmentVariable("EASYDICT_SAVED_ITEMS_DIAGNOSTICS");

    [Fact]
    public void LiveResult_FavoriteSurvivesMinimalRendererRoundTrip()
    {
        Environment.SetEnvironmentVariable("EASYDICT_SAVED_ITEMS_DIAGNOSTICS", "1");
        using var dpi = new PerMonitorDpiScope();
        using var fixture = new Fixture("Light", 0);
        ConfigureProviders();
        using var launcher = new AppLauncher();
        launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        var window = launcher.GetMainWindow();
        Resize(window, 1280);
        var providers = SeedResults(window);
        var addName = Star(providers[0]).Name;
        Invoke(Star(providers[0]));
        Retry.WhileFalse(() => Star(providers[0]).Name != addName, TimeSpan.FromSeconds(5)).Result.Should().BeTrue();
        var removeName = Star(providers[0]).Name;

        foreach (var themeName in new[] { "极简线框", "浅色" })
        {
            Invoke(Wait(window, "SettingsButton"));
            var scroller = Wait(window, "SettingsDetailsScrollViewer");
            ScrollHelper.ScrollToFind(scroller, 70, () => Find(window, "AppThemeCombo"), output.WriteLine)!
                .AsComboBox().Select(themeName);
            Invoke(Wait(window, "BackButton"));
            Star(providers[0]).Name.Should().Be(removeName, "the favorite star must survive a renderer rebuild");
            Star(providers[1]).Name.Should().Be(addName, "the other provider must remain unfavorited");
        }

        Invoke(Star(providers[0]));
        Retry.WhileFalse(() => Star(providers[0]).Name == addName, TimeSpan.FromSeconds(5)).Result.Should().BeTrue();
        using var connection = new SqliteConnection($"Data Source={SettingsPath("saved_items.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM favorites";
        Convert.ToInt32(command.ExecuteScalar()).Should().Be(0, "the correctly labeled remove action must remove the stored favorite");

        AutomationElement Star(string provider) => Wait(Wait(window, $"ServiceResultItem_{provider}"), "FavoriteButton");
    }

    [Fact]
    public void LiveResult_UnavailableStorageDoesNotFaultFavoriteRefresh()
    {
        Environment.SetEnvironmentVariable("EASYDICT_SAVED_ITEMS_DIAGNOSTICS", "1");
        using var dpi = new PerMonitorDpiScope();
        using var fixture = new Fixture("Light", 0);
        ConfigureProviders();
        using (var connection = new SqliteConnection($"Data Source={SettingsPath("saved_items.db")}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 999";
            command.ExecuteNonQuery();
        }
        using var launcher = new AppLauncher();
        launcher.LaunchAuto(TimeSpan.FromSeconds(45));
        var window = launcher.GetMainWindow();
        Resize(window, 1280);
        var providers = SeedResults(window);
        Wait(window, $"ServiceResultItem_{providers[0]}").FindAllDescendants()
            .Should().Contain(element => element.Properties.Name.ValueOrDefault == "Successful local fixture result");
        Find(window, "CurrentQueryFavoriteButton").Should().BeNull("unavailable favorite state must not be presented as unfavorited");
    }

    private static string[] SeedResults(Window window)
    {
        window.SetForeground();
        var input = Wait(window, "InputTextBox");
        input.Focus();
        Retry.WhileFalse(() => input.Properties.HasKeyboardFocus.ValueOrDefault, TimeSpan.FromSeconds(5)).Result.Should().BeTrue();
        UITestHelper.SendHotkey(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.F10);
        var path = SettingsPath("live-query-favorites.json");
        var completed = Retry.WhileFalse(() => File.Exists(path), TimeSpan.FromSeconds(10)).Result;
        if (!completed) ScreenshotHelper.CaptureWindow(window, "live_favorite_seed_timeout");
        completed.Should().BeTrue();
        using var report = JsonDocument.Parse(File.ReadAllText(path));
        report.RootElement.GetProperty("Error").ValueKind.Should().Be(JsonValueKind.Null, "optional persistence must not fault: {0}", report.RootElement);
        var providers = report.RootElement.GetProperty("Providers").EnumerateArray().Select(value => value.GetString()!).ToArray();
        providers.Should().HaveCount(2);
        return providers;
    }

    private static string SettingsPath(string name) => Path.Combine(Environment.GetEnvironmentVariable("EASYDICT_SETTINGS_DIR")!, name);

    private static void ConfigureProviders()
    {
        var path = SettingsPath("settings.json");
        var settings = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        settings["MainWindowEnabledServices"] = JsonSerializer.SerializeToNode(new[] { "bing", "youdao" });
        File.WriteAllText(path, settings.ToJsonString());
    }

    public void Dispose() => Environment.SetEnvironmentVariable("EASYDICT_SAVED_ITEMS_DIAGNOSTICS", _previousDiagnostics);
}
