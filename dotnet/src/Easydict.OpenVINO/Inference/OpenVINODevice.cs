namespace Easydict.OpenVINO.Inference;

/// <summary>
/// Compute device requested for OpenVINO inference. Maps to the
/// <c>device_type</c> parameter of <c>OrtOpenVINOProviderOptions</c>.
/// </summary>
public enum OpenVINODevice
{
    /// <summary>Let OpenVINO pick (NPU → GPU → CPU).</summary>
    Auto,

    /// <summary>Neural Processing Unit (Intel AI Boost, Snapdragon Hexagon, etc.).</summary>
    NPU,

    /// <summary>Integrated or discrete GPU.</summary>
    GPU,

    /// <summary>CPU (always available; used as fallback).</summary>
    CPU,
}

internal static class OpenVINODeviceExtensions
{
    /// <summary>
    /// Format expected by <c>OrtOpenVINOProviderOptions.device_type</c>:
    /// "AUTO:NPU,GPU,CPU", "NPU", "GPU", or "CPU".
    /// </summary>
    public static string ToOpenVINOString(this OpenVINODevice device) => device switch
    {
        OpenVINODevice.Auto => "AUTO:NPU,GPU,CPU",
        OpenVINODevice.NPU  => "NPU",
        OpenVINODevice.GPU  => "GPU",
        OpenVINODevice.CPU  => "CPU",
        _                   => "AUTO:NPU,GPU,CPU",
    };
}
