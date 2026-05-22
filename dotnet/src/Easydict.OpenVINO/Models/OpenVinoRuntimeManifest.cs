namespace Easydict.OpenVINO.Models;

public static class OpenVinoRuntimeManifest
{
    public static readonly IReadOnlyList<string> NativeFiles =
    [
        "onnxruntime.dll",
        "onnxruntime.lib",
        "onnxruntime_providers_openvino.dll",
        "onnxruntime_providers_shared.dll",
        "openvino.dll",
        "openvino_auto_batch_plugin.dll",
        "openvino_auto_plugin.dll",
        "openvino_c.dll",
        "openvino_hetero_plugin.dll",
        "openvino_intel_cpu_plugin.dll",
        "openvino_intel_gpu_plugin.dll",
        "openvino_intel_npu_plugin.dll",
        "openvino_ir_frontend.dll",
        "openvino_onnx_frontend.dll",
        "openvino_paddle_frontend.dll",
        "openvino_pytorch_frontend.dll",
        "openvino_tensorflow_frontend.dll",
        "openvino_tensorflow_lite_frontend.dll",
        "tbb12.dll",
        "tbb12_debug.dll",
        "tbbbind_2_5.dll",
        "tbbbind_2_5_debug.dll",
        "tbbmalloc.dll",
        "tbbmalloc_debug.dll",
        "tbbmalloc_proxy.dll",
        "tbbmalloc_proxy_debug.dll",
    ];
}
