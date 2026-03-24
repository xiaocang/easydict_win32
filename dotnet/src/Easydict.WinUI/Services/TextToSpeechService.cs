using System.Diagnostics;
using Easydict.TranslationService.Models;
using Windows.Media.SpeechSynthesis;
using Windows.Media.Playback;
using Windows.Media.Core;

namespace Easydict.WinUI.Services;

/// <summary>
/// Text-to-Speech service using Windows Speech Synthesis API.
/// Supports language-specific voice selection and playback control.
/// </summary>
public sealed class TextToSpeechService : IDisposable
{
    private static readonly Lazy<TextToSpeechService> _instance =
        new(() => new TextToSpeechService(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static TextToSpeechService Instance => _instance.Value;

    private readonly SpeechSynthesizer _synthesizer;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private static IReadOnlyList<VoiceInformation>? _cachedVoices;
    private MediaPlayer? _mediaPlayer;
    private SpeechSynthesisStream? _currentStream;
    private bool _isDisposed;

    /// <summary>
    /// Raised on the caller's thread when playback finishes or is stopped.
    /// </summary>
    public event Action? PlaybackEnded;

    /// <summary>
    /// Whether audio is currently playing.
    /// </summary>
    public bool IsPlaying => _mediaPlayer?.PlaybackSession?.PlaybackState == MediaPlaybackState.Playing;

    private TextToSpeechService()
    {
        _synthesizer = new SpeechSynthesizer();
    }

    /// <summary>
    /// Pre-warms the TTS engine by forcing voice enumeration.
    /// Call from a background thread at app startup to avoid first-use delay.
    /// </summary>
    public void WarmUp()
    {
        Debug.WriteLine("[TTS] Pre-warming: enumerating voices...");
        _cachedVoices = SpeechSynthesizer.AllVoices;
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

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            Stop();

            // Select a voice matching the target language
            var voice = FindVoiceForLanguage(language);
            if (voice != null)
            {
                _synthesizer.Voice = voice;
            }

            Debug.WriteLine($"[TTS] Speaking in {language} with voice: {_synthesizer.Voice.DisplayName}");

            var stream = await _synthesizer.SynthesizeTextToStreamAsync(text).AsTask(cancellationToken);

            if (cancellationToken.IsCancellationRequested)
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
            // Expected when cancelled
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TTS] Error: {ex.Message}");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Stop any currently playing audio.
    /// </summary>
    public void Stop()
    {
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

    private static VoiceInformation? FindVoiceForLanguage(Language language)
    {
        var bcp47 = language.ToBcp47();

        // Try exact match first, then prefix match
        var voices = _cachedVoices ?? SpeechSynthesizer.AllVoices;

        var exactMatch = voices.FirstOrDefault(v =>
            v.Language.Equals(bcp47, StringComparison.OrdinalIgnoreCase));
        if (exactMatch != null)
            return exactMatch;

        // Prefix match (e.g., "zh" matches "zh-CN")
        var prefix = bcp47.Split('-')[0];
        return voices.FirstOrDefault(v =>
            v.Language.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        CleanupPlayback();
        _synthesizer.Dispose();
    }
}
