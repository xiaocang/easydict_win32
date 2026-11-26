namespace Easydict.WinUI.Services;

/// <summary>Distinguishes an empty selection from an unavailable UIA provider.</summary>
internal sealed record UiaSelectionResult(string? Text, bool IsConfirmedEmpty)
{
    internal static UiaSelectionResult Unavailable { get; } = new(null, false);
    internal static UiaSelectionResult Empty { get; } = new(null, true);

    internal static UiaSelectionResult FromText(string? text)
        => text == null ? Unavailable
            : text.Length == 0 ? Empty
            : new(text, string.IsNullOrWhiteSpace(text));

    internal static UiaSelectionResult FromRanges(IEnumerable<string?>? ranges)
    {
        if (ranges == null) return Unavailable;
        using var iterator = ranges.GetEnumerator();
        if (!iterator.MoveNext()) return Empty;

        var first = FromText(iterator.Current);
        if (!first.IsConfirmedEmpty) return first;

        // Preserve the existing first-range extraction behavior. If a later range
        // has text (or cannot be read), clipboard fallback may still recover it.
        while (iterator.MoveNext())
        {
            if (iterator.Current == null || !string.IsNullOrWhiteSpace(iterator.Current))
                return Unavailable;
        }

        return first;
    }

    internal static UiaSelectionResult ReadProviders(
        IEnumerable<Func<UiaSelectionResult?>> providers, Action<Exception>? reportFailure = null)
    {
        var sawEmpty = false;
        var sawUnavailable = false;
        foreach (var read in providers)
        {
            UiaSelectionResult? result;
            try { result = read(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                sawUnavailable = true;
                reportFailure?.Invoke(ex);
                continue;
            }

            // Null means the pattern is unsupported and was not read.
            if (result == null) continue;
            if (!string.IsNullOrWhiteSpace(result.Text)) return result;
            if (result.IsConfirmedEmpty) sawEmpty = true;
            else sawUnavailable = true;
        }

        // Every attempted provider must agree that the selection is empty.
        // An unavailable/error result may conceal text another provider missed.
        return sawEmpty && !sawUnavailable ? Empty : Unavailable;
    }
}
