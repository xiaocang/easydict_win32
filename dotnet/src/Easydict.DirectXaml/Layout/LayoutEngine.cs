using Easydict.DirectXaml.Text;
using Polyglot.TextLayout;
using Polyglot.TextLayout.Layout;
using Polyglot.TextLayout.Preparation;

namespace Easydict.DirectXaml.Layout;

/// <summary>
/// Two-pass measure/arrange over a <see cref="CompiledView"/>.
///
/// Line breaking is delegated entirely to <see cref="TextLayoutEngine"/>, so CJK kinsoku rules,
/// punctuation grouping and whitespace normalisation behave exactly as they do elsewhere in the
/// app. This type only decides how much room each node gets.
/// </summary>
public sealed class LayoutEngine(CompiledView view, ITextMeasurerFactory measurers)
{
    /// <summary>Stands in for an unbounded constraint without risking arithmetic overflow.</summary>
    internal const double Unbounded = 1_000_000;

    public const double DefaultFontSize = 14;

    private readonly Size[] _desired = new Size[view.NodeCount];
    private readonly Rect[] _bounds = new Rect[view.NodeCount];
    private readonly Dictionary<int, TextLines> _textCache = new();

    public CompiledView View => view;

    public Size DesiredOf(int node) => _desired[node];

    public Rect BoundsOf(int node) => _bounds[node];

    /// <summary>Runs a full measure and arrange for the given viewport.</summary>
    public Size Layout(Size available)
    {
        _textCache.Clear();
        Array.Clear(_bounds);

        Size desired = Measure(view.RootNode, available);
        // The root always fills the width it was given; height follows content, which is what an
        // ItemsControl row wants.
        Arrange(view.RootNode, new Rect(0, 0, available.Width, desired.Height));
        return new Size(available.Width, desired.Height);
    }

    public Visibility VisibilityOf(int node) =>
        view.GetEnum(node, PropertyNames.Visibility, Visibility.Visible);

    private bool IsVisible(int node) => VisibilityOf(node) == Visibility.Visible;

    /// <summary>Visual children, excluding grid row/column definitions.</summary>
    internal IEnumerable<int> VisualChildren(int node)
    {
        foreach (int child in view.ChildrenOf(node))
        {
            NodeKind kind = view.KindOf(child);
            if (kind is not (NodeKind.RowDefinition or NodeKind.ColumnDefinition))
            {
                yield return child;
            }
        }
    }

    private List<int> Definitions(int node, NodeKind kind)
    {
        var result = new List<int>();
        foreach (int child in view.ChildrenOf(node))
        {
            if (view.KindOf(child) == kind)
            {
                result.Add(child);
            }
        }

        return result;
    }

    internal FontSpec FontOf(int node) => new(
        view.GetDouble(node, PropertyNames.FontSize, DefaultFontSize),
        view.GetEnum(node, PropertyNames.FontWeight, FontWeight.Normal));

    // ---- measure -----------------------------------------------------------------------------

    private Size Measure(int node, Size available)
    {
        if (!IsVisible(node))
        {
            _desired[node] = Size.Empty;
            return Size.Empty;
        }

        LengthValue width = view.GetLength(node, PropertyNames.Width);
        LengthValue height = view.GetLength(node, PropertyNames.Height);

        Size constraint = available;
        if (!width.IsAuto)
        {
            constraint = constraint with { Width = width.Dips };
        }

        if (!height.IsAuto)
        {
            constraint = constraint with { Height = height.Dips };
        }

        Size content = MeasureContent(node, constraint);

        double finalWidth = width.IsAuto ? content.Width : width.Dips;
        double finalHeight = height.IsAuto ? content.Height : height.Dips;

        finalWidth = Clamp(finalWidth, view.GetLength(node, PropertyNames.MinWidth), view.GetLength(node, PropertyNames.MaxWidth));
        finalHeight = Clamp(finalHeight, view.GetLength(node, PropertyNames.MinHeight), view.GetLength(node, PropertyNames.MaxHeight));

        _desired[node] = new Size(finalWidth, finalHeight);
        return _desired[node];
    }

    private static double Clamp(double value, LengthValue min, LengthValue max)
    {
        if (!min.IsAuto)
        {
            value = Math.Max(value, min.Dips);
        }

        if (!max.IsAuto)
        {
            value = Math.Min(value, max.Dips);
        }

        return Math.Max(0, value);
    }

    private Size MeasureContent(int node, Size available) => view.KindOf(node) switch
    {
        NodeKind.UserControl => MeasureSingleChild(node, available, Thickness.Zero),
        NodeKind.Border => MeasureBorder(node, available),
        NodeKind.StackPanel => MeasureStack(node, available),
        NodeKind.Grid => MeasureGrid(node, available),
        NodeKind.TextBlock => MeasureText(node, available),
        _ => Size.Empty,
    };

