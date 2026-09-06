using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.SavedItems;
using Easydict.WinUI.Views.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Easydict.WinUI.Views;

public sealed partial class SavedItemsPage
{
    private readonly Dictionary<SavedItemsSection, SectionState> _sectionStates = new();
    private readonly DispatcherQueueTimer _dayTimer;
    private readonly DispatcherQueueTimer _messageTimer;
    private DateTime _groupDay = DateTime.Today;
    private long _lastDataRevision;
    private string _appliedSearch = string.Empty;
    private bool _restoringSelection;
    private bool _allowNavigation;
    private SavedItemsRow? _displayedRow;
    private IReadOnlyList<SavedQueryResultDetail> _pendingOtherResults = [];
    private bool HasActiveFilters => _section == SavedItemsSection.History
        ? _historyKindTag.Length > 0 || _providerId.Length > 0 || _timeRangeTag.Length > 0
        : _favoriteKindTag.Length > 0 || _appliedTags.Count > 0 || _pinnedOnly;

    private sealed record SectionState(
        string Search, string Kind, string Provider, string TimeRange, IReadOnlyList<string> Tags, bool Pinned,
        SavedItemsRow[] Rows, SavedItemsCursor? PreviousCursor, SavedItemsCursor? Cursor,
        SavedItemsRow? Selection, ListAnchor? Anchor, double Scroll, long Revision, string Language);

    private sealed record ListAnchor(Guid Id, double Top);
    private ScrollViewer? _listScrollViewer;
    private double _lastListOffset;

    private void ObserveListScrolling()
    {
        if (!_isPageLoaded) return;
        var scroll = FindVisualChild<ScrollViewer>(SavedItemsList);
        if (scroll is null || ReferenceEquals(scroll, _listScrollViewer)) return;
        if (_listScrollViewer is not null) _listScrollViewer.ViewChanged -= OnListViewChanged;
        _listScrollViewer = scroll;
        _lastListOffset = scroll.VerticalOffset;
        scroll.ViewChanged += OnListViewChanged;
    }

