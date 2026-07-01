use std::collections::BTreeMap;
use std::fmt;

#[derive(Clone, Debug, Eq, PartialEq, Ord, PartialOrd, Hash)]
pub struct LocaleId(String);

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub enum TextDirection {
    #[default]
    LeftToRight,
    RightToLeft,
}

impl LocaleId {
    pub fn new(value: impl Into<String>) -> Self {
        let value = value.into();
        debug_assert!(!value.trim().is_empty(), "locale id must not be empty");
        Self(value)
    }

    pub fn as_str(&self) -> &str {
        &self.0
    }

    pub fn text_direction(&self) -> TextDirection {
        let language = self
            .0
            .split(['-', '_'])
            .next()
            .unwrap_or_default()
            .to_ascii_lowercase();
        match language.as_str() {
            "ar" | "dv" | "fa" | "he" | "ku" | "ps" | "sd" | "ug" | "ur" | "yi" => {
                TextDirection::RightToLeft
            }
            _ => TextDirection::LeftToRight,
        }
    }
}

impl Default for LocaleId {
    fn default() -> Self {
        Self::new("en-US")
    }
}

impl From<&str> for LocaleId {
    fn from(value: &str) -> Self {
        Self::new(value)
    }
}

impl From<String> for LocaleId {
    fn from(value: String) -> Self {
        Self::new(value)
    }
}

impl fmt::Display for LocaleId {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(self.as_str())
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct I18nArg {
    pub name: String,
    pub value: String,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LocalizedText {
    pub key: String,
    pub fallback: String,
    pub args: Vec<I18nArg>,
}

impl LocalizedText {
    pub fn new(key: impl Into<String>, fallback: impl Into<String>) -> Self {
        Self {
            key: key.into(),
            fallback: fallback.into(),
            args: Vec::new(),
        }
    }

    pub fn arg(mut self, name: impl Into<String>, value: impl ToString) -> Self {
        self.args.push(I18nArg {
            name: name.into(),
            value: value.to_string(),
        });
        self
    }

    pub fn fallback_text(&self) -> String {
        format_template(&self.fallback, &self.args)
    }
}

impl From<LocalizedText> for String {
    fn from(value: LocalizedText) -> Self {
        value.fallback_text()
    }
}

impl From<&LocalizedText> for String {
    fn from(value: &LocalizedText) -> Self {
        value.fallback_text()
    }
}

pub fn t(key: impl Into<String>, fallback: impl Into<String>) -> LocalizedText {
    LocalizedText::new(key, fallback)
}

pub trait Localizer {
    fn locale(&self) -> &LocaleId;
    fn resolve(&self, text: &LocalizedText) -> String;
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct I18nBundle {
    locale: LocaleId,
    strings: BTreeMap<String, String>,
}

impl I18nBundle {
    pub fn new(locale: impl Into<LocaleId>) -> Self {
        Self {
            locale: locale.into(),
            strings: BTreeMap::new(),
        }
    }

    pub fn locale(&self) -> &LocaleId {
        &self.locale
    }

    pub fn with(mut self, key: impl Into<String>, value: impl Into<String>) -> Self {
        self.strings.insert(key.into(), value.into());
        self
    }

    pub fn insert(&mut self, key: impl Into<String>, value: impl Into<String>) {
        self.strings.insert(key.into(), value.into());
    }

    pub fn get(&self, key: &str) -> Option<&str> {
        self.strings.get(key).map(String::as_str)
    }

    pub fn resolve(&self, text: &LocalizedText) -> String {
        let template = self.get(&text.key).unwrap_or(&text.fallback);
        format_template(template, &text.args)
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct I18n {
    locale: LocaleId,
    fallback_locale: LocaleId,
    bundles: BTreeMap<LocaleId, I18nBundle>,
}

impl I18n {
    pub fn new(locale: impl Into<LocaleId>) -> Self {
        let locale = locale.into();
        Self {
            fallback_locale: locale.clone(),
            locale,
            bundles: BTreeMap::new(),
        }
    }

    pub fn locale(mut self, locale: impl Into<LocaleId>) -> Self {
        self.locale = locale.into();
        self
    }

    pub fn fallback_locale(mut self, locale: impl Into<LocaleId>) -> Self {
        self.fallback_locale = locale.into();
        self
    }

    pub fn with_bundle(mut self, bundle: I18nBundle) -> Self {
        self.bundles.insert(bundle.locale.clone(), bundle);
        self
    }

    pub fn bundle(&self, locale: &LocaleId) -> Option<&I18nBundle> {
        self.bundles.get(locale)
    }

    fn lookup(&self, locale: &LocaleId, key: &str) -> Option<&str> {
        self.bundle(locale).and_then(|bundle| bundle.get(key))
    }
}

impl Localizer for I18n {
    fn locale(&self) -> &LocaleId {
        &self.locale
    }

    fn resolve(&self, text: &LocalizedText) -> String {
        self.lookup(&self.locale, &text.key)
            .or_else(|| self.lookup(&self.fallback_locale, &text.key))
            .map(|template| format_template(template, &text.args))
            .unwrap_or_else(|| text.fallback_text())
    }
}

fn format_template(template: &str, args: &[I18nArg]) -> String {
    let mut output = template.to_string();
    for arg in args {
        output = output.replace(&format!("{{{}}}", arg.name), &arg.value);
    }
    output
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn resolves_bundle_string_with_fallback_and_args() {
        let i18n = I18n::new("zh-CN")
            .with_bundle(I18nBundle::new("zh-CN").with("main.completed", "已完成 {count} 个服务"));
        let text = t("main.completed", "{count} service(s) completed").arg("count", 3);

        assert_eq!(i18n.resolve(&text), "已完成 3 个服务");
    }

    #[test]
    fn localized_text_converts_to_formatted_fallback() {
        let text = t("missing", "Hello {name}").arg("name", "World");

        assert_eq!(String::from(text), "Hello World");
    }

    #[test]
    fn locale_direction_uses_primary_language_subtag() {
        assert_eq!(
            LocaleId::new("en-US").text_direction(),
            TextDirection::LeftToRight
        );
        assert_eq!(
            LocaleId::new("ar-SA").text_direction(),
            TextDirection::RightToLeft
        );
        assert_eq!(
            LocaleId::new("he_IL").text_direction(),
            TextDirection::RightToLeft
        );
    }
}
