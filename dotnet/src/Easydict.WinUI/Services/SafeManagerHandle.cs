using Easydict.TranslationService;

namespace Easydict.WinUI.Services;

/// <summary>
/// A reference-counted handle to a TranslationManager instance.
/// Prevents the manager from being disposed while streaming operations are in progress.
///
/// Usage pattern:
/// <code>
/// using var handle = TranslationManagerService.Instance.AcquireHandle();
/// var manager = handle.Manager;
/// await foreach (var chunk in manager.TranslateStreamAsync(...))
/// {
///     // Process chunks
/// }
/// // Manager is guaranteed to remain valid until handle is disposed
/// </code>
/// </summary>
public sealed class SafeManagerHandle : IDisposable
{
    private readonly TranslationManager _manager;
    private readonly Action _onRelease;
    private bool _disposed;

    internal SafeManagerHandle(TranslationManager manager, Action onRelease)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _onRelease = onRelease ?? throw new ArgumentNullException(nameof(onRelease));
    }

    /// <summary>
    /// Gets the TranslationManager instance held by this handle.
    /// Throws ObjectDisposedException if the handle has been disposed.
    /// </summary>
    public TranslationManager Manager => !_disposed
        ? _manager
        : throw new ObjectDisposedException(nameof(SafeManagerHandle));

    /// <summary>
    /// Releases the handle, decrementing the reference count on the manager.
    /// When all handles are released, the manager becomes eligible for disposal.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _onRelease();
        }
    }
}
