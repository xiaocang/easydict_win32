using System.Text.Json;
using System.Text.Json.Serialization;

namespace Easydict.BobPlugin.Manifest;

/// <summary>How a plugin option is presented.</summary>
public enum BobPluginOptionType
{
    /// <summary>Free text (optionally masked, see <see cref="BobTextConfig.IsSecure"/>).</summary>
    Text,

    /// <summary>One of a fixed set of values.</summary>
    Menu
}

/// <summary>One choice of a menu option.</summary>
public sealed record BobMenuValue(string Title, string Value);

/// <summary>Presentation details of a text option.</summary>
public sealed record BobTextConfig(bool IsSecure, int? Height, string? PlaceholderText, string? KeyWords);

/// <summary>A user-configurable option declared by the plugin, surfaced in Easydict's settings.</summary>
public sealed record BobPluginOption
{
    public required string Identifier { get; init; }
    public required string Title { get; init; }
    public BobPluginOptionType Type { get; init; } = BobPluginOptionType.Text;
    public string? Desc { get; init; }
    public string? DefaultValue { get; init; }
    public IReadOnlyList<BobMenuValue> MenuValues { get; init; } = [];
    public BobTextConfig? TextConfig { get; init; }

    /// <summary>True when the value must be stored encrypted and masked in the UI.</summary>
    public bool IsSecure => TextConfig?.IsSecure == true;
}

/// <summary>
/// The contents of a plugin's info.json. Unknown fields are ignored so newer plugins still load.
/// </summary>
public sealed record BobPluginManifest
{
    /// <summary>Reverse-DNS plugin identifier, unique per plugin.</summary>
    public required string Identifier { get; init; }

    /// <summary>Plugin category ("translate", "ocr", "tts"). Only "translate" is supported today.</summary>
    public required string Category { get; init; }

    public required string Version { get; init; }
    public required string Name { get; init; }
    public string? Summary { get; init; }
    public string? Icon { get; init; }
    public string? Author { get; init; }
    public string? Homepage { get; init; }
    public string? Appcast { get; init; }
    public string? MinBobVersion { get; init; }
    public IReadOnlyList<BobPluginOption> Options { get; init; } = [];

    /// <summary>Category understood by this host.</summary>
    public const string TranslateCategory = "translate";

    /// <summary>True when this plugin can be hosted as a translation service.</summary>
    public bool IsTranslatePlugin => string.Equals(Category, TranslateCategory, StringComparison.OrdinalIgnoreCase);

    /// <summary>Options whose values must be stored with the platform credential store.</summary>
    public IEnumerable<BobPluginOption> SecureOptions => Options.Where(o => o.IsSecure);

    /// <summary>Parse an info.json document.</summary>
    /// <exception cref="BobPluginException">The document is unparsable or lacks required fields.</exception>
    public static BobPluginManifest Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new BobPluginException(BobPluginError.InvalidManifest, "info.json is empty.");
        }

        BobPluginManifestDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<BobPluginManifestDto>(json, ManifestJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new BobPluginException(BobPluginError.InvalidManifest, $"info.json is not valid JSON: {ex.Message}", ex);
        }

        if (dto is null)
        {
            throw new BobPluginException(BobPluginError.InvalidManifest, "info.json is empty.");
        }

        if (string.IsNullOrWhiteSpace(dto.Identifier))
        {
            throw new BobPluginException(BobPluginError.InvalidManifest, "info.json is missing 'identifier'.");
        }

        if (string.IsNullOrWhiteSpace(dto.Category))
        {
            throw new BobPluginException(BobPluginError.InvalidManifest, "info.json is missing 'category'.");
        }

        return new BobPluginManifest
        {
            Identifier = dto.Identifier.Trim(),
            Category = dto.Category.Trim(),
            Version = string.IsNullOrWhiteSpace(dto.Version) ? "0.0.0" : dto.Version.Trim(),
            Name = string.IsNullOrWhiteSpace(dto.Name) ? dto.Identifier.Trim() : dto.Name.Trim(),
            Summary = dto.Summary,
            Icon = dto.Icon,
            Author = dto.Author,
            Homepage = dto.Homepage,
            Appcast = dto.Appcast,
            MinBobVersion = dto.MinBobVersion,
            Options = (dto.Options ?? []).Where(o => !string.IsNullOrWhiteSpace(o.Identifier)).Select(Convert).ToList()
        };
    }

    internal static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static BobPluginOption Convert(BobPluginOptionDto dto)
    {
        var type = string.Equals(dto.Type, "menu", StringComparison.OrdinalIgnoreCase)
            ? BobPluginOptionType.Menu
            : BobPluginOptionType.Text;

        return new BobPluginOption
        {
            Identifier = dto.Identifier!.Trim(),
            Title = string.IsNullOrWhiteSpace(dto.Title) ? dto.Identifier!.Trim() : dto.Title!,
            Type = type,
            Desc = dto.Desc,
            DefaultValue = dto.DefaultValue,
            MenuValues = (dto.MenuValues ?? [])
                .Where(m => m.Value is not null)
                .Select(m => new BobMenuValue(m.Title ?? m.Value!, m.Value!))
                .ToList(),
            TextConfig = dto.TextConfig is null
                ? null
                : new BobTextConfig(
                    string.Equals(dto.TextConfig.Type, "secure", StringComparison.OrdinalIgnoreCase),
                    dto.TextConfig.Height,
                    dto.TextConfig.PlaceholderText,
                    dto.TextConfig.KeyWords)
        };
    }

    private sealed class BobPluginManifestDto
    {
        public string? Identifier { get; set; }
        public string? Category { get; set; }
        public string? Version { get; set; }
        public string? Name { get; set; }
        public string? Summary { get; set; }
        public string? Icon { get; set; }
        public string? Author { get; set; }
        public string? Homepage { get; set; }
        public string? Appcast { get; set; }
        public string? MinBobVersion { get; set; }
        public List<BobPluginOptionDto>? Options { get; set; }
    }

    private sealed class BobPluginOptionDto
    {
        public string? Identifier { get; set; }
        public string? Type { get; set; }
        public string? Title { get; set; }
        public string? Desc { get; set; }
        public string? DefaultValue { get; set; }
        public List<BobMenuValueDto>? MenuValues { get; set; }
        public BobTextConfigDto? TextConfig { get; set; }
    }

    private sealed class BobMenuValueDto
    {
        public string? Title { get; set; }
        public string? Value { get; set; }
    }

    private sealed class BobTextConfigDto
    {
        public string? Type { get; set; }
        public int? Height { get; set; }
        public string? PlaceholderText { get; set; }
        public string? KeyWords { get; set; }
    }
}
