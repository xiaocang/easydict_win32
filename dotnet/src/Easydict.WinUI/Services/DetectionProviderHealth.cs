namespace Easydict.WinUI.Services;

/// <summary>
/// Remembers which language-detection providers failed recently, so the next detection
/// tries a provider that is answering first. On a network where one provider is blackholed
/// (e.g. Google from mainland China) every detection would otherwise wait out that
/// provider's whole attempt timeout before reaching the one that works.
/// A demoted provider is only moved to the end of the chain, never skipped, and it
/// regains its place once it answers again or the demotion expires.
/// </summary>
internal sealed class DetectionProviderHealth
{
    internal static readonly TimeSpan DefaultDemotionPeriod = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _demotionPeriod;
    private readonly Dictionary<string, DateTimeOffset> _failedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public DetectionProviderHealth(TimeProvider? timeProvider = null, TimeSpan? demotionPeriod = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _demotionPeriod = demotionPeriod ?? DefaultDemotionPeriod;
    }

    /// <summary>
    /// Returns <paramref name="serviceIds"/> with recently failed providers moved to the end,
    /// keeping the configured order within each group.
    /// </summary>
    public IReadOnlyList<string> Order(IReadOnlyList<string> serviceIds)
    {
        lock (_lock)
        {
            var now = _timeProvider.GetUtcNow();
            var healthy = new List<string>(serviceIds.Count);
            var demoted = new List<string>();
            foreach (var id in serviceIds)
            {
                if (_failedAt.TryGetValue(id, out var failedAt) && now - failedAt < _demotionPeriod)
                    demoted.Add(id);
                else
                    healthy.Add(id);
            }

            healthy.AddRange(demoted);
            return healthy;
        }
    }

    public void ReportFailure(string serviceId)
    {
        lock (_lock)
        {
            _failedAt[serviceId] = _timeProvider.GetUtcNow();
        }
    }

    public void ReportSuccess(string serviceId)
    {
        lock (_lock)
        {
            _failedAt.Remove(serviceId);
        }
    }
}
