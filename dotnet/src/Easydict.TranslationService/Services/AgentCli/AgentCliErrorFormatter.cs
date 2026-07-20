namespace Easydict.TranslationService.Services.AgentCli;

/// <summary>
/// Formats CLI failure output into a short human-readable detail suffix.
/// </summary>
internal static class AgentCliErrorFormatter
{
    private const int MaxDetailLength = 300;

    /// <summary>
    /// Returns ": &lt;excerpt&gt;" built from stderr (preferred) or the stdout control
    /// lines, capped at a display-friendly length; empty string when there is nothing.
    /// </summary>
    public static string BuildDetail(IReadOnlyList<string> controlLines, string stdErr)
    {
        var source = !string.IsNullOrWhiteSpace(stdErr)
            ? stdErr
            : string.Join('\n', controlLines);

        var text = string.Join(
            ' ',
            source.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        if (text.Length > MaxDetailLength)
        {
            text = text[..MaxDetailLength] + "…";
        }

        return $": {text}";
    }
}
