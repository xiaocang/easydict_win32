use win_fluent::prelude::*;
use win_fluent::IconToken;

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
            Self::General => icon::settings(),
            Self::Services => icon::edit(),
            Self::Views => icon::search(),
            Self::Hotkeys => IconToken::named("keyboard"),
            Self::Advanced => icon::more(),
            Self::Language => icon::translate(),
            Self::About => icon::check(),
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

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TranslationResultPreview {
    pub id: String,
    pub service_name: String,
    pub body: String,
    pub status: ResultStatus,
    pub latency_ms: Option<u32>,
    pub enabled_query: bool,
    pub has_queried: bool,
    pub demoted: bool,
    pub expanded: bool,
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
            status: ResultStatus::Ready,
            latency_ms: None,
            enabled_query: true,
            has_queried: true,
            demoted: false,
            expanded: true,
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
            self.body.clone(),
        )
        .icon(service_icon(&self.id))
        .expanded(self.expanded)
        .toggleable(!self.demoted)
        .dimmed(self.demoted)
        .status(self.status);

        if let Some(latency_ms) = self.latency_ms {
            item = item.metadata(format!("{latency_ms}ms"));
        }

        if !self.enabled_query && !self.has_queried {
            item = item.pending_hint("Click to query this service");
        }

        item
    }
}

fn service_icon(service_id: &str) -> IconToken {
    match service_id {
        "google" => icon::translate(),
        "bing" => IconToken::with_glyph("service-bing", '\u{E774}'),
        "openai" => IconToken::with_glyph("service-ai", '\u{E8D4}'),
        _ => icon::translate(),
    }
}

