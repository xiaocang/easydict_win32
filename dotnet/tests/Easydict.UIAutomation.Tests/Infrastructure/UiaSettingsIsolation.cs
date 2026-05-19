using System.Diagnostics;

namespace Easydict.UIAutomation.Tests.Infrastructure;

internal static class UiaSettingsIsolation
{
    private const string ExePathEnvironmentVariable = "EASYDICT_EXE_PATH";
    private const string SettingsDirectoryEnvironmentVariable = "EASYDICT_SETTINGS_DIR";

    public static void ApplyTo(ProcessStartInfo startInfo)
    {
        if (TryEnsureSettingsDirectory() is { } settingsDirectory)
        {
            startInfo.Environment[SettingsDirectoryEnvironmentVariable] = settingsDirectory;
        }
    }

    public static string? TryGetSettingsFilePath()
    {
        return TryEnsureSettingsDirectory() is { } settingsDirectory
            ? Path.Combine(settingsDirectory, "settings.json")
            : null;
    }

    private static string? TryEnsureSettingsDirectory()
    {
        var settingsDirectory = Environment.GetEnvironmentVariable(SettingsDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(settingsDirectory))
        {
            return EnsureDirectory(settingsDirectory);
        }

        var exePath = Environment.GetEnvironmentVariable(ExePathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return null;
        }

        settingsDirectory = Path.Combine(
            Path.GetTempPath(),
            "Easydict.UIAutomation.Tests",
            Process.GetCurrentProcess().Id.ToString(),
            "settings");
        settingsDirectory = EnsureDirectory(settingsDirectory);
        Environment.SetEnvironmentVariable(SettingsDirectoryEnvironmentVariable, settingsDirectory);
        return settingsDirectory;
    }

    private static string EnsureDirectory(string path)
    {
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }
}
