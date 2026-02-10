using Easydict.WinUI.Services.ScreenCapture;
using FluentAssertions;
using Xunit;
using static Easydict.WinUI.Services.ScreenCapture.WindowDetector;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Tests for WindowDetector hit-test logic.
/// Uses the internal AddWindow helper to populate test data without P/Invoke.
/// </summary>
[Trait("Category", "WinUI")]
public class WindowDetectorTests
{
    #region RECT

    [Fact]
    public void RECT_Contains_PointInside_ReturnsTrue()
    {
        var rect = new RECT { Left = 10, Top = 10, Right = 110, Bottom = 60 };
        rect.Contains(50, 30).Should().BeTrue();
    }

    [Fact]
    public void RECT_Contains_PointOnTopLeft_ReturnsTrue()
    {
        var rect = new RECT { Left = 10, Top = 10, Right = 110, Bottom = 60 };
        rect.Contains(10, 10).Should().BeTrue();
    }

    [Fact]
    public void RECT_Contains_PointOnBottomRight_ReturnsFalse()
    {
        // Right and Bottom are exclusive
        var rect = new RECT { Left = 10, Top = 10, Right = 110, Bottom = 60 };
        rect.Contains(110, 60).Should().BeFalse();
    }

    [Fact]
    public void RECT_Contains_PointOutside_ReturnsFalse()
    {
        var rect = new RECT { Left = 10, Top = 10, Right = 110, Bottom = 60 };
        rect.Contains(0, 0).Should().BeFalse();
        rect.Contains(200, 200).Should().BeFalse();
    }

    [Fact]
    public void RECT_WidthHeight_Correct()
    {
        var rect = new RECT { Left = 10, Top = 20, Right = 110, Bottom = 70 };
        rect.Width.Should().Be(100);
        rect.Height.Should().Be(50);
    }

    #endregion

    #region FindRegionAtPoint

    [Fact]
    public void FindRegionAtPoint_EmptySnapshot_ReturnsNull()
    {
        var detector = new WindowDetector();
        detector.FindRegionAtPoint(50, 50).Should().BeNull();
    }

    [Fact]
    public void FindRegionAtPoint_PointInWindow_ReturnsWindowRect()
    {
        var detector = new WindowDetector();
        detector.AddWindow(new WindowInfo
        {
            Hwnd = 1,
            Rect = new RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 }
        });

