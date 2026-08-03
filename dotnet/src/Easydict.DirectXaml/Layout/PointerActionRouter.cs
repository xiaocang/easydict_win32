namespace Easydict.DirectXaml.Layout;

/// <summary>
/// Routes pointer coordinates through layout hit testing to compiled actions, including the
/// press/release lifetime of a click.
/// </summary>
public sealed class PointerActionRouter(CompiledView view)
{
    private int _pressedClickNode = -1;
    private string? _pressedClickHandler;

    /// <summary>Whether a click action is waiting for its matching pointer release.</summary>
    public bool HasPendingClick => _pressedClickNode >= 0;

    /// <summary>Raised once when a routed action is ready to execute.</summary>
    public event Action<int, string>? ActionInvoked;

    /// <summary>Processes one pointer press at layout coordinates.</summary>
    /// <returns><c>true</c> when a compiled action handled the press.</returns>
    public bool Press(LayoutEngine layout, double x, double y)
    {
        Cancel();
        int? hit = layout.HitTest(x, y);
        if (TryFindAction(hit, "click", out int clickNode, out string? clickHandler))
        {
            _pressedClickNode = clickNode;
            _pressedClickHandler = clickHandler;
            return true;
        }

        if (!TryFindAction(hit, "pointerPressed", out int actionNode, out string? handler))
        {
            return false;
        }

        ActionInvoked?.Invoke(actionNode, handler);
        return true;
    }

    /// <summary>Processes one pointer release at layout coordinates.</summary>
    /// <returns><c>true</c> when the release executed the pending click.</returns>
    public bool Release(LayoutEngine layout, double x, double y)
    {
        int actionNode = _pressedClickNode;
        string? handler = _pressedClickHandler;
        Cancel();
        if (actionNode < 0 || handler is null)
        {
            return false;
        }

        int? hit = layout.HitTest(x, y);
        while (hit is not null)
        {
            if (hit.Value == actionNode)
            {
                ActionInvoked?.Invoke(actionNode, handler);
                return true;
            }

            hit = view.ParentOf(hit.Value);
        }

        return false;
    }

    /// <summary>Cancels a pending click, for example after pointer capture is lost.</summary>
    public void Cancel()
    {
        _pressedClickNode = -1;
        _pressedClickHandler = null;
    }

    private bool TryFindAction(
        int? node,
        string @event,
        out int actionNode,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? handler)
    {
        while (node is not null)
        {
            handler = view.FindActionHandler(node.Value, @event);
            if (handler is not null)
            {
                actionNode = node.Value;
                return true;
            }

            node = view.ParentOf(node.Value);
        }

        actionNode = -1;
        handler = null;
        return false;
    }
}
