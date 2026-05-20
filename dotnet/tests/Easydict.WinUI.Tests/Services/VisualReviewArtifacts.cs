namespace Easydict.WinUI.Tests.Services;

internal static class VisualReviewArtifacts
{
    private const string EmitPngsVariable = "EASYDICT_EMIT_REVIEW_PNGS";

    public static bool ShouldEmitPngs()
    {
        var value = Environment.GetEnvironmentVariable(EmitPngsVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    public static string OptInMessage =>
        $"Set {EmitPngsVariable}=1 to emit optional visual review PNGs.";
}
