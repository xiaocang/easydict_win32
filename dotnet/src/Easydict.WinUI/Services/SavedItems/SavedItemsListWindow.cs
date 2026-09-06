using Easydict.WinUI.Models;

namespace Easydict.WinUI.Services.SavedItems;

/// <summary>A bounded window of rows. Evicted rows remain reachable through keyset cursors.</summary>
internal sealed class SavedItemsListWindow<T>(int capacity, Func<T, SavedItemsCursor> cursorFor)
{
    private readonly List<T> _items = [];
    public IReadOnlyList<T> Items => _items;
    public SavedItemsCursor? PreviousCursor { get; private set; }
    public SavedItemsCursor? NextCursor { get; private set; }

    public void Reset(SavedItemsPageResult<T> page, SavedItemsCursor? previousCursor = null)
    {
        _items.Clear();
        _items.AddRange(page.Items.Take(capacity));
        PreviousCursor = previousCursor;
        NextCursor = page.Items.Count > capacity ? cursorFor(_items[^1]) : page.NextCursor;
    }

    public void Append(SavedItemsPageResult<T> page)
    {
        _items.AddRange(page.Items);
        NextCursor = page.NextCursor;
        if (_items.Count > capacity)
        {
            _items.RemoveRange(0, _items.Count - capacity);
            PreviousCursor = cursorFor(_items[0]);
        }
    }

    public void Prepend(SavedItemsPageResult<T> page)
    {
        // Reverse queries return their rows in the same display order as forward queries.
        _items.InsertRange(0, page.Items);
        PreviousCursor = page.NextCursor;
        if (_items.Count > capacity)
        {
            _items.RemoveRange(capacity, _items.Count - capacity);
            NextCursor = cursorFor(_items[^1]);
        }
    }

    public void Clear() => Reset(new SavedItemsPageResult<T>([], null));
}
