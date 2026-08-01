using Polyglot.TextLayout;

namespace Easydict.DirectXaml.Text;

/// <summary>Everything that identifies a run of text for measurement purposes.</summary>
public readonly record struct FontSpec(double FontSize, FontWeight Weight)
{
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
    ITextMeasurer Create(FontSpec font);

    /// <summary>Baseline-to-baseline distance for the given font, in DIPs.</summary>
    double GetLineHeight(FontSpec font);
}

/// <summary>
/// A deterministic measurer: every grapheme is <paramref name="advance"/> wide and every line is
/// <paramref name="lineHeight"/> tall. Layout tests use it to assert exact geometry without
/// depending on an installed font.
/// </summary>
public sealed class FixedAdvanceTextMeasurerFactory(double advance = 8, double lineHeight = 16)
    : ITextMeasurerFactory
{
    public ITextMeasurer Create(FontSpec font) => new FixedAdvanceMeasurer(advance * font.FontSize / 14.0);

    public double GetLineHeight(FontSpec font) => lineHeight * font.FontSize / 14.0;

    private sealed class FixedAdvanceMeasurer(double advance) : ITextMeasurer
    {
        public double MeasureSegment(string text)
        {
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
