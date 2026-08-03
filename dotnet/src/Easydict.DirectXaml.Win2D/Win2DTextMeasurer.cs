using Easydict.DirectXaml.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Polyglot.TextLayout;

using DxFontWeight = Easydict.DirectXaml.FontWeight;

namespace Easydict.DirectXaml.Win2D;

/// <summary>
/// Supplies Polyglot.TextLayout with real font metrics from DirectWrite, via Win2D.
///
/// Layout asks for a measurer per distinct font, and Polyglot then calls
/// <see cref="ITextMeasurer.MeasureSegment"/> once per segment and
/// <see cref="ITextMeasurer.MeasureGrapheme"/> when it has to break inside one. That is a lot of
/// calls per paragraph, and each one would otherwise construct a <see cref="CanvasTextLayout"/>,
/// so both the formats and the measured widths are cached.
/// </summary>
public sealed class Win2DTextMeasurerFactory : ITextMeasurerFactory, IDisposable
{
    private readonly ICanvasResourceCreator _resourceCreator;
    private readonly string _fontFamily;
    private readonly Dictionary<FontSpec, CanvasTextFormat> _formats = new();
    private readonly Dictionary<FontSpec, double> _lineHeights = new();
    private readonly Dictionary<FontSpec, Win2DTextMeasurer> _measurers = new();
    private readonly Dictionary<TextLayoutKey, CanvasTextLayout> _textLayouts = new();
    private const int TextLayoutCacheLimit = 512;
    private bool _disposed;

    public Win2DTextMeasurerFactory(ICanvasResourceCreator resourceCreator, string fontFamily = "Segoe UI")
    {
        _resourceCreator = resourceCreator;
        _fontFamily = fontFamily;
    }

    public ITextMeasurer Create(FontSpec font)
    {
        if (!_measurers.TryGetValue(font, out Win2DTextMeasurer? measurer))
        {
            measurer = new Win2DTextMeasurer(_resourceCreator, GetFormat(font));
            _measurers[font] = measurer;
        }

        return measurer;
    }

    /// <summary>Warms the default text resources after the first visible card has painted.</summary>
    internal void Prewarm()
    {
        if (_disposed)
        {
            return;
        }

        FontSpec font = FontSpec.Default;
        _ = GetFormat(font);
        _ = GetLineHeight(font);
        _ = Create(font).MeasureSegment("Ag");
    }

    public double GetLineHeight(FontSpec font)
    {
        if (_lineHeights.TryGetValue(font, out double cached))
        {
            return cached;
        }

        // "Ag" spans a typical ascender and descender, so its single-line layout height is a good
        // stand-in for the font's line height without reaching into line metrics.
        using var probe = new CanvasTextLayout(_resourceCreator, "Ag", GetFormat(font), 0f, 0f);
        double height = probe.LayoutBounds.Height;
        if (height <= 0)
        {
            height = font.FontSize * 1.35;
        }

        _lineHeights[font] = height;
        return height;
    }

    /// <summary>Drops every cached device resource. Call when Win2D reports the device was lost.</summary>
    public void InvalidateResources()
    {
        foreach (CanvasTextFormat format in _formats.Values)
        {
            format.Dispose();
        }

        _formats.Clear();
        _lineHeights.Clear();
        foreach (CanvasTextLayout layout in _textLayouts.Values)
        {
            layout.Dispose();
        }

        _textLayouts.Clear();
        _measurers.Clear();
    }

    internal void DrawTextLayout(
        CanvasDrawingSession session,
        string text,
        double x,
        double y,
        FontSpec font,
        Windows.UI.Color color)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        double width = Create(font).MeasureSegment(text);
        double height = GetLineHeight(font);
        CanvasTextLayout layout = GetTextLayout(text, font, width, height);
        session.DrawTextLayout(layout, (float)x, (float)y, color);
    }

    private CanvasTextLayout GetTextLayout(string text, FontSpec font, double width, double height)
    {
        float layoutWidth = (float)Math.Max(1, width);
        float layoutHeight = (float)Math.Max(1, height);
        var key = new TextLayoutKey(text, font, layoutWidth, layoutHeight);
        if (_textLayouts.TryGetValue(key, out CanvasTextLayout? cached))
        {
            return cached;
        }

        if (_textLayouts.Count >= TextLayoutCacheLimit)
        {
            foreach (CanvasTextLayout layout in _textLayouts.Values)
            {
                layout.Dispose();
            }

            _textLayouts.Clear();
        }

        var created = new CanvasTextLayout(
            _resourceCreator,
            text,
            GetFormat(font),
            layoutWidth,
            layoutHeight);
        _textLayouts[key] = created;
        return created;
    }

    private readonly record struct TextLayoutKey(
        string Text,
        FontSpec Font,
        float Width,
        float Height);

    internal CanvasTextFormat GetFormat(FontSpec font)
    {
        if (_formats.TryGetValue(font, out CanvasTextFormat? format))
        {
            return format;
        }

        format = new CanvasTextFormat
        {
            FontFamily = _fontFamily,
            FontSize = (float)font.FontSize,
            FontWeight = ToWindowsWeight(font.Weight),
            // Polyglot owns line breaking; Win2D must report the full advance of whatever it is
            // handed, never wrap it itself.
            WordWrapping = CanvasWordWrapping.NoWrap,
        };

        _formats[font] = format;
        return format;
    }

    private static Windows.UI.Text.FontWeight ToWindowsWeight(DxFontWeight weight) => new()
    {
        Weight = weight switch
        {
            DxFontWeight.Thin => 100,
            DxFontWeight.ExtraLight => 200,
            DxFontWeight.Light => 300,
            DxFontWeight.Normal => 400,
            DxFontWeight.Medium => 500,
            DxFontWeight.SemiBold => 600,
            DxFontWeight.Bold => 700,
            DxFontWeight.ExtraBold => 800,
            DxFontWeight.Black => 900,
            _ => 400,
        },
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        InvalidateResources();
    }
}

/// <summary>A measurer bound to one font. Widths are memoised because Polyglot asks repeatedly.</summary>
internal sealed class Win2DTextMeasurer(ICanvasResourceCreator resourceCreator, CanvasTextFormat format)
    : ITextMeasurer
{
    private readonly Dictionary<string, double> _segmentWidths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _graphemeWidths = new(StringComparer.Ordinal);

    public double MeasureSegment(string text) => Measure(text, _segmentWidths);

    public double MeasureGrapheme(string grapheme) => Measure(grapheme, _graphemeWidths);

    private double Measure(string text, Dictionary<string, double> cache)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        if (cache.TryGetValue(text, out double cached))
        {
            return cached;
        }

        using var layout = new CanvasTextLayout(resourceCreator, text, format, 0f, 0f);
        double width = layout.LayoutBounds.Width;
        cache[text] = width;
        return width;
    }
}
