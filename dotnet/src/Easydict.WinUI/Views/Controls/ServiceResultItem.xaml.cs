using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.TranslationService.Services;
using Easydict.WinUI.Services;
using TranslationLanguage = Easydict.TranslationService.Models.Language;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace Easydict.WinUI.Views.Controls;

/// <summary>
/// A collapsible result item for a single translation service.
/// Mirrors macOS EZResultView behavior with expand/collapse functionality.
/// </summary>
public sealed partial class ServiceResultItem : UserControl, IServiceResultView
{
    private ServiceQueryResult? _serviceResult;
    private bool _isHovering;
    private string? _cachedServiceId;
    private ElementTheme _cachedIconTheme;
    private BitmapImage? _cachedIcon;
    private HashSet<string>? _alreadyShownPhonetics;
    private bool _webViewInitialized;
    private string? _foundryLocalDocsUrl;
    private MdxDictionaryTranslationService? _currentMdxService;
    private FrameworkElement? _themeRoot;

    /// <summary>
    /// Exposes the control instance for parent item hosting.
    /// </summary>
    public FrameworkElement Element => this;

    public FrameworkElement? ThemeRoot
    {
        get => _themeRoot;
        set
        {
            if (ReferenceEquals(_themeRoot, value))
            {
                return;
            }

            _themeRoot = value;
        }
    }

    /// <summary>
    /// The full renderer includes icons, dictionary panels, actions, and optional WebView output.
    /// </summary>
    public bool IsMinimalRenderer => false;

    /// <summary>
    /// Exposes the header panel for sticky calculation in MiniWindow.
    /// </summary>
    public FrameworkElement HeaderPanel => HeaderBar;

    /// <summary>
    /// Exposes the action buttons panel for sticky calculation in MiniWindow.
    /// </summary>
    public FrameworkElement ActionButtonsPanel => ActionButtons;


    /// <summary>
    /// Event raised when the expand/collapse state is toggled.
    /// </summary>
    public event EventHandler<ServiceQueryResult>? CollapseToggled;

    /// <summary>
    /// Event raised when user clicks to expand a manual-query service that hasn't been queried yet.
    /// The subscriber should trigger the actual translation query for this service.
    /// </summary>
    public event EventHandler<ServiceQueryResult>? QueryRequested;

    public event EventHandler<ServiceQueryResult>? FoundryLocalStartRequested;

    public ServiceResultItem()
    {
        this.InitializeComponent();
        Loaded += OnLoaded;
        ActualThemeChanged += OnActualThemeChanged;
        var loc = LocalizationService.Instance;
        ToolTipService.SetToolTip(ReplaceButton, loc.GetString("InsertReplace"));
        FoundryLocalStartButton.Content = loc.GetString(FoundryLocalResources.UiKeys.StartButton);
        FoundryLocalDocsLink.Content = loc.GetString(FoundryLocalResources.UiKeys.DocsLinkText);
        FoundryLocalDocsLink.NavigateUri = new Uri(FoundryLocalResources.InstallDocumentationUrl);
    }

    /// <summary>
    /// Re-runs <see cref="UpdateUI"/> to pick up changes in the demotion state (e.g., when
    /// <see cref="SettingsService.HideEmptyServiceResults"/> is toggled at runtime).
    /// </summary>
    public void RefreshDemotionState() => UpdateUI();

    public void RefreshThemeChrome()
    {
        ApplyServiceChromeForCurrentTheme();
    }

