using Easydict.WinUI.Services;
using FluentAssertions;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

/// <summary>
/// Unit tests for <see cref="TableStructureRecognitionService"/> static helpers.
/// No live <c>InferenceSession</c> is instantiated — all paths that touch the
/// ONNX model are exercised via the visual end-to-end run, not here.
/// </summary>
[Trait("Category", "WinUI")]
public class TableStructureRecognitionServiceTests
{
    // ---------------- PreprocessCrop ----------------

    [Fact]
    public void PreprocessCrop_ShorterSideHitsShortestEdge()
    {
        // 600×400 crop inside a 1024×1024 page. Shortest side = 400 → scale = 800/400 = 2.0,
        // longest side = 600 * 2.0 = 1200 > 1000 → scale down to 1000/600 ≈ 1.667,
        // final newW = 1000, newH = 667.
        var page = new byte[1024 * 1024 * 4];

        var (tensor, newW, newH) = TableStructureRecognitionService.PreprocessCrop(
            page, 1024, 1024, cropX: 100, cropY: 100, cropW: 600, cropH: 400);

        newW.Should().Be(1000);
        newH.Should().Be(667);
        tensor.Dimensions.ToArray().Should().Equal(1, 3, 667, 1000);
    }

    [Fact]
    public void PreprocessCrop_SquareCropScalesToShortestEdge()
    {
        // 400×400 crop → shortest side 400 → scale 800/400 = 2.0 → 800×800 (no longest clamp).
        var page = new byte[1024 * 1024 * 4];

        var (_, newW, newH) = TableStructureRecognitionService.PreprocessCrop(
            page, 1024, 1024, cropX: 0, cropY: 0, cropW: 400, cropH: 400);

        newW.Should().Be(800);
        newH.Should().Be(800);
    }

    [Fact]
    public void PreprocessCrop_AppliesImageNetNormalization()
    {
        // Feed a solid mid-gray crop (128/255 ≈ 0.502 after rescale). After
        // ImageNet normalize with mean 0.485, std 0.229, the red channel should
        // land at (0.502 - 0.485) / 0.229 ≈ 0.074. Check one pixel.
        var page = new byte[10 * 10 * 4];
        for (var i = 0; i < page.Length; i += 4)
        {
            page[i] = 128;     // B
            page[i + 1] = 128; // G
            page[i + 2] = 128; // R
            page[i + 3] = 255; // A
        }

        var (tensor, _, _) = TableStructureRecognitionService.PreprocessCrop(
            page, 10, 10, cropX: 0, cropY: 0, cropW: 10, cropH: 10);

        var red = tensor[0, 0, 0, 0];
        var green = tensor[0, 1, 0, 0];
        var blue = tensor[0, 2, 0, 0];

        red.Should().BeApproximately(0.074f, 0.01f);
        green.Should().BeApproximately((0.502f - 0.456f) / 0.224f, 0.01f);
        blue.Should().BeApproximately((0.502f - 0.406f) / 0.225f, 0.01f);
    }

    // ---------------- ParseDetrOutput ----------------

