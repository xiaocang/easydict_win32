use easydict_app::{
    build_table_cell_grid, build_table_structure_from_detections, calculate_tatr_crop_resize,
    compute_table_iou, deduplicate_table_detections_by_iou, parse_tatr_detr_output,
    preprocess_table_crop, tatr_detections_to_page_space, TableElementClass, TableSubDetection,
};

#[test]
fn table_preprocess_shorter_side_hits_shortest_edge_with_longest_clamp() {
    let page = vec![0; 1024 * 1024 * 4];

    let result = preprocess_table_crop(&page, 1024, 1024, 100, 100, 600, 400)
        .expect("crop should preprocess");

    assert_eq!(result.new_width, 1000);
    assert_eq!(result.new_height, 667);
    assert_eq!(result.tensor_shape(), [1, 3, 667, 1000]);
}

#[test]
fn table_preprocess_square_crop_scales_to_shortest_edge() {
    let resize = calculate_tatr_crop_resize(400, 400).expect("resize should calculate");

    assert_close(resize.scale, 2.0);
    assert_eq!(resize.new_width, 800);
    assert_eq!(resize.new_height, 800);
}

#[test]
fn table_preprocess_applies_imagenet_normalization() {
    let mut page = vec![0; 10 * 10 * 4];
    for pixel in page.chunks_exact_mut(4) {
        pixel[0] = 128;
        pixel[1] = 128;
        pixel[2] = 128;
        pixel[3] = 255;
    }

    let result =
        preprocess_table_crop(&page, 10, 10, 0, 0, 10, 10).expect("crop should preprocess");
    let channel_stride = result.new_width * result.new_height;
    let red = result.tensor[0];
    let green = result.tensor[channel_stride];
    let blue = result.tensor[channel_stride * 2];

    assert_close_f32(red, 0.074);
    assert_close_f32(green, (0.502 - 0.456) / 0.224);
    assert_close_f32(blue, (0.502 - 0.406) / 0.225);
}

#[test]
fn table_parse_detr_output_filters_below_threshold_queries() {
    let queries = 2;
    let logits = vec![0.0; queries * 7];
    let mut boxes = vec![0.0; queries * 4];
    for query in 0..queries {
        set_box(&mut boxes, query, [0.5, 0.5, 0.2, 0.2]);
    }

    let result = parse_tatr_detr_output(&logits, [1, queries, 7], &boxes, [1, queries, 4], 0.5);

    assert!(result.is_empty());
}

#[test]
fn table_parse_detr_output_accepts_high_confidence_row() {
    let queries = 2;
    let mut logits = vec![0.0; queries * 7];
    let mut boxes = vec![0.0; queries * 4];
    set_logit(&mut logits, 0, 2, 10.0);
    set_box(&mut boxes, 0, [0.5, 0.25, 0.8, 0.1]);
    set_logit(&mut logits, 1, 6, 10.0);
    set_box(&mut boxes, 1, [0.5, 0.5, 0.2, 0.2]);

    let result = parse_tatr_detr_output(&logits, [1, queries, 7], &boxes, [1, queries, 4], 0.5);

    assert_eq!(result.len(), 1);
    assert_eq!(result[0].class, TableElementClass::Row);
    assert!(result[0].confidence > 0.9);
    assert_close(result[0].x, 0.1);
    assert_close(result[0].y, 0.2);
    assert_close(result[0].width, 0.8);
    assert_close(result[0].height, 0.1);
}

#[test]
fn table_parse_detr_output_clamps_negative_boxes_to_zero() {
    let mut logits = vec![0.0; 7];
    let mut boxes = vec![0.0; 4];
    set_logit(&mut logits, 0, 1, 10.0);
    set_box(&mut boxes, 0, [0.05, 0.5, 0.2, 0.8]);

    let result = parse_tatr_detr_output(&logits, [1, 1, 7], &boxes, [1, 1, 4], 0.5);

    assert_eq!(result.len(), 1);
    assert_eq!(result[0].class, TableElementClass::Column);
    assert_close(result[0].x, 0.0);
    assert_close(result[0].width, 0.15);
}

#[test]
fn table_detections_translate_from_normalized_crop_to_page_space() {
    let detections = vec![detection(TableElementClass::Row, 0.9, 0.1, 0.2, 0.8, 0.1)];

    let page_space = tatr_detections_to_page_space(&detections, 20.0, 30.0, 400.0, 200.0);

    assert_eq!(page_space.len(), 1);
    assert_close(page_space[0].x, 60.0);
    assert_close(page_space[0].y, 70.0);
    assert_close(page_space[0].width, 320.0);
    assert_close(page_space[0].height, 20.0);
}

