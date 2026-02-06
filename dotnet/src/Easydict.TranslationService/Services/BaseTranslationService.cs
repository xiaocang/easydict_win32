using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
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

    /// <summary>
    /// Clean up the final translation result.
    /// Removes common artifacts like surrounding quotes and whitespace.
    /// </summary>
    protected static string CleanupResult(string text)
    {
        var result = text.Trim();

        // Remove surrounding double quotes if present
        if (result.Length >= 2 &&
            result.StartsWith('"') && result.EndsWith('"'))
        {
            result = result[1..^1].Trim();
        }

        return result;
    }

    /// <summary>
    /// Create appropriate exception from HTTP error response.
    /// Parses JSON error body to extract meaningful error messages.
    /// </summary>
    protected TranslationException CreateErrorFromResponse(HttpStatusCode statusCode, string errorBody)
    {
        var errorCode = statusCode switch
        {
            HttpStatusCode.Unauthorized => TranslationErrorCode.InvalidApiKey,
            HttpStatusCode.Forbidden => TranslationErrorCode.InvalidApiKey,
            HttpStatusCode.TooManyRequests => TranslationErrorCode.RateLimited,
            HttpStatusCode.BadRequest => TranslationErrorCode.InvalidResponse,
            HttpStatusCode.InternalServerError => TranslationErrorCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable => TranslationErrorCode.ServiceUnavailable,
            HttpStatusCode.GatewayTimeout => TranslationErrorCode.Timeout,
            _ => TranslationErrorCode.Unknown
        };

        // Try to extract error message from response
        var message = $"API error ({(int)statusCode}): {statusCode}";
        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("message", out var msgElement))
                {
                    message = msgElement.GetString() ?? message;
                }
            }
        }
        catch (JsonException)
        {
            // Use default message
        }

        return new TranslationException(message)
        {
            ErrorCode = errorCode,
            ServiceId = ServiceId
        };
    }

    /// <summary>
    /// Consume all chunks from an async stream and concatenate them.
    /// Utility for streaming services that need a non-streaming fallback.
    /// </summary>
    protected static async Task<string> ConsumeStreamAsync(
        IAsyncEnumerable<string> stream, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        await foreach (var chunk in stream.WithCancellation(cancellationToken))
        {
            sb.Append(chunk);
        }
        return sb.ToString();
    }
}

