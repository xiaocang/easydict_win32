using System;
using System.Collections.Generic;
using Easydict.TranslationService.Models;
using Microsoft.UI.Dispatching;

namespace Easydict.WinUI.Services;

/// <summary>
/// Stable-cadence applicator for streaming translation snapshots.
///
/// Why this exists: each translation service streams chunks on a background thread,
/// and the natural pattern is to <c>DispatcherQueue.TryEnqueue</c> a "set
/// StreamingText" callback per chunk. With multiple services translating in
/// parallel (5+ at typical Auto-mode loadouts), the dispatcher receives 100+
/// enqueues/second, each of which invalidates a TextBlock's measure pass. Wrapped
/// TextBlocks re-measure O(text length) on every layout pass, so the UI thread
/// starves and the mouse cursor stutters on the foreground window during
/// translation — a real, measurable Windows freeze rather than perceived latency.
///
/// The fix: services push snapshots into this coalescer from any thread; a single
/// <see cref="DispatcherQueueTimer"/> on the owning window's UI thread drains the
/// pending set at a stable 10 Hz cadence. The latest snapshot is visible within 100 ms,
/// while bursts from one or many services collapse before they can repeatedly reflow text.
/// </summary>
///
/// Lifecycle: instantiate on the UI thread (the dispatcher's timer is thread-
/// affine), dispose during teardown. Stale snapshots arriving after the streaming
/// state transitions are dropped by the <see cref="ServiceQueryResult.IsStreaming"/>
/// guard inside <see cref="OnTick"/>.
/// </summary>
internal sealed class StreamingTextCoalescer : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly Dictionary<ServiceQueryResult, PendingUpdate> _pending = new();
    private readonly object _lock = new();
    private bool _disposed;
    private const int DefaultIntervalMilliseconds = 100;

    private readonly record struct PendingUpdate(string Snapshot, Action? Applied);

    public StreamingTextCoalescer(
        DispatcherQueue dispatcher,
        int intervalMs = DefaultIntervalMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(intervalMs);
        _timer.IsRepeating = true;
        _timer.Tick += OnTick;
    }

    /// <summary>
    /// Queue a streaming text snapshot for <paramref name="target"/>. Safe to call
    /// from any thread. Multiple calls before the next cadence tick collapse to the latest
    /// snapshot for the same target — the coalescer guarantees the UI sees the
    /// final snapshot, never an intermediate one.
    /// </summary>
    public void Update(ServiceQueryResult target, string snapshot) =>
        Update(target, snapshot, onApplied: null);

    /// <summary>
    /// Queues one snapshot and, if it survives coalescing, invokes <paramref name="onApplied"/>
    /// after the UI-thread assignment. Internal benchmark code uses this to delimit a measured
    /// stream without adding a second dispatcher callback.
    /// </summary>
    internal void Update(ServiceQueryResult target, string snapshot, Action? onApplied)
    {
        if (_disposed) return;

        bool startTimer;
        lock (_lock)
        {
            startTimer = _pending.Count == 0;
            _pending[target] = new PendingUpdate(snapshot, onApplied);
        }

        if (startTimer)
        {
            // The timer's Start/Stop lives on the dispatcher thread. Marshal there
            // so the first push from a background streaming task can safely arm
            // the timer.
            _dispatcher.TryEnqueue(() =>
            {
                if (!_disposed) _timer.Start();
            });
        }
    }

    /// <summary>
    /// Drop any pending snapshot for <paramref name="target"/>. The streaming
    /// completion path (<c>IsStreaming → false</c>) calls this before clearing
    /// <see cref="ServiceQueryResult.StreamingText"/> so a queued snapshot that
    /// arrives one tick later can't overwrite the committed result with stale
    /// streaming text.
    /// </summary>
    public void Forget(ServiceQueryResult target)
    {
        if (_disposed) return;
        lock (_lock) _pending.Remove(target);
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        using var hotspot = UiThreadHotspotDiagnostics.Measure("StreamingTextCoalescer.OnTick");

        if (_disposed)
        {
            sender.Stop();
            return;
        }

        // Snapshot under lock, then release before doing any UI work — the only
        // potentially expensive thing is the property assignment, which is on
        // this same dispatcher thread anyway.
        KeyValuePair<ServiceQueryResult, PendingUpdate>[] toApply;
        lock (_lock)
        {
            if (_pending.Count == 0)
            {
                // No work this tick. Stop the timer to avoid spinning between active streams;
                // Update() will restart it on the next push.
                sender.Stop();
                return;
            }

            toApply = new KeyValuePair<ServiceQueryResult, PendingUpdate>[_pending.Count];
            int i = 0;
            foreach (var kvp in _pending)
            {
                toApply[i++] = kvp;
            }
            _pending.Clear();
        }

        UiThreadHotspotDiagnostics.LogCounter("StreamingTextCoalescer.Drain", toApply.Length);

        foreach (var (target, pending) in toApply)
        {
            // The streaming-complete lambda may have flipped IsStreaming to false
            // between the Update call and this tick. Drop the stale snapshot so
            // it can't overwrite the final committed result with stale streaming text.
            if (target.IsStreaming)
            {
                target.StreamingText = pending.Snapshot;
                pending.Applied?.Invoke();
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _timer.Tick -= OnTick; } catch { /* shutdown race */ }
        try { _timer.Stop(); } catch { /* shutdown race */ }
        lock (_lock) _pending.Clear();
    }
}
