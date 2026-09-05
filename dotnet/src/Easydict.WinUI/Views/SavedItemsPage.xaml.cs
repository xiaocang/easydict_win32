using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Models;
using Easydict.WinUI.Services;
using Easydict.WinUI.Services.SavedItems;
using Easydict.WinUI.Views.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Easydict.WinUI.Views;

public sealed partial class SavedItemsPage : Page
{
    private const double NarrowBreakpoint = 960;
    private const int PageSize = 25;

    private readonly ObservableCollection<SavedItemsRow> _items = [];
    private readonly ObservableCollection<string> _favoriteTags = [];
    private readonly List<IServiceResultView> _detailResultControls = [];
    private readonly List<IServiceResultView> _otherResultControls = [];
    private readonly Dictionary<ServiceQueryResult, Guid> _resultIds = new();
    private readonly HashSet<Guid> _pendingResultFavorites = [];
    private int _favoriteStateGeneration;
    private readonly List<ResultChoice> _resultChoices = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _searchTimer;
    private CancellationTokenSource? _loadCts;
    private SavedItemsSection _section = SavedItemsSection.History;
    private SavedItemsCursor? _nextCursor;
    private SavedQueryDetail? _activeDetail;
    private FavoriteDetail? _activeFavoriteDetail;
    private FavoriteStateMap _favoriteStates = new(false, new HashSet<Guid>());
    private Guid? _selectedFavoriteId;
    private string _historyKindTag = string.Empty;
    private string _favoriteKindTag = string.Empty;
    private string _providerId = string.Empty;
    private string _timeRangeTag = string.Empty;
    private IReadOnlyList<string> _appliedTags = [];
    private bool _pinnedOnly;
    private int _loadGeneration;
    private int _detailGeneration;
    private bool _isPageLoaded;
    private bool _isInitialized;
    private bool _isLoadingNextPage;
    private bool _nextPageQueued;
    private bool _showingNarrowDetail;
    private bool _updatingFavoriteMetadata;
    private bool _updatingKindSelectors;
    private bool _updatingResultSelector;
    private string _savedFavoriteNote = string.Empty;
    private IReadOnlyList<string> _savedFavoriteTags = [];

    public SavedItemsPage()
    {
        InitializeComponent();
        ApplyLocalization();
        SavedItemsList.ItemsSource = _items;
        FavoriteTagChips.ItemsSource = _favoriteTags;
        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(150);
        _searchTimer.Tick += OnSearchTimerTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _dayTimer = DispatcherQueue.CreateTimer();
        _dayTimer.Interval = TimeSpan.FromSeconds(30);
        _dayTimer.Tick += OnDayTimerTick;
        _messageTimer = DispatcherQueue.CreateTimer();
        _messageTimer.Interval = TimeSpan.FromSeconds(3);
        _messageTimer.Tick += (_, _) => { _messageTimer.Stop(); PageInfoBar.IsOpen = false; };
        ActualThemeChanged += OnSavedThemeChanged;
        _isInitialized = true;
#if WINUI_TEST
        InitializeSavedItemsDiagnostics();
#endif
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is SavedItemsNavigationRequest request)
        {
            if (_section != request.Section) _showingNarrowDetail = false;
            _section = request.Section;
        }

        ApplySectionState();
        if (_isPageLoaded)
            _ = LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        CaptureSectionState();
        base.OnNavigatedFrom(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isPageLoaded)
            return;

