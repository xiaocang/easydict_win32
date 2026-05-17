namespace Easydict.OpenVINO.Services;

public static class OpenVinoResources
{
    public static class StatusKeys
    {
        public const string Ready = "OpenVINO_Status_Ready";
        public const string NotDownloaded = "OpenVINO_Status_NotDownloaded";
        public const string Downloading = "OpenVINO_Status_Downloading";
        public const string DownloadFailed = "OpenVINO_Status_DownloadFailed";
    }

    public static class TitleKeys
    {
        public const string Ready = "OpenVINO_Title_Ready";
        public const string Unavailable = "OpenVINO_Title_Unavailable";
    }

    public static class UiKeys
    {
        public const string ConfigTitle = "OpenVINO_ConfigTitle";
        public const string ConfigDescription = "OpenVINO_ConfigDescription";
    }
}
