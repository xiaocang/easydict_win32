using Easydict.WinUI.Models;
using Easydict.WinUI.Services.SavedItems;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public sealed class SavedItemsListWindowTests
{
    private static SavedItemsCursor Cursor(int value) => new(0, DateTimeOffset.UnixEpoch.AddSeconds(-value), Guid.Empty);
    private static SavedItemsPageResult<int> Page(int start, int count, bool more = true)
        => new(Enumerable.Range(start, count).ToArray(), more ? Cursor(start + count - 1) : null);

    [Fact]
    public void ScrollingThousandsOfRows_KeepsBoundedWindowAndCanReturnToEvictedRows()
    {
        var window = new SavedItemsListWindow<int>(100, Cursor);
        window.Reset(Page(0, 25));
        for (var start = 25; start < 5000; start += 25)
        {
            window.Append(Page(start, 25, start < 4975));
            window.Items.Count.Should().BeLessThanOrEqualTo(100);
        }
        window.Items.Should().Equal(Enumerable.Range(4900, 100));
        window.PreviousCursor.Should().Be(Cursor(4900));
        window.NextCursor.Should().BeNull();

        for (var start = 4875; start >= 0; start -= 25)
        {
            window.Prepend(new SavedItemsPageResult<int>(Enumerable.Range(start, 25).ToArray(), start > 0 ? Cursor(start) : null));
            window.Items.Count.Should().BeLessThanOrEqualTo(100);
        }
        window.Items.Should().Equal(Enumerable.Range(0, 100));
        window.PreviousCursor.Should().BeNull();
        window.NextCursor.Should().Be(Cursor(99));
    }

    [Fact]
    public void RestoreAndClear_PreserveOnlyBoundedRowsAndTheirBoundaryCursors()
    {
        var window = new SavedItemsListWindow<int>(100, Cursor);
        window.Reset(Page(1000, 100), Cursor(1000));
        var cached = window.Items.ToArray();
        var previous = window.PreviousCursor;
        var next = window.NextCursor;
        window.Clear();
        window.Items.Should().BeEmpty();
        window.PreviousCursor.Should().BeNull();
        window.NextCursor.Should().BeNull();
        window.Reset(new SavedItemsPageResult<int>(cached, next), previous);
        window.Prepend(new SavedItemsPageResult<int>(Enumerable.Range(975, 25).ToArray(), Cursor(975)));
        window.Items.Should().Equal(Enumerable.Range(975, 100));
        window.NextCursor.Should().Be(Cursor(1074));
    }
}
