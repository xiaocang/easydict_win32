#if WINUI_TEST
using System.Text.Json;
using Easydict.WinUI.Services;
using Easydict.WinUI.Views.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Easydict.WinUI.Views;

public sealed partial class SavedItemsPage
{
    private static readonly List<WeakReference<IServiceResultView>> ObservedResults = [];

    private void InitializeSavedItemsDiagnostics()
    {
        if (Environment.GetEnvironmentVariable("EASYDICT_SAVED_ITEMS_DIAGNOSTICS") != "1") return;
        var probe = new KeyboardAccelerator { Key = VirtualKey.F12, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift };
        probe.Invoked += async (sender, args) =>
        {
            args.Handled = true;
            // Keep the UI thread pumping while native/managed finalizers release old views.
            await Task.Run(() => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); });
            ObservedResults.RemoveAll(reference => !reference.TryGetTarget(out _));
            var realized = Enumerable.Range(0, _items.Count).Count(index => SavedItemsList.ContainerFromIndex(index) is not null);
            var metrics = new
            {
                AliveResults = ObservedResults.Count,
                ActiveResults = _detailResultControls.Count + _otherResultControls.Count,
                LoadedRows = _items.Count,
                RealizedRows = realized,
                PageWidth = ActualWidth,
                Dpi = XamlRoot.RasterizationScale
            };
            var directory = SettingsService.ResolveSettingsDirectory();
            var reportPath = Path.Combine(directory, "saved-items-metrics.json");
            var temporaryPath = reportPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(metrics));
            // Existence signals a complete report to the UI test reader.
            File.Move(temporaryPath, reportPath, overwrite: true);
        };
        KeyboardAccelerators.Add(probe);
    }
}
#endif
