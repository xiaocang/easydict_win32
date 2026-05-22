using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Easydict.OpenVINO.Models;

namespace Easydict.OpenVINO.Services;

/// <summary>
/// Downloads the Intel ONNX Runtime + OpenVINO native runtime on first use.
/// The NuGet package's native assets are intentionally excluded from publish
/// output so MSIX packages stay small.
/// </summary>
public sealed class OpenVinoRuntimeDownloadService : IDisposable
{
    public const string PackageVersion = "1.21.0";
    public const string RuntimeIdentifier = "win-x64";
    public const string CompletionSentinel = ".complete";

    private const string PackageUrl =
        "https://www.nuget.org/api/v2/package/Intel.ML.OnnxRuntime.OpenVino/" + PackageVersion;
    private const string PackageSha256 =
        "a70be78c7ce5c0ff82538f8934fffaafa5f63409ee0604d3990c8b393e178e15";

    private readonly HttpClient _httpClient;
    private readonly string _cacheRoot;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public OpenVinoRuntimeDownloadService()
        : this(new HttpClient { Timeout = TimeSpan.FromMinutes(10) }, DefaultCacheRoot(), ownsHttpClient: true)
    {
    }

    internal OpenVinoRuntimeDownloadService(HttpClient httpClient, string cacheRoot)
        : this(httpClient, cacheRoot, ownsHttpClient: false)
    {
    }

    private OpenVinoRuntimeDownloadService(HttpClient httpClient, string cacheRoot, bool ownsHttpClient)
    {
        _httpClient = httpClient;
        _cacheRoot = cacheRoot;
        _ownsHttpClient = ownsHttpClient;
    }

    public string NativeDirectory =>
        Path.Combine(_cacheRoot, "openvino", PackageVersion, RuntimeIdentifier, "native");

    public bool IsSupportedCurrentArchitecture =>
        OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    public bool IsRuntimeInstalled()
    {
        if (!IsSupportedCurrentArchitecture)
        {
            return false;
        }

        var sentinel = Path.Combine(NativeDirectory, CompletionSentinel);
        if (!File.Exists(sentinel))
        {
            return false;
        }

        foreach (var file in OpenVinoRuntimeManifest.NativeFiles)
        {
            if (!File.Exists(Path.Combine(NativeDirectory, file)))
            {
                return false;
            }
        }

        return true;
    }

    public void EnsureNativeDirectoryOnPath()
    {
        if (!IsSupportedCurrentArchitecture)
        {
            return;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var nativeDir = Path.GetFullPath(NativeDirectory);
        var entries = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (entries.Any(e => PathsEqual(e, nativeDir)))
        {
            return;
        }

        Environment.SetEnvironmentVariable(
            "PATH",
            NativeDirectory + Path.PathSeparator + path,
            EnvironmentVariableTarget.Process);
    }

    private static bool PathsEqual(string path, string expectedFullPath)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(path.Trim()),
                expectedFullPath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task DownloadAsync(
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsSupportedCurrentArchitecture)
        {
            throw new PlatformNotSupportedException(
                "OpenVINO local translation runtime is only available for Windows x64.");
        }

        if (IsRuntimeInstalled())
        {
            progress?.Report(new ModelDownloadProgress("openvino-runtime", 1, 1, 100));
            return;
        }

        var packageRoot = Path.GetFullPath(Path.Combine(NativeDirectory, "..", "..", ".."));
        Directory.CreateDirectory(packageRoot);
        Directory.CreateDirectory(NativeDirectory);

        var packagePath = Path.Combine(packageRoot, $"intel.ml.onnxruntime.openvino.{PackageVersion}.nupkg");
        var tmpPackagePath = packagePath + ".part";
        var extractRoot = Path.Combine(packageRoot, "_extract-" + Guid.NewGuid().ToString("N"));

        try
        {
            await DownloadPackageAsync(tmpPackagePath, progress, cancellationToken).ConfigureAwait(false);
            await VerifySha256Async(tmpPackagePath, cancellationToken).ConfigureAwait(false);

            ZipFile.ExtractToDirectory(tmpPackagePath, extractRoot);
            var sourceNativeDir = Path.Combine(extractRoot, "runtimes", RuntimeIdentifier, "native");
            if (!Directory.Exists(sourceNativeDir))
            {
                throw new InvalidDataException(
                    $"NuGet package does not contain expected native runtime directory: {sourceNativeDir}");
            }

            foreach (var file in OpenVinoRuntimeManifest.NativeFiles)
            {
                var source = Path.Combine(sourceNativeDir, file);
                if (!File.Exists(source))
                {
                    throw new InvalidDataException($"NuGet package is missing native runtime file: {file}");
                }

                File.Copy(source, Path.Combine(NativeDirectory, file), overwrite: true);
            }

            File.Move(tmpPackagePath, packagePath, overwrite: true);
            await File.WriteAllTextAsync(
                Path.Combine(NativeDirectory, CompletionSentinel),
                DateTimeOffset.UtcNow.ToString("O"),
                cancellationToken).ConfigureAwait(false);

            EnsureNativeDirectoryOnPath();
            progress?.Report(new ModelDownloadProgress("openvino-runtime", 1, 1, 100));
        }
        finally
        {
            TryDeleteFile(tmpPackagePath);
            TryDeleteDirectory(extractRoot);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task DownloadPackageAsync(
        string tmpPackagePath,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            PackageUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        long downloaded = 0;

        await using var network = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = File.Create(tmpPackagePath);

        var buffer = new byte[81_920];
        int read;
        while ((read = await network.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            downloaded += read;
            var percent = totalBytes is > 0
                ? Math.Clamp(downloaded * 100.0 / totalBytes.Value, 0.0, 100.0)
                : 0;
            progress?.Report(new ModelDownloadProgress("openvino-runtime", downloaded, totalBytes, percent));
        }

        if (totalBytes is { } expected && downloaded != expected)
        {
            throw new EndOfStreamException(
                $"Truncated OpenVINO runtime package download: expected {expected} bytes, got {downloaded}.");
        }
    }

    private static async Task VerifySha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var actual = Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
        var expected = PackageSha256.ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"SHA-256 mismatch for Intel.ML.OnnxRuntime.OpenVino {PackageVersion}. " +
                $"Expected {expected}, got {actual}.");
        }
    }

    private static string DefaultCacheRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "Easydict", "runtimes");
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
