using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Easydict.OpenVINO.Inference;
using Easydict.OpenVINO.Models;
using Easydict.TranslationService;
using Easydict.TranslationService.LocalModels;
using Easydict.TranslationService.Models;

namespace Easydict.OpenVINO.Services;

/// <summary>
/// Translation provider that runs NLLB-200-distilled-600M on Intel/AMD NPU
/// (or GPU/CPU) via ONNX Runtime + the OpenVINO Execution Provider.
///
/// Lifecycle:
///   1. App startup: service is registered, but no model files are loaded.
///   2. Settings page calls <see cref="GetStatus"/> → reports
///      <see cref="LocalModelState.NeedsPreparation"/> if the bundle isn't in
///      the cache.
///   3. User clicks "Download model" → <see cref="PrepareAsync"/> fetches the
///      ~360 MB INT8 NLLB-200 bundle into <c>%LOCALAPPDATA%\Easydict\models</c>.
///   4. First translation: lazy-loads the tokenizer + ONNX sessions; ~1-3s on
///      NPU (driver compile), &lt;100ms on subsequent calls.
/// </summary>
public sealed class OpenVINOTranslationService : IStreamTranslationService, ILocalModelProvider, IDisposable
{
    private const int DefaultMaxNewTokens = 256;

    private readonly ModelDownloadService _downloader;
    private readonly object _engineLock = new();
    private readonly SemaphoreSlim _prepareLock = new(1, 1);

    private INllbInferenceEngine? _engine;
    private INllbTokenizer? _tokenizer;
    private OpenVINODevice _device = OpenVINODevice.Auto;
    private bool _disposed;

    public OpenVINOTranslationService()
        : this(new ModelDownloadService())
    {
    }

    internal OpenVINOTranslationService(ModelDownloadService downloader)
    {
        _downloader = downloader;
    }

    /// <summary>
    /// Test seam: inject preloaded tokenizer + engine so the suite doesn't need
    /// the ~360 MB NLLB-200 bundle on disk or a real OpenVINO runtime.
    /// </summary>
    internal OpenVINOTranslationService(
        ModelDownloadService downloader,
        INllbTokenizer tokenizer,
        INllbInferenceEngine engine)
        : this(downloader)
    {
        _tokenizer = tokenizer;
        _engine = engine;
    }

    // ── Translation service contract ────────────────────────────────────

    public string ServiceId => "openvino-local-ai";

    public string DisplayName => "OpenVINO NPU (NLLB-200)";

    public bool RequiresApiKey => false;

    public bool IsConfigured => true;

    public bool IsStreaming => true;

    private static readonly IReadOnlyList<Language> _supportedLanguages =
        NllbLanguageCodes.SupportedLanguages.ToArray();

    public IReadOnlyList<Language> SupportedLanguages => _supportedLanguages;

    public bool SupportsLanguagePair(Language from, Language to)
    {
        if (to == Language.Auto) return false;
        if (NllbLanguageCodes.TryGetCode(to) is null) return false;
        if (from == Language.Auto) return true;
        return NllbLanguageCodes.TryGetCode(from) is not null;
    }

    public Task<Language> DetectLanguageAsync(string text, CancellationToken cancellationToken = default)
    {
        // NLLB doesn't have a built-in detector. Easydict's higher layer handles auto-detect
        // via separate detection service; we treat unspecified source as auto-detect implicitly.
        return Task.FromResult(Language.Auto);
    }

    /// <summary>
    /// Sets the preferred OpenVINO compute device. Takes effect on next engine load
    /// (existing loaded session is disposed so the new device is picked up).
    /// </summary>
    public void Configure(OpenVINODevice device)
    {
        if (_device == device) return;

        _device = device;
        DisposeEngine();
    }

    public OpenVINODevice Device => _device;

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        var stopwatch = Stopwatch.StartNew();
        var sb = new StringBuilder();
        await foreach (var chunk in TranslateStreamAsync(request, cancellationToken))
        {
            sb.Append(chunk);
        }
        stopwatch.Stop();

