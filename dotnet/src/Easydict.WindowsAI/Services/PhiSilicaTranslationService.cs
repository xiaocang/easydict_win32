using System.Diagnostics;
using System.Runtime.CompilerServices;
using Easydict.TranslationService;
using Easydict.TranslationService.LocalModels;
using Easydict.TranslationService.Models;

namespace Easydict.WindowsAI.Services;

/// <summary>
/// Translation provider backed by the Windows AI APIs (Phi Silica) shipped via
/// Windows App SDK 2.x. Runs on-device, no API key, no network.
/// Available only on Copilot+ PCs that meet the model's hardware requirements;
/// surfaces a friendly state via <see cref="WindowsAIReadyState"/> on others.
/// </summary>
public sealed class PhiSilicaTranslationService : IStreamTranslationService, ILocalModelProvider
{
    public const string ServiceIdValue = "windows-local-ai";

    private static readonly IReadOnlyList<Language> _allLanguages =
        Enum.GetValues<Language>().Where(l => l != Language.Auto).ToArray();

    private const string UserFacingName = "Phi Silica";

    private readonly IWindowsLanguageModelClient _client;
    private readonly PhiSilicaBackendHealthMonitor _healthMonitor;

    public PhiSilicaTranslationService()
        : this(PhiSilicaAvailability.Client, PhiSilicaBackendHealthMonitor.Shared)
    {
    }

    internal PhiSilicaTranslationService(IWindowsLanguageModelClient client)
        : this(client, new PhiSilicaBackendHealthMonitor())
    {
    }

    internal PhiSilicaTranslationService(
        IWindowsLanguageModelClient client,
        PhiSilicaBackendHealthMonitor healthMonitor)
    {
        _client = client;
        _healthMonitor = healthMonitor;
    }

    public string ServiceId => ServiceIdValue;

    public string DisplayName => UserFacingName;

    public bool RequiresApiKey => false;

    /// <summary>
    /// Always reports configured — actual device/model availability is checked at
    /// translate time so the service still lights up in the settings UI on
    /// non-Copilot+ devices and surfaces a clear status message instead of being
    /// silently hidden.
    /// </summary>
    public bool IsConfigured => true;

    public bool IsStreaming => true;

    public IReadOnlyList<Language> SupportedLanguages => _allLanguages;

    public bool SupportsLanguagePair(Language from, Language to)
    {
        // Phi Silica handles auto-detection in the prompt; target Auto is meaningless.
        return to != Language.Auto;
    }

    public Task<Language> DetectLanguageAsync(string text, CancellationToken cancellationToken = default)
    {
        // Source language detection happens implicitly inside the translation prompt.
        return Task.FromResult(Language.Auto);
    }

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await EnsureReadyOrThrowAsync(cancellationToken);

            var prompt = BuildTranslationPrompt(request);
            var response = await _client.GenerateAsync(
                prompt,
                DefaultGenerationOptions,
                cancellationToken);

            ThrowIfNotComplete(response);
            stopwatch.Stop();

