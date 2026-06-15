use crate::i18n::{tr, tr_locale};
use crate::local_dictionary::{
    apply_active_local_dictionary_suggestion, apply_local_dictionary_suggestion,
    dismiss_local_dictionary_suggestions, exit_local_dictionary_suggestions,
    focus_local_dictionary_suggestions, move_local_dictionary_suggestion,
    LocalDictionarySuggestionUpdate,
};
use crate::mdx_native::{detect_mdx_file_is_encrypted, discover_mdd_file_paths};
use crate::mouse_selection::MouseSelectionProducer;
use crate::protocol::{
    local_ai_provider_modes, normalize_local_ai_provider_mode, ImportedMdxDictionarySnapshot,
    SettingsSnapshot, WordResultDto,
};
use crate::quick_translate::{QuickQueryMode, QuickTranslateSurface};
use crate::translation_services::{
    default_translation_service_descriptors, translation_service_capabilities,
    DEFAULT_FLOATING_WINDOW_SERVICE_IDS, DEFAULT_MAIN_WINDOW_SERVICE_IDS,
};
use crate::{
    HOTKEY_OCR_TRANSLATE, HOTKEY_SHOW_FIXED, HOTKEY_SHOW_MAIN, HOTKEY_SHOW_MINI, HOTKEY_SILENT_OCR,
    HOTKEY_TRANSLATE_CLIPBOARD,
};
use std::collections::HashMap;
use std::path::Path;
use win_fluent::prelude::*;
use win_fluent::IconToken;

pub const TRANSLATION_LANGUAGE_IDS: [&str; 43] = [
    "zh-Hans",
    "zh-Hant",
    "ja",
    "ko",
    "zh-classical",
    "en",
    "de",
    "nl",
    "sv",
    "no",
    "da",
    "fr",
    "es",
    "pt",
    "it",
    "ro",
    "ru",
    "pl",
    "cs",
    "uk",
    "bg",
    "sk",
    "sl",
    "et",
    "lv",
    "lt",
    "el",
    "hu",
    "fi",
    "tr",
    "ar",
    "fa",
    "he",
    "hi",
    "bn",
    "ta",
    "te",
    "ur",
    "vi",
    "th",
    "id",
    "ms",
    "tl",
];

pub const DEFAULT_OCR_SYSTEM_PROMPT: &str = "Extract all the text from this image perfectly. Output ONLY the extracted text, without any conversational filler, markdown formatting, or introductory words.";
const DEFAULT_OPENAI_ENDPOINT: &str = "https://api.openai.com/v1/responses";
const DEFAULT_OPENAI_MODEL: &str = "gpt-5.4-mini";
const DEFAULT_OLLAMA_ENDPOINT: &str = "http://localhost:11434/v1/chat/completions";
const DEFAULT_OLLAMA_MODEL: &str = "llama3.2";
const DEFAULT_FOUNDRY_LOCAL_MODEL: &str = "qwen2.5-0.5b";
const DEFAULT_OPENVINO_DEVICE: &str = "Auto";
const DEFAULT_DEEPSEEK_MODEL: &str = "deepseek-chat";
const DEFAULT_GROQ_MODEL: &str = "llama-3.3-70b-versatile";
const DEFAULT_ZHIPU_MODEL: &str = "glm-4.5-flash";
const DEFAULT_GITHUB_MODELS_MODEL: &str = "gpt-4.1";
const DEFAULT_GEMINI_MODEL: &str = "gemini-2.5-flash";
const DEFAULT_CUSTOM_OPENAI_MODEL: &str = "gpt-3.5-turbo";
const DEFAULT_BUILT_IN_AI_MODEL: &str = "glm-4-flash-250414";
const DEFAULT_DOUBAO_ENDPOINT: &str = "https://ark.cn-beijing.volces.com/api/v3/responses";
const DEFAULT_DOUBAO_MODEL: &str = "doubao-seed-translation-250915";
const DEFAULT_OLLAMA_OCR_ENDPOINT: &str = "http://localhost:11434/api/generate";
const DEFAULT_OLLAMA_OCR_MODEL: &str = "glm-ocr";
const DEFAULT_CUSTOM_OCR_ENDPOINT: &str = "https://api.openai.com/v1/responses";
const DEFAULT_CUSTOM_OCR_MODEL: &str = "gpt-5.4-mini";

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AppMode {
    QuickTranslate,
    LongDocument,
}

