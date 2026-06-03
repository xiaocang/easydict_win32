use easydict_app::text_layout::{
    classify_char, enumerate_graphemes, is_cjk, is_close_punctuation, is_left_sticky,
    is_open_punctuation, is_prohibited_line_end, is_prohibited_line_start, layout_next_line,
    layout_paragraph, layout_paragraph_with_lines, layout_paragraph_with_lines_and_widths,
    layout_paragraph_with_widths, normalize_whitespace, prepare_paragraph,
    prepare_paragraph_with_options, segment_text, segment_text_with_options, solve_font_fit,
    walk_line_ranges, CharCategory, FontFitRequest, KinsokuTable, LayoutCursor, SegmentKind,
    SegmentedText, TextMeasurer, TextPrepareOptions,
};

struct FixedWidthMeasurer {
    cjk_width: f64,
    latin_width: f64,
    space_width: f64,
}

impl Default for FixedWidthMeasurer {
    fn default() -> Self {
        Self {
            cjk_width: 10.0,
            latin_width: 6.0,
            space_width: 3.0,
        }
    }
}

impl TextMeasurer for FixedWidthMeasurer {
    fn measure_segment(&self, text: &str) -> f64 {
        text.chars().map(|ch| self.measure_char(ch)).sum()
    }

    fn measure_grapheme(&self, grapheme: &str) -> f64 {
        grapheme
            .chars()
            .next()
            .map(|ch| self.measure_char(ch))
            .unwrap_or(0.0)
    }
}

impl FixedWidthMeasurer {
    fn measure_char(&self, ch: char) -> f64 {
        if matches!(ch, ' ' | '\t') {
            self.space_width
        } else if is_cjk(ch) {
            self.cjk_width
        } else {
            self.latin_width
        }
    }
}

fn texts(segmented: &SegmentedText) -> Vec<&str> {
    segmented.segments.iter().map(String::as_str).collect()
}

fn line_texts(result: &easydict_app::text_layout::LayoutLinesResult) -> Vec<&str> {
    result.lines.iter().map(|line| line.text.as_str()).collect()
}

fn scaling_measurer(font_size: f64) -> FixedWidthMeasurer {
    FixedWidthMeasurer {
        latin_width: font_size * 0.5,
        cjk_width: font_size,
        space_width: font_size * 0.25,
    }
}

#[test]
fn native_text_layout_classifies_latin_cjk_whitespace_punctuation_and_soft_hyphen() {
    for ch in ['A', 'z', '0', '9', '-', '_'] {
        assert_eq!(classify_char(ch), CharCategory::Latin);
    }

    for ch in [
        '\u{4E00}',
        '\u{9FFF}',
        '\u{3040}',
        '\u{30A0}',
        '\u{AC00}',
        '\u{20000}',
    ] {
        assert_eq!(classify_char(ch), CharCategory::Cjk);
        assert!(is_cjk(ch));
    }

    for ch in [' ', '\t', '\r'] {
        assert_eq!(classify_char(ch), CharCategory::Space);
    }

    assert_eq!(classify_char('\n'), CharCategory::HardBreak);
    assert_eq!(classify_char('\u{00AD}'), CharCategory::SoftHyphen);

    for ch in [
        '(', '[', '{', '\u{3008}', '\u{300A}', '\u{300C}', '\u{301D}', '\u{FF08}',
    ] {
        assert_eq!(classify_char(ch), CharCategory::OpenPunctuation);
        assert!(is_open_punctuation(ch));
    }

    for ch in [
        ')', ']', '.', ',', ';', ':', '!', '?', '\u{3001}', '\u{3002}', '\u{3009}', '\u{300B}',
        '\u{301F}', '\u{FF09}', '\u{FF1A}',
    ] {
        assert_eq!(classify_char(ch), CharCategory::ClosePunctuation);
        assert!(is_close_punctuation(ch));
    }

    assert!(!is_cjk('A'));
}