        return new TranslationResult
        {
            OriginalText = request.Text,
            TranslatedText = sb.ToString().Trim(),
            DetectedLanguage = request.FromLanguage,
            TargetLanguage = request.ToLanguage,
            ServiceName = DisplayName,
            TimingMs = stopwatch.ElapsedMilliseconds,
            FromCache = false,
        };
    }

    public async IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        if (!_downloader.IsModelInstalled())
        {
            throw new TranslationException(
                "OpenVINO NLLB-200 model is not downloaded. Open Settings → Local AI and click \"Download model\".")
            {
                ErrorCode = TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId,
            };
        }

        var (engine, tokenizer) = EnsureLoaded();

        var srcCode = ResolveSourceCode(request.FromLanguage);
        var tgtCode = NllbLanguageCodes.GetCode(request.ToLanguage);
        var inputIds = tokenizer.EncodeSource(request.Text, srcCode);
        var tgtTokenId = tokenizer.GetLanguageTokenId(tgtCode);

        await foreach (var tokenId in engine.GenerateAsync(
            inputIds,
            tgtTokenId,
            DefaultMaxNewTokens,
            cancellationToken))
        {
            var piece = tokenizer.DecodeSingle(tokenId);
            if (!string.IsNullOrEmpty(piece))
            {
                yield return piece;
            }
        }
    }

    // ── ILocalModelProvider ─────────────────────────────────────────────

    public event EventHandler<LocalModelStatus>? StatusChanged;

    public LocalModelStatus GetStatus()
    {
        if (_downloader.IsModelInstalled())
        {
            return new LocalModelStatus(LocalModelState.Ready, "OpenVINO_Status_Ready");
        }

        return new LocalModelStatus(LocalModelState.NeedsPreparation, "OpenVINO_Status_NotDownloaded");
    }

    public async Task<LocalModelStatus> PrepareAsync(CancellationToken cancellationToken)
    {
        await _prepareLock.WaitAsync(cancellationToken);
        try
        {
            if (_downloader.IsModelInstalled())
            {
                var ready = new LocalModelStatus(LocalModelState.Ready, "OpenVINO_Status_Ready");
                RaiseStatusChanged(ready);
                return ready;
            }

            var progress = new Progress<ModelDownloadProgress>(p =>
            {
                RaiseStatusChanged(new LocalModelStatus(
                    LocalModelState.Preparing,
                    "OpenVINO_Status_Downloading",
                    ProgressPercent: p.OverallPercent,
                    DetailMessage: p.CurrentFile));
            });

            RaiseStatusChanged(new LocalModelStatus(
                LocalModelState.Preparing,
                "OpenVINO_Status_Downloading",
                ProgressPercent: 0));

            try
            {
                await _downloader.DownloadAsync(progress, cancellationToken);
                var ready = new LocalModelStatus(LocalModelState.Ready, "OpenVINO_Status_Ready");
                RaiseStatusChanged(ready);
                return ready;
            }
            catch (OperationCanceledException)
            {
                var status = GetStatus();
                RaiseStatusChanged(status);
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OpenVINOTranslationService] Download failed: {ex.Message}");
                var status = new LocalModelStatus(
                    LocalModelState.Failed,
                    "OpenVINO_Status_DownloadFailed",
                    DetailMessage: ex.Message);
                RaiseStatusChanged(status);
                return status;
            }
        }
        finally
        {
            _prepareLock.Release();
        }
    }

    // ── Engine loading ──────────────────────────────────────────────────

    private (INllbInferenceEngine engine, INllbTokenizer tokenizer) EnsureLoaded()
    {
        lock (_engineLock)
        {
            if (_engine is not null && _tokenizer is not null)
            {
                return (_engine, _tokenizer);
            }

            var dir = _downloader.ModelDirectory;
            _tokenizer = NllbTokenizer.LoadFromDirectory(dir);
            _engine = NllbInferenceEngine.Load(dir, _device);
            return (_engine, _tokenizer);
        }
    }

    private void DisposeEngine()
    {
        lock (_engineLock)
        {
            (_engine as IDisposable)?.Dispose();
            _engine = null;
            _tokenizer = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeEngine();
        _prepareLock.Dispose();
    }

    private void RaiseStatusChanged(LocalModelStatus status)
    {
        StatusChanged?.Invoke(this, status);
    }

    private static string ResolveSourceCode(Language fromLanguage)
    {
        if (fromLanguage == Language.Auto)
        {
            // Auto-detect not supported by NLLB itself. We assume English as a
            // safe-ish default and rely on the wider Easydict pipeline to feed us
            // the detected language when possible.
            return "eng_Latn";
        }

        return NllbLanguageCodes.GetCode(fromLanguage);
    }

    private void ValidateRequest(TranslationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            throw new TranslationException("Text cannot be empty")
            {
                ErrorCode = TranslationErrorCode.InvalidResponse,
                ServiceId = ServiceId,
            };
        }

        if (request.ToLanguage == Language.Auto)
        {
            throw new TranslationException("Target language cannot be Auto")
            {
                ErrorCode = TranslationErrorCode.UnsupportedLanguage,
                ServiceId = ServiceId,
            };
        }

        if (NllbLanguageCodes.TryGetCode(request.ToLanguage) is null)
        {
            throw new TranslationException($"NLLB-200 does not support translating into {request.ToLanguage}.")
            {
                ErrorCode = TranslationErrorCode.UnsupportedLanguage,
                ServiceId = ServiceId,
            };
        }
    }
}
