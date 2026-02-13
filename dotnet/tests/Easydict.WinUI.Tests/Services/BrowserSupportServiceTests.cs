using FluentAssertions;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for BrowserSupportService — checksum verification, SHA256 computation, platform detection.
/// </summary>
public class BrowserSupportServiceTests : IDisposable
{
    private readonly string _tempDir;

    public BrowserSupportServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"easydict-bss-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    // ───────────────────── ComputeSha256 ─────────────────────

    [Fact]
    public void ComputeSha256_ReturnsCorrectHash()
    {
        var filePath = Path.Combine(_tempDir, "test.bin");
        File.WriteAllText(filePath, "hello world");

        var hash = BrowserSupportService.ComputeSha256(filePath);

        // SHA256("hello world") = b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9
        hash.Should().Be("b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9");
    }

    [Fact]
    public void ComputeSha256_EmptyFile_ReturnsEmptyHash()
    {
        var filePath = Path.Combine(_tempDir, "empty.bin");
        File.WriteAllBytes(filePath, Array.Empty<byte>());

        var hash = BrowserSupportService.ComputeSha256(filePath);

        // SHA256 of empty data = e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
        hash.Should().Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    [Fact]
    public void ComputeSha256_ReturnsLowercaseHex()
    {
        var filePath = Path.Combine(_tempDir, "test.bin");
        File.WriteAllText(filePath, "test");

        var hash = BrowserSupportService.ComputeSha256(filePath);

        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    // ───────────────────── VerifyChecksum ─────────────────────

    [Fact]
    public void VerifyChecksum_ValidHash_DoesNotThrow()
    {
        var filePath = Path.Combine(_tempDir, "BrowserHostRegistrar-x64.exe");
        File.WriteAllText(filePath, "fake-registrar-content");

        var actualHash = BrowserSupportService.ComputeSha256(filePath);
        var checksumContent = $"{actualHash} *BrowserHostRegistrar-x64.exe\n";

        var act = () => BrowserSupportService.VerifyChecksum(
            checksumContent, "BrowserHostRegistrar-x64.exe", filePath);

        act.Should().NotThrow();
    }

    [Fact]
    public void VerifyChecksum_InvalidHash_Throws()
    {
        var filePath = Path.Combine(_tempDir, "BrowserHostRegistrar-x64.exe");
        File.WriteAllText(filePath, "fake-registrar-content");

        var checksumContent = "0000000000000000000000000000000000000000000000000000000000000000 *BrowserHostRegistrar-x64.exe\n";

        var act = () => BrowserSupportService.VerifyChecksum(
            checksumContent, "BrowserHostRegistrar-x64.exe", filePath);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*checksum mismatch*");
    }

    [Fact]
    public void VerifyChecksum_MissingEntry_DoesNotThrow()
    {
        var filePath = Path.Combine(_tempDir, "test.exe");
        File.WriteAllText(filePath, "content");

        var checksumContent = "abc123 *some-other-file.exe\n";

        // When entry not found, verification is skipped (no throw)
        var act = () => BrowserSupportService.VerifyChecksum(
            checksumContent, "test.exe", filePath);

        act.Should().NotThrow();
    }

    [Fact]
    public void VerifyChecksum_CaseInsensitiveFileName()
    {
        var filePath = Path.Combine(_tempDir, "test.exe");
        File.WriteAllText(filePath, "content");

        var actualHash = BrowserSupportService.ComputeSha256(filePath);
        var checksumContent = $"{actualHash} *TEST.EXE\n";

        var act = () => BrowserSupportService.VerifyChecksum(
            checksumContent, "TEST.EXE", filePath);

        act.Should().NotThrow();
    }

    [Fact]
    public void VerifyChecksum_MultipleEntries_MatchesCorrectFile()
    {
        var filePath = Path.Combine(_tempDir, "registrar.exe");
        File.WriteAllText(filePath, "registrar-content");

        var actualHash = BrowserSupportService.ComputeSha256(filePath);
        var checksumContent =
            "aaaa *bridge.exe\n" +
            $"{actualHash} *registrar.exe\n" +
            "bbbb *other.exe\n";

        var act = () => BrowserSupportService.VerifyChecksum(
            checksumContent, "registrar.exe", filePath);

        act.Should().NotThrow();
    }

    [Fact]
    public void VerifyChecksum_CaseInsensitiveHash()
    {
        var filePath = Path.Combine(_tempDir, "test.exe");
        File.WriteAllText(filePath, "content");

        var actualHash = BrowserSupportService.ComputeSha256(filePath);
        var upperHash = actualHash.ToUpperInvariant();
        var checksumContent = $"{upperHash} *test.exe\n";

        var act = () => BrowserSupportService.VerifyChecksum(
            checksumContent, "test.exe", filePath);

        act.Should().NotThrow();
    }

    // ───────────────────── GetPlatform ─────────────────────

    [Fact]
    public void GetPlatform_ReturnsKnownPlatform()
    {
        var platform = BrowserSupportService.GetPlatform();

        platform.Should().BeOneOf("x64", "x86", "arm64");
    }

    // ───────────────────── FindLatestRegistrarReleaseAsync ─────────────────────

    [Fact]
    public async Task FindLatestRegistrarRelease_NoVrRelease_Throws()
    {
        // Mock HTTP response with no vr-* tags
        var handler = new MockHttpMessageHandler("""
            [
                {
                    "tag_name": "v1.0.0",
                    "assets": [
                        { "name": "Easydict-v1.0.0.msix", "browser_download_url": "https://example.com/easydict.msix" }
                    ]
                }
            ]
            """);
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("test");

        var act = () => BrowserSupportService.FindLatestRegistrarReleaseAsync(client, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No registrar release*vr-*");
    }

    [Fact]
    public async Task FindLatestRegistrarRelease_WithVrRelease_ReturnsUrls()
    {
        var platform = BrowserSupportService.GetPlatform();
        var handler = new MockHttpMessageHandler($$"""
            [
                {
                    "tag_name": "v1.0.0",
                    "assets": [
                        { "name": "Easydict-v1.0.0.msix", "browser_download_url": "https://example.com/easydict.msix" }
                    ]
                },
                {
                    "tag_name": "vr-1.0.0-1",
                    "assets": [
                        { "name": "BrowserHostRegistrar-{{platform}}.exe", "browser_download_url": "https://example.com/registrar.exe" },
                        { "name": "browser-support-{{platform}}.sha256", "browser_download_url": "https://example.com/checksums.sha256" }
                    ]
                }
            ]
            """);
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("test");

        var (registrarUrl, checksumUrl) = await BrowserSupportService.FindLatestRegistrarReleaseAsync(
            client, CancellationToken.None);

        registrarUrl.Should().Be("https://example.com/registrar.exe");
        checksumUrl.Should().Be("https://example.com/checksums.sha256");
    }

    [Fact]
    public async Task FindLatestRegistrarRelease_NoChecksumAsset_ReturnsNullChecksum()
    {
        var platform = BrowserSupportService.GetPlatform();
        var handler = new MockHttpMessageHandler($$"""
            [
                {
                    "tag_name": "vr-1.0.0-1",
                    "assets": [
                        { "name": "BrowserHostRegistrar-{{platform}}.exe", "browser_download_url": "https://example.com/registrar.exe" }
                    ]
                }
            ]
            """);
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("test");

        var (registrarUrl, checksumUrl) = await BrowserSupportService.FindLatestRegistrarReleaseAsync(
            client, CancellationToken.None);

        registrarUrl.Should().Be("https://example.com/registrar.exe");
        checksumUrl.Should().BeNull();
    }

    [Fact]
    public async Task FindLatestRegistrarRelease_PicksFirstVrRelease()
    {
        var platform = BrowserSupportService.GetPlatform();
        var handler = new MockHttpMessageHandler($$"""
            [
                {
                    "tag_name": "vr-2.0.0-1",
                    "assets": [
                        { "name": "BrowserHostRegistrar-{{platform}}.exe", "browser_download_url": "https://example.com/registrar-v2.exe" }
                    ]
                },
                {
                    "tag_name": "vr-1.0.0-1",
                    "assets": [
                        { "name": "BrowserHostRegistrar-{{platform}}.exe", "browser_download_url": "https://example.com/registrar-v1.exe" }
                    ]
                }
            ]
            """);
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("test");

        var (registrarUrl, _) = await BrowserSupportService.FindLatestRegistrarReleaseAsync(
            client, CancellationToken.None);

        registrarUrl.Should().Be("https://example.com/registrar-v2.exe");
    }

    /// <summary>
    /// Minimal mock HTTP handler that returns a fixed JSON response for any request.
    /// </summary>
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseJson;

        public MockHttpMessageHandler(string responseJson)
        {
            _responseJson = responseJson;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_responseJson, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
