use easydict_app::{
    build_final_erase_rects_top_left, expand_line_rects_for_cell, expand_line_widths,
    handle_inline_script_lines_for_overlay, looks_like_grid_line_positions,
    looks_like_inline_script_line, needs_math_font, parse_formula_fragments,
    rects_belong_to_same_erase_band, resolve_available_height, segment_line_by_font,
    should_apply_formula_hole, split_line_rects_for_inline_script_protection,
    try_apply_inline_subscript_attachments, try_build_line_rects, try_convert_to_unicode_subscript,
    BlockLinePosition, BlockTextStyle, FontSegment, FormulaFragment, FormulaFragmentKind,
    InlineSubscriptAttachment, PdfRect,
};

fn style(
    line_spacing: Option<f64>,
    font_size: Option<f64>,
    positions: Vec<BlockLinePosition>,
) -> BlockTextStyle {
    BlockTextStyle {
        line_spacing,
        font_size,
        line_positions: positions,
    }
}

#[test]
fn native_document_layout_builds_line_rects_top_to_bottom_and_clamps_to_block() {
    let style = style(
        Some(20.0),
        None,
        vec![
            BlockLinePosition::new(700.0, 90.0, 300.0),
            BlockLinePosition::new(680.0, 80.0, 280.0),
            BlockLinePosition::new(660.0, 100.0, 320.0),
        ],
    );
    let block = PdfRect::new(90.0, 50.0, 200.0, 120.0);

    let rects = try_build_line_rects(800.0, block, Some(&style), 14.0).expect("line rects");

    assert_eq!(rects.len(), 3);
    assert!(rects[0].y < rects[1].y);
    assert!(rects[1].y < rects[2].y);

    for rect in rects {
        assert!(rect.x >= block.x);
        assert!(rect.right() <= block.right() + 0.0001);
        assert!(rect.y >= block.y);
        assert!(rect.bottom() <= block.bottom() + 0.0001);
        assert!(rect.width > 0.0);
        assert!(rect.height > 0.0);
    }
}

#[test]
fn native_document_layout_single_baseline_creates_virtual_line_rects() {
    let style = style(
        None,
        None,
        vec![BlockLinePosition::new(700.0, 120.0, 320.0)],
    );
    let block = PdfRect::new(90.0, 50.0, 300.0, 120.0);

    let rects = try_build_line_rects(800.0, block, Some(&style), 14.0).expect("line rects");

    assert!(!rects.is_empty());
    assert_eq!(rects[0].y, block.y);
    assert!((rects.last().unwrap().bottom() - block.bottom()).abs() <= 0.0001);
    for pair in rects.windows(2) {
        assert!(pair[0].y < pair[1].y);
    }
    for rect in rects {
        assert!(rect.x >= block.x);
        assert!(rect.right() <= block.right());
        assert!(rect.height > 0.0);
    }
}

#[test]
fn native_document_layout_duplicate_baselines_are_grid_like_and_return_none() {
    let positions = vec![
        BlockLinePosition::new(700.0, 100.0, 200.0),
        BlockLinePosition::new(700.2, 220.0, 320.0),
    ];
    let style = style(None, None, positions.clone());

    assert!(looks_like_grid_line_positions(&positions));
    assert!(try_build_line_rects(
        800.0,
        PdfRect::new(0.0, 0.0, 400.0, 400.0),
        Some(&style),
        14.0
    )
    .is_none());
}

#[test]
fn native_document_layout_distinct_or_single_baselines_are_not_grid_like() {
    assert!(!looks_like_grid_line_positions(&[BlockLinePosition::new(
        700.0, 100.0, 300.0
    )]));
    assert!(!looks_like_grid_line_positions(&[
        BlockLinePosition::new(700.0, 100.0, 300.0),
        BlockLinePosition::new(680.0, 100.0, 300.0),
    ]));
}

