using Easydict.WinUI.Services.DocumentExport;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class GlyphAdvanceMeasurerTests
{
    private static GlyphAdvanceMeasurer CreateMeasurer(
        double fontSize = 10.0,
        bool primaryFontIsCjk = false,
        ushort advanceWidth = 500,
        ushort? latinAdvanceWidth = null,
        ushort unitsPerEm = 1000)
    {
        var glyphMap = new Dictionary<char, ushort>();
        var advanceWidths = new Dictionary<ushort, ushort>();
        for (char c = ' '; c <= '~'; c++)
        {
            var gid = (ushort)c;
            glyphMap[c] = gid;
            advanceWidths[gid] = advanceWidth;
        }

        Dictionary<char, ushort>? latinGlyphMap = null;
        Dictionary<ushort, ushort>? latinAdvanceWidths = null;
        if (latinAdvanceWidth.HasValue)
        {
            latinGlyphMap = new Dictionary<char, ushort>();
            latinAdvanceWidths = new Dictionary<ushort, ushort>();
            for (char c = ' '; c <= '~'; c++)
            {
                var gid = (ushort)c;
                latinGlyphMap[c] = gid;
                latinAdvanceWidths[gid] = latinAdvanceWidth.Value;
            }
        }

        return new GlyphAdvanceMeasurer(
            primaryGlyphMap: glyphMap,
            primaryAdvanceWidths: advanceWidths,
            primaryUnitsPerEm: unitsPerEm,
            notoGlyphMap: null,
            notoAdvanceWidths: null,
            notoUnitsPerEm: 1000,
            primaryFontIsCjk: primaryFontIsCjk,
            fontSize: fontSize,
            latinGlyphMap: latinGlyphMap,
            latinAdvanceWidths: latinAdvanceWidths,
            latinUnitsPerEm: unitsPerEm);
    }

    [Fact]
    public void MeasureGrapheme_ScriptSignals_ReturnZero()
    {
        var measurer = CreateMeasurer(fontSize: 12.0);
        measurer.MeasureGrapheme("^").Should().Be(0);
        measurer.MeasureGrapheme("_").Should().Be(0);
    }

    [Fact]
    public void MeasureGrapheme_Space_WithLatinPrimaryFont_UsesGlyphAdvance()
    {
        var measurer = CreateMeasurer(fontSize: 10.0, advanceWidth: 500, unitsPerEm: 1000);
        measurer.MeasureGrapheme(" ").Should().BeApproximately(5.0, 0.001);
    }

    [Fact]
    public void MeasureGrapheme_Space_WithoutGlyphMetrics_FallsBackToSpaceAdvanceConstant()
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

        measurer.MeasureGrapheme(" ").Should().BeApproximately(10.0 * GlyphAdvanceMeasurer.SpaceAdvanceEm, 0.001);
    }

    [Fact]
    public void MeasureGrapheme_CjkCharacter_ReturnsFullEm()
    {
        var measurer = CreateMeasurer(fontSize: 12.0);
        measurer.MeasureGrapheme("\u4E2D").Should().Be(12.0);
    }

    [Fact]
    public void MeasureGrapheme_LatinChar_UsesGlyphAdvance()
    {
        var measurer = CreateMeasurer(fontSize: 10.0, advanceWidth: 500, unitsPerEm: 1000);
        measurer.MeasureGrapheme("A").Should().BeApproximately(5.0, 0.001);
    }

    [Fact]
    public void MeasureSegment_MixedText_SumsCharWidths()
    {
        var measurer = CreateMeasurer(fontSize: 10.0, advanceWidth: 500, unitsPerEm: 1000);
        measurer.MeasureSegment("AB").Should().BeApproximately(10.0, 0.001);
    }

    [Fact]
    public void MeasureSegment_WithScriptSignals_SkipsSignalWidth()
    {
        var measurer = CreateMeasurer(fontSize: 10.0, advanceWidth: 500, unitsPerEm: 1000);
        measurer.MeasureSegment("A^B").Should().BeApproximately(10.0, 0.001);
    }

    [Fact]
    public void MeasureGrapheme_AsciiWithCjkPrimaryFont_UsesHalfWidthAdvance()
    {
        var measurer = CreateMeasurer(fontSize: 10.0, primaryFontIsCjk: true, advanceWidth: 500, unitsPerEm: 1000);
        measurer.MeasureGrapheme("A").Should().BeApproximately(10.0 * GlyphAdvanceMeasurer.CjkPrimaryAsciiAdvanceEm, 0.001);
    }

    [Fact]
    public void MeasureGrapheme_AsciiWithCjkPrimaryFont_UsesLatinFaceAdvanceWhenAvailable()
    {
        var measurer = CreateMeasurer(
            fontSize: 10.0,
            primaryFontIsCjk: true,
            advanceWidth: 1000,
            latinAdvanceWidth: 500,
            unitsPerEm: 1000);

        measurer.MeasureGrapheme("A").Should().BeApproximately(5.0, 0.001);
    }

    [Fact]
    public void MeasureGrapheme_SpaceWithCjkPrimaryFont_UsesFixedRendererSpaceAdvance()
    {
        var measurer = CreateMeasurer(fontSize: 10.0, primaryFontIsCjk: true);
        measurer.MeasureGrapheme(" ").Should().BeApproximately(10.0 * GlyphAdvanceMeasurer.SpaceAdvanceEm, 0.001);
    }

    [Fact]
    public void MeasureGrapheme_SpaceWithCjkPrimaryFont_UsesLatinFaceSpaceAdvanceWhenAvailable()
    {
        var measurer = CreateMeasurer(
            fontSize: 10.0,
            primaryFontIsCjk: true,
            advanceWidth: 1000,
            latinAdvanceWidth: 250,
            unitsPerEm: 1000);

        measurer.MeasureGrapheme(" ").Should().BeApproximately(2.5, 0.001);
    }

    [Fact]
    public void MeasureSegment_AsciiAndSpaceWithCjkPrimaryFont_UsesFixedRendererAdvances()
    {
        var measurer = CreateMeasurer(fontSize: 10.0, primaryFontIsCjk: true, advanceWidth: 700, unitsPerEm: 1000);

        var width = measurer.MeasureSegment("A B");

        width.Should().BeApproximately(
            10.0 * (GlyphAdvanceMeasurer.CjkPrimaryAsciiAdvanceEm
                + GlyphAdvanceMeasurer.SpaceAdvanceEm
                + GlyphAdvanceMeasurer.CjkPrimaryAsciiAdvanceEm),
            0.001);
    }

    [Fact]
    public void MeasureSegment_AsciiAndSpaceWithCjkPrimaryFont_UsesLatinFaceMetricsWhenAvailable()
    {
        var measurer = CreateMeasurer(
            fontSize: 10.0,
            primaryFontIsCjk: true,
            advanceWidth: 1000,
            latinAdvanceWidth: 400,
            unitsPerEm: 1000);

        var width = measurer.MeasureSegment("A B");

        width.Should().BeApproximately(12.0, 0.001);
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

        measurer.MeasureGrapheme("A").Should().BeApproximately(6.0, 0.001);
        measurer.MeasureGrapheme("\u4E2D").Should().Be(10.0);
    }
}
