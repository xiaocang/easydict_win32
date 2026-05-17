namespace Easydict.WindowsAI.Services;

public static class PhiSilicaResources
{
    public static class StatusKeys
    {
        public const string Ready = "WindowsLocalAI_Status_Ready";
        public const string NotReady = "WindowsLocalAI_Status_NotReady";
        public const string Preparing = "WindowsLocalAI_Status_Preparing";
        public const string PrepareFailed = "WindowsLocalAI_Status_PrepareFailed";
        public const string CapabilityMissing = "WindowsLocalAI_Status_CapabilityMissing";
        public const string NotCompatibleHardware = "WindowsLocalAI_Status_NotCompatibleHardware";
        public const string OSUpdateNeeded = "WindowsLocalAI_Status_OSUpdateNeeded";
        public const string DisabledByUser = "WindowsLocalAI_Status_DisabledByUser";
        public const string UnsupportedWindowsAIBaseline = "WindowsLocalAI_Status_UnsupportedWindowsAIBaseline";
        public const string NotSupported = "WindowsLocalAI_Status_NotSupported";
        public const string RuntimeUnhealthy = "WindowsLocalAI_Status_RuntimeUnhealthy";
        public const string WarmingUp = "WindowsLocalAI_Status_WarmingUp";
        public const string WarmupRequired = "WindowsLocalAI_Status_WarmupRequired";
    }

    public static class TitleKeys
    {
        public const string Ready = "WindowsLocalAI_Title_Ready";
        public const string Unavailable = "WindowsLocalAI_Title_Unavailable";
    }

    public static class UiKeys
    {
        public const string Preparing = "WindowsLocalAI_Preparing";
        public const string PrepareButton = "WindowsLocalAI_PrepareButton";
    }

    public static class HintKeys
    {
        public const string ServiceDisabled = "WindowsLocalAI_Hint_ServiceDisabled";
        public const string PackageResourceInUse = "WindowsLocalAI_Hint_PackageResourceInUse";
        public const string NpuRuntimeReset = "WindowsLocalAI_Hint_NpuRuntimeReset";
        public const string NpuModelSessionInit = "WindowsLocalAI_Hint_NpuModelSessionInit";
    }

    public static class ProgressKeys
    {
        public const string Checking = "PhiSilicaPreparationProgress_Checking";
        public const string Requesting = "PhiSilicaPreparationProgress_Requesting";
        public const string Waiting = "PhiSilicaPreparationProgress_Waiting";
        public const string Finalizing = "PhiSilicaPreparationProgress_Finalizing";
        public const string CreatingSession = "PhiSilicaPreparationProgress_CreatingSession";
        public const string ReusingExisting = "PhiSilicaPreparationProgress_ReusingExisting";
        public const string WarmingUp = "PhiSilicaPreparationProgress_WarmingUp";
        public const string DeliveryOptimizationEstimate = "PhiSilicaPreparationProgress_DeliveryOptimizationEstimate";
        public const string TimeUnknown = "PhiSilicaPreparationProgress_TimeUnknown";
        public const string WindowsUpdateLink = "PhiSilicaPreparationProgress_WindowsUpdateLink";
    }

    public static class PromptKeys
    {
        public const string Title = "PhiSilicaModelPrompt_Title";
        public const string Message = "PhiSilicaModelPrompt_Message";
        public const string DownloadNow = "PhiSilicaModelPrompt_DownloadNow";
        public const string Disable = "PhiSilicaModelPrompt_Disable";
        public const string NotNow = "PhiSilicaModelPrompt_NotNow";
    }
}