#[test]
fn native_document_layout_expands_line_rects_for_cell_like_regions() {
    let block = PdfRect::new(10.0, 20.0, 120.0, 90.0);
    let original = vec![PdfRect::new(10.0, 20.0, 120.0, 20.0)];

    let expanded =
        expand_line_rects_for_cell(Some(&original), block, 15.0, true).expect("expanded");

    assert_eq!(expanded.len(), 6);
    assert_eq!(expanded[0].y, block.y);
    assert!((expanded.last().unwrap().bottom() - block.bottom()).abs() <= 0.0001);

    let unchanged =
        expand_line_rects_for_cell(Some(&original), block, 15.0, false).expect("unchanged");
    assert_eq!(unchanged, original);
    assert!(expand_line_rects_for_cell(None, block, 15.0, true).is_none());
}

#[test]
fn native_document_layout_resolves_available_height_from_line_rect_spans() {
    let block = PdfRect::new(10.0, 20.0, 100.0, 0.5);
    let render = vec![
        PdfRect::new(10.0, 30.0, 100.0, 10.0),
        PdfRect::new(10.0, 50.0, 100.0, 10.0),
    ];
    let background = vec![
        PdfRect::new(10.0, 25.0, 100.0, 5.0),
        PdfRect::new(10.0, 75.0, 100.0, 5.0),
    ];

    assert_eq!(
        resolve_available_height(block, Some(&render), Some(&background)),
        55.0
    );
    assert_eq!(resolve_available_height(block, Some(&render), None), 30.0);
    assert_eq!(resolve_available_height(block, None, None), 1.0);
}

#[test]
fn native_document_layout_expands_line_widths_like_pdf_export() {
    assert_eq!(expand_line_widths(&[], 0), vec![100.0]);
    assert_eq!(expand_line_widths(&[], 3), vec![100.0, 100.0, 100.0]);
    assert_eq!(
        expand_line_widths(&[50.0, 60.0], 4),
        vec![50.0, 60.0, 60.0, 60.0]
    );
    assert_eq!(expand_line_widths(&[50.0, 60.0, 70.0], 2), vec![50.0, 60.0]);
}

#[test]
fn native_document_layout_groups_erase_rects_by_horizontal_band_only() {
    assert!(rects_belong_to_same_erase_band(
        PdfRect::new(10.0, 10.0, 40.0, 10.0),
        PdfRect::new(45.0, 100.0, 40.0, 10.0)
    ));
    assert!(rects_belong_to_same_erase_band(
        PdfRect::new(10.0, 10.0, 40.0, 10.0),
        PdfRect::new(50.0, 100.0, 40.0, 10.0)
    ));
    assert!(rects_belong_to_same_erase_band(
        PdfRect::new(10.0, 10.0, 10.0, 10.0),
        PdfRect::new(24.0, 100.0, 10.0, 10.0)
    ));
    assert!(!rects_belong_to_same_erase_band(
        PdfRect::new(10.0, 10.0, 10.0, 10.0),
        PdfRect::new(24.1, 100.0, 10.0, 10.0)
    ));
    assert!(rects_belong_to_same_erase_band(
        PdfRect::new(10.0, 10.0, 200.0, 10.0),
        PdfRect::new(234.0, 100.0, 200.0, 10.0)
    ));
    assert!(!rects_belong_to_same_erase_band(
        PdfRect::new(10.0, 10.0, 200.0, 10.0),
        PdfRect::new(234.1, 100.0, 200.0, 10.0)
    ));
}

#[test]
fn native_document_layout_builds_final_erase_rects_by_merging_matching_bands() {
    let source = vec![
        PdfRect::new(10.0, 10.0, 20.0, 10.0),
        PdfRect::new(100.0, 20.0, 20.0, 10.0),
        PdfRect::new(0.0, 0.0, 0.1, 50.0),
    ];
    let render = vec![PdfRect::new(28.0, 60.0, 75.0, 10.0)];

    let rects = build_final_erase_rects_top_left(&source, &render);

    assert_eq!(rects, vec![PdfRect::new(10.0, 10.0, 110.0, 60.0)]);

    let separate = build_final_erase_rects_top_left(
        &[PdfRect::new(10.0, 10.0, 20.0, 10.0)],
        &[PdfRect::new(80.0, 5.0, 20.0, 10.0)],
    );

    assert_eq!(
        separate,
        vec![
            PdfRect::new(80.0, 5.0, 20.0, 10.0),
            PdfRect::new(10.0, 10.0, 20.0, 10.0),
        ]
    );
}