    private Thickness BorderInsets(int node)
    {
        Thickness padding = view.GetThickness(node, PropertyNames.Padding);
        Thickness border = view.GetThickness(node, PropertyNames.BorderThickness);
        return new Thickness(
            padding.Left + border.Left,
            padding.Top + border.Top,
            padding.Right + border.Right,
            padding.Bottom + border.Bottom);
    }

    private Size MeasureBorder(int node, Size available) =>
        MeasureSingleChild(node, available, BorderInsets(node));

    private Size MeasureSingleChild(int node, Size available, Thickness insets)
    {
        Size inner = available.Deflate(insets);
        Size content = Size.Empty;

        foreach (int child in VisualChildren(node))
        {
            Thickness margin = view.GetThickness(child, PropertyNames.Margin);
            Size childDesired = Measure(child, inner.Deflate(margin));
            content = new Size(
                Math.Max(content.Width, childDesired.Width + margin.Horizontal),
                Math.Max(content.Height, childDesired.Height + margin.Vertical));
        }

        return content.Inflate(insets);
    }

    private Size MeasureStack(int node, Size available)
    {
        Thickness insets = BorderInsets(node);
        Size inner = available.Deflate(insets);
        double spacing = view.GetDouble(node, PropertyNames.Spacing, 0);
        Orientation orientation = view.GetEnum(node, PropertyNames.Orientation, Orientation.Vertical);

        double main = 0;
        double cross = 0;
        int visible = 0;

        foreach (int child in VisualChildren(node))
        {
            if (!IsVisible(child))
            {
                Measure(child, Size.Empty);
                continue;
            }

            Thickness margin = view.GetThickness(child, PropertyNames.Margin);
            Size childAvailable = orientation == Orientation.Vertical
                ? new Size(Math.Max(0, inner.Width - margin.Horizontal), Unbounded)
                : new Size(Unbounded, Math.Max(0, inner.Height - margin.Vertical));

            Size childDesired = Measure(child, childAvailable);
            double childMain = orientation == Orientation.Vertical
                ? childDesired.Height + margin.Vertical
                : childDesired.Width + margin.Horizontal;
            double childCross = orientation == Orientation.Vertical
                ? childDesired.Width + margin.Horizontal
                : childDesired.Height + margin.Vertical;

            main += childMain;
            cross = Math.Max(cross, childCross);
            visible++;
        }

        if (visible > 1)
        {
            main += spacing * (visible - 1);
        }

        Size content = orientation == Orientation.Vertical
            ? new Size(cross, main)
            : new Size(main, cross);

        return content.Inflate(insets);
    }

    private Size MeasureGrid(int node, Size available)
    {
        Thickness insets = BorderInsets(node);
        Size inner = available.Deflate(insets);

        GridTracks columns = BuildTracks(node, NodeKind.ColumnDefinition, PropertyNames.Width);
        GridTracks rows = BuildTracks(node, NodeKind.RowDefinition, PropertyNames.Height);

        var children = VisualChildren(node).ToList();

        // Pass 1: unconstrained-ish measure so Auto tracks learn their content size.
        foreach (int child in children)
        {
            Thickness margin = view.GetThickness(child, PropertyNames.Margin);
            Measure(child, new Size(Math.Max(0, inner.Width - margin.Horizontal), Unbounded));
        }

        ResolveTrackSizes(columns, children, inner.Width, horizontal: true);

        // Pass 2: re-measure with the final column width. Text has to wrap at the width it will
        // actually be given, not at the width that was merely available.
        foreach (int child in children)
        {
            Thickness margin = view.GetThickness(child, PropertyNames.Margin);
            double cellWidth = SpanSize(columns, view.GetInt(child, PropertyNames.GridColumn), Span(child, PropertyNames.GridColumnSpan));
            Measure(child, new Size(Math.Max(0, cellWidth - margin.Horizontal), Unbounded));
        }

        ResolveTrackSizes(rows, children, inner.Height, horizontal: false);

        Size content = new(columns.Total, rows.Total);
        return content.Inflate(insets);
    }

    private int Span(int child, string property) => Math.Max(1, view.GetInt(child, property, 1));

    private GridTracks BuildTracks(int node, NodeKind kind, string sizeProperty)
    {
        List<int> definitions = Definitions(node, kind);
        var tracks = new GridTracks();

        if (definitions.Count == 0)
        {
            // A Grid with no explicit definitions behaves as a single auto-sized cell.
            tracks.Lengths.Add(GridLengthValue.Auto);
            tracks.Sizes.Add(0);
            return tracks;
        }

        foreach (int definition in definitions)
        {
            tracks.Lengths.Add(view.GetGridLength(definition, sizeProperty));
            tracks.Sizes.Add(0);
        }

        return tracks;
    }

