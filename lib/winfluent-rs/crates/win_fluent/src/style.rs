#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct FluentStyle {
    classes: Vec<String>,
}

impl FluentStyle {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn from_classes(classes: impl AsRef<str>) -> Self {
        let mut style = Self::new();
        style.extend(classes);
        style
    }

    pub fn extend(&mut self, classes: impl AsRef<str>) {
        self.classes.extend(
            classes
                .as_ref()
                .split_whitespace()
                .filter(|class| !class.is_empty())
                .map(str::to_string),
        );
    }

    pub fn classes(&self) -> &[String] {
        &self.classes
    }

    pub fn has(&self, class: &str) -> bool {
        self.classes.iter().any(|value| value == class)
    }

    pub fn has_prefix(&self, prefix: &str) -> bool {
        self.classes.iter().any(|value| value.starts_with(prefix))
    }

    pub fn last_with_prefix(&self, prefix: &str) -> Option<&str> {
        self.classes
            .iter()
            .rev()
            .find(|value| value.starts_with(prefix))
            .map(String::as_str)
    }

    pub fn summary(&self) -> String {
        self.classes.join(" ")
    }
}

pub fn utility_scale(value: &str) -> Option<u16> {
    let value = value.strip_prefix('[').unwrap_or(value);
    let value = value.strip_suffix(']').unwrap_or(value);

    if let Some(px) = value.strip_suffix("px") {
        return px.parse::<u16>().ok();
    }

    value
        .parse::<u16>()
        .ok()
        .and_then(|scale| scale.checked_mul(4))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_tailwind_like_class_lists_and_spacing_scale() {
        let style = FluentStyle::from_classes("p-6 gap-5 w-full surface-card");

        assert!(style.has("surface-card"));
        assert_eq!(style.last_with_prefix("gap-"), Some("gap-5"));
        assert_eq!(utility_scale("6"), Some(24));
        assert_eq!(utility_scale("[18px]"), Some(18));
    }
}
