using System.Diagnostics;
using Easydict.TranslationService.Models;
using Windows.Media.SpeechSynthesis;
using Windows.Media.Playback;
using Windows.Media.Core;

namespace Easydict.WinUI.Services;

/// <summary>
/// Text-to-Speech service using Windows Speech Synthesis API.
/// Supports language-specific voice selection and playback control.
/// Also supports SAPI 5 voices via System.Speech for extended voice options.
/// </summary>
public sealed class TextToSpeechService : IDisposable
{
    private static readonly Lazy<TextToSpeechService> _instance =
        new(() => new TextToSpeechService(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static TextToSpeechService Instance => _instance.Value;

    /// <summary>
    /// Stops playback only if the singleton has already been created.
    /// </summary>
    public static void StopIfInitialized()
    {
        if (!_instance.IsValueCreated)
        {
            return;
        }

        _instance.Value.Stop();
    }

    public record TtsVoiceEntry(
        string Id,
        string DisplayName,
        string Language,
        bool IsNeural,
        bool IsSapi5
    );

    private readonly SpeechSynthesizer _synthesizer;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private static IReadOnlyList<TtsVoiceEntry>? _cachedVoices;
    private MediaPlayer? _mediaPlayer;
    private SpeechSynthesisStream? _currentStream;
    private volatile System.Speech.Synthesis.SpeechSynthesizer? _activeSapiSynth;
    private volatile bool _isSapiPlaying;
    private CancellationTokenSource? _activeSpeakCts;
    private bool _isDisposed;

    /// <summary>
    /// Raised on the caller's thread when playback finishes or is stopped.
    /// </summary>
    public event Action? PlaybackEnded;

    /// <summary>
    /// Whether audio is currently playing.
    /// </summary>
    public bool IsPlaying => (_mediaPlayer?.PlaybackSession?.PlaybackState == MediaPlaybackState.Playing) || _isSapiPlaying;

    private TextToSpeechService()
    {
        _synthesizer = new SpeechSynthesizer();
    }

    /// <summary>
    /// Gets all available voices (cached if already enumerated).
    /// </summary>
    public IReadOnlyList<TtsVoiceEntry> GetAllVoices()
    {
        return _cachedVoices ??= GetAllVoicesIncludingSapi5();
    }

    /// <summary>
    /// Pre-warms the TTS engine by forcing voice enumeration.
    /// Call from a background thread at app startup to avoid first-use delay.
    /// </summary>
    public void WarmUp()
    {
        Debug.WriteLine("[TTS] Pre-warming: enumerating voices...");
        _cachedVoices = GetAllVoicesIncludingSapi5();
        Debug.WriteLine($"[TTS] Pre-warm complete: {_cachedVoices.Count} voices found");
    }

    /// <summary>
    /// Speak the given text using an appropriate voice for the language.
    /// Stops any currently playing audio before starting.
    /// </summary>
    public async Task SpeakAsync(string text, Language language, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var newCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var oldCts = Interlocked.Exchange(ref _activeSpeakCts, newCts);
        if (oldCts != null)
        {
            try
            {
                oldCts.Cancel();
                oldCts.Dispose();
            }
            catch (ObjectDisposedException) { }
        }

        var ct = newCts.Token;

        if (_semaphore.CurrentCount == 0 && !IsPlaying && _activeSapiSynth == null)
        {
            Debug.WriteLine("[TTS] Recovering from orphaned semaphore lock");
            try { _semaphore.Release(); } catch { }
        }

        await _semaphore.WaitAsync(ct);
        try
        {
            Stop();

            var voice = FindVoiceForLanguage(language);
            if (voice != null)
            {
                if (voice.IsSapi5)
                {
                    var sapi = new System.Speech.Synthesis.SpeechSynthesizer();
                    _activeSapiSynth = sapi;

                    sapi.SelectVoice(voice.DisplayName);
                    sapi.SetOutputToDefaultAudioDevice();

                    var tcs = new TaskCompletionSource<bool>();

                    sapi.SpeakCompleted += (s, e) =>
                    {
                        _isSapiPlaying = false;
                        if (e.Error != null) tcs.TrySetException(e.Error);
                        else if (e.Cancelled) tcs.TrySetCanceled();
                        else tcs.TrySetResult(true);

                        PlaybackEnded?.Invoke();
                        _activeSapiSynth = null;
                        sapi.Dispose();
                    };

                    using var reg = ct.Register(() =>
                    {
                        try { sapi.SpeakAsyncCancelAll(); } catch { }
                    });

                    sapi.Rate = Math.Clamp(
                        (int)Math.Round((SettingsService.Instance.TtsSpeed - 1.0) * 10.0 / 2.0),
                        -10, 10);

                    _isSapiPlaying = true;
                    sapi.SpeakAsync(text);
                    await tcs.Task;
                    return;
                }

                var winRtVoice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == voice.Id);
                if (winRtVoice != null)
                {
                    _synthesizer.Voice = winRtVoice;
                }
            }

            _synthesizer.Options.SpeakingRate = Math.Clamp(SettingsService.Instance.TtsSpeed, 0.5, 3.0);

            Debug.WriteLine($"[TTS] Speaking in {language} with voice: {_synthesizer.Voice.DisplayName}");

            var stream = await _synthesizer.SynthesizeTextToStreamAsync(text).AsTask(ct);

            if (ct.IsCancellationRequested)
            {
                stream.Dispose();
                return;
            }

            CleanupPlayback();

            _currentStream = stream;
            _mediaPlayer = new MediaPlayer
            {
                AutoPlay = false
            };
            _mediaPlayer.MediaOpened += OnMediaOpened;
            _mediaPlayer.MediaEnded += OnMediaEnded;
            _mediaPlayer.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
        }
        catch (OperationCanceledException)
        {
            _isSapiPlaying = false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TTS] Error in SpeakAsync: {ex}");
            _isSapiPlaying = false;
        }
        finally
        {
            _semaphore.Release();
            Interlocked.CompareExchange(ref _activeSpeakCts, null, newCts);
        }
    }

