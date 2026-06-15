use std::sync::OnceLock;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TranslationServiceKind {
    TextTranslation,
    Dictionary,
    OpenAiCompatible,
    CustomStreaming,
    TraditionalHttp,
    LocalAi,
    ImportedMdx,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct TranslationServiceDescriptor {
    pub service_id: &'static str,
    pub display_name: &'static str,
    pub kind: TranslationServiceKind,
    pub configured_by_default: bool,
    pub requires_api_key: bool,
    pub streaming_capable: bool,
    pub grammar_capable: bool,
}

impl TranslationServiceDescriptor {
    pub const fn new(
        service_id: &'static str,
        display_name: &'static str,
        kind: TranslationServiceKind,
    ) -> Self {
        Self {
            service_id,
            display_name,
            kind,
            configured_by_default: true,
            requires_api_key: false,
            streaming_capable: false,
            grammar_capable: false,
        }
    }

    pub const fn unconfigured(mut self) -> Self {
        self.configured_by_default = false;
        self
    }

    pub const fn requires_api_key(mut self) -> Self {
        self.requires_api_key = true;
        self
    }

    pub const fn streaming(mut self) -> Self {
        self.streaming_capable = true;
        self
    }

    pub const fn grammar(mut self) -> Self {
        self.grammar_capable = true;
        self
    }
}

pub const DEFAULT_SERVICE_ID: &str = "google";
pub const DEFAULT_MAIN_WINDOW_SERVICE_IDS: [&str; 3] = ["google", "bing", "openai"];
pub const DEFAULT_FLOATING_WINDOW_SERVICE_IDS: [&str; 1] = ["google"];

fn build_translation_service_descriptors() -> Vec<TranslationServiceDescriptor> {
    let mut services = vec![
        TranslationServiceDescriptor::new(
            "google",
            "Google Translate",
            TranslationServiceKind::TextTranslation,
        ),
        TranslationServiceDescriptor::new(
            "google_web",
            "Google Dict",
            TranslationServiceKind::Dictionary,
        ),
        TranslationServiceDescriptor::new(
            "bing",
            "Bing Translate",
            TranslationServiceKind::TextTranslation,
        ),
        TranslationServiceDescriptor::new(
            "deepl",
            "DeepL",
            TranslationServiceKind::TextTranslation,
        ),
        TranslationServiceDescriptor::new("youdao", "Youdao", TranslationServiceKind::Dictionary),
        TranslationServiceDescriptor::new(
            "openai",
            "OpenAI",
            TranslationServiceKind::OpenAiCompatible,
        )
        .unconfigured()
        .requires_api_key()
        .streaming()
        .grammar(),
        TranslationServiceDescriptor::new(
            "ollama",
            "Ollama",
            TranslationServiceKind::OpenAiCompatible,
        )
        .streaming()
        .grammar(),
        TranslationServiceDescriptor::new(
            "builtin",
            "Built-in AI",
            TranslationServiceKind::OpenAiCompatible,
        )
        .streaming()
        .grammar(),
        TranslationServiceDescriptor::new(
            "deepseek",
            "DeepSeek",
            TranslationServiceKind::OpenAiCompatible,
        )
        .unconfigured()
        .requires_api_key()
        .streaming()
        .grammar(),
        TranslationServiceDescriptor::new("groq", "Groq", TranslationServiceKind::OpenAiCompatible)
            .unconfigured()
            .requires_api_key()
            .streaming()
            .grammar(),
        TranslationServiceDescriptor::new(
            "zhipu",
            "Zhipu (智谱)",
            TranslationServiceKind::OpenAiCompatible,
        )
        .unconfigured()
        .requires_api_key()
        .streaming()
        .grammar(),
        TranslationServiceDescriptor::new(
            "github",
            "GitHub Models",
            TranslationServiceKind::OpenAiCompatible,
        )
        .unconfigured()
        .requires_api_key()
        .streaming()
        .grammar(),
        TranslationServiceDescriptor::new(
            "custom-openai",
            "Custom OpenAI",
            TranslationServiceKind::OpenAiCompatible,
        )
        .unconfigured()
        .requires_api_key()
        .streaming()
        .grammar(),
        TranslationServiceDescriptor::new(
            "gemini",
            "Gemini",
            TranslationServiceKind::CustomStreaming,
        )
        .unconfigured()
        .requires_api_key()
        .streaming()
        .grammar(),
        TranslationServiceDescriptor::new(
            "doubao",
            "Doubao",
            TranslationServiceKind::CustomStreaming,
        )
        .unconfigured()
        .requires_api_key()
        .streaming(),
        TranslationServiceDescriptor::new(
            "caiyun",
            "Caiyun",
            TranslationServiceKind::TraditionalHttp,
        )
        .unconfigured()
        .requires_api_key(),
        TranslationServiceDescriptor::new(
            "niutrans",
            "NiuTrans",
            TranslationServiceKind::TraditionalHttp,
        )
        .unconfigured()
        .requires_api_key(),
        TranslationServiceDescriptor::new(
            "volcano",
            "Volcano",
            TranslationServiceKind::TraditionalHttp,
        )
        .unconfigured()
        .requires_api_key(),
        TranslationServiceDescriptor::new(
            "linguee",
            "Linguee Dictionary",
            TranslationServiceKind::Dictionary,
        ),
    ];

    services.push(
        TranslationServiceDescriptor::new(
            "windows-local-ai",
            "Windows Local AI",
            TranslationServiceKind::LocalAi,
        )
        .streaming()
        .grammar(),
    );
    services
}

/// The default service catalog, built once and reused. Descriptors are `Copy`
/// over `&'static str`, and the set is fixed at compile time, so caching avoids
/// rebuilding the vector on every lookup.
fn cached_translation_service_descriptors() -> &'static [TranslationServiceDescriptor] {
    static DESCRIPTORS: OnceLock<Vec<TranslationServiceDescriptor>> = OnceLock::new();
    DESCRIPTORS.get_or_init(build_translation_service_descriptors)
}

pub fn default_translation_service_descriptors() -> Vec<TranslationServiceDescriptor> {
    cached_translation_service_descriptors().to_vec()
}

pub fn find_translation_service_descriptor(
    service_id: &str,
) -> Option<TranslationServiceDescriptor> {
    cached_translation_service_descriptors()
        .iter()
        .copied()
        .find(|descriptor| descriptor.service_id.eq_ignore_ascii_case(service_id))
}

pub fn translation_service_capabilities(service_id: &str) -> (bool, bool) {
    find_translation_service_descriptor(service_id)
        .map(|descriptor| (descriptor.streaming_capable, descriptor.grammar_capable))
        .unwrap_or((false, false))
}

pub fn openai_compatible_service_ids() -> Vec<&'static str> {
    cached_translation_service_descriptors()
        .iter()
        .filter(|descriptor| descriptor.kind == TranslationServiceKind::OpenAiCompatible)
        .map(|descriptor| descriptor.service_id)
        .collect()
}

pub fn app_visible_translation_service_ids() -> Vec<&'static str> {
    cached_translation_service_descriptors()
        .iter()
        .map(|descriptor| descriptor.service_id)
        .collect()
}

pub fn imported_mdx_service_descriptor(
    service_id: &'static str,
    display_name: &'static str,
) -> TranslationServiceDescriptor {
    TranslationServiceDescriptor::new(
        service_id,
        display_name,
        TranslationServiceKind::ImportedMdx,
    )
}
