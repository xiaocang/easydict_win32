using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public sealed class LocalCredentialProtectorTests
{
    [Fact]
    public void Protect_WithCurrentUserScope_ShouldRoundTripWithoutPlaintextStorage()
    {
        const string plaintext = "sk-test-local-api-key";

        var protectedValue = LocalCredentialProtector.Protect(plaintext);

        protectedValue.Should().StartWith("edcred1:user:");
        protectedValue.Should().NotContain(plaintext);
        LocalCredentialProtector.TryUnprotect(protectedValue, out var unprotected)
            .Should().BeTrue();
        unprotected.Should().Be(plaintext);
    }

    [Fact]
    public void Protect_WithLocalMachineScope_ShouldRecordMachineScope()
    {
        const string plaintext = "sk-test-shared-api-key";

        var protectedValue = LocalCredentialProtector.Protect(
            plaintext,
            LocalCredentialProtector.CredentialProtectionScope.LocalMachine);

        protectedValue.Should().StartWith("edcred1:machine:");
        protectedValue.Should().NotContain(plaintext);
        LocalCredentialProtector.TryUnprotect(protectedValue, out var unprotected)
            .Should().BeTrue();
        unprotected.Should().Be(plaintext);
    }

    [Fact]
    public void TryUnprotect_WithTamperedProtectedValue_ShouldFail()
    {
        var protectedValue = LocalCredentialProtector.Protect("sk-test-local-api-key");
        var tamperedValue = protectedValue[..^4] + "AAAA";

        LocalCredentialProtector.TryUnprotect(tamperedValue, out var unprotected)
            .Should().BeFalse();
        unprotected.Should().BeEmpty();
    }

    [Fact]
    public void IsProtected_ShouldOnlyMatchVersionedProtectedValues()
    {
        LocalCredentialProtector.IsProtected("plain-old-api-key").Should().BeFalse();
        LocalCredentialProtector.IsProtected("").Should().BeFalse();
        LocalCredentialProtector.IsProtected(null).Should().BeFalse();

        var protectedValue = LocalCredentialProtector.Protect("plain-old-api-key");
        LocalCredentialProtector.IsProtected(protectedValue).Should().BeTrue();
    }

    [Fact]
    public void UnprotectOrReturnPlaintext_WithLegacyPlaintext_ShouldRequestMigration()
    {
        var value = LocalCredentialProtector.UnprotectOrReturnPlaintext(
            "plain-old-api-key",
            "stable-machine-id",
            out var needsMigration,
            out var decryptFailed);

        value.Should().Be("plain-old-api-key");
        needsMigration.Should().BeTrue();
        decryptFailed.Should().BeFalse();
    }

    [Fact]
    public void UnprotectOrReturnPlaintext_WithProtectedValue_ShouldReturnPlaintextWithoutMigration()
    {
        var protectedValue = LocalCredentialProtector.Protect("sk-test-local-api-key");

        var value = LocalCredentialProtector.UnprotectOrReturnPlaintext(
            protectedValue,
            "stable-machine-id",
            out var needsMigration,
            out var decryptFailed);

        value.Should().Be("sk-test-local-api-key");
        needsMigration.Should().BeFalse();
        decryptFailed.Should().BeFalse();
    }

    [Fact]
    public void UnprotectOrReturnPlaintext_WithNestedProtectedValue_ShouldReturnPlaintextAndRequestMigration()
    {
        var innerProtectedValue = LocalCredentialProtector.Protect("sk-test-local-api-key");
        var nestedProtectedValue = LocalCredentialProtector.Protect(innerProtectedValue);

        var value = LocalCredentialProtector.UnprotectOrReturnPlaintext(
            nestedProtectedValue,
            "stable-machine-id",
            out var needsMigration,
            out var decryptFailed);

        value.Should().Be("sk-test-local-api-key");
        needsMigration.Should().BeTrue();
        decryptFailed.Should().BeFalse();
    }

    [Fact]
    public void TryUnprotect_WithNestedProtectedValue_ShouldReturnFinalPlaintext()
    {
        var innerProtectedValue = LocalCredentialProtector.Protect("sk-test-local-api-key");
        var nestedProtectedValue = LocalCredentialProtector.Protect(innerProtectedValue);

        LocalCredentialProtector.TryUnprotect(nestedProtectedValue, out var unprotected)
            .Should().BeTrue();
        unprotected.Should().Be("sk-test-local-api-key");
    }

    [Fact]
    public void UnprotectOrReturnPlaintext_WithTooManyNestedProtectedValues_ShouldFail()
    {
        var nestedValue = "sk-test-local-api-key";
        for (var i = 0; i <= LocalCredentialProtector.MaxNestedProtectedValueDepth; i++)
        {
            nestedValue = LocalCredentialProtector.Protect(nestedValue);
        }

        var value = LocalCredentialProtector.UnprotectOrReturnPlaintext(
            nestedValue,
            "stable-machine-id",
            out _,
            out var decryptFailed);

        value.Should().BeNull();
        decryptFailed.Should().BeTrue();
    }

    [Fact]
    public void UnprotectOrReturnPlaintext_WithLegacyProtectedValue_ShouldRequestMigration()
    {
        var protectedValue = LocalCredentialProtector.ProtectLegacy(
            "sk-test-local-api-key",
            "stable-machine-id");

        var value = LocalCredentialProtector.UnprotectOrReturnPlaintext(
            protectedValue,
            "stable-machine-id",
            out var needsMigration,
            out var decryptFailed);

        value.Should().Be("sk-test-local-api-key");
        needsMigration.Should().BeTrue();
        decryptFailed.Should().BeFalse();
    }

    [Fact]
    public void TryUnprotectLegacy_WithDifferentMachineId_ShouldFail()
    {
        var protectedValue = LocalCredentialProtector.ProtectLegacy(
            "sk-test-local-api-key",
            "stable-machine-id");

        LocalCredentialProtector.TryUnprotectLegacy(
                protectedValue,
                "different-machine-id",
                out var unprotected)
            .Should().BeFalse();
        unprotected.Should().BeEmpty();
    }

    [Fact]
    public void GetOrCreatePersistedMachineId_WithExistingMachineIdFile_ShouldReturnExistingValue()
    {
        using var testDirectory = new TemporaryDirectory();
        var path = Path.Combine(testDirectory.Path, LocalCredentialProtector.MachineIdFileName);
        File.WriteAllText(path, "persisted-machine-id");

        var machineId = LocalCredentialProtector.GetOrCreatePersistedMachineId(testDirectory.Path);

        machineId.Should().Be("persisted-machine-id");
    }

    [Fact]
    public void GetOrCreatePersistedMachineId_WithLegacyMachineIdFile_ShouldMigrateValue()
    {
        using var testDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(testDirectory.Path, "local-machine-id"), "legacy-machine-id");

        var machineId = LocalCredentialProtector.GetOrCreatePersistedMachineId(testDirectory.Path);

        machineId.Should().Be("legacy-machine-id");
        File.ReadAllText(Path.Combine(testDirectory.Path, LocalCredentialProtector.MachineIdFileName))
            .Should().Be("legacy-machine-id");
    }

    [Fact]
    public void GetOrCreatePersistedMachineId_WithoutExistingFile_ShouldCreateMachineIdFile()
    {
        using var testDirectory = new TemporaryDirectory();

        var machineId = LocalCredentialProtector.GetOrCreatePersistedMachineId(testDirectory.Path);

        machineId.Should().NotBeNullOrWhiteSpace();
        File.ReadAllText(Path.Combine(testDirectory.Path, LocalCredentialProtector.MachineIdFileName))
            .Should().Be(machineId);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"easydict-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