    /// <summary>
    /// Stop any currently playing audio.
    /// </summary>
    public void Stop()
    {
        _isSapiPlaying = false;
        _activeSapiSynth?.SpeakAsyncCancelAll();
        _activeSapiSynth?.Dispose();
        _activeSapiSynth = null;

        if (_mediaPlayer is { PlaybackSession.PlaybackState: MediaPlaybackState.Playing })
        {
            _mediaPlayer.Pause();
            PlaybackEnded?.Invoke();
        }
    }

    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        Debug.WriteLine("[TTS] Media opened, starting playback");
        sender.Play();
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        PlaybackEnded?.Invoke();
    }

    private void CleanupPlayback()
    {
        if (_mediaPlayer != null)
        {
            _mediaPlayer.MediaOpened -= OnMediaOpened;
            _mediaPlayer.MediaEnded -= OnMediaEnded;
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
        }

        _currentStream?.Dispose();
        _currentStream = null;
    }

    /// <summary>
    /// Refreshes the cached list of available voices.
    /// </summary>
    public IReadOnlyList<TtsVoiceEntry> RefreshVoices()
    {
        _cachedVoices = GetAllVoicesIncludingSapi5();
        return _cachedVoices;
    }

    private IReadOnlyList<TtsVoiceEntry> GetAllVoicesIncludingSapi5()
    {
        var entries = new List<TtsVoiceEntry>();

        foreach (var v in SpeechSynthesizer.AllVoices)
        {
            bool isNeural = v.DisplayName.Contains("Natural", StringComparison.OrdinalIgnoreCase) ||
                            v.DisplayName.Contains("Neural", StringComparison.OrdinalIgnoreCase) ||
                            v.DisplayName.Contains("Online", StringComparison.OrdinalIgnoreCase);

            entries.Add(new TtsVoiceEntry(v.Id, v.DisplayName, v.Language, isNeural, false));
        }

        try
        {
            using var sapiEnum = new System.Speech.Synthesis.SpeechSynthesizer();
            var sapiVoices = sapiEnum.GetInstalledVoices()
                .Where(v => v.Enabled)
                .Select(v => v.VoiceInfo);

            foreach (var v in sapiVoices)
            {
                if (!entries.Any(e => string.Equals(e.DisplayName, v.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    bool isNeural = v.Name.Contains("Natural", StringComparison.OrdinalIgnoreCase) ||
                                    v.Name.Contains("Neural", StringComparison.OrdinalIgnoreCase) ||
                                    v.Name.Contains("Online", StringComparison.OrdinalIgnoreCase);

                    entries.Add(new TtsVoiceEntry(v.Name, v.Name, v.Culture.Name, isNeural, true));
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TTS] Error enumerating SAPI 5 voices: {ex}");
        }

        return entries;
    }

    private static TtsVoiceEntry? FindVoiceForLanguage(Language language)
    {
        var voices = Instance.GetAllVoices();

        var selectedVoiceId = SettingsService.Instance.SelectedTtsVoiceId;
        if (!string.IsNullOrEmpty(selectedVoiceId))
        {
            var selectedVoice = voices.FirstOrDefault(v => v.Id == selectedVoiceId)
                             ?? voices.FirstOrDefault(v => v.DisplayName == selectedVoiceId);

            if (selectedVoice != null)
            {
                return selectedVoice;
            }
        }

        var bcp47 = language.ToBcp47();

        var exactMatch = voices.FirstOrDefault(v =>
            v.Language.Equals(bcp47, StringComparison.OrdinalIgnoreCase));
        if (exactMatch != null)
            return exactMatch;

        var prefix = bcp47.Split('-')[0];
        return voices.FirstOrDefault(v =>
            v.Language.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _activeSapiSynth?.SpeakAsyncCancelAll();
        _activeSapiSynth?.Dispose();
        _activeSapiSynth = null;

        CleanupPlayback();
        _synthesizer.Dispose();
    }
}
