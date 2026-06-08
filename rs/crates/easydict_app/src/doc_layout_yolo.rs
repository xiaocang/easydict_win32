use crate::table_structure::compute_rect_iou;
use std::cmp::Ordering;

pub const DOC_LAYOUT_YOLO_INPUT_SIZE: usize = 1024;
pub const DOC_LAYOUT_YOLO_NUM_CLASSES: usize = 10;
pub const DOC_LAYOUT_YOLO_DEFAULT_CONFIDENCE_THRESHOLD: f32 = 0.25;
pub const DOC_LAYOUT_YOLO_NMS_IOU_THRESHOLD: f32 = 0.45;
pub const DOC_LAYOUT_YOLO_PADDING_VALUE: f32 = 114.0 / 255.0;
pub const DOC_LAYOUT_YOLO_CLASS_NAMES: [&str; DOC_LAYOUT_YOLO_NUM_CLASSES] = [
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
];

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DocLayoutRegionType {
    Unknown,
    Header,
    Footer,
    Body,
    LeftColumn,
    RightColumn,
    TableLike,
    Figure,
    Table,
    Formula,
    Caption,
    Title,
    IsolatedFormula,
}

#[derive(Clone, Debug, PartialEq)]
pub struct DocLayoutYoloDetection {
    pub region_type: DocLayoutRegionType,
    pub confidence: f32,
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

#[derive(Clone, Debug, PartialEq)]
pub struct DocLayoutYoloPreprocessResult {
    pub tensor: Vec<f32>,
    pub scale_x: f64,
    pub scale_y: f64,
    pub pad_x: usize,
    pub pad_y: usize,
    pub new_width: usize,
    pub new_height: usize,
}

impl DocLayoutYoloPreprocessResult {
    pub fn tensor_shape(&self) -> [usize; 4] {
        [1, 3, DOC_LAYOUT_YOLO_INPUT_SIZE, DOC_LAYOUT_YOLO_INPUT_SIZE]
    }
}

pub fn doc_layout_yolo_class_to_region(class_index: i32) -> DocLayoutRegionType {
    match class_index {
        0 => DocLayoutRegionType::Title,
        1 => DocLayoutRegionType::Body,
        2 | 3 => DocLayoutRegionType::Figure,
        4 | 6 | 7 | 9 => DocLayoutRegionType::Caption,
        5 => DocLayoutRegionType::Table,
        8 => DocLayoutRegionType::IsolatedFormula,
        _ => DocLayoutRegionType::Unknown,
    }
}

pub fn preprocess_doc_layout_yolo_bgra(
    bgra: &[u8],
    width: usize,
    height: usize,
) -> Option<DocLayoutYoloPreprocessResult> {
    if width == 0 || height == 0 {
        return None;
    }

    let scale = (DOC_LAYOUT_YOLO_INPUT_SIZE as f64 / width as f64)
        .min(DOC_LAYOUT_YOLO_INPUT_SIZE as f64 / height as f64);
    let new_width = round_dotnet(width as f64 * scale).max(1.0) as usize;
    let new_height = round_dotnet(height as f64 * scale).max(1.0) as usize;
    let pad_x = (DOC_LAYOUT_YOLO_INPUT_SIZE - new_width) / 2;
    let pad_y = (DOC_LAYOUT_YOLO_INPUT_SIZE - new_height) / 2;
    let channel_stride = DOC_LAYOUT_YOLO_INPUT_SIZE.checked_mul(DOC_LAYOUT_YOLO_INPUT_SIZE)?;
    let tensor_len = channel_stride.checked_mul(3)?;
    let mut tensor = vec![DOC_LAYOUT_YOLO_PADDING_VALUE; tensor_len];

    fill_doc_layout_yolo_tensor(
        &mut tensor,
        bgra,
        width,
        height,
        scale,
        new_width,
        new_height,
        pad_x,
        pad_y,
    );

    Some(DocLayoutYoloPreprocessResult {
        tensor,
        scale_x: scale,
        scale_y: scale,
        pad_x,
        pad_y,
        new_width,
        new_height,
    })
}

#[allow(clippy::too_many_arguments)]
pub fn parse_doc_layout_yolo_output(
    output: &[f32],
    shape: [usize; 3],
    scale_x: f64,
    scale_y: f64,
    pad_x: usize,
    pad_y: usize,
    original_width: usize,
    original_height: usize,
    confidence_threshold: f32,
) -> Vec<DocLayoutYoloDetection> {
    let [batches, rows_or_features, columns_or_anchors] = shape;
    if batches == 0 || rows_or_features == 0 || columns_or_anchors == 0 {
        return Vec::new();
    }

    let Some(required_len) = rows_or_features.checked_mul(columns_or_anchors) else {
        return Vec::new();
    };
    if output.len() < required_len || scale_x <= 0.0 || scale_y <= 0.0 {
        return Vec::new();
    }

    let is_end_to_end = columns_or_anchors == 6 && rows_or_features >= 1 && rows_or_features != 14;
    if is_end_to_end {
        return parse_end_to_end_output(
            output,
            rows_or_features,
            scale_x,
            scale_y,
            pad_x,
            pad_y,
            original_width,
            original_height,
            confidence_threshold,
        );
    }

    parse_raw_yolo_output(
        output,
        rows_or_features,
        columns_or_anchors,
        scale_x,
        scale_y,
        pad_x,
        pad_y,
        original_width,
        original_height,
        confidence_threshold,
    )
}

pub fn apply_doc_layout_yolo_nms(
    detections: &[DocLayoutYoloDetection],
    iou_threshold: f32,
) -> Vec<DocLayoutYoloDetection> {
    let mut sorted = detections.to_vec();
    sorted.sort_by(|left, right| {
        right
            .confidence
            .partial_cmp(&left.confidence)
            .unwrap_or(Ordering::Equal)
    });

    let mut keep = vec![true; sorted.len()];
    for i in 0..sorted.len() {
        if !keep[i] {
            continue;
        }

        for j in (i + 1)..sorted.len() {
            if keep[j]
                && sorted[i].region_type == sorted[j].region_type
                && compute_doc_layout_yolo_iou(&sorted[i], &sorted[j]) > iou_threshold
            {
                keep[j] = false;
            }
        }
    }

    sorted
        .into_iter()
        .zip(keep)
        .filter_map(|(detection, keep)| keep.then_some(detection))
        .collect()
}

pub fn compute_doc_layout_yolo_iou(a: &DocLayoutYoloDetection, b: &DocLayoutYoloDetection) -> f32 {
    compute_rect_iou(a.x, a.y, a.width, a.height, b.x, b.y, b.width, b.height)
}

#[allow(clippy::too_many_arguments)]
fn parse_end_to_end_output(
    output: &[f32],
    num_detections: usize,
    scale_x: f64,
    scale_y: f64,
    pad_x: usize,
    pad_y: usize,
    original_width: usize,
    original_height: usize,
    confidence_threshold: f32,
) -> Vec<DocLayoutYoloDetection> {
    let mut detections = Vec::new();
    for index in 0..num_detections {
        let base = index * 6;
        let confidence = output[base + 4];
        if confidence < confidence_threshold {
            continue;
        }

        let x1_raw = f64::from(output[base]);
        let y1_raw = f64::from(output[base + 1]);
        let x2_raw = f64::from(output[base + 2]);
        let y2_raw = f64::from(output[base + 3]);
        let class_index = f64::from(output[base + 5]).round() as i32;

        let x1 = (x1_raw - pad_x as f64) / scale_x;
        let y1 = (y1_raw - pad_y as f64) / scale_y;
        let x2 = (x2_raw - pad_x as f64) / scale_x;
        let y2 = (y2_raw - pad_y as f64) / scale_y;
        let width = x2 - x1;
        let height = y2 - y1;

        push_clipped_detection(
            &mut detections,
            doc_layout_yolo_class_to_region(class_index),
            confidence,
            x1,
            y1,
            width,
            height,
            original_width,
            original_height,
        );
    }

    apply_doc_layout_yolo_nms(&detections, DOC_LAYOUT_YOLO_NMS_IOU_THRESHOLD)
}

#[allow(clippy::too_many_arguments)]
fn parse_raw_yolo_output(
    output: &[f32],
    num_features: usize,
    num_detections: usize,
    scale_x: f64,
    scale_y: f64,
    pad_x: usize,
    pad_y: usize,
    original_width: usize,
    original_height: usize,
    confidence_threshold: f32,
) -> Vec<DocLayoutYoloDetection> {
    if num_features <= 4 {
        return Vec::new();
    }

    let num_classes = num_features - 4;
    let mut detections = Vec::new();
    for index in 0..num_detections {
        let mut best_class_index = None;
        let mut best_class_score = f32::MIN;
        for class_index in 0..num_classes {
            let score = output[(4 + class_index) * num_detections + index];
            if score > best_class_score {
                best_class_score = score;
                best_class_index = Some(class_index);
            }
        }

        if best_class_score < confidence_threshold {
            continue;
        }

        let Some(best_class_index) = best_class_index else {
            continue;
        };
        let cx = f64::from(output[index]);
        let cy = f64::from(output[num_detections + index]);
        let width_model = f64::from(output[2 * num_detections + index]);
        let height_model = f64::from(output[3 * num_detections + index]);
        let x = (cx - width_model / 2.0 - pad_x as f64) / scale_x;
        let y = (cy - height_model / 2.0 - pad_y as f64) / scale_y;
        let width = width_model / scale_x;
        let height = height_model / scale_y;

        push_clipped_detection(
            &mut detections,
            doc_layout_yolo_class_to_region(best_class_index as i32),
            best_class_score,
            x,
            y,
            width,
            height,
            original_width,
            original_height,
        );
    }

    apply_doc_layout_yolo_nms(&detections, DOC_LAYOUT_YOLO_NMS_IOU_THRESHOLD)
}

#[allow(clippy::too_many_arguments)]
fn push_clipped_detection(
    detections: &mut Vec<DocLayoutYoloDetection>,
    region_type: DocLayoutRegionType,
    confidence: f32,
    x: f64,
    y: f64,
    width: f64,
    height: f64,
    original_width: usize,
    original_height: usize,
) {
    let mut clipped_x = x.max(0.0).min(original_width as f64);
    let mut clipped_y = y.max(0.0).min(original_height as f64);
    let mut clipped_width = width.min(original_width as f64 - clipped_x);
    let mut clipped_height = height.min(original_height as f64 - clipped_y);

    if !clipped_x.is_finite() || !clipped_y.is_finite() {
        return;
    }
    if !clipped_width.is_finite() || !clipped_height.is_finite() {
        return;
    }

    clipped_x = clipped_x.max(0.0);
    clipped_y = clipped_y.max(0.0);
    clipped_width = clipped_width.max(0.0);
    clipped_height = clipped_height.max(0.0);
    if clipped_width <= 0.0 || clipped_height <= 0.0 {
        return;
    }

    detections.push(DocLayoutYoloDetection {
        region_type,
        confidence,
        x: clipped_x,
        y: clipped_y,
        width: clipped_width,
        height: clipped_height,
    });
}

#[allow(clippy::too_many_arguments)]
fn fill_doc_layout_yolo_tensor(
    tensor: &mut [f32],
    bgra: &[u8],
    width: usize,
    height: usize,
    scale: f64,
    new_width: usize,
    new_height: usize,
    pad_x: usize,
    pad_y: usize,
) {
    let channel_stride = DOC_LAYOUT_YOLO_INPUT_SIZE * DOC_LAYOUT_YOLO_INPUT_SIZE;
    let red_base = 0;
    let green_base = channel_stride;
    let blue_base = 2 * channel_stride;
    let inverse_scale = 1.0 / scale;

    for y in 0..new_height {
        let source_y = ((y as f64 * inverse_scale).min((height - 1) as f64)) as usize;
        let destination_row = (y + pad_y) * DOC_LAYOUT_YOLO_INPUT_SIZE + pad_x;

        for x in 0..new_width {
            let source_x = ((x as f64 * inverse_scale).min((width - 1) as f64)) as usize;
            let source_index = source_y
                .checked_mul(width)
                .and_then(|offset| offset.checked_add(source_x))
                .and_then(|offset| offset.checked_mul(4));
            let Some(source_index) = source_index else {
                continue;
            };
            if source_index + 2 >= bgra.len() {
                continue;
            }

            let destination = destination_row + x;
            tensor[red_base + destination] = bgra[source_index + 2] as f32 / 255.0;
            tensor[green_base + destination] = bgra[source_index + 1] as f32 / 255.0;
            tensor[blue_base + destination] = bgra[source_index] as f32 / 255.0;
        }
    }
}

fn round_dotnet(value: f64) -> f64 {
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
