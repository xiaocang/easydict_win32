namespace Easydict.OpenVINO.Models;

/// <summary>
/// Progress event for <see cref="Services.ModelDownloadService"/>. Reported via
/// <see cref="IProgress{T}"/> on a worker thread; UI must marshal to the dispatcher.
/// </summary>
/// <param name="CurrentFile">Relative path of the file currently being fetched.</param>
/// <param name="FileBytesDownloaded">Bytes received for the current file so far.</param>
/// <param name="FileTotalBytes">Total size of the current file (from Content-Length); null if unknown.</param>
/// <param name="OverallPercent">Aggregate percent across all files in this download (0-100).</param>
public sealed record ModelDownloadProgress(
    string CurrentFile,
    long FileBytesDownloaded,
    long? FileTotalBytes,
    double OverallPercent);
