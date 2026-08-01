using Easydict.DirectXaml.Ir;
using Easydict.DirectXaml.Layout;
using Easydict.DirectXaml.Render;
using Easydict.DirectXaml.Text;
using Easydict.DirectXaml.Theming;
using FluentAssertions;
using Xunit;

namespace Easydict.DirectXaml.Tests;

/// <summary>
/// Exercises the parts of the direct renderer that do not need a Windows desktop: IR loading,
/// slot writes and their invalidation, layout geometry, and display-list generation.
///
/// Geometry is asserted exactly by measuring with <see cref="FixedAdvanceTextMeasurerFactory"/>
/// (8 DIPs per grapheme, 16 DIP line height), so the numbers below do not depend on an installed
/// font.
/// </summary>
public class DirectXamlTests
{
    /// <summary>
    /// UserControl > Border(padding 4, themed background) > StackPanel(spacing 6) > two TextBlocks.
    /// Hand-written rather than produced by dxamlc so these tests stand alone.
    /// </summary>
    private const string CardJson = """
    {
      "ir_version": "0.1.0",
      "compiler_version": "test",
      "source": { "path": "Card.xaml", "hash": "fnv1a64:0000000000000000" },
      "class_name": "Test.Card",
      "features": ["named-slots", "theme-resources"],
      "nodes": [
        { "id": 0, "kind": "userControl", "parent": null, "children": [1], "text": null },
        { "id": 1, "kind": "border", "parent": 0, "children": [2], "text": null },
        { "id": 2, "kind": "stackPanel", "parent": 1, "children": [3, 4], "text": null },
        { "id": 3, "kind": "textBlock", "parent": 2, "children": [], "text": null },
        { "id": 4, "kind": "textBlock", "parent": 2, "children": [], "text": null }
      ],
      "properties": [
        { "node": 1, "name": "Padding", "value": { "type": "thickness", "value": [4, 4, 4, 4] } },
        { "node": 1, "name": "Background", "value": { "type": "resource", "resource": 0 } },
        { "node": 1, "name": "BorderThickness", "value": { "type": "thickness", "value": [0, 0, 0, 1] } },
        { "node": 1, "name": "BorderBrush", "value": { "type": "color", "argb": "#FF102030" } },
        { "node": 2, "name": "Spacing", "value": { "type": "double", "value": 6 } },
        { "node": 3, "name": "Text", "value": { "type": "string", "value": "AB" } },
        { "node": 4, "name": "Text", "value": { "type": "string", "value": "CD" } }
      ],
      "named_slots": [
        {
          "name": "Body",
          "node": 3,
          "mutable": [
            { "property": "Text", "invalidation": ["measure", "paint"] },
            { "property": "Visibility", "invalidation": ["measure", "paint", "semantics"] },
            { "property": "Foreground", "invalidation": ["paint"] },
            { "property": "Opacity", "invalidation": ["paint"] }
          ]
        }
      ],
      "resources": [ { "id": 0, "kind": "themeResource", "key": "CardBrush" } ],
      "actions": [ { "node": 1, "event": "pointerPressed", "handler": "OnPressed" } ],
      "semantics": []
    }
    """;

    private static readonly Color CardColor = new(255, 1, 2, 3);

    private static IResourceResolver Resources() =>
        new DictionaryResourceResolver().Add("CardBrush", CardColor);

    private static CompiledView LoadCard() => new(IrLoader.Load(CardJson), Resources());

    private static LayoutEngine LayoutCard(out CompiledView view, double width = 200)
    {
        view = LoadCard();
        var engine = new LayoutEngine(view, new FixedAdvanceTextMeasurerFactory());
        engine.Layout(Size.FromWidth(width));
        return engine;
    }

    // ---- loading -----------------------------------------------------------------------------

    [Fact]
    public void Load_ReadsTheDocument()
    {
        CompiledView view = LoadCard();

        view.ClassName.Should().Be("Test.Card");
        view.NodeCount.Should().Be(5);
        view.RootNode.Should().Be(0);
        view.KindOf(1).Should().Be(NodeKind.Border);
        view.SlotNames.Should().ContainSingle().Which.Should().Be("Body");
    }

