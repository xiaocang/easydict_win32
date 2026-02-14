using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Easydict.BrowserRegistrar.Tests;

/// <summary>
/// Tests for BrowserRegistrarCore — uses temp directories, no side-effects on real system.
/// </summary>
public class BrowserRegistrarCoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _bridgeDir;
    private readonly string _sourceDir;
    private readonly BrowserRegistrarCore _core;

    public BrowserRegistrarCoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"easydict-test-{Guid.NewGuid():N}");
        _bridgeDir = Path.Combine(_tempDir, "bridge");
        _sourceDir = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(_bridgeDir);
        Directory.CreateDirectory(_sourceDir);

        _core = new BrowserRegistrarCore(_bridgeDir);
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

    private string CreateFakeBridge()
    {
        var bridgePath = Path.Combine(_sourceDir, BrowserRegistrarCore.BridgeExeName);
        File.WriteAllText(bridgePath, "fake-bridge-exe");
        return bridgePath;
    }

    // ───────────────────── Install ─────────────────────

    [Fact]
    public void Install_CopiesBridgeToDeployDir()
    {
        var sourceBridge = CreateFakeBridge();

        var result = _core.Install(chrome: true, firefox: false, sourceBridge,
            new[] { "test-chrome-ext-id" }, "test-firefox-ext-id");

        result.Success.Should().BeTrue();
        File.Exists(_core.BridgeExePath).Should().BeTrue();
        File.ReadAllText(_core.BridgeExePath).Should().Be("fake-bridge-exe");
    }

    [Fact]
    public void Install_ReturnsError_WhenBridgeNotFound()
    {
        var result = _core.Install(chrome: true, firefox: false,
            "/nonexistent/bridge.exe", new[] { "ext-id" }, "ext-id");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Bridge exe not found");
    }

    [Fact]
    public void Install_Chrome_WritesManifestJson()
    {
        var sourceBridge = CreateFakeBridge();

        var result = _core.Install(chrome: true, firefox: false, sourceBridge,
            new[] { "test-chrome-ext-id" }, "test-firefox-ext-id");

        result.Success.Should().BeTrue();
        result.Installed.Should().Contain("chrome");
        result.Installed.Should().NotContain("firefox");

        var manifestPath = Path.Combine(_bridgeDir, "chrome-manifest.json");
        File.Exists(manifestPath).Should().BeTrue();

        var json = File.ReadAllText(manifestPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("name").GetString().Should().Be(BrowserRegistrarCore.NativeHostName);
        root.GetProperty("type").GetString().Should().Be("stdio");
        root.GetProperty("path").GetString().Should().Be(_core.BridgeExePath);
        root.GetProperty("allowed_origins").GetArrayLength().Should().Be(1);
        root.GetProperty("allowed_origins")[0].GetString()
            .Should().Be("chrome-extension://test-chrome-ext-id/");
    }

    [Fact]
    public void Install_Firefox_WritesManifestJson()
    {
        var sourceBridge = CreateFakeBridge();

        var result = _core.Install(chrome: false, firefox: true, sourceBridge,
            new[] { "test-chrome-ext-id" }, "test-firefox-ext-id");

        result.Success.Should().BeTrue();
        result.Installed.Should().Contain("firefox");
        result.Installed.Should().NotContain("chrome");

        var manifestPath = Path.Combine(_bridgeDir, "firefox-manifest.json");
        File.Exists(manifestPath).Should().BeTrue();

        var json = File.ReadAllText(manifestPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("name").GetString().Should().Be(BrowserRegistrarCore.NativeHostName);
        root.GetProperty("type").GetString().Should().Be("stdio");
        root.GetProperty("path").GetString().Should().Be(_core.BridgeExePath);
        root.GetProperty("allowed_extensions").GetArrayLength().Should().Be(1);
        root.GetProperty("allowed_extensions")[0].GetString()
            .Should().Be("test-firefox-ext-id");
    }

    [Fact]
    public void Install_Both_WritesBothManifests()
    {
        var sourceBridge = CreateFakeBridge();

        var result = _core.Install(chrome: true, firefox: true, sourceBridge,
            new[] { "chrome-id" }, "firefox-id");

        result.Success.Should().BeTrue();
        result.Installed.Should().HaveCount(2);
        result.Installed.Should().Contain("chrome");
        result.Installed.Should().Contain("firefox");

        File.Exists(Path.Combine(_bridgeDir, "chrome-manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(_bridgeDir, "firefox-manifest.json")).Should().BeTrue();
    }

    [Fact]
    public void Install_OverwritesExistingBridge()
    {
        var sourceBridge = CreateFakeBridge();

        // First install
        _core.Install(chrome: true, firefox: false, sourceBridge, new[] { "id" }, "id");

        // Update source and reinstall
        File.WriteAllText(sourceBridge, "updated-bridge-exe");
        _core.Install(chrome: true, firefox: false, sourceBridge, new[] { "id" }, "id");

        File.ReadAllText(_core.BridgeExePath).Should().Be("updated-bridge-exe");
    }

    // ───────────────────── Uninstall ─────────────────────

    [Fact]
    public void Uninstall_Chrome_DeletesChromeManifest()
    {
        var sourceBridge = CreateFakeBridge();
        _core.Install(chrome: true, firefox: false, sourceBridge, new[] { "id" }, "id");

        File.Exists(Path.Combine(_bridgeDir, "chrome-manifest.json")).Should().BeTrue();

        _core.Uninstall(chrome: true, firefox: false);

        File.Exists(Path.Combine(_bridgeDir, "chrome-manifest.json")).Should().BeFalse();
    }

    [Fact]
    public void Uninstall_Firefox_DeletesFirefoxManifest()
    {
        var sourceBridge = CreateFakeBridge();
        _core.Install(chrome: false, firefox: true, sourceBridge, new[] { "id" }, "id");

        File.Exists(Path.Combine(_bridgeDir, "firefox-manifest.json")).Should().BeTrue();

        _core.Uninstall(chrome: false, firefox: true);

        File.Exists(Path.Combine(_bridgeDir, "firefox-manifest.json")).Should().BeFalse();
    }

    [Fact]
    public void Uninstall_ReturnsCorrectList()
    {
        var result = _core.Uninstall(chrome: true, firefox: true);

        result.Success.Should().BeTrue();
        result.Uninstalled.Should().Contain("chrome");
        result.Uninstalled.Should().Contain("firefox");
    }

    // ───────────────────── Status ─────────────────────

    [Fact]
    public void GetStatus_NothingInstalled()
    {
        var status = _core.GetStatus();

        status.ChromeInstalled.Should().BeFalse();
        status.FirefoxInstalled.Should().BeFalse();
        status.BridgeExists.Should().BeFalse();
        status.BridgeDirectory.Should().Be(_bridgeDir);
    }

    [Fact]
    public void GetStatus_BridgeExists_AfterInstall()
    {
        var sourceBridge = CreateFakeBridge();
        _core.Install(chrome: true, firefox: false, sourceBridge, new[] { "id" }, "id");

        var status = _core.GetStatus();

        status.BridgeExists.Should().BeTrue();
    }

    // ───────────────────── Manifest Content ─────────────────────

    [Fact]
    public void WriteChromeManifest_ProducesValidJson()
    {
        Directory.CreateDirectory(_bridgeDir);

        var manifestPath = _core.WriteChromeManifest(new[] { "test-ext-id" });

        File.Exists(manifestPath).Should().BeTrue();
        manifestPath.Should().EndWith("chrome-manifest.json");

        // Verify it's valid JSON
        var json = File.ReadAllText(manifestPath);
        var act = () => JsonDocument.Parse(json);
        act.Should().NotThrow();
    }

    [Fact]
    public void WriteFirefoxManifest_ProducesValidJson()
    {
        Directory.CreateDirectory(_bridgeDir);

        var manifestPath = _core.WriteFirefoxManifest("test-ext@example.com");

        File.Exists(manifestPath).Should().BeTrue();
        manifestPath.Should().EndWith("firefox-manifest.json");

        var json = File.ReadAllText(manifestPath);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("allowed_extensions")[0].GetString()
            .Should().Be("test-ext@example.com");
    }

    [Fact]
    public void ChromeManifest_HasCorrectStructure()
    {
        Directory.CreateDirectory(_bridgeDir);

        _core.WriteChromeManifest(new[] { "abc123" });

        var json = File.ReadAllText(Path.Combine(_bridgeDir, "chrome-manifest.json"));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Chrome requires these exact fields
        root.TryGetProperty("name", out _).Should().BeTrue();
        root.TryGetProperty("description", out _).Should().BeTrue();
        root.TryGetProperty("path", out _).Should().BeTrue();
        root.TryGetProperty("type", out _).Should().BeTrue();
        root.TryGetProperty("allowed_origins", out _).Should().BeTrue();

        // Firefox-only field should NOT be present
        root.TryGetProperty("allowed_extensions", out _).Should().BeFalse();
    }

    [Fact]
    public void FirefoxManifest_HasCorrectStructure()
    {
        Directory.CreateDirectory(_bridgeDir);

        _core.WriteFirefoxManifest("ext@example.com");

        var json = File.ReadAllText(Path.Combine(_bridgeDir, "firefox-manifest.json"));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Firefox requires these exact fields
        root.TryGetProperty("name", out _).Should().BeTrue();
        root.TryGetProperty("description", out _).Should().BeTrue();
        root.TryGetProperty("path", out _).Should().BeTrue();
        root.TryGetProperty("type", out _).Should().BeTrue();
        root.TryGetProperty("allowed_extensions", out _).Should().BeTrue();

        // Chrome-only field should NOT be present
        root.TryGetProperty("allowed_origins", out _).Should().BeFalse();
    }

    // ───────────────────── Multiple Chrome Extension IDs ─────────────────────

    [Fact]
    public void Install_Chrome_MultipleExtIds_WritesAllAllowedOrigins()
    {
        var sourceBridge = CreateFakeBridge();
        var extIds = new[] { "store-id-abc", "sideloaded-id-xyz" };

        var result = _core.Install(chrome: true, firefox: false, sourceBridge,
            extIds, "test-firefox-ext-id");

        result.Success.Should().BeTrue();

        var manifestPath = Path.Combine(_bridgeDir, "chrome-manifest.json");
        var json = File.ReadAllText(manifestPath);
        using var doc = JsonDocument.Parse(json);
        var origins = doc.RootElement.GetProperty("allowed_origins");

        origins.GetArrayLength().Should().Be(2);
        origins[0].GetString().Should().Be("chrome-extension://store-id-abc/");
        origins[1].GetString().Should().Be("chrome-extension://sideloaded-id-xyz/");
    }

    [Fact]
    public void WriteChromeManifest_MultipleIds_ProducesCorrectAllowedOrigins()
    {
        Directory.CreateDirectory(_bridgeDir);
        var extIds = new[] { "id-one", "id-two", "id-three" };

        _core.WriteChromeManifest(extIds);

        var json = File.ReadAllText(Path.Combine(_bridgeDir, "chrome-manifest.json"));
        using var doc = JsonDocument.Parse(json);
        var origins = doc.RootElement.GetProperty("allowed_origins");

        origins.GetArrayLength().Should().Be(3);
        origins[0].GetString().Should().Be("chrome-extension://id-one/");
        origins[1].GetString().Should().Be("chrome-extension://id-two/");
        origins[2].GetString().Should().Be("chrome-extension://id-three/");
    }

    // ───────────────────── BridgeExePath ─────────────────────

    [Fact]
    public void BridgeExePath_CombinesDirectoryAndExeName()
    {
        _core.BridgeExePath.Should().Be(
            Path.Combine(_bridgeDir, BrowserRegistrarCore.BridgeExeName));
    }
}