    [Fact]
    public void ParseDetrOutput_BelowThresholdFiltered()
    {
        // 2 queries, both land on the Row class with low confidence — should be dropped.
        var logits = new DenseTensor<float>([1, 2, 7]);  // 6 classes + no-object
        var boxes = new DenseTensor<float>([1, 2, 4]);
        for (var q = 0; q < 2; q++)
        {
            // Uniform logits → softmax ~1/7 per class.
            for (var c = 0; c < 7; c++) logits[0, q, c] = 0f;
            boxes[0, q, 0] = 0.5f;
            boxes[0, q, 1] = 0.5f;
            boxes[0, q, 2] = 0.2f;
            boxes[0, q, 3] = 0.2f;
        }

        var result = TableStructureRecognitionService.ParseDetrOutput(logits, boxes, 0.5f);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseDetrOutput_AcceptsHighConfidenceRow()
    {
        // One query with a strong Row logit and one with no-object dominance.
        var logits = new DenseTensor<float>([1, 2, 7]);
        var boxes = new DenseTensor<float>([1, 2, 4]);

        // Query 0: strong Row (class 2) — logit 10 → softmax dominated by Row.
        for (var c = 0; c < 7; c++) logits[0, 0, c] = 0f;
        logits[0, 0, 2] = 10f;
        boxes[0, 0, 0] = 0.5f;  // cx
        boxes[0, 0, 1] = 0.25f; // cy
        boxes[0, 0, 2] = 0.8f;  // w
        boxes[0, 0, 3] = 0.1f;  // h

        // Query 1: dominated by no-object (class 6) — should be dropped.
        for (var c = 0; c < 7; c++) logits[0, 1, c] = 0f;
        logits[0, 1, 6] = 10f;
        boxes[0, 1, 0] = 0.5f;
        boxes[0, 1, 1] = 0.5f;
        boxes[0, 1, 2] = 0.2f;
        boxes[0, 1, 3] = 0.2f;

        var result = TableStructureRecognitionService.ParseDetrOutput(logits, boxes, 0.5f);

        result.Should().HaveCount(1);
        result[0].Class.Should().Be(TableElementClass.Row);
        result[0].Confidence.Should().BeGreaterThan(0.9f);
        // (cx, cy, w, h) = (0.5, 0.25, 0.8, 0.1) → (x1, y1, w, h) = (0.1, 0.2, 0.8, 0.1)
        result[0].X.Should().BeApproximately(0.1, 0.001);
        result[0].Y.Should().BeApproximately(0.2, 0.001);
        result[0].Width.Should().BeApproximately(0.8, 0.001);
        result[0].Height.Should().BeApproximately(0.1, 0.001);
    }

    [Fact]
    public void ParseDetrOutput_ClampsNegativeBoxesToZero()
    {
        // A box that extends outside [0, 1] should be clipped, not dropped.
        var logits = new DenseTensor<float>([1, 1, 7]);
        var boxes = new DenseTensor<float>([1, 1, 4]);
        for (var c = 0; c < 7; c++) logits[0, 0, c] = 0f;
        logits[0, 0, 1] = 10f;  // Column class
        boxes[0, 0, 0] = 0.05f; // cx
        boxes[0, 0, 1] = 0.5f;  // cy
        boxes[0, 0, 2] = 0.2f;  // w  → x1 = -0.05 (clamp)
        boxes[0, 0, 3] = 0.8f;  // h

        var result = TableStructureRecognitionService.ParseDetrOutput(logits, boxes, 0.5f);

        result.Should().HaveCount(1);
        result[0].X.Should().Be(0);
        result[0].Width.Should().BeApproximately(0.15, 0.001); // clamped from 0.2
    }

    // ---------------- BuildCellGrid ----------------

    [Fact]
    public void BuildCellGrid_Produces3x4Intersections()
    {
        // 3 rows × 4 columns, fully inside a 400×300 table (image pixel space,
        // top-left origin). Each row spans the full width, each column the full
        // height — intersection = standard grid with 12 cells.
        var tableX = 0.0;
        var tableY = 0.0;
        var tableW = 400.0;
        var tableH = 300.0;

        var rows = new List<TableSubDetection>
        {
            new(TableElementClass.Row, 0.9f, 0,   0,   400, 100),
            new(TableElementClass.Row, 0.9f, 0, 100, 400, 100),
            new(TableElementClass.Row, 0.9f, 0, 200, 400, 100),
        };
        var columns = new List<TableSubDetection>
        {
            new(TableElementClass.Column, 0.9f,   0, 0, 100, 300),
            new(TableElementClass.Column, 0.9f, 100, 0, 100, 300),
            new(TableElementClass.Column, 0.9f, 200, 0, 100, 300),
            new(TableElementClass.Column, 0.9f, 300, 0, 100, 300),
        };

        var cells = TableStructureRecognitionService.BuildCellGrid(
            rows, columns, tableX, tableY, tableW, tableH);

        cells.Should().HaveCount(12);
        // First cell should be row 0, col 0, at (0, 0, 100, 100).
        cells[0].RowIndex.Should().Be(0);
        cells[0].ColumnIndex.Should().Be(0);
        cells[0].X.Should().Be(0);
        cells[0].Y.Should().Be(0);
        cells[0].Width.Should().Be(100);
        cells[0].Height.Should().Be(100);
        // Last cell should be row 2, col 3.
        cells[^1].RowIndex.Should().Be(2);
        cells[^1].ColumnIndex.Should().Be(3);
        cells[^1].X.Should().Be(300);
        cells[^1].Y.Should().Be(200);
    }

    [Fact]
    public void BuildCellGrid_SkipsSubMinimumCells()
    {
        // One column is degenerate (2 pixels wide) — all 3 cells in that column
        // should be skipped by the MinCellSidePx guard.
        var rows = new List<TableSubDetection>
        {
            new(TableElementClass.Row, 0.9f, 0,   0, 200, 100),
            new(TableElementClass.Row, 0.9f, 0, 100, 200, 100),
            new(TableElementClass.Row, 0.9f, 0, 200, 200, 100),
        };
        var columns = new List<TableSubDetection>
        {
            new(TableElementClass.Column, 0.9f,   0, 0, 100, 300),
            new(TableElementClass.Column, 0.9f, 100, 0,   2, 300),  // degenerate
            new(TableElementClass.Column, 0.9f, 102, 0,  98, 300),
        };

        var cells = TableStructureRecognitionService.BuildCellGrid(
            rows, columns, 0, 0, 200, 300);

        cells.Should().HaveCount(6); // 3 rows × 2 kept columns = 6 (degenerate column's 3 cells skipped)
        cells.Should().OnlyContain(c => c.ColumnIndex != 1);
    }

    // ---------------- DeduplicateByIoU ----------------

    [Fact]
    public void DeduplicateByIoU_KeepsHighestConfidenceAmongDuplicates()
    {
        var items = new List<TableSubDetection>
        {
            new(TableElementClass.Row, 0.70f, 0, 0, 100, 20),   // low-conf duplicate
            new(TableElementClass.Row, 0.95f, 1, 0, 100, 20),   // high-conf, ~1.0 IoU
            new(TableElementClass.Row, 0.80f, 0, 100, 100, 20), // distinct row
        };

        var result = TableStructureRecognitionService.DeduplicateByIoU(items, 0.8f);

        result.Should().HaveCount(2);
        result[0].Confidence.Should().Be(0.95f); // higher-conf duplicate survives
        result[1].Confidence.Should().Be(0.80f); // distinct row survives
    }

    // ---------------- ComputeIoU ----------------

    [Fact]
    public void ComputeIoU_PerfectOverlapIsOne()
    {
        var a = new TableSubDetection(TableElementClass.Row, 1f, 0, 0, 100, 50);
        var b = new TableSubDetection(TableElementClass.Row, 1f, 0, 0, 100, 50);
        TableStructureRecognitionService.ComputeIoU(a, b).Should().BeApproximately(1f, 0.001f);
    }

    [Fact]
    public void ComputeIoU_NoOverlapIsZero()
    {
        var a = new TableSubDetection(TableElementClass.Row, 1f, 0, 0, 100, 50);
        var b = new TableSubDetection(TableElementClass.Row, 1f, 200, 200, 100, 50);
        TableStructureRecognitionService.ComputeIoU(a, b).Should().Be(0f);
    }
}