#[test]
fn table_cell_grid_produces_three_by_four_intersections() {
    let rows = vec![
        detection(TableElementClass::Row, 0.9, 0.0, 0.0, 400.0, 100.0),
        detection(TableElementClass::Row, 0.9, 0.0, 100.0, 400.0, 100.0),
        detection(TableElementClass::Row, 0.9, 0.0, 200.0, 400.0, 100.0),
    ];
    let columns = vec![
        detection(TableElementClass::Column, 0.9, 0.0, 0.0, 100.0, 300.0),
        detection(TableElementClass::Column, 0.9, 100.0, 0.0, 100.0, 300.0),
        detection(TableElementClass::Column, 0.9, 200.0, 0.0, 100.0, 300.0),
        detection(TableElementClass::Column, 0.9, 300.0, 0.0, 100.0, 300.0),
    ];

    let cells = build_table_cell_grid(&rows, &columns, 0.0, 0.0, 400.0, 300.0);

    assert_eq!(cells.len(), 12);
    assert_eq!(cells[0].row_index, 0);
    assert_eq!(cells[0].column_index, 0);
    assert_close(cells[0].x, 0.0);
    assert_close(cells[0].y, 0.0);
    assert_close(cells[0].width, 100.0);
    assert_close(cells[0].height, 100.0);
    assert_eq!(cells.last().expect("last cell").row_index, 2);
    assert_eq!(cells.last().expect("last cell").column_index, 3);
    assert_close(cells.last().expect("last cell").x, 300.0);
    assert_close(cells.last().expect("last cell").y, 200.0);
}

#[test]
fn table_cell_grid_skips_sub_minimum_cells() {
    let rows = vec![
        detection(TableElementClass::Row, 0.9, 0.0, 0.0, 200.0, 100.0),
        detection(TableElementClass::Row, 0.9, 0.0, 100.0, 200.0, 100.0),
        detection(TableElementClass::Row, 0.9, 0.0, 200.0, 200.0, 100.0),
    ];
    let columns = vec![
        detection(TableElementClass::Column, 0.9, 0.0, 0.0, 100.0, 300.0),
        detection(TableElementClass::Column, 0.9, 100.0, 0.0, 2.0, 300.0),
        detection(TableElementClass::Column, 0.9, 102.0, 0.0, 98.0, 300.0),
    ];

    let cells = build_table_cell_grid(&rows, &columns, 0.0, 0.0, 200.0, 300.0);

    assert_eq!(cells.len(), 6);
    assert!(cells.iter().all(|cell| cell.column_index != 1));
}

#[test]
fn table_deduplicate_by_iou_keeps_highest_confidence_duplicate() {
    let items = vec![
        detection(TableElementClass::Row, 0.70, 0.0, 0.0, 100.0, 20.0),
        detection(TableElementClass::Row, 0.95, 1.0, 0.0, 100.0, 20.0),
        detection(TableElementClass::Row, 0.80, 0.0, 100.0, 100.0, 20.0),
    ];

    let result = deduplicate_table_detections_by_iou(&items, 0.8);

    assert_eq!(result.len(), 2);
    assert_close_f32(result[0].confidence, 0.95);
    assert_close_f32(result[1].confidence, 0.80);
}

#[test]
fn table_structure_builder_filters_classes_and_derives_cells() {
    let detections = vec![
        detection(TableElementClass::Table, 0.99, 0.0, 0.0, 200.0, 100.0),
        detection(TableElementClass::Row, 0.9, 0.0, 0.0, 200.0, 50.0),
        detection(TableElementClass::Row, 0.8, 0.0, 50.0, 200.0, 50.0),
        detection(TableElementClass::Column, 0.9, 0.0, 0.0, 100.0, 100.0),
        detection(TableElementClass::Column, 0.8, 100.0, 0.0, 100.0, 100.0),
        detection(TableElementClass::SpanningCell, 0.7, 0.0, 0.0, 200.0, 50.0),
    ];

    let structure = build_table_structure_from_detections(&detections, 0.0, 0.0, 200.0, 100.0)
        .expect("rows and columns should form a table");

    assert_eq!(structure.rows.len(), 2);
    assert_eq!(structure.columns.len(), 2);
    assert_eq!(structure.spanning_cells.len(), 1);
    assert_eq!(structure.cells.len(), 4);
}

#[test]
fn table_iou_handles_perfect_overlap_and_no_overlap() {
    let a = detection(TableElementClass::Row, 1.0, 0.0, 0.0, 100.0, 50.0);
    let b = detection(TableElementClass::Row, 1.0, 0.0, 0.0, 100.0, 50.0);
    let c = detection(TableElementClass::Row, 1.0, 200.0, 200.0, 100.0, 50.0);

    assert_close_f32(compute_table_iou(&a, &b), 1.0);
    assert_close_f32(compute_table_iou(&a, &c), 0.0);
}

fn set_logit(logits: &mut [f32], query: usize, class: usize, value: f32) {
    logits[query * 7 + class] = value;
}

fn set_box(boxes: &mut [f32], query: usize, value: [f32; 4]) {
    let start = query * 4;
    boxes[start..start + 4].copy_from_slice(&value);
}

fn detection(
    class: TableElementClass,
    confidence: f32,
    x: f64,
    y: f64,
    width: f64,
    height: f64,
) -> TableSubDetection {
    TableSubDetection {
        class,
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