    private void OnListViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroll) return;
        var delta = scroll.VerticalOffset - _lastListOffset;
        _lastListOffset = scroll.VerticalOffset;
        if (!_isPageLoaded || _isLoadingNextPage || _restoringSelection || _nextPageQueued) return;
        var previous = delta < 0 && scroll.VerticalOffset < 300 && _listWindow.PreviousCursor is not null;
        var next = delta > 0 && scroll.ScrollableHeight - scroll.VerticalOffset < 300 && _nextCursor is not null;
        if (!previous && !next) return;
        _nextPageQueued = true;
        var generation = _loadGeneration;
        // SQLite may complete synchronously. Mutate the list after the scroll/layout callback.
        DispatcherQueue.TryEnqueue(async () =>
        {
            _nextPageQueued = false;
            if (_isPageLoaded && generation == _loadGeneration && !_restoringSelection)
                await LoadNextPageAsync(previous);
        });
    }

    private ListAnchor? CaptureListAnchor()
    {
        if (FindVisualChild<ScrollViewer>(SavedItemsList) is not { } scroll) return null;
        foreach (var row in _items)
        {
            if (SavedItemsList.ContainerFromItem(row) is not FrameworkElement container) continue;
            var top = container.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point()).Y;
            if (top + container.ActualHeight > 0 && top < scroll.ViewportHeight)
                return new ListAnchor(row.StableId, top);
        }
        return null;
    }

    private void RestoreListAnchor(ListAnchor? anchor)
    {
        if (anchor is null || _items.FirstOrDefault(row => row.StableId == anchor.Id) is not { } row) return;
        SavedItemsList.ScrollIntoView(row, ScrollIntoViewAlignment.Leading);
        SavedItemsList.UpdateLayout();
        if (FindVisualChild<ScrollViewer>(SavedItemsList) is { } scroll &&
            SavedItemsList.ContainerFromItem(row) is FrameworkElement container)
        {
            var top = container.TransformToVisual(scroll).TransformPoint(new Windows.Foundation.Point()).Y;
            scroll.ChangeView(null, Math.Max(0, scroll.VerticalOffset + top - anchor.Top), null, true);
        }
    }

    private void ReplaceWindowRows()
    {
        // Paging must not discard an open detail or an unsaved favorite edit when its
        // selected row leaves the bounded list window.
        var selectedId = (SavedItemsList.SelectedItem as SavedItemsRow)?.StableId ?? _displayedRow?.StableId;
        _restoringSelection = true;
        try
        {
            _items.Clear();
            AddRows(_listWindow.Items);
            var selected = _items.FirstOrDefault(row => row.StableId == selectedId);
            SavedItemsList.SelectedItem = selected;
            if (selected is not null)
            {
                selected.IsSelected = true;
                _displayedRow = selected;
            }
        }
        finally { _restoringSelection = false; }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void CaptureSectionState()
    {
        var selected = (SavedItemsList.SelectedItem as SavedItemsRow) ?? _displayedRow;
        // Cache data rows, never rows whose PropertyChanged event is bound to recycled XAML.
        var selection = selected is null ? null : new SavedItemsRow(
            selected.QueryId, selected.FavoriteId, selected.SourceText, selected.Metadata,
            selected.PreviewText, selected.TimeText, selected.Tags, selected.CreatedUtc, selected.GroupTitle)
            { IconGlyph = selected.IconGlyph, Cursor = selected.Cursor };
        _sectionStates[_section] = new SectionState(
            SavedItemsSearchBox.Text ?? string.Empty,
            _section == SavedItemsSection.History ? _historyKindTag : _favoriteKindTag,
            _providerId, _timeRangeTag, _appliedTags.ToArray(), _pinnedOnly,
            _listWindow.Items.ToArray(), _listWindow.PreviousCursor, _nextCursor,
            selection, CaptureListAnchor(),
            FindVisualChild<ScrollViewer>(SavedItemsList)?.VerticalOffset ?? 0, _lastDataRevision, LocalizationService.Instance.CurrentLanguage);
    }

    private async Task RestoreSectionAsync()
    {
        var section = _section;
        _searchTimer.Stop();
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        Interlocked.Increment(ref _loadGeneration);
        _restoringSelection = true;
        _items.Clear();
        SavedItemsList.SelectedItem = null;
        _restoringSelection = false;
        ClearDetail();
        _listWindow.Clear();
        if (!_sectionStates.TryGetValue(_section, out var state))
        {
            SavedItemsSearchBox.Text = _appliedSearch = string.Empty;
            await LoadAsync();
            return;
        }

        SavedItemsSearchBox.Text = _appliedSearch = state.Search;
        if (_section == SavedItemsSection.History) _historyKindTag = state.Kind;
        else _favoriteKindTag = state.Kind;
        _providerId = state.Provider;
        _timeRangeTag = state.TimeRange;
        _appliedTags = state.Tags;
        _pinnedOnly = state.Pinned;
        SyncComboSelection(KindCombo, _historyKindTag);
        SyncRadioSelection(HistoryKindTabs, _historyKindTag);
        SyncComboSelection(FavoriteKindCombo, _favoriteKindTag);
        SyncRadioSelection(FavoriteKindTabs, _favoriteKindTag);
        if (state.Revision == SavedItemsService.Instance.Revision && state.Language == LocalizationService.Instance.CurrentLanguage)
        {
            _listWindow.Reset(new SavedItemsPageResult<SavedItemsRow>(state.Rows, state.Cursor), state.PreviousCursor);
            ReplaceWindowRows();
            _lastDataRevision = state.Revision;
        }
        else
        {
            // Resume at the saved window, not by loading every preceding page again.
            await LoadAsync(state.PreviousCursor is null ? null : state.Rows.FirstOrDefault()?.Cursor, includeCursor: true);
            while (_items.Count < state.Rows.Length && _nextCursor is not null && _isPageLoaded)
            {
                var previousCount = _items.Count;
                await LoadNextPageAsync();
                if (_items.Count == previousCount) break;
            }
        }
        if (!_isPageLoaded || _section != section) return;
        UpdateEmptyState();
        var generation = _loadGeneration;
        var selected = _items.FirstOrDefault(row => row.StableId == state.Selection?.StableId);
        _restoringSelection = true;
        try { SavedItemsList.SelectedItem = selected; }
        finally { _restoringSelection = false; }
        if ((selected ?? state.Selection) is { } selection)
            await LoadDetailAsync(selection);
        if (!_isPageLoaded || section != _section || generation != _loadGeneration) return;
        _restoringSelection = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (!_isPageLoaded || section != _section || generation != _loadGeneration) return;
                SavedItemsList.UpdateLayout();
                ObserveListScrolling();
                if (state.Anchor is not null) RestoreListAnchor(state.Anchor);
                else FindVisualChild<ScrollViewer>(SavedItemsList)?.ChangeView(null, state.Scroll, null, true);
            }
            finally { _restoringSelection = false; }
        });
    }

    protected override async void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
        if (_allowNavigation || !HasUnsavedFavoriteChanges) return;
        e.Cancel = true;
        var frame = Frame;
        var mode = e.NavigationMode;
        var pageType = e.SourcePageType;
        var parameter = e.Parameter;
        if (!await ConfirmLeaveFavoriteAsync()) return;
        _allowNavigation = true;
        try
        {
            if (mode == NavigationMode.Back) frame.GoBack();
            else if (mode == NavigationMode.Forward) frame.GoForward();
            else frame.Navigate(pageType, parameter);
        }
        finally { _allowNavigation = false; }
    }

    private void OnDayTimerTick(DispatcherQueueTimer sender, object e)
    {
        if (_groupDay == DateTime.Today || HasUnsavedFavoriteChanges) return;
        _groupDay = DateTime.Today;
        CaptureSectionState();
        _ = RestoreSectionAsync();
    }

    private void OnOtherResultsExpanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        if (_otherResultControls.Count > 0) return;
        foreach (var result in _pendingOtherResults)
            AddSavedResult(result, OtherDetailResults, _otherResultControls);
    }

    private void AnnounceMessage(string message)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(PageInfoBar)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(PageInfoBar);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted,
            AutomationNotificationProcessing.ImportantMostRecent, message, "SavedItemsMessage");
    }

    private void OnSavedThemeChanged(FrameworkElement sender, object args) => RefreshSavedAppearance();

    internal void RefreshSavedAppearance()
    {
        if (!_isPageLoaded) return;
        _searchHighlightBackground = _searchHighlightForeground = null;
        RefreshListHighlights(SavedItemsList);
        var controls = _detailResultControls.Concat(_otherResultControls).ToArray();
        if (controls.Any(control => control.IsMinimalRenderer != MinimalThemeService.IsActive) &&
            _activeDetail is not null && _displayedRow is not null)
        {
            // Preserve editor drafts through a renderer-only theme change.
            var note = FavoriteNoteBox.Text;
            var tags = _favoriteTags.ToArray();
            var pendingTags = FavoriteTagsBox.Text;
            var editing = FavoriteEditorPanel.Visibility == Visibility.Visible;
            var compare = CompareResultsButton.IsChecked == true;
            var selected = ResultSelector.SelectedItems.OfType<ResultChoice>().Select(choice => choice.ResultId).ToArray();
            var expansion = controls.Where(control => control.ServiceResult is not null && _resultIds.ContainsKey(control.ServiceResult))
                .ToDictionary(control => _resultIds[control.ServiceResult!], control => control.ServiceResult!.IsExpanded);
            var otherExpanded = OtherResultsExpander.IsExpanded;
            var scroll = DetailScroll.VerticalOffset;
            PopulateDetail(_displayedRow, _activeDetail, _activeFavoriteDetail);
            FavoriteNoteBox.Text = note;
            _favoriteTags.Clear();
            foreach (var tag in tags) _favoriteTags.Add(tag);
            FavoriteTagsBox.Text = pendingTags;
            FavoriteEditorPanel.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            FavoriteSummaryPanel.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
            if (compare)
            {
                CompareResultsButton.IsChecked = true;
                OnCompareResultsClicked(this, new RoutedEventArgs());
                _updatingResultSelector = true;
                ResultSelector.SelectedItems.Clear();
                foreach (var choice in _resultChoices.Where(choice => selected.Contains(choice.ResultId)))
                    ResultSelector.SelectedItems.Add(choice);
                _updatingResultSelector = false;
                RenderSelectedResults();
            }
            else if (_activeFavoriteDetail?.Favorite.TargetKind != FavoriteTargetKind.Result)
                ResultSelector.SelectedItem = _resultChoices.FirstOrDefault(choice => selected.Contains(choice.ResultId)) ?? _resultChoices.FirstOrDefault();
            OtherResultsExpander.IsExpanded = otherExpanded;
            foreach (var view in _detailResultControls.Concat(_otherResultControls))
            {
                if (view.ServiceResult is { } result && _resultIds.TryGetValue(result, out var id) && expansion.TryGetValue(id, out var expanded))
                    result.IsExpanded = expanded;
            }
            DispatcherQueue.TryEnqueue(() => DetailScroll.ChangeView(null, scroll, null, true));
        }
        else
        {
            ServiceResultViewHost.RefreshThemeChrome(controls, this);
            ServiceResultViewHost.RefreshAppearance(controls);
        }
        DetailSourceText.FontSize = 14 * AppearanceService.FontScale;
    }
}
