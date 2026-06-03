use std::cmp::Ordering;

pub const TATR_SHORTEST_EDGE: usize = 800;
pub const TATR_LONGEST_EDGE: usize = 1000;
pub const TATR_NUM_QUERIES: usize = 125;
pub const TATR_NUM_CLASSES: usize = 6;
pub const TATR_NO_OBJECT_CLASS_INDEX: usize = 6;
pub const TATR_DEFAULT_CONFIDENCE_THRESHOLD: f32 = 0.3;
pub const TATR_DUPLICATE_IOU_THRESHOLD: f32 = 0.8;
pub const TATR_MIN_CELL_SIDE_PX: f64 = 4.0;
pub const TATR_MAX_CELLS_PER_TABLE: usize = 400;
pub const TATR_IMAGE_MEAN: [f32; 3] = [0.485, 0.456, 0.406];
pub const TATR_IMAGE_STD: [f32; 3] = [0.229, 0.224, 0.225];

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TableElementClass {
    Table,
    Column,
    Row,
    ColumnHeader,
    ProjectedRowHeader,
    SpanningCell,
}

#[derive(Clone, Debug, PartialEq)]
pub struct TableSubDetection {
    pub class: TableElementClass,
    pub confidence: f32,
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

#[derive(Clone, Debug, PartialEq)]
pub struct TableCellBounds {
    pub row_index: usize,
    pub column_index: usize,
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

#[derive(Clone, Debug, PartialEq)]
pub struct TableStructure {
    pub rows: Vec<TableSubDetection>,
    pub columns: Vec<TableSubDetection>,
    pub spanning_cells: Vec<TableSubDetection>,
    pub cells: Vec<TableCellBounds>,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TableCropResize {
    pub scale: f64,
    pub new_width: usize,
    pub new_height: usize,
}

#[derive(Clone, Debug, PartialEq)]
pub struct TablePreprocessResult {
    pub tensor: Vec<f32>,
    pub new_width: usize,
    pub new_height: usize,
}

impl TablePreprocessResult {
    pub fn tensor_shape(&self) -> [usize; 4] {
        [1, 3, self.new_height, self.new_width]
    }
}

pub fn table_element_class_from_index(index: usize) -> Option<TableElementClass> {
    match index {
        0 => Some(TableElementClass::Table),
        1 => Some(TableElementClass::Column),
        2 => Some(TableElementClass::Row),
        3 => Some(TableElementClass::ColumnHeader),
        4 => Some(TableElementClass::ProjectedRowHeader),
        5 => Some(TableElementClass::SpanningCell),
        _ => None,
    }
}

pub fn calculate_tatr_crop_resize(
    crop_width: usize,
    crop_height: usize,
) -> Option<TableCropResize> {
    if crop_width == 0 || crop_height == 0 {
        return None;
    }

    let short_side = crop_width.min(crop_height) as f64;
    let long_side = crop_width.max(crop_height) as f64;
    let mut scale = TATR_SHORTEST_EDGE as f64 / short_side;
    if long_side * scale > TATR_LONGEST_EDGE as f64 {
        scale = TATR_LONGEST_EDGE as f64 / long_side;
    }

    let new_width = round_dotnet(crop_width as f64 * scale).max(1.0) as usize;
    let new_height = round_dotnet(crop_height as f64 * scale).max(1.0) as usize;

    Some(TableCropResize {
        scale,
        new_width,
        new_height,
    })
}

pub fn preprocess_table_crop(
    page_bgra: &[u8],
    page_width: usize,
    page_height: usize,
    crop_x: i32,
    crop_y: i32,
    crop_width: usize,
    crop_height: usize,
) -> Option<TablePreprocessResult> {
    if page_width == 0 || page_height == 0 || crop_width == 0 || crop_height == 0 {
        return None;
    }

    let resize = calculate_tatr_crop_resize(crop_width, crop_height)?;
    let channel_stride = resize.new_width.checked_mul(resize.new_height)?;
    let tensor_len = channel_stride.checked_mul(3)?;
    let mut tensor = vec![0.0; tensor_len];

    fill_preprocessed_table_crop(
        &mut tensor,
        page_bgra,
        page_width,
        page_height,
        crop_x,
        crop_y,
        crop_width,
        crop_height,
        resize,
    );

    Some(TablePreprocessResult {
        tensor,
        new_width: resize.new_width,
        new_height: resize.new_height,
    })
}

#[allow(clippy::too_many_arguments)]
pub fn parse_tatr_detr_output(
    logits: &[f32],
    logits_shape: [usize; 3],
    pred_boxes: &[f32],
    pred_boxes_shape: [usize; 3],
    confidence_threshold: f32,
) -> Vec<TableSubDetection> {
    let [logit_batches, num_queries, num_classes_plus_one] = logits_shape;
    let [box_batches, box_queries, box_values] = pred_boxes_shape;
    if logit_batches == 0
        || box_batches == 0
        || num_queries != box_queries
        || box_values != 4
        || num_classes_plus_one <= 1
    {
        return Vec::new();
    }

    let Some(required_logits_len) = num_queries.checked_mul(num_classes_plus_one) else {
        return Vec::new();
    };
    let Some(required_boxes_len) = num_queries.checked_mul(4) else {
        return Vec::new();
    };
    if logits.len() < required_logits_len || pred_boxes.len() < required_boxes_len {
        return Vec::new();
    }

    let num_classes = num_classes_plus_one - 1;
    let mut detections = Vec::new();

    for query in 0..num_queries {
        let logit_start = query * num_classes_plus_one;
        let query_logits = &logits[logit_start..logit_start + num_classes_plus_one];

        let max_logit = query_logits
            .iter()
            .copied()
            .fold(f32::NEG_INFINITY, f32::max);
        let denominator = query_logits
            .iter()
            .map(|value| f64::from(*value - max_logit).exp())
            .sum::<f64>();
        if denominator <= 0.0 {
            continue;
        }

        let mut best_class = None;
        let mut best_score = 0.0;
        for (class_index, value) in query_logits.iter().take(num_classes).enumerate() {
            let score = f64::from(*value - max_logit).exp() / denominator;
            if score > best_score {
                best_score = score;
                best_class = Some(class_index);
            }
        }

        if best_score < f64::from(confidence_threshold) {
            continue;
        }
        let Some(class) = best_class.and_then(table_element_class_from_index) else {
            continue;
        };

        let box_start = query * 4;
        let cx = f64::from(pred_boxes[box_start]);
        let cy = f64::from(pred_boxes[box_start + 1]);
        let mut width = f64::from(pred_boxes[box_start + 2]);
        let mut height = f64::from(pred_boxes[box_start + 3]);
        let mut x = cx - width / 2.0;
        let mut y = cy - height / 2.0;

        if x < 0.0 {
            width += x;
            x = 0.0;
        }
        if y < 0.0 {
            height += y;
            y = 0.0;
        }
        if x + width > 1.0 {
            width = 1.0 - x;
        }
        if y + height > 1.0 {
            height = 1.0 - y;
        }
        if width <= 0.0 || height <= 0.0 {
            continue;
        }

        detections.push(TableSubDetection {
            class,
            confidence: best_score as f32,
            x,
            y,
            width,
            height,
        });
    }

    detections
}

pub fn tatr_detections_to_page_space(
    detections: &[TableSubDetection],
    table_x: f64,
    table_y: f64,
    table_width: f64,
    table_height: f64,
) -> Vec<TableSubDetection> {
    detections
        .iter()
        .map(|detection| TableSubDetection {
            class: detection.class,
            confidence: detection.confidence,
            x: table_x + detection.x * table_width,
            y: table_y + detection.y * table_height,
            width: detection.width * table_width,
            height: detection.height * table_height,
        })
        .collect()
}

pub fn build_table_structure_from_detections(
    detections: &[TableSubDetection],
    table_x: f64,
    table_y: f64,
    table_width: f64,
    table_height: f64,
) -> Option<TableStructure> {
    let rows = deduplicate_table_detections_by_iou(
        &detections
            .iter()
            .filter(|detection| detection.class == TableElementClass::Row)
            .cloned()
            .collect::<Vec<_>>(),
        TATR_DUPLICATE_IOU_THRESHOLD,
    );
    let columns = deduplicate_table_detections_by_iou(
        &detections
            .iter()
            .filter(|detection| detection.class == TableElementClass::Column)
            .cloned()
            .collect::<Vec<_>>(),
        TATR_DUPLICATE_IOU_THRESHOLD,
    );
    let spanning_cells = detections
        .iter()
        .filter(|detection| detection.class == TableElementClass::SpanningCell)
        .cloned()
        .collect::<Vec<_>>();

    if rows.is_empty() || columns.is_empty() {
        return None;
    }

    let cells = build_table_cell_grid(&rows, &columns, table_x, table_y, table_width, table_height);
    if cells.is_empty() || cells.len() > TATR_MAX_CELLS_PER_TABLE {
        return None;
    }

    Some(TableStructure {
        rows,
        columns,
        spanning_cells,
        cells,
    })
}

pub fn deduplicate_table_detections_by_iou(
    items: &[TableSubDetection],
    iou_threshold: f32,
) -> Vec<TableSubDetection> {
    let mut sorted = items.to_vec();
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
            if keep[j] && compute_table_iou(&sorted[i], &sorted[j]) > iou_threshold {
                keep[j] = false;
            }
        }
    }

