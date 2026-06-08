using Easydict.WinUI.Services;
using FluentAssertions;
using System.Text.Json;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for BrowserSupportService — status detection helpers.
/// Most functionality (registry, process launch, file deploy) requires Windows
/// and is covered by integration / UI automation tests.
/// </summary>
public class BrowserSupportServiceTests
{
    [Fact]
    public void IsChromeSupportInstalled_ReturnsBool()
    {
        // Should not throw — just returns false when registry key is absent
        var result = BrowserSupportService.IsChromeSupportInstalled;
        result.Should().BeFalse();
    }

    [Fact]
    public void IsFirefoxSupportInstalled_ReturnsBool()
    {
        var result = BrowserSupportService.IsFirefoxSupportInstalled;
        result.Should().BeFalse();
    }

    [Fact]
    public void ManifestReferencesExpectedBridge_ReturnsTrueForOwnedManifest()
    {
        using var sandbox = new BrowserManifestSandbox();
        var bridgePath = sandbox.WriteBridge("dotnet", "easydict-native-bridge.exe");
        var manifestPath = sandbox.WriteManifest("chrome-manifest.json", bridgePath);

        BrowserSupportService.ManifestReferencesExpectedBridge(manifestPath, bridgePath)
            .Should().BeTrue();
    }

    [Fact]
    public void ManifestReferencesExpectedBridge_ReturnsFalseForForeignBridgePath()
    {
        using var sandbox = new BrowserManifestSandbox();
        var dotnetBridgePath = sandbox.WriteBridge("dotnet", "easydict-native-bridge.exe");
        var rustBridgePath = sandbox.WriteBridge("rs", "easydict-native-bridge.exe");
        var manifestPath = sandbox.WriteManifest("chrome-manifest.json", rustBridgePath);

        BrowserSupportService.ManifestReferencesExpectedBridge(manifestPath, dotnetBridgePath)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("not.easydict.bridge", "stdio")]
    [InlineData("com.easydict.bridge", "pipe")]
    public void ManifestReferencesExpectedBridge_ReturnsFalseForWrongNameOrType(
        string hostName,
        string manifestType)
    {
        using var sandbox = new BrowserManifestSandbox();
        var bridgePath = sandbox.WriteBridge("dotnet", "easydict-native-bridge.exe");
        var manifestPath = sandbox.WriteManifest("chrome-manifest.json", bridgePath, hostName, manifestType);

        BrowserSupportService.ManifestReferencesExpectedBridge(manifestPath, bridgePath)
            .Should().BeFalse();
    }

    private sealed class BrowserManifestSandbox : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"easydict_browser_manifest_{Environment.ProcessId}_{Guid.NewGuid():N}");

        public string WriteBridge(string directoryName, string fileName)
        {
            var directory = Path.Combine(_root, directoryName);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);
            File.WriteAllText(path, "bridge");
            return path;
        }

        public string WriteManifest(
            string fileName,
            string bridgePath,
            string hostName = "com.easydict.bridge",
            string manifestType = "stdio")
        {
            Directory.CreateDirectory(_root);
            var path = Path.Combine(_root, fileName);
            var json = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["name"] = hostName,
                ["description"] = "Easydict native messaging bridge",
                ["path"] = bridgePath,
                ["type"] = manifestType,
                ["allowed_origins"] = new[] { "chrome-extension://custom-chrome-extension/" }
            });
            File.WriteAllText(path, json);
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temp files that might be locked by antivirus scanners.
            }
        }
    }
}
