using Easydict.WinUI.Services.DocumentExport;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class GlyphAdvanceMeasurerTests
{
    /// <summary>
    /// Creates a measurer with a simple glyph map where ASCII chars map to GID = charCode,
    /// with a fixed advance width per glyph.
    /// </summary>
    private static GlyphAdvanceMeasurer CreateMeasurer(
        double fontSize = 10.0,
        bool primaryFontIsCjk = false,
        ushort advanceWidth = 500,
        ushort unitsPerEm = 1000)
    {
        // Build a simple glyph map: ASCII printable chars (0x20-0x7E)
        var glyphMap = new Dictionary<char, ushort>();
        var advanceWidths = new Dictionary<ushort, ushort>();
        for (char c = ' '; c <= '~'; c++)
        {
            var gid = (ushort)c;
            glyphMap[c] = gid;
            advanceWidths[gid] = advanceWidth;
        }

        return new GlyphAdvanceMeasurer(
            primaryGlyphMap: glyphMap,
            primaryAdvanceWidths: advanceWidths,
            primaryUnitsPerEm: unitsPerEm,
            notoGlyphMap: null,
            notoAdvanceWidths: null,
            notoUnitsPerEm: 1000,
            primaryFontIsCjk: primaryFontIsCjk,
            fontSize: fontSize);
    }

    [Fact]
    public void MeasureGrapheme_ScriptSignals_ReturnZero()
    {
        var measurer = CreateMeasurer(fontSize: 12.0);
        measurer.MeasureGrapheme("^").Should().Be(0);
        measurer.MeasureGrapheme("_").Should().Be(0);
    }

    [Fact]
    public void MeasureGrapheme_Space_ReturnsPointThreeEm()
    {
        var measurer = CreateMeasurer(fontSize: 10.0);
        measurer.MeasureGrapheme(" ").Should().BeApproximately(3.0, 0.001); // 10.0 * 0.3
    }

    [Fact]
    public void MeasureGrapheme_CjkCharacter_ReturnsFullEm()
    {
        var measurer = CreateMeasurer(fontSize: 12.0);
        measurer.MeasureGrapheme("\u4E2D").Should().Be(12.0); // CJK char = 1.0 em
    }

    [Fact]
    public void MeasureGrapheme_LatinChar_UsesGlyphAdvance()
    {
        // advanceWidth=500, unitsPerEm=1000 → 0.5 em → 5.0 at fontSize 10
        var measurer = CreateMeasurer(fontSize: 10.0, advanceWidth: 500, unitsPerEm: 1000);
        measurer.MeasureGrapheme("A").Should().BeApproximately(5.0, 0.001);
    }

    [Fact]
    public void MeasureSegment_MixedText_SumsCharWidths()
    {
        var measurer = CreateMeasurer(fontSize: 10.0, advanceWidth: 500, unitsPerEm: 1000);
        // "AB" = 5.0 + 5.0 = 10.0
        measurer.MeasureSegment("AB").Should().BeApproximately(10.0, 0.001);
    }

    [Fact]
    public void MeasureSegment_WithScriptSignals_SkipsSignalWidth()
    {
        var measurer = CreateMeasurer(fontSize: 10.0, advanceWidth: 500, unitsPerEm: 1000);
        // "A^B" → A(5.0) + ^(0) + B(5.0) = 10.0
        measurer.MeasureSegment("A^B").Should().BeApproximately(10.0, 0.001);
    }

    [Fact]
    public void MeasureGrapheme_AsciiWithCjkPrimaryFont_UsesHalfWidthAdvance()
    {
        // When primary font is CJK, ASCII should use the primary glyph map's advance
        var measurer = CreateMeasurer(fontSize: 10.0, primaryFontIsCjk: true, advanceWidth: 500, unitsPerEm: 1000);
        measurer.MeasureGrapheme("A").Should().BeApproximately(5.0, 0.001);
    }

    [Fact]
    public void MeasureGrapheme_SpaceWithCjkPrimaryFont_ReturnsPointThreeEm()
    {
        var measurer = CreateMeasurer(fontSize: 10.0, primaryFontIsCjk: true);
        measurer.MeasureGrapheme(" ").Should().BeApproximately(3.0, 0.001);
    }

    [Fact]
    public void MeasureGrapheme_FallbackWhenNoGlyphMap()
    {
        var measurer = new GlyphAdvanceMeasurer(
            primaryGlyphMap: null,
            primaryAdvanceWidths: null,
            primaryUnitsPerEm: 1000,
            notoGlyphMap: null,
            notoAdvanceWidths: null,
            notoUnitsPerEm: 1000,
            primaryFontIsCjk: false,
            fontSize: 10.0);

        // Latin char without glyph map → fallback 0.6 em = 6.0
        measurer.MeasureGrapheme("A").Should().BeApproximately(6.0, 0.001);
        // CJK char → 1.0 em = 10.0
        measurer.MeasureGrapheme("\u4E2D").Should().Be(10.0);
    }
}
