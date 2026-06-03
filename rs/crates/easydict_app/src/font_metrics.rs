use std::collections::HashMap;
use std::fmt;
use std::fs;
use std::path::Path;

use ttf_parser::{Face, FaceParsingError, GlyphId};

use crate::text_layout::{is_cjk, TextMeasurer};

pub const CJK_PRIMARY_ASCII_ADVANCE_EM: f64 = 0.55;
pub const SPACE_ADVANCE_EM: f64 = 0.3;
pub const DEFAULT_NON_CJK_ADVANCE_EM: f64 = 0.6;
pub const DEFAULT_UNITS_PER_EM: u16 = 1000;

#[derive(Clone, Debug, PartialEq)]
pub struct FontMetrics {
    pub glyph_map: HashMap<char, u16>,
    pub advance_widths: HashMap<u16, u16>,
    pub units_per_em: u16,
}

impl FontMetrics {
    pub fn new(
        glyph_map: HashMap<char, u16>,
        advance_widths: HashMap<u16, u16>,
        units_per_em: u16,
    ) -> Self {
        Self {
            glyph_map,
            advance_widths,
            units_per_em,
        }
    }

    pub fn glyph_id(&self, ch: char) -> Option<u16> {
        self.glyph_map.get(&ch).copied().filter(|gid| *gid != 0)
    }

    pub fn advance_width(&self, glyph_id: u16) -> Option<u16> {
        self.advance_widths
            .get(&glyph_id)
            .copied()
            .filter(|advance| *advance > 0)
    }

    pub fn advance_em(&self, glyph_id: u16, fallback_em: f64) -> f64 {
        glyph_advance_em(
            glyph_id,
            Some(&self.advance_widths),
            self.units_per_em,
            fallback_em,
        )
    }
}

#[derive(Debug)]
pub enum FontMetricsError {
    Io(std::io::Error),
    Parse(FaceParsingError),
}

impl fmt::Display for FontMetricsError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Io(error) => write!(f, "Failed to read font file: {error}"),
            Self::Parse(error) => write!(f, "Failed to parse font metrics: {error:?}"),
        }
    }
}

impl std::error::Error for FontMetricsError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            Self::Io(error) => Some(error),
            Self::Parse(_) => None,
        }
    }
}

impl From<std::io::Error> for FontMetricsError {
    fn from(value: std::io::Error) -> Self {
        Self::Io(value)
    }
}

impl From<FaceParsingError> for FontMetricsError {
    fn from(value: FaceParsingError) -> Self {
        Self::Parse(value)
    }
}

pub fn load_font_metrics(path: impl AsRef<Path>) -> Result<FontMetrics, FontMetricsError> {
    let bytes = fs::read(path)?;
    parse_font_metrics_from_bytes(&bytes)
}

pub fn parse_cmap_from_bytes(data: &[u8]) -> Result<HashMap<char, u16>, FontMetricsError> {
    parse_font_metrics_from_bytes(data).map(|metrics| metrics.glyph_map)
}

pub fn parse_font_metrics_from_bytes(data: &[u8]) -> Result<FontMetrics, FontMetricsError> {
    let face = Face::parse(data, 0)?;
    Ok(font_metrics_from_face(&face))
}

fn font_metrics_from_face(face: &Face<'_>) -> FontMetrics {
    let mut glyph_map = HashMap::new();
    let mut advance_widths = HashMap::new();

    for code_point in 0..=0xFFFF {
        let Some(ch) = char::from_u32(code_point) else {
            continue;
        };
        let Some(GlyphId(gid)) = face.glyph_index(ch) else {
            continue;
        };
        if gid == 0 {
            continue;
        }

        glyph_map.insert(ch, gid);
        if let Some(advance) = face
            .glyph_hor_advance(GlyphId(gid))
            .filter(|value| *value > 0)
        {
            advance_widths.insert(gid, advance);
        }
    }

    FontMetrics {
        glyph_map,
        advance_widths,
        units_per_em: face.units_per_em(),
    }
}

