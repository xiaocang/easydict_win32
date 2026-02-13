using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for BrowserSupportService — checksum verification, SHA256 computation,
/// platform detection, and download URL generation.
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

    // ───────────────────── Download URLs (vr-latest) ─────────────────────

    [Fact]
    public void GetRegistrarDownloadUrl_ContainsVrLatestTag()
    {
        var url = BrowserSupportService.GetRegistrarDownloadUrl();

        url.Should().Contain("/releases/download/vr-latest/");
    }

    [Fact]
    public void GetRegistrarDownloadUrl_ContainsPlatform()
    {
        var platform = BrowserSupportService.GetPlatform();
        var url = BrowserSupportService.GetRegistrarDownloadUrl();

        url.Should().Contain($"BrowserHostRegistrar-{platform}.exe");
    }

    [Fact]
    public void GetRegistrarDownloadUrl_IsFullGitHubUrl()
    {
        var url = BrowserSupportService.GetRegistrarDownloadUrl();

        url.Should().StartWith("https://github.com/");
        url.Should().EndWith(".exe");
    }

    [Fact]
    public void GetChecksumDownloadUrl_ContainsVrLatestTag()
    {
        var url = BrowserSupportService.GetChecksumDownloadUrl();

        url.Should().Contain("/releases/download/vr-latest/");
    }

    [Fact]
    public void GetChecksumDownloadUrl_ContainsPlatform()
    {
        var platform = BrowserSupportService.GetPlatform();
        var url = BrowserSupportService.GetChecksumDownloadUrl();

        url.Should().Contain($"browser-support-{platform}.sha256");
    }

    [Fact]
    public void GetChecksumDownloadUrl_IsFullGitHubUrl()
    {
        var url = BrowserSupportService.GetChecksumDownloadUrl();

        url.Should().StartWith("https://github.com/");
        url.Should().EndWith(".sha256");
    }

    [Fact]
    public void DownloadUrls_ShareSameRepoAndTag()
    {
        var registrarUrl = BrowserSupportService.GetRegistrarDownloadUrl();
        var checksumUrl = BrowserSupportService.GetChecksumDownloadUrl();

        // Both URLs should share the same base (repo + vr-latest tag)
        var registrarBase = registrarUrl[..registrarUrl.LastIndexOf('/')];
        var checksumBase = checksumUrl[..checksumUrl.LastIndexOf('/')];

        registrarBase.Should().Be(checksumBase);
    }
}
