namespace LexIndex;

public sealed class LexIndexMetadata
{
    public required int FormatVersion { get; init; }

    public required string NormalizationId { get; init; }

    public required int StateCount { get; init; }

    public required int EdgeCount { get; init; }

    public required int EntryCount { get; init; }

    public required int PayloadCount { get; init; }

    public required int ValueRefCount { get; init; }

    public required int StringCount { get; init; }
}
