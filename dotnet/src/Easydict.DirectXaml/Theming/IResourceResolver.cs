namespace Easydict.DirectXaml.Theming;

/// <summary>
/// Resolves the runtime resource slots the compiler emits for <c>{ThemeResource}</c> and
/// <c>{StaticResource}</c>.
///
/// Keys are never folded at compile time, so this is what keeps Light / Dark / HighContrast
/// switching working. In the app the implementation forwards to the existing
/// <c>Services/ThemeResourceService.cs</c>, which already resolves by key against a themed root.
/// </summary>
public interface IResourceResolver
{
    bool TryGetColor(string key, out Color color);

    bool TryGetThickness(string key, out Thickness thickness);

    bool TryGetCornerRadius(string key, out CornerRadius radius);

    bool TryGetDouble(string key, out double value);
}

/// <summary>An explicit dictionary of values. Used by tests and as a fallback.</summary>
public sealed class DictionaryResourceResolver : IResourceResolver
{
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

    public DictionaryResourceResolver Add(string key, Color color) => Set(key, color);

    public DictionaryResourceResolver Add(string key, Thickness thickness) => Set(key, thickness);

    public DictionaryResourceResolver Add(string key, CornerRadius radius) => Set(key, radius);

    public DictionaryResourceResolver Add(string key, double value) => Set(key, value);

    private DictionaryResourceResolver Set(string key, object value)
    {
        _values[key] = value;
        return this;
    }

    public bool TryGetColor(string key, out Color color) => TryGet(key, out color);

    public bool TryGetThickness(string key, out Thickness thickness) => TryGet(key, out thickness);

    public bool TryGetCornerRadius(string key, out CornerRadius radius) => TryGet(key, out radius);

    public bool TryGetDouble(string key, out double value) => TryGet(key, out value);

    private bool TryGet<T>(string key, out T result)
    {
        if (_values.TryGetValue(key, out object? value) && value is T typed)
        {
            result = typed;
            return true;
        }

        result = default!;
        return false;
    }
}
