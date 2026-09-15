using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.TextActions;

namespace Easydict.WinUI.Services.TextActions;

/// <summary>
/// A text action paired with presentation data resolved at display time.
/// </summary>
/// <param name="Action">The action.</param>
/// <param name="Origin">Origin of the targeted service (built-in for OpenUrl actions).</param>
/// <param name="ServiceDisplayName">Display name of the targeted service, if any.</param>
internal readonly record struct PresentedTextAction(TextAction Action, ServiceOrigin Origin, string? ServiceDisplayName)
{
    /// <summary>True when the action runs a third-party plugin service.</summary>
    public bool IsPluginAction => Action.Type == TextActionType.RunService && Origin.Kind == ServiceOriginKind.Plugin;
}

/// <summary>
/// Reads the configured text actions and filters out the ones that cannot run right now
/// (for example a RunService action whose service is no longer registered).
/// </summary>
internal static class TextActionRegistry
{
    /// <summary>Maximum number of action buttons shown on the selection pop-up strip.</summary>
    public const int MaxPopButtonActions = 4;

    /// <summary>Enabled, runnable actions in the user's order.</summary>
    public static IReadOnlyList<PresentedTextAction> GetActions()
    {
        return Present(SettingsService.Instance.TextActions.Where(a => a.IsEnabled));
    }

    /// <summary>Actions that should appear on the selection pop-up strip (capped).</summary>
    public static IReadOnlyList<PresentedTextAction> GetPopButtonActions()
    {
        return Present(SettingsService.Instance.TextActions.Where(a => a.IsEnabled && a.ShowOnPopButton))
            .Take(MaxPopButtonActions)
            .ToList();
    }

    /// <summary>Resolve presentation data for a single action (origin + service name).</summary>
    public static PresentedTextAction Present(TextAction action)
    {
        var presented = Present(new[] { action });
        return presented.Count > 0 ? presented[0] : new PresentedTextAction(action, ServiceOrigin.BuiltIn, null);
    }

    private static List<PresentedTextAction> Present(IEnumerable<TextAction> actions)
    {
        IReadOnlyDictionary<string, ITranslationService>? services = null;
        try
        {
            services = TranslationManagerService.Instance.Manager.Services;
        }
        catch
        {
            // Manager not available yet (early startup) — RunService actions are simply hidden.
        }

        var result = new List<PresentedTextAction>();
        foreach (var action in actions)
        {
            switch (action.Type)
            {
                case TextActionType.OpenUrl:
                    if (TextActionUrlBuilder.ValidateTemplate(action.UrlTemplate) is null)
                    {
                        result.Add(new PresentedTextAction(action, ServiceOrigin.BuiltIn, null));
                    }
                    break;

                case TextActionType.RunService:
                    if (!string.IsNullOrEmpty(action.ServiceId)
                        && services is not null
                        && services.TryGetValue(action.ServiceId, out var service))
                    {
                        result.Add(new PresentedTextAction(
                            action,
                            ServiceOriginHelper.Resolve(service, action.ServiceId),
                            service.DisplayName));
                    }
                    break;
            }
        }

        return result;
    }
}
