/// A markup extension, split into its name and comma-separated arguments.
///
/// v0 does not support nested extensions. Only `{ThemeResource Key}` and `{StaticResource Key}`
/// reach lowering, and neither nests, so a flat split on commas is sufficient — anything that
/// would need a real recursive parser is rejected as unsupported before the arguments matter.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MarkupExtension {
    pub name: String,
    pub arguments: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum AttributeValue {
    Literal(String),
    Markup(MarkupExtension),
}

impl AttributeValue {
    /// Classifies a raw attribute value, honouring the `{}` escape for literals that begin
    /// with a brace.
    pub fn classify(raw: &str) -> Self {
        if let Some(literal) = raw.strip_prefix("{}") {
            return Self::Literal(literal.to_string());
        }

        let trimmed = raw.trim();
        if trimmed.starts_with('{') && trimmed.ends_with('}') && trimmed.len() >= 2 {
            if let Some(extension) = parse_extension(trimmed) {
                return Self::Markup(extension);
            }
        }

        Self::Literal(raw.to_string())
    }

    pub fn as_literal(&self) -> Option<&str> {
        match self {
            Self::Literal(value) => Some(value),
            Self::Markup(_) => None,
        }
    }
}

fn parse_extension(raw: &str) -> Option<MarkupExtension> {
    let inner = raw
        .strip_prefix('{')
        .and_then(|rest| rest.strip_suffix('}'))?
        .trim();
    if inner.is_empty() {
        return None;
    }

    let (name, rest) = match inner.find(char::is_whitespace) {
        Some(index) => (&inner[..index], inner[index..].trim()),
        None => (inner, ""),
    };
    if name.is_empty() {
        return None;
    }

    let arguments = if rest.is_empty() {
        Vec::new()
    } else {
        rest.split(',')
            .map(|argument| argument.trim().to_string())
            .filter(|argument| !argument.is_empty())
            .collect()
    };

    Some(MarkupExtension {
        name: name.to_string(),
        arguments,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_a_theme_resource() {
        let value = AttributeValue::classify("{ThemeResource CardStrokeColorDefaultBrush}");
        assert_eq!(
            value,
            AttributeValue::Markup(MarkupExtension {
                name: "ThemeResource".to_string(),
                arguments: vec!["CardStrokeColorDefaultBrush".to_string()],
            })
        );
    }

    #[test]
    fn parses_an_extension_without_arguments() {
        let value = AttributeValue::classify("{x:Null}");
        assert_eq!(
            value,
            AttributeValue::Markup(MarkupExtension {
                name: "x:Null".to_string(),
                arguments: Vec::new(),
            })
        );
    }

    #[test]
    fn splits_multiple_arguments() {
        let value = AttributeValue::classify("{Binding Path=Foo, Mode=TwoWay}");
        match value {
            AttributeValue::Markup(extension) => {
                assert_eq!(extension.name, "Binding");
                assert_eq!(extension.arguments, vec!["Path=Foo", "Mode=TwoWay"]);
            }
            other => panic!("expected markup extension, got {other:?}"),
        }
    }

    #[test]
    fn honours_the_literal_escape() {
        assert_eq!(
            AttributeValue::classify("{}{not an extension}"),
            AttributeValue::Literal("{not an extension}".to_string())
        );
    }

    #[test]
    fn plain_values_stay_literal() {
        assert_eq!(
            AttributeValue::classify("12,0,4,0"),
            AttributeValue::Literal("12,0,4,0".to_string())
        );
        assert_eq!(
            AttributeValue::classify("{unterminated"),
            AttributeValue::Literal("{unterminated".to_string())
        );
    }
}