    private void ResolveTrackSizes(GridTracks tracks, List<int> children, double availableSize, bool horizontal)
    {
        double fixedAndAuto = 0;
        double starWeight = 0;

        for (int index = 0; index < tracks.Lengths.Count; index++)
        {
            GridLengthValue length = tracks.Lengths[index];
            switch (length.Unit)
            {
                case GridUnit.Dip:
                    tracks.Sizes[index] = length.Value;
                    fixedAndAuto += length.Value;
                    break;
                case GridUnit.Star:
                    starWeight += length.Value;
                    break;
                default:
                    tracks.Sizes[index] = 0;
                    break;
            }
        }

        // Auto tracks take the largest single-track child in them.
        foreach (int child in children)
        {
            if (!IsVisible(child))
            {
                continue;
            }

            int start = horizontal
                ? view.GetInt(child, PropertyNames.GridColumn)
                : view.GetInt(child, PropertyNames.GridRow);
            int span = horizontal
                ? Span(child, PropertyNames.GridColumnSpan)
                : Span(child, PropertyNames.GridRowSpan);

            if (span != 1 || start < 0 || start >= tracks.Lengths.Count)
            {
                continue;
            }

            if (tracks.Lengths[start].Unit != GridUnit.Auto)
            {
                continue;
            }

            Thickness margin = view.GetThickness(child, PropertyNames.Margin);
            double extent = horizontal
                ? _desired[child].Width + margin.Horizontal
                : _desired[child].Height + margin.Vertical;

            if (extent > tracks.Sizes[start])
            {
                fixedAndAuto += extent - tracks.Sizes[start];
                tracks.Sizes[start] = extent;
            }
        }

        if (starWeight > 0)
        {
            double remaining = Math.Max(0, availableSize - fixedAndAuto);
            for (int index = 0; index < tracks.Lengths.Count; index++)
            {
                GridLengthValue length = tracks.Lengths[index];
                if (length.Unit == GridUnit.Star)
                {
                    tracks.Sizes[index] = remaining * (length.Value / starWeight);
                }
            }
        }
    }

    private static double SpanSize(GridTracks tracks, int start, int span)
    {
        double total = 0;
        for (int index = start; index < start + span && index < tracks.Sizes.Count; index++)
        {
            if (index >= 0)
            {
                total += tracks.Sizes[index];
            }
        }

        return total;
    }

    private Size MeasureText(int node, Size available)
    {
        Thickness insets = BorderInsets(node);
        Size inner = available.Deflate(insets);

        string text = view.GetText(node);
        if (string.IsNullOrEmpty(text))
        {
            return Size.Empty;
        }

        TextWrapping wrapping = view.GetEnum(node, PropertyNames.TextWrapping, TextWrapping.NoWrap);
        double wrapWidth = wrapping == TextWrapping.NoWrap ? Unbounded : Math.Max(1, inner.Width);

        FontSpec font = FontOf(node);
        ITextMeasurer measurer = measurers.Create(font);
        PreparedParagraph prepared = TextLayoutEngine.Instance.Prepare(
            new TextPrepareRequest { Text = text },
            measurer);

        LayoutLinesResult lines = TextLayoutEngine.Instance.LayoutWithLines(prepared, wrapWidth);
        double lineHeight = measurers.GetLineHeight(font);
        _textCache[node] = new TextLines(lines.Lines, lineHeight, font);

        Size content = new(lines.MaxLineWidth, lines.Lines.Count * lineHeight);
        return content.Inflate(insets);
    }

    /// <summary>Lines produced by the last measure pass. The paint pass reuses them.</summary>
    public TextLines? TextLinesOf(int node) => _textCache.TryGetValue(node, out TextLines? lines) ? lines : null;

    // ---- arrange -----------------------------------------------------------------------------

    private void Arrange(int node, Rect final)
    {
        if (!IsVisible(node))
        {
            _bounds[node] = Rect.Empty;
            return;
        }

        _bounds[node] = final;

        switch (view.KindOf(node))
        {
            case NodeKind.UserControl:
                ArrangeSingleChild(node, final, Thickness.Zero);
                break;
            case NodeKind.Border:
                ArrangeSingleChild(node, final, BorderInsets(node));
                break;
            case NodeKind.StackPanel:
                ArrangeStack(node, final);
                break;
            case NodeKind.Grid:
                ArrangeGrid(node, final);
                break;
        }
    }

    private void ArrangeSingleChild(int node, Rect final, Thickness insets)
    {
        Rect inner = final.Deflate(insets);
        foreach (int child in VisualChildren(node))
        {
            ArrangeChild(child, inner);
        }
    }

