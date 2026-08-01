//! Parsing of XAML attribute text into typed values.
//!
//! Every function returns a human-readable message on failure; the caller attaches the span and
//! the diagnostic code.

#[derive(Debug, Clone, Copy, PartialEq)]
pub enum Length {
    Auto,
    Dip(f64),
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub enum GridLength {
    Auto,
    Dip(f64),
    Star(f64),
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Thickness {
    pub left: f64,
    pub top: f64,
    pub right: f64,
    pub bottom: f64,
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CornerRadius {
    pub top_left: f64,
    pub top_right: f64,
    pub bottom_right: f64,
    pub bottom_left: f64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Color {
    pub a: u8,
    pub r: u8,
    pub g: u8,
    pub b: u8,
}

impl Color {
    pub fn to_argb_hex(self) -> String {
        format!("#{:02X}{:02X}{:02X}{:02X}", self.a, self.r, self.g, self.b)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ResourceKind {
    Theme,
    Static,
}

impl ResourceKind {
    pub fn ir_name(self) -> &'static str {
        match self {
            Self::Theme => "themeResource",
            Self::Static => "staticResource",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ResourceRef {
    pub kind: ResourceKind,
    pub key: String,
}

/// A value that is fully known at compile time.
#[derive(Debug, Clone, PartialEq)]
pub enum LiteralValue {
    Double(f64),
    Length(Length),
    GridLength(GridLength),
    Thickness(Thickness),
    CornerRadius(CornerRadius),
    Color(Color),
    Str(String),
    Bool(bool),
    Int(i64),
    Enumeration {
        enum_name: &'static str,
        variant: &'static str,
    },
}

/// A property value: either a compile-time literal, or a resource resolved at runtime.
///
/// Resource references are legal for *any* property type, not only brushes — the real cards use
/// `{ThemeResource EasydictCardBorderThickness}` and `{ThemeResource EasydictCardCornerRadius}`.
/// The compiler cannot check a resource's runtime type, because the app merges theme dictionaries
/// dynamically; a type mismatch surfaces at runtime.
#[derive(Debug, Clone, PartialEq)]
pub enum HirValue {
    Literal(LiteralValue),
    Resource(ResourceRef),
}

fn parse_finite(raw: &str) -> Result<f64, String> {
    let trimmed = raw.trim();
    let value: f64 = trimmed
        .parse()
        .map_err(|_| format!("'{trimmed}' is not a number"))?;
    if !value.is_finite() {
        return Err(format!("'{trimmed}' is not a finite number"));
    }
    Ok(value)
}

/// Splits on commas and/or whitespace, the two separators XAML accepts in vector values.
fn split_components(raw: &str) -> Vec<&str> {
    raw.split(|c: char| c == ',' || c.is_whitespace())
        .filter(|part| !part.is_empty())
        .collect()
}

pub fn parse_double(raw: &str) -> Result<f64, String> {
    parse_finite(raw)
}

pub fn parse_int(raw: &str) -> Result<i64, String> {
    let trimmed = raw.trim();
    trimmed
        .parse()
        .map_err(|_| format!("'{trimmed}' is not an integer"))
}

pub fn parse_bool(raw: &str) -> Result<bool, String> {
    match raw.trim() {
        "True" | "true" => Ok(true),
        "False" | "false" => Ok(false),
        other => Err(format!(
            "'{other}' is not a boolean; expected True or False"
        )),
    }
}

pub fn parse_length(raw: &str) -> Result<Length, String> {
    let trimmed = raw.trim();
    if trimmed == "Auto" {
        return Ok(Length::Auto);
    }
    let value = parse_finite(trimmed)?;
    if value < 0.0 {
        return Err(format!("'{trimmed}' must not be negative"));
    }
    Ok(Length::Dip(value))
}

pub fn parse_grid_length(raw: &str) -> Result<GridLength, String> {
    let trimmed = raw.trim();
    if trimmed == "Auto" {
        return Ok(GridLength::Auto);
    }
    if let Some(prefix) = trimmed.strip_suffix('*') {
        let weight = if prefix.trim().is_empty() {
            1.0
        } else {
            parse_finite(prefix)?
        };
        if weight < 0.0 {
            return Err(format!("star weight '{trimmed}' must not be negative"));
        }
        return Ok(GridLength::Star(weight));
    }
    let value = parse_finite(trimmed)?;
    if value < 0.0 {
        return Err(format!("'{trimmed}' must not be negative"));
    }
    Ok(GridLength::Dip(value))
}

pub fn parse_thickness(raw: &str) -> Result<Thickness, String> {
    let parts = split_components(raw);
    let numbers = parts
        .iter()
        .map(|part| parse_finite(part))
        .collect::<Result<Vec<_>, _>>()?;

    match numbers.len() {
        1 => Ok(Thickness {
            left: numbers[0],
            top: numbers[0],
            right: numbers[0],
            bottom: numbers[0],
        }),
        2 => Ok(Thickness {
            left: numbers[0],
            top: numbers[1],
            right: numbers[0],
            bottom: numbers[1],
        }),
        4 => Ok(Thickness {
            left: numbers[0],
            top: numbers[1],
            right: numbers[2],
            bottom: numbers[3],
        }),
        other => Err(format!(
            "a thickness needs 1, 2 or 4 numbers, found {other}"
        )),
    }
}

pub fn parse_corner_radius(raw: &str) -> Result<CornerRadius, String> {
    let parts = split_components(raw);
    let numbers = parts
        .iter()
        .map(|part| parse_finite(part))
        .collect::<Result<Vec<_>, _>>()?;

    match numbers.len() {
        1 => Ok(CornerRadius {
            top_left: numbers[0],
            top_right: numbers[0],
            bottom_right: numbers[0],
            bottom_left: numbers[0],
        }),
        4 => Ok(CornerRadius {
            top_left: numbers[0],
            top_right: numbers[1],
            bottom_right: numbers[2],
            bottom_left: numbers[3],
        }),
        other => Err(format!(
            "a corner radius needs 1 or 4 numbers, found {other}"
        )),
    }
}

pub fn parse_color(raw: &str) -> Result<Color, String> {
    let trimmed = raw.trim();
    let digits = trimmed.strip_prefix('#').ok_or_else(|| {
        format!("'{trimmed}' is not a colour; Direct XAML v0 accepts hex literals such as #FF102030, or a {{ThemeResource}} reference")
    })?;

    if !digits.chars().all(|c| c.is_ascii_hexdigit()) {
        return Err(format!("'{trimmed}' contains non-hexadecimal characters"));
    }

    let nibble = |index: usize| -> u8 {
        u8::from_str_radix(&digits[index..index + 1], 16).unwrap_or(0) * 17
    };
    let byte =
        |index: usize| -> u8 { u8::from_str_radix(&digits[index..index + 2], 16).unwrap_or(0) };

    match digits.len() {
        3 => Ok(Color {
            a: 255,
            r: nibble(0),
            g: nibble(1),
            b: nibble(2),
        }),
        4 => Ok(Color {
            a: nibble(0),
            r: nibble(1),
            g: nibble(2),
            b: nibble(3),
        }),
        6 => Ok(Color {
            a: 255,
            r: byte(0),
            g: byte(2),
            b: byte(4),
        }),
        8 => Ok(Color {
            a: byte(0),
            r: byte(2),
            g: byte(4),
            b: byte(6),
        }),
        other => Err(format!(
            "'{trimmed}' has {other} hex digits; expected 3, 4, 6 or 8"
        )),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn lengths_accept_auto_and_numbers() {
        assert_eq!(parse_length("Auto"), Ok(Length::Auto));
        assert_eq!(parse_length(" 12 "), Ok(Length::Dip(12.0)));
        assert!(parse_length("-1").is_err());
        assert!(parse_length("Infinity").is_err());
        assert!(parse_length("wide").is_err());
    }

    #[test]
    fn grid_lengths_accept_stars() {
        assert_eq!(parse_grid_length("Auto"), Ok(GridLength::Auto));
        assert_eq!(parse_grid_length("*"), Ok(GridLength::Star(1.0)));
        assert_eq!(parse_grid_length("2*"), Ok(GridLength::Star(2.0)));
        assert_eq!(parse_grid_length("48"), Ok(GridLength::Dip(48.0)));
    }

    #[test]
    fn thickness_supports_one_two_and_four_components() {
        assert_eq!(
            parse_thickness("4"),
            Ok(Thickness {
                left: 4.0,
                top: 4.0,
                right: 4.0,
                bottom: 4.0
            })
        );
        assert_eq!(
            parse_thickness("6,4"),
            Ok(Thickness {
                left: 6.0,
                top: 4.0,
                right: 6.0,
                bottom: 4.0
            })
        );
        assert_eq!(
            parse_thickness("0,0,0,1"),
            Ok(Thickness {
                left: 0.0,
                top: 0.0,
                right: 0.0,
                bottom: 1.0
            })
        );
        assert!(parse_thickness("1,2,3").is_err());
    }

    #[test]
    fn corner_radius_takes_one_or_four() {
        assert!(parse_corner_radius("4").is_ok());
        assert!(parse_corner_radius("1,2,3,4").is_ok());
        assert!(parse_corner_radius("1,2").is_err());
    }

    #[test]
    fn colours_expand_shorthand() {
        assert_eq!(
            parse_color("#F00"),
            Ok(Color {
                a: 255,
                r: 255,
                g: 0,
                b: 0
            })
        );
        assert_eq!(
            parse_color("#80FF0000"),
            Ok(Color {
                a: 128,
                r: 255,
                g: 0,
                b: 0
            })
        );
        assert_eq!(
            parse_color("#102030").map(|c| c.to_argb_hex()),
            Ok("#FF102030".to_string())
        );
        assert!(parse_color("Red").is_err());
        assert!(parse_color("#GGG").is_err());
    }

    #[test]
    fn booleans_accept_both_casings() {
        assert_eq!(parse_bool("True"), Ok(true));
        assert_eq!(parse_bool("false"), Ok(false));
        assert!(parse_bool("yes").is_err());
    }
}
