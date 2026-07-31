using System.Diagnostics;
using System.Text.Json;

namespace Easydict.TranslationService.Services.AgentCli;

/// <summary>
/// Reads model names from agent CLI metadata commands without starting a model request.
/// </summary>
internal static class AgentCliModelCatalog
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly string[] ModelNameProperties = ["slug", "id", "name"];

    public static async Task<IReadOnlyList<string>> DiscoverClaudeModelsAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var output = await new AgentCliProcessRunner().RunToEndAsync(
                executablePath,
                ["--help"],
                "",
                ProbeTimeout,
                cancellationToken).ConfigureAwait(false);
            return ParseClaudeHelp(output);
        }
        catch (AgentCliProcessException ex)
        {
            throw ClaudeCodeEventParser.ClassifyFailure(
                ClaudeCodeService.ServiceIdValue,
                ex.ExitCode,
                [],
                ex.StdErr);
        }
        catch (TimeoutException ex)
        {
            throw new TranslationException("Claude Code model discovery timed out.", ex)
            {
                ErrorCode = TranslationErrorCode.Timeout,
                ServiceId = ClaudeCodeService.ServiceIdValue,
            };
        }
    }

    public static async Task<IReadOnlyList<string>> DiscoverCodexModelsAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var output = await new AgentCliProcessRunner().RunToEndAsync(
                executablePath,
                ["debug", "models"],
                "",
                ProbeTimeout,
                cancellationToken).ConfigureAwait(false);
            return ParseCodexCatalog(output);
        }
        catch (AgentCliProcessException ex)
        {
            throw CodexCliEventParser.ClassifyFailure(
                CodexCliService.ServiceIdValue,
                ex.ExitCode,
                [],
                ex.StdErr);
        }
        catch (TimeoutException ex)
        {
            throw new TranslationException("Codex model discovery timed out.", ex)
            {
                ErrorCode = TranslationErrorCode.Timeout,
                ServiceId = CodexCliService.ServiceIdValue,
            };
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[AgentCli] Codex model catalog was not valid JSON: {ex.Message}");
            return [];
        }
    }

    internal static IReadOnlyList<string> ParseClaudeHelp(string helpText)
    {
        const string Marker = "--model <model>";
        var start = helpText.IndexOf(Marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return [];
        }

        var end = helpText.IndexOf("\n  -", start + Marker.Length, StringComparison.Ordinal);
        var section = end >= 0 ? helpText[start..end] : helpText[start..];
        var models = new List<string>();
        var quoteStart = -1;
        for (var i = 0; i < section.Length; i++)
        {
            if (section[i] != '\'')
            {
                continue;
            }

            if (quoteStart < 0)
            {
                quoteStart = i + 1;
                continue;
            }

            var value = section[quoteStart..i];
            quoteStart = -1;
            if (AgentCliPromptBuilder.SanitizeModelName(value) is { } model
                && !models.Contains(model, StringComparer.OrdinalIgnoreCase))
            {
                models.Add(model);
            }
        }

        return models;
    }

    internal static IReadOnlyList<string> ParseCodexCatalog(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("models", out var modelsElement)
            || modelsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var models = new List<string>();
        foreach (var modelElement in modelsElement.EnumerateArray())
        {
            if (modelElement.ValueKind != JsonValueKind.Object
                || IsHidden(modelElement)
                || GetModelName(modelElement) is not { } model
                || models.Contains(model, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            models.Add(model);
        }

        return models;
    }

    private static bool IsHidden(JsonElement model)
    {
        if (!model.TryGetProperty("visibility", out var visibility)
            || visibility.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return visibility.GetString() is "hide" or "hidden";
    }

    private static string? GetModelName(JsonElement model)
    {
        foreach (var propertyName in ModelNameProperties)
        {
            if (model.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String
                && AgentCliPromptBuilder.SanitizeModelName(property.GetString()) is { } value)
            {
                return value;
            }
        }

        return null;
    }
}
