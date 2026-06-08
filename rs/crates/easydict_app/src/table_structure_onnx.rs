use std::fmt;
use std::path::{Path, PathBuf};

use crate::table_structure::{
    build_table_structure_from_detections, parse_tatr_detr_output, preprocess_table_crop,
    tatr_detections_to_page_space, TablePreprocessResult, TableStructure,
    TATR_DEFAULT_CONFIDENCE_THRESHOLD,
};
use ort::session::Session;
use ort::value::TensorRef;

#[derive(Debug, Clone, Eq, PartialEq)]
pub enum TatrOnnxError {
    MissingModel(PathBuf),
    InvalidPageImage { width: usize, height: usize },
    MissingInput(Vec<String>),
    MissingOutput(Vec<String>),
    OutputNotReturned(String),
    InvalidLogitsShape(Vec<i64>),
    InvalidPredBoxesShape(Vec<i64>),
    Runtime(String),
    Session(String),
}

impl fmt::Display for TatrOnnxError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::MissingModel(path) => {
                write!(formatter, "TATR model not found at '{}'", path.display())
            }
            Self::InvalidPageImage { width, height } => {
                write!(formatter, "invalid TATR page image size {width}x{height}")
            }
            Self::MissingInput(inputs) => write!(
                formatter,
                "TATR ONNX model has no input; available inputs: {}",
                inputs.join(", ")
            ),
            Self::MissingOutput(outputs) => write!(
                formatter,
                "TATR ONNX model must expose 'logits' and 'pred_boxes' outputs; available outputs: {}",
                outputs.join(", ")
            ),
            Self::OutputNotReturned(output) => {
                write!(formatter, "TATR ONNX output '{output}' was not returned")
            }
            Self::InvalidLogitsShape(shape) => write!(
                formatter,
                "TATR logits output must have shape [1, N, classes + 1], got {shape:?}"
            ),
            Self::InvalidPredBoxesShape(shape) => write!(
                formatter,
                "TATR pred_boxes output must have shape [1, N, 4], got {shape:?}"
            ),
            Self::Runtime(message) | Self::Session(message) => formatter.write_str(message),
        }
    }
}

impl std::error::Error for TatrOnnxError {}

