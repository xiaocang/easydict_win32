using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class OcrTranslateServiceTests
{
    [Fact]
    public void CancelPreviousOperation_CancelsLiveSource()
    {
        using var cts = new CancellationTokenSource();

        OcrTranslateService.CancelPreviousOperation(cts);

        cts.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void CancelPreviousOperation_IgnoresDisposedSource()
    {
        var cts = new CancellationTokenSource();
        cts.Dispose();

        var action = () => OcrTranslateService.CancelPreviousOperation(cts);

        action.Should().NotThrow();
    }
}
