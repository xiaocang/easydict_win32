use std::sync::OnceLock;

use win_fluent::prelude::*;

static I18N: OnceLock<I18n> = OnceLock::new();

pub fn tr(key: &'static str, fallback: &'static str) -> String {
    catalog().resolve(&t(key, fallback))
}

pub fn tr_count(key: &'static str, fallback: &'static str, count: usize) -> String {
    catalog().resolve(&t(key, fallback).arg("count", count))
}

fn catalog() -> &'static I18n {
    I18N.get_or_init(|| {
        I18n::new("en-US").with_bundle(
            I18nBundle::new("en-US")
                .with("app.name", "Easydict")
                .with("app.beta", "beta")
                .with("main.source_text", "Source Text")
                .with(
                    "main.source_placeholder",
                    "Enter or paste text to translate.",
                )
                .with("main.results", "Translation Results")
                .with("main.completed", "{count} service(s) completed")
                .with("main.auto_detect", "Auto Detect")
                .with("main.target_zh_hans", "Chinese (Simplified)")
                .with("main.language_help", "Language help")
                .with("main.translate", "Translate")
                .with("main.settings", "Settings"),
        )
    })
}