impl From<easydict_nllb::NllbError> for TatrOnnxError {
    fn from(value: easydict_nllb::NllbError) -> Self {
        Self::Runtime(value.to_string())
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TatrOutputNames {
    pub logits: String,
    pub pred_boxes: String,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TatrTableCrop {
    pub preprocess_x: i32,
    pub preprocess_y: i32,
    pub preprocess_width: usize,
    pub preprocess_height: usize,
    pub page_x: f64,
    pub page_y: f64,
    pub page_width: f64,
    pub page_height: f64,
}

pub struct TatrOnnxSession {
    session: Session,
    input_name: String,
    output_names: TatrOutputNames,
    confidence_threshold: f32,
}

impl TatrOnnxSession {
    pub fn from_model_paths(
        runtime_dir: impl AsRef<Path>,
        model_path: impl AsRef<Path>,
    ) -> Result<Self, TatrOnnxError> {
        Self::from_model_paths_with_confidence_threshold(
            runtime_dir,
            model_path,
            TATR_DEFAULT_CONFIDENCE_THRESHOLD,
        )
    }

    pub fn from_model_paths_with_confidence_threshold(
        runtime_dir: impl AsRef<Path>,
        model_path: impl AsRef<Path>,
        confidence_threshold: f32,
    ) -> Result<Self, TatrOnnxError> {
        let model_path = model_path.as_ref();
        require_tatr_model(model_path)?;
        easydict_nllb::ensure_ort_runtime_initialized(runtime_dir)?;

        let mut builder = Session::builder().map_err(map_ort_error("create TATR session"))?;
        let session = builder
            .commit_from_file(model_path)
            .map_err(map_ort_error("load TATR ONNX model"))?;
        let input_name = resolve_tatr_input_name(
            session
                .inputs()
                .iter()
                .map(|input| input.name().to_string()),
        )?;
        let output_names = resolve_tatr_output_names(
            session
                .outputs()
                .iter()
                .map(|output| output.name().to_string()),
        )?;

        Ok(Self {
            session,
            input_name,
            output_names,
            confidence_threshold,
        })
    }

    #[allow(clippy::too_many_arguments)]
    pub fn recognize_bgra(
        &mut self,
        page_bgra: &[u8],
        page_width: usize,
        page_height: usize,
        table_x: f64,
        table_y: f64,
        table_width: f64,
        table_height: f64,
    ) -> Result<Option<TableStructure>, TatrOnnxError> {
        if page_width == 0 || page_height == 0 {
            return Err(TatrOnnxError::InvalidPageImage {
                width: page_width,
                height: page_height,
            });
        }

        let Some(crop) = clamp_tatr_table_crop(
            page_width,
            page_height,
            table_x,
            table_y,
            table_width,
            table_height,
        ) else {
            return Ok(None);
        };
        let preprocess = preprocess_table_crop(
            page_bgra,
            page_width,
            page_height,
            crop.preprocess_x,
            crop.preprocess_y,
            crop.preprocess_width,
            crop.preprocess_height,
        )
        .ok_or(TatrOnnxError::InvalidPageImage {
            width: page_width,
            height: page_height,
        })?;

        self.recognize_preprocessed(&preprocess, crop)
    }

    pub fn recognize_preprocessed(
        &mut self,
        preprocess: &TablePreprocessResult,
        crop: TatrTableCrop,
    ) -> Result<Option<TableStructure>, TatrOnnxError> {
        let input = TensorRef::<f32>::from_array_view((
            [1, 3, preprocess.new_height, preprocess.new_width],
            preprocess.tensor.as_slice(),
        ))
        .map_err(map_ort_error("create TATR input tensor"))?;
        let outputs = self
            .session
            .run(ort::inputs! {
                self.input_name.as_str() => input,
            })
            .map_err(map_ort_error("run TATR session"))?;

        let logits = outputs
            .get(self.output_names.logits.as_str())
            .ok_or_else(|| TatrOnnxError::OutputNotReturned(self.output_names.logits.clone()))?;
        let (logits_shape, logits_data) = logits
            .try_extract_tensor::<f32>()
            .map_err(map_ort_error("extract TATR logits tensor"))?;

        let pred_boxes = outputs
            .get(self.output_names.pred_boxes.as_str())
            .ok_or_else(|| {
                TatrOnnxError::OutputNotReturned(self.output_names.pred_boxes.clone())
            })?;
        let (pred_boxes_shape, pred_boxes_data) = pred_boxes
            .try_extract_tensor::<f32>()
            .map_err(map_ort_error("extract TATR pred_boxes tensor"))?;

        parse_tatr_onnx_outputs(
            logits_data,
            logits_shape,
            pred_boxes_data,
            pred_boxes_shape,
            crop,
            self.confidence_threshold,
        )
    }

    pub fn input_name(&self) -> &str {
        &self.input_name
    }

    pub fn output_names(&self) -> &TatrOutputNames {
        &self.output_names
    }
}

#[allow(clippy::too_many_arguments)]
pub fn clamp_tatr_table_crop(
    page_width: usize,
    page_height: usize,
    table_x: f64,
    table_y: f64,
    table_width: f64,
    table_height: f64,
) -> Option<TatrTableCrop> {
    if page_width == 0 || page_height == 0 {
        return None;
    }

    let page_width_f = page_width as f64;
    let page_height_f = page_height as f64;
    let page_x = table_x.clamp(0.0, page_width_f - 1.0);
    let page_y = table_y.clamp(0.0, page_height_f - 1.0);
    let page_width = table_width.max(0.0).min(page_width_f - page_x);
    let page_height = table_height.max(0.0).min(page_height_f - page_y);
    if page_width < 32.0 || page_height < 32.0 {
        return None;
    }

    Some(TatrTableCrop {
        preprocess_x: round_tatr_crop_value(page_x) as i32,
        preprocess_y: round_tatr_crop_value(page_y) as i32,
        preprocess_width: round_tatr_crop_value(page_width).max(1.0) as usize,
        preprocess_height: round_tatr_crop_value(page_height).max(1.0) as usize,
        page_x,
        page_y,
        page_width,
        page_height,
    })
}

#[allow(clippy::too_many_arguments)]
pub fn parse_tatr_onnx_outputs(
    logits: &[f32],
    logits_shape: &[i64],
    pred_boxes: &[f32],
    pred_boxes_shape: &[i64],
    crop: TatrTableCrop,
    confidence_threshold: f32,
) -> Result<Option<TableStructure>, TatrOnnxError> {
    let logits_shape = normalize_tatr_logits_shape(logits_shape)?;
    let pred_boxes_shape = normalize_tatr_pred_boxes_shape(pred_boxes_shape)?;
    let detections = parse_tatr_detr_output(
        logits,
        logits_shape,
        pred_boxes,
        pred_boxes_shape,
        confidence_threshold,
    );
    if detections.is_empty() {
        return Ok(None);
    }

    let page_space = tatr_detections_to_page_space(
        &detections,
        crop.page_x,
        crop.page_y,
        crop.page_width,
        crop.page_height,
    );
    Ok(build_table_structure_from_detections(
        &page_space,
        crop.page_x,
        crop.page_y,
        crop.page_width,
        crop.page_height,
    ))
}

pub fn normalize_tatr_logits_shape(shape: &[i64]) -> Result<[usize; 3], TatrOnnxError> {
    if shape.len() != 3 || shape.iter().any(|dimension| *dimension < 0) {
        return Err(TatrOnnxError::InvalidLogitsShape(shape.to_vec()));
    }

    let normalized = [shape[0] as usize, shape[1] as usize, shape[2] as usize];
    if normalized[0] == 0 || normalized[1] == 0 || normalized[2] <= 1 {
        return Err(TatrOnnxError::InvalidLogitsShape(shape.to_vec()));
    }

    Ok(normalized)
}

pub fn normalize_tatr_pred_boxes_shape(shape: &[i64]) -> Result<[usize; 3], TatrOnnxError> {
    if shape.len() != 3 || shape.iter().any(|dimension| *dimension < 0) {
        return Err(TatrOnnxError::InvalidPredBoxesShape(shape.to_vec()));
    }

    let normalized = [shape[0] as usize, shape[1] as usize, shape[2] as usize];
    if normalized[0] == 0 || normalized[1] == 0 || normalized[2] != 4 {
        return Err(TatrOnnxError::InvalidPredBoxesShape(shape.to_vec()));
    }

    Ok(normalized)
}

pub fn resolve_tatr_input_name(
    names: impl IntoIterator<Item = String>,
) -> Result<String, TatrOnnxError> {
    let names = names.into_iter().collect::<Vec<_>>();
    if let Some(name) = names.first() {
        return Ok(name.clone());
    }

    Err(TatrOnnxError::MissingInput(names))
}

pub fn resolve_tatr_output_names(
    names: impl IntoIterator<Item = String>,
) -> Result<TatrOutputNames, TatrOnnxError> {
    let names = names.into_iter().collect::<Vec<_>>();
    let logits = names.iter().find(|name| name.as_str() == "logits").cloned();
    let pred_boxes = names
        .iter()
        .find(|name| name.as_str() == "pred_boxes")
        .cloned();

    match (logits, pred_boxes) {
        (Some(logits), Some(pred_boxes)) => Ok(TatrOutputNames { logits, pred_boxes }),
        _ => Err(TatrOnnxError::MissingOutput(names)),
    }
}

fn require_tatr_model(path: &Path) -> Result<(), TatrOnnxError> {
    if path.is_file() {
        return Ok(());
    }

    Err(TatrOnnxError::MissingModel(path.to_path_buf()))
}

fn round_tatr_crop_value(value: f64) -> f64 {
    let floor = value.floor();
    let fraction = value - floor;
    if fraction < 0.5 {
        floor
    } else if fraction > 0.5 {
        floor + 1.0
    } else if (floor as i64) % 2 == 0 {
        floor
    } else {
        floor + 1.0
    }
}

fn map_ort_error(context: impl Into<String>) -> impl FnOnce(ort::Error) -> TatrOnnxError {
    let context = context.into();
    move |error| TatrOnnxError::Session(format!("{context}: {error}"))
}