    public void Cleanup()
    {
        Debug.WriteLine(
            $"[ServiceResultItem] Cleanup serviceId={_cachedServiceId ?? _serviceResult?.ServiceId ?? "<none>"} webViewInitialized={_webViewInitialized}");

        Loaded -= OnLoaded;
        ActualThemeChanged -= OnActualThemeChanged;

        if (_serviceResult != null)
        {
            _serviceResult.PropertyChanged -= OnServiceResultPropertyChanged;
        }

        try
        {
            DictWebView.NavigationCompleted -= OnDictWebViewNavigationCompleted;

            if (_webViewInitialized && DictWebView.CoreWebView2 != null)
            {
                DictWebView.CoreWebView2.WebResourceRequested -= OnWebResourceRequested;
                DictWebView.CoreWebView2.WebMessageReceived -= OnDictWebViewWebMessageReceived;

                try
                {
                    DictWebView.NavigateToString("<html><body></body></html>");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ServiceResultItem] Cleanup navigate reset failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ServiceResultItem] Cleanup WebView2 teardown failed: {ex.Message}");
        }

        _serviceResult = null;
        _currentMdxService = null;
        _themeRoot = null;
        _cachedServiceId = null;
        _cachedIconTheme = ElementTheme.Default;
        _cachedIcon = null;
        _alreadyShownPhonetics = null;
        _updateUIPending = false;
        _webViewInitialized = false;

        ServiceNameText.Text = string.Empty;
        ServiceIcon.Source = null;
        LoadingIndicator.IsActive = false;
        LoadingIndicator.Visibility = Visibility.Collapsed;
        ErrorIcon.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        ReplaceButton.Visibility = Visibility.Collapsed;
        ResultText.Text = string.Empty;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ErrorText.Text = string.Empty;
        StatusText.Text = string.Empty;
        PhoneticPanel.Children.Clear();
        PhoneticPanel.Visibility = Visibility.Collapsed;
        DictionaryPanel.Children.Clear();
        DictionaryPanel.Visibility = Visibility.Collapsed;
        PendingQueryText.Visibility = Visibility.Collapsed;
        DictWebView.Visibility = Visibility.Collapsed;
        ResultText.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
        FoundryLocalRecoveryPanel.Visibility = Visibility.Collapsed;
        FoundryLocalStartButton.Visibility = Visibility.Collapsed;
        FoundryLocalDocsLink.Visibility = Visibility.Collapsed;
        ContentArea.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// The service query result to display.
    /// </summary>
    public ServiceQueryResult? ServiceResult
    {
        get => _serviceResult;
        set
        {
            if (_serviceResult != null)
            {
                _serviceResult.PropertyChanged -= OnServiceResultPropertyChanged;
            }

            _serviceResult = value;

            if (_serviceResult != null)
            {
                _serviceResult.PropertyChanged += OnServiceResultPropertyChanged;
            }

            UpdateUI();
        }
    }

    /// <summary>
    /// Set of phonetic keys (e.g., "US:/həˈloʊ/") that have already been shown
    /// by a previous service. These phonetics will be hidden to avoid duplication.
    /// </summary>
    public HashSet<string>? AlreadyShownPhonetics
    {
        get => _alreadyShownPhonetics;
        set
        {
            _alreadyShownPhonetics = value;
            // Re-render phonetics when this changes
            if (_serviceResult?.Result != null)
            {
                UpdatePhonetics(_serviceResult.Result);
            }
        }
    }

    /// <summary>
    /// Returns the phonetic keys that this item is currently displaying.
    /// Used by parent views to track shown phonetics for deduplication.
    /// </summary>
    public IEnumerable<string> GetDisplayedPhoneticKeys()
    {
        if (_serviceResult?.Result == null) return Array.Empty<string>();

        var result = _serviceResult.Result;
        if (result.TargetLanguage != TranslationLanguage.English) return Array.Empty<string>();

        var phonetics = PhoneticDisplayHelper.GetTargetPhonetics(result)
            .Where(p => p.Accent == "US" || p.Accent == "UK")
            .Where(p => !string.IsNullOrEmpty(p.Text));

        return phonetics.Select(p => $"{p.Accent}:{p.Text}");
    }

    /// <summary>
    /// Coalesce multiple PropertyChanged events into a single UpdateUI call.
    /// Setting StreamingText fires 3 PropertyChanged events (StreamingText, DisplayText,
    /// ContentVisibility) — without coalescing, each enqueues a redundant full UpdateUI().
    /// </summary>
    private bool _updateUIPending;

    private void OnServiceResultPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Streaming hot path: while IsStreaming, only StreamingText/DisplayText change
        // per chunk. Skip the heavy full UpdateUI (icon load, theme, error/grammar
        // branches, ~30 property writes) and just refresh the text TextBlock — the
        // rest of the state is already correct from the IsStreaming transition.
        // IsStreaming → false transitions arrive as separate PropertyChanged events
        // that fall through to the full UpdateUI below.
        if (_serviceResult?.IsStreaming == true
            && (e.PropertyName == nameof(ServiceQueryResult.StreamingText)
                || e.PropertyName == nameof(ServiceQueryResult.DisplayText)))
        {
            if (!_streamingFastPathPending)
            {
                _streamingFastPathPending = true;
                DispatcherQueue.TryEnqueue(() =>
                {
                    _streamingFastPathPending = false;
                    UpdateStreamingTextOnly();
                });
            }
            return;
        }

        if (!_updateUIPending)
        {
            _updateUIPending = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                _updateUIPending = false;
                UpdateUI();
            });
        }
    }

    private bool _streamingFastPathPending;

    private void UpdateStreamingTextOnly()
    {
        if (_serviceResult == null || !_serviceResult.IsStreaming)
        {
            return;
        }

        var text = string.IsNullOrEmpty(_serviceResult.StreamingText)
            ? "Waiting for response..."
            : _serviceResult.StreamingText;

        if (_serviceResult.IsGrammarMode)
        {
            CorrectedText.Text = text;
        }
        else
        {
            ResultText.Text = text;
        }
    }

    private void UpdateUI()
    {
        if (_serviceResult == null)
        {
            return;
        }

        // Demote no-result rows: keep them visible in the list but grayed out, force-collapsed,
        // and not expandable. MainPage additionally reorders demoted rows to the bottom.
        var hideEmpty = SettingsService.Instance.HideEmptyServiceResults
            && !_serviceResult.IsLoading
            && !_serviceResult.IsStreaming
            && !_serviceResult.HasError
            && _serviceResult.Result?.ResultKind == TranslationResultKind.NoResult;
        if (hideEmpty)
        {
            _serviceResult.IsExpanded = false;
        }
        RootBorder.Opacity = hideEmpty ? 0.5 : 1.0;
        ArrowIcon.Visibility = hideEmpty ? Visibility.Collapsed : Visibility.Visible;
        var minimal = MinimalThemeService.IsActive;

        // Service info
        ServiceNameText.Text = _serviceResult.ServiceDisplayName;
        var iconTheme = GetEffectiveIconTheme();

        // Load service icon only when ServiceId changes (avoid repeated allocations during streaming)
        if (minimal)
        {
            _cachedServiceId = null;
            _cachedIconTheme = ElementTheme.Default;
            _cachedIcon = null;
            ServiceIcon.Source = null;
            ServiceIcon.Visibility = Visibility.Collapsed;
        }
        else if (_cachedServiceId != _serviceResult.ServiceId
                 || _cachedIconTheme != iconTheme
                 || _cachedIcon is null)
        {
            _cachedServiceId = _serviceResult.ServiceId;
            _cachedIconTheme = iconTheme;
            try
            {
                _cachedIcon = new BitmapImage(ServiceIconAssetResolver.GetIconUri(_serviceResult.ServiceId, iconTheme));
                ServiceIcon.Source = _cachedIcon;
                ServiceIcon.Visibility = Visibility.Visible;
            }
            catch
            {
                // Icon not found, hide it and release previous image reference
                _cachedIcon = null;
                ServiceIcon.Source = null;
                ServiceIcon.Visibility = Visibility.Collapsed;
            }
        }

        // Loading state
        LoadingIndicator.IsActive = !minimal && _serviceResult.IsLoading;
        LoadingIndicator.Visibility = !minimal && _serviceResult.IsLoading ? Visibility.Visible : Visibility.Collapsed;

        // Error state
        var hasError = _serviceResult.HasError && !_serviceResult.IsLoading;
        ErrorIcon.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = hasError && !_serviceResult.IsStreaming
            ? Visibility.Visible : Visibility.Collapsed;

        // Status text - show "Click to query" hint for pending manual-query services
        if (_serviceResult.ShowPendingQueryHint)
        {
            StatusText.Text = "Click to query";
        }
        else if (minimal && _serviceResult.IsLoading)
        {
            StatusText.Text = "Loading";
        }
        else
        {
            StatusText.Text = _serviceResult.StatusText;
        }

        // Arrow direction
        ArrowIcon.Glyph = _serviceResult.ArrowGlyph;

        // Content visibility: show during streaming, when result available, or for pending query hint
        var showPendingHint = _serviceResult.IsExpanded && _serviceResult.ShowPendingQueryHint;
        var showContent = _serviceResult.IsExpanded &&
            (_serviceResult.HasResult || _serviceResult.IsStreaming || showPendingHint);
        ContentArea.Visibility = showContent ? Visibility.Visible : Visibility.Collapsed;

        // Update header corner radius based on expand state
        HeaderBar.CornerRadius = minimal
            ? new CornerRadius(0)
            : showContent ? new CornerRadius(6, 6, 0, 0) : new CornerRadius(6);

        // Pending query hint visibility
        PendingQueryText.Visibility = showPendingHint ? Visibility.Visible : Visibility.Collapsed;

        // Branch based on mode
        if (_serviceResult.IsGrammarMode)
        {
            UpdateGrammarUI();
        }
        else
        {
            UpdateTranslationUI();
        }

        ApplyMinimalChrome();
        if (!minimal && !_isHovering)
        {
            ApplyServiceChromeForCurrentTheme();
        }
        else
        {
            ApplyHeaderForegroundForCurrentChrome();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyServiceChromeForCurrentTheme();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        _cachedIcon = null;
        UpdateUI();
        ApplyServiceChromeForCurrentTheme();
    }

    private ElementTheme GetEffectiveIconTheme()
    {
        return ThemeRoot?.ActualTheme ?? ActualTheme;
    }

    private void ApplyMinimalChrome()
    {
        if (!MinimalThemeService.IsActive || _serviceResult is null)
        {
            return;
        }

        var hasActionableStatus =
            _serviceResult.IsLoading ||
            _serviceResult.ShowPendingQueryHint ||
            _serviceResult.HasError;
        StatusText.Visibility = hasActionableStatus ? Visibility.Visible : Visibility.Collapsed;
        ActionButtons.Visibility = Visibility.Collapsed;
        ReplaceButton.Visibility = Visibility.Collapsed;
        PlayButton.Visibility = Visibility.Collapsed;
        CopyButton.Visibility = Visibility.Collapsed;
    }

    private void UpdateTranslationUI()
    {
        GrammarResultPanel.Visibility = Visibility.Collapsed;
        var resultTextBrush = FindServiceChromeBrushFallback(
                "QueryTextBrush",
                "TextFillColorPrimaryBrush")
            ?? ResultText.Foreground;
        var infoTextBrush = FindServiceChromeBrushFallback(
                "TextFillColorSecondaryBrush",
                "ExampleTextBrush")
            ?? resultTextBrush;

        // Result text - handle streaming state
        if (_serviceResult!.IsStreaming)
        {
            // Show streaming text or placeholder while waiting for first chunk
            ResultText.Text = string.IsNullOrEmpty(_serviceResult.StreamingText)
                ? "Waiting for response..."
                : _serviceResult.StreamingText;
            ResultText.Foreground = resultTextBrush;
            ResultText.Visibility = Visibility.Visible;
            HideErrorPanel();
            PhoneticPanel.Visibility = Visibility.Collapsed;
            DictionaryPanel.Visibility = Visibility.Collapsed;
            DictWebView.Visibility = Visibility.Collapsed;
            ActionButtons.Visibility = Visibility.Collapsed; // Don't show buttons during streaming
        }
        else if (_serviceResult.Result != null)
        {
            if (_serviceResult.Result.ResultKind == TranslationResultKind.NoResult)
            {
                ResultText.Text = _serviceResult.Result.InfoMessage ?? string.Empty;
                ResultText.Foreground = infoTextBrush;
                ResultText.Visibility = string.IsNullOrWhiteSpace(ResultText.Text)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                HideErrorPanel();
                PhoneticPanel.Visibility = Visibility.Collapsed;
                DictionaryPanel.Visibility = Visibility.Collapsed;
                DictWebView.Visibility = Visibility.Collapsed;
                ActionButtons.Visibility = Visibility.Collapsed;
                ReplaceButton.Visibility = Visibility.Collapsed;
                PlayButton.Visibility = Visibility.Collapsed;
            }
            else if (!string.IsNullOrEmpty(_serviceResult.Result.RawHtml))
            {
                // Rich HTML from MDX dictionary with MDD resources — render in WebView2
                ShowRawHtmlPlainTextFallback(_serviceResult.Result, resultTextBrush);
                PhoneticPanel.Visibility = Visibility.Collapsed;
                DictionaryPanel.Visibility = Visibility.Collapsed;
                HideErrorPanel();
                _ = RenderHtmlDefinitionAsync(_serviceResult.Result.RawHtml, _serviceResult.ServiceId);
                ActionButtons.Visibility = _isHovering ? Visibility.Visible : Visibility.Collapsed;
                ReplaceButton.Visibility = TextInsertionService.HasSourceWindow ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                // Hide WebView2 for non-HTML results
                DictWebView.Visibility = Visibility.Collapsed;

                UpdatePhonetics(_serviceResult.Result);
                var hasDefinitions = UpdateDictionary(_serviceResult.Result);

                // Hide TranslatedText when definitions are shown and TranslatedText is
                // a flattened version of definitions (redundant). Youdao builds TranslatedText
                // from definitions; GoogleWeb has independent plain translation.
                if (hasDefinitions && DictionaryDisplayHelper.IsTranslatedTextRedundantWithDefinitions(_serviceResult.Result))
                {
                    ResultText.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ResultText.Text = _serviceResult.Result.TranslatedText;
                    ResultText.Foreground = resultTextBrush;
                    ResultText.Visibility = Visibility.Visible;
                }

                HideErrorPanel();
                ActionButtons.Visibility = _isHovering ? Visibility.Visible : Visibility.Collapsed;
                ReplaceButton.Visibility = TextInsertionService.HasSourceWindow ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        else if (_serviceResult.Error != null)
        {
            ErrorText.Text = GetErrorDisplayText(_serviceResult);
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorText.Visibility = Visibility.Visible;
            UpdateFoundryLocalRecoveryUi(_serviceResult.Error);
            ResultText.Visibility = Visibility.Collapsed;
            PhoneticPanel.Visibility = Visibility.Collapsed;
            DictionaryPanel.Visibility = Visibility.Collapsed;
            DictWebView.Visibility = Visibility.Collapsed;
            ActionButtons.Visibility = _isHovering ? Visibility.Visible : Visibility.Collapsed;
            ReplaceButton.Visibility = Visibility.Collapsed;
            PlayButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            ResultText.Text = "";
            ResultText.Visibility = Visibility.Collapsed;
            HideErrorPanel();
            PhoneticPanel.Visibility = Visibility.Collapsed;
            DictionaryPanel.Visibility = Visibility.Collapsed;
            DictWebView.Visibility = Visibility.Collapsed;
            ActionButtons.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowRawHtmlPlainTextFallback(TranslationResult result, Brush? foreground = null)
    {
        DictWebView.Visibility = Visibility.Collapsed;
        DictWebView.Height = 0;

        ResultText.Text = result.TranslatedText;
        if (foreground != null)
        {
            ResultText.Foreground = foreground;
        }

        ResultText.Visibility = string.IsNullOrWhiteSpace(ResultText.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void HideErrorPanel()
    {
        ErrorPanel.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
        FoundryLocalRecoveryPanel.Visibility = Visibility.Collapsed;
        FoundryLocalStartButton.Visibility = Visibility.Collapsed;
        FoundryLocalDocsLink.Visibility = Visibility.Collapsed;
    }

    private void UpdateFoundryLocalRecoveryUi(TranslationException error)
    {
        var action = error.RecoveryAction;
        var showStart = string.Equals(
            action,
            FoundryLocalResources.StartRecoveryAction,
            StringComparison.Ordinal)
            && FoundryLocalStartRequested is not null;
        var showDocs = showStart
            || string.Equals(action, FoundryLocalResources.InstallRecoveryAction, StringComparison.Ordinal);

        FoundryLocalRecoveryPanel.Visibility = showStart || showDocs
            ? Visibility.Visible
            : Visibility.Collapsed;
        FoundryLocalStartButton.Visibility = showStart ? Visibility.Visible : Visibility.Collapsed;
        FoundryLocalDocsLink.Visibility = showDocs ? Visibility.Visible : Visibility.Collapsed;
        var docsUrl = string.IsNullOrWhiteSpace(error.DocumentationUrl)
            ? FoundryLocalResources.InstallDocumentationUrl
            : error.DocumentationUrl;
        if (!string.Equals(_foundryLocalDocsUrl, docsUrl, StringComparison.Ordinal))
        {
            _foundryLocalDocsUrl = docsUrl;
            FoundryLocalDocsLink.NavigateUri = new Uri(docsUrl);
        }
    }

    private void UpdateGrammarUI()
    {
        // Hide translation-specific elements
        ResultText.Visibility = Visibility.Collapsed;
        PhoneticPanel.Visibility = Visibility.Collapsed;
        DictionaryPanel.Visibility = Visibility.Collapsed;

        // Localize labels
        var loc = LocalizationService.Instance;
        OriginalLabel.Text = loc.GetString("GrammarResult_Original") ?? "Original:";
        ChangesLabel.Text = loc.GetString("GrammarResult_Changes") ?? "Changes:";
        NoCorrectionsText.Text = loc.GetString("GrammarResult_NoIssues") ?? "No grammar issues found.";

        if (_serviceResult!.IsStreaming)
        {
            GrammarResultPanel.Visibility = Visibility.Visible;
            HideErrorPanel();
            CorrectedText.Text = string.IsNullOrEmpty(_serviceResult.StreamingText)
                ? "Waiting for response..." : _serviceResult.StreamingText;
            OriginalText.Text = "";
            ExplanationPanel.Visibility = Visibility.Collapsed;
            NoCorrectionsText.Visibility = Visibility.Collapsed;
            ActionButtons.Visibility = Visibility.Collapsed;
        }
        else if (_serviceResult.GrammarResult != null)
        {
            var gr = _serviceResult.GrammarResult;
            GrammarResultPanel.Visibility = Visibility.Visible;
            HideErrorPanel();
            CorrectedText.Text = gr.CorrectedText;
            OriginalText.Text = gr.OriginalText;

            if (gr.HasCorrections)
            {
                NoCorrectionsText.Visibility = Visibility.Collapsed;
                if (!string.IsNullOrEmpty(gr.Explanation))
                {
                    ExplanationPanel.Visibility = Visibility.Visible;
                    ExplanationText.Text = gr.Explanation;
                }
                else
                {
                    ExplanationPanel.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                NoCorrectionsText.Visibility = Visibility.Visible;
                ExplanationPanel.Visibility = Visibility.Collapsed;
            }

            ActionButtons.Visibility = _isHovering ? Visibility.Visible : Visibility.Collapsed;
            ReplaceButton.Visibility = TextInsertionService.HasSourceWindow
                ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (_serviceResult.Error != null)
        {
            GrammarResultPanel.Visibility = Visibility.Collapsed;
            ErrorText.Text = GetErrorDisplayText(_serviceResult);
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorText.Visibility = Visibility.Visible;
            UpdateFoundryLocalRecoveryUi(_serviceResult.Error);
            ActionButtons.Visibility = _isHovering ? Visibility.Visible : Visibility.Collapsed;
            ReplaceButton.Visibility = Visibility.Collapsed;
            PlayButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            GrammarResultPanel.Visibility = Visibility.Collapsed;
            HideErrorPanel();
            ActionButtons.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Populates the phonetic badges panel from WordResult phonetics data.
    /// Each badge shows: [accent label] [phonetic text] [speaker icon].
    /// Only displays phonetics when the target language is English.
    /// Filters out phonetics that have already been shown by a previous service.
    /// </summary>
    private void UpdatePhonetics(TranslationResult result)
    {
        // Only show phonetics when target language is English
        // US/UK phonetics are English pronunciation, only meaningful for English translations
        if (result.TargetLanguage != TranslationLanguage.English)
        {
            PhoneticPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var phonetics = result.WordResult?.Phonetics;
        if (phonetics == null || phonetics.Count == 0)
        {
            PhoneticPanel.Visibility = Visibility.Collapsed;
            return;
        }

        PhoneticPanel.Children.Clear();

        // Get target-related phonetics (dest/US/UK) and then display only US/UK accents
        // Filter out phonetics that have already been shown by a previous service
        var displayablePhonetics = PhoneticDisplayHelper.GetTargetPhonetics(result)
            .Where(p => p.Accent == "US" || p.Accent == "UK")
            .Where(p => !string.IsNullOrEmpty(p.Text))
            .Where(p => _alreadyShownPhonetics == null || !_alreadyShownPhonetics.Contains($"{p.Accent}:{p.Text}"))
            .ToList();

        foreach (var phonetic in displayablePhonetics)
        {
            PhoneticPanel.Children.Add(CreatePhoneticBadge(phonetic, result));
        }

        PhoneticPanel.Visibility = PhoneticPanel.Children.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Populates the dictionary panel from WordResult definitions and examples.
    /// Each definition shows a POS tag followed by meanings. Examples are shown below.
    /// Returns true if definitions were rendered.
    /// </summary>
    private bool UpdateDictionary(TranslationResult result)
    {
        var definitions = result.WordResult?.Definitions;
        var examples = result.WordResult?.Examples;
        var wordForms = result.WordResult?.WordForms;
        var synonyms = result.WordResult?.Synonyms;

        var hasDefinitions = definitions != null && definitions.Count > 0;
        var hasExamples = examples != null && examples.Count > 0;
        var hasWordForms = wordForms != null && wordForms.Count > 0;
        var hasSynonyms = synonyms != null && synonyms.Count > 0;

        if (!hasDefinitions && !hasExamples && !hasWordForms && !hasSynonyms)
        {
            DictionaryPanel.Visibility = Visibility.Collapsed;
            return false;
        }

        DictionaryPanel.Children.Clear();

        // Render definitions grouped by part of speech
        if (hasDefinitions)
        {
            foreach (var definition in definitions!)
            {
                DictionaryPanel.Children.Add(CreateDefinitionRow(definition));
            }
        }

        // Render word forms (e.g., "过去式 ran · 复数 runs · 现在分词 running")
        if (hasWordForms)
        {
            DictionaryPanel.Children.Add(CreateWordFormsRow(wordForms!));
        }

        // Render synonyms (one row per POS group)
        if (hasSynonyms)
        {
            foreach (var synonym in synonyms!)
            {
                DictionaryPanel.Children.Add(CreateSynonymRow(synonym));
            }
        }

        // Render example sentences (limit to 3 for compactness)
        if (hasExamples)
        {
            foreach (var example in examples!.Take(3))
            {
                DictionaryPanel.Children.Add(new TextBlock
                {
                    Text = $"\u201C{example}\u201D",
                    FontSize = 13,
                    FontStyle = Windows.UI.Text.FontStyle.Italic,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = FindServiceChromeBrushFallback(
                        "ExampleTextBrush",
                        "TextFillColorSecondaryBrush"),
                    IsTextSelectionEnabled = true,
                    Margin = new Thickness(0, 1, 0, 1)
                });
            }
        }

        DictionaryPanel.Visibility = Visibility.Visible;
        return hasDefinitions;
    }

    /// <summary>
    /// Creates a compact word forms row: "过去式 ran · 复数 runs · 现在分词 running"
    /// </summary>
    private TextBlock CreateWordFormsRow(IReadOnlyList<WordForm> wordForms)
    {
        var mutedBrush = FindServiceChromeBrushFallback(
            "ExampleTextBrush",
            "TextFillColorSecondaryBrush");
        var normalBrush = FindServiceChromeBrushFallback(
            "QueryTextBrush",
            "TextFillColorPrimaryBrush");

        var block = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Margin = new Thickness(0, 2, 0, 0)
        };

        for (int i = 0; i < wordForms.Count; i++)
        {
            var wf = wordForms[i];
            if (string.IsNullOrEmpty(wf.Value)) continue;

            if (block.Inlines.Count > 0)
            {
                block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                {
                    Text = " · ",
                    Foreground = mutedBrush
                });
            }

            if (!string.IsNullOrEmpty(wf.Name))
            {
                block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                {
                    Text = wf.Name + " ",
                    Foreground = mutedBrush
                });
            }

            block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = wf.Value,
                Foreground = normalBrush
            });
        }

        return block;
    }

    /// <summary>
    /// Creates a synonym row: "同义词 [n.] 问候: greeting, salutation"
    /// </summary>
    private Grid CreateSynonymRow(Synonym synonym)
    {
        var mutedBrush = FindServiceChromeBrushFallback(
            "ExampleTextBrush",
            "TextFillColorSecondaryBrush");
        var normalBrush = FindServiceChromeBrushFallback(
            "QueryTextBrush",
            "TextFillColorPrimaryBrush");

        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Label: "同义词"
        var label = new TextBlock
        {
            Text = "同义词",
            FontSize = 12,
            Foreground = mutedBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        // Content: "[n.] 问候: greeting, salutation"
        var contentBlock = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (!string.IsNullOrEmpty(synonym.PartOfSpeech))
        {
            contentBlock.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = $"[{synonym.PartOfSpeech}] ",
                Foreground = mutedBrush
            });
        }

        if (!string.IsNullOrEmpty(synonym.Meaning))
        {
            contentBlock.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = synonym.Meaning + ": ",
                Foreground = mutedBrush
            });
        }

        if (synonym.Words?.Count > 0)
        {
            contentBlock.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = string.Join(", ", synonym.Words),
                Foreground = normalBrush
            });
        }

        Grid.SetColumn(contentBlock, 1);
        row.Children.Add(contentBlock);

        return row;
    }

    /// <summary>
    /// Creates a single definition row: [POS tag] meaning1; meaning2
    /// Uses Grid layout (Auto + Star columns) so meanings text wraps correctly.
    /// </summary>
    private Grid CreateDefinitionRow(Definition definition)
    {
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int column = 0;

        // POS tag (e.g., "n.", "v.", "interjection")
        if (!string.IsNullOrEmpty(definition.PartOfSpeech))
        {
            var posTag = new Border
            {
                Background = FindServiceChromeBrushFallback(
                    "PosTagBackgroundBrush",
                    "ControlFillColorSecondaryBrush"),
                CornerRadius = MinimalThemeService.IsActive ? new CornerRadius(0) : new CornerRadius(3),
                Padding = new Thickness(5, 1, 5, 1),
                VerticalAlignment = VerticalAlignment.Center
            };

            posTag.Child = new TextBlock
            {
                Text = definition.PartOfSpeech,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = FindServiceChromeBrushFallback(
                    "PosTagTextBrush",
                    "BlueAccentBrush",
                    "QueryTextBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(posTag, 0);
            row.Children.Add(posTag);
            column = 1;
        }

        // Meanings text
        var meanings = definition.Meanings;
        if (meanings != null && meanings.Count > 0)
        {
            var meaningsBlock = new TextBlock
            {
                Text = string.Join("; ", meanings),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = FindServiceChromeBrushFallback(
                    "QueryTextBrush",
                    "TextFillColorPrimaryBrush"),
                IsTextSelectionEnabled = true,
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(meaningsBlock, column);
            row.Children.Add(meaningsBlock);
        }

        return row;
    }

    /// <summary>
    /// Creates a single phonetic badge with accent label, phonetic text, and TTS button.
    /// Includes accessibility properties for screen readers.
    /// </summary>
    private Border CreatePhoneticBadge(Phonetic phonetic, TranslationResult result)
    {
        // Build accessibility description
        var accentLabel = GetAccentDisplayLabel(phonetic.Accent);
        var accentDescription = phonetic.Accent switch
        {
            "US" => "American pronunciation",
            "UK" => "British pronunciation",
            "dest" => "Target language pronunciation",
            "src" => "Source language pronunciation",
            _ => "Pronunciation"
        };
        var accessibleName = $"{accentDescription}: {phonetic.Text}";

        var badge = new Border
        {
            Background = FindServiceChromeBrushFallback(
                "PhoneticBadgeBackgroundBrush",
                "ControlFillColorSecondaryBrush"),
            CornerRadius = MinimalThemeService.IsActive ? new CornerRadius(0) : new CornerRadius(4),
            Padding = new Thickness(6, 2, 4, 2)
        };

        // Set accessibility properties on the badge
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(badge, accessibleName);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(badge,
            "Click the speaker button to hear pronunciation");

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2
        };

        // Accent label (e.g., "美", "英", "src", "dest")
        if (!string.IsNullOrEmpty(accentLabel))
        {
            panel.Children.Add(new TextBlock
            {
                Text = accentLabel,
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = FindServiceChromeBrushFallback(
                    "PhoneticBadgeTextBrush",
                    "QueryTextBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        // Phonetic text
        panel.Children.Add(new TextBlock
        {
            Text = PhoneticDisplayHelper.FormatPhoneticText(phonetic.Text!),
            FontSize = 10,
            Foreground = FindServiceChromeBrushFallback(
                "PhoneticBadgeTextBrush",
                "QueryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = true
        });

        // TTS speaker button
        var speakerButton = new Button
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var speakerIcon = new FontIcon
        {
            Glyph = "\uE767", // Volume icon
            FontSize = 10,
            Foreground = FindServiceChromeBrushFallback(
                "PhoneticBadgeTextBrush",
                "QueryTextBrush")
        };
        speakerButton.Content = speakerIcon;

        // Determine which language to use for TTS based on accent
        // Use alias to avoid conflict with FrameworkElement.Language (string)
        TranslationLanguage ttsLanguage;
        string ttsText;

        if (phonetic.Accent == "dest")
        {
            // Destination accent: use translated text in target language
            ttsLanguage = result.TargetLanguage;
            ttsText = result.TranslatedText;
        }
        else if (phonetic.Accent == "US" || phonetic.Accent == "UK")
        {
            // English accents ("US"/"UK"): use English translation
            ttsLanguage = TranslationLanguage.English;
            ttsText = result.TranslatedText;
        }
        else
        {
            // Fallback: use original text with detected language
            ttsLanguage = result.DetectedLanguage != TranslationLanguage.Auto
                ? result.DetectedLanguage
                : TranslationLanguage.English;
            ttsText = result.OriginalText;
        }

        speakerButton.Click += async (s, e) =>
        {
            var tts = TextToSpeechService.Instance;

            // Reset the icon back to the speaker glyph on the UI thread.
            void ResetIconGlyph()
            {
                DispatcherQueue.TryEnqueue(() => speakerIcon.Glyph = "\uE767");
            }

            // Handler for playback completion; unsubscribes itself and resets the icon.
            void OnPlaybackEnded()
            {
                tts.PlaybackEnded -= OnPlaybackEnded;
                ResetIconGlyph();
            }

            speakerIcon.Glyph = "\uE71A"; // Stop icon
            tts.PlaybackEnded += OnPlaybackEnded;

            try
            {
                await tts.SpeakAsync(ttsText, ttsLanguage);
            }
            finally
            {
                // Ensure we always detach the handler and reset the icon,
                // even if SpeakAsync fails, is cancelled, or playback ends early.
                tts.PlaybackEnded -= OnPlaybackEnded;
                ResetIconGlyph();
            }
        };

        panel.Children.Add(speakerButton);
        badge.Child = panel;
        return badge;
    }

    /// <summary>
    /// Maps phonetic accent codes to display labels.
    /// Delegates to PhoneticDisplayHelper for testability.
    /// </summary>
    private static string? GetAccentDisplayLabel(string? accent)
    {
        return PhoneticDisplayHelper.GetAccentDisplayLabel(accent);
    }

    private void OnHeaderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_serviceResult == null || _serviceResult.IsLoading)
        {
            return;
        }

        // Demoted (no-result + hide-empty) rows are not expandable.
        if (ServiceResultDemotionHelper.IsDemoted(_serviceResult))
        {
            e.Handled = true;
            return;
        }

        // Only handle left click
        var point = e.GetCurrentPoint(HeaderBar);
        if (point.Properties.IsLeftButtonPressed)
        {
            ToggleCollapse();
            e.Handled = true;
        }
    }

    private void ToggleCollapse()
    {
        if (_serviceResult == null) return;

        // Check if this is a manual-query service that needs to be queried
        var wasCollapsed = !_serviceResult.IsExpanded;
        var needsQuery = !_serviceResult.EnabledQuery && !_serviceResult.HasQueried && wasCollapsed;

        _serviceResult.ToggleExpanded();
        UpdateUI();
        CollapseToggled?.Invoke(this, _serviceResult);

        // If expanding a manual-query service that hasn't been queried, request query
        if (needsQuery && _serviceResult.IsExpanded)
        {
            QueryRequested?.Invoke(this, _serviceResult);
        }
    }


    private void OnRetryClicked(object sender, RoutedEventArgs e)
    {
        if (_serviceResult == null || _serviceResult.IsLoading)
            return;

        _serviceResult.Error = null;
        _serviceResult.ClearQueried();
        QueryRequested?.Invoke(this, _serviceResult);
    }

    private void OnFoundryLocalStartClicked(object sender, RoutedEventArgs e)
    {
        if (_serviceResult == null || _serviceResult.IsLoading)
        {
            return;
        }

        FoundryLocalStartRequested?.Invoke(this, _serviceResult);
    }

    private void OnControlPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (MinimalThemeService.IsActive)
        {
            return;
        }

        _isHovering = true;

        if (_serviceResult?.IsExpanded == true &&
            (_serviceResult.Result != null || _serviceResult.Error != null || _serviceResult.GrammarResult != null))
        {
            var hasResult = (_serviceResult.Result?.ResultKind == TranslationResultKind.Success) || _serviceResult.GrammarResult != null;
            ActionButtons.Visibility = hasResult || _serviceResult.Error != null
                ? Visibility.Visible
                : Visibility.Collapsed;
            ReplaceButton.Visibility = hasResult && TextInsertionService.HasSourceWindow
                ? Visibility.Visible : Visibility.Collapsed;
            PlayButton.Visibility = _serviceResult.Result?.ResultKind == TranslationResultKind.Success
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnControlPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isHovering = false;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
        ActionButtons.Visibility = Visibility.Collapsed;
    }

    private void OnHeaderBarPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (MinimalThemeService.IsActive)
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
            return;
        }

        Brush? background = FindServiceChromeColorOrBrush(
            "ServiceResultHeaderHoverBackgroundColor",
            "ServiceResultHeaderHoverBackgroundBrush",
            "ButtonHoverBrush");
        if (background is not null)
        {
            HeaderBar.Background = background;
        }

        ApplyHeaderForegroundForCurrentChrome();

        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }

    private Brush? FindThemeBrush(string key)
    {
        return ThemeResourceService.GetBrush(key, _themeRoot ?? this);
    }

    private Windows.UI.Color? FindThemeColor(string key)
    {
        return ThemeResourceService.GetColor(key, _themeRoot ?? this);
    }

    private Brush? FindServiceChromeBrush(string key)
    {
        return FindThemeBrush(key);
    }

    private Brush? FindServiceChromeColorBrush(string key)
    {
        return FindServiceChromeColor(key) is Windows.UI.Color color
            ? new SolidColorBrush(color)
            : null;
    }

    private Brush? FindServiceChromeColorOrBrush(string colorKey, params string[] brushKeys)
    {
        return FindServiceChromeColorBrush(colorKey) ?? FindServiceChromeBrushFallback(brushKeys);
    }

    private Brush? FindServiceChromeBrushFallback(params string[] keys)
    {
        foreach (var key in keys)
        {
            if (FindServiceChromeBrush(key) is Brush brush)
            {
                return brush;
            }
        }

        return null;
    }

    private Windows.UI.Color? FindServiceChromeColor(string key)
    {
        return FindThemeColor(key);
    }

    private void ApplyServiceChromeForCurrentTheme()
    {
        if (FindServiceChromeColorOrBrush(
                "ResultViewBackgroundColor",
                "ResultViewBackgroundBrush") is Brush rootBackground)
        {
            RootBorder.Background = rootBackground;
        }

        if (FindServiceChromeColorOrBrush(
                "EasydictCardBorderColor",
                "CardStrokeColorDefaultBrush",
                "MainBorderBrush") is Brush borderBrush)
        {
            RootBorder.BorderBrush = borderBrush;
            HeaderBar.BorderBrush = borderBrush;
        }

        if (FindServiceChromeColorOrBrush(
                "ServiceResultHeaderBackgroundColor",
                "ServiceResultHeaderBackgroundBrush") is Brush brush)
        {
            HeaderBar.Background = brush;
        }

        ApplyHeaderForegroundForCurrentChrome();
    }

    private void ApplyHeaderForegroundForCurrentChrome()
    {
        var primaryBrush = FindServiceChromeColorOrBrush(
            "ServiceResultHeaderForegroundColor",
            "ServiceResultHeaderForegroundBrush",
            "TextFillColorPrimaryBrush");
        var secondaryBrush = FindServiceChromeColorOrBrush(
            "ServiceResultHeaderSecondaryForegroundColor",
            "ServiceResultHeaderSecondaryForegroundBrush",
            "TextFillColorSecondaryBrush");

        if (primaryBrush is not null)
        {
            ServiceNameText.Foreground = primaryBrush;
        }

        if (secondaryBrush is not null)
        {
            StatusText.Foreground = secondaryBrush;
            ArrowIcon.Foreground = secondaryBrush;
        }
    }

    private string ResolveServiceChromeCssColor(params string[] keys)
    {
        foreach (var key in keys)
        {
            if (FindServiceChromeColor(key) is Windows.UI.Color color)
            {
                return ToCssColor(color);
            }
        }

        return "transparent";
    }

    private static string ToCssColor(Windows.UI.Color color)
    {
        if (color.A == 255)
        {
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        return FormattableString.Invariant(
            $"rgba({color.R}, {color.G}, {color.B}, {color.A / 255d:0.###})");
    }

    private void OnHeaderBarPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (MinimalThemeService.IsActive)
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
            return;
        }

        // Restore opaque background instead of clearing it to maintain sticky header visibility.
        ApplyServiceChromeForCurrentTheme();
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }

    private async void OnReplaceClicked(object sender, RoutedEventArgs e)
    {
        var text = _serviceResult?.IsGrammarMode == true
            ? _serviceResult.GrammarResult?.CorrectedText
            : _serviceResult?.Result?.TranslatedText;
        if (string.IsNullOrEmpty(text))
            return;

        var success = await TextInsertionService.InsertTextAsync(text);

        // Visual feedback
        ReplaceIcon.Glyph = success ? "\uE8FB" : "\uE783"; // Checkmark or error
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(1500);
            ReplaceIcon.Glyph = "\uE8AC"; // Reset to replace icon
        });
    }

    private async void OnPlayClicked(object sender, RoutedEventArgs e)
    {
        var result = _serviceResult?.Result;
        if (result == null || string.IsNullOrEmpty(result.TranslatedText))
            return;

        var tts = TextToSpeechService.Instance;

        // Reset the icon back to the play glyph on the UI thread.
        void ResetIconGlyph()
        {
            DispatcherQueue.TryEnqueue(() => PlayIcon.Glyph = "\uE768");
        }

        // Handler for playback completion; unsubscribes itself and resets the icon.
        void OnPlaybackEnded()
        {
            tts.PlaybackEnded -= OnPlaybackEnded;
            ResetIconGlyph();
        }

        PlayIcon.Glyph = "\uE71A"; // Stop icon
        tts.PlaybackEnded += OnPlaybackEnded;

        try
        {
            await tts.SpeakAsync(result.TranslatedText, result.TargetLanguage);
        }
        finally
        {
            // Ensure we always detach the handler and reset the icon,
            // even if SpeakAsync fails, is cancelled, or playback ends early.
            tts.PlaybackEnded -= OnPlaybackEnded;
            ResetIconGlyph();
        }
    }

    /// <summary>
    /// Returns the error message to display, with a region-aware hint appended
    /// when an international-only service fails with a network error or timeout.
    /// </summary>
    private static string GetErrorDisplayText(ServiceQueryResult serviceResult)
    {
        var error = serviceResult.Error;
        if (error == null)
        {
            return string.Empty;
        }

        var message = error.Message;

        // Append region hint for international services that fail with network errors.
        // Also notify SettingsService so it can lazily migrate defaults (timezone + failure = China network).
        var serviceId = serviceResult.ServiceId;
        if (!string.IsNullOrEmpty(serviceId) &&
            SettingsService.IsInternationalOnlyService(serviceId) &&
            error.ErrorCode is TranslationErrorCode.NetworkError or TranslationErrorCode.Timeout)
        {
            SettingsService.Instance.NotifyInternationalServiceFailed(serviceId, error.ErrorCode);

            var loc = LocalizationService.Instance;
            var hint = loc.GetString("InternationalServiceUnavailableHint");
            if (!string.IsNullOrEmpty(hint))
            {
                message = $"{message}\n{hint}";
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ServiceResultItem] InternationalServiceUnavailableHint localization string is missing");
            }
        }

        return message;
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        var text = _serviceResult?.IsGrammarMode == true
            ? _serviceResult.GrammarResult?.CorrectedText ?? _serviceResult?.Error?.Message
            : _serviceResult?.Result?.TranslatedText ?? _serviceResult?.Error?.Message;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var dataPackage = new DataPackage();
        dataPackage.SetText(text);
        Clipboard.SetContent(dataPackage);

        // Visual feedback
        CopyIcon.Glyph = "\uE8FB"; // Checkmark
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(1500);
            CopyIcon.Glyph = "\uE8C8"; // Copy icon
        });
    }

    /// <summary>
    /// Renders raw HTML dictionary definition in the WebView2 control.
    /// Sets up virtual host mapping for loose files and MDD resource interception.
    /// </summary>
    private async Task RenderHtmlDefinitionAsync(string rawHtml, string serviceId)
    {
        try
        {
            DictWebView.Height = 1;
            DictWebView.Visibility = Visibility.Visible;

            if (!_webViewInitialized)
            {
                await DictWebView.EnsureCoreWebView2Async();
                _webViewInitialized = true;

                DictWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                DictWebView.CoreWebView2.Settings.IsScriptEnabled = true;
                DictWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                DictWebView.CoreWebView2.Settings.IsWebMessageEnabled = true;

                // Register resource request filter for MDD resources
                DictWebView.CoreWebView2.AddWebResourceRequestedFilter("https://dictassets/*", CoreWebView2WebResourceContext.All);
                DictWebView.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
                DictWebView.CoreWebView2.WebMessageReceived += OnDictWebViewWebMessageReceived;
                DictWebView.NavigationCompleted += OnDictWebViewNavigationCompleted;

                ConfigureWebViewContentDrag();
            }

            // Resolve the MDX service for resource lookups
            _currentMdxService = null;
            try
            {
                using var handle = TranslationManagerService.Instance.AcquireHandle();
                if (handle.Manager.Services.TryGetValue(serviceId, out var svc)
                    && svc is MdxDictionaryTranslationService mdxSvc)
                {
                    _currentMdxService = mdxSvc;

                    // Map dictionary directory for loose file access (images, CSS on disk)
                    if (!string.IsNullOrEmpty(mdxSvc.DictionaryDirectory) && Directory.Exists(mdxSvc.DictionaryDirectory))
                    {
                        DictWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                            "dictassets", mdxSvc.DictionaryDirectory,
                            CoreWebView2HostResourceAccessKind.Allow);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ServiceResultItem] Failed to resolve MDX service: {ex.Message}");
            }

            var bgColor = ResolveServiceChromeCssColor(
                "DictionaryHtmlBackgroundColor",
                "ResultViewBackgroundColor");
            var textColor = ResolveServiceChromeCssColor(
                "DictionaryHtmlTextColor",
                "QueryTextColor");
            var linkColor = ResolveServiceChromeCssColor(
                "DictionaryHtmlLinkColor",
                "BlueAccentColor");

            // Rewrite relative resource paths to use virtual host
            var processedHtml = RewriteResourcePaths(rawHtml);

            var html = $$"""
                <!DOCTYPE html>
                <html>
                <head>
                <meta charset="utf-8">
                <style>
                    html {
                        margin: 0;
                        padding: 0;
                        max-width: 100%;
                        overflow-x: hidden;
                        overflow-y: hidden;
                        height: auto;
                    }
                    body {
                        margin: 4px 0;
                        padding: 0 8px 12px;
                        font-family: -apple-system, 'Segoe UI', sans-serif;
                        font-size: 13px;
                        line-height: 1.45;
                        color: {{textColor}};
                        background-color: {{bgColor}};
                        word-wrap: break-word;
                        overflow-wrap: break-word;
                        -webkit-font-smoothing: antialiased;
                        text-rendering: optimizeLegibility;
                        max-width: 100%;
                        overflow-x: hidden;
                        overflow-y: hidden;
                        max-height: none;
                        height: auto;
                    }
                    h1, h2, h3, h4, h5, h6 {
                        margin: 0 0 10px;
                        line-height: 1.2;
                    }
                    ol, ul {
                        margin: 8px 0 12px;
                        padding-left: 1.35em;
                    }
                    li {
                        margin: 4px 0;
                    }
                    [class*="phon"], [class*="pron"], [class*="ipa"] {
                        letter-spacing: 0.01em;
                        line-height: 1.35;
                    }
                    [class*="meaning"], [class*="def"], [class*="sense"], [class*="gloss"] {
                        line-height: 1.55;
                    }
                    img, svg, table { max-width: 100% !important; height: auto; }
                    pre { white-space: pre-wrap; overflow-wrap: anywhere; }
                    a { color: {{linkColor}}; }
                    [style*="overflow-y"],
                    [style*="overflow:"],
                    [style*="max-height"],
                    [class*="scroll"],
                    [class*="Scroll"],
                    [id*="scroll"],
                    [id*="Scroll"] {
                        overflow-y: visible !important;
                        max-height: none !important;
                        height: auto !important;
                    }
                </style>
                <script>
                    (() => {
                        const findScrollableContainer = (start) => {
                            let node = start instanceof Element ? start : null;
                            while (node && node !== document.body) {
                                const style = window.getComputedStyle(node);
                                const overflowY = style.overflowY || style.overflow;
                                const canScroll =
                                    (overflowY === 'auto' || overflowY === 'scroll')
                                    && node.scrollHeight > node.clientHeight + 1;
                                if (canScroll) {
                                    return node;
                                }

                                node = node.parentElement;
                            }

                            return null;
                        };

                        window.addEventListener('wheel', event => {
                            const scrollable = findScrollableContainer(event.target);
                            if (!scrollable) {
                                window.chrome?.webview?.postMessage({
                                    type: 'dict-wheel-passthrough',
                                    deltaY: event.deltaY
                                });
                                event.preventDefault();
                                return;
                            }

                            const atTop = scrollable.scrollTop <= 0;
                            const atBottom = scrollable.scrollTop + scrollable.clientHeight >= scrollable.scrollHeight - 1;
                            if ((event.deltaY < 0 && atTop) || (event.deltaY > 0 && atBottom)) {
                                window.chrome?.webview?.postMessage({
                                    type: 'dict-wheel-boundary',
                                    deltaY: event.deltaY
                                });
                                event.preventDefault();
                            }
                        }, { passive: false, capture: true });
                    })();
                </script>
                </head>
                <body>{{processedHtml}}</body>
                </html>
                """;

            DictWebView.NavigateToString(html);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ServiceResultItem] WebView2 rendering failed: {ex.Message}");
            if (_serviceResult?.Result != null)
            {
                ShowRawHtmlPlainTextFallback(_serviceResult.Result);
            }
        }
    }

    /// <summary>
    /// Rewrites relative resource paths in HTML to use the dictassets virtual host.
    /// Handles src="...", href="...", and url(...) patterns.
    /// </summary>
    private static string RewriteResourcePaths(string html)
    {
        // Rewrite src="..." and href="..." (skip absolute URLs and data: URIs)
        html = Regex.Replace(html, """((?:src|href)\s*=\s*["'])(?!https?://|data:|javascript:)([^"']+)(["'])""",
            "$1https://dictassets/$2$3", RegexOptions.IgnoreCase);

        // Rewrite url(...) in CSS (skip absolute URLs and data: URIs)
        html = Regex.Replace(html, """url\(\s*["']?(?!https?://|data:)([^"')]+)["']?\s*\)""",
            "url('https://dictassets/$1')", RegexOptions.IgnoreCase);

        return html;
    }

    /// <summary>
    /// Enable drag-from-WebView2 if the user opted in and the host runtime supports it.
    /// Falls back silently when either condition fails — copy buttons / context menu still work.
    /// </summary>
    private void ConfigureWebViewContentDrag()
    {
        try
        {
            if (!Services.SettingsService.Instance.WebView2DragEnabled)
            {
                System.Diagnostics.Debug.WriteLine("[ServiceResultItem] WebView2 drag disabled by user setting.");
                return;
            }
            if (!Services.WebView2RuntimeService.SupportsContentDrag)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ServiceResultItem] WebView2 runtime {Services.WebView2RuntimeService.RuntimeVersion} " +
                    $"below drag minimum {Services.WebView2RuntimeService.DragSupportMinimumVersion}");
                return;
            }

            // Drag from WebView2 content is enabled by default in recent runtimes; the WinAppSDK
            // 2.x XAML control wires the OS drag through automatically.
            //
            // TODO(WinAppSDK 2.0.1): if a future SDK release exposes an explicit toggle (e.g.
            // CoreWebView2Settings.IsContentDragEnabled or a CoreWebView2.OnContentDragStarting
            // event), call it here and attach an Easydict source descriptor (From, SourceLanguage,
            // TargetLanguage, ServiceId) to the drag payload so downstream tools can identify the
            // origin.
            System.Diagnostics.Debug.WriteLine("[ServiceResultItem] WebView2 content drag enabled.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ServiceResultItem] ConfigureWebViewContentDrag failed: {ex.Message}");
        }
    }

    private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (_currentMdxService == null)
            return;

        var uri = new Uri(args.Request.Uri);
        var resourceKey = Uri.UnescapeDataString(uri.AbsolutePath);

        // Try MDD lookup
        var bytes = _currentMdxService.LookupResource(resourceKey);
        if (bytes != null)
        {
            var mimeType = GetMimeType(resourceKey);
            var stream = new InMemoryRandomAccessStream();
            var writer = new DataWriter(stream);
            writer.WriteBytes(bytes);
            writer.StoreAsync().AsTask().GetAwaiter().GetResult();
            writer.DetachStream();
            stream.Seek(0);

            args.Response = sender.Environment.CreateWebResourceResponse(
                stream, 200, "OK", $"Content-Type: {mimeType}");
        }
    }

    private async void OnDictWebViewNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess)
        {
            Debug.WriteLine($"[ServiceResultItem] WebView2 navigation failed: {args.WebErrorStatus}");
            if (_serviceResult?.Result != null)
            {
                ShowRawHtmlPlainTextFallback(_serviceResult.Result);
            }
            return;
        }

        try
        {
            // Let the browser settle the CSS-first overflow normalization before
            // measuring content height for the host ScrollViewer.
            await Task.Delay(50);

            var heightStr = await MeasureDictionaryHeightAsync(sender);
            if (int.TryParse(heightStr.Trim('"'), out var height) && height > 0)
            {
                var targetHeight = height + 8;
                if (Math.Abs(sender.Height - targetHeight) > 1)
                {
                    sender.DispatcherQueue.TryEnqueue(() =>
                    {
                        ResultText.Visibility = Visibility.Collapsed;
                        sender.Visibility = Visibility.Visible;
                        if (Math.Abs(sender.Height - targetHeight) > 1)
                        {
                            sender.Height = targetHeight;
                        }
                    });
                }

                return;
            }

            Debug.WriteLine("[ServiceResultItem] WebView2 measured zero height; falling back to plain text");
            if (_serviceResult?.Result != null)
            {
                ShowRawHtmlPlainTextFallback(_serviceResult.Result);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ServiceResultItem] Failed to auto-size WebView2: {ex.Message}");
            if (_serviceResult?.Result != null)
            {
                ShowRawHtmlPlainTextFallback(_serviceResult.Result);
            }
        }
    }

    private void OnDictWebViewWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement) ||
                (typeElement.GetString() is not "dict-wheel-boundary" and not "dict-wheel-passthrough") ||
                !root.TryGetProperty("deltaY", out var deltaElement))
            {
                return;
            }

            var deltaY = deltaElement.ValueKind switch
            {
                JsonValueKind.Number when deltaElement.TryGetDouble(out var value) => value,
                _ => 0
            };
            if (Math.Abs(deltaY) < double.Epsilon)
            {
                return;
            }

            var hostScrollViewer = ResultContentScrollViewer ?? FindAncestorScrollViewer(DictWebView);
            TryScrollViewerChain(hostScrollViewer, deltaY);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ServiceResultItem] Failed to relay WebView wheel boundary: {ex.Message}");
        }
    }

    private void OnResultContentScrollViewerPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ScrollViewer innerScrollViewer)
        {
            return;
        }

        var delta = e.GetCurrentPoint(innerScrollViewer).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        var atTop = innerScrollViewer.VerticalOffset <= 0;
        var atBottom = innerScrollViewer.VerticalOffset >= innerScrollViewer.ScrollableHeight;
        var shouldBubbleToOuter = (delta > 0 && atTop) || (delta < 0 && atBottom);
        if (!shouldBubbleToOuter)
        {
            return;
        }

        var offsetDelta = -delta;
        e.Handled = TryScrollViewerChain(FindAncestorScrollViewer(innerScrollViewer), offsetDelta);
    }

    private static async Task<string> MeasureDictionaryHeightAsync(WebView2 sender)
    {
        return await sender.CoreWebView2.ExecuteScriptAsync(
            """
            (() => {
                const root = document.documentElement;
                const body = document.body;
                if (!root || !body) {
                    return "0";
                }

                return Math.max(
                    body.scrollHeight,
                    body.offsetHeight,
                    root.scrollHeight,
                    root.offsetHeight
                ).toString();
            })()
            """);
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject? start)
    {
        var current = VisualTreeHelper.GetParent(start);
        while (current != null)
        {
            if (current is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static bool TryScrollViewerChain(ScrollViewer? startScrollViewer, double offsetDelta)
    {
        if (startScrollViewer == null || Math.Abs(offsetDelta) < double.Epsilon)
        {
            return false;
        }

        var currentScrollViewer = startScrollViewer;
        while (currentScrollViewer != null)
        {
            var targetOffset = Math.Clamp(
                currentScrollViewer.VerticalOffset + offsetDelta,
                0,
                currentScrollViewer.ScrollableHeight);

            if (Math.Abs(targetOffset - currentScrollViewer.VerticalOffset) > 0.5)
            {
                currentScrollViewer.ChangeView(null, targetOffset, null, disableAnimation: true);
                return true;
            }

            currentScrollViewer = FindAncestorScrollViewer(currentScrollViewer);
        }

        return false;
    }

    private static string GetMimeType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".eot" => "application/vnd.ms-fontobject",
            ".htm" or ".html" => "text/html",
            _ => "application/octet-stream"
        };
    }
}
