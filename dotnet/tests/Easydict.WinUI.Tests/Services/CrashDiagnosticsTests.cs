using System.Runtime.InteropServices;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class CrashDiagnosticsTests
{
    [Fact]
    public void IsProcessFatal_OrdinaryException_IsFalse()
    {
        CrashDiagnostics.IsProcessFatal(new InvalidOperationException()).Should().BeFalse();
    }

    [Fact]
    public void IsProcessFatal_FatalInnerException_IsTrue()
    {
        var exception = new InvalidOperationException("outer", new SEHException());

        CrashDiagnostics.IsProcessFatal(exception).Should().BeTrue();
    }

    [Fact]
    public void IsProcessFatal_OrdinaryTypeInitializationException_IsFalse()
    {
        var exception = new TypeInitializationException(
            "Easydict.TestType",
            new InvalidOperationException("inner"));

        CrashDiagnostics.IsProcessFatal(exception).Should().BeFalse();
    }

    [Fact]
    public void RegisterGlobalHandlers_CalledTwice_IsIdempotent()
    {
        Action register = () =>
        {
            CrashDiagnostics.RegisterGlobalHandlers();
            CrashDiagnostics.RegisterGlobalHandlers();
        };

        register.Should().NotThrow();
    }
}
