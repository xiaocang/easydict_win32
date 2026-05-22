using System.Reflection;
using System.Runtime.Loader;

namespace Easydict.SidecarClient;

/// <summary>
/// Resolves large managed assemblies that MSIX publishing dedupes into
/// workers/shared instead of copying once per worker.
/// </summary>
public static class WorkerSharedAssemblyResolver
{
    public const string SharedDirEnvironmentVariable = "EASYDICT_WORKER_SHARED_DIR";

    private static readonly HashSet<string> AllowedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.Windows.SDK.NET",
        "WinRT.Runtime",
        "Microsoft.Windows.UI.Xaml",
        "Microsoft.WinUI",
        "Microsoft.InteractiveExperiences.Projection",
        "Microsoft.Web.WebView2.Core.Projection",
    };

    private static int _installed;

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1)
        {
            return;
        }

        AssemblyLoadContext.Default.Resolving += ResolveFromSharedDirectory;
    }

    internal static string ResolveSharedDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable(SharedDirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envDir))
        {
            return envDir;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "shared"));
    }

    private static Assembly? ResolveFromSharedDirectory(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName.Name)
            || !AllowedAssemblies.Contains(assemblyName.Name))
        {
            return null;
        }

        var candidate = Path.Combine(ResolveSharedDirectory(), assemblyName.Name + ".dll");
        if (!File.Exists(candidate))
        {
            return null;
        }

        return context.LoadFromAssemblyPath(candidate);
    }
}
