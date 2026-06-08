use std::fs;
use std::path::PathBuf;
use std::time::{SystemTime, UNIX_EPOCH};

use easydict_app::{
    normalize_doc_layout_yolo_output_shape, parse_doc_layout_yolo_onnx_output,
    resolve_doc_layout_yolo_input_name, resolve_doc_layout_yolo_output_name, DocLayoutRegionType,
    DocLayoutYoloOnnxError, DocLayoutYoloOnnxSession, DocLayoutYoloPreprocessResult,
};

#[test]
fn doc_layout_yolo_onnx_prefers_images_input_name() {
    let name = resolve_doc_layout_yolo_input_name(vec![
        "input".to_string(),
        "images".to_string(),
        "pixel_values".to_string(),
    ])
    .expect("images input should resolve");

    assert_eq!(name, "images");
}

#[test]
fn doc_layout_yolo_onnx_falls_back_to_first_input_name() {
    let name = resolve_doc_layout_yolo_input_name(vec!["pixel_values".to_string()])
        .expect("first input should resolve");

    assert_eq!(name, "pixel_values");
}

#[test]
fn doc_layout_yolo_onnx_uses_first_output_name() {
    let name =
        resolve_doc_layout_yolo_output_name(vec!["output0".to_string(), "output1".to_string()])
            .expect("first output should resolve");

    assert_eq!(name, "output0");
}

#[test]
fn doc_layout_yolo_onnx_normalizes_supported_output_shapes() {
    assert_eq!(
        normalize_doc_layout_yolo_output_shape(&[1, 300, 6]).expect("e2e shape"),
        [1, 300, 6]
    );
    assert_eq!(
        normalize_doc_layout_yolo_output_shape(&[1, 14, 8400]).expect("raw yolo shape"),
        [1, 14, 8400]
    );
}

#[test]
fn doc_layout_yolo_onnx_rejects_invalid_output_shapes() {
    assert!(matches!(
        normalize_doc_layout_yolo_output_shape(&[1, 300]),
        Err(DocLayoutYoloOnnxError::InvalidOutputShape(_))
    ));
    assert!(matches!(
        normalize_doc_layout_yolo_output_shape(&[1, 6, 1]),
        Err(DocLayoutYoloOnnxError::InvalidOutputShape(_))
    ));
    assert!(matches!(
        normalize_doc_layout_yolo_output_shape(&[1, -1, 6]),
        Err(DocLayoutYoloOnnxError::InvalidOutputShape(_))
    ));
}

#[test]
fn doc_layout_yolo_onnx_parse_output_delegates_to_layout_parser() {
    let preprocess = DocLayoutYoloPreprocessResult {
        tensor: Vec::new(),
        scale_x: 2.0,
        scale_y: 2.0,
        pad_x: 10,
        pad_y: 20,
        new_width: 200,
        new_height: 200,
    };
    let output = vec![10.0, 20.0, 110.0, 220.0, 0.9, 5.0];

    let result =
        parse_doc_layout_yolo_onnx_output(&output, &[1, 1, 6], &preprocess, 100, 100, 0.25)
            .expect("valid output should parse");

    assert_eq!(result.len(), 1);
    assert_eq!(result[0].region_type, DocLayoutRegionType::Table);
    assert_eq!(result[0].x, 0.0);
    assert_eq!(result[0].y, 0.0);
    assert_eq!(result[0].width, 50.0);
    assert_eq!(result[0].height, 100.0);
}

#[test]
fn doc_layout_yolo_onnx_reports_missing_model_before_runtime_initialization() {
    let temp_dir = create_temp_dir("missing_model");
    let missing_model = temp_dir.join("missing.onnx");

    let error = match DocLayoutYoloOnnxSession::from_model_paths(&temp_dir, &missing_model) {
        Ok(_) => panic!("missing model should fail"),
        Err(error) => error,
    };

    assert!(matches!(error, DocLayoutYoloOnnxError::MissingModel(_)));
    let _ = fs::remove_dir_all(temp_dir);
}

#[test]
fn doc_layout_yolo_onnx_reports_missing_runtime_dll() {
    let temp_dir = create_temp_dir("missing_runtime");
    let model_path = temp_dir.join("doclayout_yolo.onnx");
    fs::write(&model_path, b"fake onnx model").expect("write fake model");

    let error = match DocLayoutYoloOnnxSession::from_model_paths(&temp_dir, &model_path) {
        Ok(_) => panic!("missing runtime should fail"),
        Err(error) => error,
    };

    match error {
        DocLayoutYoloOnnxError::Runtime(message) => {
            assert!(message.contains("ONNX Runtime DLL not found"));
        }
        other => panic!("expected runtime error, got {other:?}"),
    }

    let _ = fs::remove_dir_all(temp_dir);
}

fn create_temp_dir(label: &str) -> PathBuf {
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("system clock should be after unix epoch")
        .as_nanos();
    let path = std::env::temp_dir().join(format!(
        "easydict_doc_layout_yolo_onnx_{label}_{}_{}",
        std::process::id(),
        now
    ));
    fs::create_dir_all(&path).expect("create temp dir");
    path
}
