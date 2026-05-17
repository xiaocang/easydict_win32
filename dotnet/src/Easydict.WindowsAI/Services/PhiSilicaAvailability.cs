namespace Easydict.WindowsAI.Services;

/// <summary>
/// Lightweight static helper for the settings page (and any other UI surface)
/// to query Phi Silica availability without instantiating the translation service.
/// </summary>
public static class PhiSilicaAvailability
{
    private static readonly Lazy<IWindowsLanguageModelClient> _defaultClient =
        new(() => new WindowsLanguageModelClient(), LazyThreadSafetyMode.PublicationOnly);

    public static IWindowsLanguageModelClient Client => _defaultClient.Value;

    public static WindowsAIReadyState GetReadyState() => Client.GetReadyState();

    /// <summary>
    /// Resource-key suffix for the user-facing message describing the current state.
    /// Settings page maps this to a Resources.resw entry like PhiSilicaResources.StatusKeys.Ready.
    /// </summary>
    public static string GetStatusResourceKey(WindowsAIReadyState state) => state switch
    {
        WindowsAIReadyState.Ready => PhiSilicaResources.StatusKeys.Ready,
        WindowsAIReadyState.NotReady => PhiSilicaResources.StatusKeys.NotReady,
        WindowsAIReadyState.CapabilityMissing => PhiSilicaResources.StatusKeys.CapabilityMissing,
        WindowsAIReadyState.NotCompatibleWithSystemHardware => PhiSilicaResources.StatusKeys.NotCompatibleHardware,
        WindowsAIReadyState.OSUpdateNeeded => PhiSilicaResources.StatusKeys.OSUpdateNeeded,
        WindowsAIReadyState.DisabledByUser => PhiSilicaResources.StatusKeys.DisabledByUser,
        WindowsAIReadyState.UnsupportedWindowsAIBaseline =>
            WindowsAIBaselineDiagnostics.UnsupportedWindowsAIBaselineResourceKey,
        WindowsAIReadyState.NotSupportedOnCurrentSystem => PhiSilicaResources.StatusKeys.NotSupported,
        _ => PhiSilicaResources.StatusKeys.NotSupported,
    };
}

