using System.Security.Cryptography;

namespace Easydict.SidecarClient.Protocol;

public enum PpOcrV6ModelState
{
    Missing,
    Installed,
    Invalid,
}

public sealed record PpOcrV6ModelPaths(
    string Directory,
    string DetectorModel,
    string DetectorConfig,
    string RecognizerModel,
    string RecognizerConfig,
    string CompletionSentinel);

public sealed class PpOcrV6ModelStore
{
    public const string ModelsDirectoryName = "PpOcrV6";
    public const string CompletionSentinelName = ".complete";
    public const string DetectorModelFileName = "det.onnx";
    public const string DetectorConfigFileName = "det.yml";
    public const string RecognizerModelFileName = "rec.onnx";
    public const string RecognizerConfigFileName = "rec.yml";

    public PpOcrV6ModelStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Easydict",
            "Models",
            ModelsDirectoryName);
    }

    public string RootDirectory { get; }

    public PpOcrV6ModelPaths GetPaths(string modelId)
    {
        var model = PpOcrV6ModelCatalog.Get(modelId);
        var directory = Path.Combine(RootDirectory, model.Id);
        return new PpOcrV6ModelPaths(
            directory,
            Path.Combine(directory, DetectorModelFileName),
            Path.Combine(directory, DetectorConfigFileName),
            Path.Combine(directory, RecognizerModelFileName),
            Path.Combine(directory, RecognizerConfigFileName),
            Path.Combine(directory, CompletionSentinelName));
    }

    public PpOcrV6ModelState GetStateByPresence(string modelId)
    {
        var paths = GetPaths(modelId);
        if (!File.Exists(paths.CompletionSentinel))
        {
            return PpOcrV6ModelState.Missing;
        }

        var artifacts = PpOcrV6ModelCatalog.Get(modelId).Artifacts;
        if (artifacts.Any(artifact => !File.Exists(Path.Combine(paths.Directory, artifact.FileName))))
        {
            return PpOcrV6ModelState.Invalid;
        }

        return PpOcrV6ModelState.Installed;
    }

    public PpOcrV6ModelState GetStateBySize(string modelId)
    {
        var presence = GetStateByPresence(modelId);
        if (presence != PpOcrV6ModelState.Installed)
        {
            return presence;
        }

        var paths = GetPaths(modelId);
        foreach (var artifact in PpOcrV6ModelCatalog.Get(modelId).Artifacts)
        {
            var path = Path.Combine(paths.Directory, artifact.FileName);
            if (!File.Exists(path) || new FileInfo(path).Length != artifact.SizeBytes)
            {
                return PpOcrV6ModelState.Invalid;
            }
        }

        return PpOcrV6ModelState.Installed;
    }

    public async Task<PpOcrV6ModelState> ValidateAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        var presence = GetStateByPresence(modelId);
        if (presence != PpOcrV6ModelState.Installed)
        {
            return presence;
        }

        var model = PpOcrV6ModelCatalog.Get(modelId);
        var paths = GetPaths(modelId);
        foreach (var artifact in model.Artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(paths.Directory, artifact.FileName);
            var info = new FileInfo(path);
            if (info.Length != artifact.SizeBytes)
            {
                return PpOcrV6ModelState.Invalid;
            }

            await using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(
                await sha.ComputeHashAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(hash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return PpOcrV6ModelState.Invalid;
            }
        }

        return PpOcrV6ModelState.Installed;
    }

    public void PrepareRoot()
    {
        Directory.CreateDirectory(RootDirectory);
    }

    public void InvalidateCompletion(string modelId)
    {
        var paths = GetPaths(modelId);
        try
        {
            File.Delete(paths.CompletionSentinel);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

}
