namespace LexIndex;

public interface ILexIndex
{
    LexIndexMetadata Metadata { get; }

    IReadOnlyList<string> Complete(string prefix, int limit);

    IReadOnlyList<string> Match(string pattern, int limit);
}
