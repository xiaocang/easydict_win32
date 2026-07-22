using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class NativeCallbackGuardTests
{
    [Fact]
    public void Invoke_ActionOrdinaryFailure_DoesNotEscape()
    {
        Action action = () => NativeCallbackGuard.Invoke(
            "NativeCallbackGuardTests.Action",
            () => throw new InvalidOperationException());

        action.Should().NotThrow();
    }

    [Fact]
    public void Invoke_GenericActionOrdinaryFailure_DoesNotEscape()
    {
        Action action = () => NativeCallbackGuard.Invoke<int>(
            "NativeCallbackGuardTests.GenericAction",
            _ => throw new InvalidOperationException(),
            42);

        action.Should().NotThrow();
    }

    [Fact]
    public void Invoke_FunctionOrdinaryFailure_ReturnsFallback()
    {
        var result = NativeCallbackGuard.Invoke(
            "NativeCallbackGuardTests.Function",
            () => throw new InvalidOperationException(),
            fallback: false);

        result.Should().BeFalse();
    }

    [Fact]
    public void Invoke_FatalInitializerFailure_Rethrows()
    {
        Action action = () => NativeCallbackGuard.Invoke(
            "NativeCallbackGuardTests.Fatal",
            () => throw new TypeInitializationException("Easydict.TestType", new InvalidOperationException()));

        action.Should().Throw<TypeInitializationException>();
    }
}
