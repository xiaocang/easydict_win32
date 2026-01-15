using System.Text.Json;
using Windows.Storage;

namespace Easydict.WinUI.Services;

/// <summary>
/// Manages application settings with persistence.
/// </summary>
public sealed class SettingsService
{
    private static readonly Lazy<SettingsService> _instance = new(() => new SettingsService());
    public static SettingsService Instance => _instance.Value;

    private readonly ApplicationDataContainer _localSettings;

    private SettingsService()
    {
        _localSettings = ApplicationData.Current.LocalSettings;
        LoadSettings();
    }

    // Translation settings
    public string DefaultService { get; set; } = "google";
    public string TargetLanguage { get; set; } = "zh";
    public string SourceLanguage { get; set; } = "auto";

    // API Keys
    public string? DeepLApiKey { get; set; }
    public bool DeepLUseFreeApi { get; set; } = true;

    // Behavior settings
    public bool MinimizeToTray { get; set; } = true;
    public bool ClipboardMonitoring { get; set; } = false;
    public bool AutoTranslate { get; set; } = false;

    // Hotkey settings (stored as string like "Ctrl+Alt+T")
    public string ShowWindowHotkey { get; set; } = "Ctrl+Alt+T";
    public string TranslateSelectionHotkey { get; set; } = "Ctrl+Alt+D";

    // UI settings
    public bool AlwaysOnTop { get; set; } = false;
    public double WindowWidth { get; set; } = 600;
    public double WindowHeight { get; set; } = 700;

    private void LoadSettings()
    {
        DefaultService = GetValue(nameof(DefaultService), "google");
        TargetLanguage = GetValue(nameof(TargetLanguage), "zh");
        SourceLanguage = GetValue(nameof(SourceLanguage), "auto");
        DeepLApiKey = GetValue<string?>(nameof(DeepLApiKey), null);
        DeepLUseFreeApi = GetValue(nameof(DeepLUseFreeApi), true);
        MinimizeToTray = GetValue(nameof(MinimizeToTray), true);
        ClipboardMonitoring = GetValue(nameof(ClipboardMonitoring), false);
        AutoTranslate = GetValue(nameof(AutoTranslate), false);
        ShowWindowHotkey = GetValue(nameof(ShowWindowHotkey), "Ctrl+Alt+T");
        TranslateSelectionHotkey = GetValue(nameof(TranslateSelectionHotkey), "Ctrl+Alt+D");
        AlwaysOnTop = GetValue(nameof(AlwaysOnTop), false);
        WindowWidth = GetValue(nameof(WindowWidth), 600.0);
        WindowHeight = GetValue(nameof(WindowHeight), 700.0);
    }

    public void Save()
    {
        SetValue(nameof(DefaultService), DefaultService);
        SetValue(nameof(TargetLanguage), TargetLanguage);
        SetValue(nameof(SourceLanguage), SourceLanguage);
        SetValue(nameof(DeepLApiKey), DeepLApiKey ?? string.Empty);
        SetValue(nameof(DeepLUseFreeApi), DeepLUseFreeApi);
        SetValue(nameof(MinimizeToTray), MinimizeToTray);
        SetValue(nameof(ClipboardMonitoring), ClipboardMonitoring);
        SetValue(nameof(AutoTranslate), AutoTranslate);
        SetValue(nameof(ShowWindowHotkey), ShowWindowHotkey);
        SetValue(nameof(TranslateSelectionHotkey), TranslateSelectionHotkey);
        SetValue(nameof(AlwaysOnTop), AlwaysOnTop);
        SetValue(nameof(WindowWidth), WindowWidth);
        SetValue(nameof(WindowHeight), WindowHeight);
    }

    private T GetValue<T>(string key, T defaultValue)
    {
        if (_localSettings.Values.TryGetValue(key, out var value))
        {
            if (value is T typedValue)
                return typedValue;

            // Handle type conversion
            try
            {
                if (typeof(T) == typeof(double) && value is long longVal)
                    return (T)(object)(double)longVal;
                if (typeof(T) == typeof(bool) && value is bool boolVal)
                    return (T)(object)boolVal;
            }
            catch { }
        }
        return defaultValue;
    }

    private void SetValue<T>(string key, T value)
    {
        _localSettings.Values[key] = value;
    }
}

