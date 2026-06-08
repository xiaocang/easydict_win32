use easydict_app::{
    apply_doc_layout_yolo_nms, compute_doc_layout_yolo_iou, doc_layout_yolo_class_to_region,
    parse_doc_layout_yolo_output, preprocess_doc_layout_yolo_bgra, DocLayoutRegionType,
    DocLayoutYoloDetection, DOC_LAYOUT_YOLO_CLASS_NAMES, DOC_LAYOUT_YOLO_INPUT_SIZE,
    DOC_LAYOUT_YOLO_PADDING_VALUE,
};

#[test]
fn doc_layout_yolo_class_table_matches_docstructbench_contract() {
    assert_eq!(DOC_LAYOUT_YOLO_CLASS_NAMES.len(), 10);
    assert_eq!(
        DOC_LAYOUT_YOLO_CLASS_NAMES,
        [
            "title",
            "plain text",
            "abandon",
            "figure",
            "figure_caption",
            "table",
            "table_caption",
            "table_footnote",
            "isolate_formula",
            "formula_caption",
        ]
    );
}

#[test]
fn doc_layout_yolo_class_to_region_matches_dotnet_mapping() {
    assert_eq!(
        doc_layout_yolo_class_to_region(0),
        DocLayoutRegionType::Title
    );
    assert_eq!(
        doc_layout_yolo_class_to_region(1),
        DocLayoutRegionType::Body
    );
    assert_eq!(
        doc_layout_yolo_class_to_region(2),
        DocLayoutRegionType::Figure
    );
    assert_eq!(
        doc_layout_yolo_class_to_region(3),
        DocLayoutRegionType::Figure
    );
    assert_eq!(
        doc_layout_yolo_class_to_region(4),
        DocLayoutRegionType::Caption
    );
    assert_eq!(
        doc_layout_yolo_class_to_region(5),
        DocLayoutRegionType::Table
    );
    assert_eq!(
        doc_layout_yolo_class_to_region(8),
        DocLayoutRegionType::IsolatedFormula
    );
    assert_eq!(
        doc_layout_yolo_class_to_region(99),
        DocLayoutRegionType::Unknown
    );
}

#[test]
fn doc_layout_yolo_preprocess_letterboxes_bgra_to_planar_rgb_tensor() {
    let bgra = [
        10, 20, 30, 255, //
        40, 50, 60, 255,
    ];

    let result = preprocess_doc_layout_yolo_bgra(&bgra, 2, 1).expect("image should preprocess");

    assert_eq!(
        result.tensor_shape(),
        [1, 3, DOC_LAYOUT_YOLO_INPUT_SIZE, DOC_LAYOUT_YOLO_INPUT_SIZE]
    );
    assert_close(result.scale_x, 512.0);
    assert_close(result.scale_y, 512.0);
    assert_eq!(result.new_width, 1024);
    assert_eq!(result.new_height, 512);
    assert_eq!(result.pad_x, 0);
    assert_eq!(result.pad_y, 256);

    let channel_stride = DOC_LAYOUT_YOLO_INPUT_SIZE * DOC_LAYOUT_YOLO_INPUT_SIZE;
    let first_pixel = result.pad_y * DOC_LAYOUT_YOLO_INPUT_SIZE;
    assert_close_f32(result.tensor[first_pixel], 30.0 / 255.0);
    assert_close_f32(result.tensor[channel_stride + first_pixel], 20.0 / 255.0);
    assert_close_f32(
        result.tensor[channel_stride * 2 + first_pixel],
        10.0 / 255.0,
    );
    assert_close_f32(result.tensor[first_pixel + 512], 60.0 / 255.0);
    assert_close_f32(result.tensor[0], DOC_LAYOUT_YOLO_PADDING_VALUE);
}

#[test]
fn doc_layout_yolo_preprocess_returns_none_for_empty_dimensions() {
    assert!(preprocess_doc_layout_yolo_bgra(&[], 0, 10).is_none());
    assert!(preprocess_doc_layout_yolo_bgra(&[], 10, 0).is_none());
}

#[test]
fn doc_layout_yolo_iou_handles_overlap_cases() {
    let a = detection(DocLayoutRegionType::Body, 0.9, 0.0, 0.0, 100.0, 100.0);
    let b = detection(DocLayoutRegionType::Body, 0.8, 0.0, 0.0, 100.0, 100.0);
    let c = detection(DocLayoutRegionType::Body, 0.8, 200.0, 200.0, 100.0, 100.0);
    let d = detection(DocLayoutRegionType::Body, 0.8, 50.0, 50.0, 100.0, 100.0);

    assert_close_f32(compute_doc_layout_yolo_iou(&a, &b), 1.0);
    assert_close_f32(compute_doc_layout_yolo_iou(&a, &c), 0.0);
    assert_close_f32(compute_doc_layout_yolo_iou(&a, &d), 0.1429);
}