#[test]
fn native_text_layout_enumerates_grapheme_clusters_with_unicode_segmentation() {
    assert_eq!(enumerate_graphemes("Hello"), ["H", "e", "l", "l", "o"]);
    assert_eq!(enumerate_graphemes("你好"), ["你", "好"]);
    assert_eq!(enumerate_graphemes("e\u{301}"), ["e\u{301}"]);
    assert_eq!(enumerate_graphemes(""), Vec::<&str>::new());
}

#[test]
fn native_text_layout_segments_latin_space_hard_break_and_soft_hyphen() {
    let segmented = segment_text("Hello world");
    assert_eq!(texts(&segmented), ["Hello", " ", "world"]);
    assert_eq!(
        segmented.kinds,
        [SegmentKind::Word, SegmentKind::Space, SegmentKind::Word]
    );

    let collapsed = segment_text("Hello   world");
    assert_eq!(texts(&collapsed), ["Hello", " ", "world"]);

    let preserved = segment_text_with_options("Hello   world", false);
    assert_eq!(texts(&preserved), ["Hello", "   ", "world"]);
    assert_eq!(
        preserved.kinds,
        [SegmentKind::Word, SegmentKind::Space, SegmentKind::Word]
    );

    let hard_break = segment_text("line1\nline2");
    assert_eq!(texts(&hard_break), ["line1", "\n", "line2"]);
    assert_eq!(
        hard_break.kinds,
        [SegmentKind::Word, SegmentKind::HardBreak, SegmentKind::Word]
    );

    let trimmed = segment_text("Hello \nworld");
    assert_eq!(texts(&trimmed), ["Hello", "\n", "world"]);

    let soft_hyphen = segment_text("hel\u{00AD}lo");
    assert_eq!(texts(&soft_hyphen), ["hel", "\u{00AD}", "lo"]);
    assert_eq!(
        soft_hyphen.kinds,
        [
            SegmentKind::Word,
            SegmentKind::SoftHyphen,
            SegmentKind::Word
        ]
    );
}

#[test]
fn native_text_layout_normalizes_whitespace_like_polyglot_text_segmenter() {
    assert_eq!(
        normalize_whitespace("  Hello   world  \n  next  "),
        " Hello world\n next "
    );
    assert_eq!(
        normalize_whitespace("Hello\tworld\rnext"),
        "Hello world next"
    );
}

#[test]
fn native_text_layout_segments_mixed_latin_cjk_and_cjk_scripts_per_character() {
    let mixed = segment_text("Hello你好world");
    assert_eq!(texts(&mixed), ["Hello", "你", "好", "world"]);
    assert_eq!(
        mixed.kinds,
        [
            SegmentKind::Word,
            SegmentKind::CjkGrapheme,
            SegmentKind::CjkGrapheme,
            SegmentKind::Word
        ]
    );

    for input in [
        "你好世界",
        "こんにちは",
        "カタカナ",
        "한국어",
        "\u{20000}\u{20001}",
    ] {
        let segmented = segment_text(input);
        assert!(segmented
            .kinds
            .iter()
            .all(|kind| *kind == SegmentKind::CjkGrapheme));
        assert_eq!(segmented.segments.len(), input.chars().count());
    }
}

#[test]
fn native_text_layout_preserves_closing_punctuation_grouping_rules() {
    let latin_close = segment_text("Hello.");
    assert_eq!(texts(&latin_close), ["Hello."]);
    assert_eq!(latin_close.kinds, [SegmentKind::Word]);

    let close_after_space = segment_text("Hello .");
    assert_eq!(texts(&close_after_space), ["Hello", " ", "."]);
    assert_eq!(
        close_after_space.kinds,
        [
            SegmentKind::Word,
            SegmentKind::Space,
            SegmentKind::ClosePunctuation
        ]
    );

    let open_then_word = segment_text("(Hello)");
    assert_eq!(texts(&open_then_word), ["(", "Hello)"]);
    assert_eq!(
        open_then_word.kinds,
        [SegmentKind::OpenPunctuation, SegmentKind::Word]
    );

    let multiple_close = segment_text("Hello!!");
    assert_eq!(texts(&multiple_close), ["Hello!!"]);
    assert_eq!(multiple_close.kinds, [SegmentKind::Word]);

    let cjk_close = segment_text("你好\u{3002}");
    assert_eq!(texts(&cjk_close), ["你", "好", "\u{3002}"]);
    assert_eq!(
        cjk_close.kinds,
        [
            SegmentKind::CjkGrapheme,
            SegmentKind::CjkGrapheme,
            SegmentKind::ClosePunctuation
        ]
    );

    let cjk_brackets = segment_text("\u{300C}你好\u{300D}");
    assert_eq!(texts(&cjk_brackets), ["\u{300C}", "你", "好", "\u{300D}"]);
    assert_eq!(
        cjk_brackets.kinds,
        [
            SegmentKind::OpenPunctuation,
            SegmentKind::CjkGrapheme,
            SegmentKind::CjkGrapheme,
            SegmentKind::ClosePunctuation
        ]
    );
}

