using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services;

/// <summary>The UI-independent snapshot submitted by the AI profile editor's Save action.</summary>
internal sealed record AiServiceProfileDraftSubmission(
    string DraftKey,
    string? ProfileId,
    string? ServiceId,
    string ProviderType,
    string Name,
    bool IsEnabled,
    IReadOnlyDictionary<string, string> Configuration,
    IReadOnlyDictionary<string, string> SecretEdits,
    IReadOnlySet<string> ClearedSecretFields,
    bool IsNew,
    bool HasChanges,
    bool IsAutoConnected = false);

internal sealed record AiProfileServiceReferenceChange(string FromServiceId, string? ToServiceId);

internal sealed record AiServiceProfileDraftSaveResult(
    IReadOnlyDictionary<string, AiServiceProfile> ProfilesByDraftKey,
    IReadOnlyList<AiProfileServiceReferenceChange> ReferenceChanges);

/// <summary>
/// Applies the exact AI editor submission used by SettingsPage, keeping persistence behavior
/// independently testable without constructing a WinUI Page.
/// </summary>
internal static class AiServiceProfileDraftSaver
{
    public static AiServiceProfileDraftSaveResult Save(
        SettingsService settings,
        IReadOnlyList<AiServiceProfileDraftSubmission> drafts,
        IReadOnlyCollection<string> deletedProfileIds)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(drafts);
        ArgumentNullException.ThrowIfNull(deletedProfileIds);

        var initialProviderTypes = settings.AiServiceProfiles
            .Select(profile => profile.ProviderType)
            .ToHashSet(StringComparer.Ordinal);
        var deletedIds = deletedProfileIds.ToHashSet(StringComparer.Ordinal);
        var profilesByDraftKey = new Dictionary<string, AiServiceProfile>(StringComparer.Ordinal);
        foreach (var draft in drafts.Where(draft => draft.IsNew))
        {
            var created = settings.AddAiServiceProfile(
                draft.ProviderType, draft.Name.Trim(), draft.IsAutoConnected, deletedIds);
            profilesByDraftKey.Add(draft.DraftKey, created);
        }

        var referenceChanges = new List<AiProfileServiceReferenceChange>();
        foreach (var group in drafts.Where(draft => draft.IsNew)
            .Where(draft => !initialProviderTypes.Contains(draft.ProviderType))
            .Where(draft => AiProviderCatalog.TryGet(draft.ProviderType, out var provider) && provider.HasLegacyConfiguration)
            .GroupBy(draft => draft.ProviderType, StringComparer.Ordinal))
        {
            var firstProfile = profilesByDraftKey[group.First().DraftKey];
            settings.RedirectLegacyAiServiceReferences(group.Key, firstProfile.ServiceId);
            if (AiProviderCatalog.TryGet(group.Key, out var provider))
            {
                referenceChanges.Add(new AiProfileServiceReferenceChange(provider.LegacyServiceId, firstProfile.ServiceId));
            }
        }

        foreach (var draft in drafts.Where(draft => draft.HasChanges))
        {
            var profileId = draft.ProfileId
                ?? (draft.IsNew ? profilesByDraftKey[draft.DraftKey].ProfileId : null)
                ?? throw new InvalidOperationException($"AI profile draft '{draft.DraftKey}' has no profile identity.");
            var current = settings.AiServiceProfiles.First(profile => profile.ProfileId == profileId);

            if (!string.Equals(current.Name, draft.Name.Trim(), StringComparison.Ordinal)
                || current.IsEnabled != draft.IsEnabled)
            {
                settings.UpdateAiServiceProfileDetails(profileId, draft.Name.Trim(), draft.IsEnabled);
            }
            if (!ConfigurationsEqual(current.Configuration, draft.Configuration))
            {
                settings.UpdateAiServiceProfileConfiguration(profileId, draft.Configuration);
            }

            foreach (var fieldId in draft.ClearedSecretFields)
            {
                settings.SetAiServiceProfileSecret(profileId, fieldId, null);
            }
            foreach (var pair in draft.SecretEdits)
            {
                settings.SetAiServiceProfileSecret(profileId, pair.Key, pair.Value);
            }

            profilesByDraftKey[draft.DraftKey] = settings.AiServiceProfiles.First(profile => profile.ProfileId == profileId);
        }

        foreach (var profileId in deletedProfileIds)
        {
            var deletedProfile = settings.AiServiceProfiles.FirstOrDefault(profile => profile.ProfileId == profileId);
            if (deletedProfile is null) continue;

            var replacementDraft = drafts.FirstOrDefault(draft =>
                !deletedProfileIds.Contains(draft.ProfileId ?? string.Empty)
                && draft.ProviderType == deletedProfile.ProviderType);
            var replacement = replacementDraft is null
                ? null
                : profilesByDraftKey.GetValueOrDefault(replacementDraft.DraftKey)
                    ?? settings.AiServiceProfiles.FirstOrDefault(profile => profile.ProfileId == replacementDraft.ProfileId);
            var replacementServiceId = replacement?.ServiceId;

            settings.RemoveAiServiceProfile(profileId, replacementServiceId);
            referenceChanges.Add(new AiProfileServiceReferenceChange(deletedProfile.ServiceId, replacementServiceId));
        }

        return new AiServiceProfileDraftSaveResult(profilesByDraftKey, referenceChanges);
    }

    private static bool ConfigurationsEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
        => left.Count == right.Count
            && left.All(pair => right.TryGetValue(pair.Key, out var value)
                && string.Equals(pair.Value, value, StringComparison.Ordinal));
}