#[test]
fn doc_layout_yolo_nms_removes_same_region_duplicates_only() {
    let detections = vec![
        detection(DocLayoutRegionType::Body, 0.9, 0.0, 0.0, 100.0, 100.0),
        detection(DocLayoutRegionType::Body, 0.7, 5.0, 5.0, 100.0, 100.0),
        detection(DocLayoutRegionType::Figure, 0.8, 0.0, 0.0, 100.0, 100.0),
    ];

    let result = apply_doc_layout_yolo_nms(&detections, 0.45);

    assert_eq!(result.len(), 2);
    assert_close_f32(result[0].confidence, 0.9);
    assert_eq!(result[1].region_type, DocLayoutRegionType::Figure);
}

#[test]
fn doc_layout_yolo_parse_raw_output_filters_below_threshold() {
    let mut output = vec![0.0; 14];
    set_raw_box(&mut output, 0, 1, [512.0, 512.0, 100.0, 100.0]);
    for class_index in 0..10 {
        output[4 + class_index] = 0.1;
    }

    let result =
        parse_doc_layout_yolo_output(&output, [1, 14, 1], 1.0, 1.0, 0, 0, 1024, 1024, 0.25);

    assert!(result.is_empty());
}

#[test]
fn doc_layout_yolo_parse_raw_output_returns_high_confidence_detection() {
    let mut output = vec![0.0; 14];
    set_raw_box(&mut output, 0, 1, [512.0, 512.0, 100.0, 100.0]);
    output[4] = 0.95;

    let result =
        parse_doc_layout_yolo_output(&output, [1, 14, 1], 1.0, 1.0, 0, 0, 1024, 1024, 0.25);

    assert_eq!(result.len(), 1);
    assert_eq!(result[0].region_type, DocLayoutRegionType::Title);
    assert_close_f32(result[0].confidence, 0.95);
    assert_close(result[0].x, 462.0);
    assert_close(result[0].y, 462.0);
    assert_close(result[0].width, 100.0);
    assert_close(result[0].height, 100.0);
}

#[test]
fn doc_layout_yolo_parse_end_to_end_output_unmaps_letterbox_coordinates() {
    let output = vec![
        10.0, 20.0, 110.0, 220.0, 0.9, 5.0, //
        0.0, 0.0, 20.0, 20.0, 0.1, 0.0,
    ];

    let result = parse_doc_layout_yolo_output(&output, [1, 2, 6], 2.0, 2.0, 10, 20, 100, 100, 0.25);

    assert_eq!(result.len(), 1);
    assert_eq!(result[0].region_type, DocLayoutRegionType::Table);
    assert_close_f32(result[0].confidence, 0.9);
    assert_close(result[0].x, 0.0);
    assert_close(result[0].y, 0.0);
    assert_close(result[0].width, 50.0);
    assert_close(result[0].height, 100.0);
}

#[test]
fn doc_layout_yolo_parse_output_rejects_invalid_shapes() {
    assert!(
        parse_doc_layout_yolo_output(&[], [0, 14, 1], 1.0, 1.0, 0, 0, 100, 100, 0.25).is_empty()
    );
    assert!(
        parse_doc_layout_yolo_output(&[0.0], [1, 14, 1], 1.0, 1.0, 0, 0, 100, 100, 0.25).is_empty()
    );
    assert!(
        parse_doc_layout_yolo_output(&[0.0; 14], [1, 14, 1], 0.0, 1.0, 0, 0, 100, 100, 0.25)
            .is_empty()
    );
}

fn set_raw_box(output: &mut [f32], detection_index: usize, num_detections: usize, value: [f32; 4]) {
    for (feature_index, value) in value.into_iter().enumerate() {
        output[feature_index * num_detections + detection_index] = value;
    }
}

fn detection(
    region_type: DocLayoutRegionType,
    confidence: f32,
    x: f64,
    y: f64,
    width: f64,
    height: f64,
) -> DocLayoutYoloDetection {
    DocLayoutYoloDetection {
        region_type,
        confidence,
        x,
        y,
        width,
        height,
    }
}

fn assert_close(actual: f64, expected: f64) {
    assert!(
        (actual - expected).abs() < 1e-6,
        "expected {actual} to be close to {expected}"
    );
}

fn assert_close_f32(actual: f32, expected: f32) {
    assert!(
        (actual - expected).abs() < 0.01,
        "expected {actual} to be close to {expected}"
    );
}
