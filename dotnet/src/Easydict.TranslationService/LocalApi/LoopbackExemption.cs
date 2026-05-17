using System.Diagnostics;

namespace Easydict.TranslationService.LocalApi;

/// <summary>
/// Registers the current MSIX package as loopback-exempt so that a browser running outside
/// the AppContainer can reach <c>http://127.0.0.1:{port}</c> inside the package.
///
/// Equivalent CLI: <c>CheckNetIsolation LoopbackExempt -a -n=&lt;PackageFamilyName&gt;</c>.
/// No-op (and returns <see cref="LoopbackExemptResult.NotPackaged"/>) when the process has no packaged identity.
/// </summary>
public static class LoopbackExemption
{
    public enum LoopbackExemptResult
    {
        Applied,
        AlreadyApplied,
        NotPackaged,
        Failed,
    }

    public static async Task<LoopbackExemptResult> EnsureAsync(
        string packageFamilyName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageFamilyName))
            return LoopbackExemptResult.NotPackaged;

        // CheckNetIsolation is idempotent — adding an already-listed name returns 0 with a note.
        // We invoke and treat exit code 0 as success regardless of "already" vs. "newly added".
        var psi = new ProcessStartInfo
        {
            FileName = "CheckNetIsolation.exe",
            Arguments = $"LoopbackExempt -a -n=\"{packageFamilyName}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
                return LoopbackExemptResult.Failed;

            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return proc.ExitCode == 0
                ? LoopbackExemptResult.Applied
                : LoopbackExemptResult.Failed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LoopbackExemption] failed: {ex.Message}");
            return LoopbackExemptResult.Failed;
        }
    }
}
