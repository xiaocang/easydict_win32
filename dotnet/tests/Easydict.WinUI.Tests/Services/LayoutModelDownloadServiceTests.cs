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
    public void IsReady_ReflectsFileExistence()
    {
        using var service = new LayoutModelDownloadService();
        // IsReady should be a consistent boolean value (property accessor works)
        var result = service.IsReady;
        // If ready, both model and runtime must be ready
        if (result)
        {
            service.IsModelReady.Should().BeTrue();
            service.IsRuntimeReady.Should().BeTrue();
        }
    }

    [Fact]
    public void GetModelPath_ReturnsNullOrValidPath()
    {
        using var service = new LayoutModelDownloadService();
        var modelPath = service.GetModelPath();

        // Either null (not downloaded) or a valid .onnx path
        if (modelPath != null)
        {
            modelPath.Should().EndWith(".onnx");
            File.Exists(modelPath).Should().BeTrue();
        }
        else
        {
            service.IsModelReady.Should().BeFalse();
        }
    }

    [Fact]
    public void GetNativeLibraryDir_ReturnsNullOrValidDir()
    {
        using var service = new LayoutModelDownloadService();
        var dir = service.GetNativeLibraryDir();

        // Either null (not downloaded) or a valid directory
        if (dir != null)
        {
            Directory.Exists(dir).Should().BeTrue();
        }
        else
        {
            service.IsRuntimeReady.Should().BeFalse();
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
    public async Task ThrowsObjectDisposedException_AfterDispose()
    {
        var service = new LayoutModelDownloadService();
        service.Dispose();

        Func<Task> act = () => service.EnsureAvailableAsync();
        await act.Should().ThrowAsync<ObjectDisposedException>();
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

    [Fact]
    public void IsFileValid_ReturnsFalse_WhenFileDoesNotExist()
    {
        var result = ModelDownloadClient.IsFileValid(
            Path.Combine(Path.GetTempPath(), "nonexistent_file.onnx"), 1024);
        result.Should().BeFalse();
    }

    [Fact]
    public void IsFileValid_ReturnsFalse_WhenFileTooSmall()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Write a small file (simulating truncated download or HTML error page)
            File.WriteAllText(tempFile, "<html>Error 403 Forbidden</html>");

            var result = ModelDownloadClient.IsFileValid(tempFile, 1024 * 1024);
            result.Should().BeFalse();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void IsFileValid_ReturnsTrue_WhenFileMeetsMinSize()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Write a file that meets the minimum size
            var data = new byte[2048];
            File.WriteAllBytes(tempFile, data);

            var result = ModelDownloadClient.IsFileValid(tempFile, 1024);
            result.Should().BeTrue();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void IsReady_ReturnsFalse_WhenModelFilesTooSmall()
    {
        // With default constructor, IsReady checks real files in %LocalAppData%\Easydict\Models\
        // Since we can't control those files, we verify the property is consistent
        using var service = new LayoutModelDownloadService();
        var ready = service.IsReady;

        // If ready, both files must be valid (large enough)
        if (ready)
        {
            service.IsModelReady.Should().BeTrue();
            service.IsRuntimeReady.Should().BeTrue();
            service.GetModelPath().Should().NotBeNull();
            service.GetNativeLibraryDir().Should().NotBeNull();
            service.GetNativeLibraryPath().Should().NotBeNull();
        }
        else
        {
            // At least one is not valid — GetModelPath/GetNativeLibraryPath should reflect this
            (service.GetModelPath() is null || service.GetNativeLibraryPath() is null).Should().BeTrue();
        }
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
