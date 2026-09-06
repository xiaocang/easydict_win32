namespace Easydict.WinUI.Services.SavedItems;

internal static class SavedItemsPresentation
{
    public static string DateGroup(DateTimeOffset created, DateTimeOffset now, TimeZoneInfo zone)
    {
        var day = TimeZoneInfo.ConvertTime(created, zone).Date;
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var age = (today - day).Days;
        return age switch
        {
            <= 0 => "SavedItemsToday",
            1 => "SavedItemsYesterday",
            <= 6 => "SavedItemsLastSevenDays",
            _ => "SavedItemsEarlier"
        };
    }

    public static int ComparisonColumns(bool compare, double detailWidth) => compare && detailWidth >= 640 ? 2 : 1;
}
