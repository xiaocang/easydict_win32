#if PORTABLE_UPDATE_CHECK
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Easydict.WinUI.Services;

public sealed record GitHubReleaseUpdate(string TagName, Uri ReleaseUri);

public sealed class GitHubReleaseUpdateService
{
    public const string LatestReleaseApiUrl =
        "https://api.github.com/repos/xiaocang/easydict_win32/releases/latest";

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private readonly HttpClient _httpClient;

    public GitHubReleaseUpdateService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public async Task<GitHubReleaseUpdate?> CheckForUpdateAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default,
        bool includeLatestStableRelease = false)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            request.Headers.UserAgent.ParseAdd("Easydict-Windows-UpdateCheck/1.0");

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var release = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(
                responseStream,
                cancellationToken: cancellationToken);

            if (release is null ||
                release.Draft ||
                release.Prerelease ||
                string.IsNullOrWhiteSpace(release.TagName) ||
                string.IsNullOrWhiteSpace(release.HtmlUrl) ||
                !TryParseReleaseVersion(release.TagName, out var latestVersion) ||
                !Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var releaseUri))
            {
                return null;
            }

            var isUpdateAvailable =
                NormalizeVersion(latestVersion) > NormalizeVersion(currentVersion);
            if (!includeLatestStableRelease && !isUpdateAvailable)
            {
                return null;
            }

            return new GitHubReleaseUpdate(release.TagName, releaseUri);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static Version GetCurrentApplicationVersion()
    {
        var assembly = typeof(GitHubReleaseUpdateService).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (informationalVersion is not null &&
            TryParseReleaseVersion(informationalVersion, out var parsedVersion))
        {
            return parsedVersion;
        }

        return assembly.GetName().Version ?? new Version(0, 0, 0, 0);
    }

    internal static bool TryParseReleaseVersion(string tagName, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return false;
        }

        var normalized = tagName.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var suffixIndex = normalized.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            normalized = normalized[..suffixIndex];
        }

        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 4 || parts.Any(part => !int.TryParse(part, out _)))
        {
            return false;
        }

        var values = parts.Select(int.Parse).ToArray();
        version = new Version(
            values[0],
            values[1],
            values.Length > 2 ? values[2] : 0,
            values.Length > 3 ? values[3] : 0);
        return true;
    }

    private static Version NormalizeVersion(Version version)
    {
        return new Version(
            version.Major,
            version.Minor,
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    private sealed record GitHubReleaseResponse(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease);
}
#endif
