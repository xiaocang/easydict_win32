namespace Easydict.DirectXaml.Theming;

/// <summary>
/// Resolves the runtime resource slots the compiler emits for <c>{ThemeResource}</c> and
/// <c>{StaticResource}</c>.
///
/// Keys are never folded at compile time, so this is what keeps Light / Dark / HighContrast
/// switching working. In the app the implementation forwards to the existing
/// <c>Services/ThemeResourceService.cs</c>, which already resolves by key against a themed root.
///
/// Resource slots back more than colours — the minimal card supplies <c>BorderThickness</c> and
/// <c>CornerRadius</c> that way — so every value kind a property can hold has a lookup here.
/// </summary>
public interface IResourceResolver
{
    /// <summary>Resolves a brush key to a colour.</summary>
    /// <returns>False when the key is absent, in which case the caller keeps its fallback.</returns>
    bool TryGetColor(string key, out Color color);

    /// <summary>Resolves a key to a thickness, as used for <c>BorderThickness</c> and <c>Padding</c>.</summary>
    /// <returns>False when the key is absent.</returns>
    bool TryGetThickness(string key, out Thickness thickness);

    /// <summary>Resolves a key to a corner radius.</summary>
    /// <returns>False when the key is absent.</returns>
    bool TryGetCornerRadius(string key, out CornerRadius radius);

    /// <summary>Resolves a key to a scalar, as used for <c>FontSize</c> and <c>Spacing</c>.</summary>
    /// <returns>False when the key is absent.</returns>
    bool TryGetDouble(string key, out double value);
}

/// <summary>An explicit dictionary of values. Used by tests and as a fallback.</summary>
public sealed class DictionaryResourceResolver : IResourceResolver
{
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

    /// <summary>Adds a colour, replacing any existing entry for the key.</summary>
    public DictionaryResourceResolver Add(string key, Color color) => Set(key, color);

    /// <summary>Adds a thickness, replacing any existing entry for the key.</summary>
    public DictionaryResourceResolver Add(string key, Thickness thickness) => Set(key, thickness);

    /// <summary>Adds a corner radius, replacing any existing entry for the key.</summary>
    public DictionaryResourceResolver Add(string key, CornerRadius radius) => Set(key, radius);

    /// <summary>Adds a scalar, replacing any existing entry for the key.</summary>
    public DictionaryResourceResolver Add(string key, double value) => Set(key, value);

    private DictionaryResourceResolver Set(string key, object value)
    {
        _values[key] = value;
        return this;
    }

    /// <inheritdoc/>
    public bool TryGetColor(string key, out Color color) => TryGet(key, out color);

    /// <inheritdoc/>
    public bool TryGetThickness(string key, out Thickness thickness) => TryGet(key, out thickness);

    /// <inheritdoc/>
    public bool TryGetCornerRadius(string key, out CornerRadius radius) => TryGet(key, out radius);

    /// <inheritdoc/>
    public bool TryGetDouble(string key, out double value) => TryGet(key, out value);

    private bool TryGet<T>(string key, out T result)
    {
        // A key stored with a different value kind is treated as absent rather than as an error:
        // the compiler cannot check a resource's runtime type, so a mismatch is expected input.
        if (_values.TryGetValue(key, out object? value) && value is T typed)
        {
            result = typed;
            return true;
        }

        result = default!;
        return false;
    }
}