#[test]
fn native_text_layout_exposes_kinsoku_tables() {
    for ch in [
        '\u{3001}', '\u{3002}', '\u{300D}', '\u{300F}', '\u{3011}', '\u{FF09}', '\u{FF01}',
        '\u{FF1F}', '\u{30FC}', '\u{30FB}',
    ] {
        assert!(is_prohibited_line_start(ch));
        assert!(KinsokuTable::is_prohibited_line_start(ch));
    }

    for ch in [
        '\u{3041}', '\u{3043}', '\u{3045}', '\u{3063}', '\u{30A1}', '\u{30C3}', '\u{30E3}',
    ] {
        assert!(is_prohibited_line_start(ch));
    }

    for ch in ['\u{309D}', '\u{309E}', '\u{30FD}', '\u{30FE}', '\u{3005}'] {
        assert!(is_prohibited_line_start(ch));
    }

    for ch in ['\u{4E00}', '\u{3042}', '\u{30AB}', 'A', ' '] {
        assert!(!is_prohibited_line_start(ch));
    }

    for ch in [
        '\u{FF08}', '\u{300C}', '\u{300E}', '\u{3010}', '\u{3008}', '\u{300A}',
    ] {
        assert!(is_prohibited_line_end(ch));
        assert!(KinsokuTable::is_prohibited_line_end(ch));
    }

    for ch in ['\u{4E00}', '\u{3002}', 'A'] {
        assert!(!is_prohibited_line_end(ch));
    }

    for ch in ['.', ',', '!', '?', ')', '%'] {
        assert!(is_left_sticky(ch));
        assert!(KinsokuTable::is_left_sticky(ch));
    }

    for ch in ['A', '1', ' '] {
        assert!(!is_left_sticky(ch));
    }
}

#[test]
fn native_text_layout_prepares_widths_and_grapheme_prefix_sums() {
    let measurer = FixedWidthMeasurer::default();
    let prepared = prepare_paragraph("Hello 你好", &measurer);

    assert_eq!(prepared.count(), 4);
    assert!(prepared.is_single_chunk());
    assert_eq!(prepared.segments, ["Hello", " ", "你", "好"]);
    assert_eq!(
        prepared.kinds,
        [
            SegmentKind::Word,
            SegmentKind::Space,
            SegmentKind::CjkGrapheme,
            SegmentKind::CjkGrapheme
        ]
    );
    assert_eq!(prepared.widths, [30.0, 3.0, 10.0, 10.0]);
    assert_eq!(prepared.line_end_fit_advances, [30.0, 0.0, 10.0, 10.0]);
    assert_eq!(
        prepared.graphemes[0],
        Some(vec![
            "H".to_string(),
            "e".to_string(),
            "l".to_string(),
            "l".to_string(),
            "o".to_string()
        ])
    );
    assert_eq!(prepared.grapheme_widths[0], Some(vec![6.0; 5]));
    assert_eq!(
        prepared.grapheme_prefix_sums[0],
        Some(vec![6.0, 12.0, 18.0, 24.0, 30.0])
    );
    assert_eq!(prepared.grapheme_widths[1], None);
    assert_eq!(prepared.grapheme_widths[2], None);
}

