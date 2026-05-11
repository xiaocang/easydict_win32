namespace Easydict.OpenVINO.Models;

/// <summary>
/// Static metadata describing what to download for the OpenVINO NLLB-200
/// provider. <see cref="Revision"/> controls how reproducible installs are —
/// see the field-level XML doc for the trade-off.
/// </summary>
public static class ModelManifest
{
    public const string ModelId = "nllb-200-distilled-600M-int8";

    /// <summary>HuggingFace repo with ONNX + quantized variants of NLLB-200-distilled-600M.</summary>
    public const string HuggingFaceRepo = "Xenova/nllb-200-distilled-600M";

    /// <summary>
    /// HuggingFace ref (branch, tag, or commit SHA) used to resolve file URLs.
    /// Currently <c>main</c>, which is mutable — if the upstream repo retags
    /// or re-uploads, existing installs see no diff but fresh downloads pick up
    /// the new content. That's acceptable for the initial rollout because we
    /// don't yet have a way to migrate users whose cache is from an older
    /// revision. TODO: before declaring this provider stable, replace with an
    /// immutable 40-char commit SHA from
    /// https://huggingface.co/Xenova/nllb-200-distilled-600M/commits/main and
    /// add an integrity check (SHA-256 per file) to <see cref="Services.ModelDownloadService"/>.
    /// </summary>
    public const string Revision = "main";

    /// <summary>Subdirectory under <c>%LOCALAPPDATA%\Easydict\models</c>.</summary>
    public const string CacheDirectoryName = "nllb-200-distilled-600M";

    /// <summary>Sentinel file written when a download completes successfully.</summary>
    public const string CompletionSentinel = ".complete";

    /// <summary>
    /// Files to download. Sizes are approximate (from HuggingFace metadata) and
    /// used only for progress aggregation, not integrity checking.
    /// We pick the non-merged decoder so the first cut doesn't need KV-cache
    /// plumbing (every step re-runs the decoder on the full output prefix —
    /// O(N²) but simple). A future iteration can switch to
    /// <c>decoder_model_merged_quantized.onnx</c> for incremental decoding.
    /// </summary>
    public static readonly IReadOnlyList<ModelFileEntry> Files = new[]
    {
        // INT8-quantized encoder/decoder — preferred for NPU inference.
        new ModelFileEntry("encoder_model_quantized.onnx", "onnx/encoder_model_quantized.onnx", ApproximateBytes: 165_000_000),
        new ModelFileEntry("decoder_model_quantized.onnx", "onnx/decoder_model_quantized.onnx", ApproximateBytes: 175_000_000),

        // Tokenizer assets.
        new ModelFileEntry("sentencepiece.bpe.model", "sentencepiece.bpe.model", ApproximateBytes: 4_900_000),
        new ModelFileEntry("tokenizer.json",          "tokenizer.json",          ApproximateBytes: 17_000_000),
        new ModelFileEntry("config.json",             "config.json",             ApproximateBytes:      2_000),
    };

    /// <summary>
    /// Builds the HuggingFace <c>resolve</c> URL for a given file at the pinned revision.
    /// </summary>
    public static string GetDownloadUrl(string remoteRelativePath)
    {
        return $"https://huggingface.co/{HuggingFaceRepo}/resolve/{Revision}/{remoteRelativePath}";
    }
}

/// <summary>
/// One file in the model bundle.
/// </summary>
/// <param name="LocalFileName">Filename under the cache directory.</param>
/// <param name="RemoteRelativePath">Path under the HuggingFace repo root.</param>
/// <param name="ApproximateBytes">Best-effort size estimate used to weight progress.</param>
public sealed record ModelFileEntry(string LocalFileName, string RemoteRelativePath, long ApproximateBytes);
