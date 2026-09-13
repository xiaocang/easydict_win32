using Easydict.WinUI.Services;
using FluentAssertions;
using System.Reflection;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Persistence tests for the hover word lookup settings, using isolated SettingsService
/// instances (private constructor + EASYDICT_SETTINGS_DIR) so the singleton is untouched.
/// </summary>
[Trait("Category", "WinUI")]
[Collection("SettingsService")]
public class SettingsServiceHoverWordLookupTests
{
    [Fact]
    public void HoverWordLookupSettings_HaveExpectedDefaults()
    {
        using var directory = new TemporaryDirectory();
        var settings = CreateIsolatedSettingsService(directory.Path);

        settings.HoverWordLookupEnabled.Should().BeFalse();
        settings.HoverWordLookupModifier.Should().Be("Ctrl");
        settings.HoverWordLookupUseOcrFallback.Should().BeTrue();
        settings.HoverWordLookupServiceId.Should().BeEmpty();
    }

    [Fact]
    public void HoverWordLookupSettings_RoundTripThroughSave()
    {
        using var directory = new TemporaryDirectory();
        var settings = CreateIsolatedSettingsService(directory.Path);

        settings.HoverWordLookupEnabled = true;
        settings.HoverWordLookupModifier = "Shift";
        settings.HoverWordLookupUseOcrFallback = false;
        settings.HoverWordLookupServiceId = "youdao";
        settings.Save();

        var reloaded = CreateIsolatedSettingsService(directory.Path);
        reloaded.HoverWordLookupEnabled.Should().BeTrue();
        reloaded.HoverWordLookupModifier.Should().Be("Shift");
        reloaded.HoverWordLookupUseOcrFallback.Should().BeFalse();
        reloaded.HoverWordLookupServiceId.Should().Be("youdao");
    }

    [Fact]
    public void HoverWordLookupModifier_UnknownPersistedValue_NormalizesToCtrl()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "settings.json"),
            """{"HoverWordLookupModifier":"bogus","HoverWordLookupEnabled":true}""");

        var settings = CreateIsolatedSettingsService(directory.Path);

        settings.HoverWordLookupModifier.Should().Be("Ctrl");
        settings.HoverWordLookupEnabled.Should().BeTrue();
    }

    [Fact]
    public void HoverWordLookupModifier_PersistedValue_IsCaseNormalized()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "settings.json"),
            """{"HoverWordLookupModifier":"alt"}""");

        var settings = CreateIsolatedSettingsService(directory.Path);

        settings.HoverWordLookupModifier.Should().Be("Alt");
    }

    private static SettingsService CreateIsolatedSettingsService(string directory)
    {
        var previousSettingsDirectory = Environment.GetEnvironmentVariable("EASYDICT_SETTINGS_DIR");
        try
        {
            Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", directory);
            var constructor = typeof(SettingsService).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                Type.EmptyTypes,
                modifiers: null);

            constructor.Should().NotBeNull();
            return (SettingsService)constructor!.Invoke(null);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EASYDICT_SETTINGS_DIR", previousSettingsDirectory);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Easydict.WinUI.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }
}
