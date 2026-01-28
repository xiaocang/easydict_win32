using System.Diagnostics;
using Windows.Media.SpeechSynthesis;
using Windows.Media.Playback;
using Windows.Media.Core;
using Easydict.TranslationService.Models;

namespace Easydict.WinUI.Services;

/// <summary>
/// Text-to-Speech service using Windows Speech Synthesis API.
/// Supports language-specific voice selection and playback control.
/// </summary>
public sealed class TextToSpeechService : IDisposable
{
    private static readonly Lazy<TextToSpeechService> _instance =
        new(() => new TextToSpeechService(), LazyThreadSafetyMode.PublicationOnly);

    public static TextToSpeechService Instance => _instance.Value;

    private readonly SpeechSynthesizer _synthesizer;
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
    /// Speak the given text using an appropriate voice for the language.
    /// Stops any currently playing audio before starting.
    /// </summary>
    public async Task SpeakAsync(string text, Language language, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        Stop();

        try
        {
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
                Source = MediaSource.CreateFromStream(stream, stream.ContentType),
                AutoPlay = true
            };
            _mediaPlayer.MediaEnded += OnMediaEnded;
        }
        catch (OperationCanceledException)
        {
            // Expected when cancelled
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TTS] Error: {ex.Message}");
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

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        PlaybackEnded?.Invoke();
    }

    private void CleanupPlayback()
    {
        if (_mediaPlayer != null)
        {
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
        var voices = SpeechSynthesizer.AllVoices;

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
