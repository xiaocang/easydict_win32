# Polyglot.TextLayout

A pure-.NET text layout engine with Unicode-aware segmentation, line
breaking that respects CJK kinsoku rules, font fitting, and incremental
paragraph layout. Targets `net8.0` and has no external dependencies.

## Features

- **Segmentation**: Splits text into runs by script (Latin / CJK / digits /
  punctuation / whitespace) for correct break-opportunity classification.
- **Kinsoku**: Built-in tables for line-start/line-end forbidden characters
  (Chinese / Japanese punctuation), suitable for typesetting CJK text.
- **Line breaking**: Greedy layout with optional break-only-at-segment
  boundaries; long unbreakable segments fall back to per-glyph breaking.
- **Font fitting**: `FontFitSolver` finds the largest font size that fits
  given content into a fixed area, using a pluggable `ITextMeasurer`.
- **Incremental layout**: Re-layout only affected lines when content
  changes (`LayoutCursor` / `LayoutLineRange`).
- **Pluggable measurer**: `ITextMeasurer` lets you back the engine with
  any glyph-advance source (GDI+, DirectWrite, PdfSharp, MuPDF, etc.).

## Usage

```csharp
using Polyglot.TextLayout;
using Polyglot.TextLayout.Preparation;
using Polyglot.TextLayout.Layout;

ITextMeasurer measurer = new MyMeasurer();           // your impl
var engine = new TextLayoutEngine(measurer);

var prepared = engine.Prepare(new TextPrepareRequest
{
    Text = "今日は世界、Hello world!",
    FontSize = 14.0,
});

LayoutResult result = engine.Layout(prepared, maxWidth: 240);
foreach (var line in result.Lines)
{
    Console.WriteLine(line.Text);
}
```

See the test project for examples covering CJK kinsoku, variable-width
fonts, long-unbreakable-segment fallback, and incremental re-layout.

## License

GPL-3.0-only.
