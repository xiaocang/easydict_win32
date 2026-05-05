using Polyglot.TextLayout;
using PdfSharpCore.Drawing;

namespace Easydict.WinUI.Services.DocumentExport;

/// <summary>
/// Measures text advance widths using PdfSharpCore's XGraphics.MeasureString.
/// Used by PdfExportService for TextLayout integration.
/// </summary>
internal sealed class XGraphicsMeasurer : ITextMeasurer
{
    private readonly XGraphics _gfx;
    private readonly XFont _font;

    public XGraphicsMeasurer(XGraphics gfx, XFont font)
    {
        _gfx = gfx;
        _font = font;
    }

    public double MeasureSegment(string text) =>
        _gfx.MeasureString(text, _font).Width;

    public double MeasureGrapheme(string grapheme) =>
        _gfx.MeasureString(grapheme, _font).Width;
}
