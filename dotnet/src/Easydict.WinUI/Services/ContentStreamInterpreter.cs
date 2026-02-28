// Content stream interpreter for pdf2zh-aligned PDF translation pipeline.
// Separates text operations from graphics operations in a PDF page's content stream,
// enabling character-level content stream replacement (like pdf2zh's converter.py).

using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.PdfFonts;

namespace Easydict.WinUI.Services;

/// <summary>
/// Per-character information extracted from the PDF content stream.
/// Mirrors the data pdf2zh attaches to each LTChar via render_char():
/// cid, font object, font name/id, point size, text matrix, and position.
/// </summary>
public sealed record CharInfo
{
    /// <summary>Unicode text of the character.</summary>
    public required string Text { get; init; }

    /// <summary>Original PDF character code (before Unicode mapping).</summary>
    public required int CharacterCode { get; init; }

    /// <summary>CID for CID fonts, or character code for simple fonts.</summary>
    public required int Cid { get; init; }

    /// <summary>The font object used to render this character.</summary>
    public required IFont Font { get; init; }

    /// <summary>
    /// The font resource name (e.g. "F1", "F2") as referenced in the content stream.
    /// This corresponds to pdf2zh's fontid mapping.
    /// </summary>
    public required string FontResourceName { get; init; }

    /// <summary>Font size as specified in the Tf operator (unscaled).</summary>
    public required double FontSize { get; init; }

    /// <summary>Point size (effective size after matrix transforms).</summary>
    public required double PointSize { get; init; }

    /// <summary>The text matrix (Tm) at the time this character was rendered.</summary>
    public required TransformationMatrix TextMatrix { get; init; }

    /// <summary>The current transformation matrix (CTM) at the time this character was rendered.</summary>
    public required TransformationMatrix CurrentTransformationMatrix { get; init; }

    /// <summary>Left edge of the glyph bounding box in page coordinates.</summary>
    public double X0 { get; init; }

    /// <summary>Bottom edge of the glyph bounding box in page coordinates.</summary>
    public double Y0 { get; init; }

    /// <summary>Right edge of the glyph bounding box in page coordinates.</summary>
    public double X1 { get; init; }

    /// <summary>Top edge of the glyph bounding box in page coordinates.</summary>
    public double Y1 { get; init; }
}

/// <summary>
/// Result of interpreting a page's content stream.
/// Contains the graphics-only operations (ops_base) and per-character data.
/// </summary>
public sealed class ContentStreamResult
{
    /// <summary>
    /// Graphics-only operations (everything except text operations).
    /// These can be written back as ops_base when reconstructing the content stream.
    /// </summary>
    public required IReadOnlyList<IGraphicsStateOperation> GraphicsOperations { get; init; }

    /// <summary>
    /// Per-character data extracted from the text operations, in document order.
    /// Each character includes its CID, font, matrix, and position.
    /// </summary>
    public required IReadOnlyList<CharInfo> Characters { get; init; }

    /// <summary>
    /// Font resource name → IFont mapping for all fonts referenced in this page.
    /// Corresponds to pdf2zh's fontid dictionary.
    /// </summary>
    public required IReadOnlyDictionary<string, IFont> FontMap { get; init; }

    /// <summary>
    /// Serializes the graphics operations to a byte array suitable for writing
    /// to a PDF content stream (ops_base in pdf2zh terminology).
    /// </summary>
    public byte[] SerializeGraphicsOperations()
    {
        using var ms = new MemoryStream();
        foreach (var op in GraphicsOperations)
        {
            op.Write(ms);
            // Add newline separator between operations for readability
            ms.WriteByte((byte)'\n');
        }
        return ms.ToArray();
    }
}

/// <summary>
/// Interprets a PDF page's content stream to separate text operations from graphics operations.
/// This is the C# equivalent of pdf2zh's PDFPageInterpreterEx.execute() which filters
/// the content stream into ops_base (graphics) and character data (text).
///
/// Usage:
///   var result = ContentStreamInterpreter.Interpret(page);
///   byte[] opsBase = result.SerializeGraphicsOperations();
///   List&lt;CharInfo&gt; chars = result.Characters;
/// </summary>
public static class ContentStreamInterpreter
{
    // Text object operators: BT, ET
    private const string OpBeginText = "BT";
    private const string OpEndText = "ET";

