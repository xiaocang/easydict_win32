using Easydict.DirectXaml;
using Easydict.DirectXaml.Layout;
using Easydict.DirectXaml.Render;
using Easydict.DirectXaml.Text;
using Easydict.DirectXaml.Theming;
using Easydict.DirectXaml.Ir;
using Easydict.DirectXaml.Win2D;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using Easydict.WinUI.Views.Controls;
using FluentAssertions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "DirectXaml")]
public class DirectXamlBuildIntegrationTests
{
    private const string ResourceName =
        "Easydict.WinUI.Views.Controls.MinimalServiceResultItem.dxir.json";

    [Fact]
    public void ShippingIr_IsEmbeddedAndLoadable()
    {
        var assembly = typeof(SettingsService).Assembly;

        assembly.GetManifestResourceNames().Should().Contain(ResourceName);
        var document = IrLoader.LoadFromResource(assembly, ResourceName);

        document.ClassName.Should().Be("Easydict.WinUI.Views.Controls.MinimalServiceResultItem");
        document.Source.Path.Should().Be("Views/Controls/MinimalServiceResultItem.xaml");
        document.NamedSlots.Should().Contain(slot => slot.Name == "ResultText");
        document.Actions.Should().Contain(action => action.Handler == "OnHeaderPointerPressed");
    }

    [Fact]
    public void DirectRenderer_IsAvailableWhenGeneratedIrIsEmbedded()
    {
        DirectServiceResultItem.IsAvailable.Should().BeTrue();
    }

    [SkippableFact]
    public void DirectCards_ShareOneVirtualizedPaintHost()
    {
        Skip.IfNot(WinUITestHelper.CanCreateWindow, WinUITestHelper.SkipReason);

        var firstResult = new ServiceQueryResult
        {
            ServiceId = "first",
            ServiceDisplayName = "First Service",
            IsStreaming = true,
            StreamingText = "A translated result that wraps across the available width.",
        };
        var secondResult = new ServiceQueryResult
        {
            ServiceId = "second",
            ServiceDisplayName = "Second Service",
            IsStreaming = true,
            StreamingText = "A second translated result that also wraps across the available width.",
        };

        using var surface = new DirectXamlVirtualSurface();
        using var firstDirect = new DirectServiceResultItem(surface, themeRoot: null)
        {
            ServiceResult = firstResult,
        };
        using var secondDirect = new DirectServiceResultItem(surface, themeRoot: null)
        {
            ServiceResult = secondResult,
        };
        var firstXaml = new MinimalServiceResultItem
        {
            ServiceResult = firstResult,
        };
        var secondXaml = new MinimalServiceResultItem
        {
            ServiceResult = secondResult,
        };
        var xamlHost = new StackPanel();
        xamlHost.Children.Add(firstXaml.Element);
        xamlHost.Children.Add(secondXaml.Element);

        try
        {
            int virtualCanvasCount = CountVisualsOfType<CanvasVirtualControl>(surface.Element);
            int directElements = CountFrameworkElements(surface.Element);
            int xamlElements = CountFrameworkElements(xamlHost);

            virtualCanvasCount.Should().Be(1, "all direct cards must share one virtualized backing surface");
            directElements.Should().BeLessThan(
                xamlElements / 2,
                "the shared surface must eliminate one CanvasControl and paint subtree per card");
        }
        finally
        {
            firstXaml.Cleanup();
            secondXaml.Cleanup();
        }
    }

    [Fact]
    public void ShippingDirectCard_LaysOutAndPaintsCopyButton()
    {
        var assembly = typeof(SettingsService).Assembly;
        var document = IrLoader.LoadFromResource(assembly, ResourceName);
        var view = new CompiledView(document, new DictionaryResourceResolver());
        var bindings = new MinimalServiceResultItemDirectBindings(view);
        bindings.SetCopyButtonContent("Copy");
        bindings.SetResultTextText("Translated result");
        bindings.SetResultTextVisibility(Easydict.DirectXaml.Visibility.Visible);
        bindings.SetCopyButtonVisibility(Easydict.DirectXaml.Visibility.Visible);
        bindings.SetContentAreaVisibility(Easydict.DirectXaml.Visibility.Visible);

        var layout = new LayoutEngine(view, new FixedAdvanceTextMeasurerFactory());
        layout.Layout(Easydict.DirectXaml.Size.FromWidth(700));

        var copyBounds = layout.BoundsOf(MinimalServiceResultItemDirectBindings.CopyButtonNode);
        copyBounds.Width.Should().BeGreaterThan(0);
        copyBounds.Height.Should().BeGreaterThan(0);
        var rootBounds = layout.BoundsOf(view.RootNode);
        double copyCenterX = copyBounds.X + (copyBounds.Width / 2);
        double copyCenterY = copyBounds.Y + (copyBounds.Height / 2);
        rootBounds.Contains(copyCenterX, copyCenterY).Should().BeTrue();

        var router = new PointerActionRouter(view);
        int invocationCount = 0;
        string? invokedHandler = null;
        router.ActionInvoked += (_, handler) =>
        {
            invocationCount++;
            invokedHandler = handler;
        };
        router.Press(layout, copyCenterX, copyCenterY).Should().BeTrue();
        router.Release(layout, copyCenterX, copyCenterY).Should().BeTrue();
        invocationCount.Should().Be(1);
        invokedHandler.Should().Be("CopyCommand");
        DisplayListBuilder.Build(layout).Commands
            .OfType<DrawTextLine>()
            .Select(command => command.Text)
            .Should()
            .Contain("Copy");
    }

    private static int CountFrameworkElements(DependencyObject root)
    {
        int count = root is FrameworkElement ? 1 : 0;
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            count += CountFrameworkElements(VisualTreeHelper.GetChild(root, index));
        }

        return count;
    }
    private static int CountVisualsOfType<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = root is T ? 1 : 0;
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            count += CountVisualsOfType<T>(VisualTreeHelper.GetChild(root, index));
        }

        return count;
    }
}
