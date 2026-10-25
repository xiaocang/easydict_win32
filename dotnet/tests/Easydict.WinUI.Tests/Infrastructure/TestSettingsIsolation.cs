using System.Runtime.CompilerServices;

namespace Easydict.WinUI.Tests.Infrastructure;

/// <summary>
/// Protects singleton-backed tests from the real per-user EasyDict settings.
/// The test assembly module initializer runs before any test type can touch
/// SettingsService.Instance. An explicit caller-provided directory is honored.
/// </summary>
internal static class TestSettingsIsolation
{
    internal const string SettingsDirectoryEnvironmentVariable = "EASYDICT_SETTINGS_DIR";

    internal static string? AutomaticallyAllocatedDirectory { get; private set; }

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SettingsDirectoryEnvironmentVariable)))
        {
            return;
        }

        var directory = Path.Combine(
            Path.GetTempPath(),
            "Easydict.Tests",
            $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable(SettingsDirectoryEnvironmentVariable, directory);
        AutomaticallyAllocatedDirectory = directory;
    }
}