#[test]
fn native_text_layout_prepares_soft_hyphen_and_hard_break_metadata() {
    let measurer = FixedWidthMeasurer::default();
    let prepared = prepare_paragraph("hel\u{00AD}lo\nworld", &measurer);

    assert_eq!(prepared.segments, ["hel", "\u{00AD}", "lo", "\n", "world"]);
    assert_eq!(
        prepared.kinds,
        [
            SegmentKind::Word,
            SegmentKind::SoftHyphen,
            SegmentKind::Word,
            SegmentKind::HardBreak,
            SegmentKind::Word
        ]
    );
    assert_eq!(prepared.widths, [18.0, 0.0, 12.0, 0.0, 30.0]);
    assert_eq!(prepared.line_end_fit_advances, [18.0, 6.0, 12.0, 0.0, 30.0]);
    assert_eq!(prepared.discretionary_hyphen_width, 6.0);
    assert_eq!(prepared.hard_break_indices, [3]);
    assert!(!prepared.is_single_chunk());

    let no_soft_hyphen = prepare_paragraph("Hello", &measurer);
    assert_eq!(no_soft_hyphen.discretionary_hyphen_width, 0.0);
}

#[test]
fn native_text_layout_prepares_kinsoku_flags_and_respects_prepare_options() {
    let measurer = FixedWidthMeasurer::default();
    let prepared = prepare_paragraph("\u{300C}あぁ\u{300D}", &measurer);

    assert_eq!(prepared.segments, ["\u{300C}", "あ", "ぁ", "\u{300D}"]);
    assert!(prepared.is_prohibited_line_end[0]);
    assert!(!prepared.is_prohibited_line_start[1]);
    assert!(prepared.is_prohibited_line_start[2]);
    assert!(prepared.is_prohibited_line_start[3]);
    assert!(!prepared.is_prohibited_line_end[1]);

    let preserved = prepare_paragraph_with_options(
        "a   b",
        &measurer,
        TextPrepareOptions {
            normalize_whitespace: false,
        },
    );
    assert_eq!(preserved.segments, ["a", "   ", "b"]);
    assert_eq!(preserved.widths, [6.0, 9.0, 6.0]);
    assert_eq!(preserved.line_end_fit_advances, [6.0, 0.0, 6.0]);
}

#[test]
fn native_text_layout_fixed_width_greedy_layout_wraps_and_trims_like_polyglot() {
    let measurer = FixedWidthMeasurer::default();

    let empty = layout_paragraph_with_lines(&prepare_paragraph("", &measurer), 100.0);
    assert!(empty.lines.is_empty());

    let single = layout_paragraph_with_lines(&prepare_paragraph("Hello", &measurer), 50.0);
    assert_eq!(line_texts(&single), ["Hello"]);
    assert_eq!(single.lines[0].width, 30.0);

    let two_words = layout_paragraph_with_lines(&prepare_paragraph("Hello world", &measurer), 40.0);
    assert_eq!(line_texts(&two_words), ["Hello", "world"]);
    assert_eq!(two_words.max_line_width, 30.0);

    let leading_trim =
        layout_paragraph_with_lines(&prepare_paragraph("Hello world", &measurer), 35.0);
    assert_eq!(line_texts(&leading_trim), ["Hello", "world"]);
    assert!(!leading_trim.lines[1].text.starts_with(' '));

    let trailing_trim = layout_paragraph_with_lines(&prepare_paragraph("Hello ", &measurer), 50.0);
    assert_eq!(line_texts(&trailing_trim), ["Hello"]);

    let hard_break =
        layout_paragraph_with_lines(&prepare_paragraph("Hello\n\nworld", &measurer), 100.0);
    assert_eq!(line_texts(&hard_break), ["Hello", "", "world"]);

    let count = layout_paragraph(
        &prepare_paragraph("Hello world this is a test", &measurer),
        40.0,
    );
    let full = layout_paragraph_with_lines(
        &prepare_paragraph("Hello world this is a test", &measurer),
        40.0,
    );
    assert_eq!(count.line_count, full.lines.len());
    assert_eq!(count.max_line_width, full.max_line_width);
    assert!(!count.has_overflow);
}

