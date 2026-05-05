using System.Diagnostics;
using System.Runtime.CompilerServices;
using Easydict.TranslationService;
using Easydict.TranslationService.Models;

namespace Easydict.WindowsAI.Services;

/// <summary>
/// Translation provider backed by the Windows AI APIs (Phi Silica) shipped via
/// Windows App SDK 2.x. Runs on-device, no API key, no network.
/// Available only on Copilot+ PCs that meet the model's hardware requirements;
/// surfaces a friendly state via <see cref="WindowsAIReadyState"/> on others.
/// </summary>
public sealed class WindowsLocalAIService : IStreamTranslationService
{
    private static readonly IReadOnlyList<Language> _allLanguages =
        Enum.GetValues<Language>().Where(l => l != Language.Auto).ToArray();

    private readonly IWindowsLanguageModelClient _client;

    public WindowsLocalAIService()
        : this(WindowsLocalAIAvailability.Client)
    {
    }

    internal WindowsLocalAIService(IWindowsLanguageModelClient client)
    {
        _client = client;
    }

    public string ServiceId => "windows-local-ai";

    public string DisplayName => "Windows Local AI";

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
        catch (Exception ex)
        {
            throw new TranslationException($"Windows Local AI failed: {ex.Message}", ex)
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

        // Note: yield-return inside try/catch is restricted, so the WinRT-exception
        // wrapping for the streaming path lives in WindowsLanguageModelClient's channel
        // writer (which surfaces failures via TryComplete(ex)). EnsureReadyOrThrowAsync
        // already covers the most common failure mode (device not ready).
        await foreach (var chunk in _client.GenerateStreamAsync(
            prompt,
            DefaultGenerationOptions,
            cancellationToken))
        {
            if (!string.IsNullOrEmpty(chunk))
            {
                yield return chunk;
            }
        }
    }

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
        var state = await _client.EnsureReadyAsync(cancellationToken);
        if (state == WindowsAIReadyState.Ready)
        {
            return;
        }

        throw new TranslationException(GetReadyStateMessage(state))
        {
            ErrorCode = TranslationErrorCode.ServiceUnavailable,
            ServiceId = ServiceId,
        };
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
            ? $"Windows Local AI: {response.ErrorMessage}"
            : $"Windows Local AI returned status {response.Status}.";

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

    private static string GetReadyStateMessage(WindowsAIReadyState state) => state switch
    {
        WindowsAIReadyState.CapabilityMissing =>
            "Windows Local AI is unavailable: the app package is missing the systemAIModels capability.",

        WindowsAIReadyState.NotCompatibleWithSystemHardware =>
            "Windows Local AI requires a Copilot+ PC with a compatible NPU. Use Ollama or a cloud provider as a fallback.",

        WindowsAIReadyState.OSUpdateNeeded =>
            "Windows Local AI requires a newer Windows build. Update Windows and try again.",

        WindowsAIReadyState.DisabledByUser =>
            "Windows Local AI has been disabled or removed. Re-enable Windows AI features in system settings.",

        WindowsAIReadyState.NotSupportedOnCurrentSystem =>
            "Windows Local AI is not supported on the current system or region.",

        WindowsAIReadyState.NotReady =>
            "Windows Local AI model is not ready and could not be prepared automatically.",

        _ => $"Windows Local AI is unavailable ({state}).",
    };
}
