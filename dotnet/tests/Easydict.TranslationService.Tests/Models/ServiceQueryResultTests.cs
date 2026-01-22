using System.ComponentModel;
using Easydict.TranslationService.Models;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Models;

/// <summary>
/// Tests for ServiceQueryResult model covering streaming UI state and collapse behavior.
/// </summary>
public class ServiceQueryResultTests
{
    private static TranslationResult CreateResult(bool fromCache = false, int timingMs = 100) =>
        new()
        {
            TranslatedText = "Translated text",
            OriginalText = "Original text",
            ServiceName = "Test Service",
            FromCache = fromCache,
            TimingMs = timingMs
        };

    private static TranslationException CreateWarningError(TranslationErrorCode code) =>
        new("Warning error") { ErrorCode = code, ServiceId = "test" };

    private static TranslationException CreateNonWarningError() =>
        new("Network error") { ErrorCode = TranslationErrorCode.NetworkError, ServiceId = "test" };

    #region DisplayText Tests

    [Fact]
    public void DisplayText_WhenStreaming_ReturnsStreamingText()
    {
        // Arrange
        var result = new ServiceQueryResult
        {
            IsStreaming = true,
            StreamingText = "Streaming content..."
        };

        // Act & Assert
        result.DisplayText.Should().Be("Streaming content...");
    }

    [Fact]
    public void DisplayText_WhenNotStreaming_ReturnsResultText()
    {
        // Arrange
        var result = new ServiceQueryResult
        {
            IsStreaming = false,
            Result = CreateResult()
        };

        // Act & Assert
        result.DisplayText.Should().Be("Translated text");
    }

    [Fact]
    public void DisplayText_WhenError_ReturnsErrorMessage()
    {
        // Arrange
        var result = new ServiceQueryResult
        {
            Error = new TranslationException("Something went wrong")
        };

        // Act & Assert
        result.DisplayText.Should().Be("Something went wrong");
    }

    [Fact]
    public void DisplayText_WhenNoResultOrError_ReturnsEmpty()
    {
        // Arrange
        var result = new ServiceQueryResult();

        // Act & Assert
        result.DisplayText.Should().BeEmpty();
    }

    #endregion

    #region StatusText Tests

    [Fact]
    public void StatusText_WhenStreaming_ReturnsStreamingMessage()
    {
        // Arrange
        var result = new ServiceQueryResult { IsStreaming = true };

        // Act & Assert
        result.StatusText.Should().Be("Streaming...");
    }

    [Fact]
    public void StatusText_WhenLoading_ReturnsTranslatingMessage()
    {
        // Arrange
        var result = new ServiceQueryResult { IsLoading = true };

        // Act & Assert
        result.StatusText.Should().Be("Translating...");
    }

    [Fact]
    public void StatusText_WhenError_ReturnsError()
    {
        // Arrange
        var result = new ServiceQueryResult
        {
            Error = new TranslationException("Failed")
        };

        // Act & Assert
        result.StatusText.Should().Be("Error");
    }

    [Fact]
    public void StatusText_WhenCachedResult_ReturnsCached()
    {
        // Arrange
        var result = new ServiceQueryResult
        {
            Result = CreateResult(fromCache: true)
        };

        // Act & Assert
        result.StatusText.Should().Be("cached");
    }

    [Fact]
    public void StatusText_WhenResult_ReturnsTimingMs()
    {
        // Arrange
        var result = new ServiceQueryResult
        {
            Result = CreateResult(timingMs: 250)
        };

        // Act & Assert
        result.StatusText.Should().Be("250ms");
    }

    #endregion

    #region ContentVisibility Tests

    [Fact]
    public void ContentVisibility_WhenStreamingWithText_ReturnsTrue()
    {
        // Arrange
        var result = new ServiceQueryResult
        {
            IsExpanded = true,
            IsStreaming = true,
            StreamingText = "Some text"
        };

        // Act & Assert
        result.ContentVisibility.Should().BeTrue();
    }

