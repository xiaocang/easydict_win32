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
    private bool _isDisposed;

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
                return;

            _mediaPlayer?.Dispose();
            _mediaPlayer = new MediaPlayer
            {
                Source = MediaSource.CreateFromStream(stream, stream.ContentType),
                AutoPlay = true
            };
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
        }
    }

    private static VoiceInformation? FindVoiceForLanguage(Language language)
    {
        var bcp47 = LanguageToBcp47(language);
        if (string.IsNullOrEmpty(bcp47))
            return null;

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

    private static string LanguageToBcp47(Language language) => language switch
    {
        Language.SimplifiedChinese => "zh-CN",
        Language.TraditionalChinese => "zh-TW",
        Language.English => "en-US",
        Language.Japanese => "ja-JP",
        Language.Korean => "ko-KR",
        Language.French => "fr-FR",
        Language.Spanish => "es-ES",
        Language.Portuguese => "pt-BR",
        Language.Italian => "it-IT",
        Language.German => "de-DE",
        Language.Russian => "ru-RU",
        Language.Arabic => "ar-SA",
        Language.Swedish => "sv-SE",
        Language.Romanian => "ro-RO",
        Language.Thai => "th-TH",
        Language.Dutch => "nl-NL",
        Language.Hungarian => "hu-HU",
        Language.Greek => "el-GR",
        Language.Danish => "da-DK",
        Language.Finnish => "fi-FI",
        Language.Polish => "pl-PL",
        Language.Czech => "cs-CZ",
        Language.Turkish => "tr-TR",
        Language.Ukrainian => "uk-UA",
        Language.Bulgarian => "bg-BG",
        Language.Indonesian => "id-ID",
        Language.Malay => "ms-MY",
        Language.Vietnamese => "vi-VN",
        Language.Persian => "fa-IR",
        Language.Hindi => "hi-IN",
        Language.Telugu => "te-IN",
        Language.Tamil => "ta-IN",
        Language.Bengali => "bn-IN",
        Language.Norwegian => "nb-NO",
        Language.Hebrew => "he-IL",
        Language.Slovak => "sk-SK",
        Language.Slovenian => "sl-SI",
        Language.Estonian => "et-EE",
        Language.Latvian => "lv-LV",
        Language.Lithuanian => "lt-LT",
        Language.Filipino => "fil-PH",
        _ => "en-US"
    };

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _mediaPlayer?.Dispose();
        _synthesizer.Dispose();
    }
}
