namespace Easydict.TranslationService.Services;

public static class FoundryLocalResources
{
    public const string InstallDocumentationUrl =
        "https://learn.microsoft.com/en-us/azure/foundry-local/how-to/how-to-use-foundry-local-cli";

    public const string StartRecoveryAction = "foundry-local-start";
    public const string InstallRecoveryAction = "foundry-local-install";

    public static class StatusKeys
    {
        public const string Ready = "FoundryLocal_Status_Ready";
        public const string NotConfigured = "FoundryLocal_Status_NotConfigured";
        public const string NotInstalled = "FoundryLocal_Status_NotInstalled";
        public const string NotRunning = "FoundryLocal_Status_NotRunning";
        public const string Starting = "FoundryLocal_Status_Starting";
        public const string LoadingModel = "FoundryLocal_Status_LoadingModel";
        public const string StartFailed = "FoundryLocal_Status_StartFailed";
        public const string Checking = "FoundryLocal_Status_Checking";
    }

    public static class TitleKeys
    {
        public const string Ready = "FoundryLocal_Title_Ready";
        public const string Unavailable = "FoundryLocal_Title_Unavailable";
    }

    public static class UiKeys
    {
        public const string ConfigTitle = "FoundryLocal_ConfigTitle";
        public const string ConfigDescription = "FoundryLocal_ConfigDescription";
        public const string EndpointLabel = "FoundryLocal_EndpointLabel";
        public const string EndpointPlaceholder = "FoundryLocal_EndpointPlaceholder";
        public const string ModelLabel = "FoundryLocal_ModelLabel";
        public const string StartButton = "FoundryLocal_StartButton";
        public const string DocsLinkText = "FoundryLocal_DocsLinkText";
        public const string InstallLinkText = "FoundryLocal_InstallLinkText";
    }
}
