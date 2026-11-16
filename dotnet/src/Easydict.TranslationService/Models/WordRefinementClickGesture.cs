namespace Easydict.TranslationService.Models;

/// <summary>
/// Tracks a click independently of pointer capture. Rich text can relinquish capture before release;
/// actual pointer cancellation, dragging, or releasing over a different word cancels activation.
/// </summary>
public sealed class WordRefinementClickGesture
{
    private uint? _pointerId;
    private string? _wordId;
    private double _x;
    private double _y;
    private bool _dragged;

    public void Press(uint pointerId, string? wordId, double x, double y)
    {
        _pointerId = pointerId;
        _wordId = wordId;
        _x = x;
        _y = y;
        _dragged = false;
    }

    public void Move(uint pointerId, double x, double y)
    {
        if (_pointerId != pointerId) return;
        var dx = x - _x;
        var dy = y - _y;
        if (dx * dx + dy * dy >= 16) _dragged = true;
    }

    public string? Release(uint pointerId, string? wordId, double x, double y)
    {
        if (_pointerId != pointerId) return null;
        Move(pointerId, x, y);
        var selected = !_dragged && _wordId == wordId ? _wordId : null;
        Cancel();
        return selected;
    }

    public void Cancel()
    {
        _pointerId = null;
        _wordId = null;
        _dragged = false;
    }
}
