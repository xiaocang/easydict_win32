using System.Diagnostics;

namespace Easydict.TranslationService.Scheduling;

/// <summary>Runs thread-affine work in bounded batches, yielding to the caller's message queue.</summary>
public static class CooperativeBatchProcessor
{
    public static async Task<bool> ProcessAsync<T>(
        IReadOnlyList<T> items,
        Action<T, int> process,
        Func<Task> yieldToMessageQueue,
        Func<bool> isCurrent)
    {
        var budget = Stopwatch.StartNew();
        for (var index = 0; index < items.Count; index++)
        {
            if (!isCurrent()) return false;
            process(items[index], index);
            if ((index + 1) % 64 == 0 || budget.ElapsedMilliseconds >= 8)
            {
                await yieldToMessageQueue();
                if (!isCurrent()) return false;
                budget.Restart();
            }
        }
        return isCurrent();
    }
}
