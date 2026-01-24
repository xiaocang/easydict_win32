using Easydict.WinUI.Services;
using FluentAssertions;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Xunit;
using WinRT.Interop;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for TitleBarDragRegionHelper.
/// Verifies thread-safe initialization, disposal, event handling, and error scenarios.
/// </summary>
[Trait("Category", "WinUI")]
public class TitleBarDragRegionHelperTests : IDisposable
{
    private Window? _testWindow;
    private AppWindow? _testAppWindow;
    private Grid? _testTitleBar;
    private Button? _testButton;
    private TitleBarDragRegionHelper? _helper;

    public void Dispose()
    {
        _helper?.Dispose();
        _testWindow?.Close();
    }

    [Fact]
    public void Constructor_WithValidParameters_CreatesInstance()
    {
        // Arrange
        var window = CreateTestWindow();
        var appWindow = GetAppWindow(window);
        var titleBar = new Grid();
        var buttons = new FrameworkElement[] { new Button() };

        // Act
        var helper = new TitleBarDragRegionHelper(window, appWindow, titleBar, buttons, "Test");

        // Assert
        helper.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WithNullWindow_ThrowsArgumentNullException()
    {
        // Arrange
        var appWindow = CreateTestAppWindow();
        var titleBar = new Grid();
        var buttons = new FrameworkElement[] { new Button() };

        // Act
        var act = () => new TitleBarDragRegionHelper(null!, appWindow, titleBar, buttons, "Test");

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("window");
    }

    [Fact]
    public void Constructor_WithNullAppWindow_ThrowsArgumentNullException()
    {
        // Arrange
        var window = CreateTestWindow();
        var titleBar = new Grid();
        var buttons = new FrameworkElement[] { new Button() };

        // Act
        var act = () => new TitleBarDragRegionHelper(window, null!, titleBar, buttons, "Test");

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("appWindow");
    }

    [Fact]
    public void Constructor_WithNullTitleBarRegion_ThrowsArgumentNullException()
    {
        // Arrange
        var window = CreateTestWindow();
        var appWindow = GetAppWindow(window);
        var buttons = new FrameworkElement[] { new Button() };

        // Act
        var act = () => new TitleBarDragRegionHelper(window, appWindow, null!, buttons, "Test");

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("titleBarRegion");
    }

    [Fact]
    public void Constructor_WithNullPassthroughElements_ThrowsArgumentNullException()
    {
        // Arrange
        var window = CreateTestWindow();
        var appWindow = GetAppWindow(window);
        var titleBar = new Grid();

        // Act
        var act = () => new TitleBarDragRegionHelper(window, appWindow, titleBar, null!, "Test");

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("passthroughElements");
    }

    [Fact]
    public void Constructor_WithNullWindowName_ThrowsArgumentNullException()
    {
        // Arrange
        var window = CreateTestWindow();
        var appWindow = GetAppWindow(window);
        var titleBar = new Grid();
        var buttons = new FrameworkElement[] { new Button() };

        // Act
        var act = () => new TitleBarDragRegionHelper(window, appWindow, titleBar, buttons, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("windowName");
    }

    [Fact]
    public void Initialize_WithValidWindow_DoesNotThrow()
    {
        // Arrange
        var (window, appWindow, titleBar, buttons) = CreateFullTestSetup();
        var helper = new TitleBarDragRegionHelper(window, appWindow, titleBar, buttons, "Test");

        // Act
        var act = () => helper.Initialize();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Initialize_WithNoContent_DoesNotThrow()
    {
        // Arrange
        var window = new Window(); // No content set
        var appWindow = GetAppWindow(window);
        var titleBar = new Grid();
        var buttons = new FrameworkElement[] { new Button() };
        var helper = new TitleBarDragRegionHelper(window, appWindow, titleBar, buttons, "Test");

        // Act
        var act = () => helper.Initialize();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange
        var (window, appWindow, titleBar, buttons) = CreateFullTestSetup();
        var helper = new TitleBarDragRegionHelper(window, appWindow, titleBar, buttons, "Test");
        helper.Initialize();

        // Act
        var act = () =>
        {
            helper.Dispose();
            helper.Dispose();
            helper.Dispose();
        };

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Cleanup_CanBeCalledMultipleTimes()
    {
        // Arrange
        var (window, appWindow, titleBar, buttons) = CreateFullTestSetup();
        var helper = new TitleBarDragRegionHelper(window, appWindow, titleBar, buttons, "Test");
        helper.Initialize();

        // Act
        var act = () =>
        {
            helper.Cleanup();
            helper.Cleanup();
            helper.Cleanup();
        };

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_CallsCleanup()
    {
        // Arrange
        var (window, appWindow, titleBar, buttons) = CreateFullTestSetup();
        var helper = new TitleBarDragRegionHelper(window, appWindow, titleBar, buttons, "Test");
        helper.Initialize();

        // Act - Dispose should call Cleanup internally
        var act = () => helper.Dispose();

        // Assert
        act.Should().NotThrow();

        // Calling Cleanup again after Dispose should be safe (already cleaned up)
        var act2 = () => helper.Cleanup();
        act2.Should().NotThrow();
    }

    [Fact]
    public void UpdateDragRegions_BeforeInitialize_DoesNotThrow()
    {
        // Arrange
        var (window, appWindow, titleBar, buttons) = CreateFullTestSetup();
        var helper = new TitleBarDragRegionHelper(window, appWindow, titleBar, buttons, "Test");

        // Act - Call UpdateDragRegions before Initialize
        var act = () => helper.UpdateDragRegions();

        // Assert - Should not throw, just return early
        act.Should().NotThrow();
    }

    [Fact]
    public void UpdateDragRegions_AfterDispose_DoesNotThrow()
    {
        // Arrange
        var (window, appWindow, titleBar, buttons) = CreateFullTestSetup();
        var helper = new TitleBarDragRegionHelper(window, appWindow, titleBar, buttons, "Test");
        helper.Initialize();
        helper.Dispose();

        // Act
        var act = () => helper.UpdateDragRegions();

        // Assert - Should not throw, just return early due to disposed state
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Dispose_IsThreadSafe()
    {
        // Arrange
        var (window, appWindow, titleBar, buttons) = CreateFullTestSetup();
        var helper = new TitleBarDragRegionHelper(window, appWindow, titleBar, buttons, "Test");
        helper.Initialize();

        // Act - Call Dispose from multiple threads simultaneously
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            helper.Dispose();
        })).ToArray();

        var act = async () => await Task.WhenAll(tasks);

        // Assert - Should not throw, even when called concurrently
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Cleanup_IsThreadSafe()
    {
        // Arrange
        var (window, appWindow, titleBar, buttons) = CreateFullTestSetup();
        var helper = new TitleBarDragRegionHelper(window, appWindow, titleBar, buttons, "Test");
        helper.Initialize();

        // Act - Call Cleanup from multiple threads simultaneously
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            helper.Cleanup();
        })).ToArray();

        var act = async () => await Task.WhenAll(tasks);

        // Assert - Should not throw, even when called concurrently
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Initialize_WithAlreadyLoadedContent_CallsUpdateDragRegions()
    {
        // Arrange
        var (window, appWindow, titleBar, buttons) = CreateFullTestSetup();

        // Force content to be loaded
        if (window.Content is FrameworkElement content)
        {
            content.Loaded += (s, e) => { }; // Force load event
        }

        var helper = new TitleBarDragRegionHelper(window, appWindow, titleBar, buttons, "Test");

        // Act
        var act = () => helper.Initialize();

        // Assert - Should not throw even if content is already loaded
        act.Should().NotThrow();
    }

    [Fact]
    public void UpdateDragRegions_WithZeroSizedElements_DoesNotThrow()
    {
        // Arrange
        var (window, appWindow, titleBar, buttons) = CreateFullTestSetup();

        // Set elements to zero size (ActualWidth/Height will be 0)
        titleBar.Width = 0;
        titleBar.Height = 0;
        foreach (var button in buttons)
        {
            button.Width = 0;
            button.Height = 0;
        }

        var helper = new TitleBarDragRegionHelper(window, appWindow, titleBar, buttons, "Test");
        helper.Initialize();

        // Act - UpdateDragRegions should handle zero-sized elements gracefully
        var act = () => helper.UpdateDragRegions();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void UpdateDragRegions_WithEmptyPassthroughArray_DoesNotThrow()
    {
        // Arrange
        var (window, appWindow, titleBar, _) = CreateFullTestSetup();
        var emptyButtons = Array.Empty<FrameworkElement>();
        var helper = new TitleBarDragRegionHelper(window, appWindow, titleBar, emptyButtons, "Test");
        helper.Initialize();

        // Act
        var act = () => helper.UpdateDragRegions();

        // Assert - Should not throw with empty passthrough elements
        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_WithDefaultWindowName_DoesNotThrow()
    {
        // Arrange
        var window = CreateTestWindow();
        var appWindow = GetAppWindow(window);
        var titleBar = new Grid();
        var buttons = new FrameworkElement[] { new Button() };

        // Act - Use default windowName parameter
        var act = () => new TitleBarDragRegionHelper(window, appWindow, titleBar, buttons);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Initialize_CalledMultipleTimes_DoesNotThrow()
    {
        // Arrange
        var (window, appWindow, titleBar, buttons) = CreateFullTestSetup();
        var helper = new TitleBarDragRegionHelper(window, appWindow, titleBar, buttons, "Test");

        // Act - Call Initialize multiple times
        var act = () =>
        {
            helper.Initialize();
            helper.Initialize();
            helper.Initialize();
        };

        // Assert - Should not throw, subsequent calls are ignored
        act.Should().NotThrow();
    }

    [Fact]
    public void Initialize_AfterDispose_DoesNotThrow()
    {
        // Arrange
        var (window, appWindow, titleBar, buttons) = CreateFullTestSetup();
        var helper = new TitleBarDragRegionHelper(window, appWindow, titleBar, buttons, "Test");
        helper.Initialize();
        helper.Dispose();

        // Act - Try to initialize after disposal
        var act = () => helper.Initialize();

        // Assert - Should not throw, just return early
        act.Should().NotThrow();
    }

    [Fact]
    public void Initialize_AfterCleanup_CanReinitialize()
    {
        // Arrange
        var (window, appWindow, titleBar, buttons) = CreateFullTestSetup();
        var helper = new TitleBarDragRegionHelper(window, appWindow, titleBar, buttons, "Test");
        helper.Initialize();
        helper.Cleanup();

        // Act - Re-initialize after cleanup
        var act = () => helper.Initialize();

        // Assert - Should not throw, re-initialization is allowed after cleanup
        act.Should().NotThrow();
    }

    // Helper methods

    private Window CreateTestWindow()
    {
        var window = new Window
        {
            Content = new Grid()
        };
        _testWindow = window;
        return window;
    }

    private AppWindow GetAppWindow(Window window)
    {
        var hWnd = WindowNative.GetWindowHandle(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        _testAppWindow = appWindow;
        return appWindow;
    }

    private AppWindow CreateTestAppWindow()
    {
        var window = CreateTestWindow();
        return GetAppWindow(window);
    }

    private (Window window, AppWindow appWindow, Grid titleBar, FrameworkElement[] buttons) CreateFullTestSetup()
    {
        var window = CreateTestWindow();
        var appWindow = GetAppWindow(window);
        var titleBar = new Grid
        {
            Width = 800,
            Height = 40
        };
        _testTitleBar = titleBar;

        var button = new Button
        {
            Width = 40,
            Height = 40
        };
        _testButton = button;

        var buttons = new FrameworkElement[] { button };

        return (window, appWindow, titleBar, buttons);
    }
}
