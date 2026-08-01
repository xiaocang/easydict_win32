using Polyglot.TextLayout;

namespace Easydict.DirectXaml.Text;

/// <summary>Everything that identifies a run of text for measurement purposes.</summary>
/// <param name="FontSize">Size in DIPs.</param>
/// <param name="Weight">Stroke weight.</param>
public readonly record struct FontSpec(double FontSize, FontWeight Weight)
{
    /// <summary>The size and weight a node uses when it declares neither.</summary>
    public static readonly FontSpec Default = new(14, FontWeight.Normal);
}

/// <summary>
/// The single seam between layout and the rendering backend.
///
/// <see cref="ITextMeasurer"/> is Polyglot.TextLayout's interface and is stateful with respect to
/// font and size, so a factory is needed to produce one per distinct font. Line breaking itself is
/// Polyglot's job — this assembly does not reimplement it, which is what keeps CJK kinsoku
/// behaviour identical to the rest of the app.
/// </summary>
public interface ITextMeasurerFactory
{
    /// <summary>Returns a measurer bound to the given font.</summary>
    ITextMeasurer Create(FontSpec font);

    /// <summary>Baseline-to-baseline distance for the given font, in DIPs.</summary>
    double GetLineHeight(FontSpec font);
}

/// <summary>
/// A deterministic measurer: every grapheme is <paramref name="advance"/> wide and every line is
/// <paramref name="lineHeight"/> tall, both scaled linearly with font size. Layout tests use it to
/// assert exact geometry without depending on an installed font.
/// </summary>
/// <param name="advance">Width of one grapheme at 14 DIP font size.</param>
/// <param name="lineHeight">Line height at 14 DIP font size.</param>
public sealed class FixedAdvanceTextMeasurerFactory(double advance = 8, double lineHeight = 16)
    : ITextMeasurerFactory
{
    /// <inheritdoc/>
    public ITextMeasurer Create(FontSpec font) => new FixedAdvanceMeasurer(advance * font.FontSize / 14.0);

    /// <inheritdoc/>
    public double GetLineHeight(FontSpec font) => lineHeight * font.FontSize / 14.0;

    private sealed class FixedAdvanceMeasurer(double advance) : ITextMeasurer
    {
        public double MeasureSegment(string text)
        {
            // Counted by text element rather than by char so surrogate pairs and combining marks
            // measure as one grapheme, matching what a real shaper would report.
            double total = 0;
            var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
            while (enumerator.MoveNext())
            {
                total += advance;
            }

            return total;
        }

        public double MeasureGrapheme(string grapheme) => advance;
    }
}
