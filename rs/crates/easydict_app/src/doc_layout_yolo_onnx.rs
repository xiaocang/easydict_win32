use std::fmt;
use std::path::{Path, PathBuf};

use crate::doc_layout_yolo::{
    parse_doc_layout_yolo_output, preprocess_doc_layout_yolo_bgra, DocLayoutYoloDetection,
    DocLayoutYoloPreprocessResult, DOC_LAYOUT_YOLO_DEFAULT_CONFIDENCE_THRESHOLD,
    DOC_LAYOUT_YOLO_INPUT_SIZE,
};
use ort::session::Session;
use ort::value::TensorRef;

#[derive(Debug, Clone, Eq, PartialEq)]
pub enum DocLayoutYoloOnnxError {
    MissingModel(PathBuf),
    InvalidImage { width: usize, height: usize },
    MissingInput(Vec<String>),
    MissingOutput(Vec<String>),
    OutputNotReturned(String),
    InvalidOutputShape(Vec<i64>),
    Runtime(String),
    Session(String),
}

impl fmt::Display for DocLayoutYoloOnnxError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::MissingModel(path) => {
                write!(
                    formatter,
                    "DocLayout-YOLO model not found at '{}'",
                    path.display()
                )
            }
            Self::InvalidImage { width, height } => {
                write!(
                    formatter,
                    "invalid DocLayout-YOLO image size {width}x{height}"
                )
            }
            Self::MissingInput(inputs) => write!(
                formatter,
                "DocLayout-YOLO ONNX model has no input; available inputs: {}",
                inputs.join(", ")
            ),
            Self::MissingOutput(outputs) => write!(
                formatter,
                "DocLayout-YOLO ONNX model has no output; available outputs: {}",
                outputs.join(", ")
            ),
            Self::OutputNotReturned(output) => {
                write!(
                    formatter,
                    "DocLayout-YOLO ONNX output '{output}' was not returned"
                )
            }
            Self::InvalidOutputShape(shape) => write!(
                formatter,
                "DocLayout-YOLO output must have shape [1, N, 6] or [1, 14, N], got {shape:?}"
            ),
            Self::Runtime(message) => formatter.write_str(message),
            Self::Session(message) => formatter.write_str(message),
        }
    }
}

impl std::error::Error for DocLayoutYoloOnnxError {}

impl From<easydict_nllb::NllbError> for DocLayoutYoloOnnxError {
    fn from(value: easydict_nllb::NllbError) -> Self {
        Self::Runtime(value.to_string())
    }
}

pub struct DocLayoutYoloOnnxSession {
    session: Session,
    input_name: String,
    output_name: String,
    confidence_threshold: f32,
}

impl DocLayoutYoloOnnxSession {
    pub fn from_model_paths(
        runtime_dir: impl AsRef<Path>,
        model_path: impl AsRef<Path>,
    ) -> Result<Self, DocLayoutYoloOnnxError> {
        Self::from_model_paths_with_confidence_threshold(
            runtime_dir,
            model_path,
            DOC_LAYOUT_YOLO_DEFAULT_CONFIDENCE_THRESHOLD,
        )
    }

    pub fn from_model_paths_with_confidence_threshold(
        runtime_dir: impl AsRef<Path>,
        model_path: impl AsRef<Path>,
        confidence_threshold: f32,
    ) -> Result<Self, DocLayoutYoloOnnxError> {
        let model_path = model_path.as_ref();
        require_doc_layout_yolo_model(model_path)?;
        easydict_nllb::ensure_ort_runtime_initialized(runtime_dir)?;

        let mut builder =
            Session::builder().map_err(map_ort_error("create DocLayout-YOLO session"))?;
        let session = builder
            .commit_from_file(model_path)
            .map_err(map_ort_error("load DocLayout-YOLO ONNX model"))?;
        let input_name = resolve_doc_layout_yolo_input_name(
            session
                .inputs()
                .iter()
                .map(|input| input.name().to_string()),
        )?;
        let output_name = resolve_doc_layout_yolo_output_name(
            session
                .outputs()
                .iter()
                .map(|output| output.name().to_string()),
        )?;

        Ok(Self {
            session,
            input_name,
            output_name,
            confidence_threshold,
        })
    }

    pub fn detect_bgra(
        &mut self,
        bgra: &[u8],
        width: usize,
        height: usize,
    ) -> Result<Vec<DocLayoutYoloDetection>, DocLayoutYoloOnnxError> {
        let preprocess = preprocess_doc_layout_yolo_bgra(bgra, width, height)
            .ok_or(DocLayoutYoloOnnxError::InvalidImage { width, height })?;
        self.detect_preprocessed(&preprocess, width, height)
    }

