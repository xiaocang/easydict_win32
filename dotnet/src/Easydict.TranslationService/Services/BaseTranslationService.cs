using System.Diagnostics;
using Easydict.TranslationService.Models;

namespace Easydict.TranslationService.Services;

/// <summary>
/// Base class for translation services with common functionality.
/// </summary>
public abstract class BaseTranslationService : ITranslationService
{
    protected readonly HttpClient HttpClient;

    protected BaseTranslationService(HttpClient httpClient)
    {
        HttpClient = httpClient;
    }

    public abstract string ServiceId { get; }
    public abstract string DisplayName { get; }
    public abstract bool RequiresApiKey { get; }
    public abstract bool IsConfigured { get; }
    public abstract IReadOnlyList<Language> SupportedLanguages { get; }

    public virtual bool SupportsLanguagePair(Language from, Language to)
    {
        if (from == Language.Auto)
            return SupportedLanguages.Contains(to);

        return SupportedLanguages.Contains(from) && SupportedLanguages.Contains(to);
    }

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await TranslateInternalAsync(request, cancellationToken);
            stopwatch.Stop();

            return result with { TimingMs = stopwatch.ElapsedMilliseconds };
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationException($"Network error: {ex.Message}", ex)
            {
                ErrorCode = TranslationErrorCode.NetworkError,
                ServiceId = ServiceId
            };
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            throw new TranslationException("Request timed out", ex)
            {
                ErrorCode = TranslationErrorCode.Timeout,
                ServiceId = ServiceId
            };
        }
        catch (TranslationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new TranslationException($"Translation failed: {ex.Message}", ex)
            {
                ErrorCode = TranslationErrorCode.Unknown,
                ServiceId = ServiceId
            };
        }
    }

    public virtual Task<Language> DetectLanguageAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        // Default implementation - override in services that support detection
        return Task.FromResult(Language.Auto);
    }

    /// <summary>
    /// Implement translation logic in derived classes.
    /// </summary>
    protected abstract Task<TranslationResult> TranslateInternalAsync(
        TranslationRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Validate request parameters.
    /// </summary>
    protected virtual void ValidateRequest(TranslationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            throw new TranslationException("Text cannot be empty")
            {
                ErrorCode = TranslationErrorCode.Unknown,
                ServiceId = ServiceId
            };
        }

        if (!SupportsLanguagePair(request.FromLanguage, request.ToLanguage))
        {
            throw new TranslationException(
                $"Language pair not supported: {request.FromLanguage} -> {request.ToLanguage}")
            {
                ErrorCode = TranslationErrorCode.UnsupportedLanguage,
                ServiceId = ServiceId
            };
        }

        if (!IsConfigured)
        {
            throw new TranslationException("Service is not configured (missing API key?)")
            {
                ErrorCode = TranslationErrorCode.InvalidApiKey,
                ServiceId = ServiceId
            };
        }
    }

    /// <summary>
    /// Get language code for this service.
    /// Override if service uses non-standard codes.
    /// </summary>
    protected virtual string GetLanguageCode(Language language)
    {
        return language.ToIso639();
    }
}