#[test]
fn native_document_layout_detects_inline_script_lines_like_pdf_export() {
    assert!(looks_like_inline_script_line("t , t\u{2212}1"));
    assert!(looks_like_inline_script_line("[35, 2, 5]"));
    assert!(!looks_like_inline_script_line("GPU"));
    assert!(!looks_like_inline_script_line("states h as a function"));
    assert!(!looks_like_inline_script_line("中文"));
    assert!(!looks_like_inline_script_line("this-is-too-wordy"));
}

#[test]
fn native_document_layout_split_line_rects_protects_only_short_small_script_line() {
    let line_rects = vec![
        PdfRect::new(100.0, 100.0, 200.0, 18.0),
        PdfRect::new(100.0, 121.0, 80.0, 8.0),
    ];

    let split = split_line_rects_for_inline_script_protection(
        "states h as a function\n t , t\u{2212}1 ",
        Some(&line_rects),
    );

    assert_eq!(split.protected_inline_rects, vec![line_rects[1]]);
    assert_eq!(split.render_line_rects, Some(vec![line_rects[0]]));

    let no_split = split_line_rects_for_inline_script_protection("single line", Some(&line_rects));
    assert!(no_split.protected_inline_rects.is_empty());
    assert_eq!(no_split.render_line_rects, Some(line_rects));

    let none = split_line_rects_for_inline_script_protection("anything", None);
    assert!(none.render_line_rects.is_none());
    assert!(none.protected_inline_rects.is_empty());
}

#[test]
fn native_document_layout_converts_inline_script_tokens_to_unicode_subscripts() {
    assert_eq!(try_convert_to_unicode_subscript("t").as_deref(), Some("ₜ"));
    assert_eq!(
        try_convert_to_unicode_subscript("t\u{2212}1").as_deref(),
        Some("ₜ₋₁")
    );
    assert_eq!(
        try_convert_to_unicode_subscript("i+1").as_deref(),
        Some("ᵢ₊₁")
    );
    assert!(try_convert_to_unicode_subscript("GPU").is_none());
}

#[test]
fn native_document_layout_applies_inline_subscripts_without_repeating_existing_subscripts() {
    let attachments = [InlineSubscriptAttachment {
        base_char: 'h',
        subscript: "ₜ".to_string(),
    }];

    let augmented =
        try_apply_inline_subscript_attachments("state hₜ and state h", &attachments).unwrap();

    assert_eq!(augmented, "state hₜ and state hₜ");
}

#[test]
fn native_document_layout_overlay_augments_subscripts_and_erases_original_script_rects() {
    let line_rects = vec![
        PdfRect::new(100.0, 100.0, 200.0, 18.0),
        PdfRect::new(120.0, 121.0, 80.0, 8.0),
    ];

    let result = handle_inline_script_lines_for_overlay(
        "states h and previous state h\n t , t\u{2212}1",
        "state h and previous state h.\n t , t\u{2212}1",
        Some(&line_rects),
    );

    assert!(result.translated_text.contains("hₜ"));
    assert!(result.translated_text.contains("hₜ₋₁"));
    assert!(result.protected_inline_rects.is_empty());
    assert_eq!(result.background_line_rects, Some(line_rects.clone()));
    assert_eq!(result.render_line_rects, Some(vec![line_rects[0]]));
}

#[test]
fn native_document_layout_overlay_folds_citation_lines_when_present_in_translation() {
    let line_rects = vec![
        PdfRect::new(100.0, 100.0, 200.0, 18.0),
        PdfRect::new(180.0, 92.0, 40.0, 8.0),
    ];

    let result = handle_inline_script_lines_for_overlay(
        "long short-term memory\n [13]",
        "long short-term memory\n [13]",
        Some(&line_rects),
    );

    assert!(result.translated_text.contains("[13]"));
    assert!(!result.translated_text.contains("\n [13]"));
    assert!(result.protected_inline_rects.is_empty());
    assert_eq!(result.background_line_rects, Some(line_rects.clone()));
    assert_eq!(result.render_line_rects, Some(vec![line_rects[0]]));
}

