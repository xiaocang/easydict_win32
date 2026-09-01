namespace Easydict.TranslationService.Services.ModelCatalog;

/// <summary>
/// One model advertised by an OpenAI-compatible provider's <c>/models</c> catalog.
/// </summary>
/// <param name="Id">Model identifier passed as the <c>model</c> field of a chat request.</param>
/// <param name="Name">Human-readable name, when the provider supplies one.</param>
/// <param name="IsFree">Whether the model can be used at no cost.</param>
/// <param name="ContextLength">Advertised context window, when known.</param>
public sealed record ModelCatalogEntry(
    string Id,
    string? Name,
    bool IsFree,
    long? ContextLength)
{
    /// <summary>
    /// Label shown in the settings model dropdown. The raw <see cref="Id"/> stays the
    /// ComboBoxItem's Tag, which is what the settings page persists.
    /// </summary>
    public string DisplayLabel
    {
        get
        {
            var suffix = IsFree ? "  (Free)" : "";
            return string.IsNullOrWhiteSpace(Name) || string.Equals(Name, Id, StringComparison.Ordinal)
                ? $"{Id}{suffix}"
                : $"{Id} — {Name}{suffix}";
        }
    }
}
