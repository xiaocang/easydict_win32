#if WINUI_TEST
using System.Text.Json;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.SavedItems;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Easydict.WinUI.Views;

public sealed partial class MainPage
{
    private void InitializeSavedQueryDiagnostics()
    {
        if (Environment.GetEnvironmentVariable("EASYDICT_SAVED_ITEMS_DIAGNOSTICS") != "1") return;
        var seed = new KeyboardAccelerator
        {
            Key = VirtualKey.F10,
            Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift
        };
        seed.Invoked += async (_, args) =>
        {
            args.Handled = true;
            var draft = new QuerySnapshotDraft("Live favorite regression", "en", "zh", SavedQueryKind.Translation,
                QuerySourceKind.Manual, historyEnabled: true);
            _currentSnapshotDraft = draft;
            var results = _serviceResults.Take(2).ToArray();
            string? error = null;
            try
            {
                foreach (var result in results)
                {
                    result.Result = new TranslationResult
                    {
                        OriginalText = draft.SourceText,
                        TranslatedText = "Successful local fixture result",
                        ServiceName = result.ServiceDisplayName
                    };
                    result.MarkQueried();
                    draft.TryAddTranslation(result.ServiceId, result.ServiceDisplayName, _serviceResults.IndexOf(result), result.Result);
                    RefreshServiceResultView(result);
                }
                // Exercise the same optional persistence/read pair used by both
                // automatic-query and manual-provider completion, without a provider call.
                await SavedItemsService.Instance.RecordSnapshotAsync(draft);
                await RefreshCurrentQueryFavoriteAsync(draft);
            }
            catch (Exception exception)
            {
                error = exception.ToString();
            }
            var reportPath = Path.Combine(SettingsService.ResolveSettingsDirectory(), "live-query-favorites.json");
            var temporaryPath = reportPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(new
            {
                QueryId = draft.Id, Providers = results.Select(result => result.ServiceId).ToArray(), Error = error
            }));
            File.Move(temporaryPath, reportPath, overwrite: true);
        };
        KeyboardAccelerators.Add(seed);
    }
}
#endif