        var result = detector.FindRegionAtPoint(400, 300);
        result.Should().NotBeNull();
        result!.Value.Left.Should().Be(0);
        result!.Value.Right.Should().Be(800);
    }

    [Fact]
    public void FindRegionAtPoint_PointOutsideAllWindows_ReturnsNull()
    {
        var detector = new WindowDetector();
        detector.AddWindow(new WindowInfo
        {
            Hwnd = 1,
            Rect = new RECT { Left = 100, Top = 100, Right = 400, Bottom = 300 }
        });

        detector.FindRegionAtPoint(50, 50).Should().BeNull();
    }

    [Fact]
    public void FindRegionAtPoint_Depth0_ReturnsDeepestChild()
    {
        var detector = new WindowDetector();

        var child = new WindowInfo
        {
            Hwnd = 2,
            Rect = new RECT { Left = 50, Top = 50, Right = 200, Bottom = 150 }
        };

        var parent = new WindowInfo
        {
            Hwnd = 1,
            Rect = new RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 }
        };
        parent.Children.Add(child);

        detector.AddWindow(parent);

        var result = detector.FindRegionAtPoint(100, 100, depth: 0);
        result.Should().NotBeNull();
        // depth=0 → deepest child
        result!.Value.Left.Should().Be(50);
        result!.Value.Right.Should().Be(200);
    }

    [Fact]
    public void FindRegionAtPoint_Depth1_ReturnsParent()
    {
        var detector = new WindowDetector();

        var child = new WindowInfo
        {
            Hwnd = 2,
            Rect = new RECT { Left = 50, Top = 50, Right = 200, Bottom = 150 }
        };

        var parent = new WindowInfo
        {
            Hwnd = 1,
            Rect = new RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 }
        };
        parent.Children.Add(child);

        detector.AddWindow(parent);

        var result = detector.FindRegionAtPoint(100, 100, depth: 1);
        result.Should().NotBeNull();
        // depth=1 → one level up from deepest = parent
        result!.Value.Left.Should().Be(0);
        result!.Value.Right.Should().Be(800);
    }

    [Fact]
    public void FindRegionAtPoint_DepthExceedsChain_ClampsToRoot()
    {
        var detector = new WindowDetector();

        var child = new WindowInfo
        {
            Hwnd = 2,
            Rect = new RECT { Left = 50, Top = 50, Right = 200, Bottom = 150 }
        };

        var parent = new WindowInfo
        {
            Hwnd = 1,
            Rect = new RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 }
        };
        parent.Children.Add(child);

        detector.AddWindow(parent);

        // depth=99 exceeds chain length of 2, should clamp to root (index 0)
        var result = detector.FindRegionAtPoint(100, 100, depth: 99);
        result.Should().NotBeNull();
        result!.Value.Left.Should().Be(0);
    }

    [Fact]
    public void FindRegionAtPoint_ZOrder_FirstWindowWins()
    {
        var detector = new WindowDetector();

        // Add front window first (Z-order: front → back)
        detector.AddWindow(new WindowInfo
        {
            Hwnd = 1,
            Rect = new RECT { Left = 50, Top = 50, Right = 300, Bottom = 200 }
        });
        // Add back window second
        detector.AddWindow(new WindowInfo
        {
            Hwnd = 2,
            Rect = new RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 }
        });

        var result = detector.FindRegionAtPoint(100, 100);
        result.Should().NotBeNull();
        // First window in list wins (Z-order)
        result!.Value.Left.Should().Be(50);
    }

    [Fact]
    public void FindRegionAtPoint_NestedChildren_ThreeLevels()
    {
        var detector = new WindowDetector();

        var grandchild = new WindowInfo
        {
            Hwnd = 3,
            Rect = new RECT { Left = 100, Top = 100, Right = 150, Bottom = 130 }
        };

        var child = new WindowInfo
        {
            Hwnd = 2,
            Rect = new RECT { Left = 50, Top = 50, Right = 200, Bottom = 180 }
        };
        child.Children.Add(grandchild);

        var parent = new WindowInfo
        {
            Hwnd = 1,
            Rect = new RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 }
        };
        parent.Children.Add(child);

        detector.AddWindow(parent);

        // depth=0 → grandchild
        var d0 = detector.FindRegionAtPoint(120, 110, depth: 0);
        d0!.Value.Left.Should().Be(100);

        // depth=1 → child
        var d1 = detector.FindRegionAtPoint(120, 110, depth: 1);
        d1!.Value.Left.Should().Be(50);

        // depth=2 → parent
        var d2 = detector.FindRegionAtPoint(120, 110, depth: 2);
        d2!.Value.Left.Should().Be(0);
    }

    #endregion

    #region GetMaxDepthAtPoint

    [Fact]
    public void GetMaxDepthAtPoint_EmptySnapshot_ReturnsZero()
    {
        var detector = new WindowDetector();
        detector.GetMaxDepthAtPoint(50, 50).Should().Be(0);
    }

    [Fact]
    public void GetMaxDepthAtPoint_NoChildren_ReturnsZero()
    {
        var detector = new WindowDetector();
        detector.AddWindow(new WindowInfo
        {
            Hwnd = 1,
            Rect = new RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 }
        });

        detector.GetMaxDepthAtPoint(400, 300).Should().Be(0);
    }

    [Fact]
    public void GetMaxDepthAtPoint_OneChild_ReturnsOne()
    {
        var detector = new WindowDetector();

        var child = new WindowInfo
        {
            Hwnd = 2,
            Rect = new RECT { Left = 50, Top = 50, Right = 200, Bottom = 150 }
        };

        var parent = new WindowInfo
        {
            Hwnd = 1,
            Rect = new RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 }
        };
        parent.Children.Add(child);

        detector.AddWindow(parent);

        detector.GetMaxDepthAtPoint(100, 100).Should().Be(1);
    }

    [Fact]
    public void GetMaxDepthAtPoint_TwoLevels_ReturnsTwo()
    {
        var detector = new WindowDetector();

        var grandchild = new WindowInfo
        {
            Hwnd = 3,
            Rect = new RECT { Left = 100, Top = 100, Right = 150, Bottom = 130 }
        };

        var child = new WindowInfo
        {
            Hwnd = 2,
            Rect = new RECT { Left = 50, Top = 50, Right = 200, Bottom = 180 }
        };
        child.Children.Add(grandchild);

        var parent = new WindowInfo
        {
            Hwnd = 1,
            Rect = new RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 }
        };
        parent.Children.Add(child);

        detector.AddWindow(parent);

        detector.GetMaxDepthAtPoint(120, 110).Should().Be(2);
    }

    [Fact]
    public void GetMaxDepthAtPoint_PointOnParentNotChild_ReturnsZero()
    {
        var detector = new WindowDetector();

        var child = new WindowInfo
        {
            Hwnd = 2,
            Rect = new RECT { Left = 200, Top = 200, Right = 400, Bottom = 400 }
        };

        var parent = new WindowInfo
        {
            Hwnd = 1,
            Rect = new RECT { Left = 0, Top = 0, Right = 800, Bottom = 600 }
        };
        parent.Children.Add(child);

        detector.AddWindow(parent);

        // Point at (50, 50) is in parent but not in child
        detector.GetMaxDepthAtPoint(50, 50).Should().Be(0);
    }

    #endregion
}
