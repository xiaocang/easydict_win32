using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Easydict.TranslationService.Services.ModelCatalog;

namespace Easydict.WinUI.Services;

internal enum MagpieDiscoveryStatus
{
    Success,
    NotReachable,
    WrongServer,
    Unauthorized,
    NoModels,
    Timeout,
    Cancelled,
    InvalidAddress,
    RequestFailed
}

internal sealed record MagpieDiscoveryResult(
    MagpieDiscoveryStatus Status,
    Uri? BaseAddress = null,
    IReadOnlyList<ModelCatalogEntry>? Models = null,
    string? Detail = null)
{
    public bool IsSuccess => Status == MagpieDiscoveryStatus.Success;
}

/// <summary>Read-only, bounded discovery of the documented Magpie gateway endpoints.</summary>
internal static class MagpieGatewayDiscoveryService
{
    public const string DefaultBaseAddress = "http://127.0.0.1:3425";
    public const string LocalAttributionKey = "magpie-easydict";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);
    private const int MaxResponseBytes = 512 * 1024;

    public static async Task<MagpieDiscoveryResult> DiscoverAsync(
        HttpClient httpClient,
        string baseAddress,
        string? remoteApiKey,
        bool allowRemote,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (!TryNormalizeBaseAddress(baseAddress, out var uri, out var isLoopback))
        {
            return new(MagpieDiscoveryStatus.InvalidAddress, Detail: "Enter a gateway base address without an API path.");
        }

        if (!isLoopback && (!allowRemote
            || uri!.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(remoteApiKey)
            || string.Equals(remoteApiKey.Trim(), LocalAttributionKey, StringComparison.Ordinal)))
        {
            return new(MagpieDiscoveryStatus.InvalidAddress, Detail: "Remote gateways require explicit opt-in, HTTPS, and an authorized gateway key.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        var token = timeout.Token;
        try
        {
            var helloUri = new Uri(uri!, "api/hello");
            using var helloRequest = new HttpRequestMessage(HttpMethod.Get, helloUri);
            using var helloResponse = await httpClient.SendAsync(helloRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (helloResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new(MagpieDiscoveryStatus.Unauthorized, Detail: "The gateway rejected discovery.");
            }
            if (!helloResponse.IsSuccessStatusCode)
            {
                return new(MagpieDiscoveryStatus.WrongServer, Detail: $"Gateway identification returned HTTP {(int)helloResponse.StatusCode}.");
            }

            var helloBody = await ReadBoundedBodyAsync(helloResponse, token).ConfigureAwait(false);
            using var helloJson = JsonDocument.Parse(helloBody);
            if (!helloJson.RootElement.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String
                || !string.Equals(name.GetString(), "magpie", StringComparison.OrdinalIgnoreCase))
            {
                return new(MagpieDiscoveryStatus.WrongServer, Detail: "The address is reachable but did not identify itself as Magpie.");
            }

            var modelsUri = new Uri(uri!, "v1/models");
            using var modelsRequest = new HttpRequestMessage(HttpMethod.Get, modelsUri);
            var key = isLoopback ? LocalAttributionKey : remoteApiKey!;
            modelsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var modelsResponse = await httpClient.SendAsync(modelsRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (modelsResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new(MagpieDiscoveryStatus.Unauthorized, Detail: "The gateway rejected the supplied key.");
            }
            if (!modelsResponse.IsSuccessStatusCode)
            {
                return new(MagpieDiscoveryStatus.RequestFailed, Detail: $"Model discovery returned HTTP {(int)modelsResponse.StatusCode}.");
            }

            var modelsBody = await ReadBoundedBodyAsync(modelsResponse, token).ConfigureAwait(false);
            var models = OpenAiCompatibleModelCatalog.Parse(modelsBody);
            return models.Count == 0
                ? new(MagpieDiscoveryStatus.NoModels, uri, models, "Magpie returned no models.")
                : new(MagpieDiscoveryStatus.Success, uri, models);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(MagpieDiscoveryStatus.Cancelled, Detail: "Discovery was cancelled.");
        }
        catch (OperationCanceledException)
        {
            return new(MagpieDiscoveryStatus.Timeout, Detail: "Magpie did not respond before the discovery timeout.");
        }
        catch (HttpRequestException ex)
        {
            return new(MagpieDiscoveryStatus.NotReachable, Detail: ex.Message);
        }
        catch (IOException ex)
        {
            return new(MagpieDiscoveryStatus.RequestFailed, Detail: ex.Message);
        }
        catch (JsonException ex)
        {
            return new(MagpieDiscoveryStatus.WrongServer, Detail: $"Magpie returned an invalid response: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return new(MagpieDiscoveryStatus.RequestFailed, Detail: ex.Message);
        }
    }

    public static string ApiKeyForProfile(string baseAddress, string? remoteApiKey)
        => TryNormalizeBaseAddress(baseAddress, out _, out var isLoopback) && isLoopback
            ? LocalAttributionKey
            : remoteApiKey?.Trim() ?? "";

    public static bool IsLoopbackAddress(string? address)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri)) return false;
        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip));
    }

    private static bool TryNormalizeBaseAddress(string? address, out Uri? uri, out bool isLoopback)
    {
        uri = null;
        isLoopback = false;
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || parsed.Query.Length != 0
            || parsed.Fragment.Length != 0
            || parsed.AbsolutePath is not ("" or "/"))
        {
            return false;
        }

        isLoopback = string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(parsed.Host, out var ip) && IPAddress.IsLoopback(ip));
        uri = new UriBuilder(parsed) { Path = "/", Query = "", Fragment = "" }.Uri;
        return true;
    }

    private static async Task<string> ReadBoundedBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new IOException("Magpie response exceeded the discovery size limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new IOException("Magpie response exceeded the discovery size limit.");
            }
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }
}
