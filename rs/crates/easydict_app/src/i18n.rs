use std::sync::OnceLock;

use win_fluent::prelude::*;

static I18N: OnceLock<I18n> = OnceLock::new();

pub fn tr(key: &'static str, fallback: &'static str) -> String {
    tr_locale(&current_locale(), key, fallback)
}

pub fn tr_count(key: &'static str, fallback: &'static str, count: usize) -> String {
    tr_count_locale(&current_locale(), key, fallback, count)
}

pub fn tr_locale(locale: &str, key: &'static str, fallback: &'static str) -> String {
    catalog()
        .clone()
        .locale(normalize_locale(locale))
        .resolve(&t(key, fallback))
}

pub fn tr_count_locale(
    locale: &str,
    key: &'static str,
    fallback: &'static str,
    count: usize,
) -> String {
    catalog()
        .clone()
        .locale(normalize_locale(locale))
        .resolve(&t(key, fallback).arg("count", count))
}

/// All shipped locale tables. Each entry pairs a locale id with its PO document,
/// embedded at compile time. en-US/zh-CN are hand-authored; the rest are ported
/// from the .NET `.resw` resources via `scripts/gen-rust-po-from-resw.ps1`.
const LOCALE_PO_FILES: &[(&str, &str)] = &[
    ("en-US", include_str!("../locales/en-US.po")),
    ("zh-CN", include_str!("../locales/zh-CN.po")),
    ("zh-TW", include_str!("../locales/zh-TW.po")),
    ("ar-SA", include_str!("../locales/ar-SA.po")),
    ("da-DK", include_str!("../locales/da-DK.po")),
    ("de-DE", include_str!("../locales/de-DE.po")),
    ("fr-FR", include_str!("../locales/fr-FR.po")),
    ("hi-IN", include_str!("../locales/hi-IN.po")),
    ("id-ID", include_str!("../locales/id-ID.po")),
    ("it-IT", include_str!("../locales/it-IT.po")),
    ("ja-JP", include_str!("../locales/ja-JP.po")),
    ("ko-KR", include_str!("../locales/ko-KR.po")),
    ("ms-MY", include_str!("../locales/ms-MY.po")),
    ("th-TH", include_str!("../locales/th-TH.po")),
    ("vi-VN", include_str!("../locales/vi-VN.po")),
];

fn catalog() -> &'static I18n {
    I18N.get_or_init(|| {
        let mut i18n = I18n::new("en-US").fallback_locale("en-US");
        for (locale, source) in LOCALE_PO_FILES {
            i18n = i18n.with_bundle(po_bundle(locale, source));
        }
        i18n
    })
}

pub fn default_ui_language() -> String {
    env_ui_language()
        .or_else(system_ui_language)
        .and_then(|locale| supported_locale(&locale).map(str::to_string))
        .unwrap_or_else(|| "en-US".to_string())
}

fn current_locale() -> String {
    default_ui_language()
}

fn env_ui_language() -> Option<String> {
    ["EASYDICT_PREVIEW_UI_LANGUAGE", "EASYDICT_UI_LANGUAGE"]
        .into_iter()
        .find_map(|key| {
            std::env::var(key)
                .ok()
                .map(|value| value.trim().to_string())
                .filter(|value| !value.is_empty())
        })
}

fn normalize_locale(locale: &str) -> String {
    supported_locale(locale).unwrap_or("en-US").to_string()
}

fn supported_locale(locale: &str) -> Option<&'static str> {
    let locale = locale.trim().replace('_', "-");
    if locale.is_empty() {
        return None;
    }

    let lower = locale.to_ascii_lowercase();
    if lower == "zh"
        || lower.starts_with("zh-cn")
        || lower.starts_with("zh-hans")
        || lower.starts_with("zh-sg")
    {
        return Some("zh-CN");
    }
    if lower.starts_with("zh-tw")
        || lower.starts_with("zh-hant")
        || lower.starts_with("zh-hk")
        || lower.starts_with("zh-mo")
    {
        return Some("zh-TW");
    }

    LOCALE_PO_FILES
        .iter()
        .find(|(id, _)| id.eq_ignore_ascii_case(&locale))
        .map(|(id, _)| *id)
        .or_else(|| {
            let language = lower.split('-').next()?;
            LOCALE_PO_FILES
                .iter()
                .find(|(id, _)| id.to_ascii_lowercase().starts_with(&format!("{language}-")))
                .map(|(id, _)| *id)
        })
}

#[cfg(windows)]
fn system_ui_language() -> Option<String> {
    easydict_windows_shell::user_default_ui_language()
}

#[cfg(not(windows))]
fn system_ui_language() -> Option<String> {
    None
}

