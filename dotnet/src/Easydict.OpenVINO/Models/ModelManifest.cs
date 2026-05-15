using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Easydict.OpenVINO.Models;

/// <summary>
/// Static metadata describing what to download for the OpenVINO NLLB-200
/// provider. The mutable bits — <see cref="Revision"/> and the file list with
/// SHA-256 / size — live in the embedded
/// <c>nllb-200-distilled-600M.manifest.json</c> data file so they can be
/// updated without touching code. When pulling a new upstream snapshot, edit
/// that JSON file only.
/// </summary>
public static class ModelManifest
{
    public const string ModelId = "nllb-200-distilled-600M-int8";

    /// <summary>HuggingFace repo with ONNX + quantized variants of NLLB-200-distilled-600M.</summary>
    public const string HuggingFaceRepo = "Xenova/nllb-200-distilled-600M";

    /// <summary>Subdirectory under <c>%LOCALAPPDATA%\Easydict\models</c>.</summary>
    public const string CacheDirectoryName = "nllb-200-distilled-600M";

    /// <summary>Sentinel file written when a download completes successfully.</summary>
    public const string CompletionSentinel = ".complete";

    private const string ManifestResourceName =
        "Easydict.OpenVINO.Models.nllb-200-distilled-600M.manifest.json";

    private static readonly ManifestData _data = LoadEmbeddedManifest();

    /// <summary>
    /// Pinned HuggingFace commit SHA — immutable, so file hashes can't drift.
    /// Sourced from the embedded manifest JSON.
    /// </summary>
    public static string Revision => _data.Revision;

    /// <summary>
    /// Files to download, sourced from the embedded manifest JSON.
    /// <c>ApproximateBytes</c> matches the upstream LFS metadata at
    /// <see cref="Revision"/> and is used for progress aggregation.
    /// <c>Sha256</c> (when non-null) is verified by
    /// <see cref="Services.ModelDownloadService"/> after each file is written;
    /// mismatches delete the file and throw — defending against supply-chain
    /// tampering and corrupted transfers. The non-merged decoder is intentional:
    /// the first cut skips KV-cache plumbing (every step re-runs the decoder on
    /// the full output prefix — O(N²) but simple). A future iteration can
    /// switch to <c>decoder_model_merged_quantized.onnx</c> for incremental
    /// decoding.
    /// </summary>
    public static IReadOnlyList<ModelFileEntry> Files => _data.Files;

    /// <summary>
    /// Builds the HuggingFace <c>resolve</c> URL for a given file at the pinned revision.
    /// </summary>
    public static string GetDownloadUrl(string remoteRelativePath)
    {
        return $"https://huggingface.co/{HuggingFaceRepo}/resolve/{Revision}/{remoteRelativePath}";
    }

    private static ManifestData LoadEmbeddedManifest()
    {
        var assembly = typeof(ModelManifest).Assembly;
        using var stream = assembly.GetManifestResourceStream(ManifestResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded manifest resource '{ManifestResourceName}' not found. " +
                "Verify the .json file is included as an EmbeddedResource in the .csproj.");

        var data = JsonSerializer.Deserialize<ManifestData>(stream, JsonOptions)
            ?? throw new InvalidOperationException(
                $"Embedded manifest '{ManifestResourceName}' deserialized to null.");

        if (string.IsNullOrWhiteSpace(data.Revision))
        {
            throw new InvalidOperationException(
                $"Embedded manifest '{ManifestResourceName}' is missing 'revision'.");
        }
        if (data.Files is null || data.Files.Count == 0)
        {
            throw new InvalidOperationException(
                $"Embedded manifest '{ManifestResourceName}' is missing 'files'.");
        }

        return data;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class ManifestData
    {
        [JsonPropertyName("revision")]
        public string Revision { get; init; } = string.Empty;

        [JsonPropertyName("files")]
        public IReadOnlyList<ModelFileEntry> Files { get; init; } = Array.Empty<ModelFileEntry>();
    }
}

/// <summary>
/// One file in the model bundle.
/// </summary>
/// <param name="LocalFileName">Filename under the cache directory.</param>
/// <param name="RemoteRelativePath">Path under the HuggingFace repo root.</param>
/// <param name="ApproximateBytes">Best-effort size estimate used to weight progress.</param>
/// <param name="Sha256">
/// Lowercase hex SHA-256 of the file content. When non-null, the download
/// service computes the hash of the downloaded file and aborts (deleting the
/// file) on mismatch — defending against supply-chain tampering and corrupted
/// transfers. Null disables the check for files that aren't LFS-tracked
/// upstream (small text configs); for those, Content-Length validation in
/// the download service is the only integrity guard.
/// </param>
public sealed record ModelFileEntry(
    [property: JsonPropertyName("localFileName")] string LocalFileName,
    [property: JsonPropertyName("remoteRelativePath")] string RemoteRelativePath,
    [property: JsonPropertyName("approximateBytes")] long ApproximateBytes,
    [property: JsonPropertyName("sha256")] string? Sha256 = null);