    // Text state operators: Tc, Tf, Tz, TL, Tr, Ts, Tw
    private const string OpSetCharacterSpacing = "Tc";
    private const string OpSetFontAndSize = "Tf";
    private const string OpSetHorizontalScaling = "Tz";
    private const string OpSetTextLeading = "TL";
    private const string OpSetTextRenderingMode = "Tr";
    private const string OpSetTextRise = "Ts";
    private const string OpSetWordSpacing = "Tw";

    // Text positioning operators: T*, Td, TD, Tm
    private const string OpMoveToNextLine = "T*";
    private const string OpMoveToNextLineWithOffset = "Td";
    private const string OpMoveToNextLineWithOffsetSetLeading = "TD";
    private const string OpSetTextMatrix = "Tm";

    // Text showing operators: Tj, TJ, ', "
    private const string OpShowText = "Tj";
    private const string OpShowTextsWithPositioning = "TJ";
    private const string OpMoveToNextLineShowText = "'";
    private const string OpMoveToNextLineShowTextWithSpacing = "\"";

    // Type 3 font glyph operators
    private const string OpType3SetGlyphWidth = "d0";
    private const string OpType3SetGlyphWidthAndBoundingBox = "d1";

    /// <summary>
    /// All text-related operator symbols. Any operation whose Operator is in this set
    /// is filtered out of the graphics stream.
    /// </summary>
    private static readonly HashSet<string> TextOperators = new(StringComparer.Ordinal)
    {
        OpBeginText, OpEndText,
        OpSetCharacterSpacing, OpSetFontAndSize, OpSetHorizontalScaling,
        OpSetTextLeading, OpSetTextRenderingMode, OpSetTextRise, OpSetWordSpacing,
        OpMoveToNextLine, OpMoveToNextLineWithOffset,
        OpMoveToNextLineWithOffsetSetLeading, OpSetTextMatrix,
        OpShowText, OpShowTextsWithPositioning,
        OpMoveToNextLineShowText, OpMoveToNextLineShowTextWithSpacing,
        OpType3SetGlyphWidth, OpType3SetGlyphWidthAndBoundingBox,
    };

    /// <summary>
    /// Interprets a PDF page's content stream, separating text and graphics operations.
    /// </summary>
    /// <param name="page">The PdfPig page to interpret.</param>
    /// <returns>A result containing graphics operations and character data.</returns>
    public static ContentStreamResult Interpret(Page page)
    {
        var operations = page.Operations;
        var letters = page.Letters;

        // Build a font map from the page's letters
        var fontMap = BuildFontMap(letters);

        // Separate text from graphics operations
        var graphicsOps = new List<IGraphicsStateOperation>();
        foreach (var op in operations)
        {
            if (!TextOperators.Contains(op.Operator))
            {
                graphicsOps.Add(op);
            }
        }

        // Build per-character data from letters (which PdfPig already extracted)
        var characters = BuildCharacterList(letters, fontMap);

        return new ContentStreamResult
        {
            GraphicsOperations = graphicsOps,
            Characters = characters,
            FontMap = fontMap,
        };
    }

    /// <summary>
    /// Builds a font resource name → IFont mapping from the page's letters.
    /// Since Letter now exposes GetFont(), we can build the mapping directly.
    /// </summary>
    private static Dictionary<string, IFont> BuildFontMap(IReadOnlyList<Letter> letters)
    {
        var fontMap = new Dictionary<string, IFont>(StringComparer.Ordinal);

        foreach (var letter in letters)
        {
            var font = letter.GetFont();
            if (font is null) continue;

            var fontName = font.Name?.Data;
            if (fontName is null) continue;

            fontMap.TryAdd(fontName, font);
        }

        return fontMap;
    }

