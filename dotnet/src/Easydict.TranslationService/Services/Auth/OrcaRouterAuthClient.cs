using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Easydict.TranslationService.Services.Auth;

/// <summary>
/// OrcaRouter PKCE sign-in: builds the browser authorization URL and exchanges the
/// authorization code (received on the loopback callback) for an API key.
/// </summary>
public sealed class OrcaRouterAuthClient
{
    /// <summary>Browser page that authenticates the user and redirects to the callback URL.</summary>
    public const string AuthorizeEndpoint = "https://www.orcarouter.ai/auth";

    /// <summary>Exchanges <c>code</c> + <c>code_verifier</c> for an API key.</summary>
    public const string KeyExchangeEndpoint = "https://api.orcarouter.ai/api/v1/auth/keys";

    private const int MaxErrorBodyChars = 300;

    private readonly HttpClient _httpClient;

    public OrcaRouterAuthClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    /// Build the authorization URL to open in the user's browser.
    /// </summary>
    /// <param name="callbackUrl">Loopback URL OrcaRouter redirects to, e.g. <c>http://127.0.0.1:5123/cb</c>.</param>
    /// <param name="codeChallenge">S256 challenge for this attempt's verifier.</param>
    /// <param name="state">Opaque per-attempt CSRF token; the callback must echo it.</param>
    /// <param name="appName">Shown to the user on the consent page.</param>
    /// <param name="referralCode">Maintainer referral code.</param>
    public static Uri BuildAuthorizeUri(
        string callbackUrl,
        string codeChallenge,
        string state,
        string appName = OrcaRouterService.AppTitle,
        string referralCode = OrcaRouterService.ReferralCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(callbackUrl);
        ArgumentException.ThrowIfNullOrEmpty(codeChallenge);
        ArgumentException.ThrowIfNullOrEmpty(state);

        var query = new StringBuilder()
            .Append("callback_url=").Append(Uri.EscapeDataString(callbackUrl))
            .Append("&code_challenge=").Append(Uri.EscapeDataString(codeChallenge))
            .Append("&code_challenge_method=").Append(PkceCodeChallenge.Method)
            .Append("&state=").Append(Uri.EscapeDataString(state))
            .Append("&app_name=").Append(Uri.EscapeDataString(appName))
            .Append("&ref=").Append(Uri.EscapeDataString(referralCode));

        return new Uri(AuthorizeEndpoint + "?" + query);
    }

    /// <summary>
    /// Exchange the authorization code for an API key. The code is single-use and short-lived;
    /// a failed exchange requires a fresh sign-in attempt.
    /// </summary>
    /// <exception cref="TranslationException">Network failure, non-success status, or a response without a key.</exception>
    public async Task<string> ExchangeCodeForApiKeyAsync(
        string code,
        string codeVerifier,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentException.ThrowIfNullOrEmpty(codeVerifier);

        // Exact request body: {"code": "...", "code_verifier": "..."}.
        var payload = JsonSerializer.Serialize(new { code, code_verifier = codeVerifier });

        using var request = new HttpRequestMessage(HttpMethod.Post, KeyExchangeEndpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/xiaocang/easydict_win32");
        request.Headers.TryAddWithoutValidation("X-Title", OrcaRouterService.AppTitle);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new TranslationException($"Network error during OrcaRouter sign-in: {ex.Message}", ex)
            {
                ErrorCode = TranslationErrorCode.NetworkError,
            };
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var snippet = body.Length > MaxErrorBodyChars ? body[..MaxErrorBodyChars] + "…" : body;
                throw new TranslationException(
                    $"OrcaRouter key exchange failed ({(int)response.StatusCode}): {snippet}")
                {
                    ErrorCode = response.StatusCode switch
                    {
                        HttpStatusCode.TooManyRequests => TranslationErrorCode.RateLimited,
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => TranslationErrorCode.InvalidApiKey,
                        _ => TranslationErrorCode.ServiceUnavailable,
                    },
                };
            }

            return ParseApiKey(body);
        }
    }

    /// <summary>
    /// Extract the API key from the exchange response. Accepts <c>key</c>, <c>api_key</c> or
    /// <c>apiKey</c> at the root or nested under <c>data</c>.
    /// </summary>
    internal static string ParseApiKey(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new TranslationException($"OrcaRouter key exchange response was not valid JSON: {ex.Message}", ex)
            {
                ErrorCode = TranslationErrorCode.InvalidResponse,
            };
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryReadKey(root, out var key))
                {
                    return key;
                }

                if (root.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Object &&
                    TryReadKey(data, out key))
                {
                    return key;
                }
            }

            throw new TranslationException("OrcaRouter key exchange response did not contain an API key")
            {
                ErrorCode = TranslationErrorCode.InvalidResponse,
            };
        }
    }

    private static bool TryReadKey(JsonElement obj, out string key)
    {
        foreach (var name in KeyPropertyNames)
        {
            if (obj.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    key = text;
                    return true;
                }
            }
        }

        key = string.Empty;
        return false;
    }

    private static readonly string[] KeyPropertyNames = { "key", "api_key", "apiKey" };
}