    private void ArrangeStack(int node, Rect final)
    {
        Rect inner = final.Deflate(BorderInsets(node));
        double spacing = view.GetDouble(node, PropertyNames.Spacing, 0);
        Orientation orientation = view.GetEnum(node, PropertyNames.Orientation, Orientation.Vertical);

        double offset = 0;
        bool first = true;

        foreach (int child in VisualChildren(node))
        {
            if (!IsVisible(child))
            {
                _bounds[child] = Rect.Empty;
                continue;
            }

            if (!first)
            {
                offset += spacing;
            }

            first = false;

            Thickness margin = view.GetThickness(child, PropertyNames.Margin);
            Size desired = _desired[child];

            Rect slot = orientation == Orientation.Vertical
                ? new Rect(inner.X, inner.Y + offset, inner.Width, desired.Height + margin.Vertical)
                : new Rect(inner.X + offset, inner.Y, desired.Width + margin.Horizontal, inner.Height);

            ArrangeChild(child, slot);
            offset += orientation == Orientation.Vertical
                ? desired.Height + margin.Vertical
                : desired.Width + margin.Horizontal;
        }
    }

    private void ArrangeGrid(int node, Rect final)
    {
        Rect inner = final.Deflate(BorderInsets(node));

        GridTracks columns = BuildTracks(node, NodeKind.ColumnDefinition, PropertyNames.Width);
        GridTracks rows = BuildTracks(node, NodeKind.RowDefinition, PropertyNames.Height);
        var children = VisualChildren(node).ToList();

        ResolveTrackSizes(columns, children, inner.Width, horizontal: true);
        ResolveTrackSizes(rows, children, inner.Height, horizontal: false);

        foreach (int child in children)
        {
            if (!IsVisible(child))
            {
                _bounds[child] = Rect.Empty;
                continue;
            }

            int column = view.GetInt(child, PropertyNames.GridColumn);
            int row = view.GetInt(child, PropertyNames.GridRow);

            double x = inner.X + SpanSize(columns, 0, Math.Max(0, column));
            double y = inner.Y + SpanSize(rows, 0, Math.Max(0, row));
            double width = SpanSize(columns, column, Span(child, PropertyNames.GridColumnSpan));
            double height = SpanSize(rows, row, Span(child, PropertyNames.GridRowSpan));

            ArrangeChild(child, new Rect(x, y, width, height));
        }
    }

    /// <summary>Applies margin and alignment, then recurses.</summary>
    private void ArrangeChild(int child, Rect slot)
    {
        Thickness margin = view.GetThickness(child, PropertyNames.Margin);
        Rect available = slot.Deflate(margin);
        Size desired = _desired[child];

        HorizontalAlignment horizontal = view.GetEnum(child, PropertyNames.HorizontalAlignment, HorizontalAlignment.Stretch);
        VerticalAlignment vertical = view.GetEnum(child, PropertyNames.VerticalAlignment, VerticalAlignment.Stretch);

        double width = horizontal == HorizontalAlignment.Stretch
            ? available.Width
            : Math.Min(desired.Width, available.Width);
        double height = vertical == VerticalAlignment.Stretch
            ? available.Height
            : Math.Min(desired.Height, available.Height);

        double x = horizontal switch
        {
            HorizontalAlignment.Center => available.X + ((available.Width - width) / 2),
            HorizontalAlignment.Right => available.Right - width,
            _ => available.X,
        };

        double y = vertical switch
        {
            VerticalAlignment.Center => available.Y + ((available.Height - height) / 2),
            VerticalAlignment.Bottom => available.Bottom - height,
            _ => available.Y,
        };

        Arrange(child, new Rect(x, y, width, height));
    }

    // ---- hit testing -------------------------------------------------------------------------

    /// <summary>The deepest visible node containing the point, or <c>null</c>.</summary>
    public int? HitTest(double x, double y) => HitTest(view.RootNode, x, y);

    private int? HitTest(int node, double x, double y)
    {
        if (!IsVisible(node) || !_bounds[node].Contains(x, y))
        {
            return null;
        }

        // Later siblings paint on top, so they win the hit.
        var children = VisualChildren(node).ToList();
        for (int index = children.Count - 1; index >= 0; index--)
        {
            int? hit = HitTest(children[index], x, y);
            if (hit is not null)
            {
                return hit;
            }
        }

        return node;
    }

    private sealed class GridTracks
    {
        public List<GridLengthValue> Lengths { get; } = new();

        public List<double> Sizes { get; } = new();

        public double Total => Sizes.Sum();
    }
}

/// <summary>Laid-out text for one node, carried from measure to paint.</summary>
public sealed record TextLines(IReadOnlyList<LayoutLine> Lines, double LineHeight, FontSpec Font);
