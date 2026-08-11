using System.Collections.ObjectModel;

namespace Easydict.SidecarClient.Protocol;

public sealed record PpOcrV6Artifact(
    string FileName,
    string Url,
    long SizeBytes,
    string Sha256);

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
            6_299_683,
            1_780_590,
            4_462_639,
            883,
            55_571,
            "193bab7a04fca699a6c82e6abb5b81bdb28177f0abd4062552b04908dafb19f8",
            "9ef676d6ed3c88256a2d92c640c44f25b0c40947e111b14b8be8f594091563e6",
            "3ac018be6f97499a08faa3bbdeb33640968d9307f6736d152902747a9f259593",
            "66170210bad538e83fff3c4a3867e547d6bf20b50d64b20347c4b913f3034ea1"),
        CreateModel(
            SmallId,
            "PP-OCRv6 Small",
            "28fe5895c24fd108c19eb3e8479f4ab385fbfc62",
            "b8f84f0b80c529de40b4fbb3544b84fa7233a513",
            "PP-OCRv6_small_det",
            "PP-OCRv6_small_rec",
            31_191_354,
            9_880_512,
            21_159_378,
            885,
            150_579,
            "d73e0058b7a8086bbd57f3d10b8bcd4ff95363f67e06e2762b5e814fe9c9410e",
            "5435fd747c9e0efe15a96d0b378d5bd157e9492ed8fd80edf08f30d02fa24634",
            "193f435274bf9f0b5f71a929bbfbcf148282df7e633b34e7c373e8f44741b516",
            "ab078671bb49f06228eadccd34f1bb501e157f7a047095ffb943ba81512c77d1"),
        CreateModel(
            MediumId,
            "PP-OCRv6 Medium",
            "61323801669c338b7891481ec7bac61ce31b576a",
            "50c7eacafc52fa7bcf4194e8cd08e46f8558504b",
            "PP-OCRv6_medium_det",
            "PP-OCRv6_medium_rec",
            138_739_282,
            62_032_837,
            76_554_979,
            886,
            150_580,
            "eb13b44b25bb36f89528b68720af8a61d9cf381176107f465db1757b65d086e1",
            "9c09abf0957f7968c7586464b7397b84ad2387a0497a351af40e9acc71b673ba",
            "7298d5ead546584af2504d03355f881ac7a7bc0eb1e282d3e159277c1d0af871",
            "991b700facf5b50a7de193468207d5f4255b538dde0d312ae3b7c7a9b6873129")
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
        long downloadSizeBytes,
        long detectorSizeBytes,
        long recognizerSizeBytes,
        long detectorConfigSizeBytes,
        long recognizerConfigSizeBytes,
        string detectorSha256,
        string recognizerSha256,
        string detectorConfigSha256,
        string recognizerConfigSha256)
    {
        var detectorRepository = $"PaddlePaddle/{detectorModelName}_onnx";
        var recognizerRepository = $"PaddlePaddle/{recognizerModelName}_onnx";
        var detectorBaseUrl = $"https://huggingface.co/{detectorRepository}/resolve/{detectorRevision}";
        var recognizerBaseUrl = $"https://huggingface.co/{recognizerRepository}/resolve/{recognizerRevision}";

        return new PpOcrV6ModelInfo(
            id,
            displayName,
            downloadSizeBytes,
            detectorModelName,
            recognizerModelName,
            [
                new("det.onnx", $"{detectorBaseUrl}/inference.onnx", detectorSizeBytes, detectorSha256),
                new("det.yml", $"{detectorBaseUrl}/inference.yml", detectorConfigSizeBytes, detectorConfigSha256),
                new("rec.onnx", $"{recognizerBaseUrl}/inference.onnx", recognizerSizeBytes, recognizerSha256),
                new("rec.yml", $"{recognizerBaseUrl}/inference.yml", recognizerConfigSizeBytes, recognizerConfigSha256),
            ],
            string.Equals(id, TinyId, StringComparison.OrdinalIgnoreCase)
                ? TinyLanguages
                : SupportedLanguages);
    }
}
