using System.Text.Json;
using System.Text.Json.Serialization;

namespace Easydict.BrowserRegistrar;

// ───────────────────── CLI Output Types ─────────────────────

internal sealed record ErrorOutput(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("error")] string? Error);

internal sealed record InstallOutput(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("installed")] List<string>? Installed,
    [property: JsonPropertyName("bridge_path")] string? BridgePath);

internal sealed record UninstallOutput(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("uninstalled")] List<string> Uninstalled);

internal sealed record BrowserStatusEntry(
    [property: JsonPropertyName("installed")] bool Installed);

internal sealed record StatusOutput(
    [property: JsonPropertyName("chrome")] BrowserStatusEntry Chrome,
    [property: JsonPropertyName("firefox")] BrowserStatusEntry Firefox,
    [property: JsonPropertyName("bridge_exists")] bool BridgeExists,
    [property: JsonPropertyName("bridge_directory")] string BridgeDirectory);

// ───────────────────── Native Messaging Manifest Types ─────────────────────

internal sealed record ChromeManifest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("allowed_origins")] string[] AllowedOrigins);

internal sealed record FirefoxManifest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("allowed_extensions")] string[] AllowedExtensions);

// ───────────────────── Source-Generated Context ─────────────────────

[JsonSerializable(typeof(ErrorOutput))]
[JsonSerializable(typeof(InstallOutput))]
[JsonSerializable(typeof(UninstallOutput))]
[JsonSerializable(typeof(StatusOutput))]
[JsonSerializable(typeof(ChromeManifest))]
[JsonSerializable(typeof(FirefoxManifest))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class AppJsonContext : JsonSerializerContext
{
    /// <summary>Pre-configured context with WriteIndented for manifest files.</summary>
    internal static AppJsonContext IndentedDefault { get; } = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = Default
    });
}
