namespace Easydict.WinUI.Services;

public enum LocalAIProviderMode
{
    Auto,
    WindowsAI,
    FoundryLocal,
    OpenVINO
}

public static class LocalAIProviderModeExtensions
{
    public static LocalAIProviderMode Parse(string? value)
    {
        return Enum.TryParse<LocalAIProviderMode>(value, ignoreCase: true, out var parsed)
            ? parsed
            : LocalAIProviderMode.Auto;
    }
}
