using Easydict.WinUI.Services.Memory;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services.Memory;

[Trait("Category", "WinUI")]
public sealed class PooledDenseTensorTests
{
    [Fact]
    public void Tensor_ThrowsAfterDispose()
    {
        var owner = PooledDenseTensor<float>.Rent(1, 3, 4);

        owner.Tensor.Dimensions.ToArray().Should().Equal(1, 3, 4);

        owner.Dispose();

        var act = () => owner.Tensor;
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var owner = PooledDenseTensor<float>.Rent(1, 1);

        owner.Dispose();
        owner.Dispose();

        var act = () => owner.Tensor;
        act.Should().Throw<ObjectDisposedException>();
    }
}