    sorted
        .into_iter()
        .zip(keep)
        .filter_map(|(item, keep)| keep.then_some(item))
        .collect()
}

pub fn build_table_cell_grid(
    rows: &[TableSubDetection],
    columns: &[TableSubDetection],
    table_x: f64,
    table_y: f64,
    table_width: f64,
    table_height: f64,
) -> Vec<TableCellBounds> {
    let mut sorted_rows = rows.to_vec();
    let mut sorted_columns = columns.to_vec();
    sorted_rows.sort_by(|left, right| left.y.partial_cmp(&right.y).unwrap_or(Ordering::Equal));
    sorted_columns.sort_by(|left, right| left.x.partial_cmp(&right.x).unwrap_or(Ordering::Equal));

    let mut cells = Vec::with_capacity(sorted_rows.len().saturating_mul(sorted_columns.len()));
    let table_right = table_x + table_width;
    let table_bottom = table_y + table_height;

    for (row_index, row) in sorted_rows.iter().enumerate() {
        for (column_index, column) in sorted_columns.iter().enumerate() {
            let x1 = row.x.max(column.x).max(table_x);
            let y1 = row.y.max(column.y).max(table_y);
            let x2 = (row.x + row.width)
                .min(column.x + column.width)
                .min(table_right);
            let y2 = (row.y + row.height)
                .min(column.y + column.height)
                .min(table_bottom);

            let width = x2 - x1;
            let height = y2 - y1;
            if width < TATR_MIN_CELL_SIDE_PX || height < TATR_MIN_CELL_SIDE_PX {
                continue;
            }

            cells.push(TableCellBounds {
                row_index,
                column_index,
                x: x1,
                y: y1,
                width,
                height,
            });
        }
    }

    cells
}

pub fn compute_table_iou(a: &TableSubDetection, b: &TableSubDetection) -> f32 {
    compute_rect_iou(a.x, a.y, a.width, a.height, b.x, b.y, b.width, b.height)
}

pub fn compute_rect_iou(
    ax: f64,
    ay: f64,
    aw: f64,
    ah: f64,
    bx: f64,
    by: f64,
    bw: f64,
    bh: f64,
) -> f32 {
    let x1 = ax.max(bx);
    let y1 = ay.max(by);
    let x2 = (ax + aw).min(bx + bw);
    let y2 = (ay + ah).min(by + bh);

    let intersection_width = (x2 - x1).max(0.0);
    let intersection_height = (y2 - y1).max(0.0);
    let intersection_area = intersection_width * intersection_height;
    let union_area = aw * ah + bw * bh - intersection_area;

    if union_area > 0.0 {
        (intersection_area / union_area) as f32
    } else {
        0.0
    }
}

#[allow(clippy::too_many_arguments)]
fn fill_preprocessed_table_crop(
    tensor: &mut [f32],
    page_bgra: &[u8],
    page_width: usize,
    page_height: usize,
    crop_x: i32,
    crop_y: i32,
    crop_width: usize,
    crop_height: usize,
    resize: TableCropResize,
) {
    let channel_stride = resize.new_height * resize.new_width;
    let red_base = 0;
    let green_base = channel_stride;
    let blue_base = 2 * channel_stride;
    let inverse_scale = 1.0 / resize.scale;

    for y in 0..resize.new_height {
        let source_y_offset = ((y as f64 * inverse_scale).min((crop_height - 1) as f64)) as i32;
        let source_y = (crop_y + source_y_offset).clamp(0, page_height as i32 - 1) as usize;
        let destination_row_offset = y * resize.new_width;

        for x in 0..resize.new_width {
            let source_x_offset = ((x as f64 * inverse_scale).min((crop_width - 1) as f64)) as i32;
            let source_x = (crop_x + source_x_offset).clamp(0, page_width as i32 - 1) as usize;
            let source_index = source_y
                .checked_mul(page_width)
                .and_then(|offset| offset.checked_add(source_x))
                .and_then(|offset| offset.checked_mul(4));
            let Some(source_index) = source_index else {
                continue;
            };
            if source_index + 2 >= page_bgra.len() {
                continue;
            }

            let blue = page_bgra[source_index] as f32 / 255.0;
            let green = page_bgra[source_index + 1] as f32 / 255.0;
            let red = page_bgra[source_index + 2] as f32 / 255.0;
            let destination = destination_row_offset + x;

            tensor[red_base + destination] = (red - TATR_IMAGE_MEAN[0]) / TATR_IMAGE_STD[0];
            tensor[green_base + destination] = (green - TATR_IMAGE_MEAN[1]) / TATR_IMAGE_STD[1];
            tensor[blue_base + destination] = (blue - TATR_IMAGE_MEAN[2]) / TATR_IMAGE_STD[2];
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