#[test]
fn native_text_layout_lays_out_cjk_kinsoku_and_left_sticky_punctuation() {
    let measurer = FixedWidthMeasurer::default();

    let cjk = layout_paragraph_with_lines(&prepare_paragraph("你好世界", &measurer), 25.0);
    assert_eq!(line_texts(&cjk), ["你好", "世界"]);

    let mixed = layout_paragraph_with_lines(&prepare_paragraph("Hello你好", &measurer), 35.0);
    assert_eq!(line_texts(&mixed), ["Hello", "你好"]);

    let comma = layout_paragraph_with_lines(&prepare_paragraph("你好世界、", &measurer), 25.0);
    assert_eq!(line_texts(&comma), ["你好", "世界、"]);

    let small_kana = layout_paragraph_with_lines(&prepare_paragraph("あいっう", &measurer), 15.0);
    assert!(!small_kana
        .lines
        .iter()
        .any(|line| line.text.starts_with("っ")));

    let left_sticky = layout_paragraph_with_lines(&prepare_paragraph("你好世界.", &measurer), 25.0);
    assert_eq!(line_texts(&left_sticky), ["你好", "世界."]);
}

#[test]
fn native_text_layout_supports_variable_widths_walk_ranges_and_incremental_lines() {
    let measurer = FixedWidthMeasurer::default();
    let prepared = prepare_paragraph("Hello world test", &measurer);

    let variable = layout_paragraph_with_lines_and_widths(&prepared, &[35.0, 35.0, 100.0]);
    assert_eq!(line_texts(&variable), ["Hello", "world", "test"]);

    let variable_count = layout_paragraph_with_widths(&prepared, &[35.0, 35.0, 100.0]);
    assert_eq!(variable_count.line_count, variable.lines.len());
    assert_eq!(variable_count.max_line_width, variable.max_line_width);

    let empty_widths = layout_paragraph_with_lines_and_widths(&prepared, &[]);
    assert!(empty_widths.lines.is_empty());

    let mut ranges = Vec::new();
    let walked = walk_line_ranges(&prepared, 35.0, |range| ranges.push(range));
    assert_eq!(walked, 3);
    assert_eq!(ranges.len(), variable.lines.len());
    assert_eq!(ranges[0].width, 30.0);

    let mut cursor = LayoutCursor::START;
    let mut incremental = Vec::new();
    while cursor.segment_index < prepared.count() {
        let line = layout_next_line(&prepared, cursor, 35.0).expect("next line should exist");
        cursor = LayoutCursor {
            segment_index: line.end_segment,
            grapheme_index: line.end_grapheme,
        };
        incremental.push(line.text);
    }
    assert_eq!(incremental, ["Hello", "world", "test"]);

    assert!(layout_next_line(
        &prepared,
        LayoutCursor {
            segment_index: prepared.count(),
            grapheme_index: 0
        },
        100.0
    )
    .is_none());
}

#[test]
fn native_text_layout_breaks_long_segments_at_grapheme_boundaries() {
    let measurer = FixedWidthMeasurer::default();
    let long = layout_paragraph_with_lines(
        &prepare_paragraph("Supercalifragilistic ok", &measurer),
        40.0,
    );

    assert!(long.lines.len() > 1);
    assert!(long.lines[0].text.len() <= 7);
    assert_eq!(
        long.lines
            .iter()
            .map(|line| line.text.as_str())
            .collect::<String>(),
        "Supercalifragilisticok"
    );
    assert_eq!(long.lines.last().map(|line| line.text.as_str()), Some("ok"));

    let url = "https://example.com/very/long/path/to/resource";
    let url_lines = layout_paragraph_with_lines(&prepare_paragraph(url, &measurer), 60.0);
    assert!(url_lines.lines.len() > 1);
    assert_eq!(
        url_lines
            .lines
            .iter()
            .map(|line| line.text.as_str())
            .collect::<String>(),
        url
    );

    let wide_cjk = FixedWidthMeasurer {
        cjk_width: 100.0,
        ..FixedWidthMeasurer::default()
    };
    let single = layout_paragraph_with_lines(&prepare_paragraph("你", &wide_cjk), 50.0);
    assert_eq!(line_texts(&single), ["你"]);
    assert_eq!(single.lines[0].width, 100.0);
}