#[test]
fn native_document_layout_formula_holes_apply_only_without_any_overlap() {
    assert!(should_apply_formula_hole(
        PdfRect::new(100.0, 100.0, 50.0, 20.0),
        PdfRect::new(200.0, 200.0, 300.0, 100.0)
    ));
    assert!(!should_apply_formula_hole(
        PdfRect::new(120.0, 220.0, 40.0, 15.0),
        PdfRect::new(100.0, 200.0, 300.0, 100.0)
    ));
    assert!(!should_apply_formula_hole(
        PdfRect::new(50.0, 100.0, 60.0, 20.0),
        PdfRect::new(100.0, 100.0, 300.0, 100.0)
    ));
    assert!(should_apply_formula_hole(
        PdfRect::new(50.0, 100.0, 50.0, 20.0),
        PdfRect::new(100.0, 100.0, 300.0, 100.0)
    ));
}

#[test]
fn native_document_layout_detects_math_font_characters_like_pdf_export() {
    assert!(needs_math_font('\u{2200}'));
    assert!(needs_math_font('\u{2212}'));
    assert!(needs_math_font('\u{03B1}'));
    assert!(needs_math_font('\u{00D7}'));
    assert!(needs_math_font('\u{2192}'));
    assert!(needs_math_font('\u{2153}'));

    assert!(!needs_math_font('A'));
    assert!(!needs_math_font('中'));
    assert!(!needs_math_font('1'));
    assert!(!needs_math_font('+'));
    assert!(!needs_math_font('='));
}

#[test]
fn native_document_layout_segments_lines_by_math_font_need() {
    assert_eq!(
        segment_line_by_font("α+β=γ"),
        vec![
            FontSegment {
                text: "α".to_string(),
                needs_math_font: true,
            },
            FontSegment {
                text: "+".to_string(),
                needs_math_font: false,
            },
            FontSegment {
                text: "β".to_string(),
                needs_math_font: true,
            },
            FontSegment {
                text: "=".to_string(),
                needs_math_font: false,
            },
            FontSegment {
                text: "γ".to_string(),
                needs_math_font: true,
            },
        ]
    );
    assert_eq!(
        segment_line_by_font("Hello World"),
        vec![FontSegment {
            text: "Hello World".to_string(),
            needs_math_font: false,
        }]
    );
    assert_eq!(
        segment_line_by_font(""),
        vec![FontSegment {
            text: String::new(),
            needs_math_font: false,
        }]
    );
}

#[test]
fn native_document_layout_parses_formula_fragments_for_scripts() {
    assert_eq!(
        parse_formula_fragments("h_{t-1}"),
        vec![
            FormulaFragment {
                text: "h".to_string(),
                kind: FormulaFragmentKind::Normal,
            },
            FormulaFragment {
                text: "t-1".to_string(),
                kind: FormulaFragmentKind::Subscript,
            },
        ]
    );
    assert_eq!(
        parse_formula_fragments("x^2"),
        vec![
            FormulaFragment {
                text: "x".to_string(),
                kind: FormulaFragmentKind::Normal,
            },
            FormulaFragment {
                text: "2".to_string(),
                kind: FormulaFragmentKind::Superscript,
            },
        ]
    );
    assert_eq!(
        parse_formula_fragments("hello world"),
        vec![FormulaFragment {
            text: "hello world".to_string(),
            kind: FormulaFragmentKind::Normal,
        }]
    );
}

#[test]
fn native_document_layout_parses_grouped_formula_fragments_with_nested_braces() {
    assert_eq!(
        parse_formula_fragments("x_{a{b}}^n tail"),
        vec![
            FormulaFragment {
                text: "x".to_string(),
                kind: FormulaFragmentKind::Normal,
            },
            FormulaFragment {
                text: "a{b}".to_string(),
                kind: FormulaFragmentKind::Subscript,
            },
            FormulaFragment {
                text: "n".to_string(),
                kind: FormulaFragmentKind::Superscript,
            },
            FormulaFragment {
                text: " tail".to_string(),
                kind: FormulaFragmentKind::Normal,
            },
        ]
    );
    assert_eq!(
        parse_formula_fragments(""),
        vec![FormulaFragment {
            text: String::new(),
            kind: FormulaFragmentKind::Normal,
        }]
    );
}
