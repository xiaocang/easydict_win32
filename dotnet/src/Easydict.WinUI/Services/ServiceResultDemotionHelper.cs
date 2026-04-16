using Easydict.TranslationService.Models;

namespace Easydict.WinUI.Services;

/// <summary>
/// Shared predicate for deciding whether a <see cref="ServiceQueryResult"/> row should be
/// demoted: grayed out, moved to the bottom of the results list, and not expandable.
///
/// A row is demoted when the user enabled <see cref="SettingsService.HideEmptyServiceResults"/>
/// AND the query completed with <see cref="TranslationResultKind.NoResult"/> (no loading,
/// streaming, or error in flight).
/// </summary>
internal static class ServiceResultDemotionHelper
{
    public static bool IsDemoted(ServiceQueryResult? result) =>
        IsDemoted(result, SettingsService.Instance.HideEmptyServiceResults);

    public static bool IsDemoted(ServiceQueryResult? result, bool hideEmptySetting)
    {
        if (result is null || !hideEmptySetting) return false;
        return !result.IsLoading
            && !result.IsStreaming
            && !result.HasError
            && result.Result?.ResultKind == TranslationResultKind.NoResult;
    }

    /// <summary>
    /// Stable partition: returns the input indices rearranged so non-demoted rows come first
    /// (in their original order) followed by demoted rows (in their original order).
    /// </summary>
    public static IReadOnlyList<int> StablePartitionIndices(
        IReadOnlyList<ServiceQueryResult> results,
        bool hideEmptySetting)
    {
        var kept = new List<int>(results.Count);
        var demoted = new List<int>();
        for (int i = 0; i < results.Count; i++)
        {
            if (IsDemoted(results[i], hideEmptySetting)) demoted.Add(i);
            else kept.Add(i);
        }
        kept.AddRange(demoted);
        return kept;
    }
}
