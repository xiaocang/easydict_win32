using System.Diagnostics;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.TextActions;
using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services.TextActions;

/// <summary>
/// The click-action interface: executes a <see cref="TextAction"/> on a <see cref="TextActionContext"/>.
/// New action types are handled by adding a branch here; the UI never dispatches on the type itself.
/// Must be called on the UI thread (it opens windows / launches the browser).
/// </summary>
internal static class TextActionExecutor
{
    /// <summary>Execute the action. Returns <c>true</c> when it was dispatched successfully.</summary>
    public static async Task<bool> ExecuteAsync(TextAction action, TextActionContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(context.Text))
        {
            return false;
        }

        try
        {
            switch (action.Type)
            {
                case TextActionType.OpenUrl:
                    if (!TextActionUrlBuilder.TryBuildUri(action, context, out var uri, out var error))
                    {
                        Debug.WriteLine($"[TextActionExecutor] Invalid URL action '{action.Id}': {error}");
                        return false;
                    }

                    Debug.WriteLine($"[TextActionExecutor] Opening {uri!.Host} for action '{action.Id}'");
                    return await Windows.System.Launcher.LaunchUriAsync(uri);

                case TextActionType.RunService:
                    if (string.IsNullOrEmpty(action.ServiceId))
                    {
                        return false;
                    }

                    Debug.WriteLine($"[TextActionExecutor] Running service '{action.ServiceId}' for action '{action.Id}'");
                    MiniWindowService.Instance.ShowWithTextForService(context.Text, action.ServiceId, QuerySourceKind.Selection);
                    return true;

                default:
                    Debug.WriteLine($"[TextActionExecutor] Unsupported action type {action.Type}");
                    return false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TextActionExecutor] Action '{action.Id}' failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Build the context for text captured from a selection (no translation yet; the target language
    /// follows the user's first/second language rule).
    /// </summary>
    public static TextActionContext CreateSelectionContext(string text)
    {
        Language target;
        try
        {
            target = new TargetLanguageSelector(SettingsService.Instance).ResolveAutoTargetLanguage(Language.Auto);
        }
        catch
        {
            target = Language.English;
        }

        return new TextActionContext(text, null, Language.Auto, target);
    }
}
