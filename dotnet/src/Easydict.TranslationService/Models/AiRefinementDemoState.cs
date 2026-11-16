using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Easydict.TranslationService.Models;

/// <summary>
/// Shared Labs refinement interaction state. The preview constructor uses local preset data;
/// CreateLive binds a finished translation to the provider's refinement capabilities.
/// </summary>
public sealed partial class AiRefinementDemoState : INotifyPropertyChanged, IDisposable
{
    private readonly object _candidateGate = new();
    private readonly Dictionary<string, string> _confirmedChoices = new(StringComparer.Ordinal);
    private Dictionary<string, IReadOnlyList<string>> _candidateCache = new(StringComparer.Ordinal);
    private CancellationTokenSource? _candidateLoadCancellation;
    private IReadOnlyList<AiRefinementDemoWord> _words = Array.Empty<AiRefinementDemoWord>();
    private string _translationText = string.Empty;
    private bool _supportsAiExtensions;
    private bool _contextFeatureEnabled;
    private bool _wordRefinementEnabled;
    private string _draftContext = string.Empty;
    private string _appliedContext = string.Empty;
    private AiRefinementDemoContextConflict? _contextConflict;
    private bool _isContextEditorExpanded;
    private bool _isCandidatePrefetching;
    private bool _isSelectedWordLoading;
    private string? _selectedWordId;
    private IReadOnlyList<string> _selectedCandidates = Array.Empty<string>();
    private int _generation;
    private bool _disposed;

    public AiRefinementDemoState(
        string sourceText,
        bool supportsAiExtensions,
        bool contextFeatureEnabled,
        bool wordRefinementEnabled)
    {
        SourceText = sourceText ?? string.Empty;
        _supportsAiExtensions = supportsAiExtensions;
        _contextFeatureEnabled = contextFeatureEnabled;
        _wordRefinementEnabled = wordRefinementEnabled;
        Words = CreateWords(_appliedContext);
    }

    public string SourceText { get; }

    public bool SupportsAiExtensions
    {
        get => _supportsAiExtensions;
        set
        {
            if (!SetField(ref _supportsAiExtensions, value)) return;
            OnPropertyChanged(nameof(CanShowContext));
            OnPropertyChanged(nameof(CanShowWordRefinement));
            if (!value)
            {
                CancelCandidateLoad();
                lock (_candidateGate)
                {
                    _candidateCache.Clear();
                    IsCandidatePrefetching = false;
                    IsContextEditorExpanded = false;
                    SelectedWordId = null;
                    SelectedCandidates = Array.Empty<string>();
                    IsSelectedWordLoading = false;
                }
            }
        }
    }

    public bool ContextFeatureEnabled
    {
        get => _contextFeatureEnabled;
        set
        {
            if (SetField(ref _contextFeatureEnabled, value))
            {
                OnPropertyChanged(nameof(CanShowContext));
                if (!CanShowContext) IsContextEditorExpanded = false;
            }
        }
    }

    public bool WordRefinementEnabled
    {
        get => _wordRefinementEnabled;
        set
        {
            if (SetField(ref _wordRefinementEnabled, value))
            {
                OnPropertyChanged(nameof(CanShowWordRefinement));
                if (!CanShowWordRefinement)
                {
                    CancelCandidateLoad();
                    lock (_candidateGate)
                    {
                        _candidateCache.Clear();
                        IsCandidatePrefetching = false;
                        SelectedWordId = null;
                        SelectedCandidates = Array.Empty<string>();
                        IsSelectedWordLoading = false;
                    }
                }
            }
        }
    }

    public bool CanShowContext => SupportsAiExtensions && ContextFeatureEnabled && (!IsLive || _contextService is not null);
    public bool CanShowWordRefinement => SupportsAiExtensions && WordRefinementEnabled && (!IsLive || _wordService is not null);

    public string DraftContext
    {
        get => _draftContext;
        set => SetField(ref _draftContext, value ?? string.Empty);
    }

    public string AppliedContext
    {
        get => _appliedContext;
        private set
        {
            if (SetField(ref _appliedContext, value))
            {
                OnPropertyChanged(nameof(HasAppliedContext));
                OnPropertyChanged(nameof(ContextSummary));
            }
        }
    }

    public bool HasAppliedContext => !string.IsNullOrWhiteSpace(AppliedContext);

    public AiRefinementDemoContextConflict? ContextConflict
    {
        get => _contextConflict;
        private set
        {
            if (SetField(ref _contextConflict, value))
            {
                OnPropertyChanged(nameof(HasContextConflict));
            }
        }
    }

