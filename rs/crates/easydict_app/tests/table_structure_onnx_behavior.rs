use std::fs;
use std::path::PathBuf;
use std::time::{SystemTime, UNIX_EPOCH};

use easydict_app::{
    clamp_tatr_table_crop, normalize_tatr_logits_shape, normalize_tatr_pred_boxes_shape,
    parse_tatr_onnx_outputs, resolve_tatr_input_name, resolve_tatr_output_names, TatrOnnxError,
    TatrOnnxSession,
};

#[test]
fn tatr_onnx_uses_first_input_name_like_dotnet_service() {
    let name = resolve_tatr_input_name(vec![
        "images".to_string(),
        "pixel_values".to_string(),
        "input".to_string(),
    ])
    .expect("first input should resolve");

    assert_eq!(name, "images");
}

#[test]
fn tatr_onnx_requires_logits_and_pred_boxes_outputs() {
    let outputs = resolve_tatr_output_names(vec![
        "pred_boxes".to_string(),
        "scores".to_string(),
        "logits".to_string(),
    ])
    .expect("required TATR outputs should resolve");

    assert_eq!(outputs.logits, "logits");
    assert_eq!(outputs.pred_boxes, "pred_boxes");
    assert!(matches!(
        resolve_tatr_output_names(vec!["output0".to_string(), "pred_boxes".to_string()]),
        Err(TatrOnnxError::MissingOutput(_))
    ));
}

#[test]
fn tatr_onnx_normalizes_supported_output_shapes() {
    assert_eq!(
        normalize_tatr_logits_shape(&[1, 125, 7]).expect("logits shape"),
        [1, 125, 7]
    );
    assert_eq!(
        normalize_tatr_logits_shape(&[1, 32, 9]).expect("variable query/class shape"),
        [1, 32, 9]
    );
    assert_eq!(
        normalize_tatr_pred_boxes_shape(&[1, 125, 4]).expect("pred_boxes shape"),
        [1, 125, 4]
    );
}

#[test]
fn tatr_onnx_rejects_invalid_output_shapes() {
    assert!(matches!(
        normalize_tatr_logits_shape(&[1, 125]),
        Err(TatrOnnxError::InvalidLogitsShape(_))
    ));
    assert!(matches!(
        normalize_tatr_logits_shape(&[1, 125, 1]),
        Err(TatrOnnxError::InvalidLogitsShape(_))
    ));
    assert!(matches!(
        normalize_tatr_pred_boxes_shape(&[1, 125, 5]),
        Err(TatrOnnxError::InvalidPredBoxesShape(_))
    ));
    assert!(matches!(
        normalize_tatr_pred_boxes_shape(&[1, -1, 4]),
        Err(TatrOnnxError::InvalidPredBoxesShape(_))
    ));
}

#[test]
fn tatr_onnx_clamps_table_crop_and_skips_tiny_regions() {
    let crop =
        clamp_tatr_table_crop(640, 480, -10.0, 12.5, 700.0, 80.5).expect("table crop should clamp");

    assert_eq!(crop.preprocess_x, 0);
    assert_eq!(crop.preprocess_y, 12);
    assert_eq!(crop.preprocess_width, 640);
    assert_eq!(crop.preprocess_height, 80);
    assert_eq!(crop.page_x, 0.0);
    assert_eq!(crop.page_y, 12.5);
    assert_eq!(crop.page_width, 640.0);
    assert_eq!(crop.page_height, 80.5);
    assert!(clamp_tatr_table_crop(640, 480, 10.0, 10.0, 31.9, 100.0).is_none());
}

#[test]
fn tatr_onnx_parse_outputs_delegates_to_table_structure_builder() {
    let queries = 4;
    let mut logits = vec![0.0; queries * 7];
    let mut boxes = vec![0.0; queries * 4];
    set_logit(&mut logits, 0, 2, 10.0);
    set_box(&mut boxes, 0, [0.5, 0.25, 1.0, 0.5]);
    set_logit(&mut logits, 1, 2, 9.0);
    set_box(&mut boxes, 1, [0.5, 0.75, 1.0, 0.5]);
    set_logit(&mut logits, 2, 1, 10.0);
    set_box(&mut boxes, 2, [0.25, 0.5, 0.5, 1.0]);
    set_logit(&mut logits, 3, 1, 9.0);
    set_box(&mut boxes, 3, [0.75, 0.5, 0.5, 1.0]);
    let crop = clamp_tatr_table_crop(400, 300, 10.0, 20.0, 200.0, 100.0).expect("valid table crop");

    let structure = parse_tatr_onnx_outputs(&logits, &[1, 4, 7], &boxes, &[1, 4, 4], crop, 0.5)
        .expect("outputs should parse")
        .expect("rows and columns should form a table structure");

    assert_eq!(structure.rows.len(), 2);
    assert_eq!(structure.columns.len(), 2);
    assert_eq!(structure.cells.len(), 4);
    assert_eq!(structure.cells[0].row_index, 0);
    assert_eq!(structure.cells[0].column_index, 0);
    assert_close(structure.cells[0].x, 10.0);
    assert_close(structure.cells[0].y, 20.0);
    assert_close(structure.cells[0].width, 100.0);
    assert_close(structure.cells[0].height, 50.0);
}

#[test]
fn tatr_onnx_reports_missing_model_before_runtime_initialization() {
    let temp_dir = create_temp_dir("missing_model");
    let missing_model = temp_dir.join("missing.onnx");

    let error = match TatrOnnxSession::from_model_paths(&temp_dir, &missing_model) {
        Ok(_) => panic!("missing model should fail"),
        Err(error) => error,
    };

    assert!(matches!(error, TatrOnnxError::MissingModel(_)));
    let _ = fs::remove_dir_all(temp_dir);
}

#[test]
fn tatr_onnx_reports_missing_runtime_dll() {
    let temp_dir = create_temp_dir("missing_runtime");
    let model_path = temp_dir.join("tatr_structure.onnx");
    fs::write(&model_path, b"fake onnx model").expect("write fake model");

    let error = match TatrOnnxSession::from_model_paths(&temp_dir, &model_path) {
        Ok(_) => panic!("missing runtime should fail"),
        Err(error) => error,
    };

    match error {
        TatrOnnxError::Runtime(message) => {
            assert!(message.contains("ONNX Runtime DLL not found"));
        }
        other => panic!("expected runtime error, got {other:?}"),
    }

    let _ = fs::remove_dir_all(temp_dir);
}

fn set_logit(logits: &mut [f32], query: usize, class: usize, value: f32) {
    logits[query * 7 + class] = value;
}

fn set_box(boxes: &mut [f32], query: usize, value: [f32; 4]) {
    let start = query * 4;
    boxes[start..start + 4].copy_from_slice(&value);
}

fn assert_close(actual: f64, expected: f64) {
    assert!(
        (actual - expected).abs() < 1e-6,
        "expected {actual} to be close to {expected}"
    );
}

fn create_temp_dir(label: &str) -> PathBuf {
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("system clock should be after unix epoch")
        .as_nanos();
    let path = std::env::temp_dir().join(format!(
        "easydict_tatr_onnx_{label}_{}_{}",
        std::process::id(),
        now
    ));
    fs::create_dir_all(&path).expect("create temp dir");
    path
}
