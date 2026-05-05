namespace Easydict.WindowsAI.Services;

/// <summary>
/// Lightweight static helper for the settings page (and any other UI surface)
/// to query Phi Silica availability without instantiating the translation service.
/// </summary>
public static class WindowsLocalAIAvailability
{
    private static readonly Lazy<IWindowsLanguageModelClient> _defaultClient =
        new(() => new WindowsLanguageModelClient(), LazyThreadSafetyMode.PublicationOnly);

    public static IWindowsLanguageModelClient Client => _defaultClient.Value;

    public static WindowsAIReadyState GetReadyState() => Client.GetReadyState();

    public static bool IsReady => GetReadyState() == WindowsAIReadyState.Ready;

    /// <summary>
    /// Resource-key suffix for the user-facing message describing the current state.
    /// Settings page maps this to a Resources.resw entry like "WindowsLocalAI_Status_Ready".
    /// </summary>
    public static string GetStatusResourceKey(WindowsAIReadyState state) => state switch
    {
        WindowsAIReadyState.Ready => "WindowsLocalAI_Status_Ready",
        WindowsAIReadyState.NotReady => "WindowsLocalAI_Status_NotReady",
        WindowsAIReadyState.CapabilityMissing => "WindowsLocalAI_Status_CapabilityMissing",
        WindowsAIReadyState.NotCompatibleWithSystemHardware => "WindowsLocalAI_Status_NotCompatibleHardware",
        WindowsAIReadyState.OSUpdateNeeded => "WindowsLocalAI_Status_OSUpdateNeeded",
        WindowsAIReadyState.DisabledByUser => "WindowsLocalAI_Status_DisabledByUser",
        WindowsAIReadyState.NotSupportedOnCurrentSystem => "WindowsLocalAI_Status_NotSupported",
        _ => "WindowsLocalAI_Status_NotSupported",
    };
}
