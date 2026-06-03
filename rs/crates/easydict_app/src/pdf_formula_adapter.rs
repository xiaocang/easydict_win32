use crate::character_paragraph::{
    build_character_level_protection, strip_subset_prefix, CharInfo, CharacterLevelProtection,
    TextMatrix,
};
use crate::content_preservation::{
    is_font_based_formula, BlockFormulaCharacters, FormulaCharacterInfo,
};
use crate::formula_text_reconstruction::{
    is_reconstruction_quality_acceptable, reconstruct_formula_aware_text,
    should_use_letter_based_block_text, LetterGeometry,
};

pub const PDF_BLOCK_GLYPH_TOLERANCE_PT: f64 = 1.0;
pub const PDF_LETTER_VERTICAL_TOLERANCE_PT: f64 = 1.5;

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PdfBlockTolerance {
    pub horizontal: f64,
    pub vertical: f64,
}

impl PdfBlockTolerance {
    pub const fn new(horizontal: f64, vertical: f64) -> Self {
        Self {
            horizontal,
            vertical,
        }
    }

    pub const fn uniform(tolerance: f64) -> Self {
        Self {
            horizontal: tolerance,
            vertical: tolerance,
        }
    }

    pub const fn character_level() -> Self {
        Self::uniform(PDF_BLOCK_GLYPH_TOLERANCE_PT)
    }

