using System.Text.Json;
using System.Text.RegularExpressions;

namespace Easydict.TranslationService.Services;

internal sealed class DeepLAppVersionProvider
{
    private const string LookupUrl = "https://itunes.apple.com/lookup?id=1552407475&country=us";
    private const string ExpectedBundleId = "com.linguee.DeepLMobileTranslator";
    private const string ExpectedSeller = "DeepL SE";
    private const string FallbackVersion = "26.58";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(3);
    private static readonly HttpClient SharedHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly DeepLAppVersionProvider SharedInstance = new(SharedHttpClient);

    private readonly HttpClient? _httpClient;
    private readonly string? _fixedVersion;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _cachedVersion;
    private DateTimeOffset _fetchedAt;
    private DateTimeOffset _lastAttemptAt;

    internal DeepLAppVersionProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    internal DeepLAppVersionProvider(string fixedVersion)
    {
        _fixedVersion = fixedVersion;
    }

    internal static DeepLAppVersionProvider Shared => SharedInstance;

    internal async Task<string> GetCurrentVersionAsync(CancellationToken cancellationToken)
    {
        if (_fixedVersion is not null)
        {
            return _fixedVersion;
        }

        var now = DateTimeOffset.UtcNow;
        if (_cachedVersion is not null && now - _fetchedAt < CacheLifetime)
        {
            return _cachedVersion;
        }

        if (_lastAttemptAt != default && now - _lastAttemptAt < RetryBackoff)
        {
            return _cachedVersion ?? FallbackVersion;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_cachedVersion is not null && now - _fetchedAt < CacheLifetime)
            {
                return _cachedVersion;
            }

            if (_lastAttemptAt != default && now - _lastAttemptAt < RetryBackoff)
            {
                return _cachedVersion ?? FallbackVersion;
            }

            _lastAttemptAt = now;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(LookupTimeout);

            try
            {
                using var response = await _httpClient!.GetAsync(LookupUrl, timeout.Token);
                response.EnsureSuccessStatusCode();
                await using var content = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var document = await JsonDocument.ParseAsync(content, cancellationToken: timeout.Token);

                var root = document.RootElement;
                if (!root.TryGetProperty("resultCount", out var countElement) ||
                    !countElement.TryGetInt32(out var resultCount) ||
                    resultCount != 1 ||
                    !root.TryGetProperty("results", out var results) ||
                    results.ValueKind != JsonValueKind.Array ||
                    results.GetArrayLength() != 1)
                {
                    return _cachedVersion ?? FallbackVersion;
                }

                var app = results[0];
                if (!app.TryGetProperty("trackId", out var trackId) ||
                    !trackId.TryGetInt64(out var appTrackId) ||
                    appTrackId != 1552407475 ||
                    !string.Equals(ReadString(app, "bundleId"), ExpectedBundleId, StringComparison.Ordinal) ||
                    !string.Equals(ReadString(app, "artistName"), ExpectedSeller, StringComparison.Ordinal))
                {
                    return _cachedVersion ?? FallbackVersion;
                }

                var version = ReadString(app, "version");
                if (version is null || !Regex.IsMatch(version, @"^\d+(?:\.\d+){1,3}$", RegexOptions.CultureInvariant))
                {
                    return _cachedVersion ?? FallbackVersion;
                }

                _cachedVersion = version;
                _fetchedAt = DateTimeOffset.UtcNow;
                return version;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or JsonException)
            {
                return _cachedVersion ?? FallbackVersion;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
