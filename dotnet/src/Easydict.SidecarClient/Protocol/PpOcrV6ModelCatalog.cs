using System.Collections.ObjectModel;

namespace Easydict.SidecarClient.Protocol;

public sealed record PpOcrV6Artifact(
    string FileName,
    string Url,
    long SizeBytes);

public sealed record PpOcrV6ModelInfo(
    string Id,
    string DisplayName,
    long DownloadSizeBytes,
    string DetectorModelName,
    string RecognizerModelName,
    IReadOnlyList<PpOcrV6Artifact> Artifacts,
    IReadOnlyList<string> Languages);

public static class PpOcrV6ModelCatalog
{
    public const string TinyId = "PP-OCRv6_tiny";
    public const string SmallId = "PP-OCRv6_small";
    public const string MediumId = "PP-OCRv6_medium";
    public const int MinThreadCount = 1;
    public const int MaxThreadCount = 16;
    public const int DefaultThreadCount = 4;

    private static readonly IReadOnlyList<string> SupportedLanguages = new ReadOnlyCollection<string>(
    [
        "zh-Hans", "zh-Hant", "en", "ja", "af", "az", "bs", "ca", "cs", "cy",
        "da", "de", "es", "et", "eu", "fi", "fr", "ga", "gl", "hr", "hu",
        "id", "is", "it", "ku", "la", "lb", "lt", "lv", "mi", "ms", "mt",
        "nl", "no", "oc", "pl", "pt", "qu", "rm", "ro", "sr-Latn", "sk",
        "sl", "sq", "sv", "sw", "tl", "tr", "uz", "vi"
    ]);

    private static readonly IReadOnlyList<string> TinyLanguages = new ReadOnlyCollection<string>(
        SupportedLanguages.Where(language => !string.Equals(language, "ja", StringComparison.Ordinal)).ToArray());

    private static readonly IReadOnlyList<PpOcrV6ModelInfo> _models =
    [
        CreateModel(
            TinyId,
            "PP-OCRv6 Tiny",
            "2ba1506c0380b8f0b03dd142459aac66d4421f6c",
            "2612ab37152ae0a677521bae4e1e3d4fb4cf7c30",
            "PP-OCRv6_tiny_det",
            "PP-OCRv6_tiny_rec",
            [
                new(PpOcrV6ModelStore.DetectorModelFileName, "inference.onnx", 1_780_590),
                new(PpOcrV6ModelStore.DetectorConfigFileName, "inference.yml", 883),
                new(PpOcrV6ModelStore.RecognizerModelFileName, "inference.onnx", 4_462_639),
                new(PpOcrV6ModelStore.RecognizerConfigFileName, "inference.yml", 55_571),
            ]),
        CreateModel(
            SmallId,
            "PP-OCRv6 Small",
            "28fe5895c24fd108c19eb3e8479f4ab385fbfc62",
            "b8f84f0b80c529de40b4fbb3544b84fa7233a513",
            "PP-OCRv6_small_det",
            "PP-OCRv6_small_rec",
            [
                new(PpOcrV6ModelStore.DetectorModelFileName, "inference.onnx", 9_880_512),
                new(PpOcrV6ModelStore.DetectorConfigFileName, "inference.yml", 885),
                new(PpOcrV6ModelStore.RecognizerModelFileName, "inference.onnx", 21_159_378),
                new(PpOcrV6ModelStore.RecognizerConfigFileName, "inference.yml", 150_579),
            ]),
        CreateModel(
            MediumId,
            "PP-OCRv6 Medium",
            "61323801669c338b7891481ec7bac61ce31b576a",
            "50c7eacafc52fa7bcf4194e8cd08e46f8558504b",
            "PP-OCRv6_medium_det",
            "PP-OCRv6_medium_rec",
            [
                new(PpOcrV6ModelStore.DetectorModelFileName, "inference.onnx", 62_032_837),
                new(PpOcrV6ModelStore.DetectorConfigFileName, "inference.yml", 886),
                new(PpOcrV6ModelStore.RecognizerModelFileName, "inference.onnx", 76_554_979),
                new(PpOcrV6ModelStore.RecognizerConfigFileName, "inference.yml", 150_580),
            ])
    ];

    public static IReadOnlyList<PpOcrV6ModelInfo> Models => _models;

    public static PpOcrV6ModelInfo Get(string id)
    {
        return _models.FirstOrDefault(model =>
                   string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"Unknown PP-OCRv6 model: {id}", nameof(id));
    }

    public static bool TryGet(string? id, out PpOcrV6ModelInfo? model)
    {
        model = _models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        return model is not null;
    }

    public static bool SupportsLanguage(string modelId, string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag) || string.Equals(languageTag, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalized = languageTag.Trim().ToLowerInvariant();
        var model = Get(modelId);
        return model.Languages.Any(language =>
        {
            var candidate = language.ToLowerInvariant();
            return normalized == candidate
                || normalized.StartsWith(candidate + "-", StringComparison.Ordinal)
                || candidate.StartsWith(normalized + "-", StringComparison.Ordinal)
                || (candidate == "zh-hans" && normalized.StartsWith("zh-cn", StringComparison.Ordinal))
                || (candidate == "zh-hant" && normalized.StartsWith("zh-tw", StringComparison.Ordinal));
        });
    }

    private static PpOcrV6ModelInfo CreateModel(
        string id,
        string displayName,
        string detectorRevision,
        string recognizerRevision,
        string detectorModelName,
        string recognizerModelName,
        IReadOnlyList<ArtifactSpec> artifactSpecs)
    {
        var detectorRepository = $"PaddlePaddle/{detectorModelName}_onnx";
        var recognizerRepository = $"PaddlePaddle/{recognizerModelName}_onnx";
        var detectorBaseUrl = $"https://huggingface.co/{detectorRepository}/resolve/{detectorRevision}";
        var recognizerBaseUrl = $"https://huggingface.co/{recognizerRepository}/resolve/{recognizerRevision}";
        var artifacts = artifactSpecs.Select(spec => new PpOcrV6Artifact(
            spec.FileName,
            $"{(spec.IsDetector ? detectorBaseUrl : recognizerBaseUrl)}/{spec.RemoteFileName}",
            spec.SizeBytes)).ToArray();

        return new PpOcrV6ModelInfo(
            id,
            displayName,
            artifacts.Sum(artifact => artifact.SizeBytes),
            detectorModelName,
            recognizerModelName,
            artifacts,
            string.Equals(id, TinyId, StringComparison.OrdinalIgnoreCase)
                ? TinyLanguages
                : SupportedLanguages);
    }

    private sealed record ArtifactSpec(
        string FileName,
        string RemoteFileName,
        long SizeBytes,
        bool IsDetector)
    {
        public ArtifactSpec(string fileName, string remoteFileName, long sizeBytes)
            : this(fileName, remoteFileName, sizeBytes, fileName.StartsWith("det.", StringComparison.Ordinal))
        {
        }
    }
}
