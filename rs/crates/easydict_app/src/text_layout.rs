use unicode_segmentation::UnicodeSegmentation;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CharCategory {
    Cjk,
    Latin,
    Space,
    HardBreak,
    OpenPunctuation,
    ClosePunctuation,
    SoftHyphen,
    Other,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SegmentKind {
    Word,
    CjkGrapheme,
    Space,
    HardBreak,
    FormulaPlaceholder,
    OpenPunctuation,
    ClosePunctuation,
    SoftHyphen,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct SegmentedText {
    pub segments: Vec<String>,
    pub kinds: Vec<SegmentKind>,
}

pub trait TextMeasurer {
    fn measure_segment(&self, text: &str) -> f64;
    fn measure_grapheme(&self, grapheme: &str) -> f64;
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct TextPrepareOptions {
    pub normalize_whitespace: bool,
}

impl Default for TextPrepareOptions {
    fn default() -> Self {
        Self {
            normalize_whitespace: true,
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct PreparedParagraph {
    pub segments: Vec<String>,
    pub widths: Vec<f64>,
    pub kinds: Vec<SegmentKind>,
    pub line_end_fit_advances: Vec<f64>,
    pub grapheme_widths: Vec<Option<Vec<f64>>>,
    pub grapheme_prefix_sums: Vec<Option<Vec<f64>>>,
    pub graphemes: Vec<Option<Vec<String>>>,
    pub is_prohibited_line_start: Vec<bool>,
    pub is_prohibited_line_end: Vec<bool>,
    pub hard_break_indices: Vec<usize>,
    pub discretionary_hyphen_width: f64,
}

impl PreparedParagraph {
    pub fn count(&self) -> usize {
        self.segments.len()
    }

    pub fn is_single_chunk(&self) -> bool {
        self.hard_break_indices.is_empty()
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct LayoutCursor {
    pub segment_index: usize,
    pub grapheme_index: usize,
}

impl LayoutCursor {
    pub const START: Self = Self {
        segment_index: 0,
        grapheme_index: 0,
    };
}

#[derive(Clone, Debug, PartialEq)]
pub struct LayoutLine {
    pub start_segment: usize,
    pub end_segment: usize,
    pub start_grapheme: usize,
    pub end_grapheme: usize,
    pub width: f64,
    pub text: String,
}

#[derive(Clone, Debug, PartialEq)]
pub struct LayoutLineRange {
    pub start_segment: usize,
    pub end_segment: usize,
    pub start_grapheme: usize,
    pub end_grapheme: usize,
    pub width: f64,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct LayoutResult {
    pub line_count: usize,
    pub max_line_width: f64,
    pub has_overflow: bool,
}

#[derive(Clone, Debug, PartialEq)]
pub struct LayoutLinesResult {
    pub lines: Vec<LayoutLine>,
    pub max_line_width: f64,
    pub has_overflow: bool,
}

#[derive(Clone, Debug, PartialEq)]
pub struct FontFitRequest {
    pub text: String,
    pub start_font_size: f64,
    pub min_font_size: f64,
    pub max_width: Option<f64>,
    pub max_height: Option<f64>,
    pub line_height_multiplier: f64,
    pub line_widths: Option<Vec<f64>>,
    pub max_line_count: Option<usize>,
    pub line_heights: Option<Vec<f64>>,
    pub normalize_whitespace: bool,
}

impl FontFitRequest {
    pub fn new(text: impl Into<String>, start_font_size: f64) -> Self {
        Self {
            text: text.into(),
            start_font_size,
            min_font_size: 6.0,
            max_width: None,
            max_height: None,
            line_height_multiplier: 1.2,
            line_widths: None,
            max_line_count: None,
            line_heights: None,
            normalize_whitespace: true,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct FontFitResult {
    pub chosen_font_size: f64,
    pub chosen_line_height: f64,
    pub was_shrunk: bool,
    pub was_truncated: bool,
    pub line_count: usize,
}

pub struct KinsokuTable;

impl KinsokuTable {
    pub fn is_prohibited_line_start(ch: char) -> bool {
        is_prohibited_line_start(ch)
    }

    pub fn is_prohibited_line_end(ch: char) -> bool {
        is_prohibited_line_end(ch)
    }

    pub fn is_left_sticky(ch: char) -> bool {
        is_left_sticky(ch)
    }
}

pub fn classify_char(ch: char) -> CharCategory {
    if ch == '\n' {
        return CharCategory::HardBreak;
    }

    if ch == '\u{00AD}' {
        return CharCategory::SoftHyphen;
    }

    if matches!(ch, ' ' | '\t' | '\r') {
        return CharCategory::Space;
    }

    if is_open_punctuation(ch) {
        return CharCategory::OpenPunctuation;
    }

    if is_close_punctuation(ch) {
        return CharCategory::ClosePunctuation;
    }

    if is_cjk(ch) {
        return CharCategory::Cjk;
    }

    CharCategory::Latin
}

pub fn is_cjk(ch: char) -> bool {
    matches!(
        ch,
        '\u{3000}'..='\u{303F}'
            | '\u{3040}'..='\u{309F}'
            | '\u{30A0}'..='\u{30FF}'
            | '\u{3400}'..='\u{4DBF}'
            | '\u{4E00}'..='\u{9FFF}'
            | '\u{AC00}'..='\u{D7AF}'
            | '\u{F900}'..='\u{FAFF}'
            | '\u{FF00}'..='\u{FFEF}'
            | '\u{20000}'..='\u{3FFFF}'
    )
}

pub fn is_open_punctuation(ch: char) -> bool {
    matches!(
        ch,
        '(' | '['
            | '{'
            | '<'
            | '\u{3008}'
            | '\u{300A}'
            | '\u{300C}'
            | '\u{300E}'
            | '\u{3010}'
            | '\u{3014}'
            | '\u{3016}'
            | '\u{3018}'
            | '\u{301A}'
            | '\u{301D}'
            | '\u{FF08}'
            | '\u{201C}'
            | '\u{2018}'
            | '\u{00AB}'
    )
}

pub fn is_close_punctuation(ch: char) -> bool {
    matches!(
        ch,
        ')' | ']'
            | '}'
            | '>'
            | '.'
            | ','
            | ';'
            | ':'
            | '!'
            | '?'
            | '%'
            | '\u{3001}'
            | '\u{3002}'
            | '\u{3009}'
            | '\u{300B}'
            | '\u{300D}'
            | '\u{300F}'
            | '\u{3011}'
            | '\u{3015}'
            | '\u{3017}'
            | '\u{3019}'
            | '\u{301B}'
            | '\u{301F}'
            | '\u{FF09}'
            | '\u{FF0C}'
            | '\u{FF1A}'
            | '\u{FF1B}'
            | '\u{FF01}'
            | '\u{FF1F}'
            | '\u{FF0E}'
            | '\u{201D}'
            | '\u{2019}'
            | '\u{00BB}'
    )
}

pub fn enumerate_graphemes(text: &str) -> Vec<&str> {
    UnicodeSegmentation::graphemes(text, true).collect()
}

pub fn normalize_whitespace(text: &str) -> String {
    let mut normalized = String::with_capacity(text.len());
    let mut last_was_space = false;

    for ch in text.chars() {
        if ch == '\n' {
            if normalized.ends_with(' ') {
                normalized.pop();
            }
            normalized.push('\n');
            last_was_space = false;
        } else if matches!(ch, ' ' | '\t' | '\r') {
            if !last_was_space {
                normalized.push(' ');
                last_was_space = true;
            }
        } else {
            normalized.push(ch);
            last_was_space = false;
        }
    }

    normalized
}

pub fn segment_text(text: &str) -> SegmentedText {
    segment_text_with_options(text, true)
}

pub fn segment_text_with_options(text: &str, normalize: bool) -> SegmentedText {
    if text.is_empty() {
        return SegmentedText {
            segments: Vec::new(),
            kinds: Vec::new(),
        };
    }

    let text = if normalize {
        normalize_whitespace(text)
    } else {
        text.to_string()
    };
    let chars: Vec<char> = text.chars().collect();
    let mut segments = Vec::new();
    let mut kinds = Vec::new();
    let mut buffer = String::new();
    let mut index = 0;

    while index < chars.len() {
        let ch = chars[index];
        match classify_char(ch) {
            CharCategory::HardBreak => {
                flush_word(&mut buffer, &mut segments, &mut kinds);
                segments.push("\n".to_string());
                kinds.push(SegmentKind::HardBreak);
                index += 1;
            }
            CharCategory::SoftHyphen => {
                flush_word(&mut buffer, &mut segments, &mut kinds);
                segments.push("\u{00AD}".to_string());
                kinds.push(SegmentKind::SoftHyphen);
                index += 1;
            }
            CharCategory::Space => {
                flush_word(&mut buffer, &mut segments, &mut kinds);
                let start = index;
                while index < chars.len() && matches!(chars[index], ' ' | '\t' | '\r') {
                    index += 1;
                }
                segments.push(chars[start..index].iter().collect());
                kinds.push(SegmentKind::Space);
            }
            CharCategory::Cjk => {
                flush_word(&mut buffer, &mut segments, &mut kinds);
                segments.push(ch.to_string());
                kinds.push(SegmentKind::CjkGrapheme);
                index += 1;
            }
            CharCategory::OpenPunctuation => {
                flush_word(&mut buffer, &mut segments, &mut kinds);
                segments.push(ch.to_string());
                kinds.push(SegmentKind::OpenPunctuation);
                index += 1;
            }
            CharCategory::ClosePunctuation => {
                if buffer.is_empty() {
                    segments.push(ch.to_string());
                    kinds.push(SegmentKind::ClosePunctuation);
                    index += 1;
                } else {
                    buffer.push(ch);
                    index += 1;
                    while index < chars.len() && is_close_punctuation(chars[index]) {
                        buffer.push(chars[index]);
                        index += 1;
                    }
                    flush_word(&mut buffer, &mut segments, &mut kinds);
                }
            }
            CharCategory::Latin | CharCategory::Other => {
                buffer.push(ch);
                index += 1;
                while index < chars.len() && classify_char(chars[index]) == CharCategory::Latin {
                    buffer.push(chars[index]);
                    index += 1;
                }
            }
        }
    }

    flush_word(&mut buffer, &mut segments, &mut kinds);
    SegmentedText { segments, kinds }
}

pub fn prepare_paragraph<M>(text: &str, measurer: &M) -> PreparedParagraph
where
    M: TextMeasurer + ?Sized,
{
    prepare_paragraph_with_options(text, measurer, TextPrepareOptions::default())
}

pub fn prepare_paragraph_with_options<M>(
    text: &str,
    measurer: &M,
    options: TextPrepareOptions,
) -> PreparedParagraph
where
    M: TextMeasurer + ?Sized,
{
    let segmented = segment_text_with_options(text, options.normalize_whitespace);
    let count = segmented.segments.len();
    let mut widths = vec![0.0; count];
    let mut line_end_fit_advances = vec![0.0; count];
    let mut grapheme_widths = vec![None; count];
    let mut grapheme_prefix_sums = vec![None; count];
    let mut graphemes = vec![None; count];
    let mut prohibited_line_start = vec![false; count];
    let mut prohibited_line_end = vec![false; count];
    let mut hard_break_indices = Vec::new();
    let mut has_soft_hyphen = false;

    for index in 0..count {
        let segment = &segmented.segments[index];
        let kind = segmented.kinds[index];

        if let Some(first) = segment.chars().next() {
            prohibited_line_start[index] = is_prohibited_line_start(first);
        }

        if let Some(last) = segment.chars().next_back() {
            prohibited_line_end[index] = is_prohibited_line_end(last);
        }

        match kind {
            SegmentKind::HardBreak => {
                hard_break_indices.push(index);
            }
            SegmentKind::SoftHyphen => {
                has_soft_hyphen = true;
            }
            SegmentKind::CjkGrapheme => {
                let width = measurer.measure_grapheme(segment);
                widths[index] = width;
                line_end_fit_advances[index] = width;
            }
            _ => {
                let width = measurer.measure_segment(segment);
                widths[index] = width;
                if kind != SegmentKind::Space {
                    line_end_fit_advances[index] = width;
                }

                if kind == SegmentKind::Word {
                    let segment_graphemes = enumerate_graphemes(segment);
                    if segment_graphemes.len() > 1 {
                        let measured_widths: Vec<f64> = segment_graphemes
                            .iter()
                            .map(|grapheme| measurer.measure_grapheme(grapheme))
                            .collect();
                        let mut running_width = 0.0;
                        let prefix_sums = measured_widths
                            .iter()
                            .map(|width| {
                                running_width += width;
                                running_width
                            })
                            .collect();

                        graphemes[index] =
                            Some(segment_graphemes.into_iter().map(str::to_string).collect());
                        grapheme_widths[index] = Some(measured_widths);
                        grapheme_prefix_sums[index] = Some(prefix_sums);
                    }
                }
            }
        }
    }

    let discretionary_hyphen_width = if has_soft_hyphen {
        let width = measurer.measure_grapheme("-");
        for (index, kind) in segmented.kinds.iter().enumerate() {
            if *kind == SegmentKind::SoftHyphen {
                line_end_fit_advances[index] = width;
            }
        }
        width
    } else {
        0.0
    };

    PreparedParagraph {
        segments: segmented.segments,
        widths,
        kinds: segmented.kinds,
        line_end_fit_advances,
        grapheme_widths,
        grapheme_prefix_sums,
        graphemes,
        is_prohibited_line_start: prohibited_line_start,
        is_prohibited_line_end: prohibited_line_end,
        hard_break_indices,
        discretionary_hyphen_width,
    }
}

pub fn layout_paragraph(prepared: &PreparedParagraph, max_width: f64) -> LayoutResult {
    let mut line_count = 0;
    let mut max_line_width = 0.0_f64;

    walk_line_ranges(prepared, max_width, |range| {
        line_count += 1;
        max_line_width = max_line_width.max(range.width);
    });

    LayoutResult {
        line_count,
        max_line_width,
        has_overflow: false,
    }
}

pub fn layout_paragraph_with_lines(
    prepared: &PreparedParagraph,
    max_width: f64,
) -> LayoutLinesResult {
    let mut lines = Vec::new();
    let mut max_line_width = 0.0_f64;
    let mut cursor = LayoutCursor::START;

    while cursor.segment_index < prepared.count() {
        let Some(line) = layout_next_line(prepared, cursor, max_width) else {
            break;
        };

        max_line_width = max_line_width.max(line.width);
        cursor = LayoutCursor {
            segment_index: line.end_segment,
            grapheme_index: line.end_grapheme,
        };
        lines.push(line);
    }

    LayoutLinesResult {
        lines,
        max_line_width,
        has_overflow: false,
    }
}

pub fn layout_paragraph_with_widths(
    prepared: &PreparedParagraph,
    max_widths: &[f64],
) -> LayoutResult {
    if max_widths.is_empty() {
        return LayoutResult {
            line_count: 0,
            max_line_width: 0.0,
            has_overflow: false,
        };
    }

    let mut line_count = 0;
    let mut max_line_width = 0.0_f64;
    let mut cursor = LayoutCursor::START;

    while cursor.segment_index < prepared.count() {
        let width = max_widths[usize::min(line_count, max_widths.len() - 1)];
        let Some(line) = layout_next_line_core(prepared, cursor, width, false) else {
            break;
        };

        line_count += 1;
        max_line_width = max_line_width.max(line.width);
        cursor = LayoutCursor {
            segment_index: line.end_segment,
            grapheme_index: line.end_grapheme,
        };
    }

    LayoutResult {
        line_count,
        max_line_width,
        has_overflow: false,
    }
}

pub fn layout_paragraph_with_lines_and_widths(
    prepared: &PreparedParagraph,
    max_widths: &[f64],
) -> LayoutLinesResult {
    if max_widths.is_empty() {
        return LayoutLinesResult {
            lines: Vec::new(),
            max_line_width: 0.0,
            has_overflow: false,
        };
    }

    let mut lines = Vec::new();
    let mut max_line_width = 0.0_f64;
    let mut cursor = LayoutCursor::START;

    while cursor.segment_index < prepared.count() {
        let width = max_widths[usize::min(lines.len(), max_widths.len() - 1)];
        let Some(line) = layout_next_line(prepared, cursor, width) else {
            break;
        };

        max_line_width = max_line_width.max(line.width);
        cursor = LayoutCursor {
            segment_index: line.end_segment,
            grapheme_index: line.end_grapheme,
        };
        lines.push(line);
    }

    LayoutLinesResult {
        lines,
        max_line_width,
        has_overflow: false,
    }
}

pub fn walk_line_ranges(
    prepared: &PreparedParagraph,
    max_width: f64,
    mut on_line: impl FnMut(LayoutLineRange),
) -> usize {
    let mut line_count = 0;
    let mut cursor = LayoutCursor::START;

    while cursor.segment_index < prepared.count() {
        let Some(line) = layout_next_line_core(prepared, cursor, max_width, false) else {
            break;
        };

        on_line(LayoutLineRange {
            start_segment: line.start_segment,
            end_segment: line.end_segment,
            start_grapheme: line.start_grapheme,
            end_grapheme: line.end_grapheme,
            width: line.width,
        });
        line_count += 1;

        cursor = LayoutCursor {
            segment_index: line.end_segment,
            grapheme_index: line.end_grapheme,
        };
    }

    line_count
}

pub fn layout_next_line(
    prepared: &PreparedParagraph,
    start: LayoutCursor,
    max_width: f64,
) -> Option<LayoutLine> {
    layout_next_line_core(prepared, start, max_width, true)
}

pub fn solve_font_fit<M, F>(request: &FontFitRequest, mut measurer_factory: F) -> FontFitResult
where
    M: TextMeasurer,
    F: FnMut(f64) -> M,
{
    let prepare_options = TextPrepareOptions {
        normalize_whitespace: request.normalize_whitespace,
    };

    if let (true, line_count) = try_fit_font_size(
        request.start_font_size,
        request,
        &prepare_options,
        &mut measurer_factory,
    ) {
        let chosen_line_height = request.start_font_size * request.line_height_multiplier;
        return FontFitResult {
            chosen_font_size: request.start_font_size,
            chosen_line_height,
            was_shrunk: false,
            was_truncated: false,
            line_count,
        };
    }

    let mut lo = request.min_font_size;
    let mut hi = request.start_font_size;
    let mut best_size = lo;
    let mut best_line_count = 0;
    let mut best_fits = false;

    while hi - lo > 0.25 {
        let mid = (lo + hi) / 2.0;
        let (fits, line_count) =
            try_fit_font_size(mid, request, &prepare_options, &mut measurer_factory);
        if fits {
            best_size = mid;
            best_line_count = line_count;
            best_fits = true;
            lo = mid;
        } else {
            hi = mid;
        }
    }

    if !best_fits {
        let (_, line_count) = try_fit_font_size(
            request.min_font_size,
            request,
            &prepare_options,
            &mut measurer_factory,
        );
        best_size = request.min_font_size;
        best_line_count = line_count;
    }

    let chosen_line_height = best_size * request.line_height_multiplier;
    FontFitResult {
        chosen_font_size: best_size,
        chosen_line_height,
        was_shrunk: true,
        was_truncated: !best_fits,
        line_count: best_line_count,
    }
}

fn try_fit_font_size<M, F>(
    font_size: f64,
    request: &FontFitRequest,
    prepare_options: &TextPrepareOptions,
    measurer_factory: &mut F,
) -> (bool, usize)
where
    M: TextMeasurer,
    F: FnMut(f64) -> M,
{
    let measurer = measurer_factory(font_size);
    let prepared = prepare_paragraph_with_options(&request.text, &measurer, *prepare_options);
    let line_height = font_size * request.line_height_multiplier;

    if let Some(line_widths) = request
        .line_widths
        .as_ref()
        .filter(|widths| !widths.is_empty())
    {
        let result = layout_paragraph_with_widths(&prepared, line_widths);
        let line_count = result.line_count;
        let max_line_count = request.max_line_count.unwrap_or(line_widths.len());

        if max_line_count > 0 && line_count > max_line_count {
            return (false, line_count);
        }

        if let Some(max_height) = request.max_height {
            let total_height = line_count as f64 * line_height;
            if total_height > max_height + 0.01 {
                return (false, line_count);
            }
        }

        if let Some(line_heights) = request
            .line_heights
            .as_ref()
            .filter(|heights| !heights.is_empty())
        {
            if line_count > line_heights.len() {
                return (false, line_count);
            }

            let min_height = line_heights
                .iter()
                .take(line_count)
                .copied()
                .fold(f64::MAX, f64::min);
            if font_size > min_height * 0.98 {
                return (false, line_count);
            }
        }

        return (true, line_count);
    }

    if let Some(max_width) = request.max_width {
        let result = layout_paragraph(&prepared, max_width);
        let line_count = result.line_count;

        if let Some(max_height) = request.max_height {
            let max_lines = if line_height > 0.0 {
                usize::max(1, (max_height / line_height).floor() as usize)
            } else {
                usize::MAX
            };
            return (line_count <= max_lines, line_count);
        }

        return (true, line_count);
    }

    (true, 0)
}

fn layout_next_line_core(
    prepared: &PreparedParagraph,
    start: LayoutCursor,
    max_width: f64,
    build_text: bool,
) -> Option<LayoutLine> {
    let segments = &prepared.segments;
    let widths = &prepared.widths;
    let kinds = &prepared.kinds;

    if start.segment_index >= segments.len() {
        return None;
    }

    if start.grapheme_index > 0 {
        return layout_line_from_mid_segment(prepared, start, max_width, build_text);
    }

    let mut seg = start.segment_index;
    while seg < segments.len() && kinds[seg] == SegmentKind::Space {
        seg += 1;
    }

    if seg >= segments.len() {
        return None;
    }

    if kinds[seg] == SegmentKind::HardBreak {
        return Some(LayoutLine {
            start_segment: seg,
            end_segment: seg + 1,
            start_grapheme: 0,
            end_grapheme: 0,
            width: 0.0,
            text: String::new(),
        });
    }

    let line_start_seg = seg;
    let mut line_width = 0.0;
    let mut content_width = 0.0;
    let mut last_content_kind = SegmentKind::Space;
    let mut text = if build_text {
        Some(String::new())
    } else {
        None
    };

    while seg < segments.len() {
        let kind = kinds[seg];

        if kind == SegmentKind::HardBreak {
            seg += 1;
            break;
        }

        let seg_width = widths[seg];
        if line_width + seg_width > max_width && line_width > 0.0 {
            if prepared.is_prohibited_line_start[seg]
                && matches!(
                    kind,
                    SegmentKind::CjkGrapheme | SegmentKind::ClosePunctuation
                )
            {
                // Carry the prohibited line-start segment.
            } else if is_left_sticky_break(&segments[seg], last_content_kind) {
                // Carry ASCII punctuation after CJK content.
            } else {
                break;
            }
        }

        if line_width + seg_width > max_width && line_width == 0.0 {
            if kind == SegmentKind::Word && prepared.grapheme_prefix_sums[seg].is_some() {
                return break_long_segment(prepared, seg, max_width, line_start_seg, build_text);
            }

            if let Some(text) = text.as_mut() {
                text.push_str(&segments[seg]);
            }
            line_width += seg_width;
            content_width = line_width;
            seg += 1;
            break;
        }

        if kind == SegmentKind::Space {
            if line_width == 0.0 {
                seg += 1;
                continue;
            }

            line_width += seg_width;
            if let Some(text) = text.as_mut() {
                text.push_str(&segments[seg]);
            }
        } else {
            line_width += seg_width;
            if let Some(text) = text.as_mut() {
                text.push_str(&segments[seg]);
            }
            content_width = line_width;
            last_content_kind = kind;

            while seg + 1 < segments.len() && kinds[seg + 1] == SegmentKind::ClosePunctuation {
                let close_width = widths[seg + 1];
                if line_width + close_width > max_width {
                    break;
                }

                seg += 1;
                line_width += close_width;
                if let Some(text) = text.as_mut() {
                    text.push_str(&segments[seg]);
                }
                content_width = line_width;
            }
        }

        seg += 1;

        if seg < segments.len() && kinds[seg] == SegmentKind::OpenPunctuation {
            let open_width = widths[seg];
            let after_open_width = if seg + 1 < segments.len() {
                widths[seg + 1]
            } else {
                0.0
            };
            if line_width + open_width + after_open_width > max_width && line_width > 0.0 {
                break;
            }
        }
    }

    Some(LayoutLine {
        start_segment: line_start_seg,
        end_segment: seg,
        start_grapheme: 0,
        end_grapheme: 0,
        width: content_width,
        text: text
            .map(|text| text.trim_end().to_string())
            .unwrap_or_default(),
    })
}

fn layout_line_from_mid_segment(
    prepared: &PreparedParagraph,
    start: LayoutCursor,
    max_width: f64,
    build_text: bool,
) -> Option<LayoutLine> {
    let seg = start.segment_index;
    let grapheme_start = start.grapheme_index;
    let Some(graphemes) = prepared.graphemes.get(seg).and_then(Option::as_ref) else {
        return layout_next_line_core(
            prepared,
            LayoutCursor {
                segment_index: seg + 1,
                grapheme_index: 0,
            },
            max_width,
            build_text,
        );
    };
    let Some(grapheme_widths) = prepared.grapheme_widths.get(seg).and_then(Option::as_ref) else {
        return layout_next_line_core(
            prepared,
            LayoutCursor {
                segment_index: seg + 1,
                grapheme_index: 0,
            },
            max_width,
            build_text,
        );
    };
    if grapheme_start >= graphemes.len() {
        return layout_next_line_core(
            prepared,
            LayoutCursor {
                segment_index: seg + 1,
                grapheme_index: 0,
            },
            max_width,
            build_text,
        );
    }

    let mut text = if build_text {
        Some(String::new())
    } else {
        None
    };
    let mut line_width = 0.0;
    let mut grapheme_index = grapheme_start;

    while grapheme_index < graphemes.len() {
        let width = grapheme_widths[grapheme_index];
        if line_width + width > max_width && line_width > 0.0 {
            break;
        }

        if let Some(text) = text.as_mut() {
            text.push_str(&graphemes[grapheme_index]);
        }
        line_width += width;
        grapheme_index += 1;
    }

    if grapheme_index >= graphemes.len() {
        return Some(LayoutLine {
            start_segment: seg,
            end_segment: seg + 1,
            start_grapheme: grapheme_start,
            end_grapheme: 0,
            width: line_width,
            text: text.unwrap_or_default(),
        });
    }

    Some(LayoutLine {
        start_segment: seg,
        end_segment: seg,
        start_grapheme: grapheme_start,
        end_grapheme: grapheme_index,
        width: line_width,
        text: text.unwrap_or_default(),
    })
}

fn break_long_segment(
    prepared: &PreparedParagraph,
    seg: usize,
    max_width: f64,
    line_start_seg: usize,
    build_text: bool,
) -> Option<LayoutLine> {
    let prefix_sums = prepared.grapheme_prefix_sums[seg].as_ref()?;
    let graphemes = prepared.graphemes[seg].as_ref()?;
    let mut lo = 0;
    let mut hi = prefix_sums.len().saturating_sub(1);
    let mut best_count = 0;

    while lo <= hi {
        let mid = lo + (hi - lo) / 2;
        if prefix_sums[mid] <= max_width {
            best_count = mid + 1;
            lo = mid + 1;
        } else if mid == 0 {
            break;
        } else {
            hi = mid - 1;
        }
    }

    if best_count == 0 {
        best_count = 1;
    }

    let text = if build_text {
        graphemes[..best_count].concat()
    } else {
        String::new()
    };
    let width = if best_count == 0 {
        0.0
    } else {
        prefix_sums[best_count - 1]
    };

    Some(LayoutLine {
        start_segment: line_start_seg,
        end_segment: seg,
        start_grapheme: 0,
        end_grapheme: best_count,
        width,
        text,
    })
}

fn is_left_sticky_break(segment: &str, last_content_kind: SegmentKind) -> bool {
    if last_content_kind != SegmentKind::CjkGrapheme {
        return false;
    }

    segment.chars().next().is_some_and(is_left_sticky)
}

pub fn is_prohibited_line_start(ch: char) -> bool {
    matches!(
        ch,
        '\u{FF09}'
            | '\u{3001}'
            | '\u{3002}'
            | '\u{300D}'
            | '\u{300F}'
            | '\u{3011}'
            | '\u{3015}'
            | '\u{3009}'
            | '\u{300B}'
            | '\u{3017}'
            | '\u{3019}'
            | '\u{301B}'
            | '\u{301F}'
            | '\u{30FB}'
            | '\u{30FC}'
            | '\u{FF01}'
            | '\u{FF1F}'
            | '\u{FF1B}'
            | '\u{FF1A}'
            | '\u{FF0C}'
            | '\u{FF0E}'
            | '\u{3041}'
            | '\u{3043}'
            | '\u{3045}'
            | '\u{3047}'
            | '\u{3049}'
            | '\u{3063}'
            | '\u{3083}'
            | '\u{3085}'
            | '\u{3087}'
            | '\u{308E}'
            | '\u{30A1}'
            | '\u{30A3}'
            | '\u{30A5}'
            | '\u{30A7}'
            | '\u{30A9}'
            | '\u{30C3}'
            | '\u{30E3}'
            | '\u{30E5}'
            | '\u{30E7}'
            | '\u{30EE}'
            | '\u{309D}'
            | '\u{309E}'
            | '\u{30FD}'
            | '\u{30FE}'
            | '\u{3005}'
            | '\u{303B}'
    )
}

pub fn is_prohibited_line_end(ch: char) -> bool {
    matches!(
        ch,
        '\u{FF08}'
            | '\u{3014}'
            | '\u{3008}'
            | '\u{300A}'
            | '\u{300C}'
            | '\u{300E}'
            | '\u{3010}'
            | '\u{3016}'
            | '\u{3018}'
            | '\u{301A}'
            | '\u{301D}'
    )
}

pub fn is_left_sticky(ch: char) -> bool {
    matches!(
        ch,
        '.' | ',' | '!' | '?' | ':' | ';' | ')' | ']' | '}' | '%' | '"' | '\u{2026}'
    )
}

fn flush_word(buffer: &mut String, segments: &mut Vec<String>, kinds: &mut Vec<SegmentKind>) {
    if !buffer.is_empty() {
        segments.push(std::mem::take(buffer));
        kinds.push(SegmentKind::Word);
    }
}
