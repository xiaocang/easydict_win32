using System.Text;

namespace LexIndex;

public sealed class LexIndexBuildOptions
{
    public static readonly string DefaultNormalizationId = "nfkc-lower-invariant-v1";

    public string NormalizationId { get; init; } = DefaultNormalizationId;

    public Encoding StringEncoding { get; init; } = Encoding.UTF8;
}
