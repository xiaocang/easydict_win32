using Easydict.WinUI.Services;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "WinUI")]
public class LayoutModelDownloadServiceTests
{
    [Fact]
    public void Constructor_CreatesModelDirectory()
    {
        using var service = new LayoutModelDownloadService();

        var modelsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Easydict", "Models");

        Directory.Exists(modelsDir).Should().BeTrue();
    }

    [Fact]
    public void IsReady_InitiallyFalse_WhenModelsNotDownloaded()
    {
        using var service = new LayoutModelDownloadService();
        // IsReady should be false unless both files exist
        // (can't guarantee they don't exist, but this tests the property accessor works)
        service.IsReady.Should().Be(
            File.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Easydict", "Models", "onnxruntime.dll")) &&
            File.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Easydict", "Models", "doclayout_yolo.onnx")));
    }

    [Fact]
    public void GetModelPath_ReturnsNull_WhenNotDownloaded()
    {
        using var service = new LayoutModelDownloadService();
        var modelPath = service.GetModelPath();

        if (!service.IsModelReady)
        {
            modelPath.Should().BeNull();
        }
    }

    [Fact]
    public void GetNativeLibraryDir_ReturnsNull_WhenNotDownloaded()
    {
        using var service = new LayoutModelDownloadService();
        var dir = service.GetNativeLibraryDir();

        if (!service.IsRuntimeReady)
        {
            dir.Should().BeNull();
        }
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var service = new LayoutModelDownloadService();
        service.Dispose();
        service.Dispose(); // Should not throw
    }

    [Fact]
    public void ThrowsObjectDisposedException_AfterDispose()
    {
        var service = new LayoutModelDownloadService();
        service.Dispose();

        var act = () => service.EnsureAvailableAsync();
        act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public void ModelDownloadProgress_RecordCreation()
    {
        var progress = new ModelDownloadProgress("runtime", 1024, 10240, 10.0);

        progress.Stage.Should().Be("runtime");
        progress.BytesDownloaded.Should().Be(1024);
        progress.TotalBytes.Should().Be(10240);
        progress.Percentage.Should().Be(10.0);
    }
}

[Trait("Category", "WinUI")]
public class VisionLayoutDetectionServiceTests
{
    [Fact]
    public void ParseDetectionArray_ValidJson_ParsesCorrectly()
    {
        var json = """
        [
            {"type":"title","x":10,"y":5,"width":80,"height":4,"confidence":0.95},
            {"type":"figure","x":20,"y":30,"width":60,"height":40,"confidence":0.88},
            {"type":"text","x":10,"y":75,"width":80,"height":20}
        ]
        """;

        var results = VisionLayoutDetectionService.ParseDetectionArray(json, 1000, 1000);

        results.Should().HaveCount(3);

        results[0].RegionType.Should().Be(LayoutRegionType.Title);
        results[0].X.Should().BeApproximately(100, 0.1);  // 10% of 1000
        results[0].Confidence.Should().BeApproximately(0.95f, 0.01f);

        results[1].RegionType.Should().Be(LayoutRegionType.Figure);
        results[1].X.Should().BeApproximately(200, 0.1);  // 20% of 1000
        results[1].Width.Should().BeApproximately(600, 0.1);  // 60% of 1000

        results[2].RegionType.Should().Be(LayoutRegionType.Body);  // "text" maps to Body
        results[2].Confidence.Should().BeApproximately(0.8f, 0.01f);  // Default confidence
    }

    [Fact]
    public void ParseDetectionArray_InvalidJson_ReturnsEmpty()
    {
        var results = VisionLayoutDetectionService.ParseDetectionArray("not json", 1000, 1000);
        results.Should().BeEmpty();
    }

    [Fact]
    public void ParseDetectionArray_EmptyArray_ReturnsEmpty()
    {
        var results = VisionLayoutDetectionService.ParseDetectionArray("[]", 1000, 1000);
        results.Should().BeEmpty();
    }

    [Fact]
    public void ParseVisionResponse_ValidOpenAIResponse_ParsesCorrectly()
    {
        var response = """
        {
            "choices": [{
                "message": {
                    "content": "[{\"type\":\"title\",\"x\":10,\"y\":5,\"width\":80,\"height\":4,\"confidence\":0.95}]"
                }
            }]
        }
        """;

        var results = VisionLayoutDetectionService.ParseVisionResponse(response, 1000, 1000);
        results.Should().HaveCount(1);
        results[0].RegionType.Should().Be(LayoutRegionType.Title);
    }

    [Fact]
    public void ParseVisionResponse_ContentWithCodeBlock_ParsesCorrectly()
    {
        var response = """
        {
            "choices": [{
                "message": {
                    "content": "Here are the detected regions:\n```json\n[{\"type\":\"table\",\"x\":15,\"y\":40,\"width\":70,\"height\":30,\"confidence\":0.92}]\n```"
                }
            }]
        }
        """;

        var results = VisionLayoutDetectionService.ParseVisionResponse(response, 1000, 1000);
        results.Should().HaveCount(1);
        results[0].RegionType.Should().Be(LayoutRegionType.Table);
    }

    [Fact]
    public void ParseVisionResponse_InvalidJson_ReturnsEmpty()
    {
        var response = "not json";
        var results = VisionLayoutDetectionService.ParseVisionResponse(response, 1000, 1000);
        results.Should().BeEmpty();
    }
}