    public bool HasContextConflict => ContextConflict is not null;

    public string ContextSummary
    {
        get
        {
            var summary = string.Join(' ', AppliedContext.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return summary.Length <= 72 ? summary : summary[..69] + "…";
        }
    }

    public bool IsContextEditorExpanded
    {
        get => _isContextEditorExpanded;
        set => SetField(ref _isContextEditorExpanded, value);
    }

    public IReadOnlyList<AiRefinementDemoWord> Words
    {
        get => _words;
        private set => SetWords(value, string.Concat(value.Select(word => word.Text)));
    }

    public string TranslationText => _translationText;

    private void SetWords(IReadOnlyList<AiRefinementDemoWord> words, string translationText)
    {
        if (ReferenceEquals(_words, words)) return;
        var textChanged = !string.Equals(_translationText, translationText, StringComparison.Ordinal);
        _words = words;
        _translationText = translationText;
        OnPropertyChanged(nameof(Words));
        if (textChanged) OnPropertyChanged(nameof(TranslationText));
    }

    public bool IsCandidatePrefetching
    {
        get => _isCandidatePrefetching;
        private set => SetField(ref _isCandidatePrefetching, value);
    }

    public bool IsSelectedWordLoading
    {
        get => _isSelectedWordLoading;
        private set => SetField(ref _isSelectedWordLoading, value);
    }

    public string? SelectedWordId
    {
        get => _selectedWordId;
        private set
        {
            if (SetField(ref _selectedWordId, value))
            {
                OnPropertyChanged(nameof(SelectedWordText));
                OnPropertyChanged(nameof(HasSelectedWord));
            }
        }
    }

    public string SelectedWordText => Words.FirstOrDefault(word => word.Id == SelectedWordId)?.Text ?? string.Empty;
    public bool HasSelectedWord => SelectedWordId is not null;

    public IReadOnlyList<string> SelectedCandidates
    {
        get => _selectedCandidates;
        private set => SetField(ref _selectedCandidates, value);
    }

    public int ContextRevision
    {
        get { lock (_candidateGate) return _generation; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void OpenContextEditor()
    {
        DraftContext = AppliedContext;
        IsContextEditorExpanded = true;
    }

    public void CloseContextEditor() => IsContextEditorExpanded = false;

    /// <summary>Apply a local demo context, invalidate stale candidates, and retain confirmed choices.</summary>
    public int ApplyContext(string? context)
    {
        ThrowIfDisposed();
        if (IsLive) throw new InvalidOperationException("Use ApplyContextAsync for live translation.");
        AppliedContext = (context ?? string.Empty).Trim();
        DraftContext = AppliedContext;
        IsContextEditorExpanded = false;
        InvalidateCandidates();
        Words = CreateWords(AppliedContext);
        RefreshContextConflict();
        OnPropertyChanged(nameof(ContextRevision));
        return ContextRevision;
    }

    /// <summary>
    /// Simulate a batched candidate prefetch with fixed local data. The generation check prevents an
    /// older context's delayed result from overwriting the current cache.
    /// </summary>
    public async Task PrefetchCandidatesAsync()
    {
        ThrowIfDisposed();
        if (!CanShowWordRefinement) return;
        if (IsLive)
        {
            await LoadLiveCandidatesAsync();
            return;
        }

        CancelCandidateLoad();
        var cancellation = new CancellationTokenSource();
        int generation;
        lock (_candidateGate)
        {
            _candidateLoadCancellation = cancellation;
            generation = _generation;
            _candidateCache.Clear();
            IsCandidatePrefetching = true;
            SelectedCandidates = Array.Empty<string>();
        }

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(420), cancellation.Token).ConfigureAwait(false);
            lock (_candidateGate)
            {
                if (generation != _generation || cancellation.IsCancellationRequested || _disposed) return;

                // The batch contains validated word-position matches. The final token deliberately has
                // no prefetched entry so the UI can demonstrate its local on-demand fallback.
                foreach (var word in Words.Where(word => word.IsInteractive && word.Id != "growth"))
                {
                    _candidateCache[word.Id] = GetPresetCandidates(word.Id, AppliedContext);
                }

                IsCandidatePrefetching = false;
            }
            if (SelectedWordId is not null)
            {
                await LoadSelectedWordCandidatesAsync(SelectedWordId, generation, cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer context or disabled feature owns the current state.
        }
        finally
        {
            lock (_candidateGate)
            {
                if (ReferenceEquals(_candidateLoadCancellation, cancellation))
                {
                    _candidateLoadCancellation = null;
                    IsCandidatePrefetching = false;
                }
            }
            cancellation.Dispose();
        }
    }

    public async Task SelectWordAsync(string? wordId)
    {
        ThrowIfDisposed();
        if (!CanShowWordRefinement || string.IsNullOrWhiteSpace(wordId)
            || Words.FirstOrDefault(word => word.Id == wordId) is not { IsInteractive: true })
        {
            return;
        }

        int generation;
        lock (_candidateGate)
        {
            SelectedWordId = wordId;
            if (_candidateCache.TryGetValue(wordId, out var cached))
            {
                SelectedCandidates = cached;
                IsSelectedWordLoading = false;
                return;
            }

            generation = _generation;
            IsSelectedWordLoading = true;
            SelectedCandidates = Array.Empty<string>();
        }
        if (IsLive)
        {
            IsSelectedWordLoading = false;
            return;
        }
        using var cancellation = new CancellationTokenSource();
        try
        {
            await LoadSelectedWordCandidatesAsync(wordId, generation, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Selection changed while the local fallback was loading.
        }
    }

    public bool ApplyCandidate(string? wordId, string? candidate)
    {
        ThrowIfDisposed();
        if (IsLive) throw new InvalidOperationException("Use ApplyCandidateAsync for live translation.");
        if (string.IsNullOrWhiteSpace(wordId) || string.IsNullOrWhiteSpace(candidate)) return false;

        lock (_candidateGate)
        {
            if (SelectedWordId != wordId || !SelectedCandidates.Contains(candidate, StringComparer.Ordinal))
            {
                return false;
            }

            _confirmedChoices[wordId] = candidate;
            Words = CreateWords(AppliedContext);
            SelectedWordId = wordId;
            SelectedCandidates = _candidateCache.TryGetValue(wordId, out var candidates)
                ? candidates
                : SelectedCandidates;
            RefreshContextConflict();
            return true;
        }
    }

    public void KeepOriginalWord()
    {
        ThrowIfDisposed();
        lock (_candidateGate)
        {
            var wordId = SelectedWordId;
            if (wordId is null) return;

            _confirmedChoices.Remove(wordId);
            Words = IsLive
                ? Words.Select(w => w.Id == wordId && _originalWords.TryGetValue(wordId, out var original) ? w with { Text = original } : w).ToArray()
                : CreateWords(AppliedContext);
            SelectedWordId = wordId;
            SelectedCandidates = _candidateCache.TryGetValue(wordId, out var candidates)
                ? candidates
                : SelectedCandidates;
            RefreshContextConflict();
        }
    }

    public bool ResolveContextConflict(bool useSuggestedWording)
    {
        ThrowIfDisposed();
        lock (_candidateGate)
        {
            if (ContextConflict is not { } conflict) return false;
            if (useSuggestedWording)
            {
                _confirmedChoices[conflict.WordId] = conflict.SuggestedWording;
            }

            ContextConflict = null;
            Words = CreateWords(AppliedContext);
            return true;
        }
    }

    public void DismissSelectedWord()
    {
        lock (_candidateGate)
        {
            SelectedWordId = null;
            SelectedCandidates = Array.Empty<string>();
            IsSelectedWordLoading = false;
        }
    }

    private async Task LoadSelectedWordCandidatesAsync(
        string wordId,
        int generation,
        CancellationToken cancellationToken)
    {
        IsSelectedWordLoading = true;
        await Task.Delay(TimeSpan.FromMilliseconds(260), cancellationToken).ConfigureAwait(false);
        lock (_candidateGate)
        {
            if (generation != _generation || cancellationToken.IsCancellationRequested || _disposed
                || SelectedWordId != wordId)
            {
                return;
            }

            if (!_candidateCache.TryGetValue(wordId, out var candidates))
            {
                candidates = GetPresetCandidates(wordId, AppliedContext);
                _candidateCache[wordId] = candidates;
            }

            SelectedCandidates = candidates;
            IsSelectedWordLoading = false;
        }
    }

    private void RefreshContextConflict()
    {
        if (IsLive) { ContextConflict = null; return; }
        var conflict = GetContextConflict(AppliedContext, _confirmedChoices);
        ContextConflict = conflict;
    }

    private static AiRefinementDemoContextConflict? GetContextConflict(
        string context,
        IReadOnlyDictionary<string, string> confirmedChoices)
    {
        var isFormal = IsFormalToneRequested(context);
        if (!isFormal || !confirmedChoices.TryGetValue("growth", out var wording)) return null;

        // Deterministic offline example: expressive growth wording conflicts with a formal tone.
        return wording is "蓬勃发展" or "茁壮成长"
            ? new AiRefinementDemoContextConflict("growth", wording, "稳步发展")
            : null;
    }

    private static bool IsFormalToneRequested(string context)
    {
        if (string.IsNullOrWhiteSpace(context)) return false;

        var lowerContext = context.ToLowerInvariant();
        if (lowerContext.Contains("informal", StringComparison.Ordinal)
            || lowerContext.Contains("non-formal", StringComparison.Ordinal)
            || lowerContext.Contains("nonformal", StringComparison.Ordinal)
            || lowerContext.Contains("not formal", StringComparison.Ordinal)
            || lowerContext.Contains("avoid formal", StringComparison.Ordinal)
            || lowerContext.Contains("don't use formal", StringComparison.Ordinal)
            || lowerContext.Contains("do not use formal", StringComparison.Ordinal)
            || lowerContext.Contains("no formal", StringComparison.Ordinal)
            || context.Contains("非正式", StringComparison.Ordinal)
            || context.Contains("不正式", StringComparison.Ordinal)
            || context.Contains("不要正式", StringComparison.Ordinal)
            || context.Contains("避免正式", StringComparison.Ordinal)
            || context.Contains("不要用正式", StringComparison.Ordinal)
            || context.Contains("不使用正式", StringComparison.Ordinal))
        {
            return false;
        }

        return Regex.IsMatch(
                context,
                @"\bformal\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || context.Contains("正式", StringComparison.Ordinal);
    }

    private void InvalidateCandidates()
    {
        CancelCandidateLoad();
        lock (_candidateGate)
        {
            _generation++;
            _candidateCache.Clear();
            IsCandidatePrefetching = false;
            IsSelectedWordLoading = false;
            SelectedWordId = null;
            SelectedCandidates = Array.Empty<string>();
        }
    }

    private void CancelCandidateLoad()
    {
        CancellationTokenSource? cancellation;
        lock (_candidateGate)
        {
            cancellation = _candidateLoadCancellation;
            _candidateLoadCancellation = null;
        }
        if (cancellation is null) return;
        _ = CancelWithoutBlockingAsync(cancellation);
    }

    private static async Task CancelWithoutBlockingAsync(CancellationTokenSource cancellation, bool dispose = false)
    {
        try
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (AggregateException)
        {
            System.Diagnostics.Debug.WriteLine("A refinement cancellation callback failed.");
        }
        finally
        {
            if (dispose) cancellation.Dispose();
        }
    }

    private IReadOnlyList<AiRefinementDemoWord> CreateWords(string context)
    {
        var formal = IsFormalToneRequested(context);
        var words = formal
            ? new (string Id, string Text, bool IsInteractive)[]
            {
                ("space", "宁静的工作空间", true), ("comma", "，", false),
                ("cause", "使", true), ("ideas", "创意", true),
                ("growth", "持续成长", true), ("period", "。", false)
            }
            : new (string Id, string Text, bool IsInteractive)[]
            {
                ("space", "安静的空间", true), ("comma", "，", false),
                ("cause", "让", true), ("ideas", "想法", true),
                ("growth", "自由生长", true), ("period", "。", false)
            };

        return words
            .Select(word => new AiRefinementDemoWord(
                word.Id,
                _confirmedChoices.TryGetValue(word.Id, out var selected) ? selected : word.Text,
                word.IsInteractive))
            .ToArray();
    }

    private static IReadOnlyList<string> GetPresetCandidates(string wordId, string context)
    {
        var formal = IsFormalToneRequested(context);
        return wordId switch
        {
            "space" => formal ? ["宁静的办公环境", "安静的工作场所"] : ["宁静的工作环境", "清静的空间"],
            "cause" => formal ? ["促使", "令"] : ["使", "令"],
            "ideas" => formal ? ["构想", "创意"] : ["灵感", "创意"],
            "growth" => formal ? ["稳步发展", "持续进步"] : ["蓬勃发展", "茁壮成长"],
            _ => Array.Empty<string>()
        };
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = CancelWithoutBlockingAsync(_liveLifetime, dispose: true);
        CancelCandidateLoad();
        PropertyChanged = null;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record AiRefinementDemoWord(string Id, string Text, bool IsInteractive);

public sealed record AiRefinementDemoContextConflict(
    string WordId,
    string ConfirmedWording,
    string SuggestedWording);
