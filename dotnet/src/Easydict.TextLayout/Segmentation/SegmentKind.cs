namespace Easydict.TextLayout.Segmentation;

/// <summary>
/// Classifies a text segment for line-breaking decisions.
/// </summary>
public enum SegmentKind
{
    /// <summary>Latin/Cyrillic/Greek word run, possibly with trailing punctuation.</summary>
    Word,

    /// <summary>Single CJK character — breakable at any boundary.</summary>
    CjkGrapheme,

    /// <summary>One or more spaces or tabs (collapsible whitespace).</summary>
    Space,

    /// <summary>Hard line break (\n).</summary>
    HardBreak,

    /// <summary>Atomic formula placeholder — not breakable internally.</summary>
    FormulaPlaceholder,

    /// <summary>Opening punctuation that groups with the following segment: ( [ { etc.</summary>
    OpenPunctuation,

    /// <summary>Closing punctuation that groups with the preceding segment: ) ] } . , ; : ! ? etc.</summary>
    ClosePunctuation,

    /// <summary>Soft hyphen (U+00AD) — zero width normally, visible hyphen at line break.</summary>
    SoftHyphen,
}