    /// <summary>
    /// Builds per-character info from the page's letters.
    /// Each Letter already contains CharacterCode, TextMatrix, CTM, and font.
    /// We enrich this with CID (via IFont.GetCid) and position data.
    /// </summary>
    private static List<CharInfo> BuildCharacterList(
        IReadOnlyList<Letter> letters,
        IReadOnlyDictionary<string, IFont> fontMap)
    {
        var characters = new List<CharInfo>(letters.Count);

        foreach (var letter in letters)
        {
            var font = letter.GetFont();
            if (font is null) continue;

            var fontResourceName = font.Name?.Data ?? "Unknown";
            var characterCode = letter.CharacterCode;
            var cid = font.GetCid(characterCode);

            characters.Add(new CharInfo
            {
                Text = letter.Value,
                CharacterCode = characterCode,
                Cid = cid,
                Font = font,
                FontResourceName = fontResourceName,
                FontSize = letter.FontSize,
                PointSize = letter.PointSize,
                TextMatrix = letter.TextMatrix,
                CurrentTransformationMatrix = letter.CurrentTransformationMatrix,
                X0 = letter.GlyphRectangle.Left,
                Y0 = letter.GlyphRectangle.Bottom,
                X1 = letter.GlyphRectangle.Right,
                Y1 = letter.GlyphRectangle.Top,
            });
        }

        return characters;
    }

    /// <summary>
    /// Generates a PDF text operation string for a single character, matching pdf2zh's gen_op_txt().
    /// Format: "/{font} {size} Tf 1 0 0 1 {x} {y} Tm [&lt;{hexCid}&gt;] TJ "
    /// </summary>
    /// <param name="fontResourceName">The font resource name (e.g. "F1").</param>
    /// <param name="fontSize">The font size.</param>
    /// <param name="x">X position.</param>
    /// <param name="y">Y position.</param>
    /// <param name="hexCid">Hex-encoded CID string.</param>
    /// <returns>PDF operator string for rendering this character.</returns>
    public static string GenerateTextOperator(string fontResourceName, double fontSize, double x, double y, string hexCid)
    {
        return $"/{fontResourceName} {fontSize:F6} Tf 1 0 0 1 {x:F6} {y:F6} Tm [<{hexCid}>] TJ ";
    }

    /// <summary>
    /// Converts a CID to its hex representation for use in PDF text operators.
    /// For 1-byte CIDs, returns 2-hex-digit string. For 2-byte CIDs, returns 4-hex-digit string.
    /// </summary>
    /// <param name="cid">The CID value.</param>
    /// <param name="isCidFont">Whether the font is a CID font (determines 2-byte vs 1-byte encoding).</param>
    /// <returns>Hex-encoded CID string.</returns>
    public static string CidToHex(int cid, bool isCidFont)
    {
        if (isCidFont)
        {
            // CID fonts use 2-byte encoding
            return cid.ToString("X4");
        }
        // Simple fonts use 1-byte encoding
        return cid.ToString("X2");
    }

    /// <summary>
    /// Wraps a sequence of text operations in BT/ET markers and prepends graphics ops,
    /// producing a complete content stream replacement.
    /// Format matches pdf2zh: "q {ops_base} Q 1 0 0 1 {x0} {y0} cm BT {ops_text} ET"
    /// </summary>
    /// <param name="graphicsOpsBytes">Serialized graphics operations (ops_base).</param>
    /// <param name="textOps">Text operation strings to include.</param>
    /// <param name="originX">X origin offset for the text coordinate system.</param>
    /// <param name="originY">Y origin offset for the text coordinate system.</param>
    /// <returns>Complete content stream bytes.</returns>
    public static byte[] BuildContentStream(byte[] graphicsOpsBytes, string textOps, double originX = 0, double originY = 0)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);

        // Wrap original graphics operations in q/Q (save/restore state)
        writer.Write("q ");
        writer.Flush();
        ms.Write(graphicsOpsBytes, 0, graphicsOpsBytes.Length);
        writer.Write("Q ");

        // Set coordinate origin and write text block
        writer.Write($"1 0 0 1 {originX:F6} {originY:F6} cm ");
        writer.Write("BT ");
        writer.Write(textOps);
        writer.Write("ET");
        writer.Flush();

        return ms.ToArray();
    }
}
