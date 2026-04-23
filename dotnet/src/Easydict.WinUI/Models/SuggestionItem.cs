namespace Easydict.WinUI.Models;

public sealed class SuggestionItem
{
    public required string Key { get; init; }

    public required string DictDisplayName { get; init; }

    public required string DictServiceId { get; init; }
}