    [Fact]
    public void ContentVisibility_WhenStreamingWithoutText_ReturnsFalse()
    {
        // Arrange
        var result = new ServiceQueryResult
        {
            IsExpanded = true,
            IsStreaming = true,
            StreamingText = ""
        };

        // Act & Assert
        result.ContentVisibility.Should().BeFalse();
    }

    [Fact]
    public void ContentVisibility_WhenCollapsed_ReturnsFalse()
    {
        // Arrange
        var result = new ServiceQueryResult
        {
            IsExpanded = false,
            Result = CreateResult()
        };

        // Act & Assert
        result.ContentVisibility.Should().BeFalse();
    }

    #endregion

    #region Reset Tests

    [Fact]
    public void Reset_ClearsAllState()
    {
        // Arrange
        var result = new ServiceQueryResult
        {
            Result = CreateResult(),
            Error = new TranslationException("Error"),
            IsLoading = true,
            IsStreaming = true,
            StreamingText = "Some streaming text"
        };

        // Act
        result.Reset();

        // Assert
        result.Result.Should().BeNull();
        result.Error.Should().BeNull();
        result.IsLoading.Should().BeFalse();
        result.IsStreaming.Should().BeFalse();
        result.StreamingText.Should().BeEmpty();
    }

    [Fact]
    public void Reset_SetsIsExpandedToTrue()
    {
        // Arrange
        var result = new ServiceQueryResult { IsExpanded = false };

        // Act
        result.Reset();

        // Assert
        result.IsExpanded.Should().BeTrue();
    }

    [Fact]
    public void Reset_ClearsManuallyToggled()
    {
        // Arrange
        var result = new ServiceQueryResult { ManuallyToggled = true };

        // Act
        result.Reset();

        // Assert
        result.ManuallyToggled.Should().BeFalse();
    }