            return new TranslationResult
            {
                OriginalText = request.Text,
                TranslatedText = CleanModelOutput(response.Text),
                DetectedLanguage = request.FromLanguage,
                TargetLanguage = request.ToLanguage,
                ServiceName = DisplayName,
                TimingMs = stopwatch.ElapsedMilliseconds,
                FromCache = false,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TranslationException)
        {
            throw;
        }
        catch (WindowsLanguageModelException wex)
        {
            MarkUnhealthy(wex);
            throw MapWindowsLanguageModelException(wex);
        }
        catch (Exception ex)
        {
            throw new TranslationException($"{UserFacingName} failed: {ex.Message}", ex)
            {
                ErrorCode = TranslationErrorCode.Unknown,
                ServiceId = ServiceId,
            };
        }
    }

    public async IAsyncEnumerable<string> TranslateStreamAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        await EnsureReadyOrThrowAsync(cancellationToken);

        var prompt = BuildTranslationPrompt(request);

        // Manual iteration (rather than `await foreach`) so we can wrap raw
        // WinRT-level exceptions into TranslationException with a populated
        // ErrorCode + ServiceId — matching the behavior of the non-streaming
        // path. C# forbids yield-return inside try/catch, so the catch
        // surrounds MoveNextAsync and the yield happens outside it.
        var enumerator = _client
            .GenerateStreamAsync(prompt, DefaultGenerationOptions, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        try
        {
            while (true)
            {
                bool hasNext;
                string current;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                    if (!hasNext)
                    {
                        yield break;
                    }
                    current = enumerator.Current;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (TranslationException)
                {
                    throw;
                }
                catch (WindowsLanguageModelException wex)
                {
                    MarkUnhealthy(wex);
                    throw MapWindowsLanguageModelException(wex);
                }
                catch (Exception ex)
                {
                    throw new TranslationException($"{UserFacingName} failed: {ex.Message}", ex)
                    {
                        ErrorCode = TranslationErrorCode.Unknown,
                        ServiceId = ServiceId,
                    };
                }

                if (!string.IsNullOrEmpty(current))
                {
                    yield return current;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    private TranslationException MapWindowsLanguageModelException(WindowsLanguageModelException wex)
    {
        var code = wex.Status switch
        {
            WindowsAIResponseStatus.Error => TranslationErrorCode.Unknown,
            WindowsAIResponseStatus.PromptLargerThanContext => TranslationErrorCode.TextTooLong,
            WindowsAIResponseStatus.BlockedByPolicy => TranslationErrorCode.ServiceUnavailable,
            _ => TranslationErrorCode.InvalidResponse,
        };

        return new TranslationException(
            $"{UserFacingName}: {wex.Message}", wex)
        {
            ErrorCode = code,
            ServiceId = ServiceId,
        };
    }

    // ── ILocalModelProvider ─────────────────────────────────────────────

    public event EventHandler<LocalModelStatus>? StatusChanged;

    public LocalModelStatus GetStatus()
    {
        return _healthMonitor.GetStatus(_client);
    }

    public async Task<LocalModelStatus> PrepareAsync(CancellationToken cancellationToken)
    {
        RaiseStatusChanged(new LocalModelStatus(
            LocalModelState.Preparing,
            PhiSilicaResources.StatusKeys.Preparing));
        _healthMonitor.Reset();

        try
        {
            var newState = await _client.EnsureReadyAsync(cancellationToken);
            if (newState == WindowsAIReadyState.NotReady)
            {
                var failed = new LocalModelStatus(
                    LocalModelState.Failed,
                    PhiSilicaResources.StatusKeys.PrepareFailed,
                    DetailMessage: "Windows reported that the model is still not ready after the preparation request completed.");
                RaiseStatusChanged(failed);
                return failed;
            }

            if (newState == WindowsAIReadyState.Ready)
            {
                await _healthMonitor.EnsureHealthyAsync(
                    _client,
                    snapshot => RaiseStatusChanged(MapHealthSnapshotToStatus(snapshot)),
                    cancellationToken);
            }

            var status = GetStatus();
            RaiseStatusChanged(status);
            return status;
        }
        catch (OperationCanceledException)
        {
            var status = GetStatus();
            RaiseStatusChanged(status);
            throw;
        }
        catch (Exception ex)
        {
            // Distinct resource key from NotReady — the UI should be able to
            // tell users "we tried and it failed" vs. "you haven't tried yet".
            // The original exception message is forwarded as DetailMessage for
            // diagnostics (e.g. surfaced via InfoBar's secondary content).
            var fingerprint = TryGetHealthFingerprint(_client);
            var status = LooksLikeRuntimeFailure(ex.Message)
                ? CreateRuntimeFailureStatus(ex.Message, fingerprint)
                : CreatePreparationFailureStatus(ex.Message, fingerprint);
            RaiseStatusChanged(status);
            return status;
        }
    }

    private void RaiseStatusChanged(LocalModelStatus status)
    {
        StatusChanged?.Invoke(this, status);
    }

    public static LocalModelStatus MapReadyStateToStatus(WindowsAIReadyState state) => state switch
    {
        WindowsAIReadyState.Ready =>
            new LocalModelStatus(LocalModelState.Ready, PhiSilicaResources.StatusKeys.Ready),

        WindowsAIReadyState.NotReady =>
            new LocalModelStatus(LocalModelState.NeedsPreparation, PhiSilicaResources.StatusKeys.NotReady),

        WindowsAIReadyState.CapabilityMissing =>
            new LocalModelStatus(LocalModelState.NotCompatible, PhiSilicaResources.StatusKeys.CapabilityMissing),

        WindowsAIReadyState.NotCompatibleWithSystemHardware =>
            new LocalModelStatus(LocalModelState.NotCompatible, PhiSilicaResources.StatusKeys.NotCompatibleHardware),

        WindowsAIReadyState.OSUpdateNeeded =>
            new LocalModelStatus(LocalModelState.NotCompatible, PhiSilicaResources.StatusKeys.OSUpdateNeeded),

        WindowsAIReadyState.DisabledByUser =>
            new LocalModelStatus(LocalModelState.NotCompatible, PhiSilicaResources.StatusKeys.DisabledByUser),

        WindowsAIReadyState.UnsupportedWindowsAIBaseline =>
            new LocalModelStatus(
                LocalModelState.NotCompatible,
                WindowsAIBaselineDiagnostics.UnsupportedWindowsAIBaselineResourceKey),

        _ =>
            new LocalModelStatus(LocalModelState.NotCompatible, PhiSilicaResources.StatusKeys.NotSupported),
    };

    public static LocalModelStatus CreatePreparationFailureStatus(
        string? detailMessage,
        WindowsAIHealthFingerprint? fingerprint = null)
    {
        return WindowsAIBaselineDiagnostics.LooksLikeUnsupportedBaseline(fingerprint, detailMessage)
            ? new LocalModelStatus(
                LocalModelState.Failed,
                WindowsAIBaselineDiagnostics.UnsupportedWindowsAIBaselineResourceKey,
                DetailMessage: detailMessage)
            : new LocalModelStatus(
                LocalModelState.Failed,
                PhiSilicaResources.StatusKeys.PrepareFailed,
                DetailMessage: detailMessage);
    }

    public static LocalModelStatus CreateRuntimeFailureStatus(
        string? detailMessage,
        WindowsAIHealthFingerprint? fingerprint = null)
    {
        var detail = FormatRuntimeFailureDetail(detailMessage, fingerprint);
        return WindowsAIBaselineDiagnostics.LooksLikeUnsupportedBaseline(fingerprint, detail)
            ? new LocalModelStatus(
                LocalModelState.Failed,
                WindowsAIBaselineDiagnostics.UnsupportedWindowsAIBaselineResourceKey,
                DetailMessage: detail)
            : new LocalModelStatus(
                LocalModelState.Failed,
                PhiSilicaResources.StatusKeys.RuntimeUnhealthy,
                DetailMessage: detail);
    }

    private static bool LooksLikeRuntimeFailure(string? detailMessage)
    {
        return detailMessage?.Contains(
            "Windows AI runtime failed while running Phi Silica",
            StringComparison.OrdinalIgnoreCase) == true
            || detailMessage?.Contains(
                "Phi Silica backend is unhealthy",
                StringComparison.OrdinalIgnoreCase) == true;
    }

    internal static string FormatRuntimeFailureDetail(
        string? detailMessage,
        WindowsAIHealthFingerprint? fingerprint)
    {
        var parts = new List<string>();
        var detail = string.IsNullOrWhiteSpace(detailMessage)
            ? null
            : detailMessage.Trim();

        if (detail is not null)
        {
            parts.Add(detail);
        }

        if (fingerprint is not null)
        {
            var fingerprintDetail = FormatSupplementalFingerprint(fingerprint, detail);
            if (!string.IsNullOrWhiteSpace(fingerprintDetail))
            {
                parts.Add(fingerprintDetail);
            }
        }

        return string.Join("; ", parts);
    }

    private static LocalModelStatus MapHealthSnapshotToStatus(PhiSilicaBackendHealthSnapshot snapshot)
    {
        return snapshot.State switch
        {
            PhiSilicaBackendHealthState.Healthy =>
                new LocalModelStatus(LocalModelState.Ready, PhiSilicaResources.StatusKeys.Ready),

            PhiSilicaBackendHealthState.Unhealthy =>
                CreateRuntimeFailureStatus(snapshot.DetailMessage, snapshot.Fingerprint),

            _ =>
                new LocalModelStatus(LocalModelState.Preparing, PhiSilicaResources.StatusKeys.WarmingUp),
        };
    }

    private static string FormatSupplementalFingerprint(
        WindowsAIHealthFingerprint fingerprint,
        string? existingDetail)
    {
        var parts = new List<string>();
        AddDiagnosticIfMissing(parts, existingDetail, "osBuild", fingerprint.OsBuild);
        AddDiagnosticIfMissing(parts, existingDetail, "ubr", fingerprint.Ubr?.ToString() ?? "unknown");
        AddDiagnosticIfMissing(parts, existingDetail, "windowsAppSdk", fingerprint.WindowsAppSdkVersion);
        AddDiagnosticIfMissing(parts, existingDetail, "processArch", fingerprint.ProcessArchitecture);
        AddDiagnosticIfMissing(parts, existingDetail, "backend", fingerprint.BackendName);
        AddDiagnosticIfMissing(parts, existingDetail, "component", fingerprint.ComponentMarker);
        AddDiagnosticIfMissing(parts, existingDetail, "windowsActivated", FormatOptionalBool(fingerprint.WindowsActivated));
        AddDiagnosticIfMissing(parts, existingDetail, "phiSilicaAiComponentsPresent", FormatOptionalBool(fingerprint.PhiSilicaAiComponentsPresent));
        return string.Join("; ", parts);
    }

    private static string FormatOptionalBool(bool? value) =>
        value is { } present ? present.ToString() : "unknown";

    private static void AddDiagnosticIfMissing(
        List<string> parts,
        string? existingDetail,
        string key,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (existingDetail?.Contains($"{key}=", StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        parts.Add($"{key}={value}");
    }

    // ── Internal helpers ────────────────────────────────────────────────

    private static WindowsAIGenerationOptions DefaultGenerationOptions =>
        new(Temperature: 0.1f, TopK: 1, TopP: 0.9f);

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
    }

    private async Task EnsureReadyOrThrowAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var state = _client.GetReadyState();
        if (state != WindowsAIReadyState.Ready)
        {
            throw new TranslationException(GetReadyStateMessage(state))
            {
                ErrorCode = state == WindowsAIReadyState.NotReady
                    ? TranslationErrorCode.LocalModelNeedsPreparation
                    : TranslationErrorCode.ServiceUnavailable,
                ServiceId = ServiceId,
            };
        }

        try
        {
            await _healthMonitor.EnsureHealthyAsync(_client, cancellationToken: cancellationToken);
        }
        catch (WindowsLanguageModelException wex)
        {
            throw MapWindowsLanguageModelException(wex);
        }
    }

    private void MarkUnhealthy(WindowsLanguageModelException wex)
    {
        if (wex.Status == WindowsAIResponseStatus.Error)
        {
            _healthMonitor.MarkUnhealthy(_client, wex.Message);
            RaiseStatusChanged(GetStatus());
        }
    }

    private void ThrowIfNotComplete(WindowsAIResponse response)
    {
        if (response.Status == WindowsAIResponseStatus.Complete)
        {
            return;
        }

        var code = response.Status switch
        {
            WindowsAIResponseStatus.PromptLargerThanContext => TranslationErrorCode.TextTooLong,
            WindowsAIResponseStatus.BlockedByPolicy => TranslationErrorCode.ServiceUnavailable,
            _ => TranslationErrorCode.InvalidResponse,
        };

        var message = !string.IsNullOrWhiteSpace(response.ErrorMessage)
            ? $"{UserFacingName}: {response.ErrorMessage}"
            : $"{UserFacingName} returned status {response.Status}.";

        throw new TranslationException(message)
        {
            ErrorCode = code,
            ServiceId = ServiceId,
        };
    }

    internal static string BuildTranslationPrompt(TranslationRequest request)
    {
        var from = request.FromLanguage == Language.Auto
            ? "the source language, auto-detected"
            : FormatLanguage(request.FromLanguage);

        var to = FormatLanguage(request.ToLanguage);

        var custom = string.IsNullOrWhiteSpace(request.CustomPrompt)
            ? string.Empty
            : $"""

               Additional user instruction:
               {request.CustomPrompt!.Trim()}
               """;

        return $"""
        You are a professional translation engine used inside a desktop dictionary app.

        Task:
        Translate the text from {from} to {to}.

        Rules:
        - Output only the translated text.
        - Do not explain.
        - Do not add greetings, notes, markdown fences, or alternatives.
        - Preserve original line breaks.
        - Preserve URLs, emails, file paths, code, variables, placeholders, formulas, and numbers.
        - Preserve markdown structure when the input is markdown.{custom}

        Text to translate:
        <<<EASYDICT_SOURCE_TEXT
        {request.Text}
        EASYDICT_SOURCE_TEXT
        """;
    }

    private static string FormatLanguage(Language language) => language switch
    {
        Language.SimplifiedChinese => "Simplified Chinese (zh-CN)",
        Language.TraditionalChinese => "Traditional Chinese (zh-TW)",
        Language.ClassicalChinese => "Classical Chinese (zh-CN)",
        _ => $"{language} ({language.ToIso639()})",
    };

    private static string CleanModelOutput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var output = text.Trim();
        const string prefix = "Translation:";
        if (output.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            output = output[prefix.Length..].TrimStart();
        }

        if (output.Length >= 2 && output.StartsWith('"') && output.EndsWith('"'))
        {
            output = output[1..^1].Trim();
        }

        return output;
    }

    internal static string GetReadyStateMessage(WindowsAIReadyState state) => state switch
    {
        WindowsAIReadyState.CapabilityMissing =>
            $"{UserFacingName} is unavailable: the app package is missing the systemAIModels capability.",

        WindowsAIReadyState.NotCompatibleWithSystemHardware =>
            $"{UserFacingName} requires a Copilot+ PC with a compatible NPU. Select Auto or OpenVINO in Windows Local AI settings to use the local fallback.",

        WindowsAIReadyState.OSUpdateNeeded =>
            $"{UserFacingName} requires a newer Windows build. Update Windows and try again.",

        WindowsAIReadyState.DisabledByUser =>
            $"{UserFacingName} has been disabled or removed. Re-enable Windows AI features in system settings.",

        WindowsAIReadyState.UnsupportedWindowsAIBaseline =>
            $"{UserFacingName} is unavailable because this Windows installation does not appear to have a valid Windows AI baseline. " +
            "This usually happens on unactivated, outdated, Insider, managed, or incomplete Copilot+ PC images where Windows Update cannot install AI Components. " +
            "Activate Windows, install the latest cumulative update and AI Components, then verify Phi Silica in AI Dev Gallery.",

        WindowsAIReadyState.NotSupportedOnCurrentSystem =>
            $"{UserFacingName} is not supported on the current system or region. Select Auto or OpenVINO in Windows Local AI settings to use the local fallback.",

        WindowsAIReadyState.NotReady =>
            $"{UserFacingName} model is not ready. Start a translation again and choose Download to prepare it.",

        _ => $"{UserFacingName} is unavailable ({state}).",
    };

    private static WindowsAIHealthFingerprint? TryGetHealthFingerprint(IWindowsLanguageModelClient client)
    {
        try
        {
            return client.GetHealthFingerprint();
        }
        catch
        {
            return null;
        }
    }
}