    [Fact]
    public void Load_RejectsAnUnsupportedIrVersion()
    {
        string json = CardJson.Replace("\"ir_version\": \"0.1.0\"", "\"ir_version\": \"9.9.9\"");

        Action load = () => IrLoader.Load(json);

        load.Should().Throw<IrLoadException>().WithMessage("*9.9.9*");
    }

    [Fact]
    public void Load_RejectsAnUnknownFeature()
    {
        string json = CardJson.Replace("\"theme-resources\"", "\"time-travel\"");

        Action load = () => IrLoader.Load(json);

        load.Should().Throw<IrLoadException>().WithMessage("*time-travel*");
    }

    [Fact]
    public void Load_RejectsInconsistentParentLinks()
    {
        string json = CardJson.Replace(
            "{ \"id\": 3, \"kind\": \"textBlock\", \"parent\": 2,",
            "{ \"id\": 3, \"kind\": \"textBlock\", \"parent\": 1,");

        Action load = () => IrLoader.Load(json);

        load.Should().Throw<IrLoadException>();
    }

    // ---- slots and invalidation --------------------------------------------------------------

    [Fact]
    public void SlotWrite_AppliesTheDeclaredInvalidation()
    {
        CompiledView view = LoadCard();
        view.MarkClean();

        view.SetForeground("Body", new Color(255, 9, 9, 9));

        // A colour change repaints; it must not force a re-measure.
        view.Dirty.Should().Be(Invalidation.Paint);
    }

    [Fact]
    public void SlotWrite_TextAlsoRemeasures()
    {
        CompiledView view = LoadCard();
        view.MarkClean();

        view.SetText("Body", "something longer");

        view.Dirty.Should().Be(Invalidation.Measure | Invalidation.Paint);
    }

    [Fact]
    public void SlotWrite_UnchangedValueDirtiesNothing()
    {
        CompiledView view = LoadCard();
        view.SetText("Body", "same");
        view.MarkClean();

        view.SetText("Body", "same");

        // UpdateUI rewrites every value on each notification, so no-op writes must stay free.
        view.Dirty.Should().Be(Invalidation.None);
    }

    [Fact]
    public void SlotWrite_RejectsAnUnknownSlot()
    {
        CompiledView view = LoadCard();

        Action write = () => view.SetText("Nope", "x");

        write.Should().Throw<ArgumentException>().WithMessage("*Nope*");
    }

    [Fact]
    public void SlotWrite_RejectsAPropertyTheSlotDoesNotDeclare()
    {
        CompiledView view = LoadCard();

        // Background is not in this slot's mutable set; a silent no-op would surface much later
        // as a rendering bug.
        Action write = () => view.SetBackground("Body", CardColor);

        write.Should().Throw<InvalidOperationException>().WithMessage("*Background*");
    }

    // ---- layout ------------------------------------------------------------------------------

    [Fact]
    public void Layout_StacksChildrenWithSpacing()
    {
        LayoutEngine engine = LayoutCard(out _);

        // Border padding 4 insets the stack; each line is 16 tall with 6 between.
        engine.BoundsOf(3).Should().Be(new Rect(4, 4, 192, 16));
        engine.BoundsOf(4).Should().Be(new Rect(4, 26, 192, 16));
    }

    [Fact]
    public void Layout_SizesTheCardToItsContent()
    {
        LayoutEngine engine = LayoutCard(out _);

        // padding 4 + 16 + spacing 6 + 16 + padding 4 + the 1 DIP bottom border
        engine.BoundsOf(0).Should().Be(new Rect(0, 0, 200, 47));
    }

    [Fact]
    public void Layout_CollapsedChildrenLeaveTheFlow()
    {
        CompiledView view = LoadCard();
        view.SetVisibility("Body", Visibility.Collapsed);
        var engine = new LayoutEngine(view, new FixedAdvanceTextMeasurerFactory());

        engine.Layout(Size.FromWidth(200));

        engine.BoundsOf(3).Should().Be(Rect.Empty);
        // The remaining line sits at the top with no spacing reserved for the hidden one.
        engine.BoundsOf(4).Should().Be(new Rect(4, 4, 192, 16));
        engine.BoundsOf(0).Height.Should().Be(25);
    }