impl AppMode {
    pub fn id(self) -> &'static str {
        match self {
            Self::QuickTranslate => "quick",
            Self::LongDocument => "long-document",
        }
    }

    pub fn title(self) -> &'static str {
        match self {
            Self::QuickTranslate => "Easydict",
            Self::LongDocument => "Long Document",
        }
    }

    fn from_id(id: &str) -> Self {
        match id {
            "long-document" => Self::LongDocument,
            _ => Self::QuickTranslate,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SettingsSection {
    General,
    Services,
    Views,
    Hotkeys,
    Advanced,
    Language,
    About,
}

impl SettingsSection {
    pub const ALL: [Self; 7] = [
        Self::General,
        Self::Services,
        Self::Views,
        Self::Hotkeys,
        Self::Advanced,
        Self::Language,
        Self::About,
    ];

    pub fn id(self) -> &'static str {
        match self {
            Self::General => "general",
            Self::Services => "services",
            Self::Views => "views",
            Self::Hotkeys => "hotkeys",
            Self::Advanced => "advanced",
            Self::Language => "language",
            Self::About => "about",
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            Self::General => "General",
            Self::Services => "Services",
            Self::Views => "Views",
            Self::Hotkeys => "Hotkeys",
            Self::Advanced => "Advanced",
            Self::Language => "Language",
            Self::About => "About",
        }
    }

    pub fn icon(self) -> IconToken {
        match self {
            Self::General => IconToken::with_glyph("settings-general", '\u{E713}'),
            Self::Services => IconToken::with_glyph("settings-services", '\u{E90F}'),
            Self::Views => IconToken::with_glyph("settings-views", '\u{E8A7}'),
            Self::Hotkeys => IconToken::with_glyph("settings-hotkeys", '\u{E765}'),
            Self::Advanced => IconToken::with_glyph("settings-advanced", '\u{E771}'),
            Self::Language => IconToken::with_glyph("settings-language", '\u{E774}'),
            Self::About => IconToken::with_glyph("settings-about", '\u{E946}'),
        }
    }

    fn from_id(id: &str) -> Self {
        Self::ALL
            .iter()
            .copied()
            .find(|section| section.id() == id)
            .unwrap_or(Self::General)
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SettingsLink {
    GitHubRepository,
    IssueFeedback,
    EasydictForMacOS,
}

impl SettingsLink {
    pub fn id(self) -> &'static str {
        match self {
            Self::GitHubRepository => "GitHubRepositoryLink",
            Self::IssueFeedback => "IssueFeedbackLink",
            Self::EasydictForMacOS => "InspiredByLink",
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            Self::GitHubRepository => "GitHub Repository",
            Self::IssueFeedback => "Issue Feedback",
            Self::EasydictForMacOS => "Easydict for macOS",
        }
    }

    pub fn url(self) -> &'static str {
        match self {
            Self::GitHubRepository => "https://github.com/xiaocang/easydict_win32",
            Self::IssueFeedback => "https://github.com/xiaocang/easydict_win32/issues/new/choose",
            Self::EasydictForMacOS => "https://github.com/tisfeng/Easydict",
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ConnectionStatus {
    Connected,
    Disconnected,
    Error,
}

impl ConnectionStatus {
    pub fn label(self) -> &'static str {
        match self {
            Self::Connected => "Connected",
            Self::Disconnected => "Disconnected",
            Self::Error => "Error",
        }
    }

    pub fn severity(self) -> ValidationSeverity {
        match self {
            Self::Connected => ValidationSeverity::Success,
            Self::Disconnected => ValidationSeverity::Info,
            Self::Error => ValidationSeverity::Error,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PreviewScenario {
    Initial,
    BeforeTranslate,
    Loading,
    AfterTranslate,
    Error,
    ModeOverlay,
    PrimaryHover,
    PrimaryPressed,
    SourceInputHover,
    SourceInputFocused,
    ResultHeaderHover,
    ResultCollapsed,
    LocalDictionarySuggestions,
    LongDocument,
    LongDocumentRunning,
    LongDocumentError,
}

impl PreviewScenario {
    pub const ALL: [Self; 16] = [
        Self::Initial,
        Self::BeforeTranslate,
        Self::Loading,
        Self::AfterTranslate,
        Self::Error,
        Self::ModeOverlay,
        Self::PrimaryHover,
        Self::PrimaryPressed,
        Self::SourceInputHover,
        Self::SourceInputFocused,
        Self::ResultHeaderHover,
        Self::ResultCollapsed,
        Self::LocalDictionarySuggestions,
        Self::LongDocument,
        Self::LongDocumentRunning,
        Self::LongDocumentError,
    ];

    pub fn id(self) -> &'static str {
        match self {
            Self::Initial => "initial",
            Self::BeforeTranslate => "before_translate",
            Self::Loading => "loading",
            Self::AfterTranslate => "after_translate",
            Self::Error => "error",
            Self::ModeOverlay => "mode_overlay",
            Self::PrimaryHover => "primary_hover",
            Self::PrimaryPressed => "primary_pressed",
            Self::SourceInputHover => "source_input_hover",
            Self::SourceInputFocused => "source_input_focused",
            Self::ResultHeaderHover => "result_header_hover",
            Self::ResultCollapsed => "result_collapsed",
            Self::LocalDictionarySuggestions => "local_dictionary_suggestions",
            Self::LongDocument => "long_document",
            Self::LongDocumentRunning => "long_document_running",
            Self::LongDocumentError => "long_document_error",
        }
    }

    pub fn from_id(value: &str) -> Self {
        Self::ALL
            .iter()
            .copied()
            .find(|scenario| scenario.id() == value)
            .unwrap_or(Self::Initial)
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct GrammarCorrectionPreview {
    pub original_text: String,
    pub corrected_text: String,
    pub explanation: Option<String>,
    pub has_corrections: bool,
}

impl GrammarCorrectionPreview {
    pub fn new(
        original_text: impl Into<String>,
        corrected_text: impl Into<String>,
        explanation: Option<String>,
        has_corrections: bool,
    ) -> Self {
        Self {
            original_text: original_text.into(),
            corrected_text: corrected_text.into(),
            explanation,
            has_corrections,
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TranslationResultPreview {
    pub id: String,
    pub service_name: String,
    pub body: String,
    pub grammar_result: Option<GrammarCorrectionPreview>,
    pub alternatives: Option<Vec<String>>,
    pub word_result: Option<WordResultDto>,
    pub raw_html: Option<String>,
    pub streamed_chunks: Vec<String>,
    pub no_result: bool,
    pub status: ResultStatus,
    pub query_mode: QuickQueryMode,
    pub latency_ms: Option<u32>,
    pub enabled_query: bool,
    pub grammar_capable: bool,
    pub streaming_capable: bool,
    pub has_queried: bool,
    pub demoted: bool,
    pub expanded: bool,
    pub header_state: ControlState,
}

impl TranslationResultPreview {
    pub fn new(
        id: impl Into<String>,
        service_name: impl Into<String>,
        body: impl Into<String>,
    ) -> Self {
        Self {
            id: id.into(),
            service_name: service_name.into(),
            body: body.into(),
            grammar_result: None,
            alternatives: None,
            word_result: None,
            raw_html: None,
            streamed_chunks: Vec::new(),
            no_result: false,
            status: ResultStatus::Ready,
            query_mode: QuickQueryMode::Translation,
            latency_ms: None,
            enabled_query: true,
            grammar_capable: false,
            streaming_capable: false,
            has_queried: true,
            demoted: false,
            expanded: true,
            header_state: ControlState::default(),
        }
    }

    pub fn latency_ms(mut self, value: u32) -> Self {
        self.latency_ms = Some(value);
        self
    }

    pub fn status(mut self, status: ResultStatus) -> Self {
        self.status = status;
        self
    }

    pub fn expanded(mut self, expanded: bool) -> Self {
        self.expanded = expanded;
        self
    }

    pub fn manual_query(mut self) -> Self {
        self.enabled_query = false;
        self.has_queried = false;
        self.expanded = false;
        self
    }

    pub fn grammar_capable(mut self, grammar_capable: bool) -> Self {
        self.grammar_capable = grammar_capable;
        self
    }

    pub fn streaming_capable(mut self, streaming_capable: bool) -> Self {
        self.streaming_capable = streaming_capable;
        self
    }

    pub fn demoted(mut self, demoted: bool) -> Self {
        self.demoted = demoted;
        if demoted {
            self.expanded = false;
        }
        self
    }

    pub fn to_result_item(&self) -> ResultItem {
        let mut item = ResultItem::new(
            self.id.clone(),
            self.service_name.clone(),
            self.result_body(),
        )
        .icon(service_icon(&self.id))
        .expanded(self.expanded)
        .toggleable(!self.demoted)
        .dimmed(self.demoted)
        .status(self.status)
        .header_state(self.header_state.clone())
        .actions_visible(
            self.header_state.hovered || self.header_state.pressed || self.header_state.focused,
        );

        if let Some(metadata) = self.result_metadata() {
            item = item.metadata(metadata);
        }

        if !self.enabled_query && !self.has_queried {
            item = item.pending_hint(tr(
                "main.result.pending_query",
                "Click to query this service",
            ));
        }

        item
    }

    pub fn result_body(&self) -> String {
        if let Some(grammar) = &self.grammar_result {
            return grammar_body(grammar);
        }

        if self.status == ResultStatus::Streaming && !self.streamed_chunks.is_empty() {
            return self.streamed_chunks.join("");
        }

        let mut body = self.body.clone();
        if let Some(alternatives) = &self.alternatives {
            let alternatives: Vec<&str> = alternatives
                .iter()
                .map(String::as_str)
                .filter(|alt| !alt.trim().is_empty())
                .collect();
            if !alternatives.is_empty() {
                if !body.is_empty() {
                    body.push('\n');
                }
                body.push_str("Also: ");
                body.push_str(&alternatives.join("; "));
            }
        }
        if let Some(word_result) = &self.word_result {
            append_word_result_body(&mut body, word_result);
        }
        body
    }

    fn result_metadata(&self) -> Option<String> {
        let latency = self.latency_ms.map(|value| format!("{value}ms"));

        if let Some(grammar) = &self.grammar_result {
            let mode = if grammar.has_corrections {
                "Grammar"
            } else {
                "No changes"
            };
            return Some(match latency {
                Some(latency) => format!("{mode} - {latency}"),
                None => mode.to_string(),
            });
        }

        if self.no_result {
            return Some(match latency {
                Some(latency) => format!("No result - {latency}"),
                None => "No result".to_string(),
            });
        }

        latency
    }
}

fn grammar_body(grammar: &GrammarCorrectionPreview) -> String {
    let mut body = format!("Corrected\n{}", grammar.corrected_text);

    if let Some(explanation) = grammar
        .explanation
        .as_deref()
        .map(str::trim)
        .filter(|value| !value.is_empty())
    {
        body.push_str("\n\nExplanation\n");
        body.push_str(explanation);
    } else if !grammar.has_corrections {
        body.push_str("\n\nNo grammar changes found.");
    }

    body
}

fn append_word_result_body(body: &mut String, word_result: &WordResultDto) {
    let mut sections = Vec::new();

    if let Some(phonetics) = &word_result.phonetics {
        let values: Vec<String> = phonetics
            .iter()
            .filter_map(|phonetic| {
                let text = phonetic.text.as_deref()?.trim();
                if text.is_empty() {
                    return None;
                }
                let accent = phonetic.accent.as_deref().unwrap_or("").trim();
                Some(if accent.is_empty() {
                    format_phonetic_text(text)
                } else {
                    format!("{accent} {}", format_phonetic_text(text))
                })
            })
            .collect();
        if !values.is_empty() {
            sections.push(format!("Phonetics: {}", values.join("; ")));
        }
    }

    if let Some(definitions) = &word_result.definitions {
        let values: Vec<String> = definitions
            .iter()
            .filter_map(|definition| {
                let meanings: Vec<&str> = definition
                    .meanings
                    .as_deref()
                    .unwrap_or(&[])
                    .iter()
                    .map(String::as_str)
                    .filter(|meaning| !meaning.trim().is_empty())
                    .collect();
                if meanings.is_empty() {
                    return None;
                }
                let part = definition.part_of_speech.as_deref().unwrap_or("").trim();
                Some(if part.is_empty() {
                    meanings.join("; ")
                } else {
                    format!("{part} {}", meanings.join("; "))
                })
            })
            .collect();
        if !values.is_empty() {
            sections.push(format!("Definitions: {}", values.join(" | ")));
        }
    }

    if let Some(examples) = &word_result.examples {
        let values: Vec<&str> = examples
            .iter()
            .map(String::as_str)
            .filter(|example| !example.trim().is_empty())
            .collect();
        if !values.is_empty() {
            sections.push(format!("Examples: {}", values.join(" | ")));
        }
    }

    if let Some(word_forms) = &word_result.word_forms {
        let values: Vec<String> = word_forms
            .iter()
            .filter_map(|form| {
                let value = form.value.as_deref()?.trim();
                if value.is_empty() {
                    return None;
                }
                let name = form.name.as_deref().unwrap_or("").trim();
                Some(if name.is_empty() {
                    value.to_string()
                } else {
                    format!("{name}: {value}")
                })
            })
            .collect();
        if !values.is_empty() {
            sections.push(format!("Forms: {}", values.join("; ")));
        }
    }

    if let Some(synonyms) = &word_result.synonyms {
        let values: Vec<String> = synonyms
            .iter()
            .filter_map(|synonym| {
                let words: Vec<&str> = synonym
                    .words
                    .as_deref()
                    .unwrap_or(&[])
                    .iter()
                    .map(String::as_str)
                    .filter(|word| !word.trim().is_empty())
                    .collect();
                if words.is_empty() {
                    return None;
                }
                let part = synonym.part_of_speech.as_deref().unwrap_or("").trim();
                let meaning = synonym.meaning.as_deref().unwrap_or("").trim();
                let prefix = [part, meaning]
                    .into_iter()
                    .filter(|value| !value.is_empty())
                    .collect::<Vec<_>>()
                    .join(" ");
                Some(if prefix.is_empty() {
                    words.join(", ")
                } else {
                    format!("{prefix}: {}", words.join(", "))
                })
            })
            .collect();
        if !values.is_empty() {
            sections.push(format!("Synonyms: {}", values.join(" | ")));
        }
    }

    if !sections.is_empty() {
        if !body.is_empty() {
            body.push('\n');
        }
        body.push_str(&sections.join("\n"));
    }
}

fn format_phonetic_text(text: &str) -> String {
    if text.starts_with('/') && text.ends_with('/') {
        text.to_string()
    } else {
        format!("/{text}/")
    }
}

fn service_icon(service_id: &str) -> IconToken {
    match service_id {
        "google" | "google_web" => IconToken::with_image(
            "service-google",
            include_bytes!("../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Google.scale-100.png"),
        ),
        "bing" => IconToken::with_image(
            "service-bing",
            include_bytes!("../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Bing.scale-100.png"),
        ),
        "windows-local-ai" => IconToken::with_image(
            "service-local-ai",
            include_bytes!(
                "../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/windows-local-ai.scale-100.png"
            ),
        ),
        "openai" => IconToken::with_image(
            "service-ai",
            include_bytes!("../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/OpenAI.scale-100.png"),
        ),
        "deepseek" => IconToken::with_image(
            "service-deepseek",
            include_bytes!("../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/DeepSeek.scale-100.png"),
        ),
        "deepl" => IconToken::with_image(
            "service-deepl",
            include_bytes!("../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/DeepL.scale-100.png"),
        ),
        "ollama" => IconToken::with_image(
            "service-ollama",
            include_bytes!("../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Ollama.scale-100.png"),
        ),
        "zhipu" => IconToken::with_image(
            "service-zhipu",
            include_bytes!("../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Zhipu.scale-100.png"),
        ),
        "groq" => IconToken::with_image(
            "service-groq",
            include_bytes!("../../../../dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Groq.scale-100.png"),
        ),
        service_id if service_id.starts_with("mdx::") => {
            IconToken::with_glyph("service-mdx", '\u{E8D5}')
        }
        _ => icon::translate(),
    }
}

fn toggle_result_expanded(results: &mut [TranslationResultPreview], id: &str) {
    if let Some(result) = results.iter_mut().find(|result| result.id == id) {
        if result.demoted {
            return;
        }
        result.expanded = !result.expanded;
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct LongDocumentState {
    pub source_text: String,
    pub selected_file: String,
    pub source_language: String,
    pub target_language: String,
    pub service: String,
    pub service_combo_state: ControlState,
    pub input_mode: String,
    pub output_mode: String,
    pub concurrency: String,
    pub page_range: String,
    pub two_pass_context: bool,
    pub output_folder: String,
    pub status_text: String,
    pub is_translating: bool,
    pub active_query_id: Option<u64>,
    pub last_output_path: Option<String>,
    pub last_error: Option<String>,
    pub progress_percentage: Option<f64>,
    pub progress_detail: Option<String>,
    pub last_translated_block: Option<String>,
    pub history: Vec<TranslationResultPreview>,
}

impl Default for LongDocumentState {
    fn default() -> Self {
        Self {
            source_text: String::new(),
            selected_file: "No file selected".to_string(),
            source_language: "auto".to_string(),
            target_language: "zh-Hans".to_string(),
            service: "openai".to_string(),
            service_combo_state: ControlState::default(),
            input_mode: "pdf".to_string(),
            output_mode: "mono".to_string(),
            concurrency: "4".to_string(),
            page_range: String::new(),
            two_pass_context: true,
            output_folder: "(same as input file folder)".to_string(),
            status_text: "Idle".to_string(),
            is_translating: false,
            active_query_id: None,
            last_output_path: None,
            last_error: None,
            progress_percentage: None,
            progress_detail: None,
            last_translated_block: None,
            history: vec![TranslationResultPreview::new(
                "sample-history",
                "Recent document",
                "No completed long document translation yet.",
            )],
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FloatingWindowState {
    pub title: String,
    pub text: String,
    pub source_language: String,
    pub target_language: String,
    pub target_language_manually_selected: bool,
    pub detected_language: Option<String>,
    pub pinned: bool,
    pub status_text: String,
    pub current_quick_query_mode: QuickQueryMode,
    pub grammar_correction_fallback: bool,
    pub is_translating: bool,
    pub services_completed: usize,
    pub active_query_id: Option<u64>,
    pub active_query_service_count: usize,
    pub active_query_success_count: usize,
    pub translate_button_state: ControlState,
    pub results: Vec<TranslationResultPreview>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct PopButtonAnchor {
    pub x: i32,
    pub y: i32,
}

impl PopButtonAnchor {
    pub const fn new(x: i32, y: i32) -> Self {
        Self { x, y }
    }

    pub const fn window_position_dips(self) -> (f32, f32) {
        ((self.x + 8) as f32, (self.y - 32) as f32)
    }
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct PopButtonState {
    pub pending_text: Option<String>,
    pub visible: bool,
    pub anchor: Option<PopButtonAnchor>,
    pub generation: u64,
}

impl PopButtonState {
    pub fn clear(&mut self) {
        self.pending_text = None;
        self.visible = false;
        self.anchor = None;
    }
}

impl FloatingWindowState {
    pub fn mini_demo() -> Self {
        Self {
            title: "Quick Translate".to_string(),
            text: "Oh, I am mini window".to_string(),
            source_language: "auto".to_string(),
            target_language: "zh-Hans".to_string(),
            target_language_manually_selected: false,
            detected_language: None,
            pinned: false,
            status_text: String::new(),
            current_quick_query_mode: QuickQueryMode::Translation,
            grammar_correction_fallback: false,
            is_translating: false,
            services_completed: 1,
            active_query_id: None,
            active_query_service_count: 0,
            active_query_success_count: 0,
            translate_button_state: ControlState::default(),
            results: vec![TranslationResultPreview::new(
                "google",
                "Google Translate",
                "Sample compact translation result.",
            )
            .latency_ms(649)],
        }
    }

    pub fn fixed_demo() -> Self {
        Self {
            title: "Fixed Translate".to_string(),
            text: "hello, I am fixed window".to_string(),
            source_language: "auto".to_string(),
            target_language: "zh-Hans".to_string(),
            target_language_manually_selected: false,
            detected_language: None,
            pinned: true,
            status_text: String::new(),
            current_quick_query_mode: QuickQueryMode::Translation,
            grammar_correction_fallback: false,
            is_translating: false,
            services_completed: 1,
            active_query_id: None,
            active_query_service_count: 0,
            active_query_success_count: 0,
            translate_button_state: ControlState::default(),
            results: vec![TranslationResultPreview::new(
                "google",
                "Google Translate",
                "Sample fixed translation result.",
            )
            .latency_ms(852)],
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct ImportedMdxDictionary {
    pub service_id: String,
    pub display_name: String,
    pub file_path: String,
    pub is_encrypted: bool,
    pub regcode: Option<String>,
    pub email: Option<String>,
    pub mdd_file_paths: Vec<String>,
}

impl ImportedMdxDictionary {
    pub fn snapshot(&self) -> ImportedMdxDictionarySnapshot {
        ImportedMdxDictionarySnapshot {
            service_id: self.service_id.clone(),
            display_name: self.display_name.clone(),
            file_path: self.file_path.clone(),
            is_encrypted: self.is_encrypted,
            regcode: self.regcode.clone(),
            email: self.email.clone(),
            mdd_file_paths: self.mdd_file_paths.clone(),
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct LocalDictionarySuggestion {
    pub key: String,
    pub dictionary_name: String,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowServiceSetting {
    pub service_id: String,
    pub display_name: String,
    pub enabled: bool,
    pub enabled_query: bool,
    pub configured: bool,
}

impl WindowServiceSetting {
    fn new(service_id: impl Into<String>, display_name: impl Into<String>) -> Self {
        Self {
            service_id: service_id.into(),
            display_name: display_name.into(),
            enabled: true,
            enabled_query: true,
            configured: true,
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ServiceProviderField {
    ApiKey,
    Endpoint,
    Model,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ServiceProviderSetting {
    pub service_id: String,
    pub api_key: String,
    pub endpoint: String,
    pub model: String,
    pub status: String,
}

impl ServiceProviderSetting {
    pub(crate) fn new(
        service_id: impl Into<String>,
        endpoint: impl Into<String>,
        model: impl Into<String>,
    ) -> Self {
        Self {
            service_id: service_id.into(),
            api_key: String::new(),
            endpoint: endpoint.into(),
            model: model.into(),
            status: "Not tested".to_string(),
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct SettingsState {
    pub selected_section: SettingsSection,
    pub hovered_section: Option<SettingsSection>,
    pub pressed_section: Option<SettingsSection>,
    pub tab_switching: bool,
    pub scrollbars_visible: bool,
    pub unsaved_changes: bool,
    pub show_unsaved_changes_dialog: bool,
    pub save_error_message: Option<String>,
    pub theme: ThemeMode,
    pub ui_language: String,
    pub first_language: String,
    pub second_language: String,
    pub selected_languages: Vec<String>,
    pub translation_languages_expanded: bool,
    pub tts_speed_slider_state: ControlState,
    pub auto_play_translation_toggle_state: ControlState,
    pub import_mdx_button_state: ControlState,
    pub international_services_toggle_state: ControlState,
    pub deepl_service_expander_state: ControlState,
    pub auto_select_target_language: bool,
    pub minimize_to_tray: bool,
    pub start_minimized: bool,
    pub monitor_clipboard: bool,
    pub mouse_selection_translate: bool,
    pub mouse_selection_excluded_apps: String,
    pub launch_at_startup: bool,
    pub shell_context_menu: bool,
    pub enable_international_services: bool,
    pub hide_empty_service_results: bool,
    pub tts_speed: String,
    pub auto_play_translation: bool,
    pub ocr_language: String,
    pub ocr_engine: String,
    pub ocr_api_key: String,
    pub ocr_endpoint: String,
    pub ocr_model: String,
    pub ocr_system_prompt: String,
    pub ocr_test_result: String,
    pub layout_detection_mode: String,
    pub vision_layout_service: String,
    pub layout_model_status: String,
    pub cjk_font_status: String,
    /// Async lifecycle of the settings runtime-status check (model/font on-disk
    /// availability). `is_loading()` drives the entry loading overlay; the
    /// settled value carries the resolved availability.
    pub settings_runtime: Loadable<crate::settings_status::SettingsRuntimeStatus>,
    pub formula_font_pattern: String,
    pub formula_char_pattern: String,
    pub translation_cache_enabled: bool,
    pub translation_cache_status: String,
    pub custom_translation_prompt: String,
    pub proxy_enabled: bool,
    pub proxy_url: String,
    pub proxy_bypass_local: bool,
    pub deepl_api_key: String,
    pub deepl_use_free_api: bool,
    pub deepl_use_quality_optimized: bool,
    pub open_ai_api_key: String,
    pub open_ai_endpoint: String,
    pub open_ai_model: String,
    pub open_ai_api_format_override: String,
    pub open_ai_test_status: String,
    pub device_id: String,
    pub device_token: String,
    pub ollama_endpoint: String,
    pub ollama_model: String,
    pub ollama_status: String,
    pub local_ai_provider: String,
    pub local_ai_status: String,
    pub local_ai_prepare_progress: String,
    pub foundry_local_endpoint: String,
    pub foundry_local_model: String,
    pub foundry_local_status: String,
    pub open_vino_device: String,
    pub open_vino_status: String,
    pub open_vino_download_progress: String,
    pub service_provider_settings: Vec<ServiceProviderSetting>,
    pub expanded_service_configurations: Vec<String>,
    pub caiyun_api_key: String,
    pub caiyun_status: String,
    pub niu_trans_api_key: String,
    pub niu_trans_status: String,
    pub youdao_app_key: String,
    pub youdao_app_secret: String,
    pub youdao_use_official_api: bool,
    pub youdao_status: String,
    pub volcano_access_key_id: String,
    pub volcano_secret_access_key: String,
    pub volcano_status: String,
    pub show_main_hotkey: HotkeySetting,
    pub translate_clipboard_hotkey: HotkeySetting,
    pub show_mini_hotkey: HotkeySetting,
    pub show_fixed_hotkey: HotkeySetting,
    pub ocr_translate_hotkey: HotkeySetting,
    pub silent_ocr_hotkey: HotkeySetting,
    pub mini_auto_close: bool,
    pub fixed_always_on_top: bool,
    pub main_window_reorder_mode: bool,
    pub mini_window_reorder_mode: bool,
    pub fixed_window_reorder_mode: bool,
    pub main_window_services: Vec<WindowServiceSetting>,
    pub mini_window_services: Vec<WindowServiceSetting>,
    pub fixed_window_services: Vec<WindowServiceSetting>,
    pub local_dictionary_suggestions: bool,
    pub imported_mdx_dictionaries: Vec<ImportedMdxDictionary>,
    pub pending_mdx_delete_service_id: Option<String>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct HotkeySetting {
    pub shortcut: String,
    pub enabled: bool,
}

impl HotkeySetting {
    pub fn enabled(shortcut: impl Into<String>) -> Self {
        Self {
            shortcut: shortcut.into(),
            enabled: true,
        }
    }
}

impl Default for SettingsState {
    fn default() -> Self {
        Self {
            selected_section: SettingsSection::General,
            hovered_section: None,
            pressed_section: None,
            tab_switching: false,
            scrollbars_visible: false,
            unsaved_changes: false,
            show_unsaved_changes_dialog: false,
            save_error_message: None,
            theme: ThemeMode::System,
            ui_language: "en-US".to_string(),
            first_language: "zh".to_string(),
            second_language: "en".to_string(),
            selected_languages: default_selected_languages(),
            translation_languages_expanded: false,
            tts_speed_slider_state: ControlState::default(),
            auto_play_translation_toggle_state: ControlState::default(),
            import_mdx_button_state: ControlState::default(),
            international_services_toggle_state: ControlState::default(),
            deepl_service_expander_state: ControlState::default(),
            auto_select_target_language: true,
            minimize_to_tray: true,
            start_minimized: false,
            monitor_clipboard: false,
            mouse_selection_translate: false,
            mouse_selection_excluded_apps: "code".to_string(),
            launch_at_startup: false,
            shell_context_menu: false,
            enable_international_services: true,
            hide_empty_service_results: false,
            tts_speed: "1.0".to_string(),
            auto_play_translation: false,
            ocr_language: "auto".to_string(),
            ocr_engine: "WindowsNative".to_string(),
            ocr_api_key: String::new(),
            ocr_endpoint: String::new(),
            ocr_model: String::new(),
            ocr_system_prompt: DEFAULT_OCR_SYSTEM_PROMPT.to_string(),
            ocr_test_result: "Not tested".to_string(),
            layout_detection_mode: "Auto".to_string(),
            vision_layout_service: "gemini".to_string(),
            layout_model_status: "Not downloaded".to_string(),
            cjk_font_status: "Not downloaded".to_string(),
            settings_runtime: Loadable::Idle,
            formula_font_pattern: String::new(),
            formula_char_pattern: String::new(),
            translation_cache_enabled: true,
            translation_cache_status: "Ready".to_string(),
            custom_translation_prompt: String::new(),
            proxy_enabled: false,
            proxy_url: String::new(),
            proxy_bypass_local: true,
            deepl_api_key: String::new(),
            deepl_use_free_api: true,
            deepl_use_quality_optimized: false,
            open_ai_api_key: String::new(),
            open_ai_endpoint: DEFAULT_OPENAI_ENDPOINT.to_string(),
            open_ai_model: DEFAULT_OPENAI_MODEL.to_string(),
            open_ai_api_format_override: "Auto".to_string(),
            open_ai_test_status: "Not tested".to_string(),
            device_id: String::new(),
            device_token: String::new(),
            ollama_endpoint: DEFAULT_OLLAMA_ENDPOINT.to_string(),
            ollama_model: DEFAULT_OLLAMA_MODEL.to_string(),
            ollama_status: "Not refreshed".to_string(),
            local_ai_provider: local_ai_provider_modes::AUTO.to_string(),
            local_ai_status: local_ai_provider_status(local_ai_provider_modes::AUTO).to_string(),
            local_ai_prepare_progress: "Idle".to_string(),
            foundry_local_endpoint: String::new(),
            foundry_local_model: DEFAULT_FOUNDRY_LOCAL_MODEL.to_string(),
            foundry_local_status: "Endpoint auto-detected at runtime".to_string(),
            open_vino_device: DEFAULT_OPENVINO_DEVICE.to_string(),
            open_vino_status: "Model not downloaded".to_string(),
            open_vino_download_progress: "Idle".to_string(),
            service_provider_settings: default_service_provider_settings(),
            expanded_service_configurations: Vec::new(),
            caiyun_api_key: String::new(),
            caiyun_status: "Not tested".to_string(),
            niu_trans_api_key: String::new(),
            niu_trans_status: "Not tested".to_string(),
            youdao_app_key: String::new(),
            youdao_app_secret: String::new(),
            youdao_use_official_api: false,
            youdao_status: "Web dictionary mode".to_string(),
            volcano_access_key_id: String::new(),
            volcano_secret_access_key: String::new(),
            volcano_status: "Not tested".to_string(),
            show_main_hotkey: HotkeySetting::enabled("Ctrl+Alt+T"),
            translate_clipboard_hotkey: HotkeySetting::enabled("Ctrl+Alt+D"),
            show_mini_hotkey: HotkeySetting::enabled("Ctrl+Alt+M"),
            show_fixed_hotkey: HotkeySetting::enabled("Ctrl+Alt+F"),
            ocr_translate_hotkey: HotkeySetting::enabled("Ctrl+Alt+S"),
            silent_ocr_hotkey: HotkeySetting::enabled("Ctrl+Alt+Shift+S"),
            mini_auto_close: true,
            fixed_always_on_top: true,
            main_window_reorder_mode: false,
            mini_window_reorder_mode: false,
            fixed_window_reorder_mode: false,
            main_window_services: default_main_window_services(),
            mini_window_services: default_floating_window_services(),
            fixed_window_services: default_floating_window_services(),
            local_dictionary_suggestions: true,
            imported_mdx_dictionaries: Vec::new(),
            pending_mdx_delete_service_id: None,
        }
    }
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct BrowserSupportState {
    pub chrome_installed: bool,
    pub firefox_installed: bool,
    pub loaded: bool,
    pub last_error: Option<String>,
}

impl BrowserSupportState {
    pub fn from_status(status: &crate::browser_registrar::StatusOutput) -> Self {
        Self {
            chrome_installed: status.chrome.installed,
            firefox_installed: status.firefox.installed,
            loaded: true,
            last_error: None,
        }
    }

    pub fn failed(error: impl Into<String>) -> Self {
        Self {
            loaded: true,
            last_error: Some(error.into()),
            ..Self::default()
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct EasydictUiState {
    pub mode: AppMode,
    pub settings_open: bool,
    pub next_query_id: u64,
    pub next_ocr_query_id: u64,
    pub active_query_id: Option<u64>,
    pub active_ocr_query_id: Option<u64>,
    pub active_ocr_mode: Option<crate::ocr::OcrMode>,
    pub pending_ocr_mode: Option<crate::ocr::OcrMode>,
    pub capture_interaction: crate::screen_capture::CaptureInteractionState,
    pub capture_window_detector: crate::screen_capture::WindowDetector,
    pub capture_selection: Option<crate::screen_capture::CaptureRect>,
    /// Frozen desktop screenshot shown under the capture overlay's dim mask,
    /// like the WinUI ScreenCaptureWindow.
    pub capture_background: Option<CaptureBackground>,
    pub translation_cache: crate::translation_cache::TranslationMemoryCache,
    pub pending_quick_translate_cache_requests:
        HashMap<(u64, String), crate::translation_cache::TranslationCacheRequest>,
    pub active_query_service_count: usize,
    pub active_query_success_count: usize,
    pub connection_status: ConnectionStatus,
    pub status_text: String,
    pub ocr_status_text: String,
    pub last_ocr_text: Option<String>,
    pub last_ocr_error: Option<String>,
    pub source_text: String,
    pub detected_language: Option<String>,
    pub source_language: String,
    pub target_language: String,
    pub target_language_manually_selected: bool,
    pub current_quick_query_mode: QuickQueryMode,
    pub grammar_correction_fallback: bool,
    pub is_translating: bool,
    pub mode_overlay_active: bool,
    pub services_completed: usize,
    pub last_result_action: Option<ResultActionIntent>,
    pub last_opened_settings_link: Option<SettingsLink>,
    pub source_text_focused: bool,
    pub source_text_state: ControlState,
    pub main_translate_button_state: ControlState,
    pub next_suggestion_query_id: u64,
    pub active_suggestion_query_id: Option<u64>,
    pub local_dictionary_suggestion_query: Option<String>,
    pub local_dictionary_suggestions: Vec<LocalDictionarySuggestion>,
    pub local_dictionary_suggestion_active_index: Option<usize>,
    pub local_dictionary_suggestion_error: Option<String>,
    pub results: Vec<TranslationResultPreview>,
    pub long_document: LongDocumentState,
    pub browser_support: BrowserSupportState,
    pub settings: SettingsState,
    pub saved_settings: SettingsState,
    pub pop_button: PopButtonState,
    pub mouse_selection_producer: MouseSelectionProducer,
    pub mini: FloatingWindowState,
    pub fixed: FloatingWindowState,
}

impl Default for EasydictUiState {
    fn default() -> Self {
        Self {
            mode: AppMode::QuickTranslate,
            settings_open: false,
            next_query_id: 1,
            next_ocr_query_id: 1,
            active_query_id: None,
            active_ocr_query_id: None,
            active_ocr_mode: None,
            pending_ocr_mode: None,
            capture_interaction: crate::screen_capture::CaptureInteractionState::new(),
            capture_window_detector: crate::screen_capture::WindowDetector::new(),
            capture_selection: None,
            capture_background: None,
            translation_cache: crate::translation_cache::TranslationMemoryCache::new(),
            pending_quick_translate_cache_requests: HashMap::new(),
            active_query_service_count: 0,
            active_query_success_count: 0,
            connection_status: ConnectionStatus::Disconnected,
            status_text: "Disconnected".to_string(),
            ocr_status_text: "Idle".to_string(),
            last_ocr_text: None,
            last_ocr_error: None,
            source_text: "Artificial intelligence is transforming how we work and live".to_string(),
            detected_language: None,
            source_language: "auto".to_string(),
            target_language: "auto".to_string(),
            target_language_manually_selected: false,
            current_quick_query_mode: QuickQueryMode::Translation,
            grammar_correction_fallback: false,
            is_translating: false,
            mode_overlay_active: false,
            services_completed: 3,
            last_result_action: None,
            last_opened_settings_link: None,
            source_text_focused: true,
            source_text_state: ControlState::default().focused(true),
            main_translate_button_state: ControlState::default(),
            next_suggestion_query_id: 1,
            active_suggestion_query_id: None,
            local_dictionary_suggestion_query: None,
            local_dictionary_suggestions: Vec::new(),
            local_dictionary_suggestion_active_index: None,
            local_dictionary_suggestion_error: None,
            results: vec![
                TranslationResultPreview::new(
                    "google",
                    "Google Translate",
                    "人工智能正在改变我们的工作和生活方式",
                )
                .latency_ms(1228),
                TranslationResultPreview::new(
                    "bing",
                    "Bing Translate",
                    "人工智能正在改变我们的工作和生活方式",
                )
                .latency_ms(1108),
                TranslationResultPreview::new(
                    "openai",
                    "OpenAI",
                    "人工智能正在改变我们的工作和生活方式。",
                )
                .grammar_capable(true)
                .streaming_capable(true)
                .latency_ms(1032),
            ],
            long_document: LongDocumentState::default(),
            browser_support: BrowserSupportState::default(),
            settings: SettingsState::default(),
            saved_settings: SettingsState::default(),
            pop_button: PopButtonState::default(),
            mouse_selection_producer: MouseSelectionProducer::default(),
            mini: FloatingWindowState::mini_demo(),
            fixed: FloatingWindowState::fixed_demo(),
        }
    }
}

impl EasydictUiState {
    pub fn preview(scenario: PreviewScenario, theme: ThemeMode) -> Self {
        let mut state = Self::default();
        state.settings.theme = theme;
        state.connection_status = ConnectionStatus::Connected;
        state.status_text = "Ready".to_string();
        state.source_text_focused = false;
        state.source_text_state = ControlState::default();
        state.saved_settings = sanitized_settings_snapshot(&state.settings);

        match scenario {
            PreviewScenario::Initial => {
                apply_initial_quick_translate_preview(&mut state);
            }
            PreviewScenario::BeforeTranslate => {
                apply_before_translate_preview(&mut state);
            }
            PreviewScenario::Loading => {
                state.source_text =
                    "Streaming translation should keep the input responsive".to_string();
                state.detected_language = Some("Detected: English".to_string());
                state.is_translating = true;
                state.connection_status = ConnectionStatus::Connected;
                state.status_text = "Translating".to_string();
                state.services_completed = 1;
                state.results = vec![
                    TranslationResultPreview::new(
                        "google",
                        "Google Translate",
                        "流式翻译应保持输入响应",
                    )
                    .status(ResultStatus::Ready)
                    .latency_ms(612),
                    TranslationResultPreview::new("bing", "Bing Translate", "Streaming...")
                        .status(ResultStatus::Streaming),
                    TranslationResultPreview::new("openai", "OpenAI", "")
                        .streaming_capable(true)
                        .status(ResultStatus::Loading),
                ];
            }
            PreviewScenario::AfterTranslate => {
                state.connection_status = ConnectionStatus::Connected;
                state.status_text = "Ready".to_string();
            }
            PreviewScenario::Error => {
                state.connection_status = ConnectionStatus::Error;
                state.status_text = "Error".to_string();
                state.services_completed = 2;
                state.results = vec![
                    TranslationResultPreview::new(
                        "google",
                        "Google Translate",
                        "人工智能正在改变我们的工作和生活方式",
                    )
                    .latency_ms(1228),
                    TranslationResultPreview::new(
                        "bing",
                        "Bing Translate",
                        "Network error: request timed out.",
                    )
                    .status(ResultStatus::Error),
                    TranslationResultPreview::new("openai", "OpenAI", "")
                        .streaming_capable(true)
                        .manual_query(),
                ];
            }
            PreviewScenario::ModeOverlay => {
                state.mode = AppMode::LongDocument;
                state.mode_overlay_active = true;
                state.is_translating = true;
                state.status_text = "Switching mode".to_string();
            }
            PreviewScenario::PrimaryHover => {
                apply_initial_quick_translate_preview(&mut state);
                state.source_text_focused = false;
                state.source_text_state = ControlState::default();
                state.main_translate_button_state = ControlState::default().hovered(true);
            }
            PreviewScenario::PrimaryPressed => {
                apply_initial_quick_translate_preview(&mut state);
                state.source_text_focused = false;
                state.source_text_state = ControlState::default();
                state.main_translate_button_state =
                    ControlState::default().hovered(true).pressed(true);
            }
            PreviewScenario::SourceInputHover => {
                apply_before_translate_preview(&mut state);
                state.source_text_focused = false;
                state.source_text_state = ControlState::default().hovered(true);
            }
            PreviewScenario::SourceInputFocused => {
                apply_before_translate_preview(&mut state);
                state.source_text_focused = true;
                state.source_text_state = ControlState::default().focused(true);
            }
            PreviewScenario::ResultHeaderHover => {
                apply_initial_quick_translate_preview(&mut state);
                state.source_text_focused = false;
                state.source_text_state = ControlState::default();
                if let Some(result) = state.results.first_mut() {
                    result.header_state = ControlState::default().hovered(true);
                }
            }
            PreviewScenario::ResultCollapsed => {
                state.results = preview_waiting_results();
                state.connection_status = ConnectionStatus::Connected;
                state.status_text = "Ready".to_string();
                if let Some(result) = state.results.first_mut() {
                    result.expanded = false;
                }
            }
            PreviewScenario::LocalDictionarySuggestions => {
                state.source_text = "please app".to_string();
                state.detected_language = Some("Detected: English".to_string());
                state.local_dictionary_suggestion_query = Some("app".to_string());
                state.local_dictionary_suggestions = vec![
                    LocalDictionarySuggestion {
                        key: "apple".to_string(),
                        dictionary_name: "Demo Dictionary".to_string(),
                    },
                    LocalDictionarySuggestion {
                        key: "application".to_string(),
                        dictionary_name: "Demo Dictionary".to_string(),
                    },
                ];
                state.local_dictionary_suggestion_active_index = Some(0);
            }
            PreviewScenario::LongDocument => {
                state.mode = AppMode::LongDocument;
                state.connection_status = ConnectionStatus::Connected;
                state.status_text = "Ready".to_string();
            }
            PreviewScenario::LongDocumentRunning => {
                state.mode = AppMode::LongDocument;
                state.connection_status = ConnectionStatus::Connected;
                state.status_text = "Translating document".to_string();
                state.long_document.source_text =
                    "Long document translation should expose running progress.".to_string();
                state.long_document.selected_file = "research-paper.pdf".to_string();
                state.long_document.input_mode = "pdf".to_string();
                state.long_document.output_mode = "bilingual".to_string();
                state.long_document.service = "openai".to_string();
                state.long_document.page_range = "1-18".to_string();
                state.long_document.status_text = "Translating document".to_string();
                state.long_document.is_translating = true;
                state.long_document.active_query_id = Some(42);
                state.long_document.progress_percentage = Some(42.0);
                state.long_document.progress_detail =
                    Some("Translating page 8 of 18 with OpenAI".to_string());
                state.long_document.last_translated_block =
                    Some("Abstract and introduction completed".to_string());
            }
            PreviewScenario::LongDocumentError => {
                state.mode = AppMode::LongDocument;
                state.connection_status = ConnectionStatus::Error;
                state.status_text = "Long document failed".to_string();
                state.long_document.source_text =
                    "Failed long document previews should keep retry visible.".to_string();
                state.long_document.selected_file = "scanned-report.pdf".to_string();
                state.long_document.input_mode = "pdf".to_string();
                state.long_document.output_mode = "both".to_string();
                state.long_document.service = "deepseek".to_string();
                state.long_document.status_text =
                    "Failed: page 12 layout detection timed out".to_string();
                state.long_document.last_error =
                    Some("page 12 layout detection timed out".to_string());
                state.long_document.progress_percentage = Some(67.0);
                state.long_document.progress_detail =
                    Some("Retry failed blocks after checking OCR/Layout settings.".to_string());
                state.long_document.last_translated_block =
                    Some("Sections 1-3 were preserved in the draft output".to_string());
                state.long_document.history = vec![TranslationResultPreview::new(
                    "long-doc-error",
                    "scanned-report.pdf",
                    "page 12 layout detection timed out",
                )
                .status(ResultStatus::Error)];
            }
        }

        state
    }

    pub fn preview_from_env() -> Self {
        let scenario = std::env::var("EASYDICT_PREVIEW_SCENARIO")
            .ok()
            .map(|value| PreviewScenario::from_id(&value))
            .unwrap_or(PreviewScenario::Initial);
        let theme = std::env::var("EASYDICT_PREVIEW_THEME")
            .ok()
            .map(|value| theme_from_id(&value))
            .unwrap_or(ThemeMode::System);

        let mut state = Self::preview(scenario, theme);
        if let Ok(value) = std::env::var("EASYDICT_PREVIEW_UI_LANGUAGE") {
            let value = value.trim();
            if !value.is_empty() {
                state.settings.ui_language = value.to_string();
                localize_preview_status_text(&mut state);
                state.saved_settings = sanitized_settings_snapshot(&state.settings);
            }
        }
        let mut settings_seed_changed = false;
        if let Ok(value) = std::env::var("EASYDICT_PREVIEW_SETTINGS_MOUSE_SELECTION_TRANSLATE") {
            state.settings.mouse_selection_translate = env_truthy(&value);
            settings_seed_changed = true;
        }
        if let Ok(value) = std::env::var("EASYDICT_PREVIEW_SETTINGS_FIXED_ALWAYS_ON_TOP") {
            state.settings.fixed_always_on_top = env_truthy(&value);
            settings_seed_changed = true;
        }
        if let Ok(value) = std::env::var("EASYDICT_PREVIEW_SETTINGS_HIDE_EMPTY_SERVICE_RESULTS") {
            state.settings.hide_empty_service_results = env_truthy(&value);
            settings_seed_changed = true;
        }
        if std::env::var("EASYDICT_PREVIEW_SETTINGS_IMPORTED_MDX")
            .ok()
            .is_some_and(|value| env_truthy(&value))
        {
            apply_preview_imported_mdx_dictionary(&mut state.settings);
            settings_seed_changed = true;
        }
        if settings_seed_changed {
            state.saved_settings = sanitized_settings_snapshot(&state.settings);
        }
        if let Ok(value) = std::env::var("EASYDICT_PREVIEW_MAIN_TRANSLATE_STATE") {
            state.main_translate_button_state = preview_control_state_from_id(&value);
        }
        if let Ok(value) = std::env::var("EASYDICT_PREVIEW_SOURCE_TEXT_STATE") {
            state.source_text_state = preview_control_state_from_id(&value);
            state.source_text_focused = state.source_text_state.focused;
        }
        if let Ok(value) = std::env::var("EASYDICT_PREVIEW_RESULT_HEADER_STATE") {
            let service_id = std::env::var("EASYDICT_PREVIEW_RESULT_HEADER_SERVICE_ID")
                .ok()
                .filter(|value| !value.trim().is_empty())
                .unwrap_or_else(|| "google".to_string());
            if let Some(result) = state
                .results
                .iter_mut()
                .find(|result| result.id == service_id)
            {
                result.header_state = preview_control_state_from_id(&value);
            }
        }
        if let Ok(service_id) = std::env::var("EASYDICT_PREVIEW_RESULT_COLLAPSED_SERVICE_ID") {
            let service_id = service_id.trim();
            if !service_id.is_empty() {
                for result in &mut state.results {
                    if service_id.eq_ignore_ascii_case("all") || result.id == service_id {
                        result.expanded = false;
                    }
                }
            }
        }
        if let Ok(value) = std::env::var("EASYDICT_PREVIEW_LONG_DOC_INPUT_MODE") {
            state.long_document.input_mode = long_document_input_mode_from_preview(&value);
        }
        if let Ok(value) = std::env::var("EASYDICT_PREVIEW_LONG_DOC_OUTPUT_MODE") {
            state.long_document.output_mode = long_document_output_mode_from_preview(&value);
        }
        if let Ok(value) = std::env::var("EASYDICT_PREVIEW_LONG_DOC_SERVICE_STATE") {
            state.long_document.service_combo_state = preview_control_state_from_id(&value);
        }
        if let Ok(value) = std::env::var("EASYDICT_PREVIEW_SETTINGS_TTS_SPEED_STATE") {
            state.settings.tts_speed_slider_state = preview_control_state_from_id(&value);
        }
        if let Ok(value) = std::env::var("EASYDICT_PREVIEW_SETTINGS_AUTO_PLAY_STATE") {
            state.settings.auto_play_translation_toggle_state =
                preview_control_state_from_id(&value);
        }
        if let Ok(value) = std::env::var("EASYDICT_PREVIEW_SETTINGS_IMPORT_MDX_STATE") {
            state.settings.import_mdx_button_state = preview_control_state_from_id(&value);
        }
        if let Ok(value) = std::env::var("EASYDICT_PREVIEW_SETTINGS_INTERNATIONAL_TOGGLE_STATE") {
            state.settings.international_services_toggle_state =
                preview_control_state_from_id(&value);
        }
        if let Ok(value) = std::env::var("EASYDICT_PREVIEW_SETTINGS_DEEPL_EXPANDER_STATE") {
            state.settings.deepl_service_expander_state = preview_control_state_from_id(&value);
        }
        if let Ok(value) =
            std::env::var("EASYDICT_PREVIEW_SETTINGS_EXPANDED_SERVICE_CONFIGURATIONS")
        {
            for service_id in value
                .split(',')
                .map(str::trim)
                .filter(|service_id| !service_id.is_empty())
            {
                if !state
                    .settings
                    .expanded_service_configurations
                    .iter()
                    .any(|existing| existing == service_id)
                {
                    state
                        .settings
                        .expanded_service_configurations
                        .push(service_id.to_string());
                }
            }
        }
        if let Ok(value) = std::env::var("EASYDICT_PREVIEW_SETTINGS_LOCAL_AI_PROVIDER") {
            let provider = normalize_local_ai_provider(&value);
            state.settings.local_ai_provider = provider;
            state.settings.local_ai_status =
                local_ai_provider_status(&state.settings.local_ai_provider).to_string();
        }
        if let Ok(value) = std::env::var("EASYDICT_PREVIEW_CAPTURE_OVERLAY_STATE") {
            apply_capture_overlay_preview(&mut state, &value);
            // Freeze the desktop like the real capture flow so the preview
            // overlay dims a screenshot instead of a flat surface.
            state.capture_background = capture_screen_background();
        }
        if let Ok(section) = std::env::var("EASYDICT_PREVIEW_SETTINGS_SECTION") {
            state.settings.selected_section = SettingsSection::from_id(&section);
            state.saved_settings = sanitized_settings_snapshot(&state.settings);
        }
        if let Ok(profile) = std::env::var("EASYDICT_PREVIEW_SETTINGS_VIEW_SERVICE_PROFILE") {
            apply_settings_view_service_preview_profile(&mut state.settings, &profile);
            state.saved_settings = sanitized_settings_snapshot(&state.settings);
        }
        if let Ok(section) = std::env::var("EASYDICT_PREVIEW_SETTINGS_HOVERED_SECTION") {
            state.settings.hovered_section = Some(SettingsSection::from_id(&section));
        }
        if let Ok(section) = std::env::var("EASYDICT_PREVIEW_SETTINGS_PRESSED_SECTION") {
            let section = SettingsSection::from_id(&section);
            state.settings.pressed_section = Some(section);
            state.settings.hovered_section.get_or_insert(section);
        }
        if std::env::var("EASYDICT_PREVIEW_SCROLL_PERCENT")
            .ok()
            .and_then(|value| value.trim().parse::<f32>().ok())
            .is_some_and(|value| value > 0.0)
        {
            state.settings.scrollbars_visible = true;
        }
        if std::env::var("EASYDICT_PREVIEW_SETTINGS_TAB_SWITCHING")
            .ok()
            .is_some_and(|value| env_truthy(&value))
        {
            state.settings.tab_switching = true;
        }

        if std::env::var("EASYDICT_PREVIEW_SETTINGS_OPEN")
            .ok()
            .is_some_and(|value| env_truthy(&value))
        {
            state.settings_open = true;
        }

        if std::env::var("EASYDICT_PREVIEW_SETTINGS_UNSAVED_DIALOG")
            .ok()
            .is_some_and(|value| env_truthy(&value))
        {
            state.settings.unsaved_changes = true;
            state.settings.show_unsaved_changes_dialog = true;
        }

        if std::env::var("EASYDICT_PREVIEW_TRANSLATION_LANGUAGES_EXPANDED")
            .ok()
            .is_some_and(|value| env_truthy(&value))
        {
            state.settings.translation_languages_expanded = true;
            state.saved_settings = sanitized_settings_snapshot(&state.settings);
        }
        if let Ok(value) = std::env::var("EASYDICT_PREVIEW_MINI_TRANSLATE_STATE") {
            state.mini.translate_button_state = preview_control_state_from_id(&value);
        }
        if let Ok(value) = std::env::var("EASYDICT_PREVIEW_FIXED_TRANSLATE_STATE") {
            state.fixed.translate_button_state = preview_control_state_from_id(&value);
        }
        if let Ok(value) = std::env::var("EASYDICT_PREVIEW_FLOATING_CONTENT") {
            if matches!(
                value.trim().to_ascii_lowercase().as_str(),
                "empty" | "blank" | "initial"
            ) {
                apply_empty_floating_preview(&mut state.mini);
                apply_empty_floating_preview(&mut state.fixed);
                // Match the .NET fixed window's initial rows: Bing plus the
                // imported MDX dictionary expander.
                state.fixed.results.push(
                    TranslationResultPreview::new("bing", "Bing Translate", "").expanded(false),
                );
                state.fixed.results.push(
                    TranslationResultPreview::new(
                        "mdx::collins-cobuild-english-usage",
                        PREVIEW_DOTNET_REFERENCE_MDX_DISPLAY_NAME,
                        "",
                    )
                    .expanded(false),
                );
            }
        }

        state
    }

    pub fn apply(&mut self, message: Message) {
        match message {
            Message::ModeChanged(id) => {
                self.mode = AppMode::from_id(&id);
            }
            Message::SourceTextChanged(value) => {
                self.source_text = value;
                self.source_text_focused = true;
                self.source_text_state = ControlState::default().focused(true);
            }
            Message::SourceTextSubmitted => {
                apply_active_local_dictionary_suggestion(self);
            }
            Message::LocalDictionarySuggestionsFinished(update) => {
                crate::local_dictionary::apply_local_dictionary_suggestion_update(self, update);
            }
            Message::ApplyLocalDictionarySuggestion(value) => {
                apply_local_dictionary_suggestion(self, &value);
            }
            Message::FocusLocalDictionarySuggestions => {
                focus_local_dictionary_suggestions(self);
            }
            Message::MoveLocalDictionarySuggestion(delta) => {
                move_local_dictionary_suggestion(self, delta);
            }
            Message::CommitLocalDictionarySuggestion => {
                apply_active_local_dictionary_suggestion(self);
            }
            Message::DismissLocalDictionarySuggestions => {
                dismiss_local_dictionary_suggestions(self);
            }
            Message::ExitLocalDictionarySuggestions => {
                exit_local_dictionary_suggestions(self);
            }
            Message::FloatingTextChanged(value) => {
                self.mini.text = value.clone();
                self.fixed.text = value;
            }
            Message::FloatingSurfaceTextChanged(surface, value) => {
                if let Some(floating) = floating_surface_mut(self, surface) {
                    floating.text = value;
                }
            }
            Message::LongDocumentSourceTextChanged(value) => {
                if !self.long_document.is_translating {
                    self.long_document.source_text = value;
                }
            }
            Message::SourceLanguageChanged(value) => {
                self.source_language = value;
            }
            Message::TargetLanguageChanged(value) => {
                self.target_language = value;
                self.target_language_manually_selected = true;
            }
            Message::FloatingSourceLanguageChanged(surface, value) => {
                if let Some(floating) = floating_surface_mut(self, surface) {
                    floating.source_language = value;
                }
            }
            Message::FloatingTargetLanguageChanged(surface, value) => {
                if let Some(floating) = floating_surface_mut(self, surface) {
                    floating.target_language = value;
                    floating.target_language_manually_selected = true;
                }
            }
            Message::LongDocumentSourceLanguageChanged(value) => {
                if !self.long_document.is_translating {
                    self.long_document.source_language = value;
                }
            }
            Message::LongDocumentTargetLanguageChanged(value) => {
                if !self.long_document.is_translating {
                    self.long_document.target_language = value;
                }
            }
            Message::LongDocumentServiceChanged(value) => {
                if !self.long_document.is_translating {
                    self.long_document.service = value;
                }
            }
            Message::LongDocumentInputModeChanged(value) => {
                if !self.long_document.is_translating {
                    self.long_document.input_mode = value;
                }
            }
            Message::LongDocumentOutputModeChanged(value) => {
                if !self.long_document.is_translating {
                    self.long_document.output_mode = value;
                }
            }
            Message::LongDocumentConcurrencyChanged(value) => {
                if !self.long_document.is_translating {
                    self.long_document.concurrency = value;
                }
            }
            Message::LongDocumentPageRangeChanged(value) => {
                if !self.long_document.is_translating {
                    self.long_document.page_range = value;
                }
            }
            Message::LongDocumentFileSelected(path) => {
                if !self.long_document.is_translating {
                    apply_long_document_file_selection(self, path);
                }
            }
            Message::LongDocumentOutputFolderSelected(path) => {
                if !self.long_document.is_translating {
                    apply_long_document_output_folder_selection(self, path);
                }
            }
            Message::MdxDictionarySelected(path) => {
                if apply_mdx_dictionary_selection(self, path) {
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::MdxDictionaryEmailChanged(service_id, value) => {
                if update_mdx_dictionary_email(&mut self.settings, &service_id, value) {
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::MdxDictionaryRegcodeChanged(service_id, value) => {
                if update_mdx_dictionary_regcode(&mut self.settings, &service_id, value) {
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::RescanMdxMddFiles(service_id) => {
                if rescan_mdx_mdd_files(&mut self.settings, &service_id) {
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::RequestDeleteMdxDictionary(service_id) => {
                if self
                    .settings
                    .imported_mdx_dictionaries
                    .iter()
                    .any(|dictionary| dictionary.service_id == service_id)
                {
                    self.settings.pending_mdx_delete_service_id = Some(service_id);
                }
            }
            Message::ConfirmDeleteMdxDictionary => {
                if let Some(service_id) = self.settings.pending_mdx_delete_service_id.take() {
                    if remove_mdx_dictionary(self, &service_id) {
                        mark_settings_changed(&mut self.settings);
                    }
                }
            }
            Message::CancelDeleteMdxDictionary => {
                self.settings.pending_mdx_delete_service_id = None;
            }
            Message::SettingsSectionChanged(id) => {
                self.settings.selected_section = SettingsSection::from_id(&id);
            }
            Message::OpenSettings => {
                self.settings_open = true;
                self.settings.show_unsaved_changes_dialog = false;
                self.settings.save_error_message = None;
                // Kick off the async runtime-status check (see lib.rs); the entry
                // loading overlay is shown until SettingsRuntimeStatusLoaded.
                self.settings.settings_runtime.begin();
                if !self.settings.unsaved_changes {
                    self.saved_settings = sanitized_settings_snapshot(&self.settings);
                }
            }
            Message::SettingsRuntimeStatusLoaded(status) => {
                self.settings.layout_model_status = status.layout_model.clone();
                self.settings.cjk_font_status = status.cjk_font.clone();
                if should_apply_windows_ai_runtime_status(&self.settings) {
                    self.settings.local_ai_status = status.windows_ai_status.clone();
                }
                if should_apply_foundry_runtime_status(&self.settings.foundry_local_status) {
                    self.settings.foundry_local_status = status.foundry_local_status.clone();
                }
                if self.settings.open_vino_download_progress == "Idle" {
                    self.settings.open_vino_status = status.open_vino_status.clone();
                    self.settings.open_vino_download_progress =
                        status.open_vino_download_progress.clone();
                }
                self.settings.settings_runtime.resolve(Ok(status));
            }
            Message::BuiltInAiDeviceRegistrationFinished(result) => {
                if let Ok(Some(token)) = result {
                    if !token.trim().is_empty() {
                        self.settings.device_token = token.clone();
                        self.saved_settings.device_token = token;
                    }
                }
            }
            Message::Back => {
                if self.settings.unsaved_changes {
                    self.settings.show_unsaved_changes_dialog = true;
                } else {
                    self.settings_open = false;
                }
            }
            Message::SaveSettingsChanges => {
                save_settings_changes(self);
            }
            Message::DiscardSettingsChanges => {
                discard_settings_changes(self);
            }
            Message::CancelSettingsChangesDialog => {
                self.settings.show_unsaved_changes_dialog = false;
            }
            Message::DismissSettingsError => {
                self.settings.save_error_message = None;
            }
            Message::OpenSettingsLink(link) => {
                self.last_opened_settings_link = Some(link);
            }
            Message::ThemeChanged(id) => {
                self.settings.theme = match id.as_str() {
                    "dark" => ThemeMode::Dark,
                    "minimal" => ThemeMode::Minimal,
                    "high-contrast" => ThemeMode::HighContrast,
                    "system" => ThemeMode::System,
                    _ => ThemeMode::Light,
                };
                mark_settings_changed(&mut self.settings);
            }
            Message::ToggleMinimizeToTray(value) => {
                self.settings.minimize_to_tray = value;
                mark_settings_changed(&mut self.settings);
            }
            Message::ToggleStartMinimized(value) => {
                self.settings.start_minimized = value;
                mark_settings_changed(&mut self.settings);
            }
            Message::ToggleMonitorClipboard(value) => {
                self.settings.monitor_clipboard = value;
                mark_settings_changed(&mut self.settings);
            }
            Message::ToggleMouseSelectionTranslate(value) => {
                self.settings.mouse_selection_translate = value;
                mark_settings_changed(&mut self.settings);
            }
            Message::MouseSelectionExcludedAppsChanged(value) => {
                if self.settings.mouse_selection_excluded_apps != value {
                    self.settings.mouse_selection_excluded_apps = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::ToggleLaunchAtStartup(value) => {
                if self.settings.launch_at_startup != value {
                    self.settings.launch_at_startup = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::ToggleShellContextMenu(value) => {
                self.settings.shell_context_menu = value;
                mark_settings_changed(&mut self.settings);
            }
            Message::ToggleInternationalServices(value) => {
                self.settings.enable_international_services = value;
                mark_settings_changed(&mut self.settings);
            }
            Message::ToggleHideEmptyServiceResults(value) => {
                self.settings.hide_empty_service_results = value;
                mark_settings_changed(&mut self.settings);
                apply_hide_empty_to_all_results(self, value);
            }
            Message::TtsSpeedChanged(value) => {
                if self.settings.tts_speed != value {
                    self.settings.tts_speed = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::ToggleAutoPlayTranslation(value) => {
                if self.settings.auto_play_translation != value {
                    self.settings.auto_play_translation = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::OcrEngineChanged(value) => {
                if self.settings.ocr_engine != value {
                    self.settings.ocr_engine = value;
                    apply_ocr_engine_defaults(&mut self.settings);
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::OcrApiKeyChanged(value) => {
                if self.settings.ocr_api_key != value {
                    self.settings.ocr_api_key = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::OcrEndpointChanged(value) => {
                if self.settings.ocr_endpoint != value {
                    self.settings.ocr_endpoint = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::OcrModelChanged(value) => {
                if self.settings.ocr_model != value {
                    self.settings.ocr_model = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::OcrSystemPromptChanged(value) => {
                if self.settings.ocr_system_prompt != value {
                    self.settings.ocr_system_prompt = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::TestOcrConnection => {
                self.settings.ocr_test_result = ocr_test_result(&self.settings);
            }
            Message::LayoutDetectionModeChanged(value) => {
                if self.settings.layout_detection_mode != value {
                    self.settings.layout_detection_mode = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::VisionLayoutServiceChanged(value) => {
                if self.settings.vision_layout_service != value {
                    self.settings.vision_layout_service = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::DownloadLayoutModel => {
                self.settings.layout_model_status = "Download queued (~75MB)".to_string();
            }
            Message::DeleteLayoutModel => {
                self.settings.layout_model_status = "Deleted".to_string();
            }
            Message::DownloadCjkFont => {
                self.settings.cjk_font_status = "Download queued".to_string();
            }
            Message::DeleteCjkFont => {
                self.settings.cjk_font_status = "Deleted".to_string();
            }
            Message::FormulaFontPatternChanged(value) => {
                if self.settings.formula_font_pattern != value {
                    self.settings.formula_font_pattern = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::FormulaCharPatternChanged(value) => {
                if self.settings.formula_char_pattern != value {
                    self.settings.formula_char_pattern = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::ToggleTranslationCache(value) => {
                if self.settings.translation_cache_enabled != value {
                    self.settings.translation_cache_enabled = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::ClearTranslationCache => {
                self.translation_cache.clear();
                self.pending_quick_translate_cache_requests.clear();
                self.settings.translation_cache_status = "Cleared".to_string();
            }
            Message::CustomTranslationPromptChanged(value) => {
                if self.settings.custom_translation_prompt != value {
                    self.settings.custom_translation_prompt = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::ToggleProxyEnabled(value) => {
                if self.settings.proxy_enabled != value {
                    self.settings.proxy_enabled = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::ProxyUrlChanged(value) => {
                if self.settings.proxy_url != value {
                    self.settings.proxy_url = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::ToggleProxyBypassLocal(value) => {
                if self.settings.proxy_bypass_local != value {
                    self.settings.proxy_bypass_local = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::DeepLApiKeyChanged(value) => {
                if self.settings.deepl_api_key != value {
                    self.settings.deepl_api_key = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::ToggleDeepLUseFreeApi(value) => {
                if self.settings.deepl_use_free_api != value {
                    self.settings.deepl_use_free_api = value;
                    if value {
                        self.settings.deepl_use_quality_optimized = false;
                    }
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::ToggleDeepLUseQualityOptimized(value) => {
                if self.settings.deepl_use_quality_optimized != value {
                    self.settings.deepl_use_quality_optimized = value;
                    if value {
                        self.settings.deepl_use_free_api = false;
                    }
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::OpenAIApiKeyChanged(value) => {
                if self.settings.open_ai_api_key != value {
                    self.settings.open_ai_api_key = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::OpenAIEndpointChanged(value) => {
                if self.settings.open_ai_endpoint != value {
                    self.settings.open_ai_endpoint = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::OpenAIModelChanged(value) => {
                if self.settings.open_ai_model != value {
                    self.settings.open_ai_model = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::OpenAIApiFormatChanged(value) => {
                if self.settings.open_ai_api_format_override != value {
                    self.settings.open_ai_api_format_override = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::TestOpenAI => {
                self.settings.open_ai_test_status =
                    format!("Test requested ({})", open_ai_format_label(&self.settings));
            }
            Message::OllamaEndpointChanged(value) => {
                if self.settings.ollama_endpoint != value {
                    self.settings.ollama_endpoint = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::OllamaModelChanged(value) => {
                if self.settings.ollama_model != value {
                    self.settings.ollama_model = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::RefreshOllamaModels => {
                self.settings.ollama_status = format!(
                    "Refresh requested from {}",
                    setting_or_default(&self.settings.ollama_endpoint, DEFAULT_OLLAMA_ENDPOINT)
                );
            }
            Message::TestOllama => {
                self.settings.ollama_status = format!(
                    "Test requested for {}",
                    setting_or_default(&self.settings.ollama_model, DEFAULT_OLLAMA_MODEL)
                );
            }
            Message::LocalAiProviderChanged(value) => {
                let provider = normalize_local_ai_provider(&value);
                if self.settings.local_ai_provider != provider {
                    self.settings.local_ai_provider = provider;
                    self.settings.local_ai_status =
                        local_ai_provider_status(&self.settings.local_ai_provider).to_string();
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::PrepareLocalAiModel => match self.settings.local_ai_provider.as_str() {
                local_ai_provider_modes::FOUNDRY_LOCAL => {
                    self.settings.local_ai_status =
                        local_ai_provider_status(local_ai_provider_modes::FOUNDRY_LOCAL)
                            .to_string();
                    self.settings.local_ai_prepare_progress =
                        "Starting Foundry Local service...".to_string();
                    self.settings.foundry_local_status =
                        "Starting Foundry Local service...".to_string();
                }
                local_ai_provider_modes::OPENVINO => {
                    self.settings.local_ai_status =
                        local_ai_provider_status(local_ai_provider_modes::OPENVINO).to_string();
                    self.settings.local_ai_prepare_progress =
                        "Use Download model to prepare OpenVINO assets".to_string();
                }
                _ => {
                    self.settings.local_ai_status = "Preparing Phi Silica model".to_string();
                    self.settings.local_ai_prepare_progress =
                        "Requesting model download and preparation from Windows".to_string();
                }
            },
            Message::WindowsAiPrepareFinished(result) => match result {
                Ok(status) => {
                    self.settings.local_ai_status = status.message.clone();
                    self.settings.local_ai_prepare_progress = match status.state {
                        easydict_windows_ai::WindowsAiModelState::Ready => "Ready".to_string(),
                        easydict_windows_ai::WindowsAiModelState::NeedsPreparation => {
                            "Needs preparation".to_string()
                        }
                        easydict_windows_ai::WindowsAiModelState::NotCompatible => {
                            "Not compatible".to_string()
                        }
                        easydict_windows_ai::WindowsAiModelState::Failed => "Failed".to_string(),
                    };
                }
                Err(message) => {
                    self.settings.local_ai_status = message;
                    self.settings.local_ai_prepare_progress = "Failed".to_string();
                }
            },
            Message::OpenWindowsAiUpdate => {
                self.settings.local_ai_prepare_progress =
                    "Windows Update progress link requested".to_string();
            }
            Message::FoundryLocalEndpointChanged(value) => {
                if self.settings.foundry_local_endpoint != value {
                    self.settings.foundry_local_endpoint = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::FoundryLocalModelChanged(value) => {
                if self.settings.foundry_local_model != value {
                    self.settings.foundry_local_model = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::StartFoundryLocal => {
                self.settings.foundry_local_status =
                    "Starting Foundry Local service...".to_string();
            }
            Message::FoundryLocalPrepareFinished(result) => match result {
                Ok(outcome) => {
                    self.settings.foundry_local_status = outcome.status_message;
                    if self.settings.foundry_local_endpoint.trim().is_empty() {
                        if let Some(endpoint) = outcome.endpoint {
                            if !endpoint.trim().is_empty() {
                                self.settings.foundry_local_endpoint = endpoint;
                                mark_settings_changed(&mut self.settings);
                            }
                        }
                    }

                    if self.settings.foundry_local_model.trim().is_empty()
                        && !outcome.model.trim().is_empty()
                    {
                        self.settings.foundry_local_model = outcome.model;
                        mark_settings_changed(&mut self.settings);
                    }
                }
                Err(message) => {
                    self.settings.foundry_local_status = message;
                }
            },
            Message::InstallFoundryLocal => {
                self.settings.foundry_local_status =
                    "Install Foundry Local link requested".to_string();
            }
            Message::OpenFoundryLocalDocs => {
                self.settings.foundry_local_status =
                    "Foundry Local documentation link requested".to_string();
            }
            Message::OpenVinoDeviceChanged(value) => {
                let device = normalize_open_vino_device(&value);
                if self.settings.open_vino_device != device {
                    self.settings.open_vino_device = device;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::DownloadOpenVinoModel => {
                self.settings.open_vino_status =
                    "Download queued for NLLB-200 model (~360 MB)".to_string();
                self.settings.open_vino_download_progress = "Queued".to_string();
            }
            Message::OpenVinoDownloadFinished(result) => match result {
                Ok(status) if status.is_ready() => {
                    self.settings.open_vino_status = "NLLB-200 model ready".to_string();
                    self.settings.open_vino_download_progress = "Idle".to_string();
                }
                Ok(_) => {
                    self.settings.open_vino_status = "Model not downloaded".to_string();
                    self.settings.open_vino_download_progress = "Idle".to_string();
                }
                Err(error) => {
                    self.settings.open_vino_status = format!("Download failed: {error}");
                    self.settings.open_vino_download_progress = "Failed".to_string();
                }
            },
            Message::ServiceProviderSettingChanged(service_id, field, value) => {
                if let Some(setting) = service_provider_setting_mut(&mut self.settings, &service_id)
                {
                    let current = match field {
                        ServiceProviderField::ApiKey => &mut setting.api_key,
                        ServiceProviderField::Endpoint => &mut setting.endpoint,
                        ServiceProviderField::Model => &mut setting.model,
                    };

                    if current != &value {
                        *current = value;
                        mark_settings_changed(&mut self.settings);
                    }
                }
            }
            Message::TestServiceProvider(service_id) => {
                if let Some(setting) = service_provider_setting_mut(&mut self.settings, &service_id)
                {
                    setting.status = format!(
                        "Test requested for {}",
                        setting_or_default(
                            &setting.model,
                            service_provider_default_model(&service_id)
                        )
                    );
                }
            }
            Message::ToggleServiceConfigurationExpanded(service_id, expanded) => {
                if expanded {
                    if !self
                        .settings
                        .expanded_service_configurations
                        .iter()
                        .any(|id| id == &service_id)
                    {
                        self.settings
                            .expanded_service_configurations
                            .push(service_id);
                    }
                } else {
                    self.settings
                        .expanded_service_configurations
                        .retain(|id| id != &service_id);
                }
            }
            Message::CaiyunApiKeyChanged(value) => {
                if self.settings.caiyun_api_key != value {
                    self.settings.caiyun_api_key = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::TestCaiyun => {
                self.settings.caiyun_status = "Test requested for Caiyun".to_string();
            }
            Message::NiuTransApiKeyChanged(value) => {
                if self.settings.niu_trans_api_key != value {
                    self.settings.niu_trans_api_key = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::TestNiuTrans => {
                self.settings.niu_trans_status = "Test requested for NiuTrans".to_string();
            }
            Message::YoudaoAppKeyChanged(value) => {
                if self.settings.youdao_app_key != value {
                    self.settings.youdao_app_key = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::YoudaoAppSecretChanged(value) => {
                if self.settings.youdao_app_secret != value {
                    self.settings.youdao_app_secret = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::ToggleYoudaoUseOfficialApi(value) => {
                if self.settings.youdao_use_official_api != value {
                    self.settings.youdao_use_official_api = value;
                    self.settings.youdao_status = youdao_mode_status(value).to_string();
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::TestYoudao => {
                self.settings.youdao_status = format!(
                    "Test requested for {}",
                    if self.settings.youdao_use_official_api {
                        "Official API"
                    } else {
                        "Web dictionary mode"
                    }
                );
            }
            Message::VolcanoAccessKeyIdChanged(value) => {
                if self.settings.volcano_access_key_id != value {
                    self.settings.volcano_access_key_id = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::VolcanoSecretAccessKeyChanged(value) => {
                if self.settings.volcano_secret_access_key != value {
                    self.settings.volcano_secret_access_key = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::TestVolcano => {
                self.settings.volcano_status = "Test requested for Volcano".to_string();
            }
            Message::ToggleLocalDictionarySuggestions(value) => {
                self.settings.local_dictionary_suggestions = value;
                mark_settings_changed(&mut self.settings);
                if !value {
                    self.active_suggestion_query_id = None;
                    self.local_dictionary_suggestion_query = None;
                    self.local_dictionary_suggestions.clear();
                    self.local_dictionary_suggestion_active_index = None;
                    self.local_dictionary_suggestion_error = None;
                    self.source_text_focused = true;
                    self.source_text_state = ControlState::default().focused(true);
                }
            }
            Message::UiLanguageChanged(value) => {
                if self.settings.ui_language != value {
                    self.settings.ui_language = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::OcrLanguageChanged(value) => {
                if self.settings.ocr_language != value {
                    self.settings.ocr_language = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::FirstLanguageChanged(value) => {
                if set_first_language(&mut self.settings, &value) {
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::SecondLanguageChanged(value) => {
                if set_second_language(&mut self.settings, &value) {
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::ToggleAutoSelectTargetLanguage(value) => {
                if self.settings.auto_select_target_language != value {
                    self.settings.auto_select_target_language = value;
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::ToggleSelectedLanguage(language_id, value) => {
                if set_selected_language(&mut self.settings, &language_id, value) {
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::ToggleTranslationLanguagesExpanded(value) => {
                self.settings.translation_languages_expanded = value;
            }
            Message::ToggleHotkey(hotkey_id, value) => {
                if let Some(setting) = hotkey_setting_mut(&mut self.settings, &hotkey_id) {
                    if setting.enabled != value {
                        setting.enabled = value;
                        mark_settings_changed(&mut self.settings);
                    }
                }
            }
            Message::HotkeyShortcutChanged(hotkey_id, value) => {
                if let Some(setting) = hotkey_setting_mut(&mut self.settings, &hotkey_id) {
                    if setting.shortcut != value {
                        setting.shortcut = value;
                        mark_settings_changed(&mut self.settings);
                    }
                }
            }
            Message::ToggleMiniAutoClose(value) => {
                self.settings.mini_auto_close = value;
                mark_settings_changed(&mut self.settings);
            }
            Message::ToggleFixedAlwaysOnTop(value) => {
                self.settings.fixed_always_on_top = value;
                mark_settings_changed(&mut self.settings);
            }
            Message::ToggleWindowReorderMode(surface) => match surface {
                QuickTranslateSurface::Main => {
                    self.settings.main_window_reorder_mode =
                        !self.settings.main_window_reorder_mode;
                }
                QuickTranslateSurface::Mini => {
                    self.settings.mini_window_reorder_mode =
                        !self.settings.mini_window_reorder_mode;
                }
                QuickTranslateSurface::Fixed => {
                    self.settings.fixed_window_reorder_mode =
                        !self.settings.fixed_window_reorder_mode;
                }
            },
            Message::ToggleWindowService(surface, service_id, enabled) => {
                if set_window_service_enabled(&mut self.settings, surface, &service_id, enabled) {
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::ToggleWindowServiceQuery(surface, service_id, enabled_query) => {
                if set_window_service_enabled_query(
                    &mut self.settings,
                    surface,
                    &service_id,
                    enabled_query,
                ) {
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::MoveWindowService(surface, service_id, offset) => {
                if move_window_service(&mut self.settings, surface, &service_id, offset) {
                    mark_settings_changed(&mut self.settings);
                }
            }
            Message::ToggleTwoPassContext(value) => {
                if !self.long_document.is_translating {
                    self.long_document.two_pass_context = value;
                }
            }
            Message::TogglePin(value) => self.mini.pinned = value,
            Message::SwapLanguages => {
                std::mem::swap(&mut self.source_language, &mut self.target_language);
                self.target_language_manually_selected = true;
            }
            Message::SwapFloatingLanguages(surface) => {
                if let Some(floating) = floating_surface_mut(self, surface) {
                    std::mem::swap(&mut floating.source_language, &mut floating.target_language);
                    floating.target_language_manually_selected = true;
                }
            }
            Message::ToggleResultExpanded(id) => {
                toggle_result_expanded(&mut self.results, &id);
                toggle_result_expanded(&mut self.mini.results, &id);
                toggle_result_expanded(&mut self.fixed.results, &id);
                toggle_result_expanded(&mut self.long_document.history, &id);
            }
            Message::ToggleResultExpandedIn(surface, id) => match surface {
                QuickTranslateSurface::Main => toggle_result_expanded(&mut self.results, &id),
                QuickTranslateSurface::Mini => {
                    toggle_result_expanded(&mut self.mini.results, &id);
                }
                QuickTranslateSurface::Fixed => {
                    toggle_result_expanded(&mut self.fixed.results, &id);
                }
            },
            Message::QuickTranslateFinished(outcome) => {
                crate::quick_translate::apply_quick_translate_outcome(self, outcome);
            }
            Message::QuickTranslateServiceFinished(update) => {
                crate::quick_translate::apply_quick_translate_service_update(self, update);
            }
            Message::QuickTranslateStreamChunk(chunk) => {
                crate::quick_translate::apply_quick_translate_stream_chunk(self, chunk);
            }
            Message::LongDocumentFinished(outcome) => {
                crate::long_document::apply_long_document_outcome(self, outcome);
            }
            Message::OcrRecognizeFinished(outcome) => {
                crate::ocr::apply_ocr_outcome(self, outcome);
            }
            Message::CaptureSelectionChanged(selection) => {
                self.capture_selection =
                    selection.map(crate::screen_capture::CaptureRect::normalized);
            }
            Message::CaptureWindowsChanged(windows) => {
                self.capture_window_detector =
                    crate::screen_capture::WindowDetector::from_windows(windows);
            }
            Message::CopyResultIn(surface, id) => {
                capture_result_action(self, ResultActionKind::Copy, surface, &id);
            }
            Message::SpeakResultIn(surface, id) => {
                capture_result_action(self, ResultActionKind::Speak, surface, &id);
            }
            Message::ReplaceResultIn(surface, id) => {
                capture_result_action(self, ResultActionKind::Replace, surface, &id);
            }
            Message::BrowserSupportStatusLoaded(result) => {
                self.browser_support = match result {
                    Ok(status) => BrowserSupportState::from_status(&status),
                    Err(error) => BrowserSupportState::failed(error.clone()),
                };
            }
            Message::Noop
            | Message::QuickTranslate
            | Message::QuickTranslateIn(_)
            | Message::InstallBrowserSupport
            | Message::UninstallBrowserSupport
            | Message::HotkeyTriggered(_)
            | Message::TrayCommand(_)
            | Message::WindowEvent(_)
            | Message::ClipboardTextReceived(_)
            | Message::TrayClipboardTextReceived(_)
            | Message::MouseSelectionInputHookEvent(_)
            | Message::MouseSelectionPendingMultiClickElapsed(_)
            | Message::OcrCaptureFinished(_)
            | Message::SilentOcrCaptureFinished(_)
            | Message::OcrCaptureCancelled(_)
            | Message::Translate
            | Message::CaptureMouseMoved(_)
            | Message::CaptureLeftButtonDown(_)
            | Message::CaptureLeftButtonUp(_)
            | Message::CaptureDoubleClick(_)
            | Message::CaptureRightButtonDown
            | Message::CaptureMouseWheel { .. }
            | Message::CaptureNudgeSelection { .. }
            | Message::CaptureEscape
            | Message::CopyResult
            | Message::ReplaceResult
            | Message::RetryResult
            | Message::RetryResultIn(_, _)
            | Message::SpeakResult
            | Message::MinimizeWindow
            | Message::ToggleMaximizeWindow
            | Message::CloseMainWindow
            | Message::CloseWindow
            | Message::BrowseFile
            | Message::BrowseOutputFolder
            | Message::ImportMdxDictionary
            | Message::RetryLongDocument
            | Message::ConfirmCapture
            | Message::CancelCapture
            | Message::TranslateSelection
            | Message::SelectionTextReady { .. }
            | Message::DismissPopButton
            | Message::PopButtonAutoDismiss(_)
            | Message::PopButtonClicked => {}
            Message::ClearHistory => {
                self.long_document.history.clear();
            }
        }
    }
}

fn apply_empty_floating_preview(state: &mut FloatingWindowState) {
    state.text.clear();
    state.detected_language = None;
    state.status_text.clear();
    state.is_translating = false;
    state.services_completed = 0;
    state.active_query_id = None;
    state.active_query_service_count = 0;
    state.active_query_success_count = 0;
    state.results.clear();
}

pub fn settings_snapshot(settings: &SettingsState) -> SettingsSnapshot {
    let deepseek = service_provider_setting(settings, "deepseek");
    let groq = service_provider_setting(settings, "groq");
    let zhipu = service_provider_setting(settings, "zhipu");
    let github = service_provider_setting(settings, "github");
    let gemini = service_provider_setting(settings, "gemini");
    let custom_openai = service_provider_setting(settings, "custom-openai");
    let built_in_ai = service_provider_setting(settings, "builtin");
    let doubao = service_provider_setting(settings, "doubao");

    SettingsSnapshot {
        open_ai_api_key: non_empty_setting(&settings.open_ai_api_key),
        open_ai_endpoint: Some(setting_or_default(
            &settings.open_ai_endpoint,
            DEFAULT_OPENAI_ENDPOINT,
        )),
        open_ai_model: Some(setting_or_default(
            &settings.open_ai_model,
            DEFAULT_OPENAI_MODEL,
        )),
        open_ai_api_format_override: Some(setting_or_default(
            &settings.open_ai_api_format_override,
            "Auto",
        )),
        deep_l_api_key: non_empty_setting(&settings.deepl_api_key),
        deep_l_use_free_api: Some(settings.deepl_use_free_api),
        deep_l_use_quality_optimized: Some(settings.deepl_use_quality_optimized),
        deep_seek_api_key: deepseek.and_then(|setting| non_empty_setting(&setting.api_key)),
        deep_seek_model: Some(setting_or_default(
            deepseek
                .map(|setting| setting.model.as_str())
                .unwrap_or_default(),
            DEFAULT_DEEPSEEK_MODEL,
        )),
        groq_api_key: groq.and_then(|setting| non_empty_setting(&setting.api_key)),
        groq_model: Some(setting_or_default(
            groq.map(|setting| setting.model.as_str())
                .unwrap_or_default(),
            DEFAULT_GROQ_MODEL,
        )),
        zhipu_api_key: zhipu.and_then(|setting| non_empty_setting(&setting.api_key)),
        zhipu_model: Some(setting_or_default(
            zhipu
                .map(|setting| setting.model.as_str())
                .unwrap_or_default(),
            DEFAULT_ZHIPU_MODEL,
        )),
        github_models_api_key: github.and_then(|setting| non_empty_setting(&setting.api_key)),
        github_models_model: Some(setting_or_default(
            github
                .map(|setting| setting.model.as_str())
                .unwrap_or_default(),
            DEFAULT_GITHUB_MODELS_MODEL,
        )),
        gemini_api_key: gemini.and_then(|setting| non_empty_setting(&setting.api_key)),
        gemini_model: Some(setting_or_default(
            gemini
                .map(|setting| setting.model.as_str())
                .unwrap_or_default(),
            DEFAULT_GEMINI_MODEL,
        )),
        custom_open_ai_api_key: custom_openai
            .and_then(|setting| non_empty_setting(&setting.api_key)),
        custom_open_ai_endpoint: custom_openai
            .and_then(|setting| non_empty_setting(&setting.endpoint)),
        custom_open_ai_model: Some(setting_or_default(
            custom_openai
                .map(|setting| setting.model.as_str())
                .unwrap_or_default(),
            DEFAULT_CUSTOM_OPENAI_MODEL,
        )),
        built_in_ai_api_key: built_in_ai.and_then(|setting| non_empty_setting(&setting.api_key)),
        built_in_ai_model: Some(setting_or_default(
            built_in_ai
                .map(|setting| setting.model.as_str())
                .unwrap_or_default(),
            DEFAULT_BUILT_IN_AI_MODEL,
        )),
        device_id: non_empty_setting(&settings.device_id),
        device_token: non_empty_setting(&settings.device_token),
        doubao_api_key: doubao.and_then(|setting| non_empty_setting(&setting.api_key)),
        doubao_endpoint: Some(setting_or_default(
            doubao
                .map(|setting| setting.endpoint.as_str())
                .unwrap_or_default(),
            DEFAULT_DOUBAO_ENDPOINT,
        )),
        doubao_model: Some(setting_or_default(
            doubao
                .map(|setting| setting.model.as_str())
                .unwrap_or_default(),
            DEFAULT_DOUBAO_MODEL,
        )),
        caiyun_token: non_empty_setting(&settings.caiyun_api_key),
        niu_trans_api_key: non_empty_setting(&settings.niu_trans_api_key),
        youdao_app_key: non_empty_setting(&settings.youdao_app_key),
        youdao_app_secret: non_empty_setting(&settings.youdao_app_secret),
        youdao_use_official_api: Some(settings.youdao_use_official_api),
        volcano_access_key_id: non_empty_setting(&settings.volcano_access_key_id),
        volcano_secret_access_key: non_empty_setting(&settings.volcano_secret_access_key),
        ollama_endpoint: Some(setting_or_default(
            &settings.ollama_endpoint,
            DEFAULT_OLLAMA_ENDPOINT,
        )),
        ollama_model: Some(setting_or_default(
            &settings.ollama_model,
            DEFAULT_OLLAMA_MODEL,
        )),
        foundry_local_endpoint: non_empty_setting(&settings.foundry_local_endpoint),
        foundry_local_model: Some(setting_or_default(
            &settings.foundry_local_model,
            DEFAULT_FOUNDRY_LOCAL_MODEL,
        )),
        open_vino_device: Some(setting_or_default(
            &settings.open_vino_device,
            DEFAULT_OPENVINO_DEVICE,
        )),
        local_ai_provider: Some(normalize_local_ai_provider(&settings.local_ai_provider)),
        ocr_engine: Some(normalize_ocr_engine(&settings.ocr_engine)),
        ocr_api_key: non_empty_setting(&settings.ocr_api_key),
        ocr_endpoint: Some(setting_or_default(
            &settings.ocr_endpoint,
            default_ocr_endpoint(&settings.ocr_engine),
        )),
        ocr_model: Some(setting_or_default(
            &settings.ocr_model,
            default_ocr_model(&settings.ocr_engine),
        )),
        ocr_system_prompt: Some(setting_or_default(
            &settings.ocr_system_prompt,
            DEFAULT_OCR_SYSTEM_PROMPT,
        )),
        ocr_language: Some(setting_or_default(&settings.ocr_language, "auto")),
        enable_international_services: Some(settings.enable_international_services),
        proxy_enabled: Some(settings.proxy_enabled),
        proxy_uri: non_empty_setting(&settings.proxy_url),
        proxy_bypass_local: Some(settings.proxy_bypass_local),
        enable_translation_cache: Some(settings.translation_cache_enabled),
        formula_font_pattern: non_empty_setting(&settings.formula_font_pattern),
        formula_char_pattern: non_empty_setting(&settings.formula_char_pattern),
        long_doc_custom_prompt: non_empty_setting(&settings.custom_translation_prompt),
        layout_detection_mode: Some(settings.layout_detection_mode.clone()),
        imported_mdx_dictionaries: (!settings.imported_mdx_dictionaries.is_empty()).then(|| {
            settings
                .imported_mdx_dictionaries
                .iter()
                .map(ImportedMdxDictionary::snapshot)
                .collect()
        }),
        ..SettingsSnapshot::default()
    }
}

fn setting_or_default(value: &str, fallback: &str) -> String {
    let value = value.trim();
    if value.is_empty() {
        fallback.to_string()
    } else {
        value.to_string()
    }
}

fn normalize_local_ai_provider(value: &str) -> String {
    normalize_local_ai_provider_mode(Some(value)).to_string()
}

fn normalize_ocr_engine(value: &str) -> String {
    match value.trim().to_ascii_lowercase().as_str() {
        "ollama" => "Ollama".to_string(),
        "customapi" | "custom-api" | "custom_api" => "CustomApi".to_string(),
        _ => "WindowsNative".to_string(),
    }
}

fn normalize_open_vino_device(value: &str) -> String {
    match value.trim().to_ascii_uppercase().as_str() {
        "NPU" => "NPU".to_string(),
        "GPU" => "GPU".to_string(),
        "CPU" => "CPU".to_string(),
        _ => DEFAULT_OPENVINO_DEVICE.to_string(),
    }
}

fn local_ai_provider_status(provider: &str) -> &'static str {
    match provider {
        local_ai_provider_modes::WINDOWS_AI => "Phi Silica selected",
        local_ai_provider_modes::FOUNDRY_LOCAL => "Foundry Local selected",
        local_ai_provider_modes::OPENVINO => "OpenVINO selected",
        _ => "Auto fallback: Phi Silica -> Foundry Local -> OpenVINO",
    }
}

fn should_apply_foundry_runtime_status(current: &str) -> bool {
    let current = current.trim();
    current.is_empty()
        || current == "Endpoint auto-detected at runtime"
        || current.starts_with("Foundry Local is ready")
        || current.starts_with("Foundry Local service")
        || current.contains("Foundry Local CLI is not installed")
        || current.contains("not available on PATH")
}

fn should_apply_windows_ai_runtime_status(settings: &SettingsState) -> bool {
    matches!(
        settings.local_ai_provider.as_str(),
        local_ai_provider_modes::AUTO | local_ai_provider_modes::WINDOWS_AI
    ) && matches!(
        settings.local_ai_prepare_progress.as_str(),
        "" | "Idle" | "Ready" | "Needs preparation" | "Not compatible" | "Failed"
    )
}

fn youdao_mode_status(use_official_api: bool) -> &'static str {
    if use_official_api {
        "Official API mode"
    } else {
        "Web dictionary mode"
    }
}

fn non_empty_setting(value: &str) -> Option<String> {
    let value = value.trim();
    (!value.is_empty()).then(|| value.to_string())
}

fn open_ai_format_label(settings: &SettingsState) -> &'static str {
    match settings.open_ai_api_format_override.as_str() {
        "Responses" => "Responses API",
        "ChatCompletions" => "Chat Completions API",
        _ if setting_or_default(&settings.open_ai_endpoint, DEFAULT_OPENAI_ENDPOINT)
            .trim_end_matches('/')
            .ends_with("/responses") =>
        {
            "Responses API"
        }
        _ => "Chat Completions API",
    }
}

fn apply_long_document_file_selection(state: &mut EasydictUiState, path: Option<String>) {
    let Some(path) = path
        .map(|path| path.trim().to_string())
        .filter(|path| !path.is_empty())
    else {
        return;
    };

    state.long_document.selected_file = path.clone();
    if let Some(input_mode) = long_document_input_mode_for_path(&path) {
        state.long_document.input_mode = input_mode.to_string();
    }
    if let Some(folder) = parent_folder(&path) {
        state.long_document.output_folder = folder;
    }
    state.long_document.status_text = "Ready".to_string();
    state.long_document.last_error = None;
    state.long_document.last_output_path = None;
    state.long_document.progress_percentage = None;
    state.long_document.progress_detail = None;
    state.long_document.last_translated_block = None;
}

fn apply_long_document_output_folder_selection(state: &mut EasydictUiState, path: Option<String>) {
    let Some(path) = path
        .map(|path| path.trim().to_string())
        .filter(|path| !path.is_empty())
    else {
        return;
    };

    state.long_document.output_folder = path;
    state.long_document.status_text = "Output folder selected".to_string();
    state.long_document.last_error = None;
}

fn mark_settings_changed(settings: &mut SettingsState) {
    settings.unsaved_changes = true;
    settings.show_unsaved_changes_dialog = false;
    settings.save_error_message = None;
}

fn apply_ocr_engine_defaults(settings: &mut SettingsState) {
    if known_ocr_default_endpoint(&settings.ocr_endpoint) {
        settings.ocr_endpoint = default_ocr_endpoint(&settings.ocr_engine).to_string();
    }

    if known_ocr_default_model(&settings.ocr_model) {
        settings.ocr_model = default_ocr_model(&settings.ocr_engine).to_string();
    }
}

fn ocr_test_result(settings: &SettingsState) -> String {
    if settings.ocr_engine == "WindowsNative" {
        return "Windows Native OCR is selected.".to_string();
    }

    if settings.ocr_endpoint.trim().is_empty() {
        return "Endpoint is required before testing.".to_string();
    }

    format!(
        "Connection test requested for {}.",
        ocr_engine_label(&settings.ocr_engine)
    )
}

fn ocr_engine_label(engine: &str) -> &'static str {
    match engine {
        "Ollama" => "Ollama Local VLM",
        "CustomApi" => "Custom API",
        _ => "Windows Native OCR",
    }
}

fn default_ocr_endpoint(engine: &str) -> &'static str {
    if engine == "CustomApi" {
        DEFAULT_CUSTOM_OCR_ENDPOINT
    } else {
        DEFAULT_OLLAMA_OCR_ENDPOINT
    }
}

fn default_ocr_model(engine: &str) -> &'static str {
    if engine == "CustomApi" {
        DEFAULT_CUSTOM_OCR_MODEL
    } else {
        DEFAULT_OLLAMA_OCR_MODEL
    }
}

fn known_ocr_default_endpoint(value: &str) -> bool {
    let value = value.trim();
    value.is_empty()
        || value.eq_ignore_ascii_case(DEFAULT_OLLAMA_OCR_ENDPOINT)
        || value.eq_ignore_ascii_case(DEFAULT_CUSTOM_OCR_ENDPOINT)
}

fn known_ocr_default_model(value: &str) -> bool {
    let value = value.trim();
    value.is_empty()
        || value.eq_ignore_ascii_case(DEFAULT_OLLAMA_OCR_MODEL)
        || value.eq_ignore_ascii_case(DEFAULT_CUSTOM_OCR_MODEL)
}

fn default_main_window_services() -> Vec<WindowServiceSetting> {
    default_window_services(&DEFAULT_MAIN_WINDOW_SERVICE_IDS)
}

fn default_floating_window_services() -> Vec<WindowServiceSetting> {
    default_window_services(&DEFAULT_FLOATING_WINDOW_SERVICE_IDS)
}

fn default_window_services(enabled_ids: &[&str]) -> Vec<WindowServiceSetting> {
    default_translation_service_descriptors()
        .into_iter()
        .map(|descriptor| {
            let mut setting =
                WindowServiceSetting::new(descriptor.service_id, descriptor.display_name);
            setting.enabled = enabled_ids
                .iter()
                .any(|enabled_id| *enabled_id == descriptor.service_id);
            setting.enabled_query = true;
            setting.configured = descriptor.configured_by_default;
            setting
        })
        .collect()
}

fn apply_settings_view_service_preview_profile(settings: &mut SettingsState, profile: &str) {
    if !profile.eq_ignore_ascii_case("dotnet-reference") {
        return;
    }

    let services = dotnet_reference_window_services();
    settings.main_window_services = services.clone();
    settings.mini_window_services = services.clone();
    settings.fixed_window_services = services;
    apply_preview_imported_mdx_dictionary(settings);
}

fn apply_preview_imported_mdx_dictionary(settings: &mut SettingsState) {
    settings.imported_mdx_dictionaries = vec![ImportedMdxDictionary {
        service_id: PREVIEW_DOTNET_REFERENCE_MDX_SERVICE_ID.to_string(),
        display_name: PREVIEW_DOTNET_REFERENCE_MDX_DISPLAY_NAME.to_string(),
        file_path: "C:\\Dictionaries\\Collins COBUILD English Usage.mdx".to_string(),
        is_encrypted: false,
        regcode: None,
        email: None,
        mdd_file_paths: Vec::new(),
    }];
}

const PREVIEW_DOTNET_REFERENCE_MDX_SERVICE_ID: &str = "mdx::collins-cobuild-english-usage";
const PREVIEW_DOTNET_REFERENCE_MDX_DISPLAY_NAME: &str = "Collins COBUILD English Usage";

fn dotnet_reference_window_services() -> Vec<WindowServiceSetting> {
    let mut remaining = default_window_services(&[]);
    let mut mdx = WindowServiceSetting::new(
        PREVIEW_DOTNET_REFERENCE_MDX_SERVICE_ID,
        PREVIEW_DOTNET_REFERENCE_MDX_DISPLAY_NAME,
    );
    mdx.configured = true;
    remaining.push(mdx);

    let preferred_order = [
        "bing",
        "windows-local-ai",
        PREVIEW_DOTNET_REFERENCE_MDX_SERVICE_ID,
        "google",
        "volcano",
        "google_web",
        "deepl",
        "ollama",
        "openai",
        "builtin",
        "deepseek",
        "zhipu",
        "groq",
        "gemini",
        "github",
        "custom-openai",
        "doubao",
        "caiyun",
        "niutrans",
        "youdao",
        "linguee",
    ];

    let mut ordered = Vec::with_capacity(remaining.len());
    for service_id in preferred_order {
        if let Some(index) = remaining
            .iter()
            .position(|service| service.service_id.eq_ignore_ascii_case(service_id))
        {
            let mut setting = remaining.remove(index);
            apply_dotnet_reference_window_service_state(&mut setting);
            ordered.push(setting);
        }
    }

    for mut setting in remaining {
        apply_dotnet_reference_window_service_state(&mut setting);
        ordered.push(setting);
    }

    ordered
}

fn apply_dotnet_reference_window_service_state(setting: &mut WindowServiceSetting) {
    setting.enabled = matches!(
        setting.service_id.as_str(),
        "bing"
            | "windows-local-ai"
            | PREVIEW_DOTNET_REFERENCE_MDX_SERVICE_ID
            | "google"
            | "volcano"
    );
    setting.enabled_query = !matches!(
        setting.service_id.as_str(),
        "bing" | "windows-local-ai" | "volcano"
    );
    if setting.enabled {
        setting.configured = true;
    }
}

fn default_service_provider_settings() -> Vec<ServiceProviderSetting> {
    vec![
        ServiceProviderSetting::new("deepseek", "", DEFAULT_DEEPSEEK_MODEL),
        ServiceProviderSetting::new("groq", "", DEFAULT_GROQ_MODEL),
        ServiceProviderSetting::new("zhipu", "", DEFAULT_ZHIPU_MODEL),
        ServiceProviderSetting::new("github", "", DEFAULT_GITHUB_MODELS_MODEL),
        ServiceProviderSetting::new("gemini", "", DEFAULT_GEMINI_MODEL),
        ServiceProviderSetting::new("custom-openai", "", DEFAULT_CUSTOM_OPENAI_MODEL),
        ServiceProviderSetting::new("builtin", "", DEFAULT_BUILT_IN_AI_MODEL),
        ServiceProviderSetting::new("doubao", DEFAULT_DOUBAO_ENDPOINT, DEFAULT_DOUBAO_MODEL),
    ]
}

fn service_provider_setting<'a>(
    settings: &'a SettingsState,
    service_id: &str,
) -> Option<&'a ServiceProviderSetting> {
    settings
        .service_provider_settings
        .iter()
        .find(|setting| setting.service_id == service_id)
}

fn service_provider_setting_mut<'a>(
    settings: &'a mut SettingsState,
    service_id: &str,
) -> Option<&'a mut ServiceProviderSetting> {
    settings
        .service_provider_settings
        .iter_mut()
        .find(|setting| setting.service_id == service_id)
}

fn service_provider_default_model(service_id: &str) -> &'static str {
    match service_id {
        "deepseek" => DEFAULT_DEEPSEEK_MODEL,
        "groq" => DEFAULT_GROQ_MODEL,
        "zhipu" => DEFAULT_ZHIPU_MODEL,
        "github" => DEFAULT_GITHUB_MODELS_MODEL,
        "gemini" => DEFAULT_GEMINI_MODEL,
        "custom-openai" => DEFAULT_CUSTOM_OPENAI_MODEL,
        "builtin" => DEFAULT_BUILT_IN_AI_MODEL,
        "doubao" => DEFAULT_DOUBAO_MODEL,
        _ => "",
    }
}

fn hotkey_setting_mut<'a>(
    settings: &'a mut SettingsState,
    hotkey_id: &str,
) -> Option<&'a mut HotkeySetting> {
    match hotkey_id {
        HOTKEY_SHOW_MAIN => Some(&mut settings.show_main_hotkey),
        HOTKEY_TRANSLATE_CLIPBOARD => Some(&mut settings.translate_clipboard_hotkey),
        HOTKEY_SHOW_MINI => Some(&mut settings.show_mini_hotkey),
        HOTKEY_SHOW_FIXED => Some(&mut settings.show_fixed_hotkey),
        HOTKEY_OCR_TRANSLATE => Some(&mut settings.ocr_translate_hotkey),
        HOTKEY_SILENT_OCR => Some(&mut settings.silent_ocr_hotkey),
        _ => None,
    }
}

fn default_selected_languages() -> Vec<String> {
    ["zh-Hans", "en", "ja", "ko", "fr", "de", "es"]
        .into_iter()
        .map(str::to_string)
        .collect()
}

fn set_first_language(settings: &mut SettingsState, language_id: &str) -> bool {
    let Some(language_id) = settings_language_id(language_id) else {
        return false;
    };

    if settings.first_language == language_id {
        return false;
    }

    settings.first_language = language_id;
    if same_language(&settings.first_language, &settings.second_language) {
        settings.second_language = different_settings_language(&settings.first_language);
    }
    true
}

fn set_second_language(settings: &mut SettingsState, language_id: &str) -> bool {
    let Some(language_id) = settings_language_id(language_id) else {
        return false;
    };

    if settings.second_language == language_id {
        return false;
    }

    settings.second_language = language_id;
    if same_language(&settings.first_language, &settings.second_language) {
        settings.first_language = different_settings_language(&settings.second_language);
    }
    true
}

fn set_selected_language(settings: &mut SettingsState, language_id: &str, selected: bool) -> bool {
    let Some(language_id) = settings_language_id(language_id) else {
        return false;
    };

    let contains_language = settings
        .selected_languages
        .iter()
        .any(|language| same_language(language, &language_id));
    if selected {
        if contains_language {
            return false;
        }

        settings.selected_languages.push(language_id);
        normalize_selected_languages(&mut settings.selected_languages);
        return true;
    }

    if !contains_language || settings.selected_languages.len() <= 2 {
        return false;
    }

    settings
        .selected_languages
        .retain(|language| !same_language(language, &language_id));
    normalize_selected_languages(&mut settings.selected_languages);
    ensure_language_preferences_are_selected(settings);
    true
}

fn normalize_selected_languages(selected_languages: &mut Vec<String>) {
    let mut normalized: Vec<String> = Vec::new();
    for id in TRANSLATION_LANGUAGE_IDS {
        if selected_languages
            .iter()
            .any(|language| same_language(language, id))
            && !normalized
                .iter()
                .any(|language| same_language(language, id))
        {
            normalized.push(id.to_string());
        }
    }
    *selected_languages = normalized;
}

fn ensure_language_preferences_are_selected(settings: &mut SettingsState) {
    if !is_language_selected(&settings.selected_languages, &settings.first_language) {
        settings.first_language = preferred_selected_language(&settings.selected_languages, None);
    }

    if !is_language_selected(&settings.selected_languages, &settings.second_language)
        || same_language(&settings.first_language, &settings.second_language)
    {
        settings.second_language = preferred_selected_language(
            &settings.selected_languages,
            Some(&settings.first_language),
        );
    }
}

fn is_language_selected(selected_languages: &[String], language_id: &str) -> bool {
    selected_languages
        .iter()
        .any(|language| same_language(language, language_id))
}

fn preferred_selected_language(
    selected_languages: &[String],
    except_language: Option<&str>,
) -> String {
    selected_languages
        .iter()
        .find(|language| {
            except_language
                .map(|except_language| !same_language(language, except_language))
                .unwrap_or(true)
        })
        .or_else(|| selected_languages.first())
        .cloned()
        .unwrap_or_else(|| "en".to_string())
}

fn settings_language_id(language_id: &str) -> Option<String> {
    let normalized = normalize_settings_language(language_id);
    TRANSLATION_LANGUAGE_IDS
        .into_iter()
        .any(|id| same_language(id, &normalized))
        .then_some(normalized)
}

fn different_settings_language(language_id: &str) -> String {
    TRANSLATION_LANGUAGE_IDS
        .into_iter()
        .find(|candidate| !same_language(candidate, language_id))
        .unwrap_or("en")
        .to_string()
}

fn same_language(left: &str, right: &str) -> bool {
    normalize_settings_language(left) == normalize_settings_language(right)
}

fn normalize_settings_language(language_id: &str) -> String {
    match language_id.trim().to_ascii_lowercase().as_str() {
        "zh" | "zh-cn" | "zh-hans" => "zh-Hans".to_string(),
        "zh-tw" | "zh-hant" => "zh-Hant".to_string(),
        "ar-sa" => "ar".to_string(),
        "bg-bg" => "bg".to_string(),
        "bn-in" => "bn".to_string(),
        "cs-cz" => "cs".to_string(),
        "da-dk" => "da".to_string(),
        "de-de" => "de".to_string(),
        "el-gr" => "el".to_string(),
        "en-us" | "en-gb" => "en".to_string(),
        "es-es" => "es".to_string(),
        "et-ee" => "et".to_string(),
        "fa-ir" => "fa".to_string(),
        "fi-fi" => "fi".to_string(),
        "fil" | "fil-ph" => "tl".to_string(),
        "fr-fr" => "fr".to_string(),
        "he-il" | "iw" => "he".to_string(),
        "hi-in" => "hi".to_string(),
        "hu-hu" => "hu".to_string(),
        "id-id" => "id".to_string(),
        "it-it" => "it".to_string(),
        "ja-jp" => "ja".to_string(),
        "ko-kr" => "ko".to_string(),
        "lt-lt" => "lt".to_string(),
        "lv-lv" => "lv".to_string(),
        "ms-my" => "ms".to_string(),
        "nb" | "nb-no" => "no".to_string(),
        "nl-nl" => "nl".to_string(),
        "pl-pl" => "pl".to_string(),
        "pt-br" | "pt-pt" => "pt".to_string(),
        "ro-ro" => "ro".to_string(),
        "ru-ru" => "ru".to_string(),
        "sk-sk" => "sk".to_string(),
        "sl-si" => "sl".to_string(),
        "sv-se" => "sv".to_string(),
        "ta-in" => "ta".to_string(),
        "te-in" => "te".to_string(),
        "th-th" => "th".to_string(),
        "tr-tr" => "tr".to_string(),
        "uk-ua" => "uk".to_string(),
        "ur-pk" => "ur".to_string(),
        "vi-vn" => "vi".to_string(),
        other => other.to_string(),
    }
}

fn localize_preview_status_text(state: &mut EasydictUiState) {
    if matches!(
        state.status_text.as_str(),
        "Ready" | "Connected" | "Disconnected"
    ) {
        state.status_text = tr_locale(&state.settings.ui_language, "status.ready", "Ready");
    }
}

fn sanitized_settings_snapshot(settings: &SettingsState) -> SettingsState {
    let mut snapshot = settings.clone();
    snapshot.unsaved_changes = false;
    snapshot.show_unsaved_changes_dialog = false;
    snapshot.save_error_message = None;
    snapshot.pending_mdx_delete_service_id = None;
    snapshot.expanded_service_configurations.clear();
    reset_settings_reorder_modes(&mut snapshot);
    snapshot
}

fn save_settings_changes(state: &mut EasydictUiState) {
    if let Err(message) = validate_settings_changes(&state.settings) {
        state.settings.save_error_message = Some(message);
        state.settings.show_unsaved_changes_dialog = false;
        return;
    }

    ensure_window_services_have_enabled(&mut state.settings);
    for (results, services) in [
        (&mut state.results, &state.settings.main_window_services),
        (
            &mut state.mini.results,
            &state.settings.mini_window_services,
        ),
        (
            &mut state.fixed.results,
            &state.settings.fixed_window_services,
        ),
    ] {
        apply_window_service_settings(results, services);
    }
    apply_hide_empty_to_all_results(state, state.settings.hide_empty_service_results);

    state.settings.unsaved_changes = false;
    state.settings.show_unsaved_changes_dialog = false;
    state.settings.save_error_message = None;
    state.settings.pending_mdx_delete_service_id = None;
    reset_settings_reorder_modes(&mut state.settings);
    state.saved_settings = sanitized_settings_snapshot(&state.settings);
    state.settings_open = false;
}

fn validate_settings_changes(settings: &SettingsState) -> Result<(), String> {
    if same_language(&settings.first_language, &settings.second_language) {
        return Err("First Language and Second Language must be different.".to_string());
    }

    if settings.deepl_use_quality_optimized && settings.deepl_api_key.trim().is_empty() {
        return Err("DeepL quality-optimized mode requires an API key.".to_string());
    }

    if settings.proxy_enabled {
        let proxy_url = settings.proxy_url.trim();
        if proxy_url.is_empty() {
            return Err("Proxy URL is required when HTTP proxy is enabled.".to_string());
        }

        if !looks_like_absolute_uri(proxy_url) {
            return Err("Proxy URL must be an absolute URI.".to_string());
        }
    }

    Ok(())
}

fn looks_like_absolute_uri(value: &str) -> bool {
    let Some((scheme, rest)) = value.split_once(':') else {
        return false;
    };

    let mut chars = scheme.chars();
    let Some(first) = chars.next() else {
        return false;
    };

    first.is_ascii_alphabetic()
        && chars.all(|ch| ch.is_ascii_alphanumeric() || matches!(ch, '+' | '-' | '.'))
        && !rest.trim().is_empty()
        && !value.chars().any(char::is_whitespace)
}

fn ensure_window_services_have_enabled(settings: &mut SettingsState) {
    for services in [
        &mut settings.main_window_services,
        &mut settings.mini_window_services,
        &mut settings.fixed_window_services,
    ] {
        if services.iter().any(|service| service.enabled) {
            continue;
        }

        if let Some(service) = services.first_mut() {
            service.enabled = true;
            service.enabled_query = true;
        }
    }
}

fn apply_window_service_settings(
    results: &mut Vec<TranslationResultPreview>,
    services: &[WindowServiceSetting],
) {
    let previous = std::mem::take(results);
    let mut updated = Vec::new();

    for service in services.iter().filter(|service| service.enabled) {
        let mut result = previous
            .iter()
            .find(|result| result.id == service.service_id)
            .cloned()
            .unwrap_or_else(|| {
                TranslationResultPreview::new(
                    service.service_id.clone(),
                    service.display_name.clone(),
                    String::new(),
                )
                .expanded(false)
            });

        result.service_name = service.display_name.clone();
        result.enabled_query = service.enabled_query;
        let (streaming_capable, grammar_capable) =
            translation_service_capabilities(&service.service_id);
        result.streaming_capable = streaming_capable;
        result.grammar_capable = grammar_capable;
        if !service.enabled_query {
            result.has_queried = false;
            result.status = ResultStatus::Ready;
        }
        updated.push(result);
    }

    *results = updated;
}

fn discard_settings_changes(state: &mut EasydictUiState) {
    state.settings = sanitized_settings_snapshot(&state.saved_settings);
    apply_hide_empty_to_all_results(state, state.settings.hide_empty_service_results);
    state.settings_open = false;
}

fn reset_settings_reorder_modes(settings: &mut SettingsState) {
    settings.main_window_reorder_mode = false;
    settings.mini_window_reorder_mode = false;
    settings.fixed_window_reorder_mode = false;
}

fn window_services_mut(
    settings: &mut SettingsState,
    surface: QuickTranslateSurface,
) -> &mut Vec<WindowServiceSetting> {
    match surface {
        QuickTranslateSurface::Main => &mut settings.main_window_services,
        QuickTranslateSurface::Mini => &mut settings.mini_window_services,
        QuickTranslateSurface::Fixed => &mut settings.fixed_window_services,
    }
}

fn set_window_service_enabled(
    settings: &mut SettingsState,
    surface: QuickTranslateSurface,
    service_id: &str,
    enabled: bool,
) -> bool {
    let Some(service) = window_services_mut(settings, surface)
        .iter_mut()
        .find(|service| service.service_id == service_id)
    else {
        return false;
    };

    if service.enabled == enabled {
        return false;
    }

    service.enabled = enabled;
    true
}

fn set_window_service_enabled_query(
    settings: &mut SettingsState,
    surface: QuickTranslateSurface,
    service_id: &str,
    enabled_query: bool,
) -> bool {
    let Some(service) = window_services_mut(settings, surface)
        .iter_mut()
        .find(|service| service.service_id == service_id)
    else {
        return false;
    };

    if service.enabled_query == enabled_query {
        return false;
    }

    service.enabled_query = enabled_query;
    true
}

fn move_window_service(
    settings: &mut SettingsState,
    surface: QuickTranslateSurface,
    service_id: &str,
    offset: isize,
) -> bool {
    let services = window_services_mut(settings, surface);
    let Some(index) = services
        .iter()
        .position(|service| service.service_id == service_id)
    else {
        return false;
    };

    let target = index.saturating_add_signed(offset);
    if target == index || target >= services.len() {
        return false;
    }

    services.swap(index, target);
    true
}

fn apply_mdx_dictionary_selection(state: &mut EasydictUiState, path: Option<String>) -> bool {
    let Some(path) = path
        .map(|path| path.trim().to_string())
        .filter(|path| !path.is_empty())
    else {
        return false;
    };

    let display_name = mdx_display_name(&path);
    let service_id = mdx_service_id(
        &display_name,
        &path,
        &state.settings.imported_mdx_dictionaries,
    );

    let dictionary = ImportedMdxDictionary {
        service_id: service_id.clone(),
        display_name: display_name.clone(),
        file_path: path.clone(),
        is_encrypted: detect_mdx_file_is_encrypted(&path).unwrap_or(false),
        regcode: None,
        email: None,
        mdd_file_paths: discover_mdd_file_paths(&path),
    };

    if let Some(existing) = state
        .settings
        .imported_mdx_dictionaries
        .iter_mut()
        .find(|dictionary| dictionary.file_path.eq_ignore_ascii_case(&path))
    {
        *existing = dictionary;
    } else {
        state.settings.imported_mdx_dictionaries.push(dictionary);
    }

    add_mdx_result_row(&mut state.results, &service_id, &display_name);
    add_mdx_result_row(&mut state.mini.results, &service_id, &display_name);
    add_mdx_result_row(&mut state.fixed.results, &service_id, &display_name);
    add_window_service_setting(
        &mut state.settings.main_window_services,
        &service_id,
        &display_name,
    );
    add_window_service_setting(
        &mut state.settings.mini_window_services,
        &service_id,
        &display_name,
    );
    add_window_service_setting(
        &mut state.settings.fixed_window_services,
        &service_id,
        &display_name,
    );
    true
}

fn update_mdx_dictionary_email(
    settings: &mut SettingsState,
    service_id: &str,
    value: String,
) -> bool {
    let Some(dictionary) = settings
        .imported_mdx_dictionaries
        .iter_mut()
        .find(|dictionary| dictionary.service_id == service_id)
    else {
        return false;
    };

    let value = non_empty_setting(&value);
    if dictionary.email == value {
        return false;
    }

    dictionary.email = value;
    true
}

fn update_mdx_dictionary_regcode(
    settings: &mut SettingsState,
    service_id: &str,
    value: String,
) -> bool {
    let Some(dictionary) = settings
        .imported_mdx_dictionaries
        .iter_mut()
        .find(|dictionary| dictionary.service_id == service_id)
    else {
        return false;
    };

    let value = non_empty_setting(&value);
    if dictionary.regcode == value {
        return false;
    }

    dictionary.regcode = value;
    true
}

fn rescan_mdx_mdd_files(settings: &mut SettingsState, service_id: &str) -> bool {
    let Some(dictionary) = settings
        .imported_mdx_dictionaries
        .iter_mut()
        .find(|dictionary| dictionary.service_id == service_id)
    else {
        return false;
    };

    let discovered = discover_mdd_file_paths(&dictionary.file_path);
    if dictionary.mdd_file_paths == discovered {
        return false;
    }

    dictionary.mdd_file_paths = discovered;
    true
}

fn remove_mdx_dictionary(state: &mut EasydictUiState, service_id: &str) -> bool {
    let before = state.settings.imported_mdx_dictionaries.len();
    state
        .settings
        .imported_mdx_dictionaries
        .retain(|dictionary| dictionary.service_id != service_id);
    if state.settings.imported_mdx_dictionaries.len() == before {
        return false;
    }

    for services in [
        &mut state.settings.main_window_services,
        &mut state.settings.mini_window_services,
        &mut state.settings.fixed_window_services,
    ] {
        remove_window_service_setting(services, service_id);
    }

    for results in [
        &mut state.results,
        &mut state.mini.results,
        &mut state.fixed.results,
    ] {
        remove_mdx_result_row(results, service_id);
    }

    if state.settings.imported_mdx_dictionaries.is_empty() {
        state.active_suggestion_query_id = None;
        state.local_dictionary_suggestion_query = None;
        state.local_dictionary_suggestions.clear();
        state.local_dictionary_suggestion_active_index = None;
        state.local_dictionary_suggestion_error = None;
    }

    true
}

fn add_window_service_setting(
    services: &mut Vec<WindowServiceSetting>,
    service_id: &str,
    display_name: &str,
) {
    if let Some(service) = services
        .iter_mut()
        .find(|service| service.service_id == service_id)
    {
        service.display_name = display_name.to_string();
        service.configured = true;
        return;
    }

    services.push(WindowServiceSetting::new(service_id, display_name));
}

fn remove_window_service_setting(services: &mut Vec<WindowServiceSetting>, service_id: &str) {
    services.retain(|service| service.service_id != service_id);
}

fn add_mdx_result_row(
    results: &mut Vec<TranslationResultPreview>,
    service_id: &str,
    display_name: &str,
) {
    if results.iter().any(|result| result.id == service_id) {
        return;
    }

    results.push(TranslationResultPreview::new(
        service_id.to_string(),
        display_name.to_string(),
        "Ready",
    ));
}

fn remove_mdx_result_row(results: &mut Vec<TranslationResultPreview>, service_id: &str) {
    results.retain(|result| result.id != service_id);
}

fn mdx_display_name(path: &str) -> String {
    Path::new(path)
        .file_stem()
        .and_then(|stem| stem.to_str())
        .filter(|stem| !stem.trim().is_empty())
        .map(|stem| stem.trim().to_string())
        .or_else(|| {
            path.rsplit_once(['\\', '/'])
                .map(|(_, file_name)| file_name)
                .and_then(|file_name| file_name.rsplit_once('.').map(|(stem, _)| stem))
                .filter(|stem| !stem.trim().is_empty())
                .map(|stem| stem.trim().to_string())
        })
        .unwrap_or_else(|| "MDX Dictionary".to_string())
}

fn mdx_service_id(
    display_name: &str,
    path: &str,
    dictionaries: &[ImportedMdxDictionary],
) -> String {
    if let Some(existing) = dictionaries
        .iter()
        .find(|dictionary| dictionary.file_path.eq_ignore_ascii_case(path))
    {
        return existing.service_id.clone();
    }

    let slug = slugify_service_name(display_name);
    let base = format!("mdx::{slug}");
    if !dictionaries
        .iter()
        .any(|dictionary| dictionary.service_id.eq_ignore_ascii_case(&base))
    {
        return base;
    }

    for suffix in 2.. {
        let candidate = format!("{base}-{suffix}");
        if !dictionaries
            .iter()
            .any(|dictionary| dictionary.service_id.eq_ignore_ascii_case(&candidate))
        {
            return candidate;
        }
    }

    unreachable!("unbounded suffix search should find an MDX service id")
}

fn slugify_service_name(value: &str) -> String {
    let mut slug = String::new();
    let mut previous_dash = false;

    for character in value.chars() {
        if character.is_ascii_alphanumeric() {
            slug.push(character.to_ascii_lowercase());
            previous_dash = false;
        } else if !previous_dash && !slug.is_empty() {
            slug.push('-');
            previous_dash = true;
        }
    }

    while slug.ends_with('-') {
        slug.pop();
    }

    if slug.is_empty() {
        "dictionary".to_string()
    } else {
        slug
    }
}

fn long_document_input_mode_for_path(path: &str) -> Option<&'static str> {
    let extension = Path::new(path)
        .extension()
        .and_then(|extension| extension.to_str())
        .map(str::to_ascii_lowercase)
        .or_else(|| {
            path.rsplit_once('.')
                .map(|(_, extension)| extension.to_ascii_lowercase())
        })?;

    match extension.as_str() {
        "pdf" => Some("pdf"),
        "md" | "markdown" => Some("markdown"),
        "txt" | "text" => Some("plaintext"),
        _ => None,
    }
}

fn long_document_input_mode_from_preview(value: &str) -> String {
    match value.trim().to_ascii_lowercase().as_str() {
        "text" | "txt" | "plain" | "plaintext" => "plaintext",
        "md" | "markdown" => "markdown",
        "pdf" => "pdf",
        _ => "pdf",
    }
    .to_string()
}

fn long_document_output_mode_from_preview(value: &str) -> String {
    match value.trim().to_ascii_lowercase().as_str() {
        "mono" | "monolingual" | "translated" => "mono",
        "bilingual" | "bi" => "bilingual",
        "both" => "both",
        _ => "mono",
    }
    .to_string()
}

fn apply_capture_overlay_preview(state: &mut EasydictUiState, value: &str) {
    use crate::screen_capture::{
        CaptureInteractionState, CapturePhase, CapturePoint, CaptureRect, DetectedWindow,
        WindowDetector,
    };

    let detector = WindowDetector::from_windows([
        DetectedWindow::new(1, CaptureRect::new(40, 48, 820, 560))
            .with_children([DetectedWindow::new(2, CaptureRect::new(96, 118, 720, 458))
                .with_children([DetectedWindow::new(3, CaptureRect::new(126, 158, 680, 372))])]),
        DetectedWindow::new(4, CaptureRect::new(860, 92, 1220, 360)),
    ]);
    let mut interaction = CaptureInteractionState::new();
    let preview = value.trim().to_ascii_lowercase();

    match preview.as_str() {
        "detect" | "detecting" | "window" | "window-detect" | "window_detect" => {
            interaction.on_mouse_move(CapturePoint::new(168, 188), &detector);
        }
        "depth" | "nested-window" | "nested_window" => {
            interaction.on_mouse_move(CapturePoint::new(168, 188), &detector);
            interaction.on_mouse_wheel(-120, CapturePoint::new(168, 188), &detector);
        }
        "drag" | "drag-selection" | "drag_selection" | "selecting" => {
            interaction.on_left_button_down(CapturePoint::new(180, 164));
            interaction.on_mouse_move(CapturePoint::new(604, 386), &detector);
        }
        "selected" | "handles" | "magnifier" => {
            interaction.phase = CapturePhase::Selecting;
            interaction.selection = Some(CaptureRect::new(180, 164, 604, 386));
        }
        "adjust" | "adjusting" => {
            interaction.set_adjusting_selection(CaptureRect::new(180, 164, 604, 386));
        }
        _ => {}
    }

    state.capture_window_detector = detector;
    state.capture_selection = interaction.selection;
    state.capture_interaction = interaction;
}

fn parent_folder(path: &str) -> Option<String> {
    Path::new(path)
        .parent()
        .and_then(|parent| parent.to_str())
        .map(str::to_string)
        .filter(|parent| !parent.is_empty())
        .or_else(|| {
            path.rsplit_once('\\')
                .map(|(parent, _)| parent.to_string())
                .filter(|parent| !parent.is_empty())
        })
}

fn apply_hide_empty_service_results(results: &mut Vec<TranslationResultPreview>, enabled: bool) {
    for result in results.iter_mut() {
        if result.no_result {
            result.demoted = enabled;
            if enabled {
                result.expanded = false;
            }
        }
    }
    stable_partition_demoted(results);
}

fn apply_hide_empty_to_all_results(state: &mut EasydictUiState, enabled: bool) {
    for results in [
        &mut state.results,
        &mut state.mini.results,
        &mut state.fixed.results,
    ] {
        apply_hide_empty_service_results(results, enabled);
    }
}

pub(crate) fn stable_partition_demoted(results: &mut Vec<TranslationResultPreview>) {
    let mut demoted = Vec::new();
    let mut kept = Vec::with_capacity(results.len());

    for result in results.drain(..) {
        if result.demoted {
            demoted.push(result);
        } else {
            kept.push(result);
        }
    }

    kept.extend(demoted);
    *results = kept;
}

fn floating_surface_mut(
    state: &mut EasydictUiState,
    surface: QuickTranslateSurface,
) -> Option<&mut FloatingWindowState> {
    match surface {
        QuickTranslateSurface::Main => None,
        QuickTranslateSurface::Mini => Some(&mut state.mini),
        QuickTranslateSurface::Fixed => Some(&mut state.fixed),
    }
}

fn capture_result_action(
    state: &mut EasydictUiState,
    kind: ResultActionKind,
    surface: QuickTranslateSurface,
    service_id: &str,
) {
    if let Some(intent) = resolve_result_action_intent(state, kind, surface, service_id) {
        state.last_result_action = Some(intent);
    }
}

pub fn resolve_result_action_intent(
    state: &EasydictUiState,
    kind: ResultActionKind,
    surface: QuickTranslateSurface,
    service_id: &str,
) -> Option<ResultActionIntent> {
    let result = result_for_surface(state, surface, service_id)?;
    let text = result.result_body();
    if text.trim().is_empty() {
        return None;
    }

    Some(ResultActionIntent {
        kind,
        surface,
        service_id: service_id.to_string(),
        text,
        language: result_action_language(state, surface),
    })
}

fn result_for_surface<'a>(
    state: &'a EasydictUiState,
    surface: QuickTranslateSurface,
    service_id: &str,
) -> Option<&'a TranslationResultPreview> {
    match surface {
        QuickTranslateSurface::Main => &state.results,
        QuickTranslateSurface::Mini => &state.mini.results,
        QuickTranslateSurface::Fixed => &state.fixed.results,
    }
    .iter()
    .find(|result| result.id == service_id)
}

fn result_action_language(state: &EasydictUiState, surface: QuickTranslateSurface) -> String {
    match surface {
        QuickTranslateSurface::Main => &state.target_language,
        QuickTranslateSurface::Mini => &state.mini.target_language,
        QuickTranslateSurface::Fixed => &state.fixed.target_language,
    }
    .clone()
}

/// Frozen desktop screenshot backing the capture overlay (raw BGRA pixel file
/// written by the native screen-capture helper).
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CaptureBackground {
    pub bgra_path: String,
    pub pixel_width: u32,
    pub pixel_height: u32,
}

/// Freezes the desktop for the capture overlay, mirroring the WinUI
/// ScreenCaptureWindow's BitBlt-on-open. Returns `None` when the platform
/// capture fails (the overlay then falls back to the plain dim mask).
pub fn capture_screen_background() -> Option<CaptureBackground> {
    crate::screen_capture_native::capture_screen_region(
        easydict_windows_screen_capture::ScreenCaptureRequest::virtual_desktop(),
    )
    .map(|capture| CaptureBackground {
        bgra_path: capture.pixel_data_path,
        pixel_width: capture.pixel_width,
        pixel_height: capture.pixel_height,
    })
}

fn preview_waiting_results() -> Vec<TranslationResultPreview> {
    vec![
        TranslationResultPreview::new("bing", "Bing Translate", "").manual_query(),
        TranslationResultPreview::new("windows-local-ai", "Windows Local AI", "").manual_query(),
        TranslationResultPreview::new(
            "mdx::collins-cobuild-english-usage",
            PREVIEW_DOTNET_REFERENCE_MDX_DISPLAY_NAME,
            "",
        )
        .expanded(false),
        TranslationResultPreview::new("google", "Google Translate", "").expanded(false),
        TranslationResultPreview::new("volcano", "Volcano", "").manual_query(),
    ]
}

fn apply_initial_quick_translate_preview(state: &mut EasydictUiState) {
    state.source_text.clear();
    state.detected_language = None;
    state.services_completed = 0;
    state.results = preview_waiting_results();
}

fn apply_before_translate_preview(state: &mut EasydictUiState) {
    state.source_text = "Hello from the Rust main window preview".to_string();
    state.detected_language = None;
    state.services_completed = 0;
    state.results = preview_waiting_results();
}

pub fn theme_from_id(id: &str) -> ThemeMode {
    match id {
        "dark" => ThemeMode::Dark,
        "minimal" => ThemeMode::Minimal,
        "high-contrast" => ThemeMode::HighContrast,
        "system" => ThemeMode::System,
        _ => ThemeMode::Light,
    }
}

fn env_truthy(value: &str) -> bool {
    matches!(
        value.trim().to_ascii_lowercase().as_str(),
        "1" | "true" | "yes" | "on"
    )
}

pub fn preview_control_state_from_id(value: &str) -> ControlState {
    match value.trim().to_ascii_lowercase().as_str() {
        "hover" | "hovered" | "pointerover" | "pointer-over" => {
            ControlState::default().hovered(true)
        }
        "press" | "pressed" | "pointerpressed" | "pointer-pressed" => {
            ControlState::default().hovered(true).pressed(true)
        }
        "focus" | "focused" | "keyboardfocus" | "keyboard-focus" => {
            ControlState::default().focused(true)
        }
        "disabled" => ControlState::default().disabled(),
        _ => ControlState::default(),
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ResultActionKind {
    Copy,
    Speak,
    Replace,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ResultActionIntent {
    pub kind: ResultActionKind,
    pub surface: QuickTranslateSurface,
    pub service_id: String,
    pub text: String,
    pub language: String,
}

#[derive(Clone, Debug, PartialEq)]
pub enum Message {
    ModeChanged(String),
    SourceTextChanged(String),
    SourceTextSubmitted,
    FloatingTextChanged(String),
    FloatingSurfaceTextChanged(QuickTranslateSurface, String),
    LongDocumentSourceTextChanged(String),
    SourceLanguageChanged(String),
    TargetLanguageChanged(String),
    FloatingSourceLanguageChanged(QuickTranslateSurface, String),
    FloatingTargetLanguageChanged(QuickTranslateSurface, String),
    LongDocumentSourceLanguageChanged(String),
    LongDocumentTargetLanguageChanged(String),
    LongDocumentServiceChanged(String),
    LongDocumentInputModeChanged(String),
    LongDocumentOutputModeChanged(String),
    LongDocumentConcurrencyChanged(String),
    LongDocumentPageRangeChanged(String),
    LongDocumentFileSelected(Option<String>),
    LongDocumentOutputFolderSelected(Option<String>),
    MdxDictionarySelected(Option<String>),
    SettingsSectionChanged(String),
    ThemeChanged(String),
    ToggleMinimizeToTray(bool),
    ToggleStartMinimized(bool),
    ToggleMonitorClipboard(bool),
    ToggleMouseSelectionTranslate(bool),
    MouseSelectionExcludedAppsChanged(String),
    ToggleLaunchAtStartup(bool),
    ToggleShellContextMenu(bool),
    ToggleInternationalServices(bool),
    ToggleHideEmptyServiceResults(bool),
    TtsSpeedChanged(String),
    ToggleAutoPlayTranslation(bool),
    OcrEngineChanged(String),
    OcrApiKeyChanged(String),
    OcrEndpointChanged(String),
    OcrModelChanged(String),
    OcrSystemPromptChanged(String),
    TestOcrConnection,
    LayoutDetectionModeChanged(String),
    VisionLayoutServiceChanged(String),
    DownloadLayoutModel,
    DeleteLayoutModel,
    DownloadCjkFont,
    DeleteCjkFont,
    FormulaFontPatternChanged(String),
    FormulaCharPatternChanged(String),
    ToggleTranslationCache(bool),
    ClearTranslationCache,
    CustomTranslationPromptChanged(String),
    ToggleProxyEnabled(bool),
    ProxyUrlChanged(String),
    ToggleProxyBypassLocal(bool),
    DeepLApiKeyChanged(String),
    ToggleDeepLUseFreeApi(bool),
    ToggleDeepLUseQualityOptimized(bool),
    OpenAIApiKeyChanged(String),
    OpenAIEndpointChanged(String),
    OpenAIModelChanged(String),
    OpenAIApiFormatChanged(String),
    TestOpenAI,
    OllamaEndpointChanged(String),
    OllamaModelChanged(String),
    RefreshOllamaModels,
    TestOllama,
    LocalAiProviderChanged(String),
    PrepareLocalAiModel,
    OpenWindowsAiUpdate,
    FoundryLocalEndpointChanged(String),
    FoundryLocalModelChanged(String),
    StartFoundryLocal,
    FoundryLocalPrepareFinished(
        Result<crate::openai_compatible::FoundryLocalPrepareOutcome, String>,
    ),
    WindowsAiPrepareFinished(Result<easydict_windows_ai::WindowsAiStatus, String>),
    InstallFoundryLocal,
    OpenFoundryLocalDocs,
    OpenVinoDeviceChanged(String),
    DownloadOpenVinoModel,
    OpenVinoDownloadFinished(Result<crate::openvino_download::OpenVinoDownloadStatus, String>),
    ServiceProviderSettingChanged(String, ServiceProviderField, String),
    TestServiceProvider(String),
    ToggleServiceConfigurationExpanded(String, bool),
    CaiyunApiKeyChanged(String),
    TestCaiyun,
    NiuTransApiKeyChanged(String),
    TestNiuTrans,
    YoudaoAppKeyChanged(String),
    YoudaoAppSecretChanged(String),
    ToggleYoudaoUseOfficialApi(bool),
    TestYoudao,
    VolcanoAccessKeyIdChanged(String),
    VolcanoSecretAccessKeyChanged(String),
    TestVolcano,
    ToggleLocalDictionarySuggestions(bool),
    UiLanguageChanged(String),
    OcrLanguageChanged(String),
    FirstLanguageChanged(String),
    SecondLanguageChanged(String),
    ToggleAutoSelectTargetLanguage(bool),
    ToggleSelectedLanguage(String, bool),
    ToggleTranslationLanguagesExpanded(bool),
    ToggleHotkey(String, bool),
    HotkeyShortcutChanged(String, String),
    ToggleMiniAutoClose(bool),
    ToggleFixedAlwaysOnTop(bool),
    ToggleWindowReorderMode(QuickTranslateSurface),
    ToggleWindowService(QuickTranslateSurface, String, bool),
    ToggleWindowServiceQuery(QuickTranslateSurface, String, bool),
    MoveWindowService(QuickTranslateSurface, String, isize),
    ToggleTwoPassContext(bool),
    TogglePin(bool),
    ToggleResultExpanded(String),
    ToggleResultExpandedIn(QuickTranslateSurface, String),
    InstallBrowserSupport,
    UninstallBrowserSupport,
    BrowserSupportStatusLoaded(Result<crate::browser_registrar::StatusOutput, String>),
    SwapLanguages,
    SwapFloatingLanguages(QuickTranslateSurface),
    QuickTranslate,
    QuickTranslateIn(QuickTranslateSurface),
    QuickTranslateFinished(crate::quick_translate::QuickTranslateOutcome),
    QuickTranslateServiceFinished(crate::quick_translate::QuickTranslateServiceUpdate),
    QuickTranslateStreamChunk(crate::quick_translate::QuickTranslateStreamChunk),
    LocalDictionarySuggestionsFinished(LocalDictionarySuggestionUpdate),
    FocusLocalDictionarySuggestions,
    MoveLocalDictionarySuggestion(isize),
    CommitLocalDictionarySuggestion,
    DismissLocalDictionarySuggestions,
    ExitLocalDictionarySuggestions,
    LongDocumentFinished(crate::long_document::LongDocumentOutcome),
    OcrCaptureFinished(crate::ocr::OcrCaptureResult),
    SilentOcrCaptureFinished(crate::ocr::OcrCaptureResult),
    OcrCaptureCancelled(crate::ocr::OcrMode),
    OcrRecognizeFinished(crate::ocr::OcrOutcome),
    CaptureSelectionChanged(Option<crate::screen_capture::CaptureRect>),
    CaptureWindowsChanged(Vec<crate::screen_capture::DetectedWindow>),
    CaptureMouseMoved(crate::screen_capture::CapturePoint),
    CaptureLeftButtonDown(crate::screen_capture::CapturePoint),
    CaptureLeftButtonUp(crate::screen_capture::CapturePoint),
    CaptureDoubleClick(crate::screen_capture::CapturePoint),
    CaptureRightButtonDown,
    CaptureMouseWheel {
        delta: i32,
        point: crate::screen_capture::CapturePoint,
    },
    CaptureNudgeSelection {
        delta_x: i32,
        delta_y: i32,
    },
    CaptureEscape,
    HotkeyTriggered(String),
    TrayCommand(String),
    WindowEvent(WindowEvent),
    ClipboardTextReceived(Option<String>),
    TrayClipboardTextReceived(Option<String>),
    Translate,
    CopyResult,
    CopyResultIn(QuickTranslateSurface, String),
    ReplaceResult,
    ReplaceResultIn(QuickTranslateSurface, String),
    RetryResult,
    RetryResultIn(QuickTranslateSurface, String),
    SpeakResult,
    SpeakResultIn(QuickTranslateSurface, String),
    OpenSettings,
    /// Result of the async settings runtime-status check (model/font on-disk
    /// availability), used to settle the `settings_runtime` [`Loadable`] and
    /// populate the displayed statuses.
    SettingsRuntimeStatusLoaded(crate::settings_status::SettingsRuntimeStatus),
    BuiltInAiDeviceRegistrationFinished(Result<Option<String>, String>),
    Back,
    SaveSettingsChanges,
    DiscardSettingsChanges,
    CancelSettingsChangesDialog,
    DismissSettingsError,
    OpenSettingsLink(SettingsLink),
    MinimizeWindow,
    ToggleMaximizeWindow,
    CloseMainWindow,
    CloseWindow,
    BrowseFile,
    BrowseOutputFolder,
    ImportMdxDictionary,
    MdxDictionaryEmailChanged(String, String),
    MdxDictionaryRegcodeChanged(String, String),
    RescanMdxMddFiles(String),
    RequestDeleteMdxDictionary(String),
    ConfirmDeleteMdxDictionary,
    CancelDeleteMdxDictionary,
    ApplyLocalDictionarySuggestion(String),
    RetryLongDocument,
    ClearHistory,
    ConfirmCapture,
    CancelCapture,
    TranslateSelection,
    SelectionTextReady {
        text: String,
        anchor_x: i32,
        anchor_y: i32,
        generation: u64,
    },
    MouseSelectionInputHookEvent(easydict_windows_text_selection::LowLevelInputHookEvent),
    MouseSelectionPendingMultiClickElapsed(u64),
    DismissPopButton,
    PopButtonAutoDismiss(u64),
    PopButtonClicked,
    Noop,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dotnet_reference_window_services_use_catalog_ids_for_parity_order() {
        let services = dotnet_reference_window_services();
        let first_ids = services
            .iter()
            .take(5)
            .map(|service| service.service_id.as_str())
            .collect::<Vec<_>>();

        assert_eq!(
            first_ids,
            [
                "bing",
                "windows-local-ai",
                PREVIEW_DOTNET_REFERENCE_MDX_SERVICE_ID,
                "google",
                "volcano",
            ]
        );

        let windows_local_ai = services
            .iter()
            .find(|service| service.service_id == "windows-local-ai")
            .expect("dotnet reference profile should include Windows Local AI");
        assert!(windows_local_ai.enabled);
        assert!(!windows_local_ai.enabled_query);

        let custom_openai = services
            .iter()
            .find(|service| service.service_id == "custom-openai")
            .expect("dotnet reference profile should keep Custom OpenAI by catalog id");
        assert!(!custom_openai.enabled);

        let volcano = services
            .iter()
            .find(|service| service.service_id == "volcano")
            .expect("dotnet reference profile should include Volcano");
        assert!(volcano.enabled);
        assert!(volcano.configured);
        assert!(!volcano.enabled_query);
    }
}
