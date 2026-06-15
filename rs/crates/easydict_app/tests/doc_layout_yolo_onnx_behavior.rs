use std::fs;
use std::path::PathBuf;
use std::time::{SystemTime, UNIX_EPOCH};

use easydict_app::{
    normalize_doc_layout_yolo_output_shape, parse_doc_layout_yolo_onnx_output,
    resolve_doc_layout_yolo_input_name, resolve_doc_layout_yolo_output_name, DocLayoutRegionType,
    DocLayoutYoloOnnxError, DocLayoutYoloOnnxSession, DocLayoutYoloPreprocessResult,
};

const DOC_LAYOUT_ONNX_RUNTIME_DIR_ENV: &str = "EASYDICT_DOC_LAYOUT_YOLO_ONNX_SMOKE_RUNTIME_DIR";
const DOC_LAYOUT_ONNX_MODEL_ENV: &str = "EASYDICT_DOC_LAYOUT_YOLO_ONNX_SMOKE_MODEL";

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

#[test]
fn doc_layout_yolo_onnx_real_provider_smoke_when_env_configured() {
    let Some((runtime_dir, model_path)) =
        smoke_paths(DOC_LAYOUT_ONNX_RUNTIME_DIR_ENV, DOC_LAYOUT_ONNX_MODEL_ENV)
    else {
        eprintln!(
            "skipping real DocLayout-YOLO ONNX smoke; set {DOC_LAYOUT_ONNX_RUNTIME_DIR_ENV} and {DOC_LAYOUT_ONNX_MODEL_ENV}"
        );
        return;
    };

    let mut session = DocLayoutYoloOnnxSession::from_model_paths(runtime_dir, model_path)
        .expect("real DocLayout-YOLO session should load");
    let width = 320usize;
    let height = 240usize;
    let bgra = synthetic_document_bgra(width, height);

    let detections = session
        .detect_bgra(&bgra, width, height)
        .expect("real DocLayout-YOLO session should run inference");

    for detection in detections {
        assert!(detection.confidence.is_finite());
        assert!(detection.x.is_finite());
        assert!(detection.y.is_finite());
        assert!(detection.width.is_finite());
        assert!(detection.height.is_finite());
        assert!(detection.x >= 0.0);
        assert!(detection.y >= 0.0);
        assert!(detection.width >= 0.0);
        assert!(detection.height >= 0.0);
        assert!(detection.x + detection.width <= width as f64 + 1.0);
        assert!(detection.y + detection.height <= height as f64 + 1.0);
    }
}

fn smoke_paths(runtime_env: &str, model_env: &str) -> Option<(PathBuf, PathBuf)> {
    let runtime_dir = std::env::var_os(runtime_env).map(PathBuf::from);
    let model_path = std::env::var_os(model_env).map(PathBuf::from);
    if runtime_dir.is_none() && model_path.is_none() {
        return None;
    }

    let runtime_dir = runtime_dir.unwrap_or_else(|| {
        panic!("{runtime_env} must be set when {model_env} is set for real ONNX smoke")
    });
    let model_path = model_path.unwrap_or_else(|| {
        panic!("{model_env} must be set when {runtime_env} is set for real ONNX smoke")
    });
    assert!(
        runtime_dir.is_dir(),
        "{runtime_env} must point to an ONNX Runtime directory: {}",
        runtime_dir.display()
    );
    assert!(
        model_path.is_file(),
        "{model_env} must point to a DocLayout-YOLO ONNX model: {}",
        model_path.display()
    );
    Some((runtime_dir, model_path))
}

fn synthetic_document_bgra(width: usize, height: usize) -> Vec<u8> {
    let mut bgra = vec![255u8; width * height * 4];
    fill_rect(&mut bgra, width, 24, 24, width.saturating_sub(48), 24);
    fill_rect(&mut bgra, width, 36, 76, width.saturating_sub(72), 18);
    fill_rect(&mut bgra, width, 36, 112, width.saturating_sub(72), 18);
    fill_rect(&mut bgra, width, 72, 160, width.saturating_sub(144), 44);
    bgra
}

fn fill_rect(bgra: &mut [u8], image_width: usize, x: usize, y: usize, width: usize, height: usize) {
    let image_height = bgra.len() / image_width / 4;
    for row in y..(y + height).min(image_height) {
        for column in x..(x + width).min(image_width) {
            let offset = (row * image_width + column) * 4;
            bgra[offset..offset + 4].copy_from_slice(&[0, 0, 0, 255]);
        }
    }
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
