using System.Runtime.InteropServices;
using Easydict.TranslationService.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace Easydict.WinUI.Services;

internal static class TranslationDiagnosticsPresenter
{
    private static bool _isOpen;

    public static async Task ShowAsync(FrameworkElement owner, ServiceQueryResult? result)
    {
        if (_isOpen || owner.XamlRoot is null || result?.Diagnostics is not { } diagnostics) return;
        var loc = LocalizationService.Instance;
        var text = new TextBlock { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        var dialog = new ContentDialog
        {
            Title = loc.GetString("ViewQueryDiagnostics") + " · " + result.ServiceDisplayName,
            Content = new ScrollViewer { Content = text, MaxHeight = 440, MinWidth = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = loc.GetString("Close"),
            SecondaryButtonText = loc.GetString("Copy"),
            XamlRoot = owner.XamlRoot
        };
        var timer = owner.DispatcherQueue.CreateTimer();
        AutomationProperties.SetAutomationId(dialog, "QueryDiagnosticsDialog");
        AutomationProperties.SetAutomationId(text, "QueryDiagnosticsText");
        timer.Interval = TimeSpan.FromMilliseconds(250);
        void Refresh() => text.Text = TranslationDiagnosticFormatter.Format(diagnostics.Snapshot(), loc.GetString);
        void OnUnloaded(object sender, RoutedEventArgs args)
        {
            try { dialog.Hide(); } catch (COMException) { }
        }
        timer.Tick += (_, _) => Refresh();
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            try { ClipboardService.SetText(text.Text); } catch (COMException) { }
        };
        owner.Unloaded += OnUnloaded;
        _isOpen = true;
        try
        {
            Refresh();
            timer.Start();
            await dialog.ShowAsync();
        }
        catch (COMException)
        {
            // The owner can close between menu activation and the dialog opening.
        }
        finally
        {
            timer.Stop();
            owner.Unloaded -= OnUnloaded;
            _isOpen = false;
        }
    }
}
