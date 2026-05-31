using System.Text.Json;
using System.Text.Json.Nodes;
using Easydict.SidecarClient.Protocol;

namespace Easydict.CompatHost;

public sealed class FileSettingsCompatMigrator : ICompatHostSettingsMigrator
{
    private const string LegacyOpenVinoServiceId = "openvino-local-ai";
    private const string WindowsLocalAiServiceId = "windows-local-ai";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public async Task<SettingsMigrateResult> MigrateAsync(
        SettingsMigrateParams parameters,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = ResolveSourcePath(parameters.LegacySettingsPath);
        var targetPath = string.IsNullOrWhiteSpace(parameters.TargetSettingsPath)
            ? sourcePath
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(parameters.TargetSettingsPath));
        var warnings = new List<string>();

        if (!File.Exists(sourcePath))
        {
            warnings.Add($"Settings file not found: {sourcePath}");
            return new SettingsMigrateResult
            {
                Migrated = false,
                Warnings = warnings,
            };
        }

        JsonObject root;
        try
        {
            await using var input = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true);
            root = await JsonNode.ParseAsync(input, cancellationToken: cancellationToken)
                    .ConfigureAwait(false) as JsonObject
                ?? throw new JsonException("settings root is not an object");
        }
        catch (JsonException ex)
        {
            throw new CompatHostException(
                IpcErrorCodes.InvalidParams,
                $"Settings file is not valid JSON: {ex.Message}");
        }

        var changed = false;
        changed |= CopyLegacyDouble(root, "WindowWidth", "WindowWidthDips");
        changed |= CopyLegacyDouble(root, "WindowHeight", "WindowHeightDips");
        changed |= SetPositionSavedFromCoordinates(root, "MiniWindow");
        changed |= SetPositionSavedFromCoordinates(root, "FixedWindow");
        changed |= RemoveRuntimeOnlyWorkerIsolationSettings(root);
        changed |= MigrateStandaloneOpenVinoService(root);

        if (changed || !PathsEqual(sourcePath, targetPath))
        {
            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(
                    targetPath,
                    root.ToJsonString(JsonOptions),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new SettingsMigrateResult
        {
            Migrated = changed || !PathsEqual(sourcePath, targetPath),
            Warnings = warnings,
        };
    }

    internal static string ResolveSourcePath(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        }

        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appDataPath, "Easydict", "settings.json");
    }

    private static bool CopyLegacyDouble(JsonObject root, string legacyKey, string newKey)
    {
        if (root.ContainsKey(newKey) || !TryGetDouble(root, legacyKey, out var value))
        {
            return false;
        }

        root[newKey] = value;
        return true;
    }

    private static bool SetPositionSavedFromCoordinates(JsonObject root, string prefix)
    {
        var key = $"{prefix}PositionSaved";
        if (root.ContainsKey(key))
        {
            return false;
        }

        var hasX = TryGetDouble(root, $"{prefix}XDips", out var x);
        var hasY = TryGetDouble(root, $"{prefix}YDips", out var y);
        if (!hasX && !hasY)
        {
            return false;
        }

        root[key] = x != 0.0 || y != 0.0;
        return true;
    }

    private static bool RemoveRuntimeOnlyWorkerIsolationSettings(JsonObject root)
    {
        var changed = false;
        changed |= root.Remove("UseLongDocWorker");
        changed |= root.Remove("UseLocalAiWorker");
        changed |= root.Remove("UseOcrWorker");
        return changed;
    }

    private static bool MigrateStandaloneOpenVinoService(JsonObject root)
    {
        var changed = false;
        var listKeys = new[]
        {
            "MiniWindowEnabledServices",
            "MainWindowEnabledServices",
            "FixedWindowEnabledServices",
        };
        var dictionaryKeys = new[]
        {
            "MiniWindowServiceEnabledQuery",
            "MainWindowServiceEnabledQuery",
            "FixedWindowServiceEnabledQuery",
        };

        var hadOpenVino = listKeys.Any(key => ArrayContains(root, key, LegacyOpenVinoServiceId));
        var hadWindowsLocalAi = listKeys.Any(key => ArrayContains(root, key, WindowsLocalAiServiceId));

        if (hadOpenVino && !hadWindowsLocalAi && !root.ContainsKey("LocalAIProvider"))
        {
            root["LocalAIProvider"] = "OpenVINO";
            changed = true;
        }

        foreach (var key in listKeys)
        {
            changed |= ReplaceStringInArray(root, key, LegacyOpenVinoServiceId, WindowsLocalAiServiceId);
        }

        foreach (var key in dictionaryKeys)
        {
            changed |= MoveDictionaryKey(root, key, LegacyOpenVinoServiceId, WindowsLocalAiServiceId);
        }

        return changed;
    }

    private static bool ReplaceStringInArray(JsonObject root, string key, string oldValue, string newValue)
    {
        if (root[key] is not JsonArray array)
        {
            return false;
        }

        var changed = false;
        var hasNewValue = array.Any(item =>
            string.Equals(item?.GetValue<string>(), newValue, StringComparison.OrdinalIgnoreCase));

        for (var i = array.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(array[i]?.GetValue<string>(), oldValue, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (hasNewValue)
            {
                array.RemoveAt(i);
            }
            else
            {
                array[i] = newValue;
                hasNewValue = true;
            }

            changed = true;
        }

        return changed;
    }

    private static bool MoveDictionaryKey(JsonObject root, string objectKey, string oldKey, string newKey)
    {
        if (root[objectKey] is not JsonObject obj)
        {
            return false;
        }

        var existing = obj.FirstOrDefault(prop =>
            string.Equals(prop.Key, oldKey, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(existing.Key))
        {
            return false;
        }

        var value = existing.Value?.DeepClone();
        obj.Remove(existing.Key);
        if (!obj.Any(prop => string.Equals(prop.Key, newKey, StringComparison.OrdinalIgnoreCase)))
        {
            obj[newKey] = value;
        }

        return true;
    }

    private static bool ArrayContains(JsonObject root, string key, string value)
    {
        return root[key] is JsonArray array
            && array.Any(item =>
                string.Equals(item?.GetValue<string>(), value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetDouble(JsonObject root, string key, out double value)
    {
        value = 0;
        try
        {
            if (root[key] is JsonValue jsonValue
                && jsonValue.TryGetValue<double>(out var number))
            {
                value = number;
                return true;
            }
        }
        catch (InvalidOperationException)
        {
        }

        return false;
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
    }
}
