using Easydict.TranslationService;
using Easydict.TranslationService.Models;
using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public class ServiceResultDemotionHelperTests
{
    private static ServiceQueryResult MakeResult(
        TranslationResultKind? kind = TranslationResultKind.NoResult,
        bool isLoading = false,
        bool isStreaming = false,
        bool hasError = false)
    {
        var r = new ServiceQueryResult
        {
            ServiceId = "test",
            IsLoading = isLoading,
            IsStreaming = isStreaming,
        };
        if (hasError)
        {
            r.Error = new TranslationException("boom");
        }
        if (kind.HasValue)
        {
            r.Result = new TranslationResult
            {
                ServiceName = "test",
                ResultKind = kind.Value,
                TranslatedText = kind == TranslationResultKind.Success ? "hi" : "",
                OriginalText = "q"
            };
        }
        return r;
    }

    [Fact]
    public void IsDemoted_ReturnsFalse_WhenSettingDisabled()
    {
        var r = MakeResult(TranslationResultKind.NoResult);
        ServiceResultDemotionHelper.IsDemoted(r, hideEmptySetting: false).Should().BeFalse();
    }

    [Fact]
    public void IsDemoted_ReturnsTrue_ForNoResultWithSettingEnabled()
    {
        var r = MakeResult(TranslationResultKind.NoResult);
        ServiceResultDemotionHelper.IsDemoted(r, hideEmptySetting: true).Should().BeTrue();
    }

    [Fact]
    public void IsDemoted_ReturnsFalse_ForSuccessResult()
    {
        var r = MakeResult(TranslationResultKind.Success);
        ServiceResultDemotionHelper.IsDemoted(r, hideEmptySetting: true).Should().BeFalse();
    }

    [Fact]
    public void IsDemoted_ReturnsFalse_WhileLoading()
    {
        var r = MakeResult(kind: null, isLoading: true);
        ServiceResultDemotionHelper.IsDemoted(r, hideEmptySetting: true).Should().BeFalse();
    }

    [Fact]
    public void IsDemoted_ReturnsFalse_WhileStreaming()
    {
        var r = MakeResult(TranslationResultKind.NoResult, isStreaming: true);
        ServiceResultDemotionHelper.IsDemoted(r, hideEmptySetting: true).Should().BeFalse();
    }

    [Fact]
    public void IsDemoted_ReturnsFalse_WhenErrorPresent()
    {
        var r = MakeResult(TranslationResultKind.NoResult, hasError: true);
        ServiceResultDemotionHelper.IsDemoted(r, hideEmptySetting: true).Should().BeFalse();
    }

    [Fact]
    public void IsDemoted_ReturnsFalse_ForNullResult()
    {
        ServiceResultDemotionHelper.IsDemoted(null, hideEmptySetting: true).Should().BeFalse();
    }

    [Fact]
    public void StablePartitionIndices_PreservesOrderWithinBuckets()
    {
        var results = new[]
        {
            MakeResult(TranslationResultKind.Success),       // 0 kept
            MakeResult(TranslationResultKind.NoResult),      // 1 demoted
            MakeResult(TranslationResultKind.Success),       // 2 kept
            MakeResult(TranslationResultKind.NoResult),      // 3 demoted
            MakeResult(kind: null, isLoading: true),         // 4 kept (loading)
        };

        var order = ServiceResultDemotionHelper.StablePartitionIndices(results, hideEmptySetting: true);
        order.Should().Equal(new[] { 0, 2, 4, 1, 3 });
    }

    [Fact]
    public void StablePartitionIndices_IsIdentityWhenSettingDisabled()
    {
        var results = new[]
        {
            MakeResult(TranslationResultKind.NoResult),
            MakeResult(TranslationResultKind.Success),
        };
        var order = ServiceResultDemotionHelper.StablePartitionIndices(results, hideEmptySetting: false);
        order.Should().Equal(new[] { 0, 1 });
    }

    [Fact]
    public void StablePartitionIndices_Idempotent()
    {
        var results = new[]
        {
            MakeResult(TranslationResultKind.NoResult),
            MakeResult(TranslationResultKind.Success),
            MakeResult(TranslationResultKind.NoResult),
        };
        var first = ServiceResultDemotionHelper.StablePartitionIndices(results, hideEmptySetting: true);
        var reordered = first.Select(i => results[i]).ToArray();
        var second = ServiceResultDemotionHelper.StablePartitionIndices(reordered, hideEmptySetting: true);
        second.Should().Equal(new[] { 0, 1, 2 });
    }
}
