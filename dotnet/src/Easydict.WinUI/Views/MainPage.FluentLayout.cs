using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Easydict.WinUI.Services;
using Microsoft.UI.Xaml.Automation;

namespace Easydict.WinUI.Views;

public sealed partial class MainPage
{
    private bool _quickWideLayout;
    private double _stackedScrollOffset;
    private double _inputScrollOffset;
    private double _resultScrollOffset;

    private void InitializeFluentLayout()
    {
        SizeChanged += (_, _) => UpdateFluentLayout();
        QuickTranslateContent.SizeChanged += (_, _) => UpdateFluentLayout();
        Loaded += (_, _) => UpdateFluentLayout();
    }

    private void UpdateFluentLayout()
    {
        if (QuickContentGrid is null || ActualWidth <= 0) return;
        var wide = ActualWidth >= 960;
        // Capture before disabling the outer scroller, which can coerce its offset.
        if (wide && !_quickWideLayout)
            _stackedScrollOffset = QuickTranslateContent.VerticalOffset;
        else if (!wide && _quickWideLayout)
        {
            _inputScrollOffset = QuickInputScrollViewer.VerticalOffset;
            _resultScrollOffset = QuickResultsScrollViewer.VerticalOffset;
        }
        AutomationProperties.SetItemStatus(ModeSelectorButton,
            FormattableString.Invariant($"PageWidth={ActualWidth:0.##};Wide={wide}"));
        var margin = wide ? 24 : 16;
        var gap = IsCompactChrome ? 8 : 16;
        MainHeader.Margin = new Thickness(margin, 8, margin, 8);
        QuickTranslateContent.HorizontalContentAlignment = HorizontalAlignment.Left;
        QuickContentGrid.Width = Math.Min(1280, QuickTranslateContent.ActualWidth);
        QuickContentGrid.Padding = new Thickness(margin, 0, margin, margin);
        QuickContentGrid.ColumnSpacing = wide ? 16 : 0;
        QuickContentGrid.RowSpacing = wide ? 0 : gap;
        QuickInputColumn.Width = wide ? new GridLength(360) : new GridLength(1, GridUnitType.Star);
        QuickResultColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(QuickResultsScrollViewer, wide ? 1 : 0);
        Grid.SetRow(QuickResultsScrollViewer, wide ? 0 : 1);
        var viewportHeight = Math.Max(0, QuickTranslateContent.ActualHeight - margin);
        QuickInputScrollViewer.Height = wide ? viewportHeight : double.NaN;
        QuickResultsScrollViewer.Height = wide ? viewportHeight : double.NaN;
        QuickInputScrollViewer.VerticalScrollBarVisibility = wide ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        QuickResultsScrollViewer.VerticalScrollBarVisibility = wide ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        QuickTranslateContent.VerticalScrollBarVisibility = wide ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        // In two-column mode only the column under the pointer may scroll.
        // Auto leaves a fitting input column stationary, but keeps short windows usable.
        QuickTranslateContent.VerticalScrollMode = wide ? ScrollMode.Disabled : ScrollMode.Auto;
        QuickInputScrollViewer.VerticalScrollMode = wide ? ScrollMode.Auto : ScrollMode.Disabled;
        QuickResultsScrollViewer.VerticalScrollMode = wide ? ScrollMode.Auto : ScrollMode.Disabled;
        QuickInputScrollViewer.IsVerticalScrollChainingEnabled = !wide;
        QuickResultsScrollViewer.IsVerticalScrollChainingEnabled = !wide;
        QuickInputCard.Margin = QuickOutputCard.Margin = new Thickness(0);
        QuickInputCard.Padding = QuickOutputCard.Padding = new Thickness(IsCompactChrome ? 8 : 12);
        QuickInputCardContent.Margin = QuickOutputCardContent.Margin = new Thickness(0);
        QuickInputHeaderRow.Height = GridLength.Auto;
        SourcePlayButton.Width = SourcePlayButton.Height = 32;
        ActionBarWide.Margin = new Thickness(0, gap, 0, gap);
        SourceLangCombo.MinWidth = TargetLangCombo.MinWidth = 0;
        ResultsTitleText.FontSize = 16;
        QuickSourceTitleText.FontSize = 16;
        QuickSourceTitleText.Text = LocalizationService.Instance.GetString("SourceText");
        ModeTitleText.FontSize = 24;
        LongDocContentGrid.MaxWidth = 1280;
        LongDocContentGrid.Padding = new Thickness(margin, 0, margin, margin);
        LongDocControlBar.RowSpacing = gap;
        LongDocControlBar.Margin = new Thickness(0, gap, 0, gap);
        LongDocInputCard.Padding = LongDocOutputCard.Padding = new Thickness(IsCompactChrome ? 8 : 12);
        LongDocInputTitle.FontSize = LongDocOutputTitle.FontSize = 16;
        LongDocRetryButton.MinHeight = 32;
        LongDocMoreOptions.Header = LocalizationService.Instance.GetString("FluentMoreOptions");
        // Give the provider its own row on narrow pages without duplicating controls.
        while (LongDocControlBar.RowDefinitions.Count < 4)
            LongDocControlBar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(LongDocServiceCombo, wide ? 0 : 1);
        Grid.SetColumn(LongDocServiceCombo, wide ? 2 : 0);
        Grid.SetColumnSpan(LongDocServiceCombo, wide ? 2 : 4);
        Grid.SetColumnSpan(LongDocSourceLangCombo, wide ? 1 : 2);
        Grid.SetColumn(LongDocTargetLangCombo, wide ? 1 : 2);
        Grid.SetColumnSpan(LongDocTargetLangCombo, wide ? 1 : 2);
        Grid.SetRow(LongDocMoreOptions, wide ? 1 : 2);
        Grid.SetRow(LongDocDocumentContextPassCheckBox, wide ? 2 : 3);
        Grid.SetRow(LongDocTranslateButton, wide ? 2 : 3);
        if (wide == _quickWideLayout) return;
        _quickWideLayout = wide;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_quickWideLayout)
            {
                QuickTranslateContent.ChangeView(null, 0, null, true);
                QuickInputScrollViewer.ChangeView(null, _inputScrollOffset, null, true);
                QuickResultsScrollViewer.ChangeView(null, _resultScrollOffset, null, true);
            }
            else QuickTranslateContent.ChangeView(null, _stackedScrollOffset, null, true);
        });
    }
}