/// Build an [`I18nBundle`] from a gettext PO document. The translation tables
/// live in `locales/<locale>.po` (one file per language) and are embedded at
/// compile time via `include_str!`, so no runtime file IO is required.
fn po_bundle(locale: &str, source: &str) -> I18nBundle {
    let mut bundle = I18nBundle::new(locale);
    for (key, value) in parse_po_entries(source) {
        bundle.insert(key, value);
    }
    bundle
}

/// Minimal gettext PO parser. Reads `msgid`/`msgstr` pairs, supports multi-line
/// continuation strings, and skips comments plus the empty header entry. This is
/// deliberately scoped to the subset of PO that Easydict authors by hand (no
/// plural forms or message contexts).
fn parse_po_entries(source: &str) -> Vec<(String, String)> {
    enum Field {
        None,
        Id,
        Str,
    }

    let mut entries = Vec::new();
    let mut field = Field::None;
    let mut id = String::new();
    let mut value = String::new();
    let mut have_id = false;

    for line in source.lines() {
        let trimmed = line.trim();
        if trimmed.is_empty() || trimmed.starts_with('#') {
            continue;
        }

        if let Some(rest) = trimmed.strip_prefix("msgid ") {
            // A new entry begins; commit the previous one (the empty-id header
            // entry is dropped here).
            if have_id && !id.is_empty() {
                entries.push((std::mem::take(&mut id), std::mem::take(&mut value)));
            } else {
                id.clear();
                value.clear();
            }
            id.push_str(&unquote_po(rest));
            have_id = true;
            field = Field::Id;
        } else if let Some(rest) = trimmed.strip_prefix("msgstr ") {
            value.push_str(&unquote_po(rest));
            field = Field::Str;
        } else if trimmed.starts_with('"') {
            let piece = unquote_po(trimmed);
            match field {
                Field::Id => id.push_str(&piece),
                Field::Str => value.push_str(&piece),
                Field::None => {}
            }
        }
    }

    if have_id && !id.is_empty() {
        entries.push((id, value));
    }

    entries
}

/// Strip the surrounding quotes from a PO string token and decode the standard
/// gettext escape sequences.
fn unquote_po(token: &str) -> String {
    let token = token.trim();
    let inner = token
        .strip_prefix('"')
        .and_then(|rest| rest.strip_suffix('"'))
        .unwrap_or(token);

    let mut out = String::with_capacity(inner.len());
    let mut chars = inner.chars();
    while let Some(ch) = chars.next() {
        if ch == '\\' {
            match chars.next() {
                Some('n') => out.push('\n'),
                Some('t') => out.push('\t'),
                Some('r') => out.push('\r'),
                Some('"') => out.push('"'),
                Some('\\') => out.push('\\'),
                Some(other) => out.push(other),
                None => {}
            }
        } else {
            out.push(ch);
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn po_entries_parse_keys_values_and_skip_header() {
        let entries = parse_po_entries(
            "msgid \"\"\nmsgstr \"\"\n\"Language: en-US\\n\"\n\n# comment\nmsgid \"a.b\"\nmsgstr \"Hello\"\n",
        );
        assert_eq!(entries, vec![("a.b".to_string(), "Hello".to_string())]);
    }

    #[test]
    fn po_unquote_decodes_escaped_quotes() {
        assert_eq!(unquote_po("\"say \\\"hi\\\"\""), "say \"hi\"");
    }

    #[test]
    fn bundled_locales_resolve_translations() {
        assert_eq!(tr_locale("en-US", "settings.tab.general", "x"), "General");
        assert_eq!(tr_locale("zh-CN", "settings.tab.general", "x"), "常规");
        assert_eq!(
            tr_locale("zh-CN", "ocr.capture.instructions", "x"),
            "拖动选择区域  |  双击选择窗口  |  滚轮切换窗口  |  Esc 退出"
        );
        // Missing keys fall back to the caller-provided default.
        assert_eq!(tr_locale("en-US", "missing.key", "fallback"), "fallback");
    }

    #[test]
    fn ported_locales_are_registered_and_resolve() {
        // Ported from .NET resw.
        assert_eq!(tr_locale("de-DE", "settings.tab.general", "x"), "Allgemein");
        assert_eq!(tr_locale("zh-TW", "settings.tab.services", "x"), "服務");
        assert_eq!(tr_locale("ja-JP", "settings.save", "x"), "設定を保存");
        // Unknown locale falls back to the en-US bundle (the configured fallback
        // locale), not the caller default.
        assert_eq!(tr_locale("xx-XX", "settings.tab.general", "x"), "General");
    }

    #[test]
    fn locale_aliases_normalize_to_bundled_languages() {
        assert_eq!(normalize_locale("zh-Hans-CN"), "zh-CN");
        assert_eq!(normalize_locale("zh_Hant_TW"), "zh-TW");
        assert_eq!(normalize_locale("de"), "de-DE");
        assert_eq!(normalize_locale("xx-XX"), "en-US");
    }
}
