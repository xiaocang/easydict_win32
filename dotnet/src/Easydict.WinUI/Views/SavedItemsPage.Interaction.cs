using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Easydict.WinUI.Models;

namespace Easydict.WinUI.Views;

public sealed partial class SavedItemsPage
{
    private async void OnNavigationInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs e)
    {
        switch ((e.InvokedItemContainer as NavigationViewItem)?.Tag as string)
        {
            case "history":
                await ShowSectionAsync(SavedItemsSection.History);
                break;
            case "favorites":
                await ShowSectionAsync(SavedItemsSection.Favorites);
                break;
            case "settings":
                if (await ConfirmLeaveFavoriteAsync())
                {
                    // Finish the navigation item's input/UIA callback before
                    // unloading result WebViews and their automation providers.
                    SavedNavigation.IsPaneOpen = false;
                    await Task.Yield();
                    if (!_isPageLoaded) return;
                    Frame.Navigate(typeof(SettingsPage));
                    return;
                }
                break;
            case "translation":
                if (await ConfirmLeaveFavoriteAsync())
                {
                    SavedNavigation.IsPaneOpen = false;
                    await Task.Yield();
                    if (!_isPageLoaded) return;
                    NavigateToMainPage();
                    return;
                }
                break;
        }
        SavedNavigation.IsPaneOpen = false;
        ApplySectionState();
    }

    private void OnListPaneSizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout();

    private void OnSavedItemClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SavedItemsRow row && _displayedRow?.StableId == row.StableId && _activeDetail is not null)
        {
            _showingNarrowDetail = true;
            UpdateResponsiveLayout();
        }
    }

    private void OnDetailSizeChanged(object sender, SizeChangedEventArgs e) => UpdateResultsLayout();

    private void OnResultChoiceContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not ResultChoice choice) return;
        args.ItemContainer.IsEnabled = choice.ResultId is not null &&
            (ResultSelector.SelectedItems.Count < 2 || ResultSelector.SelectedItems.Contains(choice));
    }

    private static double RequiredSelectorWidth(SelectorBar selector)
    {
        return selector.Items.Sum(item =>
        {
            var label = new TextBlock { Text = item.Text, FontSize = 14 };
            label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            return label.DesiredSize.Width + 28;
        }) + 8;
    }

    private void UpdateResultsLayout()
    {
        if (!_isInitialized)
            return;
        if (DetailResults.ItemsPanelRoot is SavedResultsPanel panel)
            panel.IsComparison = CompareResultsButton.IsChecked == true;

        var compare = CompareResultsButton.IsChecked == true;
        var useCombo = !compare && (_resultChoices.Count > 4 ||
            RequiredSelectorWidth(ResultProviderTabs) > ResultSelectionPanel.ActualWidth - CompareResultsButton.ActualWidth - 8);
        ResultSelector.Visibility = compare ? Visibility.Visible : Visibility.Collapsed;
        ResultProviderTabs.Visibility = !compare && !useCombo ? Visibility.Visible : Visibility.Collapsed;
        ResultProviderCombo.Visibility = useCombo ? Visibility.Visible : Visibility.Collapsed;
        _updatingResultSelector = true;
        try
        {
            if (!ReferenceEquals(ResultProviderCombo.ItemsSource, _resultChoices))
                ResultProviderCombo.ItemsSource = _resultChoices;
            ResultProviderCombo.SelectedItem = ResultSelector.SelectedItem;
            ResultProviderTabs.SelectedItem = ResultProviderTabs.Items.FirstOrDefault(item => ReferenceEquals(item.Tag, ResultSelector.SelectedItem));
            CompareResultsButton.Content = compare
                ? string.Format(L("SavedItemsCompareCount", "Compare ({0}/2)"), ResultSelector.SelectedItems.Count)
                : L("SavedItemsCompare", "Compare");
        }
        finally { _updatingResultSelector = false; }
    }

    private void OnProviderComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingResultSelector)
            return;
        ResultSelector.SelectedItem = ResultProviderCombo.SelectedItem;
    }

    private void OnProviderTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs e)
    {
        if (!_updatingResultSelector && sender.SelectedItem?.Tag is ResultChoice choice)
            ResultSelector.SelectedItem = choice;
    }

    private Brush? _searchHighlightBackground;
    private Brush? _searchHighlightForeground;

    private void OnPreviewTextLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBlock text)
            return;
        text.TextHighlighters.Clear();
        var query = SavedItemsSearchBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return;
        var firstMatch = text.Text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (firstMatch < 0)
            return;
        // Resource lookup traverses native theme dictionaries. Share the brushes
        // across realized rows and invalidate them only when appearance changes.
        var highlighter = new TextHighlighter
        {
            Background = _searchHighlightBackground ??= Services.ThemeResourceService.GetBrush("SystemFillColorAttentionBackgroundBrush", this),
            Foreground = _searchHighlightForeground ??= Services.ThemeResourceService.GetBrush("TextFillColorPrimaryBrush", this)
        };
        for (var start = firstMatch; start < text.Text.Length;)
        {
            var index = text.Text.IndexOf(query, start, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0)
                break;
            highlighter.Ranges.Add(new TextRange { StartIndex = index, Length = query.Length });
            start = index + query.Length;
        }
        text.TextHighlighters.Add(highlighter);
    }

    private void RefreshListHighlights(DependencyObject root)
    {
        if (root is TextBlock text) OnPreviewTextLoaded(text, new RoutedEventArgs());
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            RefreshListHighlights(VisualTreeHelper.GetChild(root, i));
    }

    private async void OnClearSearchClicked(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmLeaveFavoriteAsync()) return;
        SavedItemsSearchBox.Text = string.Empty;
        _providerId = _timeRangeTag = _historyKindTag = _favoriteKindTag = string.Empty;
        _appliedTags = [];
        _pinnedOnly = false;
        SyncComboSelection(KindCombo, string.Empty);
        SyncComboSelection(FavoriteKindCombo, string.Empty);
        SyncRadioSelection(HistoryKindTabs, string.Empty);
        SyncRadioSelection(FavoriteKindTabs, string.Empty);
        await LoadAsync();
    }

    private void OnEditFavoriteClicked(object sender, RoutedEventArgs e)
    {
        FavoriteSummaryPanel.Visibility = Visibility.Collapsed;
        FavoriteEditorPanel.Visibility = Visibility.Visible;
        FavoriteNoteBox.Focus(FocusState.Programmatic);
    }

    private bool HasUnsavedFavoriteChanges => _activeFavoriteDetail is not null &&
        FavoriteEditorPanel.Visibility == Visibility.Visible &&
        (FavoriteNoteBox.Text != _savedFavoriteNote ||
         !string.IsNullOrWhiteSpace(FavoriteTagsBox.Text) ||
         !_favoriteTags.SequenceEqual(_savedFavoriteTags));

    private bool _leaveDialogOpen;
    private bool _savingFavoriteMetadata;

    private async Task<bool> ConfirmLeaveFavoriteAsync()
    {
        if (!HasUnsavedFavoriteChanges)
            return true;
        if (_leaveDialogOpen)
            return false;
        _leaveDialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = L("SavedItemsUnsavedTitle", "Save your changes?"),
                Content = L("SavedItemsUnsavedDescription", "This favorite has changes to its note or tags."),
                PrimaryButtonText = L("SavedItemsSave", "Save"),
                SecondaryButtonText = L("SavedItemsDiscard", "Discard"),
                CloseButtonText = L("Cancel", "Cancel"),
                DefaultButton = ContentDialogButton.Primary
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
                return await SaveFavoriteChangesAsync();
            if (result == ContentDialogResult.Secondary)
            {
                OnCancelFavoriteMetadataClicked(this, new RoutedEventArgs());
                return true;
            }
            return false;
        }
        finally { _leaveDialogOpen = false; }
    }

    private async Task<bool> SaveFavoriteChangesAsync()
    {
        if (_selectedFavoriteId is not { } favoriteId)
            return true;
        if (!string.IsNullOrWhiteSpace(FavoriteTagsBox.Text) && !TryReplaceFavoriteTags(FavoriteTagsBox.Text))
            return false;
        _savingFavoriteMetadata = true;
        try
        {
            await Services.SavedItems.SavedItemsService.Instance.UpdateFavoriteMetadataAsync(
                favoriteId, FavoriteNoteBox.Text, _favoriteTags.ToArray());
            _savedFavoriteNote = FavoriteNoteBox.Text;
            _savedFavoriteTags = _favoriteTags.ToArray();
            FavoriteTagsBox.Text = string.Empty;
            FavoriteNoteSummary.Text = string.IsNullOrWhiteSpace(_savedFavoriteNote) ? L("SavedItemsNoNote", "No note") : _savedFavoriteNote;
            FavoriteTagsSummary.Text = string.Join(" · ", _savedFavoriteTags);
            FavoriteEditorPanel.Visibility = Visibility.Collapsed;
            FavoriteSummaryPanel.Visibility = Visibility.Visible;
            ShowInfo(L("SavedItemsMetadataSaved", "Favorite details saved."));
            return true;
        }
        catch (Exception exception) { ShowError(exception.Message); return false; }
        finally { _savingFavoriteMetadata = false; }
    }
}