    [Fact]
    public void Layout_WrapsTextAtTheAvailableWidth()
    {
        CompiledView view = LoadCard();
        view.SetText("Body", "aaaa bbbb");
        var engine = new LayoutEngine(view, new FixedAdvanceTextMeasurerFactory());

        // 9 graphemes at 8 DIPs each need 72; 60 forces a break.
        engine.Layout(Size.FromWidth(60 + 8));

        engine.TextLinesOf(3)!.Lines.Count.Should().BeGreaterThan(1);
    }

    [Fact]
    public void HitTest_ReturnsTheDeepestNode()
    {
        LayoutEngine engine = LayoutCard(out _);

        engine.HitTest(10, 10).Should().Be(3);
        engine.HitTest(10, 30).Should().Be(4);
    }

    [Fact]
    public void HitTest_MissesOutsideTheCard()
    {
        LayoutEngine engine = LayoutCard(out _);

        engine.HitTest(10, 500).Should().BeNull();
    }

    [Fact]
    public void Actions_AreFoundOnTheNodeThatDeclaredThem()
    {
        CompiledView view = LoadCard();

        view.FindActionHandler(1, "pointerPressed").Should().Be("OnPressed");
        view.FindActionHandler(3, "pointerPressed").Should().BeNull();
        // Hit testing walks up from the leaf to reach it.
        view.ParentOf(3).Should().Be(2);
    }

    // ---- display list ------------------------------------------------------------------------

    [Fact]
    public void DisplayList_ResolvesThemeResourcesRatherThanFoldingThem()
    {
        LayoutEngine engine = LayoutCard(out _);

        DisplayList list = DisplayListBuilder.Build(engine);

        list.Commands.OfType<FillRectangle>()
            .Should().Contain(fill => fill.Color == CardColor);
    }

    [Fact]
    public void DisplayList_DrawsAnAsymmetricBorderAsSingleEdges()
    {
        LayoutEngine engine = LayoutCard(out _);

        DisplayList list = DisplayListBuilder.Build(engine);

        // BorderThickness="0,0,0,1" is bottom-only; a single stroked rectangle would draw all four
        // sides, so it has to come out as one edge fill.
        var edges = list.Commands
            .OfType<FillRectangle>()
            .Where(fill => fill.Color == new Color(255, 16, 32, 48))
            .ToList();

        edges.Should().ContainSingle();
        edges[0].Bounds.Height.Should().Be(1);
        edges[0].Bounds.Y.Should().Be(46);
        list.Commands.OfType<StrokeRectangle>().Should().BeEmpty();
    }

    [Fact]
    public void DisplayList_EmitsTextForEachLine()
    {
        LayoutEngine engine = LayoutCard(out _);

        DisplayList list = DisplayListBuilder.Build(engine);

        list.Commands.OfType<DrawTextLine>()
            .Select(line => line.Text)
            .Should().Equal("AB", "CD");
    }

    [Fact]
    public void DisplayList_SkipsCollapsedSubtrees()
    {
        CompiledView view = LoadCard();
        view.SetVisibility("Body", Visibility.Collapsed);
        var engine = new LayoutEngine(view, new FixedAdvanceTextMeasurerFactory());
        engine.Layout(Size.FromWidth(200));

        DisplayList list = DisplayListBuilder.Build(engine);

        list.Commands.OfType<DrawTextLine>()
            .Select(line => line.Text)
            .Should().Equal("CD");
    }

    [Fact]
    public void DisplayList_WrapsOpacityInAGroup()
    {
        CompiledView view = LoadCard();
        view.SetOpacity("Body", 0.5);
        var engine = new LayoutEngine(view, new FixedAdvanceTextMeasurerFactory());
        engine.Layout(Size.FromWidth(200));

        DisplayList list = DisplayListBuilder.Build(engine);

        list.Commands.OfType<PushOpacity>().Should().ContainSingle();
        list.Commands.OfType<PopOpacity>().Should().ContainSingle();
    }
}