    pub fn detect_preprocessed(
        &mut self,
        preprocess: &DocLayoutYoloPreprocessResult,
        original_width: usize,
        original_height: usize,
    ) -> Result<Vec<DocLayoutYoloDetection>, DocLayoutYoloOnnxError> {
        let input = TensorRef::<f32>::from_array_view((
            [1, 3, DOC_LAYOUT_YOLO_INPUT_SIZE, DOC_LAYOUT_YOLO_INPUT_SIZE],
            preprocess.tensor.as_slice(),
        ))
        .map_err(map_ort_error("create DocLayout-YOLO input tensor"))?;
        let outputs = self
            .session
            .run(ort::inputs! {
                self.input_name.as_str() => input,
            })
            .map_err(map_ort_error("run DocLayout-YOLO session"))?;
        let output = outputs
            .get(self.output_name.as_str())
            .ok_or_else(|| DocLayoutYoloOnnxError::OutputNotReturned(self.output_name.clone()))?;
        let (shape, data) = output
            .try_extract_tensor::<f32>()
            .map_err(map_ort_error("extract DocLayout-YOLO output tensor"))?;

        parse_doc_layout_yolo_onnx_output(
            data,
            shape,
            preprocess,
            original_width,
            original_height,
            self.confidence_threshold,
        )
    }

    pub fn input_name(&self) -> &str {
        &self.input_name
    }

    pub fn output_name(&self) -> &str {
        &self.output_name
    }
}

pub fn parse_doc_layout_yolo_onnx_output(
    output: &[f32],
    shape: &[i64],
    preprocess: &DocLayoutYoloPreprocessResult,
    original_width: usize,
    original_height: usize,
    confidence_threshold: f32,
) -> Result<Vec<DocLayoutYoloDetection>, DocLayoutYoloOnnxError> {
    let shape = normalize_doc_layout_yolo_output_shape(shape)?;
    Ok(parse_doc_layout_yolo_output(
        output,
        shape,
        preprocess.scale_x,
        preprocess.scale_y,
        preprocess.pad_x,
        preprocess.pad_y,
        original_width,
        original_height,
        confidence_threshold,
    ))
}

pub fn normalize_doc_layout_yolo_output_shape(
    shape: &[i64],
) -> Result<[usize; 3], DocLayoutYoloOnnxError> {
    if shape.len() != 3 || shape.iter().any(|dimension| *dimension < 0) {
        return Err(DocLayoutYoloOnnxError::InvalidOutputShape(shape.to_vec()));
    }

    let [batches, rows_or_features, columns_or_anchors] =
        [shape[0] as usize, shape[1] as usize, shape[2] as usize];
    let is_end_to_end =
        batches == 1 && columns_or_anchors == 6 && rows_or_features >= 1 && rows_or_features != 14;
    let is_raw_yolo =
        batches == 1 && rows_or_features >= 5 && columns_or_anchors >= 1 && rows_or_features != 6;
    if !is_end_to_end && !is_raw_yolo {
        return Err(DocLayoutYoloOnnxError::InvalidOutputShape(shape.to_vec()));
    }

    Ok([batches, rows_or_features, columns_or_anchors])
}

pub fn resolve_doc_layout_yolo_input_name(
    names: impl IntoIterator<Item = String>,
) -> Result<String, DocLayoutYoloOnnxError> {
    let names = names.into_iter().collect::<Vec<_>>();
    if let Some(name) = names
        .iter()
        .find(|name| name.eq_ignore_ascii_case("images"))
        .or_else(|| names.first())
    {
        return Ok(name.clone());
    }

    Err(DocLayoutYoloOnnxError::MissingInput(names))
}

pub fn resolve_doc_layout_yolo_output_name(
    names: impl IntoIterator<Item = String>,
) -> Result<String, DocLayoutYoloOnnxError> {
    let names = names.into_iter().collect::<Vec<_>>();
    if let Some(name) = names.first() {
        return Ok(name.clone());
    }

    Err(DocLayoutYoloOnnxError::MissingOutput(names))
}

fn require_doc_layout_yolo_model(path: &Path) -> Result<(), DocLayoutYoloOnnxError> {
    if path.is_file() {
        return Ok(());
    }

    Err(DocLayoutYoloOnnxError::MissingModel(path.to_path_buf()))
}

fn map_ort_error(context: impl Into<String>) -> impl FnOnce(ort::Error) -> DocLayoutYoloOnnxError {
    let context = context.into();
    move |error| DocLayoutYoloOnnxError::Session(format!("{context}: {error}"))
}
