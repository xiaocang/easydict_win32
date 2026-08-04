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
      "ir_version": "0.2.0",
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
        { "node": 3, "name": "TextWrapping", "value": { "type": "enum", "enum": "TextWrapping", "value": "Wrap" } },
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
      "bindings": [],
      "resources": [ { "id": 0, "kind": "themeResource", "key": "CardBrush" } ],
      "actions": [ { "node": 1, "event": "pointerPressed", "handler": "OnPressed" } ],
      "semantics": []
    }
    """;
    private const string OverlapJson = """
    {
      "ir_version": "0.2.0",
      "compiler_version": "test",
      "source": { "path": "Overlap.xaml", "hash": "fnv1a64:0000000000000000" },
      "class_name": "Test.Overlap",
      "features": ["named-slots"],
      "nodes": [
        { "id": 0, "kind": "userControl", "parent": null, "children": [1], "text": null },
        { "id": 1, "kind": "grid", "parent": 0, "children": [2, 4], "text": null },
        { "id": 2, "kind": "border", "parent": 1, "children": [3], "text": null },
        { "id": 3, "kind": "textBlock", "parent": 2, "children": [], "text": null },
        { "id": 4, "kind": "border", "parent": 1, "children": [5], "text": null },
        { "id": 5, "kind": "textBlock", "parent": 4, "children": [], "text": null }
      ],
      "properties": [
        { "node": 2, "name": "Width", "value": { "type": "length", "value": { "kind": "dip", "value": 100 } } },
        { "node": 2, "name": "Height", "value": { "type": "length", "value": { "kind": "dip", "value": 32 } } },
        { "node": 2, "name": "Background", "value": { "type": "color", "argb": "#FFFF0000" } },
        { "node": 3, "name": "Text", "value": { "type": "string", "value": "dynamic" } },
        { "node": 4, "name": "Width", "value": { "type": "length", "value": { "kind": "dip", "value": 100 } } },
        { "node": 4, "name": "Height", "value": { "type": "length", "value": { "kind": "dip", "value": 32 } } },
        { "node": 4, "name": "Background", "value": { "type": "color", "argb": "#FF0000FF" } },
        { "node": 5, "name": "Text", "value": { "type": "string", "value": "static" } }
      ],
      "named_slots": [
        {
          "name": "DynamicPanel",
          "node": 2,
          "mutable": [
            { "property": "Background", "invalidation": ["paint"] }
          ]
        }
      ],
      "bindings": [],
      "resources": [],
      "actions": [],
      "semantics": []
    }
    """;

    private static readonly Color OverlapDynamicColor = new(255, 255, 0, 0);
    private static readonly Color OverlapStaticColor = new(255, 0, 0, 255);


    private static readonly Color CardColor = new(255, 1, 2, 3);

    private static IResourceResolver Resources() =>
        new DictionaryResourceResolver().Add("CardBrush", CardColor);

    private static CompiledView LoadCard() => new(IrLoader.Load(CardJson), Resources());
    private static string BindingCardJson() =>
        CardJson
            .Replace(
                "\"features\": [\"named-slots\", \"theme-resources\"]",
                "\"features\": [\"named-slots\", \"bindings\", \"theme-resources\"], \"binding_context_type\": \"Test.CardContext\"")
            .Replace(
                "\"bindings\": []",
                """
                "bindings": [
                  {
                    "target_node": 3,
                    "target_property": "Text",
                    "source_path": ["ResultText"],
                    "mode": "oneWay",
                    "invalidation": ["measure", "paint"]
                  }
                ]
                """);

    private static CompiledView LoadBindingCard() =>
        new(IrLoader.Load(BindingCardJson()), Resources());
    private static CompiledView LoadOverlap() => new(IrLoader.Load(OverlapJson), Resources());

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
    public void Load_AcceptsValidBindings()
    {
        string json = BindingCardJson();

        IrDocument document = IrLoader.Load(json);

        document.BindingContextType.Should().Be("Test.CardContext");
        document.Bindings.Should().ContainSingle();
        document.Bindings[0].TargetNode.Should().Be(3);
    }

    [Fact]
    public void BoundStringWrite_AppliesDeclaredInvalidationAndValue()
    {
        CompiledView view = LoadBindingCard();
        view.MarkClean();

        view.SetBoundString(3, "Text", "updated");

        view.GetString(3, "Text").Should().Be("updated");
        view.Dirty.Should().Be(Invalidation.Measure | Invalidation.Paint);
        view.DirtyOf(2).Should().Be(Invalidation.Measure | Invalidation.Arrange);
    }

    [Fact]
    public async Task BindingDispatch_QueuesOffThreadAndStopsAfterTeardown()
    {
        CompiledView view = LoadBindingCard();
        var queued = new List<Action>();
        bool ran = false;

        view.ConfigureUiDispatcher(action =>
        {
            queued.Add(action);
            return true;
        });

        bool queuedResult = await Task.Run(() => view.TryDispatch(() => ran = true));

        queuedResult.Should().BeTrue();
        ran.Should().BeFalse();
        queued.Should().ContainSingle();

        queued[0]();
        ran.Should().BeTrue();

        view.ClearUiDispatcher();
        bool afterTeardown = await Task.Run(() => view.TryDispatch(() => ran = false));

        afterTeardown.Should().BeFalse();
        ran.Should().BeTrue();
        Action configureAgain = () => view.ConfigureUiDispatcher(_ => true);
        configureAgain.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Load_RejectsAnUnsupportedIrVersion()
    {
        string json = CardJson.Replace("\"ir_version\": \"0.2.0\"", "\"ir_version\": \"9.9.9\"");

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
    public void Load_RejectsUnknownDocumentFields()
    {
        string json = CardJson.Replace(
            "\"semantics\": []",
            "\"semantics\": [], \"silent_semantic_downgrade\": true");

        Action load = () => IrLoader.Load(json);

        load.Should().Throw<IrLoadException>().WithMessage("*not valid JSON*");
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

    [Fact]
    public void Load_RejectsNodeWhoseParentDoesNotListItAsChild()
    {
        string json = CardJson.Replace(
            "\"children\": [3, 4]",
            "\"children\": [4]");

        Action load = () => IrLoader.Load(json);

        load.Should()
            .Throw<IrLoadException>()
            .WithMessage("*node 3 claims parent 2, which does not list it as a child*");
    }

    [Fact]
    public void Load_RejectsDuplicateChildren()
    {
        string json = CardJson.Replace(
            "\"children\": [3, 4]",
            "\"children\": [3, 3, 4]");

        Action load = () => IrLoader.Load(json);

        load.Should()
            .Throw<IrLoadException>()
            .WithMessage("*node 2 lists child 3 more than once*");
    }

    [Fact]
    public void Load_RejectsDisconnectedCycles()
    {
        string json = CardJson
            .Replace(
                "{ \"id\": 2, \"kind\": \"stackPanel\", \"parent\": 1, \"children\": [3, 4], \"text\": null },",
                "{ \"id\": 2, \"kind\": \"stackPanel\", \"parent\": 1, \"children\": [], \"text\": null },")
            .Replace(
                "{ \"id\": 3, \"kind\": \"textBlock\", \"parent\": 2, \"children\": [], \"text\": null },",
                "{ \"id\": 3, \"kind\": \"textBlock\", \"parent\": 4, \"children\": [4], \"text\": null },")
            .Replace(
                "{ \"id\": 4, \"kind\": \"textBlock\", \"parent\": 2, \"children\": [], \"text\": null }",
                "{ \"id\": 4, \"kind\": \"textBlock\", \"parent\": 3, \"children\": [3], \"text\": null }");

        Action load = () => IrLoader.Load(json);

        load.Should()
            .Throw<IrLoadException>()
            .WithMessage("*node 3 is not reachable from root node 0*");
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
    public void SlotWrite_TracksNodeAndAncestorLayoutInvalidation()
    {
        CompiledView view = LoadCard();
        view.MarkClean();

        view.SetText("Body", "something longer");

        view.DirtyOf(3).Should().Be(Invalidation.Measure | Invalidation.Paint);
        view.DirtyOf(2).Should().Be(Invalidation.Measure | Invalidation.Arrange);
        view.DirtyOf(1).Should().Be(Invalidation.Measure | Invalidation.Arrange);
        view.DirtyOf(0).Should().Be(Invalidation.Measure | Invalidation.Arrange);
    }

    [Fact]
    public void MarkLayoutClean_PreservesPaintAndSemanticWork()
    {
        CompiledView view = LoadCard();
        view.MarkClean();
        view.Invalidate(
            Invalidation.Measure | Invalidation.Arrange | Invalidation.Paint | Invalidation.Semantics);

        view.MarkLayoutClean();

        view.Dirty.Should().Be(Invalidation.Paint | Invalidation.Semantics);
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
    public void Layout_AppendingAfterAWidthChangeRebuildsThePrefix()
    {
        CompiledView view = LoadCard();
        var engine = new LayoutEngine(view, new FixedAdvanceTextMeasurerFactory());
        engine.Layout(Size.FromWidth(200));

        view.SetText("Body", "AB");
        engine.Layout(Size.FromWidth(200));
        view.SetText("Body", "AB CDEF");
        engine.Layout(Size.FromWidth(68));

        CompiledView expectedView = LoadCard();
        expectedView.SetText("Body", "AB CDEF");
        var expectedEngine = new LayoutEngine(expectedView, new FixedAdvanceTextMeasurerFactory());
        expectedEngine.Layout(Size.FromWidth(68));

        engine.TextLinesOf(3)!.Lines.Should().Equal(expectedEngine.TextLinesOf(3)!.Lines);
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

    [Fact]
    public void Button_ContentAndClickActionParticipateInLayoutAndPaint()
    {
        string json = CardJson
            .Replace(
                "{ \"id\": 4, \"kind\": \"textBlock\", \"parent\": 2, \"children\": [], \"text\": null }",
                "{ \"id\": 4, \"kind\": \"button\", \"parent\": 2, \"children\": [], \"text\": null }")
            .Replace(
                "{ \"node\": 4, \"name\": \"Text\", \"value\": { \"type\": \"string\", \"value\": \"CD\" } }",
                "{ \"node\": 4, \"name\": \"Content\", \"value\": { \"type\": \"string\", \"value\": \"Copy\" } }")
            .Replace(
                "\"actions\": [ { \"node\": 1, \"event\": \"pointerPressed\", \"handler\": \"OnPressed\" } ]",
                "\"actions\": [ { \"node\": 1, \"event\": \"pointerPressed\", \"handler\": \"OnPressed\" }, { \"node\": 4, \"event\": \"click\", \"handler\": \"CopyCommand\" } ]");
        var view = new CompiledView(IrLoader.Load(json), Resources());
        var engine = new LayoutEngine(view, new FixedAdvanceTextMeasurerFactory());

        engine.Layout(Size.FromWidth(200));
        DisplayList list = DisplayListBuilder.Build(engine);

        view.KindOf(4).Should().Be(NodeKind.Button);
        view.FindActionHandler(4, "click").Should().Be("CopyCommand");
        engine.BoundsOf(4).Height.Should().BeGreaterThan(0);
        var bounds = engine.BoundsOf(4);
        var router = new PointerActionRouter(view);
        int invocationCount = 0;
        int invokedNode = -1;
        string? invokedHandler = null;
        router.ActionInvoked += (node, handler) =>
        {
            invocationCount++;
            invokedNode = node;
            invokedHandler = handler;
        };

        double centerX = bounds.X + (bounds.Width / 2);
        double centerY = bounds.Y + (bounds.Height / 2);
        router.Press(engine, centerX, centerY).Should().BeTrue();
        invocationCount.Should().Be(0, "click waits for pointer release");
        router.Release(engine, centerX, centerY).Should().BeTrue();
        invocationCount.Should().Be(1);
        invokedNode.Should().Be(4);
        invokedHandler.Should().Be("CopyCommand");
        router.Release(engine, centerX, centerY).Should().BeFalse();
        invocationCount.Should().Be(1, "one press/release gesture executes once");
        list.Commands.OfType<DrawTextLine>()
            .Select(line => line.Text)
            .Should().Contain("Copy");
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
    public void DisplayList_PartitionsOverlappingNamedSlotSubtreeInPaintOrder()
    {
        CompiledView view = LoadOverlap();
        var engine = new LayoutEngine(view, new FixedAdvanceTextMeasurerFactory());
        engine.Layout(Size.FromWidth(100));

        DisplayList list = DisplayListBuilder.Build(engine);

        list.DynamicCommands.OfType<DrawTextLine>()
            .Select(line => line.Text)
            .Should()
            .Equal("dynamic");
        list.StaticCommands.OfType<DrawTextLine>()
            .Select(line => line.Text)
            .Should()
            .Equal("static");
        list.Commands.OfType<FillRectangle>()
            .Select(fill => fill.Color)
            .Should()
            .Equal(OverlapDynamicColor, OverlapStaticColor);
        list.Commands.OfType<DrawTextLine>()
            .Select(line => line.Text)
            .Should()
            .Equal("dynamic", "static");
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

        DrawTextLine[] lines = list.Commands.OfType<DrawTextLine>().ToArray();
        lines.Select(line => line.Text).Should().Equal("AB", "CD");
        lines.Select(line => line.Width).Should().Equal(16, 16);
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

    [Fact]
    public void LoadingSpinner_AdvancesTheLeadingDotAcrossFrames()
    {
        var bounds = new Rect(10, 20, 8, 8);

        SpinnerDot firstFrameLead = LoadingSpinnerGeometry.GetDot(bounds, frame: 0, segment: 0);
        SpinnerDot nextFrameLead = LoadingSpinnerGeometry.GetDot(bounds, frame: 1, segment: 1);
        SpinnerDot firstFrameTrailing = LoadingSpinnerGeometry.GetDot(bounds, frame: 0, segment: 7);

        firstFrameLead.X.Should().BeApproximately(17, 0.001);
        firstFrameLead.Y.Should().BeApproximately(24, 0.001);
        firstFrameLead.Opacity.Should().Be(1);
        nextFrameLead.Opacity.Should().Be(1);
        firstFrameTrailing.Opacity.Should().Be(0.875);
        firstFrameLead.Radius.Should().BeApproximately(0.96, 0.001);
    }

}
