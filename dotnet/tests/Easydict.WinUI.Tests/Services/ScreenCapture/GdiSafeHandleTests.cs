using Easydict.WinUI.Services.ScreenCapture;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services.ScreenCapture;

[Trait("Category", "WinUI")]
public sealed class GdiSafeHandleTests
{
    [Fact]
    public void SafeGdiObjectHandle_DefaultHandle_IsInvalidAndDoubleDisposeIsStable()
    {
        var handle = new SafeGdiObjectHandle();

        handle.IsInvalid.Should().BeTrue();
        handle.Value.Should().Be(IntPtr.Zero);

        handle.Dispose();
        handle.Dispose();

        handle.IsClosed.Should().BeTrue();
        handle.Value.Should().Be(IntPtr.Zero);
    }

    [Fact]
    public void SafeGdiObjectHandle_UnownedHandle_DoesNotReleaseNativeObjectOnDispose()
    {
        var handle = new SafeGdiObjectHandle(123, ownsHandle: false);

        handle.Value.Should().Be((IntPtr)123);

        handle.Dispose();

        handle.IsClosed.Should().BeTrue();
        handle.Value.Should().Be(IntPtr.Zero);
    }

    [Fact]
    public void SafeCompatibleDcHandle_DefaultHandle_IsInvalidAndDoubleDisposeIsStable()
    {
        var handle = new SafeCompatibleDcHandle();

        handle.IsInvalid.Should().BeTrue();
        handle.Value.Should().Be(IntPtr.Zero);

        handle.Dispose();
        handle.Dispose();

        handle.IsClosed.Should().BeTrue();
        handle.Value.Should().Be(IntPtr.Zero);
    }

    [Fact]
    public void SafeCompatibleDcHandle_UnownedHandle_DoesNotDeleteDcOnDispose()
    {
        var handle = new SafeCompatibleDcHandle(456, ownsHandle: false);

        handle.Value.Should().Be((IntPtr)456);

        handle.Dispose();

        handle.IsClosed.Should().BeTrue();
        handle.Value.Should().Be(IntPtr.Zero);
    }
}
