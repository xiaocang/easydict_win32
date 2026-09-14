using System.Reflection;
using Jint;

namespace Easydict.BobPlugin.Runtime;

/// <summary>
/// Small shims over Jint APIs whose exact shape varies between versions. Reflection is used so a
/// version bump degrades at runtime (promise jobs simply not drained early) instead of breaking
/// the build.
/// </summary>
internal static class JintCompat
{
    private static readonly object Sync = new();
    private static bool _probed;
    private static PropertyInfo? _advancedProperty;
    private static MethodInfo? _processTasksMethod;

    /// <summary>
    /// Drain the engine's promise job queue if this Jint build exposes the operation.
    /// Called after every host-to-JS entry so `await` inside a plugin makes progress.
    /// </summary>
    public static void ProcessPendingJobs(Engine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        EnsureProbed(engine);

        if (_advancedProperty is null || _processTasksMethod is null)
        {
            return;
        }

        try
        {
            var advanced = _advancedProperty.GetValue(engine);
            if (advanced is not null)
            {
                _processTasksMethod.Invoke(advanced, null);
            }
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Surface what the job actually threw, not the reflection wrapper.
            throw ex.InnerException;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[JintCompat] ProcessTasks failed: {ex.Message}");
        }
    }

    private static void EnsureProbed(Engine engine)
    {
        if (_probed)
        {
            return;
        }

        lock (Sync)
        {
            if (_probed)
            {
                return;
            }

            _advancedProperty = engine.GetType().GetProperty("Advanced", BindingFlags.Public | BindingFlags.Instance);
            _processTasksMethod = _advancedProperty?.PropertyType
                .GetMethod("ProcessTasks", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            _probed = true;
        }
    }
}
