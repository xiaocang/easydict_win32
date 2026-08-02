using System.Net;
using System.Text;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public class GitHubReleaseUpdateServiceTests
{
    [Fact]
    public async Task CheckForUpdateAsync_ReturnsLatestStableRelease_WhenVersionIsNewer()
    {
        var handler = CreateJsonHandler(
            """
            {
              "tag_name": "v0.9.0",
              "html_url": "https://github.com/xiaocang/easydict_win32/releases/tag/v0.9.0",
              "draft": false,
              "prerelease": false
            }
            """);
        using var client = new HttpClient(handler);
        var service = new GitHubReleaseUpdateService(client);

        var result = await service.CheckForUpdateAsync(new Version(0, 8, 8, 0));

        result.Should().NotBeNull();
        result!.TagName.Should().Be("v0.9.0");
        result.ReleaseUri.Should().Be(
            new Uri("https://github.com/xiaocang/easydict_win32/releases/tag/v0.9.0"));
        handler.LastRequestUri.Should().Be(new Uri(GitHubReleaseUpdateService.LatestReleaseApiUrl));
    }

    [Theory]
    [InlineData("v0.8.8")]
    [InlineData("v0.8.7")]
    public async Task CheckForUpdateAsync_ReturnsNull_WhenReleaseIsNotNewer(string tagName)
    {
        var handler = CreateJsonHandler(
            $$"""
            {
              "tag_name": "{{tagName}}",
              "html_url": "https://github.com/xiaocang/easydict_win32/releases/tag/{{tagName}}",
              "draft": false,
              "prerelease": false
            }
            """);
        using var client = new HttpClient(handler);
        var service = new GitHubReleaseUpdateService(client);

        var result = await service.CheckForUpdateAsync(new Version(0, 8, 8, 0));

        result.Should().BeNull();
    }

    [Fact]
    public async Task CheckForUpdateAsync_ReturnsLatestStableRelease_WhenRequested()
    {
        var handler = CreateJsonHandler(
            """
            {
              "tag_name": "v0.8.8",
              "html_url": "https://github.com/xiaocang/easydict_win32/releases/tag/v0.8.8",
              "draft": false,
              "prerelease": false
            }
            """);
        using var client = new HttpClient(handler);
        var service = new GitHubReleaseUpdateService(client);

        var result = await service.CheckForUpdateAsync(
            new Version(0, 8, 8, 0),
            includeLatestStableRelease: true);

        result.Should().NotBeNull();
        result!.TagName.Should().Be("v0.8.8");
    }

    [Fact]
    public async Task CheckForUpdateAsync_IgnoresPrereleaseResponses()
    {
        var handler = CreateJsonHandler(
            """
            {
              "tag_name": "v1.0.0-rc.1",
              "html_url": "https://github.com/xiaocang/easydict_win32/releases/tag/v1.0.0-rc.1",
              "draft": false,
              "prerelease": true
            }
            """);
        using var client = new HttpClient(handler);
        var service = new GitHubReleaseUpdateService(client);

        var result = await service.CheckForUpdateAsync(new Version(0, 8, 8, 0));

        result.Should().BeNull();
    }

    [Fact]
    public async Task CheckForUpdateAsync_ReturnsNull_WhenGitHubRequestFails()
    {
        var handler = new RecordingHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var client = new HttpClient(handler);
        var service = new GitHubReleaseUpdateService(client);

        var result = await service.CheckForUpdateAsync(new Version(0, 8, 8, 0));

        result.Should().BeNull();
    }

    private static RecordingHttpMessageHandler CreateJsonHandler(string json)
    {
        return new RecordingHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            }));
    }
}
