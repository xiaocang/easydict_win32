using System.Text.Json;
using System.Text.Json.Serialization;

namespace Easydict.WinUI.Services;

/// <summary>
/// Manages application settings with persistence using JSON file.
/// Works for both packaged and unpackaged WinUI 3 apps.
/// </summary>
public sealed class SettingsService
{
    private static readonly Lazy<SettingsService> _instance = new(() => new SettingsService());
    public static SettingsService Instance => _instance.Value;

    private readonly string _settingsFilePath;
    private Dictionary<string, object?> _settings = new();

    private SettingsService()
    {
        // Use AppData\Local\Easydict for settings
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var easydictPath = Path.Combine(appDataPath, "Easydict");
        Directory.CreateDirectory(easydictPath);
        _settingsFilePath = Path.Combine(easydictPath, "settings.json");

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

    /// <summary>
    /// Enable DPI-aware window positioning and scaling.
    /// Set to false to revert to legacy behavior if issues occur.
    /// </summary>
    public bool EnableDpiAwareness { get; set; } = true;

    /// <summary>
    /// Window width stored in DIPs (Device-Independent Pixels).
    /// Ensures consistent sizing across different DPI monitors.
    /// </summary>
    public double WindowWidthDips { get; set; } = 430;

    /// <summary>
    /// Window height stored in DIPs (Device-Independent Pixels).
    /// Ensures consistent sizing across different DPI monitors.
    /// </summary>
    public double WindowHeightDips { get; set; } = 503;

    /// <summary>
    /// Legacy property for backwards compatibility.
    /// Maps to WindowWidthDips for migration from older settings files.
    /// </summary>
    [JsonIgnore]
    public double WindowWidth
    {
        get => WindowWidthDips;
        set => WindowWidthDips = value;
    }

    /// <summary>
    /// Legacy property for backwards compatibility.
    /// Maps to WindowHeightDips for migration from older settings files.
    /// </summary>
    [JsonIgnore]
    public double WindowHeight
    {
        get => WindowHeightDips;
        set => WindowHeightDips = value;
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                _settings = JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new();
            }
        }
        catch
        {
            _settings = new();
        }

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
        EnableDpiAwareness = GetValue(nameof(EnableDpiAwareness), true);

        // Try to load WindowWidthDips first (new format), fallback to WindowWidth (legacy)
        WindowWidthDips = GetValue(nameof(WindowWidthDips), GetValue(nameof(WindowWidth), 600.0));
        WindowHeightDips = GetValue(nameof(WindowHeightDips), GetValue(nameof(WindowHeight), 700.0));
    }

    public void Save()
    {
        _settings[nameof(DefaultService)] = DefaultService;
        _settings[nameof(TargetLanguage)] = TargetLanguage;
        _settings[nameof(SourceLanguage)] = SourceLanguage;
        _settings[nameof(DeepLApiKey)] = DeepLApiKey ?? string.Empty;
        _settings[nameof(DeepLUseFreeApi)] = DeepLUseFreeApi;
        _settings[nameof(MinimizeToTray)] = MinimizeToTray;
        _settings[nameof(ClipboardMonitoring)] = ClipboardMonitoring;
        _settings[nameof(AutoTranslate)] = AutoTranslate;
        _settings[nameof(ShowWindowHotkey)] = ShowWindowHotkey;
        _settings[nameof(TranslateSelectionHotkey)] = TranslateSelectionHotkey;
        _settings[nameof(AlwaysOnTop)] = AlwaysOnTop;
        _settings[nameof(EnableDpiAwareness)] = EnableDpiAwareness;
        _settings[nameof(WindowWidthDips)] = WindowWidthDips;
        _settings[nameof(WindowHeightDips)] = WindowHeightDips;

        try
        {
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json);
        }
        catch
        {
            // Ignore save errors
        }
    }

    private T GetValue<T>(string key, T defaultValue)
    {
        if (_settings.TryGetValue(key, out var value) && value != null)
        {
            try
            {
                if (value is JsonElement jsonElement)
                {
                    if (typeof(T) == typeof(string))
                        return (T)(object)jsonElement.GetString()!;
                    if (typeof(T) == typeof(bool))
                        return (T)(object)jsonElement.GetBoolean();
                    if (typeof(T) == typeof(double))
                        return (T)(object)jsonElement.GetDouble();
                    if (typeof(T) == typeof(int))
                        return (T)(object)jsonElement.GetInt32();
                }

                if (value is T typedValue)
                    return typedValue;

                // Handle type conversion
                if (typeof(T) == typeof(double) && value is long longVal)
                    return (T)(object)(double)longVal;
                if (typeof(T) == typeof(bool) && value is bool boolVal)
                    return (T)(object)boolVal;
            }
            catch { }
        }
        return defaultValue;
    }
}

