using System.Text.Json;

namespace Easydict.TranslationService.Models;

public sealed partial class AiRefinementDemoState
{
    private readonly CancellationTokenSource _liveLifetime = new();
    private Dictionary<string, string> _originalWords = new(StringComparer.Ordinal);
    private IContextTranslationService? _contextService;
    private IWordRefinementService? _wordService;
    private Language _targetLanguage;
    private string? _refinementError;
    private bool _isApplyingContext;
    private bool _isApplyingWord;

    public bool IsLive { get; private init; }
    public string? RefinementError { get => _refinementError; private set => SetField(ref _refinementError, value); }
    public bool IsApplyingContext
    {
        get => _isApplyingContext;
        private set { if (SetField(ref _isApplyingContext, value)) OnPropertyChanged(nameof(IsRefining)); }
    }
    public bool IsRefining => IsApplyingContext || _isApplyingWord;

    public async Task<bool> ApplyCandidateAsync(string wordId, string candidate)
    {
        if (!IsLive) return ApplyCandidate(wordId, candidate);
        ThrowIfDisposed();
        if (!CanShowWordRefinement || IsRefining || SelectedWordId != wordId
            || !SelectedCandidates.Contains(candidate, StringComparer.Ordinal)) return false;
        var word = Words.FirstOrDefault(w => w.Id == wordId);
        if (word is null) return false;
        var start = Words.TakeWhile(w => w.Id != wordId).Sum(w => w.Text.Length);
        var request = new WordReplacementRequest(CreateLiveRequest(AppliedContext), start, word.Text.Length, candidate);
        var prefix = TranslationText[..start] + candidate;
        var previousCandidates = new Dictionary<string, IReadOnlyList<string>>(_candidateCache, StringComparer.Ordinal);
        InvalidateCandidates();
        var generation = _generation;
        _isApplyingWord = true;
        OnPropertyChanged(nameof(IsRefining));
        RefinementError = null;
        var applied = false;
        try
        {
            var token = _liveLifetime.Token;
            var text = await Task.Run(() => _wordService!.ApplyWordAlternativeAsync(request, token), token);
            if (_disposed || generation != _generation || !CanShowWordRefinement) return false;
            if (string.IsNullOrEmpty(text) || !text.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidDataException("The service did not preserve the selected wording.");
            _originalWords.Clear();
            _confirmedChoices.Clear();
            Words = [new("translation", text, false)];
            applied = true;
        }
        catch (OperationCanceledException) when (_disposed || _liveLifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!_disposed && generation == _generation) RefinementError = ex.Message; }
        finally
        {
            if (!applied && !_disposed && generation == _generation && CanShowWordRefinement)
            {
                foreach (var pair in previousCandidates) _candidateCache[pair.Key] = pair.Value;
            }
            _isApplyingWord = false;
            if (!_disposed) OnPropertyChanged(nameof(IsRefining));
        }
        if (applied && !_disposed && generation == _generation && CanShowWordRefinement) await PrefetchCandidatesAsync();
        return applied;
    }

    public static AiRefinementDemoState? CreateLive(
        ITranslationService service, TranslationResult result, bool contextEnabled, bool wordsEnabled)
    {
        var context = service.ContextTranslation;
        var words = service.WordRefinement;
        if ((!contextEnabled || context is null) && (!wordsEnabled || words is null)) return null;
        var state = new AiRefinementDemoState(result.OriginalText, true, contextEnabled, wordsEnabled)
        {
            IsLive = true,
            _contextService = context,
            _wordService = words,
            _targetLanguage = result.TargetLanguage
        };
        state.Words = [new("translation", result.TranslatedText, false)];
        return state;
    }

    private TranslationRefinementRequest CreateLiveRequest(string context) =>
        new(SourceText, TranslationText, _targetLanguage, context);

