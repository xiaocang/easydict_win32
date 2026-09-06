using Easydict.WinUI.Services.SavedItems;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public sealed class SavedItemsPresentationTests
{
    [Theory]
    [InlineData(0, "SavedItemsToday")]
    [InlineData(1, "SavedItemsYesterday")]
    [InlineData(2, "SavedItemsLastSevenDays")]
    [InlineData(6, "SavedItemsLastSevenDays")]
    [InlineData(7, "SavedItemsEarlier")]
    public void DateBuckets_DoNotOverlap(int daysAgo, string expected)
    {
        var now = DateTimeOffset.Parse("2026-09-05T12:00:00+08:00");
        var zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        SavedItemsPresentation.DateGroup(now.AddDays(-daysAgo), now, zone).Should().Be(expected);
    }

    [Fact]
    public void Midnight_UsesLocalDateAndMovesExistingRowsToYesterday()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        var created = DateTimeOffset.Parse("2026-09-05T15:59:59Z");
        SavedItemsPresentation.DateGroup(created, created, zone).Should().Be("SavedItemsToday");
        SavedItemsPresentation.DateGroup(created, created.AddSeconds(1), zone).Should().Be("SavedItemsYesterday");
    }

    [Fact]
    public void DaylightSaving_UsesCalendarDaysInsteadOfTwentyFourHourIntervals()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        var created = DateTimeOffset.Parse("2026-03-08T00:30:00-08:00");
        var now = DateTimeOffset.Parse("2026-03-09T00:15:00-07:00");
        (now - created).TotalHours.Should().BeLessThan(24);
        SavedItemsPresentation.DateGroup(created, now, zone).Should().Be("SavedItemsYesterday");
    }
}
