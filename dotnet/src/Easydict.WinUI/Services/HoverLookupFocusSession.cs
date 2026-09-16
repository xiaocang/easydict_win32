namespace Easydict.WinUI.Services;

internal enum HoverLookupFocusState
{
    Focusing,
    Succeeded,
    Failed,
    Querying,
    Finished,
    Cancelled,
}

/// <summary>
/// Coordinates recognition with animation boundaries. Querying is released only after
/// recognition succeeds and the success animation finishes. Owned by the UI thread.
/// </summary>
internal sealed class HoverLookupFocusSession
{
    private readonly TaskCompletionSource<bool> _queryReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool? _recognitionSucceeded;

    public HoverLookupFocusState State { get; private set; } = HoverLookupFocusState.Focusing;
    public Task<bool> QueryReady => _queryReady.Task;

    public void Resolve(bool succeeded)
    {
        if (State == HoverLookupFocusState.Focusing)
            _recognitionSucceeded ??= succeeded;
    }

    public HoverLookupFocusState CompleteCycle()
    {
        if (State == HoverLookupFocusState.Focusing && _recognitionSucceeded is { } succeeded)
            State = succeeded ? HoverLookupFocusState.Succeeded : HoverLookupFocusState.Failed;
        return State;
    }

    public void CompleteSuccess()
    {
        if (State != HoverLookupFocusState.Succeeded) return;
        State = HoverLookupFocusState.Querying;
        _queryReady.TrySetResult(true);
    }

    public void CompleteFailure()
    {
        if (State != HoverLookupFocusState.Failed) return;
        State = HoverLookupFocusState.Finished;
        _queryReady.TrySetResult(false);
    }

    public void Cancel()
    {
        State = HoverLookupFocusState.Cancelled;
        _queryReady.TrySetCanceled();
    }
}