#[derive(Clone, Debug)]
pub struct GlyphAdvanceMeasurer {
    pub primary_metrics: Option<FontMetrics>,
    pub noto_metrics: Option<FontMetrics>,
    pub latin_metrics: Option<FontMetrics>,
    pub primary_font_is_cjk: bool,
    pub font_size: f64,
}

impl GlyphAdvanceMeasurer {
    pub fn new(font_size: f64) -> Self {
        Self {
            primary_metrics: None,
            noto_metrics: None,
            latin_metrics: None,
            primary_font_is_cjk: false,
            font_size,
        }
    }

    pub fn with_primary_metrics(mut self, metrics: FontMetrics) -> Self {
        self.primary_metrics = Some(metrics);
        self
    }

    pub fn with_noto_metrics(mut self, metrics: FontMetrics) -> Self {
        self.noto_metrics = Some(metrics);
        self
    }

    pub fn with_latin_metrics(mut self, metrics: FontMetrics) -> Self {
        self.latin_metrics = Some(metrics);
        self
    }

    pub fn with_primary_font_is_cjk(mut self, value: bool) -> Self {
        self.primary_font_is_cjk = value;
        self
    }

    pub fn measure_char(&self, ch: char) -> f64 {
        if is_script_signal(ch) {
            return 0.0;
        }

        if ch == ' ' {
            return self.measure_space();
        }

        if self.primary_font_is_cjk && ch.is_ascii_graphic() {
            if let Some(width) =
                self.measure_from_metrics(&self.latin_metrics, ch, CJK_PRIMARY_ASCII_ADVANCE_EM)
            {
                return width;
            }

            return self.font_size * CJK_PRIMARY_ASCII_ADVANCE_EM;
        }

        if is_cjk(ch) {
            return self.font_size;
        }

        if let Some(width) =
            self.measure_from_metrics(&self.primary_metrics, ch, DEFAULT_NON_CJK_ADVANCE_EM)
        {
            return width;
        }

        if let Some(width) =
            self.measure_from_metrics(&self.noto_metrics, ch, DEFAULT_NON_CJK_ADVANCE_EM)
        {
            return width;
        }

        self.font_size * DEFAULT_NON_CJK_ADVANCE_EM
    }

    fn measure_space(&self) -> f64 {
        if self.primary_font_is_cjk {
            if let Some(width) =
                self.measure_from_metrics(&self.latin_metrics, ' ', SPACE_ADVANCE_EM)
            {
                return width;
            }

            return self.font_size * SPACE_ADVANCE_EM;
        }

        if let Some(width) = self.measure_from_metrics(&self.primary_metrics, ' ', SPACE_ADVANCE_EM)
        {
            return width;
        }

        if let Some(width) = self.measure_from_metrics(&self.noto_metrics, ' ', SPACE_ADVANCE_EM) {
            return width;
        }

        self.font_size * SPACE_ADVANCE_EM
    }

    fn measure_from_metrics(
        &self,
        metrics: &Option<FontMetrics>,
        ch: char,
        fallback_em: f64,
    ) -> Option<f64> {
        let metrics = metrics.as_ref()?;
        let glyph_id = metrics.glyph_id(ch)?;
        Some(self.font_size * metrics.advance_em(glyph_id, fallback_em))
    }
}

impl TextMeasurer for GlyphAdvanceMeasurer {
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

pub fn glyph_advance_em(
    glyph_id: u16,
    advance_widths: Option<&HashMap<u16, u16>>,
    units_per_em: u16,
    fallback_em: f64,
) -> f64 {
    if let Some(advance) = advance_widths
        .and_then(|widths| widths.get(&glyph_id))
        .copied()
        .filter(|advance| *advance > 0)
    {
        if units_per_em > 0 {
            return advance as f64 / units_per_em as f64;
        }
    }

    fallback_em
}

pub fn is_script_signal(ch: char) -> bool {
    matches!(ch, '^' | '_')
}