    public async Task ApplyContextAsync(string? context)
    {
        if (!IsLive) { ApplyContext(context); return; }
        ThrowIfDisposed();
        if (!CanShowContext || IsRefining) return;
        InvalidateCandidates();
        var generation = _generation;
        var revision = (context ?? string.Empty).Trim();
        IsApplyingContext = true;
        RefinementError = null;
        try
        {
            // Explicitly include confirmed choices when asking for a contextual revision.
            var instructions = revision;
            if (_confirmedChoices.Count > 0)
                instructions += "\nPreserve these user-confirmed word choices where possible: " + JsonSerializer.Serialize(_confirmedChoices.Values);
            var request = CreateLiveRequest(instructions);
            var token = _liveLifetime.Token;
            var text = await Task.Run(() => _contextService!.TranslateWithContextAsync(request, token), token);
            if (_disposed || generation != _generation || !CanShowContext) return;
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("The service returned an empty translation.");
            AppliedContext = revision;
            DraftContext = revision;
            IsContextEditorExpanded = false;
            _originalWords.Clear();
            _confirmedChoices.Clear();
            Words = [new("translation", text, false)];
            OnPropertyChanged(nameof(ContextRevision));
        }
        catch (OperationCanceledException) when (_disposed || _liveLifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!_disposed && generation == _generation) RefinementError = ex.Message; }
        finally { if (!_disposed) IsApplyingContext = false; }
        if (!_disposed && generation == _generation && CanShowWordRefinement && RefinementError is null) await PrefetchCandidatesAsync();
    }

    private async Task LoadLiveCandidatesAsync()
    {
        CancelCandidateLoad();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_liveLifetime.Token);
        _candidateLoadCancellation = cancellation;
        var generation = _generation;
        var translation = TranslationText;
        IsCandidatePrefetching = true;
        RefinementError = null;
        try
        {
            var request = CreateLiveRequest(AppliedContext);
            // Fetch, enumerate, validate and normalize the complete response off the UI thread.
            var prepared = await Task.Run(async () =>
            {
                var segments = await _wordService!.GetWordAlternativesAsync(request, cancellation.Token).ConfigureAwait(false);
                return PrepareCandidates(segments, translation, cancellation.Token);
            }, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || generation != _generation || !CanShowWordRefinement || translation != TranslationText) return;
            // Publish prepared data atomically; no enumeration or copying on the calling thread.
            _candidateCache = prepared.Candidates;
            _originalWords = prepared.Originals;
            SetWords(prepared.Words, translation);
            DismissSelectedWord();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { if (!_disposed && generation == _generation && !cancellation.IsCancellationRequested) RefinementError = ex.Message; }
        finally
        {
            if (ReferenceEquals(_candidateLoadCancellation, cancellation))
            {
                _candidateLoadCancellation = null;
                IsCandidatePrefetching = false;
            }
        }
    }

    private sealed record PreparedCandidates(
        IReadOnlyList<AiRefinementDemoWord> Words,
        Dictionary<string, IReadOnlyList<string>> Candidates,
        Dictionary<string, string> Originals);

    private static PreparedCandidates PrepareCandidates(IReadOnlyList<WordAlternatives> segments, string translation, CancellationToken cancellationToken)
    {
        if (segments.Count == 0) throw new InvalidDataException("Word alternatives did not match the translation.");
        var words = new List<AiRefinementDemoWord>(segments.Count);
        var candidates = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var originals = new Dictionary<string, string>(StringComparer.Ordinal);
        var offset = 0;
        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (segment is null || string.IsNullOrEmpty(segment.Text) || segment.Alternatives is null
                || offset > translation.Length - segment.Text.Length
                || !translation.AsSpan(offset, segment.Text.Length).SequenceEqual(segment.Text.AsSpan()))
                throw new InvalidDataException("Word alternatives did not match the translation.");
            var id = words.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var alternatives = segment.Alternatives.Where(a => !string.IsNullOrWhiteSpace(a) && a != segment.Text)
                .Distinct(StringComparer.Ordinal).Take(3).ToArray();
            originals[id] = segment.Text;
            candidates[id] = alternatives;
            words.Add(new(id, segment.Text, alternatives.Length > 0));
            offset += segment.Text.Length;
        }
        if (offset != translation.Length) throw new InvalidDataException("Word alternatives did not match the translation.");
        return new(words.ToArray(), candidates, originals);
    }
}
