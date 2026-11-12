using System.Runtime.InteropServices;
using Easydict.TranslationService.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Easydict.WinUI.Services;

internal static class TranslationDiagnosticsPresenter
{
    private const double BarHeight = 8;
    private const double BarRadius = 2;
    private const double MinimumVisibleRatio = 0.002;

    private static bool _isOpen;

    public static async Task ShowAsync(FrameworkElement owner, ServiceQueryResult? result)
    {
        if (_isOpen || owner.XamlRoot is null || result?.Diagnostics is not { } diagnostics) return;
        var loc = LocalizationService.Instance;
        var text = new TextBlock { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        var waterfall = new StackPanel { Spacing = 6 };

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(waterfall);
        content.Children.Add(text);

        var dialog = new ContentDialog
        {
            Title = loc.GetString("ViewQueryDiagnostics") + " · " + result.ServiceDisplayName,
            Content = new ScrollViewer { Content = content, MaxHeight = 440, MinWidth = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = loc.GetString("Close"),
            SecondaryButtonText = loc.GetString("Copy"),
            XamlRoot = owner.XamlRoot
        };
        var timer = owner.DispatcherQueue.CreateTimer();
        AutomationProperties.SetAutomationId(dialog, "QueryDiagnosticsDialog");
        AutomationProperties.SetAutomationId(text, "QueryDiagnosticsText");
        AutomationProperties.SetAutomationId(waterfall, "QueryDiagnosticsWaterfall");
        timer.Interval = TimeSpan.FromMilliseconds(250);
        void Refresh()
        {
            var snapshot = diagnostics.Snapshot();
            text.Text = TranslationDiagnosticFormatter.Format(snapshot, loc.GetString);
            Populate(waterfall, snapshot, loc.GetString, ResolveBarBrush(owner));
        }
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

    /// <summary>
    /// Rebuilds one row per stage. Bars are sized with star columns, so the track stretches to the
    /// dialog width without measuring text or handling SizeChanged, and rows grow with the stage count.
    /// </summary>
    private static void Populate(StackPanel host, TranslationDiagnosticSnapshot snapshot, Func<string, string> localize, Brush brush)
    {
        host.Children.Clear();
        var bars = TranslationDiagnosticWaterfall.Build(snapshot);

        for (var index = 0; index < snapshot.Timings.Count; index++)
        {
            var phase = snapshot.Timings[index];
            var bar = bars[index];
            var label = localize("DiagnosticStage" + phase.Stage)
                + (phase.IsRunning ? $" ({localize("DiagnosticStatusRunning")})" : "");

            host.Children.Add(CreateRow(bar, brush, label, phase));
        }
    }

    private static FrameworkElement CreateRow(
        DiagnosticBar bar, Brush brush, string label, TranslationDiagnosticTiming phase)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var track = new Grid { Height = BarHeight };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(bar.OffsetRatio, GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(bar.LengthRatio, GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - bar.OffsetRatio - bar.LengthRatio, MinimumVisibleRatio), GridUnitType.Star) });

        var barShape = new Rectangle
        {
            Height = BarHeight,
            RadiusX = BarRadius,
            RadiusY = BarRadius,
            Fill = brush,
            Opacity = bar.IsRunning ? 0.55 : 1
        };
        Grid.SetColumn(barShape, 1);
        track.Children.Add(barShape);
        row.Children.Add(track);

        var caption = new TextBlock
        {
            Text = $"{label}  {phase.StartedMs:F0}/{phase.DurationMs:F0} ms",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        ToolTipService.SetToolTip(caption, $"{label}{Environment.NewLine}{phase.StartedMs:F0} ms → {phase.StartedMs + phase.DurationMs:F0} ms");
        Grid.SetColumn(caption, 1);
        row.Children.Add(caption);
        return row;
    }

    private static Brush ResolveBarBrush(FrameworkElement owner) =>
        ThemeResourceService.GetBrush("AccentFillColorDefaultBrush", owner)
        ?? new SolidColorBrush(Microsoft.UI.Colors.SteelBlue);
}
