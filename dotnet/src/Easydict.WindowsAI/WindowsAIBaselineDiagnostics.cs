using Easydict.WindowsAI.Services;

namespace Easydict.WindowsAI;

public static class WindowsAIBaselineDiagnostics
{
    public const string UnsupportedWindowsAIBaselineResourceKey =
        PhiSilicaResources.StatusKeys.UnsupportedWindowsAIBaseline;

    public static readonly Version MinimumSupportedOsBaseline = new(10, 0, 26200, 7309);

    public static bool IsBelowMinimumOsBaseline(WindowsAIHealthFingerprint fingerprint)
    {
        return IsBelowMinimumOsBaseline(fingerprint.OsBuild, fingerprint.Ubr);
    }

    public static bool IsBelowMinimumOsBaseline(string osBuild, int? ubr)
    {
        if (!Version.TryParse(osBuild, out var osVersion))
        {
            return false;
        }

        var revision = ubr
            ?? (osVersion.Revision >= 0 ? osVersion.Revision : 0);
        var effectiveVersion = new Version(
            osVersion.Major,
            osVersion.Minor,
            osVersion.Build >= 0 ? osVersion.Build : 0,
            revision);

        return effectiveVersion < MinimumSupportedOsBaseline;
    }

    public static bool LooksLikeUnsupportedBaseline(
        WindowsAIHealthFingerprint? fingerprint,
        string? diagnosticText)
    {
        if (fingerprint is not null && IsBelowMinimumOsBaseline(fingerprint))
        {
            return true;
        }

        if (fingerprint?.WindowsActivated == false
            && fingerprint.PhiSilicaAiComponentsPresent == false)
        {
            return true;
        }

        return ContainsBaselineMarker(diagnosticText);
    }

    internal static bool ContainsBaselineMarker(string? diagnosticText)
    {
        if (string.IsNullOrWhiteSpace(diagnosticText))
        {
            return false;
        }

        return Contains("Windows AI baseline")
            || Contains("AI Components")
            || Contains("AI component")
            || Contains("Windows Update")
            || Contains("Delivery Optimization")
            || Contains("OSUpdateNeeded")
            || Contains("0x80070422")
            || Contains("activate Windows")
            || Contains("unactivated");

        bool Contains(string marker) =>
            diagnosticText.Contains(marker, StringComparison.OrdinalIgnoreCase);
    }
}
