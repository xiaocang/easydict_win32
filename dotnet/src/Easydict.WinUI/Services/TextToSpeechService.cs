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
    private readonly LatestSpeechRequestGate _requestGate;
    private readonly SapiPlaybackController _sapiPlayback;
    private readonly object _playbackLock = new();
    private static IReadOnlyList<TtsVoiceEntry>? _cachedVoices;
    private MediaPlayer? _mediaPlayer;
    private SpeechSynthesisStream? _currentStream;
    private TaskCompletionSource? _winRtPlaybackCompletion;
    private bool _isDisposed;


    private TextToSpeechService()
        : this(new LatestSpeechRequestGate())
    {
    }

    internal TextToSpeechService(LatestSpeechRequestGate requestGate)
    {
        _requestGate = requestGate ?? throw new ArgumentNullException(nameof(requestGate));
        _synthesizer = new SpeechSynthesizer();
        _sapiPlayback = new SapiPlaybackController(() => new SystemSapiSpeechSynthesizer());
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
    public Task SpeakAsync(
        string text,
        Language language,
        CancellationToken cancellationToken = default)
    {
        return SpeakCoreAsync(
            text,
            language,
            selectedVoiceIdOverride: null,
            speedOverride: null,
            cancellationToken);
    }

    internal Task SpeakPreviewAsync(
        string text,
        Language language,
        string selectedVoiceId,
        double speed,
        CancellationToken cancellationToken = default)
    {
        return SpeakCoreAsync(
            text,
            language,
            selectedVoiceId,
            speed,
            cancellationToken);
    }

    private async Task SpeakCoreAsync(
        string text,
        Language language,
        string? selectedVoiceIdOverride,
        double? speedOverride,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            await _requestGate.RunLatestAsync(async currentToken =>
            {

                var speed = speedOverride ?? SettingsService.Instance.TtsSpeed;
                var voice = FindVoiceForLanguage(language, selectedVoiceIdOverride);
                if (voice != null)
                {
                    if (voice.IsSapi5)
                    {
                        var rate = Math.Clamp(
                            (int)Math.Round((speed - 1.0) * 10.0 / 2.0),
                            -10, 10);
                        await _sapiPlayback.SpeakAsync(
                            voice.DisplayName,
                            text,
                            rate,
                            currentToken);
                        return;
                    }

                    var winRtVoice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == voice.Id);
                    if (winRtVoice != null)
                    {
                        _synthesizer.Voice = winRtVoice;
                    }
                }

                _synthesizer.Options.SpeakingRate = Math.Clamp(speed, 0.5, 3.0);

                Debug.WriteLine(
                    $"[TTS] Speaking in {language} with voice: {_synthesizer.Voice.DisplayName}");

                var stream = await _synthesizer
                    .SynthesizeTextToStreamAsync(text)
                    .AsTask(currentToken);

                if (currentToken.IsCancellationRequested)
                {
                    stream.Dispose();
                    return;
                }

                var (player, playbackCompletion) = PrepareWinRtPlayback(stream);
                try
                {
                    await playbackCompletion.WaitAsync(currentToken);
                }
                finally
                {
                    StopWinRtPlayback(player);
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected when this request is replaced or canceled.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TTS] Error in SpeakAsync: {ex}");
        }
    }


    /// <summary>
    /// Stops the active speech request and any published WinRT playback.
    /// </summary>
    public void Stop()
    {
        _requestGate.CancelActive();
        StopWinRtPlayback();
    }

    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        try
        {
            lock (_playbackLock)
            {
                if (!ReferenceEquals(sender, _mediaPlayer))
                {
                    return;
                }

                Debug.WriteLine("[TTS] Media opened, starting playback");
                sender.Play();
            }
        }
        catch (Exception ex)
        {
            CompleteWinRtPlaybackFailure(sender, ex);
        }
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        TaskCompletionSource? completion;
        lock (_playbackLock)
        {
            if (!ReferenceEquals(sender, _mediaPlayer))
            {
                return;
            }

            completion = _winRtPlaybackCompletion;
            _winRtPlaybackCompletion = null;
            CleanupPlaybackCore();
        }

        completion?.TrySetResult();
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        CompleteWinRtPlaybackFailure(
            sender,
            new InvalidOperationException($"Media playback failed: {args.ErrorMessage}"));
    }

    private void CompleteWinRtPlaybackFailure(MediaPlayer sender, Exception exception)
    {
        TaskCompletionSource? completion;
        lock (_playbackLock)
        {
            if (!ReferenceEquals(sender, _mediaPlayer))
            {
                return;
            }

            completion = _winRtPlaybackCompletion;
            _winRtPlaybackCompletion = null;
            CleanupPlaybackCore();
        }

        completion?.TrySetException(exception);
    }

    private (MediaPlayer Player, Task Completion) PrepareWinRtPlayback(
        SpeechSynthesisStream stream)
    {
        lock (_playbackLock)
        {
            CleanupPlaybackCore();

            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var player = new MediaPlayer
            {
                AutoPlay = false
            };

            _currentStream = stream;
            _winRtPlaybackCompletion = completion;
            _mediaPlayer = player;
            player.MediaOpened += OnMediaOpened;
            player.MediaEnded += OnMediaEnded;
            player.MediaFailed += OnMediaFailed;

            try
            {
                player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
                return (player, completion.Task);
            }
            catch
            {
                CleanupPlaybackCore();
                throw;
            }
        }
    }

    private void StopWinRtPlayback(MediaPlayer? expectedPlayer = null)
    {
        lock (_playbackLock)
        {
            if (_mediaPlayer == null ||
                (expectedPlayer != null && !ReferenceEquals(expectedPlayer, _mediaPlayer)))
            {
                return;
            }

            try
            {
                _mediaPlayer.Pause();
            }
            catch (ObjectDisposedException)
            {
                // Cleanup below still owns the current playback state.
            }

            CleanupPlaybackCore();
        }
    }

    private void CleanupPlayback()
    {
        lock (_playbackLock)
        {
            CleanupPlaybackCore();
        }
    }

    private void CleanupPlaybackCore()
    {
        var completion = _winRtPlaybackCompletion;
        _winRtPlaybackCompletion = null;

        if (_mediaPlayer != null)
        {
            _mediaPlayer.MediaOpened -= OnMediaOpened;
            _mediaPlayer.MediaEnded -= OnMediaEnded;
            _mediaPlayer.MediaFailed -= OnMediaFailed;
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
        }

        _currentStream?.Dispose();
        _currentStream = null;
        completion?.TrySetCanceled();
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

    private static TtsVoiceEntry? FindVoiceForLanguage(
        Language language,
        string? selectedVoiceIdOverride)
    {
        var voices = Instance.GetAllVoices();

        var selectedVoiceId =
            selectedVoiceIdOverride ?? SettingsService.Instance.SelectedTtsVoiceId;
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

        _requestGate.CancelActive();
        _sapiPlayback.Stop();
        CleanupPlayback();
        _synthesizer.Dispose();
    }
}