#[test]
fn native_text_layout_font_fit_keeps_original_size_when_text_fits() {
    let request = FontFitRequest {
        max_width: Some(100.0),
        max_height: Some(20.0),
        ..FontFitRequest::new("Hi", 12.0)
    };

    let result = solve_font_fit(&request, scaling_measurer);

    assert!(!result.was_shrunk);
    assert_eq!(result.chosen_font_size, 12.0);
    assert!(!result.was_truncated);
    assert_eq!(result.line_count, 1);
}

#[test]
fn native_text_layout_font_fit_shrinks_with_binary_search() {
    let request = FontFitRequest {
        max_width: Some(40.0),
        max_height: Some(14.0),
        ..FontFitRequest::new("Hello world", 12.0)
    };

    let result = solve_font_fit(&request, scaling_measurer);

    assert!(result.was_shrunk);
    assert!(result.chosen_font_size < 12.0);
    assert!(result.chosen_font_size >= 6.0);
    assert!(!result.was_truncated);
}

#[test]
fn native_text_layout_font_fit_flags_truncated_when_min_size_still_does_not_fit() {
    let request = FontFitRequest {
        min_font_size: 6.0,
        max_width: Some(20.0),
        max_height: Some(10.0),
        ..FontFitRequest::new(
            "This is a very long text that cannot possibly fit in a tiny box",
            12.0,
        )
    };

    let result = solve_font_fit(&request, scaling_measurer);

    assert!(result.was_truncated);
    assert_eq!(result.chosen_font_size, 6.0);
}

#[test]
fn native_text_layout_font_fit_respects_line_rect_constraints() {
    let request = FontFitRequest {
        line_widths: Some(vec![40.0, 40.0, 40.0]),
        ..FontFitRequest::new("Hello world test", 12.0)
    };

    let result = solve_font_fit(&request, scaling_measurer);

    assert!(result.line_count <= 3);

    let no_ceiling = FontFitRequest {
        line_widths: Some(vec![100.0, 100.0]),
        max_line_count: Some(2),
        max_height: Some(30.0),
        ..FontFitRequest::new("Hello world", 12.0)
    };

    let no_ceiling_result = solve_font_fit(&no_ceiling, scaling_measurer);
    assert!(!no_ceiling_result.was_shrunk);
    assert_eq!(no_ceiling_result.chosen_font_size, 12.0);
    assert!(!no_ceiling_result.was_truncated);

    let height_ceiling = FontFitRequest {
        line_widths: Some(vec![100.0, 100.0]),
        line_heights: Some(vec![8.0, 8.0]),
        ..FontFitRequest::new("Hello world", 12.0)
    };

    let height_ceiling_result = solve_font_fit(&height_ceiling, scaling_measurer);
    assert!(height_ceiling_result.chosen_font_size <= 8.0);
    assert!(height_ceiling_result.was_shrunk);
}

#[test]
fn native_text_layout_font_fit_converges_and_matches_followup_layout() {
    let request = FontFitRequest {
        max_width: Some(60.0),
        max_height: Some(40.0),
        ..FontFitRequest::new("Hello world this is a test", 14.0)
    };

    let result = solve_font_fit(&request, scaling_measurer);

    assert!((6.0..=14.0).contains(&result.chosen_font_size));
    assert!((result.chosen_line_height - result.chosen_font_size * 1.2).abs() <= 0.01);

    let measurer = scaling_measurer(result.chosen_font_size);
    let prepared = prepare_paragraph(&request.text, &measurer);
    let layout = layout_paragraph_with_lines(&prepared, request.max_width.unwrap());

    assert_eq!(layout.lines.len(), result.line_count);
    if !result.was_truncated {
        let total_height = layout.lines.len() as f64 * result.chosen_line_height;
        assert!(total_height <= request.max_height.unwrap() + 0.5);
    }
}
