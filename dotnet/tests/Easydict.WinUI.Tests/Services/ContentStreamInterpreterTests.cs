using System.Text;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class ContentStreamInterpreterTests
{
    [Theory]
    [InlineData(0x41, false, "41")]         // 'A' in simple font → 2-hex-digit
    [InlineData(0xFF, false, "FF")]         // max 1-byte
    [InlineData(0x00, false, "00")]         // zero
    [InlineData(0x41, true, "0041")]        // 'A' in CID font → 4-hex-digit
    [InlineData(0x1234, true, "1234")]      // CJK CID
    [InlineData(0xFFFF, true, "FFFF")]      // max 2-byte CID
    [InlineData(0x0000, true, "0000")]      // zero CID
    public void CidToHex_ReturnsCorrectEncoding(int cid, bool isCidFont, string expected)
    {
        ContentStreamInterpreter.CidToHex(cid, isCidFont).Should().Be(expected);
    }

    [Fact]
    public void GenerateTextOperator_ProducesCorrectPdfFormat()
    {
        var result = ContentStreamInterpreter.GenerateTextOperator("F1", 12.0, 100.5, 200.3, "0041");

        result.Should().Contain("/F1 ");
        result.Should().Contain("12.000000 Tf");
        result.Should().Contain("100.500000");
        result.Should().Contain("200.300000");
        result.Should().Contain("Tm");
        result.Should().Contain("[<0041>] TJ");
    }

    [Fact]
    public void GenerateTextOperator_MatchesPdf2zhFormat()
    {
        // pdf2zh format: "/{font} {size:f} Tf 1 0 0 1 {x:f} {y:f} Tm [<{rtxt}>] TJ "
        var result = ContentStreamInterpreter.GenerateTextOperator("noto", 10.0, 50.0, 750.0, "4E2D");

        result.Should().Be("/noto 10.000000 Tf 1 0 0 1 50.000000 750.000000 Tm [<4E2D>] TJ ");
    }

    [Fact]
    public void BuildContentStream_WrapsInQAndBT()
    {
        var graphicsOps = Encoding.ASCII.GetBytes("1 0 0 1 0 0 cm\n");
        var textOps = "/F1 12 Tf 1 0 0 1 100 200 Tm [<41>] TJ ";

        var result = ContentStreamInterpreter.BuildContentStream(graphicsOps, textOps);
        var content = Encoding.ASCII.GetString(result);

        // Should start with q (save state), contain graphics ops, end with Q (restore), then BT...ET
        content.Should().StartWith("q ");
        content.Should().Contain("1 0 0 1 0 0 cm");
        content.Should().Contain("Q ");
        content.Should().Contain("BT ");
        content.Should().Contain("/F1 12 Tf");
        content.Should().EndWith("ET");
    }

    [Fact]
    public void BuildContentStream_WithOriginOffset_IncludesCmOperator()
    {
        var graphicsOps = Array.Empty<byte>();
        var textOps = "/F1 12 Tf [<41>] TJ ";

        var result = ContentStreamInterpreter.BuildContentStream(graphicsOps, textOps, originX: 72.0, originY: 36.0);
        var content = Encoding.ASCII.GetString(result);

        content.Should().Contain("1 0 0 1 72.000000 36.000000 cm");
    }

    [Fact]
    public void BuildContentStream_ZeroOrigin_StillIncludesCm()
    {
        var graphicsOps = Array.Empty<byte>();
        var textOps = "/F1 10 Tf [<48>] TJ ";

        var result = ContentStreamInterpreter.BuildContentStream(graphicsOps, textOps);
        var content = Encoding.ASCII.GetString(result);

        content.Should().Contain("1 0 0 1 0.000000 0.000000 cm");
    }

    [Fact]
    public void BuildContentStream_EmptyGraphics_ProducesValidStream()
    {
        var result = ContentStreamInterpreter.BuildContentStream(Array.Empty<byte>(), "");
        var content = Encoding.ASCII.GetString(result);

        // Should have q/Q wrapper even with empty graphics
        content.Should().Contain("q ");
        content.Should().Contain("Q ");
        content.Should().Contain("BT ");
        content.Should().Contain("ET");
    }

    [Theory]
    [InlineData(0x4E2D, true)]     // CJK character '中'
    [InlineData(0x0041, false)]    // Latin 'A'
    public void CidToHex_CjkVsLatin_DifferentWidths(int cid, bool isCidFont)
    {
        var hex = ContentStreamInterpreter.CidToHex(cid, isCidFont);

        if (isCidFont)
            hex.Should().HaveLength(4, "CID fonts use 2-byte encoding");
        else
            hex.Should().HaveLength(2, "simple fonts use 1-byte encoding");
    }

    [Fact]
    public void GenerateTextOperator_MultipleCharacters_ProducesSequentialOps()
    {
        // Simulating rendering "Hi" as two sequential operations (like pdf2zh does)
        var op1 = ContentStreamInterpreter.GenerateTextOperator("F1", 12.0, 100.0, 200.0, "48");
        var op2 = ContentStreamInterpreter.GenerateTextOperator("F1", 12.0, 106.0, 200.0, "69");

        var combined = op1 + op2;

        // Should have two Tf operators (pdf2zh sets font for each character)
        combined.Split("Tf").Length.Should().Be(3, "two characters = two Tf operators + trailing");
        combined.Split("TJ").Length.Should().Be(3, "two characters = two TJ operators + trailing");
    }
}