        _isPageLoaded = true;
        _searchHighlightBackground = _searchHighlightForeground = null;
        ApplyLocalization();
        SyncComboSelection(KindCombo, _historyKindTag);
        SyncComboSelection(FavoriteKindCombo, _favoriteKindTag);
        SyncRadioSelection(HistoryKindTabs, _historyKindTag);
        SyncRadioSelection(FavoriteKindTabs, _favoriteKindTag);
        SavedItemsService.Instance.Changed += OnSavedItemsChanged;
        ApplySectionState();
        UpdateResponsiveLayout();
        _dayTimer.Start();
        await RestoreSectionAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_isPageLoaded)
            return;

        _isPageLoaded = false;
        SavedItemsService.Instance.Changed -= OnSavedItemsChanged;
        _searchTimer.Stop();
        _dayTimer.Stop();
        _messageTimer.Stop();
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        Interlocked.Increment(ref _detailGeneration);
        ReleaseResultViews();
        _activeDetail = null;
        _activeFavoriteDetail = null;
        _pendingOtherResults = [];
    }

    private void OnSavedItemsChanged(object? sender, SavedItemsChangedEventArgs e)
    {
        if (!_isPageLoaded || _savingFavoriteMetadata || HasUnsavedFavoriteChanges)
            return;

        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_isPageLoaded && !_savingFavoriteMetadata && !HasUnsavedFavoriteChanges)
            {
                // Keep live controls for a favorite change to the displayed history
                // query. Expired queries still reload: removing their last favorite
                // can delete them from the store.
                if (_section == SavedItemsSection.History && e.Kind == SavedItemsChangeKind.Favorite &&
                    _activeDetail is { } active && e.QueryId == active.Query.Id &&
                    active.Query.CreatedUtc >= DateTimeOffset.UtcNow.AddDays(-Math.Clamp(SettingsService.Instance.HistoryRetentionDays, 1, 3650)))
                {
                    try { await RefreshFavoriteStatesAsync(); }
                    catch (Exception exception)
                    {
                        System.Diagnostics.Debug.WriteLine($"[SavedItems] Favorite refresh failed: {exception}");
                        ShowError(exception.Message);
                    }
                    return;
                }
                CaptureSectionState();
                await RestoreSectionAsync();
            }
        });
    }

    private async void OnSearchTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (await ConfirmLeaveFavoriteAsync()) await LoadAsync();
        else SavedItemsSearchBox.Text = _appliedSearch;
    }

    private async Task LoadAsync()
    {
        if (!_isInitialized || !_isPageLoaded)
            return;

        var selectedQueryId = (SavedItemsList.SelectedItem as SavedItemsRow)?.QueryId;
        var selectedFavoriteId = (SavedItemsList.SelectedItem as SavedItemsRow)?.FavoriteId;
        var generation = Interlocked.Increment(ref _loadGeneration);
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var cancellationToken = _loadCts.Token;
        _nextCursor = null;
        SetLoading(true);

        try
        {
            ApplySectionState();
            var page = await QueryRowsAsync(null, cancellationToken);
            if (generation != _loadGeneration || cancellationToken.IsCancellationRequested)
                return;

            _appliedSearch = SavedItemsSearchBox.Text ?? string.Empty;
            _restoringSelection = true;
            _items.Clear();
            AddRows(page.Items);
            _restoringSelection = false;
            _lastDataRevision = SavedItemsService.Instance.Revision;
            _nextCursor = page.NextCursor;
            UpdateEmptyState();

            var preserved = _items.FirstOrDefault(row =>
                selectedFavoriteId is { } favoriteId
                    ? row.FavoriteId == favoriteId
                    : selectedQueryId is { } queryId && row.QueryId == queryId);
            if (preserved is not null)
            {
                SavedItemsList.SelectedItem = preserved;
            }
            else
            {
                SavedItemsList.SelectedItem = null;
                ClearDetail();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (generation != _loadGeneration)
                return;

            EmptyState.Visibility = Visibility.Visible;
            EmptyStateText.Text = string.Format(
                L("SavedItemsLoadError", "Unable to load saved items: {0}"),
                exception.Message);
            RetryLoadButton.Visibility = Visibility.Visible;
            EmptyBackButton.Visibility = Visibility.Collapsed;
            ClearSearchButton.Visibility = Visibility.Collapsed;
            EmptyStateIcon.Glyph = "\uE783";
            SavedItemsList.Visibility = Visibility.Collapsed;
            ShowError(exception.Message);
        }
        finally
        {
            if (generation == _loadGeneration)
                SetLoading(false);
        }
    }

    private async Task LoadNextPageAsync()
    {
        if (_nextCursor is not { } cursor || _isLoadingNextPage || _loadCts is null)
            return;

        var generation = _loadGeneration;
        var cancellationToken = _loadCts.Token;
        _isLoadingNextPage = true;
        SetLoading(true);
        try
        {
            var page = await QueryRowsAsync(cursor, cancellationToken);
            if (generation != _loadGeneration || cancellationToken.IsCancellationRequested)
                return;

            AddRows(page.Items.Where(row => !_items.Any(existing => existing.StableId == row.StableId)));
            _nextCursor = page.NextCursor;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (generation == _loadGeneration)
                ShowError(exception.Message);
        }
        finally
        {
            _isLoadingNextPage = false;
            if (generation == _loadGeneration)
                SetLoading(false);
        }
    }

    private async Task<SavedItemsPageResult<SavedItemsRow>> QueryRowsAsync(
        SavedItemsCursor? cursor,
        CancellationToken cancellationToken)
    {
        if (_section == SavedItemsSection.History)
        {
            var (startUtc, endUtc) = GetTimeRange(_timeRangeTag);
            var page = await SavedItemsService.Instance.ListHistoryAsync(
                new HistoryListRequest(
                    SavedItemsSearchBox.Text,
                    ParseSavedQueryKind(_historyKindTag),
                    _providerId,
                    startUtc,
                    endUtc,
                    cursor,
                    PageSize),
                cancellationToken);
            var rows = page.Items.Select(item => new SavedItemsRow(
                item.Id,
                null,
                item.SourceText,
                $"{item.PreviewProviderName} · {item.SourceLanguage} → {item.TargetLanguage} · {KindLabel(item.Kind)} · {ResultCountLabel(item.SuccessResultCount)}",
                item.PreviewText,
                FormatListTime(item.CreatedUtc),
                [],
                item.CreatedUtc,
                string.Empty) { IconGlyph = item.Kind == SavedQueryKind.Ocr ? "\uE8A7" : item.Kind == SavedQueryKind.GrammarCorrection ? "\uE8F2" : "\uE8A5" }).ToArray();
            return new SavedItemsPageResult<SavedItemsRow>(rows, page.NextCursor);
        }

        var favoritesPage = await SavedItemsService.Instance.ListFavoritesAsync(
            new FavoriteListRequest(
                SavedItemsSearchBox.Text,
                ParseFavoriteTargetKind(_favoriteKindTag),
                _appliedTags,
                _pinnedOnly,
                cursor,
                PageSize),
            cancellationToken);
        var favoriteRows = favoritesPage.Items.Select(item =>
        {
            var pin = string.Empty;
            return new SavedItemsRow(
                item.QueryId,
                item.Id,
                item.SourceText,
                $"{pin}{item.ProviderName} · {item.SourceLanguage} → {item.TargetLanguage} · {KindLabel(item.QueryKind)} · {FavoriteTargetLabel(item.TargetKind)} · {ResultCountLabel(item.SuccessResultCount)}",
                item.PreviewText,
                FormatListTime(item.CreatedUtc),
                item.Tags,
                item.CreatedUtc,
                string.Empty) { IconGlyph = item.IsPinned ? "\uE718" : "\uE734" };
        }).ToArray();
        return new SavedItemsPageResult<SavedItemsRow>(favoriteRows, favoritesPage.NextCursor);
    }

    private void AddRows(IEnumerable<SavedItemsRow> rows)
    {
        var previous = _items.LastOrDefault()?.CreatedUtc;
        var groupHistory = _section == SavedItemsSection.History &&
            string.IsNullOrWhiteSpace(SavedItemsSearchBox.Text);
        foreach (var row in rows)
        {
            var groupTitle = groupHistory && (previous is null || SavedItemsPresentation.DateGroup(previous.Value, DateTimeOffset.Now, TimeZoneInfo.Local) != SavedItemsPresentation.DateGroup(row.CreatedUtc, DateTimeOffset.Now, TimeZoneInfo.Local))
                ? GetHistoryGroupTitle(row.CreatedUtc)
                : string.Empty;
            _items.Add(new SavedItemsRow(row.QueryId, row.FavoriteId, row.SourceText,
                row.Metadata, row.PreviewText, FormatListTime(row.CreatedUtc), row.Tags,
                row.CreatedUtc, groupTitle) { IconGlyph = row.IconGlyph });
            previous = row.CreatedUtc;
        }
    }

    private static string GetHistoryGroupTitle(DateTimeOffset createdUtc)
    {
        var key = SavedItemsPresentation.DateGroup(createdUtc, DateTimeOffset.Now, TimeZoneInfo.Local);
        return L(key, key switch
        {
            "SavedItemsToday" => "Today",
            "SavedItemsYesterday" => "Yesterday",
            "SavedItemsLastSevenDays" => "Last 7 days",
            _ => "Earlier"
        });
    }

    private static string ResultCountLabel(int count) => string.Format(L("SavedItemsResultCount", "{0} results"), count);

    private static string FormatListTime(DateTimeOffset created)
    {
        var day = created.LocalDateTime;
        if (day.Date == DateTime.Today) return day.ToString("t");
        if (day.Date == DateTime.Today.AddDays(-1)) return L("SavedItemsYesterday", "Yesterday");
        return day.ToString("d");
    }

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SavedItemsList.Visibility = _items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var filtered = !string.IsNullOrWhiteSpace(SavedItemsSearchBox.Text) || HasActiveFilters;
        ClearSearchButton.Visibility = filtered ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateIcon.Glyph = filtered ? "\uE721" : _section == SavedItemsSection.Favorites ? "\uE734" : "\uE81C";
        EmptyStateText.Text = filtered ? L("SavedItemsNoSearchResults", "No matching records. Try another search or clear your filters.") : _section == SavedItemsSection.History
            ? L("SavedItemsNoHistory", "No completed queries yet.")
            : L("SavedItemsNoFavorites", "No favorites yet.");
        RetryLoadButton.Visibility = Visibility.Collapsed;
        EmptyBackButton.Visibility = filtered ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetLoading(bool isLoading)
    {
        PageLoadingRing.IsActive = isLoading;
        PageLoadingRing.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (!_isInitialized || !_isPageLoaded)
            return;

        if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        }
    }

    private async void OnHistoryKindChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs e)
    {
        if (!_isInitialized || _updatingKindSelectors || HistoryKindTabs.SelectedItem is not SelectorBarItem selected)
            return;

        _historyKindTag = selected.Tag?.ToString() ?? string.Empty;
        SyncComboSelection(KindCombo, _historyKindTag);
        await LoadAsync();
    }

    private async void OnNarrowKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || _updatingKindSelectors || KindCombo.SelectedItem is not ComboBoxItem selected)
            return;

        _historyKindTag = selected.Tag?.ToString() ?? string.Empty;
        SyncRadioSelection(HistoryKindTabs, _historyKindTag);
        await LoadAsync();
    }

    private async void OnFavoriteKindChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs e)
    {
        if (!_isInitialized || _updatingKindSelectors || FavoriteKindTabs.SelectedItem is not SelectorBarItem selected)
            return;

        if (!await ConfirmLeaveFavoriteAsync())
        {
            SyncRadioSelection(FavoriteKindTabs, _favoriteKindTag);
            return;
        }
        _favoriteKindTag = selected.Tag?.ToString() ?? string.Empty;
        SyncComboSelection(FavoriteKindCombo, _favoriteKindTag);
        await LoadAsync();
    }

    private async void OnNarrowFavoriteKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || _updatingKindSelectors || FavoriteKindCombo.SelectedItem is not ComboBoxItem selected)
            return;

        if (!await ConfirmLeaveFavoriteAsync())
        {
            SyncComboSelection(FavoriteKindCombo, _favoriteKindTag);
            return;
        }
        _favoriteKindTag = selected.Tag?.ToString() ?? string.Empty;
        SyncRadioSelection(FavoriteKindTabs, _favoriteKindTag);
        await LoadAsync();
    }

    private void SyncComboSelection(ComboBox comboBox, string tag)
    {
        _updatingKindSelectors = true;
        try
        {
            comboBox.SelectedItem = comboBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString() ?? string.Empty, tag, StringComparison.Ordinal));
        }
        finally
        {
            _updatingKindSelectors = false;
        }
    }

    private void SyncRadioSelection(SelectorBar radioButtons, string tag)
    {
        _updatingKindSelectors = true;
        try
        {
            radioButtons.SelectedItem = radioButtons.Items.OfType<SelectorBarItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString() ?? string.Empty, tag, StringComparison.Ordinal));
        }
        finally
        {
            _updatingKindSelectors = false;
        }
    }

    private void OnFilterButtonClicked(object sender, RoutedEventArgs e)
    {
    }

    private async void OnFilterFlyoutOpened(object sender, object e)
    {
        try
        {
            var options = await SavedItemsService.Instance.GetFilterOptionsAsync();
            HistoryFilterPanel.Visibility = _section == SavedItemsSection.History ? Visibility.Visible : Visibility.Collapsed;
            FavoritesFilterPanel.Visibility = _section == SavedItemsSection.Favorites ? Visibility.Visible : Visibility.Collapsed;
            FilterTitleText.Text = L("SavedItemsFilters", "Filters");

            var selectedProvider = _providerId;
            ProviderCombo.Items.Clear();
            ProviderCombo.Items.Add(new ComboBoxItem
            {
                Content = L("SavedItemsAllProviders", "All providers"),
                Tag = string.Empty
            });
            foreach (var (id, name) in options.Providers)
                ProviderCombo.Items.Add(new ComboBoxItem { Content = name, Tag = id });
            ProviderCombo.SelectedItem = ProviderCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), selectedProvider, StringComparison.Ordinal))
                ?? ProviderCombo.Items[0];
            SelectComboTag(TimeRangeCombo, _timeRangeTag);

            FavoriteTagsFilterList.ItemsSource = options.Tags;
            FavoriteTagsFilterList.SelectedItems.Clear();
            foreach (var tag in options.Tags.Where(tag => _appliedTags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
                FavoriteTagsFilterList.SelectedItems.Add(tag);
            PinnedOnlyToggle.IsOn = _pinnedOnly;
        }
        catch (Exception exception)
        {
            FilterFlyout.Hide();
            ShowError(exception.Message);
        }
    }

    private void OnResetFiltersClicked(object sender, RoutedEventArgs e)
    {
        SelectComboTag(TimeRangeCombo, string.Empty);
        SelectComboTag(ProviderCombo, string.Empty);
        FavoriteTagsFilterList.SelectedItems.Clear();
        PinnedOnlyToggle.IsOn = false;
    }

    private async void OnApplyFiltersClicked(object sender, RoutedEventArgs e)
    {
        FilterFlyout.Hide();
        if (!await ConfirmLeaveFavoriteAsync()) return;
        _timeRangeTag = (TimeRangeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        _providerId = (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        _appliedTags = FavoriteTagsFilterList.SelectedItems.Cast<string>().ToArray();
        _pinnedOnly = PinnedOnlyToggle.IsOn;
        UpdateFilterBadge();
        FilterFlyout.Hide();
        await LoadAsync();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void OnHistoryRailClicked(object sender, RoutedEventArgs e)
    {
        await ShowSectionAsync(SavedItemsSection.History);
    }

    private async void OnFavoritesRailClicked(object sender, RoutedEventArgs e)
    {
        await ShowSectionAsync(SavedItemsSection.Favorites);
    }

    internal async Task ShowSectionAsync(SavedItemsSection section)
    {
        if (_section == section)
            return;
        if (!await ConfirmLeaveFavoriteAsync())
        {
            ApplySectionState();
            return;
        }
        CaptureSectionState();
        _section = section;
        _showingNarrowDetail = false;
        ApplySectionState();
        UpdateResponsiveLayout();
        if (_isPageLoaded)
            await RestoreSectionAsync();
    }

    private void ApplySectionState()
    {
        if (!_isInitialized)
            return;

        PageTitleText.Text = _section == SavedItemsSection.History
            ? L("SavedItemsHistory", "History")
            : L("SavedItemsFavorites", "Favorites");
        AutomationProperties.SetName(PageTitleText, PageTitleText.Text);
        AutomationProperties.SetHelpText(PageTitleText, $"SavedItemsSection:{_section}");
        HistoryRailButton.IsEnabled = true;
        FavoritesRailButton.IsEnabled = true;
        SavedNavigation.SelectedItem = _section == SavedItemsSection.History ? HistoryRailButton : FavoritesRailButton;
        var historyDisabled = _section == SavedItemsSection.History && !SettingsService.Instance.HistoryEnabled;
        HistoryDisabledNotice.IsOpen = historyDisabled;
        HistoryDisabledNotice.Visibility = historyDisabled ? Visibility.Visible : Visibility.Collapsed;
        UpdateFilterBadge();
        UpdateResponsiveLayout();
    }

    private void UpdateFilterBadge()
    {
        var hasActiveFilter = _section == SavedItemsSection.History
            ? !string.IsNullOrEmpty(_timeRangeTag) || !string.IsNullOrEmpty(_providerId)
            : _appliedTags.Count > 0 || _pinnedOnly;
        FilterActiveBadge.Visibility = hasActiveFilter ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout();

    private void UpdateResponsiveLayout()
    {
        if (!_isInitialized)
            return;
#if WINUI_TEST
        AutomationProperties.SetItemStatus(ReturnToTranslationButton, FormattableString.Invariant($"PageWidth={ActualWidth:F1};Dpi={XamlRoot?.RasterizationScale ?? 1:F2}"));
#endif

        var narrow = ActualWidth < NarrowBreakpoint;
        var collapseRail = ActualWidth < 640;
        SavedNavigation.PaneDisplayMode = collapseRail ? NavigationViewPaneDisplayMode.LeftMinimal : NavigationViewPaneDisplayMode.LeftCompact;
        SavedNavigation.IsPaneToggleButtonVisible = collapseRail;
        ReturnToTranslationButton.Margin = new Thickness(collapseRail ? 56 : 16, 12, 16, 0);
        RootGrid.Padding = new Thickness(narrow ? 16 : 24);
        RootGrid.ColumnSpacing = narrow ? 0 : 16;
        if (narrow)
        {
            ListColumn.Width = _showingNarrowDetail ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            DetailColumn.Width = _showingNarrowDetail ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            ListPane.Visibility = _showingNarrowDetail ? Visibility.Collapsed : Visibility.Visible;
            DetailScroll.Visibility = _showingNarrowDetail ? Visibility.Visible : Visibility.Collapsed;
            DetailBackButton.Visibility = _showingNarrowDetail ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            ListColumn.Width = new GridLength(360);
            DetailColumn.Width = new GridLength(1, GridUnitType.Star);
            ListPane.Visibility = Visibility.Visible;
            DetailScroll.Visibility = Visibility.Visible;
            DetailBackButton.Visibility = Visibility.Collapsed;
        }

        var categories = _section == SavedItemsSection.History ? HistoryKindTabs : FavoriteKindTabs;
        var showNarrowSelector = ListPane.ActualWidth < RequiredSelectorWidth(categories);
        UpdateResultsLayout();
        HistoryKindTabs.Visibility = _section == SavedItemsSection.History && !showNarrowSelector
            ? Visibility.Visible : Visibility.Collapsed;
        KindCombo.Visibility = _section == SavedItemsSection.History && showNarrowSelector
            ? Visibility.Visible : Visibility.Collapsed;
        FavoriteKindTabs.Visibility = _section == SavedItemsSection.Favorites && !showNarrowSelector
            ? Visibility.Visible : Visibility.Collapsed;
        FavoriteKindCombo.Visibility = _section == SavedItemsSection.Favorites && showNarrowSelector
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnListContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is SavedItemsRow row)
        {
            AutomationProperties.SetName(args.ItemContainer, $"{row.SourceText}. {row.Metadata}. {row.PreviewText}");
            var container = args.ItemContainer;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isPageLoaded) RefreshListHighlights(container);
            });
        }
        if (!args.InRecycleQueue && args.ItemIndex >= _items.Count - 5 && !_nextPageQueued)
        {
            // SQLite can complete synchronously. Never mutate ItemsSource while
            // WinUI is realizing/recycling containers inside a layout pass.
            _nextPageQueued = true;
            var generation = _loadGeneration;
            DispatcherQueue.TryEnqueue(async () =>
            {
                _nextPageQueued = false;
                if (_isPageLoaded && generation == _loadGeneration)
                    await LoadNextPageAsync();
            });
        }
    }

    private void OnSavedItemsListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Enter or VirtualKey.Space && SavedItemsList.SelectedItem is not null)
        {
            _showingNarrowDetail = true;
            UpdateResponsiveLayout();
            e.Handled = true;
        }
        else if (e.Key is VirtualKey.Escape or VirtualKey.GoBack && _showingNarrowDetail)
        {
            ShowListPane();
            e.Handled = true;
        }
    }

    private async void OnItemSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringSelection || SavedItemsList.SelectedItem is not SavedItemsRow row)
            return;
        if (HasUnsavedFavoriteChanges && _displayedRow?.StableId != row.StableId)
        {
            _restoringSelection = true;
            SavedItemsList.SelectedItem = _displayedRow;
            _restoringSelection = false;
            if (!await ConfirmLeaveFavoriteAsync()) return;
            _restoringSelection = true;
            SavedItemsList.SelectedItem = row;
            _restoringSelection = false;
        }
        _displayedRow = row;
        foreach (var item in _items)
            item.IsSelected = ReferenceEquals(item, row);
        var generation = Interlocked.Increment(ref _detailGeneration);
        try
        {
            SavedQueryDetail? detail;
            FavoriteDetail? favoriteDetail = null;
            if (_section == SavedItemsSection.Favorites && row.FavoriteId is { } favoriteId)
            {
                favoriteDetail = await SavedItemsService.Instance.GetFavoriteDetailAsync(favoriteId);
                detail = favoriteDetail?.QueryDetail;
            }
            else
            {
                detail = await SavedItemsService.Instance.GetQueryDetailAsync(row.QueryId);
            }

            if (generation != _detailGeneration ||
                SavedItemsList.SelectedItem is not SavedItemsRow selected ||
                selected.StableId != row.StableId ||
                detail is null)
                return;

            var favoriteStates = await SavedItemsService.Instance.GetFavoriteStatesAsync(row.QueryId);
            if (generation != _detailGeneration)
                return;

            _activeDetail = detail;
            _activeFavoriteDetail = favoriteDetail;
            _selectedFavoriteId = favoriteDetail?.Favorite.Id;
            _favoriteStates = favoriteStates;
            PopulateDetail(row, detail, favoriteDetail);
            DetailSourceText.FontSize = 14 * AppearanceService.FontScale;
            _showingNarrowDetail = true;
            UpdateResponsiveLayout();
        }
        catch (Exception exception)
        {
            if (generation == _detailGeneration)
                ShowError(exception.Message);
        }
    }

    private void PopulateDetail(
        SavedItemsRow row,
        SavedQueryDetail detail,
        FavoriteDetail? favoriteDetail)
    {
        ReleaseResultViews();
        NoSelectionPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        OtherResultsExpander.IsExpanded = false;
        CompareResultsButton.IsChecked = false;
        _resultChoices.Clear();
        _updatingResultSelector = true;
        ResultProviderTabs.Items.Clear();
        ResultProviderCombo.ItemsSource = null;
        _updatingResultSelector = false;
        FavoriteEditorPanel.Visibility = Visibility.Collapsed;
        FavoriteSummaryPanel.Visibility = Visibility.Visible;
        DetailTitleText.Text = L("SavedItemsSource", "Source text");
        DetailSourceText.Text = detail.Query.SourceText;
        DetailMetadataText.Text =
            $"{detail.Query.SourceLanguage} → {detail.Query.TargetLanguage} · {KindLabel(detail.Query.Kind)} · {detail.Query.CreatedUtc.LocalDateTime:F} · {L("SavedItemsSource" + detail.Query.SourceKind, detail.Query.SourceKind.ToString())}";

        CopySourceButton.Visibility = Visibility.Visible;
        RerunQueryButton.Visibility = Visibility.Visible;
        DeleteHistoryButton.Visibility = _section == SavedItemsSection.History ? Visibility.Visible : Visibility.Collapsed;
        DetailMoreButton.Visibility = DeleteHistoryButton.Visibility;
        ToggleQueryFavoriteButton.Visibility = _section == SavedItemsSection.History ? Visibility.Visible : Visibility.Collapsed;
        ToggleQueryFavoriteButton.Label = _favoriteStates.IsQueryFavorited
            ? L("SavedItemsRemoveFavorite", "Remove favorite")
            : L("SavedItemsAddFavorite", "Add to favorites");
        RemoveFavoriteButton.Visibility = favoriteDetail is null ? Visibility.Collapsed : Visibility.Visible;

        FavoriteMetadataPanel.Visibility = favoriteDetail is null ? Visibility.Collapsed : Visibility.Visible;
        if (favoriteDetail is not null)
        {
            _updatingFavoriteMetadata = true;
            try
            {
                PinFavoriteButton.IsChecked = favoriteDetail.Favorite.IsPinned;
                FavoriteNoteBox.Text = favoriteDetail.Favorite.Note;
                _savedFavoriteNote = favoriteDetail.Favorite.Note;
                _favoriteTags.Clear();
                foreach (var tag in favoriteDetail.Favorite.Tags)
                    _favoriteTags.Add(tag);
                _savedFavoriteTags = favoriteDetail.Favorite.Tags.ToArray();
                FavoriteNoteSummary.Text = string.IsNullOrWhiteSpace(_savedFavoriteNote) ? L("SavedItemsNoNote", "No note") : _savedFavoriteNote;
                FavoriteTagsSummary.Text = string.Join(" · ", _savedFavoriteTags);
                FavoriteTagsBox.Text = string.Empty;
            }
            finally
            {
                _updatingFavoriteMetadata = false;
            }
        }

        if (favoriteDetail?.Favorite.TargetKind == FavoriteTargetKind.Result &&
            favoriteDetail.Favorite.ResultId is { } favoriteResultId)
        {
            ResultSelectionPanel.Visibility = Visibility.Collapsed;
            var targetResult = detail.Results.FirstOrDefault(result => result.Id == favoriteResultId);
            if (targetResult is not null)
                AddSavedResult(targetResult, DetailResults, _detailResultControls);
            var otherResults = detail.Results.Where(result => result.Id != favoriteResultId).ToArray();
            OtherResultsExpander.Header = string.Format(L("SavedItemsOtherResultsCount", "Other results from this query ({0})"), otherResults.Length);
            OtherResultsExpander.Visibility = otherResults.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            _pendingOtherResults = otherResults;
            UpdateResultsLayout();
            return;
        }

        _pendingOtherResults = [];
        OtherResultsExpander.Visibility = Visibility.Collapsed;
        ResultSelectionPanel.Visibility = detail.Results.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _resultChoices.Clear();
        _resultChoices.Add(new ResultChoice(null, string.Format(
            L("SavedItemsAllResults", "All results ({0})"),
            detail.Results.Count)));
        _resultChoices.AddRange(detail.Results.Select(result => new ResultChoice(result.Id, result.ProviderName)));
        _updatingResultSelector = true;
        try
        {
            foreach (var choice in _resultChoices)
                ResultProviderTabs.Items.Add(new SelectorBarItem { Text = choice.DisplayName, Tag = choice });
            ResultSelector.ItemsSource = null;
            ResultSelector.ItemsSource = _resultChoices;
            ResultSelector.SelectionMode = ListViewSelectionMode.Single;
            ResultSelector.SelectedItem = _resultChoices[0];
            CompareResultsButton.IsChecked = false;
            CompareResultsButton.IsEnabled = detail.Results.Count >= 2;
        }
        finally
        {
            _updatingResultSelector = false;
        }
        RenderSelectedResults();
    }

    private void AddSavedResult(
        SavedQueryResultDetail result,
        ItemsControl panel,
        IList<IServiceResultView> controls)
    {
        ServiceQueryResult? serviceResult;
        try
        {
            serviceResult = new ServiceQueryResult
            {
                ServiceId = result.ProviderId,
                ServiceDisplayName = result.ProviderName,
                CurrentMode = result.ContentType == SavedResultContentType.GrammarCorrection
                    ? QueryMode.GrammarCorrection
                    : QueryMode.Translation
            };
            if (result.ContentType == SavedResultContentType.GrammarCorrection)
            {
                serviceResult.GrammarResult = JsonSerializer.Deserialize<GrammarCorrectionResult>(result.PayloadJson)
                    ?? throw new InvalidDataException("The saved grammar result payload is empty.");
            }
            else
            {
                serviceResult.Result = JsonSerializer.Deserialize<TranslationResult>(result.PayloadJson)
                    ?? throw new InvalidDataException("The saved translation result payload is empty.");
            }
            serviceResult.MarkQueried();
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or NotSupportedException)
        {
            CrashDiagnostics.Log(
                $"[SavedItemsPage] Unable to deserialize query {_activeDetail?.Query.Id}, result {result.Id}: {exception.Message}");
            panel.Items.Add(CreateUnreadableResultCard(result.ProviderName));
            return;
        }

        var view = ServiceResultViewHost.Add(
            serviceResult,
            controls,
            panel,
            OnSavedResultCollapseToggled,
            OnSavedResultQueryRequested,
            this,
            foundryLocalStartRequested: null,
            favoriteRequested: OnSavedResultFavoriteRequested,
            isSavedItemView: true,
            copyCompleted: OnSavedResultCopied);
        _resultIds[serviceResult] = result.Id;
#if WINUI_TEST
        ObservedResults.Add(new WeakReference<IServiceResultView>(view));
#endif
        view.SetFavoriteState(true, _favoriteStates.FavoritedResultIds.Contains(result.Id));
    }

    private Border CreateUnreadableResultCard(string providerName)
    {
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(new TextBlock
        {
            Text = providerName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = L("SavedItemsUnreadableResult", "This saved result cannot be read."),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
        });
        return new Border
        {
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = content
        };
    }

    private void ReleaseResultViews()
    {
        ServiceResultViewHost.Release(
            _detailResultControls,
            DetailResults,
            OnSavedResultCollapseToggled,
            OnSavedResultQueryRequested,
            foundryLocalStartRequested: null,
            favoriteRequested: OnSavedResultFavoriteRequested,
            copyCompleted: OnSavedResultCopied);
        ServiceResultViewHost.Release(
            _otherResultControls,
            OtherDetailResults,
            OnSavedResultCollapseToggled,
            OnSavedResultQueryRequested,
            foundryLocalStartRequested: null,
            favoriteRequested: OnSavedResultFavoriteRequested,
            copyCompleted: OnSavedResultCopied);
        _resultIds.Clear();
    }

    private void OnSavedResultCollapseToggled(object? sender, ServiceQueryResult result)
    {
    }

    private void OnSavedResultCopied(object? sender, EventArgs e)
    {
        if (e is ResultCopyEventArgs { Error: { } error })
            ShowError(string.Format(L("SavedItemsCopyError", "Unable to copy: {0}"), error.Message));
        else
            ShowInfo(L("SavedItemsResultCopied", "Result copied."));
    }

    private void OnSavedResultQueryRequested(object? sender, ServiceQueryResult result)
    {
    }

    private async void OnSavedResultFavoriteRequested(object? sender, ServiceQueryResult result)
    {
        if (_activeDetail is null || !_resultIds.TryGetValue(result, out var resultId))
        {
            System.Diagnostics.Debug.WriteLine($"[SavedItems] Result favorite ignored: activeDetail={_activeDetail is not null}, provider={result.ServiceId}, generation={_detailGeneration}");
            return;
        }

        var queryId = _activeDetail.Query.Id;
        if (!_pendingResultFavorites.Add(resultId)) return;
        System.Diagnostics.Debug.WriteLine($"[SavedItems] Result favorite requested: query={queryId}, result={resultId}");

        var control = _detailResultControls.Concat(_otherResultControls)
            .FirstOrDefault(item => ReferenceEquals(item.ServiceResult, result));
        if (control?.Element is Control element)
            element.IsEnabled = false;
        try
        {
            await SavedItemsService.Instance.ToggleStoredResultFavoriteAsync(queryId, resultId);
            System.Diagnostics.Debug.WriteLine($"[SavedItems] Result favorite persisted: query={queryId}, result={resultId}");
            if (_activeDetail?.Query.Id == queryId)
                await RefreshFavoriteStatesAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"[SavedItems] Result favorite failed: query={queryId}, result={resultId}: {exception}");
            ShowError(exception.Message);
        }
        finally
        {
            _pendingResultFavorites.Remove(resultId);
            if (control?.Element is Control resultElement)
                resultElement.IsEnabled = true;
        }
    }

    private async Task RefreshFavoriteStatesAsync()
    {
        if (!_isPageLoaded || _activeDetail is not { } detail) return;
        var detailGeneration = _detailGeneration;
        var stateGeneration = ++_favoriteStateGeneration;
        var states = await SavedItemsService.Instance.GetFavoriteStatesAsync(detail.Query.Id);
        if (!_isPageLoaded || detailGeneration != _detailGeneration ||
            stateGeneration != _favoriteStateGeneration || !ReferenceEquals(detail, _activeDetail)) return;
        _favoriteStates = states;
        ToggleQueryFavoriteButton.Label = states.IsQueryFavorited
            ? L("SavedItemsRemoveFavorite", "Remove favorite")
            : L("SavedItemsAddFavorite", "Add to favorites");
        foreach (var view in _detailResultControls.Concat(_otherResultControls))
            if (view.ServiceResult is { } result && _resultIds.TryGetValue(result, out var id))
                view.SetFavoriteState(true, states.FavoritedResultIds.Contains(id));
    }

    private void OnCompareResultsClicked(object sender, RoutedEventArgs e)
    {
        if (_activeDetail is null)
            return;

        _updatingResultSelector = true;
        try
        {
            if (CompareResultsButton.IsChecked == true)
            {
                ResultSelector.SelectedItem = null;
                ResultSelector.SelectionMode = ListViewSelectionMode.Multiple;
                ResultSelector.ItemsSource = _resultChoices.Where(choice => choice.ResultId is not null).ToArray();
                ResultSelector.SelectedItems.Clear();
                foreach (var choice in _resultChoices.Where(choice => choice.ResultId is not null).Take(2))
                    ResultSelector.SelectedItems.Add(choice);
            }
            else
            {
                if (ResultSelector.SelectionMode == ListViewSelectionMode.Multiple)
                    ResultSelector.SelectedItems.Clear();
                ResultSelector.SelectionMode = ListViewSelectionMode.Single;
                ResultSelector.ItemsSource = _resultChoices;
                ResultSelector.SelectedItem = _resultChoices.FirstOrDefault();
            }
        }
        finally
        {
            _updatingResultSelector = false;
        }
        UpdateResultChoiceAvailability();
        RenderSelectedResults();
    }

    private void OnResultSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingResultSelector)
            return;

        if (CompareResultsButton.IsChecked == true)
        {
            _updatingResultSelector = true;
            try
            {
                foreach (var allChoice in ResultSelector.SelectedItems.Cast<ResultChoice>()
                             .Where(choice => choice.ResultId is null).ToArray())
                    ResultSelector.SelectedItems.Remove(allChoice);
                while (ResultSelector.SelectedItems.Count > 2)
                    ResultSelector.SelectedItems.RemoveAt(ResultSelector.SelectedItems.Count - 1);
            }
            finally
            {
                _updatingResultSelector = false;
            }
        }
        UpdateResultChoiceAvailability();
        RenderSelectedResults();
    }

    private void UpdateResultChoiceAvailability()
    {
        var atLimit = CompareResultsButton.IsChecked == true && ResultSelector.SelectedItems.Count >= 2;
        foreach (var choice in _resultChoices)
        {
            if (ResultSelector.ContainerFromItem(choice) is ListViewItem container)
            {
                container.IsEnabled = choice.ResultId is not null &&
                    (!atLimit || ResultSelector.SelectedItems.Contains(choice));
            }
        }
    }

    private void RenderSelectedResults()
    {
        if (_activeDetail is null)
            return;

        foreach (var view in _detailResultControls)
        {
            if (view.ServiceResult is { } result)
                _resultIds.Remove(result);
        }

        ServiceResultViewHost.Release(
            _detailResultControls,
            DetailResults,
            OnSavedResultCollapseToggled,
            OnSavedResultQueryRequested,
            foundryLocalStartRequested: null,
            favoriteRequested: OnSavedResultFavoriteRequested,
            copyCompleted: OnSavedResultCopied);
        foreach (var result in _activeDetail.Results.Where(ShouldRenderResult))
            AddSavedResult(result, DetailResults, _detailResultControls);
        UpdateResultsLayout();
        DispatcherQueue.TryEnqueue(UpdateResultsLayout);
    }

    private bool ShouldRenderResult(SavedQueryResultDetail result)
    {
        if (CompareResultsButton.IsChecked == true)
        {
            return ResultSelector.SelectedItems.Cast<ResultChoice>()
                .Any(choice => choice.ResultId == result.Id);
        }

        return ResultSelector.SelectedItem is not ResultChoice choice ||
            choice.ResultId is null ||
            choice.ResultId == result.Id;
    }

    private async void OnCopySourceClicked(object sender, RoutedEventArgs e)
    {
        if (_activeDetail is null)
            return;

        try
        {
            ClipboardService.SetText(_activeDetail.Query.SourceText);
            ShowInfo(L("SavedItemsSourceCopied", "Source copied."));
        }
        catch (Exception exception) { ShowError(string.Format(L("SavedItemsCopyError", "Unable to copy: {0}"), exception.Message)); }
        await Task.CompletedTask;
    }

    private async void OnRerunQueryClicked(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmLeaveFavoriteAsync() || _activeDetail is null)
            return;

        var request = new SavedQueryRerunRequest(
            _activeDetail.Query.SourceText,
            _activeDetail.Query.SourceLanguage,
            _activeDetail.Query.TargetLanguage,
            _activeDetail.Query.Kind);
        NavigateToMainPage(mainPage => mainPage.RerunSavedQuery(request));
    }

    private async void OnToggleQueryFavoriteClicked(object sender, RoutedEventArgs e)
    {
        if (_activeDetail is null)
            return;

        ToggleQueryFavoriteButton.IsEnabled = false;
        var queryId = _activeDetail.Query.Id;
        try
        {
            await SavedItemsService.Instance.ToggleStoredQueryFavoriteAsync(queryId);
            if (_activeDetail?.Query.Id == queryId) await RefreshFavoriteStatesAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            ToggleQueryFavoriteButton.IsEnabled = true;
        }
    }

    private async void OnPinFavoriteClicked(object sender, RoutedEventArgs e)
    {
        if (_updatingFavoriteMetadata || _selectedFavoriteId is not { } favoriteId)
            return;

        PinFavoriteButton.IsEnabled = false;
        try
        {
            await SavedItemsService.Instance.SetFavoritePinnedAsync(
                favoriteId,
                PinFavoriteButton.IsChecked == true);
            // Pinning is immediate; do not discard an open note/tag draft.
            if (!HasUnsavedFavoriteChanges) await LoadAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            PinFavoriteButton.IsEnabled = true;
        }
    }

    private void OnFavoriteTagSubmitted(
        AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (TryReplaceFavoriteTags(args.QueryText))
            sender.Text = string.Empty;
    }

    private void OnRemoveFavoriteTagClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
            _favoriteTags.Remove(tag);
    }

    private bool TryReplaceFavoriteTags(string input)
    {
        var candidates = input
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var merged = _favoriteTags
            .Concat(candidates)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (merged.Length > 20 ||
            merged.Any(static tag => System.Globalization.StringInfo.ParseCombiningCharacters(tag).Length > 40))
        {
            ShowError(L(
                "SavedItemsTagLimits",
                "Use no more than 20 tags, with up to 40 characters per tag."));
            return false;
        }

        _favoriteTags.Clear();
        foreach (var tag in merged)
            _favoriteTags.Add(tag);
        return true;
    }

    private async void OnSaveFavoriteMetadataClicked(object sender, RoutedEventArgs e)
    {
        SaveFavoriteMetadataButton.IsEnabled = false;
        try
        {
            if (await SaveFavoriteChangesAsync())
                await LoadAsync();
        }
        finally { SaveFavoriteMetadataButton.IsEnabled = true; }
    }

    private void OnCancelFavoriteMetadataClicked(object sender, RoutedEventArgs e)
    {
        _updatingFavoriteMetadata = true;
        try
        {
            FavoriteEditorPanel.Visibility = Visibility.Collapsed;
            FavoriteSummaryPanel.Visibility = Visibility.Visible;
            FavoriteNoteBox.Text = _savedFavoriteNote;
            FavoriteTagsBox.Text = string.Empty;
            _favoriteTags.Clear();
            foreach (var tag in _savedFavoriteTags)
                _favoriteTags.Add(tag);
        }
        finally
        {
            _updatingFavoriteMetadata = false;
        }
    }

    private async void OnRemoveFavoriteClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedFavoriteId is not { } favoriteId)
            return;

        if (!await ConfirmAsync(
                L("SavedItemsRemoveFavorite", "Remove favorite"),
                L("SavedItemsRemoveFavoriteConfirm", "Remove this favorite?"),
                L("SavedItemsRemoveFavorite", "Remove favorite")))
            return;

        try
        {
            await SavedItemsService.Instance.RemoveFavoriteAsync(favoriteId);
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private async void OnDeleteHistoryClicked(object sender, RoutedEventArgs e)
    {
        if (SavedItemsList.SelectedItem is not SavedItemsRow row)
            return;

        if (!await ConfirmAsync(
                L("SavedItemsDeleteHistory", "Delete from history"),
                L("SavedItemsDeleteHistoryConfirm", "Delete this query from history? Favorites will not be deleted."),
                L("SavedItemsDeleteHistory", "Delete from history")))
            return;

        try
        {
            await SavedItemsService.Instance.DeleteHistoryAsync(row.QueryId);
            await LoadAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string primaryText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = primaryText,
            CloseButtonText = L("Cancel", "Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private async void OnBackClicked(object sender, RoutedEventArgs e)
    {
        if (_showingNarrowDetail)
        {
            ShowListPane();
            return;
        }

        if (await ConfirmLeaveFavoriteAsync()) NavigateToMainPage();
    }

    private void OnDetailBackClicked(object sender, RoutedEventArgs e) => ShowListPane();

    private async void OnReturnToTranslationClicked(object sender, RoutedEventArgs e)
    {
        if (await ConfirmLeaveFavoriteAsync()) NavigateToMainPage();
    }

    private void ShowListPane()
    {
        _showingNarrowDetail = false;
        UpdateResponsiveLayout();
        if (SavedItemsList.SelectedItem is { } selected)
        {
            SavedItemsList.ScrollIntoView(selected);
            (SavedItemsList.ContainerFromItem(selected) as Control)?.Focus(FocusState.Keyboard);
        }
        else SavedItemsList.Focus(FocusState.Keyboard);
    }

    private void NavigateToMainPage(Action<MainPage>? action = null)
    {
        var frame = Frame;
        var mainEntry = frame.BackStack.LastOrDefault(entry => entry.SourcePageType == typeof(MainPage));
        if (mainEntry is not null)
        {
            while (frame.BackStack.Count > 0 && frame.BackStack[^1] != mainEntry)
                frame.BackStack.RemoveAt(frame.BackStack.Count - 1);
            frame.GoBack();
        }
        if (frame.Content is not MainPage)
            frame.Navigate(typeof(MainPage));

        if (action is not null)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (frame.Content is MainPage mainPage)
                    action(mainPage);
            });
        }
    }

    private void ClearDetail()
    {
        Interlocked.Increment(ref _detailGeneration);
        ReleaseResultViews();
        _activeDetail = null;
        NoSelectionPanel.Visibility = Visibility.Visible;
        DetailPanel.Visibility = Visibility.Collapsed;
        _activeFavoriteDetail = null;
        _pendingOtherResults = [];
        _selectedFavoriteId = null;
        _displayedRow = null;
        _showingNarrowDetail = false;
        UpdateResponsiveLayout();
        DetailTitleText.Text = L("SavedItemsSelectQuery", "Select a saved query");
        DetailSourceText.Text = string.Empty;
        DetailMetadataText.Text = string.Empty;
        CopySourceButton.Visibility = Visibility.Collapsed;
        RerunQueryButton.Visibility = Visibility.Collapsed;
        DeleteHistoryButton.Visibility = Visibility.Collapsed;
        DetailMoreButton.Visibility = Visibility.Collapsed;
        ToggleQueryFavoriteButton.Visibility = Visibility.Collapsed;
        RemoveFavoriteButton.Visibility = Visibility.Collapsed;
        FavoriteMetadataPanel.Visibility = Visibility.Collapsed;
        _favoriteTags.Clear();
        ResultSelectionPanel.Visibility = Visibility.Collapsed;
        OtherResultsExpander.Visibility = Visibility.Collapsed;
    }

    private void ShowInfo(string message)
    {
        _messageTimer.Stop();
        PageInfoBar.Severity = InfoBarSeverity.Success;
        PageInfoBar.Message = message;
        PageInfoBar.IsOpen = true;
        AnnounceMessage(message);
        _messageTimer.Start();
    }

    private void ShowError(string message)
    {
        _messageTimer.Stop();
        PageInfoBar.Severity = InfoBarSeverity.Error;
        PageInfoBar.Message = message;
        PageInfoBar.IsOpen = true;
        AnnounceMessage(message);
    }

    private static SavedQueryKind? ParseSavedQueryKind(string tag) => tag switch
    {
        "translation" => SavedQueryKind.Translation,
        "grammar" => SavedQueryKind.GrammarCorrection,
        "ocr" => SavedQueryKind.Ocr,
        _ => null
    };

    private static FavoriteTargetKind? ParseFavoriteTargetKind(string tag) => tag switch
    {
        "query" => FavoriteTargetKind.Query,
        "result" => FavoriteTargetKind.Result,
        _ => null
    };

    private static void SelectComboTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal))
            ?? comboBox.Items.FirstOrDefault();
    }

    private static (DateTimeOffset? StartUtc, DateTimeOffset? EndUtc) GetTimeRange(string range)
    {
        if (string.IsNullOrEmpty(range))
            return (null, null);

        var today = DateTime.Today;
        var start = range switch
        {
            "today" => today,
            "week" => today.AddDays(-6),
            "month" => today.AddDays(-29),
            _ => today
        };
        return (LocalDateBoundary(start), LocalDateBoundary(today.AddDays(1)));
    }

    private static DateTimeOffset LocalDateBoundary(DateTime localDate)
        => new DateTimeOffset(localDate, TimeZoneInfo.Local.GetUtcOffset(localDate)).ToUniversalTime();

    private static string KindLabel(SavedQueryKind kind) => kind switch
    {
        SavedQueryKind.Translation => L("SavedItemsTranslationKind", "Translation / Dictionary"),
        SavedQueryKind.GrammarCorrection => L("SavedItemsGrammarKind", "Grammar correction"),
        SavedQueryKind.Ocr => L("SavedItemsOcrKind", "OCR"),
        _ => kind.ToString()
    };

    private static string FavoriteTargetLabel(FavoriteTargetKind kind) => kind == FavoriteTargetKind.Query
        ? L("SavedItemsQueryFavorites", "Whole query")
        : L("SavedItemsResultFavorites", "Individual result");

    private static string L(string key, string fallback)
        => LocalizationService.Instance.GetStringOrDefault(key, fallback);

    private void ApplyLocalization()
    {
        ReturnToTranslationText.Text = L("SavedItemsBackToTranslation", "Go to translation");
        BackButton.Content = L("SavedItemsBackToTranslation", "Go to translation");
        HistoryRailButton.Content = L("SavedItemsHistory", "History");
        FavoritesRailButton.Content = L("SavedItemsFavorites", "Favorites");
        SettingsRailButton.Content = L("Settings", "Settings");
        HistoryDisabledNotice.Title = L("SavedItemsHistoryDisabledTitle", "History is turned off");
        HistoryDisabledNotice.Message = string.Format(
            L("SavedItemsHistoryDisabledMessage", "To save future queries, go to {0} → {1} → {2} and turn on “{3}”. Existing records are kept."),
            L("Settings", "Settings"), L("SettingsTab_General", "General"),
            L("HistoryPrivacyTitle", "History & privacy"), L("HistoryEnabledLabel", "Save query history"));
        NoSelectionTitle.Text = L("SavedItemsSelectQuery", "Select a saved query");
        NoSelectionDescription.Text = L("SavedItemsSelectDescription", "Choose a record on the left to read its source and results.");
        ClearSearchButton.Content = L("SavedItemsClearSearch", "Clear search and filters");
        EditFavoriteButton.Content = L("SavedItemsEdit", "Edit note and tags");
        var back = L("Back", "Back");
        ToolTipService.SetToolTip(BackButton, BackButton.Content);
        AutomationProperties.SetName(BackButton, BackButton.Content?.ToString() ?? back);
        DetailBackText.Text = L("SavedItemsBackToList", "Back to list");
        ToolTipService.SetToolTip(HistoryRailButton, L("SavedItemsHistory", "History"));
        AutomationProperties.SetName(HistoryRailButton, L("SavedItemsHistory", "History"));
        ToolTipService.SetToolTip(FavoritesRailButton, L("SavedItemsFavorites", "Favorites"));
        AutomationProperties.SetName(FavoritesRailButton, L("SavedItemsFavorites", "Favorites"));
        SavedItemsSearchBox.PlaceholderText = L("SavedItemsSearchPlaceholder", "Search source, result, or provider");

        SetKindLabels(HistoryKindTabs.Items, KindCombo.Items);
        FavoriteKindTabs.Items[0].Text = L("SavedItemsAllKinds", "All");
        FavoriteKindTabs.Items[1].Text = L("SavedItemsQueryFavorites", "Whole queries");
        FavoriteKindTabs.Items[2].Text = L("SavedItemsResultFavorites", "Individual results");
        ((ComboBoxItem)FavoriteKindCombo.Items[0]).Content = L("SavedItemsAllKinds", "All");
        ((ComboBoxItem)FavoriteKindCombo.Items[1]).Content = L("SavedItemsQueryFavorites", "Whole queries");
        ((ComboBoxItem)FavoriteKindCombo.Items[2]).Content = L("SavedItemsResultFavorites", "Individual results");

        ((ComboBoxItem)TimeRangeCombo.Items[0]).Content = L("SavedItemsAnyTime", "Any time");
        ((ComboBoxItem)TimeRangeCombo.Items[1]).Content = L("SavedItemsToday", "Today");
        ((ComboBoxItem)TimeRangeCombo.Items[2]).Content = L("SavedItemsLastSevenDays", "Last 7 days");
        ((ComboBoxItem)TimeRangeCombo.Items[3]).Content = L("SavedItemsLastThirtyDays", "Last 30 days");
        TimeRangeCombo.Header = L("SavedItemsTimeRange", "Time range");
        ProviderCombo.Header = L("SavedItemsProvider", "Provider");
        FavoriteTagsFilterLabel.Text = L("SavedItemsTags", "Tags");
        PinnedOnlyToggle.Header = L("SavedItemsPinnedOnly", "Pinned only");
        ResetFiltersButton.Content = L("SavedItemsReset", "Reset");
        ApplyFiltersButton.Content = L("SavedItemsApply", "Apply");
        ToolTipService.SetToolTip(SavedItemsFilterButton, L("SavedItemsFilters", "Filters"));
        AutomationProperties.SetName(SavedItemsFilterButton, L("SavedItemsFilters", "Filters"));

        CopySourceButton.Label = L("SavedItemsCopySource", "Copy source");
        DeleteHistoryButton.Text = L("SavedItemsDeleteHistory", "Delete from history");
        ToggleQueryFavoriteButton.Label = L("SavedItemsAddFavorite", "Add to favorites");
        RerunQueryButton.Label = L("SavedItemsRerun", "Translate again");
        RemoveFavoriteButton.Label = L("SavedItemsRemoveFavorite", "Remove favorite");
        PinFavoriteButton.Content = L("SavedItemsPinFavorite", "Pin favorite");
        FavoriteNoteBox.Header = L("SavedItemsNote", "Note");
        FavoriteTagsHeader.Text = L("SavedItemsTags", "Tags");
        FavoriteTagsBox.Header = null;
        FavoriteTagsBox.PlaceholderText = L("SavedItemsTagsPlaceholder", "Separate tags with commas");
        var more = L("SavedItemsMore", "More");
        ToolTipService.SetToolTip(DetailMoreButton, more);
        AutomationProperties.SetName(DetailMoreButton, more);
        SaveFavoriteMetadataButton.Content = L("SavedItemsSave", "Save");
        CancelFavoriteMetadataButton.Content = L("Cancel", "Cancel changes");
        CompareResultsButton.Content = L("SavedItemsCompare", "Compare");
        OtherResultsExpander.Header = L("SavedItemsOtherResults", "Other results");
        RetryLoadButton.Content = L("SavedItemsRetry", "Retry");
        EmptyBackButton.Content = L("SavedItemsBackToTranslation", "Go to translation");
    }

    private static void SetKindLabels(IList<SelectorBarItem> radioItems, IList<object> comboItems)
    {
        var labels = new[]
        {
            L("SavedItemsAllKinds", "All"),
            L("SavedItemsTranslationKind", "Translation / Dictionary"),
            L("SavedItemsGrammarKind", "Grammar correction"),
            L("SavedItemsOcrKind", "OCR")
        };
        for (var index = 0; index < labels.Length; index++)
        {
            radioItems[index].Text = labels[index];
            ((ComboBoxItem)comboItems[index]).Content = labels[index];
        }
    }

    private sealed record SavedItemsRow(
        Guid QueryId,
        Guid? FavoriteId,
        string SourceText,
        string Metadata,
        string PreviewText,
        string TimeText,
        IReadOnlyList<string> Tags,
        DateTimeOffset CreatedUtc,
        string GroupTitle) : System.ComponentModel.INotifyPropertyChanged
    {
        public Guid StableId => FavoriteId ?? QueryId;
        public string IconGlyph { get; init; } = "\uE8A5";
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SelectionVisibility)));
            }
        }
        public Visibility SelectionVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed record ResultChoice(Guid? ResultId, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }
}

public sealed class SavedItemsGroupHeaderVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