    [Fact]
    public void Reset_RaisesPropertyChangedForComputedProperties()
    {
        // Arrange
        var result = new ServiceQueryResult();
        var changedProperties = new List<string>();
        result.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != null)
                changedProperties.Add(e.PropertyName);
        };

        // Act
        result.Reset();

        // Assert
        changedProperties.Should().Contain(nameof(ServiceQueryResult.HasResult));
        changedProperties.Should().Contain(nameof(ServiceQueryResult.HasError));
        changedProperties.Should().Contain(nameof(ServiceQueryResult.DisplayText));
        changedProperties.Should().Contain(nameof(ServiceQueryResult.StatusText));
        changedProperties.Should().Contain(nameof(ServiceQueryResult.ContentVisibility));
    }

    #endregion

    #region Auto-Collapse Logic Tests

    [Fact]
    public void ApplyAutoCollapseLogic_WithUnsupportedLanguageError_Collapses()
    {
        // Arrange
        var result = new ServiceQueryResult
        {
            Error = CreateWarningError(TranslationErrorCode.UnsupportedLanguage)
        };

        // Act
        result.ApplyAutoCollapseLogic();

        // Assert
        result.IsExpanded.Should().BeFalse();
    }

    [Fact]
    public void ApplyAutoCollapseLogic_WithInvalidResponseError_Collapses()
    {
        // Arrange
        var result = new ServiceQueryResult
        {
            Error = CreateWarningError(TranslationErrorCode.InvalidResponse)
        };

        // Act
        result.ApplyAutoCollapseLogic();

        // Assert
        result.IsExpanded.Should().BeFalse();
    }

    [Fact]
    public void ApplyAutoCollapseLogic_WithNetworkError_DoesNotCollapse()
    {
        // Arrange
        var result = new ServiceQueryResult
        {
            Error = CreateNonWarningError()
        };

        // Act
        result.ApplyAutoCollapseLogic();

        // Assert
        result.IsExpanded.Should().BeTrue();
    }

    [Fact]
    public void ApplyAutoCollapseLogic_WhenManuallyToggled_DoesNotCollapse()
    {
        // Arrange
        var result = new ServiceQueryResult
        {
            Error = CreateWarningError(TranslationErrorCode.UnsupportedLanguage),
            ManuallyToggled = true
        };

        // Act
        result.ApplyAutoCollapseLogic();

        // Assert
        result.IsExpanded.Should().BeTrue();
    }

    [Fact]
    public void ApplyAutoCollapseLogic_WithNoError_DoesNotCollapse()
    {
        // Arrange
        var result = new ServiceQueryResult
        {
            Result = CreateResult()
        };

        // Act
        result.ApplyAutoCollapseLogic();

        // Assert
        result.IsExpanded.Should().BeTrue();
    }

    [Fact]
    public void IsWarningError_ReturnsTrueForWarningCodes()
    {
        // Arrange & Act & Assert
        var unsupported = new ServiceQueryResult
        {
            Error = CreateWarningError(TranslationErrorCode.UnsupportedLanguage)
        };
        unsupported.IsWarningError.Should().BeTrue();

        var invalidResponse = new ServiceQueryResult
        {
            Error = CreateWarningError(TranslationErrorCode.InvalidResponse)
        };
        invalidResponse.IsWarningError.Should().BeTrue();
    }

    [Fact]
    public void IsWarningError_ReturnsFalseForOtherCodes()
    {
        // Arrange & Act & Assert
        var networkError = new ServiceQueryResult
        {
            Error = new TranslationException("Error") { ErrorCode = TranslationErrorCode.NetworkError }
        };
        networkError.IsWarningError.Should().BeFalse();

        var timeout = new ServiceQueryResult
        {
            Error = new TranslationException("Error") { ErrorCode = TranslationErrorCode.Timeout }
        };
        timeout.IsWarningError.Should().BeFalse();

        var rateLimited = new ServiceQueryResult
        {
            Error = new TranslationException("Error") { ErrorCode = TranslationErrorCode.RateLimited }
        };
        rateLimited.IsWarningError.Should().BeFalse();

        var invalidApiKey = new ServiceQueryResult
        {
            Error = new TranslationException("Error") { ErrorCode = TranslationErrorCode.InvalidApiKey }
        };
        invalidApiKey.IsWarningError.Should().BeFalse();

        var serviceUnavailable = new ServiceQueryResult
        {
            Error = new TranslationException("Error") { ErrorCode = TranslationErrorCode.ServiceUnavailable }
        };
        serviceUnavailable.IsWarningError.Should().BeFalse();
    }

    #endregion

    #region Toggle Behavior Tests

    [Fact]
    public void ToggleExpanded_FlipsIsExpanded()
    {
        // Arrange
        var result = new ServiceQueryResult { IsExpanded = true };

        // Act
        result.ToggleExpanded();

        // Assert
        result.IsExpanded.Should().BeFalse();

        // Act again
        result.ToggleExpanded();

        // Assert
        result.IsExpanded.Should().BeTrue();
    }

    [Fact]
    public void ToggleExpanded_WhenExpanding_SetsManuallyToggled()
    {
        // Arrange
        var result = new ServiceQueryResult
        {
            IsExpanded = false,
            ManuallyToggled = false
        };

        // Act
        result.ToggleExpanded();

        // Assert
        result.IsExpanded.Should().BeTrue();
        result.ManuallyToggled.Should().BeTrue();
    }

    [Fact]
    public void ToggleExpanded_WhenCollapsing_DoesNotSetManuallyToggled()
    {
        // Arrange
        var result = new ServiceQueryResult
        {
            IsExpanded = true,
            ManuallyToggled = false
        };

        // Act
        result.ToggleExpanded();

        // Assert
        result.IsExpanded.Should().BeFalse();
        result.ManuallyToggled.Should().BeFalse();
    }

    #endregion
}
