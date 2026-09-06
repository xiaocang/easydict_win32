using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Easydict.WinUI.Views;

public sealed partial class SettingsPage
{
    private bool? _settingsWideLayout;
    private double _settingsStackedOffset;
    private double _settingsDetailsOffset;

    private void InitializeFluentSettingsLayout()
    {
        Loaded += (_, _) => UpdateFluentSettingsLayout();
        SizeChanged += (_, _) => UpdateFluentSettingsLayout();
        HeaderSection.SizeChanged += (_, _) => UpdateFluentSettingsLayout();
    }

    private void UpdateFluentSettingsLayout()
    {
        var wide = ActualWidth >= 960;
        if (ActualWidth <= 0) return;
        var changed = _settingsWideLayout != wide;
        if (changed)
        {
            if (wide) _settingsStackedOffset = MainScrollViewer.VerticalOffset;
            else _settingsDetailsOffset = SettingsDetailsScrollViewer.VerticalOffset;
        }
        var margin = wide ? 24 : 16;
        MainScrollViewer.Padding = new Thickness(margin);
        SettingsContentRoot.Width = Math.Min(1280, Math.Max(0, ActualWidth - 2 * margin));
        SettingsNavigationColumn.Width = new GridLength(wide ? 192 : 0);
        SettingsContentRoot.ColumnSpacing = wide ? 16 : 0;
        Grid.SetColumnSpan(SettingsNavigationScrollViewer, wide ? 1 : 2);
        var height = Math.Max(0, ActualHeight - 2 * margin - HeaderSection.ActualHeight - SettingsContentRoot.RowSpacing);
        MainScrollViewer.VerticalScrollMode = wide ? ScrollMode.Disabled : ScrollMode.Auto;
        MainScrollViewer.VerticalScrollBarVisibility = wide ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        foreach (var scroller in new[] { SettingsNavigationScrollViewer, SettingsDetailsScrollViewer })
        {
            scroller.Height = wide ? height : double.NaN;
            scroller.VerticalScrollMode = wide ? ScrollMode.Auto : ScrollMode.Disabled;
            scroller.VerticalScrollBarVisibility = wide ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            scroller.IsVerticalScrollChainingEnabled = !wide;
        }
        if (_settingsWideLayout != wide)
        {
            _settingsWideLayout = wide;
            SettingsTabsHost.ItemsPanel = (ItemsPanelTemplate)Resources[wide ? "SettingsRailItemsPanel" : "SettingsCompactTabsPanel"];
            SettingsTabsHost.ItemTemplate = (DataTemplate)Resources[wide ? "SettingsRailItemTemplate" : "SettingsCompactTabItemTemplate"];
        }
        Grid.SetRow(SettingsDetailsScrollViewer, wide ? 1 : 2);
        Grid.SetColumn(SettingsDetailsScrollViewer, wide ? 1 : 0);
        Grid.SetColumnSpan(SettingsDetailsScrollViewer, wide ? 1 : 2);
        if (changed) DispatcherQueue.TryEnqueue(() =>
        {
            if (_settingsWideLayout == true)
            {
                MainScrollViewer.ChangeView(null, 0, null, true);
                SettingsDetailsScrollViewer.ChangeView(null, _settingsDetailsOffset, null, true);
            }
            else MainScrollViewer.ChangeView(null, _settingsStackedOffset, null, true);
        });
    }

}
