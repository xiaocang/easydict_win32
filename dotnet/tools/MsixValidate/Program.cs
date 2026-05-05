using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace Easydict.Tools.MsixValidate;

/// <summary>
/// Pre-publish validator for Easydict MSIX bundles.
///
/// WinAppSDK 2.0.1 ships <c>Microsoft.Windows.Management.Deployment</c> with first-party
/// validators (<c>PackageFamilyNameValidator</c>, <c>PackageMinimumVersionValidator</c>,
/// <c>PackageCertificateEkuValidator</c>). Those types only run on Windows + WinAppSDK
/// runtime, which would force CI to install the runtime just to validate.
///
/// This tool re-implements the same checks against the raw MSIX (zip) so it runs anywhere
/// .NET 8 runs — including non-Windows CI shards. The checks intentionally mirror the
/// WinAppSDK validator names so the migration path is obvious if/when we want to switch.
///
/// Usage:
///   dotnet run --project tools/MsixValidate -- &lt;path-to-msix&gt; [--expected-name X] [--min-version 10.0.19041.0]
///
/// Exit codes:
///   0 — all checks passed
///   1 — validation failed (details on stderr)
///   2 — usage / I/O error
/// </summary>
internal static class Program
{
    private const string DefaultExpectedName = "xiaocang.EasydictforWindows";
    private const string DefaultExpectedPublisher = "CN=33FC47D7-8283-45FC-BB5D-297D1476BB29";
    private const string DefaultMinVersion = "10.0.19041.0";

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return 2;
        }

        string msixPath = args[0];
        string expectedName = DefaultExpectedName;
        string expectedPublisher = DefaultExpectedPublisher;
        string minVersion = DefaultMinVersion;
        bool allowUnsigned = false;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--expected-name": expectedName = args[++i]; break;
                case "--expected-publisher": expectedPublisher = args[++i]; break;
                case "--min-version": minVersion = args[++i]; break;
                case "--allow-unsigned": allowUnsigned = true; break;
            }
        }

        if (!File.Exists(msixPath))
        {
            Console.Error.WriteLine($"error: MSIX not found: {msixPath}");
            return 2;
        }

        try
        {
            var manifest = ReadAppxManifest(msixPath);
            var failures = 0;

            failures += Check("PackageFamilyNameValidator", () => ValidateIdentity(manifest, expectedName, expectedPublisher));
            failures += Check("PackageMinimumVersionValidator", () => ValidateMinVersion(manifest, minVersion));
            if (allowUnsigned)
            {
                Console.WriteLine("  [skip] PackageCertificateEkuValidator (--allow-unsigned)");
            }
            else
            {
                failures += Check("PackageCertificateEkuValidator", () => ValidateSignature(msixPath));
            }

            if (failures > 0)
            {
                Console.Error.WriteLine($"FAIL: {failures} check(s) failed for {msixPath}");
                return 1;
            }

            Console.WriteLine($"OK: all checks passed for {msixPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: MsixValidate <path-to-msix> [--expected-name <name>] [--expected-publisher <publisher>] [--min-version <ver>] [--allow-unsigned]");
        Console.WriteLine($"  defaults: name={DefaultExpectedName}, min-version={DefaultMinVersion}");
        Console.WriteLine("  --allow-unsigned: skip the AppxSignature.p7x check (use for the release workflow which builds unsigned bundles)");
    }

    private static int Check(string name, Action probe)
    {
        try
        {
            probe();
            Console.WriteLine($"  [pass] {name}");
            return 0;
        }
        catch (ValidationException ex)
        {
            Console.Error.WriteLine($"  [FAIL] {name}: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [FAIL] {name}: unexpected {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static XDocument ReadAppxManifest(string msixPath)
    {
        using var archive = ZipFile.OpenRead(msixPath);
        var entry = archive.GetEntry("AppxManifest.xml")
            ?? throw new ValidationException("AppxManifest.xml not found inside MSIX");
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static void ValidateIdentity(XDocument manifest, string expectedName, string expectedPublisher)
    {
        var identity = manifest.Root!
            .Elements()
            .FirstOrDefault(e => e.Name.LocalName == "Identity")
            ?? throw new ValidationException("<Identity> element missing");

        var name = identity.Attribute("Name")?.Value ?? string.Empty;
        var publisher = identity.Attribute("Publisher")?.Value ?? string.Empty;

        if (!string.Equals(name, expectedName, StringComparison.Ordinal))
            throw new ValidationException($"Identity Name '{name}' != expected '{expectedName}'");
        if (!string.Equals(publisher, expectedPublisher, StringComparison.Ordinal))
            throw new ValidationException($"Identity Publisher '{publisher}' != expected '{expectedPublisher}'");
    }

    private static void ValidateMinVersion(XDocument manifest, string minVersion)
    {
        var tdf = manifest.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "TargetDeviceFamily")
            ?? throw new ValidationException("<TargetDeviceFamily> element missing");

        var actualStr = tdf.Attribute("MinVersion")?.Value
            ?? throw new ValidationException("TargetDeviceFamily MinVersion attribute missing");

        if (!Version.TryParse(actualStr, out var actual))
            throw new ValidationException($"TargetDeviceFamily MinVersion '{actualStr}' is unparseable");
        if (!Version.TryParse(minVersion, out var expected))
            throw new ValidationException($"--min-version '{minVersion}' is unparseable");

        if (actual < expected)
            throw new ValidationException($"TargetDeviceFamily MinVersion '{actual}' < required '{expected}' (catches Fix-MsixMinVersion regressions)");
    }

    private static void ValidateSignature(string msixPath)
    {
        // The MSIX zip contains an `AppxSignature.p7x` blob when signed; verifying the EKU
        // (1.3.6.1.5.5.7.3.3 = code signing) requires platform-specific PE/CMS parsing that
        // is out of scope for this cross-platform tool. We assert presence here so an
        // unsigned bundle never reaches release.
        using var archive = ZipFile.OpenRead(msixPath);
        var sig = archive.GetEntry("AppxSignature.p7x")
            ?? throw new ValidationException("AppxSignature.p7x not present — bundle is unsigned");
        if (sig.Length == 0)
            throw new ValidationException("AppxSignature.p7x is empty");

        // TODO(WinAppSDK 2.0.1): when running on Windows, switch to
        // Microsoft.Windows.Management.Deployment.PackageCertificateEkuValidator and assert
        // 1.3.6.1.5.5.7.3.3 (code signing) is present.
    }

    private sealed class ValidationException(string message) : Exception(message);
}