fn toggle_result_expanded(results: &mut [TranslationResultPreview], id: &str) {
    if let Some(result) = results.iter_mut().find(|result| result.id == id) {
        if result.demoted {
            return;
        }
        if !result.enabled_query && !result.has_queried && !result.expanded {
            result.has_queried = true;
        }
        result.expanded = !result.expanded;
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LongDocumentState {
    pub source_text: String,
    pub selected_file: String,
    pub source_language: String,
    pub target_language: String,
    pub service: String,
    pub input_mode: String,
    pub output_mode: String,
    pub concurrency: String,
    pub page_range: String,
    pub two_pass_context: bool,
    pub output_folder: String,
    pub status_text: String,
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
            input_mode: "pdf".to_string(),
            output_mode: "bilingual".to_string(),
            concurrency: "4".to_string(),
            page_range: String::new(),
            two_pass_context: true,
            output_folder: "(same as input file folder)".to_string(),
            status_text: "Idle".to_string(),
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
    pub detected_language: Option<String>,
    pub pinned: bool,
    pub status_text: String,
    pub results: Vec<TranslationResultPreview>,
}

impl FloatingWindowState {
    pub fn mini_demo() -> Self {
        Self {
            title: "Quick Translate".to_string(),
            text: "Oh, I am mini window".to_string(),
            source_language: "auto".to_string(),
            target_language: "zh-Hans".to_string(),
            detected_language: Some("Detected: English".to_string()),
            pinned: false,
            status_text: String::new(),
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
            detected_language: Some("Detected: English".to_string()),
            pinned: true,
            status_text: String::new(),
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
pub struct SettingsState {
    pub selected_section: SettingsSection,
    pub theme: ThemeMode,
    pub minimize_to_tray: bool,
    pub start_minimized: bool,
    pub monitor_clipboard: bool,
    pub mouse_selection_translate: bool,
    pub enable_international_services: bool,
    pub mini_auto_close: bool,
    pub fixed_always_on_top: bool,
}

impl Default for SettingsState {
    fn default() -> Self {
        Self {
            selected_section: SettingsSection::General,
            theme: ThemeMode::Light,
            minimize_to_tray: true,
            start_minimized: false,
            monitor_clipboard: false,
            mouse_selection_translate: false,
            enable_international_services: true,
            mini_auto_close: true,
            fixed_always_on_top: true,
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct EasydictUiState {
    pub mode: AppMode,
    pub status_text: String,
    pub source_text: String,
    pub detected_language: Option<String>,
    pub source_language: String,
    pub target_language: String,
    pub services_completed: usize,
    pub results: Vec<TranslationResultPreview>,
    pub long_document: LongDocumentState,
    pub settings: SettingsState,
    pub mini: FloatingWindowState,
    pub fixed: FloatingWindowState,
}

impl Default for EasydictUiState {
    fn default() -> Self {
        Self {
            mode: AppMode::QuickTranslate,
            status_text: "Ready".to_string(),
            source_text: "Artificial intelligence is transforming how we work and live".to_string(),
            detected_language: None,
            source_language: "auto".to_string(),
            target_language: "zh-Hans".to_string(),
            services_completed: 3,
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
                .latency_ms(1032),
            ],
            long_document: LongDocumentState::default(),
            settings: SettingsState::default(),
            mini: FloatingWindowState::mini_demo(),
            fixed: FloatingWindowState::fixed_demo(),
        }
    }
}

impl EasydictUiState {
    pub fn apply(&mut self, message: Message) {
        match message {
            Message::ModeChanged(id) => {
                self.mode = AppMode::from_id(&id);
            }
            Message::SourceTextChanged(value) => {
                self.source_text = value;
            }
            Message::FloatingTextChanged(value) => {
                self.mini.text = value.clone();
                self.fixed.text = value;
            }
            Message::LongDocumentSourceTextChanged(value) => {
                self.long_document.source_text = value;
            }
            Message::SourceLanguageChanged(value) => {
                self.source_language = value;
            }
            Message::TargetLanguageChanged(value) => {
                self.target_language = value;
            }
            Message::LongDocumentSourceLanguageChanged(value) => {
                self.long_document.source_language = value;
            }
            Message::LongDocumentTargetLanguageChanged(value) => {
                self.long_document.target_language = value;
            }
            Message::LongDocumentServiceChanged(value) => {
                self.long_document.service = value;
            }
            Message::LongDocumentInputModeChanged(value) => {
                self.long_document.input_mode = value;
            }
            Message::LongDocumentOutputModeChanged(value) => {
                self.long_document.output_mode = value;
            }
            Message::LongDocumentConcurrencyChanged(value) => {
                self.long_document.concurrency = value;
            }
            Message::LongDocumentPageRangeChanged(value) => {
                self.long_document.page_range = value;
            }
            Message::SettingsSectionChanged(id) => {
                self.settings.selected_section = SettingsSection::from_id(&id);
            }
            Message::ThemeChanged(id) => {
                self.settings.theme = match id.as_str() {
                    "dark" => ThemeMode::Dark,
                    "easydict" => ThemeMode::Easydict,
                    "high-contrast" => ThemeMode::HighContrast,
                    "system" => ThemeMode::System,
                    _ => ThemeMode::Light,
                };
            }
            Message::ToggleMinimizeToTray(value) => self.settings.minimize_to_tray = value,
            Message::ToggleStartMinimized(value) => self.settings.start_minimized = value,
            Message::ToggleMonitorClipboard(value) => self.settings.monitor_clipboard = value,
            Message::ToggleMouseSelectionTranslate(value) => {
                self.settings.mouse_selection_translate = value;
            }
            Message::ToggleInternationalServices(value) => {
                self.settings.enable_international_services = value;
            }
            Message::ToggleMiniAutoClose(value) => self.settings.mini_auto_close = value,
            Message::ToggleFixedAlwaysOnTop(value) => self.settings.fixed_always_on_top = value,
            Message::ToggleTwoPassContext(value) => self.long_document.two_pass_context = value,
            Message::TogglePin(value) => self.mini.pinned = value,
            Message::SwapLanguages => {
                std::mem::swap(&mut self.source_language, &mut self.target_language);
            }
            Message::ToggleResultExpanded(id) => {
                toggle_result_expanded(&mut self.results, &id);
                toggle_result_expanded(&mut self.mini.results, &id);
                toggle_result_expanded(&mut self.fixed.results, &id);
                toggle_result_expanded(&mut self.long_document.history, &id);
            }
            Message::Noop
            | Message::Translate
            | Message::CopyResult
            | Message::SpeakResult
            | Message::OpenSettings
            | Message::Back
            | Message::MinimizeWindow
            | Message::ToggleMaximizeWindow
            | Message::CloseWindow
            | Message::BrowseFile
            | Message::RetryLongDocument
            | Message::ClearHistory
            | Message::ConfirmCapture
            | Message::CancelCapture
            | Message::TranslateSelection => {}
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub enum Message {
    ModeChanged(String),
    SourceTextChanged(String),
    FloatingTextChanged(String),
    LongDocumentSourceTextChanged(String),
    SourceLanguageChanged(String),
    TargetLanguageChanged(String),
    LongDocumentSourceLanguageChanged(String),
    LongDocumentTargetLanguageChanged(String),
    LongDocumentServiceChanged(String),
    LongDocumentInputModeChanged(String),
    LongDocumentOutputModeChanged(String),
    LongDocumentConcurrencyChanged(String),
    LongDocumentPageRangeChanged(String),
    SettingsSectionChanged(String),
    ThemeChanged(String),
    ToggleMinimizeToTray(bool),
    ToggleStartMinimized(bool),
    ToggleMonitorClipboard(bool),
    ToggleMouseSelectionTranslate(bool),
    ToggleInternationalServices(bool),
    ToggleMiniAutoClose(bool),
    ToggleFixedAlwaysOnTop(bool),
    ToggleTwoPassContext(bool),
    TogglePin(bool),
    ToggleResultExpanded(String),
    SwapLanguages,
    Translate,
    CopyResult,
    SpeakResult,
    OpenSettings,
    Back,
    MinimizeWindow,
    ToggleMaximizeWindow,
    CloseWindow,
    BrowseFile,
    RetryLongDocument,
    ClearHistory,
    ConfirmCapture,
    CancelCapture,
    TranslateSelection,
    Noop,
}
