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
        SavedItemsRow[] Rows, SavedItemsCursor? Cursor, Guid? Selection, double Scroll, long Revision, string Language);

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
        _sectionStates[_section] = new SectionState(
            SavedItemsSearchBox.Text ?? string.Empty,
            _section == SavedItemsSection.History ? _historyKindTag : _favoriteKindTag,
            _providerId, _timeRangeTag, _appliedTags.ToArray(), _pinnedOnly,
            _items.ToArray(), _nextCursor, (SavedItemsList.SelectedItem as SavedItemsRow)?.StableId,
            FindVisualChild<ScrollViewer>(SavedItemsList)?.VerticalOffset ?? 0, _lastDataRevision, LocalizationService.Instance.CurrentLanguage);
    }

    private async Task RestoreSectionAsync()
    {
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
        _nextCursor = null;
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
            AddRows(state.Rows);
            _nextCursor = state.Cursor;
            _lastDataRevision = state.Revision;
        }
        else
        {
            await LoadAsync();
            // Restore a previously paged selection even when the underlying store changed.
            while (_items.Count < state.Rows.Length && _nextCursor is not null && _isPageLoaded)
            {
                var previousCount = _items.Count;
                await LoadNextPageAsync();
                if (_items.Count == previousCount) break;
            }
        }
        UpdateEmptyState();
        SavedItemsList.SelectedItem = _items.FirstOrDefault(row => row.StableId == state.Selection);
        var section = _section;
        var generation = _loadGeneration;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isPageLoaded || section != _section || generation != _loadGeneration) return;
            SavedItemsList.UpdateLayout();
            FindVisualChild<ScrollViewer>(SavedItemsList)?.ChangeView(null, state.Scroll, null, true);
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