    pub const fn letter_geometry() -> Self {
        Self::new(
            PDF_BLOCK_GLYPH_TOLERANCE_PT,
            PDF_LETTER_VERTICAL_TOLERANCE_PT,
        )
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PdfBlockBounds {
    pub left: f64,
    pub right: f64,
    pub top: f64,
    pub bottom: f64,
}

impl PdfBlockBounds {
    pub const fn from_lrtb(left: f64, right: f64, top: f64, bottom: f64) -> Self {
        Self {
            left,
            right,
            top,
            bottom,
        }
    }

    pub fn contains_glyph(&self, bounds: &PdfGlyphBounds, tolerance: PdfBlockTolerance) -> bool {
        bounds.left >= self.left - tolerance.horizontal
            && bounds.right <= self.right + tolerance.horizontal
            && bounds.bottom >= self.bottom - tolerance.vertical
            && bounds.top <= self.top + tolerance.vertical
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PdfGlyphBounds {
    pub left: f64,
    pub bottom: f64,
    pub right: f64,
    pub top: f64,
}

impl PdfGlyphBounds {
    pub const fn from_lbrt(left: f64, bottom: f64, right: f64, top: f64) -> Self {
        Self {
            left,
            bottom,
            right,
            top,
        }
    }

    pub fn width(&self) -> f64 {
        (self.right - self.left).max(0.0)
    }

    pub fn height(&self) -> f64 {
        (self.top - self.bottom).max(0.0)
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PdfTextOrientation {
    Horizontal,
    Rotate90,
    Rotate180,
    Rotate270,
    Other,
}

impl PdfTextOrientation {
    pub fn is_horizontal(self) -> bool {
        self == Self::Horizontal
    }

    pub fn is_vertical(self) -> bool {
        matches!(self, Self::Rotate90 | Self::Rotate270)
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct PdfGlyph {
    pub value: String,
    pub bounds: PdfGlyphBounds,
    pub baseline_y: f64,
    pub point_size: f64,
    pub font_name: String,
    pub character_code: u32,
    pub cid: u32,
    pub orientation: PdfTextOrientation,
    pub text_matrix: Option<TextMatrix>,
    pub current_transformation_matrix: Option<TextMatrix>,
}

impl PdfGlyph {
    pub fn new(
        value: impl Into<String>,
        bounds: PdfGlyphBounds,
        point_size: f64,
        font_name: impl Into<String>,
    ) -> Self {
        let value = value.into();
        let character_code = value.chars().next().map(|ch| ch as u32).unwrap_or(0);
        Self {
            value,
            bounds,
            baseline_y: bounds.bottom,
            point_size,
            font_name: font_name.into(),
            character_code,
            cid: character_code,
            orientation: PdfTextOrientation::Horizontal,
            text_matrix: None,
            current_transformation_matrix: None,
        }
    }

    pub fn with_baseline_y(mut self, baseline_y: f64) -> Self {
        self.baseline_y = baseline_y;
        self
    }

    pub fn with_orientation(mut self, orientation: PdfTextOrientation) -> Self {
        self.orientation = orientation;
        self
    }

    pub fn with_text_matrix(mut self, matrix: TextMatrix) -> Self {
        self.text_matrix = Some(matrix);
        self
    }

    pub fn with_current_transformation_matrix(mut self, matrix: TextMatrix) -> Self {
        self.current_transformation_matrix = Some(matrix);
        self
    }

    pub fn with_codes(mut self, character_code: u32, cid: u32) -> Self {
        self.character_code = character_code;
        self.cid = cid;
        self
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct PdfBlockFormulaEvidence {
    pub char_infos: Vec<CharInfo>,
    pub letter_geometries: Vec<LetterGeometry>,
    pub formula_characters: Option<BlockFormulaCharacters>,
    pub character_level_protection: Option<CharacterLevelProtection>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct PdfFormulaAwareBlockText {
    pub block_text: String,
    pub fallback_text: Option<String>,
    pub evidence: PdfBlockFormulaEvidence,
}

pub fn glyphs_in_block<'a>(
    glyphs: &'a [PdfGlyph],
    bounds: PdfBlockBounds,
    tolerance: PdfBlockTolerance,
) -> Vec<&'a PdfGlyph> {
    glyphs
        .iter()
        .filter(|glyph| bounds.contains_glyph(&glyph.bounds, tolerance))
        .collect()
}

pub fn char_info_from_pdf_glyph(glyph: &PdfGlyph) -> CharInfo {
    let font_name = strip_subset_prefix(&glyph.font_name).to_string();
    CharInfo {
        text: glyph.value.clone(),
        character_code: glyph.character_code,
        cid: glyph.cid,
        font_name,
        font_size: glyph.point_size,
        point_size: glyph.point_size,
        text_matrix: glyph
            .text_matrix
            .unwrap_or_else(|| text_matrix_for_orientation(glyph.orientation, glyph.bounds)),
        current_transformation_matrix: glyph
            .current_transformation_matrix
            .unwrap_or(TextMatrix::IDENTITY),
        x0: glyph.bounds.left,
        y0: glyph.bounds.bottom,
        x1: glyph.bounds.right,
        y1: glyph.bounds.top,
    }
}

pub fn char_infos_for_pdf_block(glyphs: &[PdfGlyph], bounds: PdfBlockBounds) -> Vec<CharInfo> {
    glyphs_in_block(glyphs, bounds, PdfBlockTolerance::character_level())
        .into_iter()
        .map(char_info_from_pdf_glyph)
        .collect()
}

pub fn letter_geometry_from_pdf_glyph(glyph: &PdfGlyph) -> Option<LetterGeometry> {
    glyph.orientation.is_horizontal().then(|| {
        LetterGeometry::new(
            &glyph.value,
            glyph.bounds.left,
            glyph.bounds.right,
            glyph.bounds.bottom,
            glyph.bounds.top,
            glyph.baseline_y,
            glyph.point_size,
            strip_subset_prefix(&glyph.font_name),
        )
    })
}

pub fn letter_geometries_for_pdf_block(
    glyphs: &[PdfGlyph],
    bounds: PdfBlockBounds,
) -> Vec<LetterGeometry> {
    glyphs_in_block(glyphs, bounds, PdfBlockTolerance::letter_geometry())
        .into_iter()
        .filter_map(letter_geometry_from_pdf_glyph)
        .collect()
}

pub fn block_formula_characters_for_pdf_block(
    glyphs: &[PdfGlyph],
    bounds: PdfBlockBounds,
) -> Option<BlockFormulaCharacters> {
    let glyphs = glyphs_in_block(glyphs, bounds, PdfBlockTolerance::character_level());
    if glyphs.is_empty() {
        return None;
    }

    let median_point_size = median_positive(glyphs.iter().map(|glyph| glyph.point_size), 0.0);
    let median_baseline_y = median_positive(glyphs.iter().map(|glyph| glyph.bounds.bottom), 0.0);
    let script_size_threshold = median_point_size * 0.8;

    let mut has_math_font_characters = false;
    let characters = glyphs
        .into_iter()
        .map(|glyph| {
            let font_name = strip_subset_prefix(&glyph.font_name).to_string();
            let is_math_font = pdf_font_name_is_math(&font_name);
            has_math_font_characters |= is_math_font;

            let is_small = script_size_threshold > 0.0
                && glyph.point_size > 0.0
                && glyph.point_size < script_size_threshold;
            let is_subscript = is_small && glyph.bounds.bottom < median_baseline_y - 0.5;
            let is_superscript = is_small && glyph.bounds.bottom > median_baseline_y + 0.5;

            FormulaCharacterInfo {
                value: glyph.value.clone(),
                font_name,
                is_math_font,
                is_subscript,
                is_superscript,
            }
        })
        .collect::<Vec<_>>();

    has_math_font_characters.then_some(BlockFormulaCharacters {
        characters,
        has_math_font_characters,
    })
}

pub fn build_pdf_block_formula_evidence(
    glyphs: &[PdfGlyph],
    bounds: PdfBlockBounds,
) -> PdfBlockFormulaEvidence {
    let char_infos = char_infos_for_pdf_block(glyphs, bounds);
    let character_level_protection = build_character_level_protection(&char_infos);

    PdfBlockFormulaEvidence {
        char_infos,
        letter_geometries: letter_geometries_for_pdf_block(glyphs, bounds),
        formula_characters: block_formula_characters_for_pdf_block(glyphs, bounds),
        character_level_protection,
    }
}

pub fn build_formula_aware_pdf_block_text<S: AsRef<str>>(
    line_texts: &[S],
    glyphs: &[PdfGlyph],
    bounds: PdfBlockBounds,
) -> PdfFormulaAwareBlockText {
    let evidence = build_pdf_block_formula_evidence(glyphs, bounds);
    let fallback_text = line_texts
        .iter()
        .map(|line| line.as_ref())
        .collect::<Vec<_>>()
        .join("\n")
        .trim()
        .to_string();

    let character_level_protected_text = evidence
        .character_level_protection
        .as_ref()
        .map(|protection| protection.protected_text.as_str());
    if !should_use_letter_based_block_text(
        line_texts,
        evidence.formula_characters.as_ref(),
        character_level_protected_text,
    ) {
        return PdfFormulaAwareBlockText {
            block_text: fallback_text,
            fallback_text: None,
            evidence,
        };
    }

    if evidence.letter_geometries.is_empty() {
        return PdfFormulaAwareBlockText {
            block_text: fallback_text,
            fallback_text: None,
            evidence,
        };
    }

    for word_gap_scale in [1.0, 0.5] {
        let reconstructed =
            reconstruct_formula_aware_text(&evidence.letter_geometries, word_gap_scale);
        if !reconstructed.trim().is_empty()
            && is_reconstruction_quality_acceptable(&reconstructed, &fallback_text)
        {
            let secondary_fallback = (reconstructed != fallback_text).then_some(fallback_text);
            return PdfFormulaAwareBlockText {
                block_text: reconstructed,
                fallback_text: secondary_fallback,
                evidence,
            };
        }
    }

    PdfFormulaAwareBlockText {
        block_text: fallback_text,
        fallback_text: None,
        evidence,
    }
}

fn text_matrix_for_orientation(
    orientation: PdfTextOrientation,
    bounds: PdfGlyphBounds,
) -> TextMatrix {
    if orientation.is_vertical() {
        TextMatrix::from_values(0.0, 1.0, -1.0, 0.0, bounds.left, bounds.bottom)
    } else {
        TextMatrix::IDENTITY
    }
}

fn pdf_font_name_is_math(font_name: &str) -> bool {
    let font_names = [font_name.to_string()];
    is_font_based_formula(Some(&font_names), None)
}

fn median_positive(values: impl Iterator<Item = f64>, default_value: f64) -> f64 {
    let mut values = values
        .filter(|value| value.is_finite() && *value > 0.0)
        .collect::<Vec<_>>();
    if values.is_empty() {
        return default_value;
    }
    values.sort_by(f64::total_cmp);
    values[values.len() / 2]
}
