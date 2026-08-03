using Easydict.DirectXaml;
using Easydict.DirectXaml.Ir;
using Easydict.DirectXaml.Theming;
using FluentAssertions;
using Xunit;

namespace Easydict.DirectXaml.Tests;

public sealed class TypedBindingGenerationTests
{
    private const string IrResourceName =
        "Easydict.DirectXaml.Tests.TypedBindingFixture.dxir.json";

    [Fact]
    public async Task GeneratedTypedBindingsCompileAndDispatchUpdates()
    {
        var assembly = typeof(TypedBindingCardDirectBindings).Assembly;
        IrDocument document = IrLoader.LoadFromResource(assembly, IrResourceName);
        var view = new CompiledView(document, new DictionaryResourceResolver());
        var generated = new TypedBindingCardDirectBindings(view);
        var queued = new List<Action>();

        view.ConfigureUiDispatcher(action =>
        {
            queued.Add(action);
            return true;
        });

        var context = new TypedBindingContext
        {
            ResultText = "one-time",
            Status = "initial",
        };
        generated.SetBindingContext(context);

        view.GetString(2, "Text").Should().Be("one-time");
        view.GetString(3, "Content").Should().Be("initial");

        queued.Clear();
        await Task.Run(() => context.Status = "background");
        queued.Should().ContainSingle();
        queued[0]();
        view.GetString(3, "Content").Should().Be("background");

        queued.Clear();
        await Task.Run(() => context.ResultText = "must-not-rebind");
        queued.Should().BeEmpty();
        view.GetString(2, "Text").Should().Be("one-time");

        view.ClearUiDispatcher();
    }

    [Fact]
    public async Task GeneratedOneWayBindingUnsubscribesDuringTeardown()
    {
        var assembly = typeof(TypedBindingCardDirectBindings).Assembly;
        IrDocument document = IrLoader.LoadFromResource(assembly, IrResourceName);
        var view = new CompiledView(document, new DictionaryResourceResolver());
        var generated = new TypedBindingCardDirectBindings(view);
        var queued = new List<Action>();

        view.ConfigureUiDispatcher(action =>
        {
            queued.Add(action);
            return true;
        });

        var context = new TypedBindingContext
        {
            ResultText = "one-time",
            Status = "initial",
        };
        generated.SetBindingContext(context);

        // Keep teardown on the owner thread; only the model notification crosses threads.
        generated.ClearBindingContext();
        queued.Should().BeEmpty();

        await Task.Run(() => context.Status = "after-teardown");
        queued.Should().BeEmpty();
        view.GetString(3, "Content").Should().Be("initial");

        view.ClearUiDispatcher();
    }
}
