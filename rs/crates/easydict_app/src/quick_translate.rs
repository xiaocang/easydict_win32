use crate::compat_client::{DirectWorkerFacade, WorkerClientError};
use crate::compat_protocol::{
    local_ai_provider_modes, ConfigureParams, DefinitionDto, GrammarCorrectParams,
    GrammarCorrectResultDto, LocalAiTranslateParams, MdxLookupEntry, MdxLookupParams,
    MdxLookupResult, PhoneticDto, SettingsSnapshot, SynonymDto, TranslateParams,
    TranslateStreamResult, TranslationResultDto, WordFormDto, WordResultDto,
};
use crate::custom_streaming::{
    build_custom_streaming_translation_request_plan, cleanup_custom_streaming_translation_text,
    correct_custom_streaming_grammar, custom_streaming_config_for_service,
    execute_custom_streaming_request, translate_custom_streaming_service,
    CustomStreamingHttpClient, CustomStreamingServiceConfig, ReqwestCustomStreamingHttpClient,
};
use crate::grammar_correction::parse_grammar_correction;
use crate::mdx_native::{
    native_mdx_lookup_can_route, native_mdx_lookup_local_input_error,
    native_mdx_lookup_needs_credentials, run_native_mdx_lookup_with_factory,
    NativeMdxDictionaryReaderFactory, RsMdictReaderFactory,
};
use crate::openai_compatible::{
    build_openai_translation_request_plan, cleanup_openai_translation_text,
    correct_grammar_openai_compatible, execute_openai_stream_request,
    openai_compatible_service_can_route_natively, resolve_foundry_local_model_id_for_config,
    resolve_openai_compatible_config_for_service, translate_openai_compatible,
    validate_openai_translation_request_for_service, CommandFoundryLocalEndpointResolver,
    FoundryLocalEndpointResolver, OpenAiCompatibleConfig, OpenAiExecutionError, OpenAiHttpClient,
    OpenAiTranslationRequest, ReqwestOpenAiHttpClient,
};
use crate::retained_workers::RetainedWorkerPolicy;
use crate::settings_status::{open_vino_cache_status_for_settings, OpenVinoCacheStatus};
use crate::state::{
    settings_snapshot, stable_partition_demoted, ConnectionStatus, EasydictUiState,
    GrammarCorrectionPreview, Message, TranslationResultPreview,
};
use crate::traditional_http::{
    bing_host, traditional_http_config_for_request, translate_bing_service,
    translate_traditional_http_service, BingHttpClient, ReqwestBingHttpClient,
    ReqwestTraditionalHttpClient, TraditionalHttpClient, TraditionalHttpServiceConfig,
};
use crate::translation_cache::{
    Definition, Phonetic, Synonym, TranslationCacheRequest, TranslationMemoryCache,
    TranslationResult as CachedTranslationResult, TranslationResultKind, WordForm, WordResult,
};
use crate::translation_language::TranslationLanguage;
use crate::translation_services::find_translation_service_descriptor;
use futures_channel::mpsc::{unbounded, UnboundedReceiver, UnboundedSender};
use std::collections::HashSet;
use std::fmt;
use std::path::{Path, PathBuf};
use win_fluent::prelude::ResultStatus;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum QuickTranslateSurface {
    Main,
    Mini,
    Fixed,
}

#[derive(Clone, Debug, PartialEq)]
pub struct QuickTranslatePlan {
    pub query_id: u64,
    pub text: String,
    pub from: Option<String>,
    pub to: Option<String>,
    pub settings: SettingsSnapshot,
    pub language_resolution: QuickQueryLanguageResolution,
    pub services: Vec<QuickTranslateService>,
}

impl QuickTranslatePlan {
    pub fn params_for_service(&self, service: &QuickTranslateService) -> TranslateParams {
        TranslateParams {
            text: self.text.clone(),
            from: self.from.clone(),
            to: self.to.clone(),
            services: Some(vec![service.id.clone()]),
            custom_prompt: None,
        }
    }

    pub fn service_requests(&self) -> Vec<QuickTranslateServiceRequest> {
        self.services
            .iter()
            .cloned()
            .map(|service| QuickTranslateServiceRequest {
                query_id: self.query_id,
                params: self.params_for_service(&service),
                grammar_params: self.grammar_params_for_service(&service),
                settings: self.settings.clone(),
                query_mode: service_query_mode(self, &service),
                execution_kind: service_execution_kind(self, &service),
                service,
            })
            .collect()
    }

    pub fn grammar_params_for_service(
        &self,
        service: &QuickTranslateService,
    ) -> Option<GrammarCorrectParams> {
        (service_query_mode(self, service) == QuickQueryMode::GrammarCorrection).then(|| {
            GrammarCorrectParams {
                text: self.text.clone(),
                language: source_language_param(
                    &self.language_resolution.effective_source_language,
                ),
                services: Some(vec![service.id.clone()]),
                include_explanations: true,
            }
        })
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct QuickTranslateService {
    pub id: String,
    pub name: String,
    pub enabled_query: bool,
    pub grammar_capable: bool,
    pub streaming_capable: bool,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum QuickQueryMode {
    Translation,
    GrammarCorrection,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct QuickQueryLanguageResolution {
    pub selected_source_language: String,
    pub selected_target_language: String,
    pub effective_source_language: String,
    pub effective_target_language: String,
    pub effective_mode: QuickQueryMode,
    pub is_target_auto: bool,
    pub grammar_correction_requested: bool,
    pub grammar_correction_fallback: bool,
}

#[derive(Clone, Debug, PartialEq)]
pub struct QuickTranslateServiceRequest {
    pub query_id: u64,
    pub service: QuickTranslateService,
    pub query_mode: QuickQueryMode,
    pub execution_kind: QuickTranslateExecutionKind,
    pub params: TranslateParams,
    pub grammar_params: Option<GrammarCorrectParams>,
    pub settings: SettingsSnapshot,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum QuickTranslateExecutionKind {
    Translate,
    TranslateStream,
    GrammarCorrection,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum QuickTranslateStartError {
    EmptyText,
    NoEnabledServices,
}

impl fmt::Display for QuickTranslateStartError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::EmptyText => formatter.write_str("Text cannot be empty"),
            Self::NoEnabledServices => formatter.write_str("No translation services are enabled"),
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct QuickTranslateOutcome {
    pub query_id: u64,
    pub results: Vec<QuickTranslateServiceOutcome>,
}

impl QuickTranslateOutcome {
    pub fn all_failed(plan: &QuickTranslatePlan, message: impl Into<String>) -> Self {
        let message = message.into();
        Self {
            query_id: plan.query_id,
            results: plan
                .services
                .iter()
                .cloned()
                .map(|service| QuickTranslateServiceOutcome {
                    service,
                    grammar_result: None,
                    streamed_chunks: Vec::new(),
                    result: Err(QuickTranslateBackendError::new(message.clone())),
                })
                .collect(),
        }
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct QuickTranslateServiceOutcome {
    pub service: QuickTranslateService,
    pub grammar_result: Option<GrammarCorrectionPreview>,
    pub streamed_chunks: Vec<String>,
    pub result: Result<TranslationResultDto, QuickTranslateBackendError>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct QuickTranslateServiceUpdate {
    pub query_id: u64,
    pub outcome: QuickTranslateServiceOutcome,
}

#[derive(Clone, Debug, PartialEq)]
pub struct QuickTranslateStreamChunk {
    pub query_id: u64,
    pub service: QuickTranslateService,
    pub text: String,
}

#[derive(Clone, Debug, PartialEq)]
pub struct QuickTranslateStreamResult {
    pub result: TranslationResultDto,
    pub chunks: Vec<String>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct QuickTranslateBackendError {
    pub message: String,
}

impl QuickTranslateBackendError {
    pub fn new(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
        }
    }
}

impl fmt::Display for QuickTranslateBackendError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.message)
    }
}

impl From<WorkerClientError> for QuickTranslateBackendError {
    fn from(error: WorkerClientError) -> Self {
        Self::new(error.to_string())
    }
}

impl From<OpenAiExecutionError> for QuickTranslateBackendError {
    fn from(error: OpenAiExecutionError) -> Self {
        Self::new(error.to_string())
    }
}

pub fn translation_cache_request_for_quick_translate(
    request: &QuickTranslateServiceRequest,
) -> Option<TranslationCacheRequest> {
    if request.query_mode != QuickQueryMode::Translation
        || request.execution_kind != QuickTranslateExecutionKind::Translate
        || request.service.id.starts_with("mdx::")
    {
        return None;
    }

    if request
        .params
        .custom_prompt
        .as_deref()
        .is_some_and(|prompt| !prompt.trim().is_empty())
    {
        return None;
    }

    let from_language = request
        .params
        .from
        .as_deref()
        .map(TranslationLanguage::from_code)
        .unwrap_or(TranslationLanguage::Auto);
    let to_language_code = request.params.to.as_deref()?.trim();
    if is_auto_language(to_language_code) {
        return None;
    }

    let to_language = TranslationLanguage::from_code(to_language_code);
    if to_language == TranslationLanguage::Auto {
        return None;
    }

    Some(TranslationCacheRequest::new(
        request.service.id.clone(),
        from_language,
        to_language,
        request.params.text.clone(),
    ))
}

pub fn quick_translate_service_update_from_cache(
    request: &QuickTranslateServiceRequest,
    result: CachedTranslationResult,
) -> QuickTranslateServiceUpdate {
    QuickTranslateServiceUpdate {
        query_id: request.query_id,
        outcome: QuickTranslateServiceOutcome {
            service: request.service.clone(),
            grammar_result: None,
            streamed_chunks: Vec::new(),
            result: Ok(cached_translation_result_to_dto(&request.service, &result)),
        },
    }
}

pub fn store_quick_translate_cache_result(
    cache: &mut TranslationMemoryCache,
    cache_request: &TranslationCacheRequest,
    update: &QuickTranslateServiceUpdate,
) -> bool {
    if update.outcome.grammar_result.is_some() || !update.outcome.streamed_chunks.is_empty() {
        return false;
    }

    let Ok(result) = &update.outcome.result else {
        return false;
    };

    let Some(result) =
        cached_translation_result_from_dto(cache_request, &update.outcome.service, result)
    else {
        return false;
    };

    let detected_language = result.detected_language;
    cache.insert(cache_request, result.clone());

    if cache_request.from_language == TranslationLanguage::Auto
        && detected_language != TranslationLanguage::Auto
    {
        let mut detected_request = cache_request.clone();
        detected_request.from_language = detected_language;
        cache.insert(&detected_request, result);
    }

    true
}

fn cached_translation_result_from_dto(
    cache_request: &TranslationCacheRequest,
    service: &QuickTranslateService,
    result: &TranslationResultDto,
) -> Option<CachedTranslationResult> {
    let result_kind = cached_result_kind_from_dto(result.result_kind.as_deref());
    let detected_language = result
        .detected_language
        .as_deref()
        .map(TranslationLanguage::from_code)
        .filter(|language| *language != TranslationLanguage::Auto)
        .unwrap_or(cache_request.from_language);

    Some(CachedTranslationResult {
        translated_text: result.translated_text.clone(),
        original_text: cache_request.text.clone(),
        detected_language,
        target_language: cache_request.to_language,
        service_name: result
            .service_name
            .clone()
            .filter(|name| !name.trim().is_empty())
            .unwrap_or_else(|| service.name.clone()),
        result_kind,
        info_message: result.info_message.clone(),
        timing_ms: result.timing_ms.unwrap_or(0),
        from_cache: false,
        alternatives: result.alternatives.clone().unwrap_or_default(),
        word_result: result.word_result.as_ref().map(cached_word_result_from_dto),
        raw_html: None,
    })
}

fn cached_translation_result_to_dto(
    service: &QuickTranslateService,
    result: &CachedTranslationResult,
) -> TranslationResultDto {
    TranslationResultDto {
        translated_text: result.translated_text.clone(),
        service_id: Some(service.id.clone()),
        service_name: Some(if result.service_name.trim().is_empty() {
            service.name.clone()
        } else {
            result.service_name.clone()
        }),
        detected_language: Some(result.detected_language.to_code().to_string()),
        result_kind: Some(match result.result_kind {
            TranslationResultKind::Success => "Success".to_string(),
            TranslationResultKind::NoResult => "NoResult".to_string(),
        }),
        info_message: result.info_message.clone(),
        timing_ms: Some(result.timing_ms),
        alternatives: (!result.alternatives.is_empty()).then(|| result.alternatives.clone()),
        word_result: result.word_result.as_ref().map(cached_word_result_to_dto),
    }
}

fn cached_result_kind_from_dto(result_kind: Option<&str>) -> TranslationResultKind {
    let normalized = result_kind
        .unwrap_or("Success")
        .chars()
        .filter(|character| character.is_ascii_alphanumeric())
        .collect::<String>()
        .to_ascii_lowercase();

    if normalized == "noresult" {
        TranslationResultKind::NoResult
    } else {
        TranslationResultKind::Success
    }
}

fn cached_word_result_from_dto(result: &WordResultDto) -> WordResult {
    WordResult {
        phonetics: result
            .phonetics
            .clone()
            .unwrap_or_default()
            .into_iter()
            .map(cached_phonetic_from_dto)
            .collect(),
        definitions: result
            .definitions
            .clone()
            .unwrap_or_default()
            .into_iter()
            .map(cached_definition_from_dto)
            .collect(),
        examples: result.examples.clone().unwrap_or_default(),
        word_forms: result
            .word_forms
            .clone()
            .unwrap_or_default()
            .into_iter()
            .map(cached_word_form_from_dto)
            .collect(),
        synonyms: result
            .synonyms
            .clone()
            .unwrap_or_default()
            .into_iter()
            .map(cached_synonym_from_dto)
            .collect(),
    }
}

fn cached_word_result_to_dto(result: &WordResult) -> WordResultDto {
    WordResultDto {
        phonetics: (!result.phonetics.is_empty()).then(|| {
            result
                .phonetics
                .iter()
                .map(cached_phonetic_to_dto)
                .collect()
        }),
        definitions: (!result.definitions.is_empty()).then(|| {
            result
                .definitions
                .iter()
                .map(cached_definition_to_dto)
                .collect()
        }),
        examples: (!result.examples.is_empty()).then(|| result.examples.clone()),
        word_forms: (!result.word_forms.is_empty()).then(|| {
            result
                .word_forms
                .iter()
                .map(cached_word_form_to_dto)
                .collect()
        }),
        synonyms: (!result.synonyms.is_empty())
            .then(|| result.synonyms.iter().map(cached_synonym_to_dto).collect()),
    }
}

fn cached_phonetic_from_dto(value: PhoneticDto) -> Phonetic {
    Phonetic {
        text: value.text,
        audio_url: value.audio_url,
        accent: value.accent,
    }
}

fn cached_phonetic_to_dto(value: &Phonetic) -> PhoneticDto {
    PhoneticDto {
        text: value.text.clone(),
        audio_url: value.audio_url.clone(),
        accent: value.accent.clone(),
    }
}

fn cached_definition_from_dto(value: DefinitionDto) -> Definition {
    Definition {
        part_of_speech: value.part_of_speech,
        meanings: value.meanings.unwrap_or_default(),
    }
}

fn cached_definition_to_dto(value: &Definition) -> DefinitionDto {
    DefinitionDto {
        part_of_speech: value.part_of_speech.clone(),
        meanings: (!value.meanings.is_empty()).then(|| value.meanings.clone()),
    }
}

fn cached_word_form_from_dto(value: WordFormDto) -> WordForm {
    WordForm {
        name: value.name,
        value: value.value,
    }
}

fn cached_word_form_to_dto(value: &WordForm) -> WordFormDto {
    WordFormDto {
        name: value.name.clone(),
        value: value.value.clone(),
    }
}

fn cached_synonym_from_dto(value: SynonymDto) -> Synonym {
    Synonym {
        part_of_speech: value.part_of_speech,
        meaning: value.meaning,
        words: value.words.unwrap_or_default(),
    }
}

fn cached_synonym_to_dto(value: &Synonym) -> SynonymDto {
    SynonymDto {
        part_of_speech: value.part_of_speech.clone(),
        meaning: value.meaning.clone(),
        words: (!value.words.is_empty()).then(|| value.words.clone()),
    }
}

pub trait QuickTranslateBackend {
    fn configure(&mut self, settings: &SettingsSnapshot) -> Result<(), QuickTranslateBackendError> {
        let _ = settings;
        Ok(())
    }

    fn translate(
        &mut self,
        params: &TranslateParams,
    ) -> Result<TranslationResultDto, QuickTranslateBackendError>;

    fn translate_stream(
        &mut self,
        params: &TranslateParams,
    ) -> Result<QuickTranslateStreamResult, QuickTranslateBackendError>;

    fn correct_grammar(
        &mut self,
        params: &GrammarCorrectParams,
    ) -> Result<GrammarCorrectResultDto, QuickTranslateBackendError>;

    fn mdx_lookup(
        &mut self,
        params: &MdxLookupParams,
    ) -> Result<MdxLookupResult, QuickTranslateBackendError> {
        let _ = params;
        Err(QuickTranslateBackendError::new(
            "MDX lookup is not available in this backend",
        ))
    }
}

pub struct NativeMdxQuickTranslateBackend<F = RsMdictReaderFactory> {
    settings: Option<SettingsSnapshot>,
    reader_factory: F,
}

impl Default for NativeMdxQuickTranslateBackend<RsMdictReaderFactory> {
    fn default() -> Self {
        Self::new(RsMdictReaderFactory)
    }
}

impl<F> NativeMdxQuickTranslateBackend<F> {
    pub fn new(reader_factory: F) -> Self {
        Self {
            settings: None,
            reader_factory,
        }
    }

    pub fn reader_factory(&self) -> &F {
        &self.reader_factory
    }
}

impl<F: NativeMdxDictionaryReaderFactory> QuickTranslateBackend
    for NativeMdxQuickTranslateBackend<F>
{
    fn configure(&mut self, settings: &SettingsSnapshot) -> Result<(), QuickTranslateBackendError> {
        self.settings = Some(settings.clone());
        Ok(())
    }

    fn translate(
        &mut self,
        params: &TranslateParams,
    ) -> Result<TranslationResultDto, QuickTranslateBackendError> {
        let _ = params;
        Err(QuickTranslateBackendError::new(
            "MDX native backend only supports dictionary lookup",
        ))
    }

    fn translate_stream(
        &mut self,
        params: &TranslateParams,
    ) -> Result<QuickTranslateStreamResult, QuickTranslateBackendError> {
        let _ = params;
        Err(QuickTranslateBackendError::new(
            "MDX native backend does not support streaming translation",
        ))
    }

    fn correct_grammar(
        &mut self,
        params: &GrammarCorrectParams,
    ) -> Result<GrammarCorrectResultDto, QuickTranslateBackendError> {
        let _ = params;
        Err(QuickTranslateBackendError::new(
            "MDX native backend does not support grammar correction",
        ))
    }

    fn mdx_lookup(
        &mut self,
        params: &MdxLookupParams,
    ) -> Result<MdxLookupResult, QuickTranslateBackendError> {
        let settings = self.settings.as_ref().ok_or_else(|| {
            QuickTranslateBackendError::new("MDX native backend must be configured before use")
        })?;

        run_native_mdx_lookup_with_factory(&mut self.reader_factory, params, settings)
            .map_err(|error| QuickTranslateBackendError::new(error.to_string()))
    }
}

pub struct NativeOpenAiQuickTranslateBackend<C, R = CommandFoundryLocalEndpointResolver> {
    http_client: C,
    settings: Option<SettingsSnapshot>,
    foundry_local_endpoint_resolver: R,
}

impl<C> NativeOpenAiQuickTranslateBackend<C, CommandFoundryLocalEndpointResolver> {
    pub fn new(http_client: C) -> Self {
        Self {
            http_client,
            settings: None,
            foundry_local_endpoint_resolver: CommandFoundryLocalEndpointResolver::default(),
        }
    }
}

impl<C, R> NativeOpenAiQuickTranslateBackend<C, R> {
    pub fn with_foundry_local_endpoint_resolver(
        http_client: C,
        foundry_local_endpoint_resolver: R,
    ) -> Self {
        Self {
            http_client,
            settings: None,
            foundry_local_endpoint_resolver,
        }
    }

    pub fn http_client(&self) -> &C {
        &self.http_client
    }

    pub fn foundry_local_endpoint_resolver(&self) -> &R {
        &self.foundry_local_endpoint_resolver
    }

    pub fn into_http_client(self) -> C {
        self.http_client
    }
}

impl<C: OpenAiHttpClient, R: FoundryLocalEndpointResolver> QuickTranslateBackend
    for NativeOpenAiQuickTranslateBackend<C, R>
{
    fn configure(&mut self, settings: &SettingsSnapshot) -> Result<(), QuickTranslateBackendError> {
        self.settings = Some(settings.clone());
        Ok(())
    }

    fn translate(
        &mut self,
        params: &TranslateParams,
    ) -> Result<TranslationResultDto, QuickTranslateBackendError> {
        let request = self.openai_translation_request(params)?;
        let (service_id, service_name, config) = self.service_context(params)?;
        let config = self.resolve_foundry_local_model_if_needed(&service_id, config);
        translate_openai_compatible(
            &mut self.http_client,
            &config,
            &request,
            service_id,
            service_name,
        )
        .map_err(QuickTranslateBackendError::from)
    }

    fn translate_stream(
        &mut self,
        params: &TranslateParams,
    ) -> Result<QuickTranslateStreamResult, QuickTranslateBackendError> {
        let request = self.openai_translation_request(params)?;
        let (service_id, service_name, config) = self.service_context(params)?;
        let config = self.resolve_foundry_local_model_if_needed(&service_id, config);
        validate_openai_translation_request_for_service(&service_id, &request)
            .map_err(OpenAiExecutionError::from)
            .map_err(QuickTranslateBackendError::from)?;
        let plan = build_openai_translation_request_plan(&config, &request)
            .map_err(OpenAiExecutionError::from)?;
        let chunks = execute_openai_stream_request(&mut self.http_client, &plan)?;
        let translated_text = cleanup_openai_translation_text(&chunks.concat());

        Ok(QuickTranslateStreamResult {
            result: TranslationResultDto {
                translated_text,
                service_id: Some(service_id),
                service_name: Some(service_name),
                detected_language: Some(request.from_language.to_code().to_string()),
                result_kind: Some("Success".to_string()),
                info_message: None,
                timing_ms: None,
                alternatives: None,
                word_result: None,
            },
            chunks,
        })
    }

    fn correct_grammar(
        &mut self,
        params: &GrammarCorrectParams,
    ) -> Result<GrammarCorrectResultDto, QuickTranslateBackendError> {
        let (service_id, service_name, config) = self.service_context_for_ids(&params.services)?;
        let config = self.resolve_foundry_local_model_if_needed(&service_id, config);
        let language = params
            .language
            .as_deref()
            .map(TranslationLanguage::from_code)
            .unwrap_or(TranslationLanguage::Auto);

        correct_grammar_openai_compatible(
            &mut self.http_client,
            &config,
            language,
            &params.text,
            params.include_explanations,
            service_id,
            service_name,
        )
        .map_err(QuickTranslateBackendError::from)
    }
}

impl<C, R: FoundryLocalEndpointResolver> NativeOpenAiQuickTranslateBackend<C, R> {
    fn openai_translation_request(
        &self,
        params: &TranslateParams,
    ) -> Result<OpenAiTranslationRequest, QuickTranslateBackendError> {
        Ok(OpenAiTranslationRequest {
            text: params.text.clone(),
            from_language: params
                .from
                .as_deref()
                .map(TranslationLanguage::from_code)
                .unwrap_or(TranslationLanguage::Auto),
            to_language: params
                .to
                .as_deref()
                .map(TranslationLanguage::from_code)
                .unwrap_or(TranslationLanguage::English),
            custom_prompt: params.custom_prompt.clone(),
        })
    }

    fn service_context(
        &mut self,
        params: &TranslateParams,
    ) -> Result<(String, String, OpenAiCompatibleConfig), QuickTranslateBackendError> {
        self.service_context_for_ids(&params.services)
    }

    fn service_context_for_ids(
        &mut self,
        services: &Option<Vec<String>>,
    ) -> Result<(String, String, OpenAiCompatibleConfig), QuickTranslateBackendError> {
        let service_id = services
            .as_ref()
            .and_then(|services| services.first())
            .cloned()
            .ok_or_else(|| {
                QuickTranslateBackendError::new(
                    "OpenAI-compatible request must specify a service id",
                )
            })?;

        let settings = self.settings.as_ref().ok_or_else(|| {
            QuickTranslateBackendError::new(
                "OpenAI-compatible backend must be configured before use",
            )
        })?;

        let config = resolve_openai_compatible_config_for_service(
            &service_id,
            settings,
            &mut self.foundry_local_endpoint_resolver,
        )
        .map_err(QuickTranslateBackendError::from)?
        .ok_or_else(|| {
            QuickTranslateBackendError::new(format!(
                "Service '{service_id}' is not handled by the native OpenAI-compatible backend"
            ))
        })?;
        let service_name = native_openai_service_name(&service_id);
        Ok((service_id, service_name, config))
    }
}

impl<C: OpenAiHttpClient, R: FoundryLocalEndpointResolver> NativeOpenAiQuickTranslateBackend<C, R> {
    fn resolve_foundry_local_model_if_needed(
        &mut self,
        service_id: &str,
        config: OpenAiCompatibleConfig,
    ) -> OpenAiCompatibleConfig {
        if service_id == "windows-local-ai" {
            resolve_foundry_local_model_id_for_config(&mut self.http_client, &config)
        } else {
            config
        }
    }
}

pub struct NativeCustomStreamingQuickTranslateBackend<C> {
    http_client: C,
    settings: Option<SettingsSnapshot>,
}

impl<C> NativeCustomStreamingQuickTranslateBackend<C> {
    pub fn new(http_client: C) -> Self {
        Self {
            http_client,
            settings: None,
        }
    }

    pub fn http_client(&self) -> &C {
        &self.http_client
    }

    pub fn into_http_client(self) -> C {
        self.http_client
    }
}

impl<C: CustomStreamingHttpClient> QuickTranslateBackend
    for NativeCustomStreamingQuickTranslateBackend<C>
{
    fn configure(&mut self, settings: &SettingsSnapshot) -> Result<(), QuickTranslateBackendError> {
        self.settings = Some(settings.clone());
        Ok(())
    }

    fn translate(
        &mut self,
        params: &TranslateParams,
    ) -> Result<TranslationResultDto, QuickTranslateBackendError> {
        let request = self.openai_translation_request(params);
        let (service_id, service_name, config) = self.service_context(params)?;
        translate_custom_streaming_service(
            &mut self.http_client,
            &config,
            &request,
            service_id,
            service_name,
        )
        .map_err(QuickTranslateBackendError::from)
    }

    fn translate_stream(
        &mut self,
        params: &TranslateParams,
    ) -> Result<QuickTranslateStreamResult, QuickTranslateBackendError> {
        let request = self.openai_translation_request(params);
        let (service_id, service_name, config) = self.service_context(params)?;
        let plan = build_custom_streaming_translation_request_plan(&config, &request)?;
        let chunks = execute_custom_streaming_request(&mut self.http_client, &plan)?;
        let translated_text = cleanup_custom_streaming_translation_text(&config, &chunks.concat());

        Ok(QuickTranslateStreamResult {
            result: TranslationResultDto {
                translated_text,
                service_id: Some(service_id),
                service_name: Some(service_name),
                detected_language: Some(request.from_language.to_code().to_string()),
                result_kind: Some("Success".to_string()),
                info_message: None,
                timing_ms: None,
                alternatives: None,
                word_result: None,
            },
            chunks,
        })
    }

    fn correct_grammar(
        &mut self,
        params: &GrammarCorrectParams,
    ) -> Result<GrammarCorrectResultDto, QuickTranslateBackendError> {
        let (service_id, service_name, config) = self.service_context_for_ids(&params.services)?;
        let language = params
            .language
            .as_deref()
            .map(TranslationLanguage::from_code)
            .unwrap_or(TranslationLanguage::Auto);

        correct_custom_streaming_grammar(
            &mut self.http_client,
            &config,
            language,
            &params.text,
            params.include_explanations,
            service_id,
            service_name,
        )
        .map_err(QuickTranslateBackendError::from)
    }
}

impl<C> NativeCustomStreamingQuickTranslateBackend<C> {
    fn openai_translation_request(&self, params: &TranslateParams) -> OpenAiTranslationRequest {
        OpenAiTranslationRequest {
            text: params.text.clone(),
            from_language: params
                .from
                .as_deref()
                .map(TranslationLanguage::from_code)
                .unwrap_or(TranslationLanguage::Auto),
            to_language: params
                .to
                .as_deref()
                .map(TranslationLanguage::from_code)
                .unwrap_or(TranslationLanguage::English),
            custom_prompt: params.custom_prompt.clone(),
        }
    }

    fn service_context(
        &self,
        params: &TranslateParams,
    ) -> Result<(String, String, CustomStreamingServiceConfig), QuickTranslateBackendError> {
        self.service_context_for_ids(&params.services)
    }

    fn service_context_for_ids(
        &self,
        services: &Option<Vec<String>>,
    ) -> Result<(String, String, CustomStreamingServiceConfig), QuickTranslateBackendError> {
        let service_id = services
            .as_ref()
            .and_then(|services| services.first())
            .cloned()
            .ok_or_else(|| {
                QuickTranslateBackendError::new(
                    "Custom streaming request must specify a service id",
                )
            })?;

        let settings = self.settings.as_ref().ok_or_else(|| {
            QuickTranslateBackendError::new(
                "Custom streaming backend must be configured before use",
            )
        })?;

        let config =
            custom_streaming_config_for_service(&service_id, settings).ok_or_else(|| {
                QuickTranslateBackendError::new(format!(
                    "Service '{service_id}' is not handled by the native custom streaming backend"
                ))
            })?;
        let service_name = native_openai_service_name(&service_id);
        Ok((service_id, service_name, config))
    }
}

pub struct NativeTraditionalHttpQuickTranslateBackend<C> {
    http_client: C,
    settings: Option<SettingsSnapshot>,
}

impl<C> NativeTraditionalHttpQuickTranslateBackend<C> {
    pub fn new(http_client: C) -> Self {
        Self {
            http_client,
            settings: None,
        }
    }

    pub fn http_client(&self) -> &C {
        &self.http_client
    }

    pub fn into_http_client(self) -> C {
        self.http_client
    }
}

impl<C: TraditionalHttpClient> QuickTranslateBackend
    for NativeTraditionalHttpQuickTranslateBackend<C>
{
    fn configure(&mut self, settings: &SettingsSnapshot) -> Result<(), QuickTranslateBackendError> {
        self.settings = Some(settings.clone());
        Ok(())
    }

    fn translate(
        &mut self,
        params: &TranslateParams,
    ) -> Result<TranslationResultDto, QuickTranslateBackendError> {
        let (service_id, service_name, config) = self.service_context(params)?;
        translate_traditional_http_service(
            &mut self.http_client,
            &config,
            &params.text,
            params
                .from
                .as_deref()
                .map(TranslationLanguage::from_code)
                .unwrap_or(TranslationLanguage::Auto),
            params
                .to
                .as_deref()
                .map(TranslationLanguage::from_code)
                .unwrap_or(TranslationLanguage::English),
            service_id,
            service_name,
        )
        .map_err(QuickTranslateBackendError::from)
    }

    fn translate_stream(
        &mut self,
        params: &TranslateParams,
    ) -> Result<QuickTranslateStreamResult, QuickTranslateBackendError> {
        let result = self.translate(params)?;
        let chunks = (!result.translated_text.is_empty())
            .then(|| result.translated_text.clone())
            .into_iter()
            .collect();
        Ok(QuickTranslateStreamResult { result, chunks })
    }

    fn correct_grammar(
        &mut self,
        params: &GrammarCorrectParams,
    ) -> Result<GrammarCorrectResultDto, QuickTranslateBackendError> {
        let _ = params;
        Err(QuickTranslateBackendError::new(
            "Grammar correction is not available in this backend",
        ))
    }
}

impl<C> NativeTraditionalHttpQuickTranslateBackend<C> {
    fn service_context(
        &self,
        params: &TranslateParams,
    ) -> Result<(String, String, TraditionalHttpServiceConfig), QuickTranslateBackendError> {
        let service_id = params
            .services
            .as_ref()
            .and_then(|services| services.first())
            .cloned()
            .ok_or_else(|| {
                QuickTranslateBackendError::new(
                    "Traditional HTTP request must specify a service id",
                )
            })?;

        let settings = self.settings.as_ref().ok_or_else(|| {
            QuickTranslateBackendError::new(
                "Traditional HTTP backend must be configured before use",
            )
        })?;

        let config = traditional_http_config_for_request(&service_id, settings, &params.text)
            .ok_or_else(|| {
                QuickTranslateBackendError::new(format!(
                    "Service '{service_id}' is not handled by the native traditional HTTP backend"
                ))
            })?;
        let service_name = native_openai_service_name(&service_id);
        Ok((service_id, service_name, config))
    }
}

/// Native backend for Bing's stateful two-phase free web flow (fetch session
/// credentials from the translator page, then translate).
pub struct NativeBingQuickTranslateBackend<C> {
    http_client: C,
    settings: Option<SettingsSnapshot>,
}

impl<C> NativeBingQuickTranslateBackend<C> {
    pub fn new(http_client: C) -> Self {
        Self {
            http_client,
            settings: None,
        }
    }

    pub fn http_client(&self) -> &C {
        &self.http_client
    }
}

impl<C: BingHttpClient> QuickTranslateBackend for NativeBingQuickTranslateBackend<C> {
    fn configure(&mut self, settings: &SettingsSnapshot) -> Result<(), QuickTranslateBackendError> {
        self.settings = Some(settings.clone());
        Ok(())
    }

    fn translate(
        &mut self,
        params: &TranslateParams,
    ) -> Result<TranslationResultDto, QuickTranslateBackendError> {
        let enable_international_services = self
            .settings
            .as_ref()
            .and_then(|settings| settings.enable_international_services)
            .unwrap_or(true);
        let host = bing_host(!enable_international_services);

        translate_bing_service(
            &mut self.http_client,
            host,
            &params.text,
            params
                .from
                .as_deref()
                .map(TranslationLanguage::from_code)
                .unwrap_or(TranslationLanguage::Auto),
            params
                .to
                .as_deref()
                .map(TranslationLanguage::from_code)
                .unwrap_or(TranslationLanguage::English),
            "bing",
            native_openai_service_name("bing"),
        )
        .map_err(QuickTranslateBackendError::from)
    }

    fn translate_stream(
        &mut self,
        params: &TranslateParams,
    ) -> Result<QuickTranslateStreamResult, QuickTranslateBackendError> {
        let result = self.translate(params)?;
        let chunks = (!result.translated_text.is_empty())
            .then(|| result.translated_text.clone())
            .into_iter()
            .collect();
        Ok(QuickTranslateStreamResult { result, chunks })
    }

    fn correct_grammar(
        &mut self,
        _params: &GrammarCorrectParams,
    ) -> Result<GrammarCorrectResultDto, QuickTranslateBackendError> {
        Err(QuickTranslateBackendError::new(
            "Grammar correction is not available in this backend",
        ))
    }
}

pub struct LocalAiWorkerQuickTranslateBackend {
    facade: DirectWorkerFacade,
    settings: Option<SettingsSnapshot>,
}

impl LocalAiWorkerQuickTranslateBackend {
    pub fn new(facade: DirectWorkerFacade) -> Self {
        Self {
            facade,
            settings: None,
        }
    }

    pub fn into_facade(self) -> DirectWorkerFacade {
        self.facade
    }

    fn settings(&self) -> Result<&SettingsSnapshot, QuickTranslateBackendError> {
        self.settings.as_ref().ok_or_else(|| {
            QuickTranslateBackendError::new("Local AI worker backend must be configured before use")
        })
    }
}

impl QuickTranslateBackend for LocalAiWorkerQuickTranslateBackend {
    fn configure(&mut self, settings: &SettingsSnapshot) -> Result<(), QuickTranslateBackendError> {
        DirectWorkerFacade::configure(
            &mut self.facade,
            &ConfigureParams {
                settings: settings.clone(),
            },
        )
        .map_err(|error| {
            QuickTranslateBackendError::new(error.process_message("Local AI worker"))
        })?;
        self.settings = Some(settings.clone());
        Ok(())
    }

    fn translate(
        &mut self,
        params: &TranslateParams,
    ) -> Result<TranslationResultDto, QuickTranslateBackendError> {
        self.translate_stream(params).map(|stream| stream.result)
    }

    fn translate_stream(
        &mut self,
        params: &TranslateParams,
    ) -> Result<QuickTranslateStreamResult, QuickTranslateBackendError> {
        let local_params = local_ai_params_from_translate_params(params, self.settings()?, None);
        let mut chunks = Vec::new();
        let result = self
            .facade
            .local_ai_translate_stream_observing_chunks(&local_params, |chunk| {
                chunks.push(chunk.text);
            })
            .map_err(|error| {
                QuickTranslateBackendError::new(error.process_message("Local AI worker"))
            })?;
        Ok(local_ai_stream_result_to_quick_translate_result(
            result, chunks,
        ))
    }

    fn correct_grammar(
        &mut self,
        params: &GrammarCorrectParams,
    ) -> Result<GrammarCorrectResultDto, QuickTranslateBackendError> {
        let local_params = local_ai_params_from_grammar_params(params, self.settings()?);
        let mut chunks = Vec::new();
        let result = self
            .facade
            .local_ai_grammar_stream_observing_chunks(&local_params, |chunk| {
                chunks.push(chunk.text);
            })
            .map_err(|error| {
                QuickTranslateBackendError::new(error.process_message("Local AI worker"))
            })?;
        Ok(local_ai_grammar_stream_result_to_grammar_result(
            params, result, chunks,
        ))
    }
}

pub fn begin_quick_translate(
    state: &mut EasydictUiState,
) -> Result<QuickTranslatePlan, QuickTranslateStartError> {
    begin_quick_translate_for_surface(state, QuickTranslateSurface::Main)
}

pub fn begin_quick_translate_for_surface(
    state: &mut EasydictUiState,
    surface: QuickTranslateSurface,
) -> Result<QuickTranslatePlan, QuickTranslateStartError> {
    let plan = build_quick_translate_plan_for_surface(state, state.next_query_id, surface)?;
    state.next_query_id = state.next_query_id.saturating_add(1);
    mark_quick_translate_started(state, surface, &plan);
    Ok(plan)
}

pub fn begin_manual_quick_translate_service(
    state: &mut EasydictUiState,
    service_id: &str,
) -> Result<Option<QuickTranslateServiceRequest>, QuickTranslateStartError> {
    begin_manual_quick_translate_service_for_surface(state, QuickTranslateSurface::Main, service_id)
}

pub fn begin_manual_quick_translate_service_for_surface(
    state: &mut EasydictUiState,
    surface: QuickTranslateSurface,
    service_id: &str,
) -> Result<Option<QuickTranslateServiceRequest>, QuickTranslateStartError> {
    let Some(service) = state
        .surface_results(surface)
        .iter()
        .find(|result| result.id == service_id)
        .filter(|result| !result.demoted && !result.enabled_query && !result.has_queried)
        .map(|result| service_from_result(result, false))
    else {
        return Ok(None);
    };

    let plan = build_quick_translate_plan_for_services(
        state,
        state.next_query_id,
        surface,
        vec![service],
    )?;
    state.next_query_id = state.next_query_id.saturating_add(1);
    mark_quick_translate_started(state, surface, &plan);
    Ok(plan.service_requests().into_iter().next())
}

pub fn begin_retry_quick_translate_service_for_surface(
    state: &mut EasydictUiState,
    surface: QuickTranslateSurface,
    service_id: &str,
) -> Result<Option<QuickTranslateServiceRequest>, QuickTranslateStartError> {
    let Some(service) = state
        .surface_results(surface)
        .iter()
        .find(|result| result.id == service_id)
        .filter(|result| !result.demoted && result.has_queried)
        .map(|result| service_from_result(result, result.enabled_query))
    else {
        return Ok(None);
    };

    let plan = build_quick_translate_plan_for_services(
        state,
        state.next_query_id,
        surface,
        vec![service],
    )?;
    state.next_query_id = state.next_query_id.saturating_add(1);
    mark_quick_translate_started(state, surface, &plan);
    Ok(plan.service_requests().into_iter().next())
}

pub fn build_quick_translate_plan(
    state: &EasydictUiState,
    query_id: u64,
) -> Result<QuickTranslatePlan, QuickTranslateStartError> {
    build_quick_translate_plan_for_surface(state, query_id, QuickTranslateSurface::Main)
}

pub fn build_quick_translate_plan_for_surface(
    state: &EasydictUiState,
    query_id: u64,
    surface: QuickTranslateSurface,
) -> Result<QuickTranslatePlan, QuickTranslateStartError> {
    build_quick_translate_plan_for_services(
        state,
        query_id,
        surface,
        enabled_services(state.surface_results(surface)),
    )
}

fn build_quick_translate_plan_for_services(
    state: &EasydictUiState,
    query_id: u64,
    surface: QuickTranslateSurface,
    services: Vec<QuickTranslateService>,
) -> Result<QuickTranslatePlan, QuickTranslateStartError> {
    let query_state = state.surface_query_state(surface);
    let text = query_state.text.trim().to_string();
    if text.is_empty() {
        return Err(QuickTranslateStartError::EmptyText);
    }

    if services.is_empty() {
        return Err(QuickTranslateStartError::NoEnabledServices);
    }

    let grammar_available = services.iter().any(|service| service.grammar_capable);
    let effective_source = effective_source_language(&query_state);
    let language_resolution = resolve_quick_query_language(
        query_state.source_language,
        selected_target_language(&query_state, state),
        &effective_source,
        grammar_available,
        &state.settings.first_language,
        &state.settings.second_language,
    );

    Ok(QuickTranslatePlan {
        query_id,
        text,
        from: source_language_param(&language_resolution.effective_source_language),
        to: language_param(&language_resolution.effective_target_language),
        settings: settings_snapshot(&state.settings),
        language_resolution,
        services,
    })
}

pub fn resolve_quick_query_language(
    selected_source: &str,
    selected_target: &str,
    effective_source: &str,
    grammar_correction_available: bool,
    first_language: &str,
    second_language: &str,
) -> QuickQueryLanguageResolution {
    let selected_source = normalize_language_code(selected_source);
    let selected_target = normalize_language_code(selected_target);
    let effective_source = normalize_language_code(effective_source);
    let is_target_auto = is_auto_language(&selected_target);

    let mut target = if is_target_auto {
        resolve_auto_target_language(&effective_source, first_language, second_language)
    } else {
        selected_target.clone()
    };

    let grammar_requested =
        !is_auto_language(&effective_source) && !is_target_auto && target == effective_source;

    if grammar_requested && grammar_correction_available {
        return QuickQueryLanguageResolution {
            selected_source_language: selected_source,
            selected_target_language: selected_target,
            effective_source_language: effective_source.clone(),
            effective_target_language: effective_source,
            effective_mode: QuickQueryMode::GrammarCorrection,
            is_target_auto,
            grammar_correction_requested: true,
            grammar_correction_fallback: false,
        };
    }

    let mut grammar_fallback = false;
    if grammar_requested {
        target =
            resolve_different_target_language(&effective_source, first_language, second_language);
        grammar_fallback = !is_auto_language(&target) && target != effective_source;
    }

    QuickQueryLanguageResolution {
        selected_source_language: selected_source,
        selected_target_language: selected_target,
        effective_source_language: effective_source,
        effective_target_language: target,
        effective_mode: QuickQueryMode::Translation,
        is_target_auto,
        grammar_correction_requested: grammar_requested,
        grammar_correction_fallback: grammar_fallback,
    }
}

pub fn resolve_auto_target_language(
    source: &str,
    first_language: &str,
    second_language: &str,
) -> String {
    let source = normalize_language_code(source);
    let first_language = normalize_language_code(first_language);
    let second_language = normalize_language_code(second_language);

    let mut target = first_language.clone();
    if source == first_language {
        target = second_language;
    }

    if target == source {
        target = resolve_different_target_language(&source, &first_language, &target);
    }

    target
}

pub fn resolve_different_target_language(
    source: &str,
    first_language: &str,
    second_language: &str,
) -> String {
    let source = normalize_language_code(source);
    let first_language = normalize_language_code(first_language);
    let second_language = normalize_language_code(second_language);

    if source != first_language && !is_auto_language(&first_language) {
        return first_language;
    }

    if source != second_language && !is_auto_language(&second_language) {
        return second_language;
    }

    for language in SELECTABLE_LANGUAGE_CODES {
        let language = normalize_language_code(language);
        if language != source {
            return language;
        }
    }

    if source != "en" {
        return "en".to_string();
    }

    if source != "zh-Hans" {
        return "zh-Hans".to_string();
    }

    "auto".to_string()
}

pub fn run_quick_translate<B: QuickTranslateBackend>(
    backend: &mut B,
    plan: &QuickTranslatePlan,
) -> QuickTranslateOutcome {
    QuickTranslateOutcome {
        query_id: plan.query_id,
        results: plan
            .service_requests()
            .into_iter()
            .map(|request| run_quick_translate_service(backend, &request).outcome)
            .collect(),
    }
}

pub fn run_quick_translate_service<B: QuickTranslateBackend>(
    backend: &mut B,
    request: &QuickTranslateServiceRequest,
) -> QuickTranslateServiceUpdate {
    QuickTranslateServiceUpdate {
        query_id: request.query_id,
        outcome: run_service_request(backend, request),
    }
}

pub fn run_quick_translate_with_current_app_dir(plan: QuickTranslatePlan) -> QuickTranslateOutcome {
    match current_app_dir() {
        Ok(app_dir) => run_quick_translate_with_packaged_app_dir(plan, app_dir),
        Err(message) => QuickTranslateOutcome::all_failed(&plan, message),
    }
}

pub fn run_quick_translate_with_packaged_app_dir(
    plan: QuickTranslatePlan,
    app_dir: impl AsRef<Path>,
) -> QuickTranslateOutcome {
    let app_dir = app_dir.as_ref().to_path_buf();
    QuickTranslateOutcome {
        query_id: plan.query_id,
        results: plan
            .service_requests()
            .into_iter()
            .map(|request| {
                run_quick_translate_service_with_packaged_app_dir(request, &app_dir).outcome
            })
            .collect(),
    }
}

pub fn run_quick_translate_service_with_current_app_dir(
    request: QuickTranslateServiceRequest,
) -> QuickTranslateServiceUpdate {
    match current_app_dir() {
        Ok(app_dir) => run_quick_translate_service_with_packaged_app_dir(request, app_dir),
        Err(message) => service_error_update(request, message),
    }
}

pub fn run_quick_translate_service_with_packaged_app_dir(
    request: QuickTranslateServiceRequest,
    app_dir: impl AsRef<Path>,
) -> QuickTranslateServiceUpdate {
    run_quick_translate_service_with_packaged_app_dir_and_worker_policy(
        request,
        app_dir,
        RetainedWorkerPolicy::from_environment(),
    )
}

pub fn run_quick_translate_service_with_packaged_app_dir_and_worker_policy(
    request: QuickTranslateServiceRequest,
    app_dir: impl AsRef<Path>,
    worker_policy: RetainedWorkerPolicy,
) -> QuickTranslateServiceUpdate {
    if let Some(error) = local_ai_quick_translate_local_error(&request, worker_policy) {
        return service_error_update(request, error);
    }

    if quick_translate_request_can_route_natively(&request) {
        return run_quick_translate_service_with_native_route(request)
            .expect("native route was checked before dispatch");
    }

    let mut foundry_resolver = CommandFoundryLocalEndpointResolver::default();
    if let Some(native_request) =
        auto_foundry_local_native_probe_request(&request, &mut foundry_resolver)
    {
        return run_quick_translate_service_with_native_openai(native_request);
    }

    if request_uses_local_ai_worker_bridge(&request) {
        return run_quick_translate_service_with_local_ai_bridge(request, app_dir);
    }

    unsupported_rust_native_route_update(request)
}

pub fn run_quick_translate_streaming_service_with_current_app_dir(
    request: QuickTranslateServiceRequest,
) -> UnboundedReceiver<Message> {
    let (sender, receiver) = unbounded();

    std::thread::spawn(move || {
        let update = match current_app_dir() {
            Ok(app_dir) => run_quick_translate_streaming_service_with_packaged_app_dir(
                request, app_dir, &sender,
            ),
            Err(message) => service_error_update(request, message),
        };

        let _ = sender.unbounded_send(Message::QuickTranslateServiceFinished(update));
    });

    receiver
}

fn run_quick_translate_streaming_service_with_packaged_app_dir(
    request: QuickTranslateServiceRequest,
    app_dir: impl AsRef<Path>,
    sender: &UnboundedSender<Message>,
) -> QuickTranslateServiceUpdate {
    if let Some(error) =
        local_ai_quick_translate_local_error(&request, RetainedWorkerPolicy::from_environment())
    {
        return service_error_update(request, error);
    }

    if quick_translate_request_can_route_natively(&request) {
        return run_quick_translate_streaming_service_with_native_route(request, sender)
            .expect("native route was checked before dispatch");
    }

    let mut foundry_resolver = CommandFoundryLocalEndpointResolver::default();
    if let Some(native_request) =
        auto_foundry_local_native_probe_request(&request, &mut foundry_resolver)
    {
        return run_quick_translate_streaming_service_with_native_openai(native_request, sender);
    }

    if request_uses_local_ai_worker_bridge(&request) {
        return run_quick_translate_streaming_service_with_local_ai_bridge(
            request, app_dir, sender,
        );
    }

    unsupported_rust_native_route_update(request)
}

pub fn quick_translate_request_can_route_natively(request: &QuickTranslateServiceRequest) -> bool {
    request_uses_native_openai(request)
        || request_uses_native_custom_streaming(request)
        || request_uses_native_traditional_http(request)
        || request_uses_native_bing(request)
        || request_uses_native_mdx(request)
}

pub fn auto_foundry_local_native_probe_request<R: FoundryLocalEndpointResolver>(
    request: &QuickTranslateServiceRequest,
    foundry_local_endpoint_resolver: &mut R,
) -> Option<QuickTranslateServiceRequest> {
    if !request_should_probe_auto_foundry_local(request) {
        return None;
    }

    let endpoint = foundry_local_endpoint_resolver
        .resolve_chat_completions_endpoint()
        .ok()
        .flatten()?;

    let mut native_request = request.clone();
    native_request.settings.foundry_local_endpoint = Some(endpoint);
    Some(native_request)
}

pub fn run_quick_translate_service_with_native_route(
    request: QuickTranslateServiceRequest,
) -> Option<QuickTranslateServiceUpdate> {
    if request_uses_native_openai(&request) {
        return Some(run_quick_translate_service_with_native_openai(request));
    }

    if request_uses_native_custom_streaming(&request) {
        return Some(run_quick_translate_service_with_native_custom_streaming(
            request,
        ));
    }

    if request_uses_native_traditional_http(&request) {
        return Some(run_quick_translate_service_with_native_traditional_http(
            request,
        ));
    }

    if request_uses_native_bing(&request) {
        return Some(run_quick_translate_service_with_native_bing(request));
    }

    if request_uses_native_mdx(&request) {
        return Some(run_quick_translate_service_with_native_mdx(request));
    }

    None
}

pub fn run_quick_translate_streaming_service_with_native_route(
    request: QuickTranslateServiceRequest,
    sender: &UnboundedSender<Message>,
) -> Option<QuickTranslateServiceUpdate> {
    if request_uses_native_openai(&request) {
        return Some(run_quick_translate_streaming_service_with_native_openai(
            request, sender,
        ));
    }

    if request_uses_native_custom_streaming(&request) {
        return Some(
            run_quick_translate_streaming_service_with_native_custom_streaming(request, sender),
        );
    }

    if request_uses_native_traditional_http(&request) {
        return Some(
            run_quick_translate_streaming_service_with_native_traditional_http(request, sender),
        );
    }

    if request_uses_native_bing(&request) {
        return Some(run_quick_translate_streaming_service_with_native_bing(
            request, sender,
        ));
    }

    if request_uses_native_mdx(&request) {
        return Some(run_quick_translate_service_with_native_mdx(request));
    }

    None
}

fn run_quick_translate_service_with_native_openai(
    request: QuickTranslateServiceRequest,
) -> QuickTranslateServiceUpdate {
    let mut backend = match ReqwestOpenAiHttpClient::from_settings(&request.settings) {
        Ok(client) => NativeOpenAiQuickTranslateBackend::new(client),
        Err(error) => return service_error_update(request, error.to_string()),
    };

    run_quick_translate_service(&mut backend, &request)
}

fn run_quick_translate_streaming_service_with_native_openai(
    request: QuickTranslateServiceRequest,
    sender: &UnboundedSender<Message>,
) -> QuickTranslateServiceUpdate {
    let mut backend = match ReqwestOpenAiHttpClient::from_settings(&request.settings) {
        Ok(client) => NativeOpenAiQuickTranslateBackend::new(client),
        Err(error) => return service_error_update(request, error.to_string()),
    };

    if let Err(error) = QuickTranslateBackend::configure(&mut backend, &request.settings) {
        return service_error_update(request, error.to_string());
    }

    if request.execution_kind != QuickTranslateExecutionKind::TranslateStream {
        return run_quick_translate_service(&mut backend, &request);
    }

    let query_id = request.query_id;
    let service = request.service.clone();
    match backend.translate_stream(&request.params) {
        Ok(streamed) => {
            for chunk in &streamed.chunks {
                let _ = sender.unbounded_send(Message::QuickTranslateStreamChunk(
                    QuickTranslateStreamChunk {
                        query_id,
                        service: service.clone(),
                        text: chunk.clone(),
                    },
                ));
            }

            QuickTranslateServiceUpdate {
                query_id,
                outcome: QuickTranslateServiceOutcome {
                    service: request.service,
                    grammar_result: None,
                    streamed_chunks: streamed.chunks,
                    result: Ok(streamed.result),
                },
            }
        }
        Err(error) => service_error_update(request, error.to_string()),
    }
}

fn run_quick_translate_service_with_native_custom_streaming(
    request: QuickTranslateServiceRequest,
) -> QuickTranslateServiceUpdate {
    let mut backend = match ReqwestCustomStreamingHttpClient::from_settings(&request.settings) {
        Ok(client) => NativeCustomStreamingQuickTranslateBackend::new(client),
        Err(error) => return service_error_update(request, error.to_string()),
    };

    run_quick_translate_service(&mut backend, &request)
}

fn run_quick_translate_streaming_service_with_native_custom_streaming(
    request: QuickTranslateServiceRequest,
    sender: &UnboundedSender<Message>,
) -> QuickTranslateServiceUpdate {
    let mut backend = match ReqwestCustomStreamingHttpClient::from_settings(&request.settings) {
        Ok(client) => NativeCustomStreamingQuickTranslateBackend::new(client),
        Err(error) => return service_error_update(request, error.to_string()),
    };

    if let Err(error) = QuickTranslateBackend::configure(&mut backend, &request.settings) {
        return service_error_update(request, error.to_string());
    }

    if request.execution_kind != QuickTranslateExecutionKind::TranslateStream {
        return run_quick_translate_service(&mut backend, &request);
    }

    let query_id = request.query_id;
    let service = request.service.clone();
    match backend.translate_stream(&request.params) {
        Ok(streamed) => {
            for chunk in &streamed.chunks {
                let _ = sender.unbounded_send(Message::QuickTranslateStreamChunk(
                    QuickTranslateStreamChunk {
                        query_id,
                        service: service.clone(),
                        text: chunk.clone(),
                    },
                ));
            }

            QuickTranslateServiceUpdate {
                query_id,
                outcome: QuickTranslateServiceOutcome {
                    service: request.service,
                    grammar_result: None,
                    streamed_chunks: streamed.chunks,
                    result: Ok(streamed.result),
                },
            }
        }
        Err(error) => service_error_update(request, error.to_string()),
    }
}

fn run_quick_translate_service_with_native_traditional_http(
    request: QuickTranslateServiceRequest,
) -> QuickTranslateServiceUpdate {
    let mut backend = match ReqwestTraditionalHttpClient::from_settings(&request.settings) {
        Ok(client) => NativeTraditionalHttpQuickTranslateBackend::new(client),
        Err(error) => return service_error_update(request, error.to_string()),
    };

    run_quick_translate_service(&mut backend, &request)
}

fn run_quick_translate_streaming_service_with_native_traditional_http(
    request: QuickTranslateServiceRequest,
    sender: &UnboundedSender<Message>,
) -> QuickTranslateServiceUpdate {
    let mut backend = match ReqwestTraditionalHttpClient::from_settings(&request.settings) {
        Ok(client) => NativeTraditionalHttpQuickTranslateBackend::new(client),
        Err(error) => return service_error_update(request, error.to_string()),
    };

    if let Err(error) = QuickTranslateBackend::configure(&mut backend, &request.settings) {
        return service_error_update(request, error.to_string());
    }

    if request.execution_kind != QuickTranslateExecutionKind::TranslateStream {
        return run_quick_translate_service(&mut backend, &request);
    }

    let query_id = request.query_id;
    let service = request.service.clone();
    match backend.translate_stream(&request.params) {
        Ok(streamed) => {
            for chunk in &streamed.chunks {
                let _ = sender.unbounded_send(Message::QuickTranslateStreamChunk(
                    QuickTranslateStreamChunk {
                        query_id,
                        service: service.clone(),
                        text: chunk.clone(),
                    },
                ));
            }

            QuickTranslateServiceUpdate {
                query_id,
                outcome: QuickTranslateServiceOutcome {
                    service: request.service,
                    grammar_result: None,
                    streamed_chunks: streamed.chunks,
                    result: Ok(streamed.result),
                },
            }
        }
        Err(error) => service_error_update(request, error.to_string()),
    }
}

fn run_quick_translate_service_with_native_bing(
    request: QuickTranslateServiceRequest,
) -> QuickTranslateServiceUpdate {
    let mut backend = match ReqwestBingHttpClient::from_settings(&request.settings) {
        Ok(client) => NativeBingQuickTranslateBackend::new(client),
        Err(error) => return service_error_update(request, error.to_string()),
    };

    run_quick_translate_service(&mut backend, &request)
}

fn run_quick_translate_service_with_native_mdx(
    request: QuickTranslateServiceRequest,
) -> QuickTranslateServiceUpdate {
    let mut backend = NativeMdxQuickTranslateBackend::default();
    run_quick_translate_service(&mut backend, &request)
}

fn run_quick_translate_service_with_local_ai_bridge(
    request: QuickTranslateServiceRequest,
    app_dir: impl AsRef<Path>,
) -> QuickTranslateServiceUpdate {
    match DirectWorkerFacade::spawn_packaged_local_ai(app_dir) {
        Ok(facade) => {
            let mut backend = LocalAiWorkerQuickTranslateBackend::new(facade);
            run_quick_translate_service(&mut backend, &request)
        }
        Err(error) => service_error_update(request, error.process_message("Local AI worker")),
    }
}

fn run_quick_translate_streaming_service_with_local_ai_bridge(
    request: QuickTranslateServiceRequest,
    app_dir: impl AsRef<Path>,
    sender: &UnboundedSender<Message>,
) -> QuickTranslateServiceUpdate {
    let mut backend = match DirectWorkerFacade::spawn_packaged_local_ai(app_dir) {
        Ok(facade) => LocalAiWorkerQuickTranslateBackend::new(facade),
        Err(error) => {
            return service_error_update(request, error.process_message("Local AI worker"));
        }
    };

    if let Err(error) = QuickTranslateBackend::configure(&mut backend, &request.settings) {
        return service_error_update(request, error.to_string());
    }

    if request.execution_kind != QuickTranslateExecutionKind::TranslateStream {
        return run_quick_translate_service(&mut backend, &request);
    }

    let query_id = request.query_id;
    let service = request.service.clone();
    match backend.translate_stream(&request.params) {
        Ok(streamed) => {
            for chunk in &streamed.chunks {
                let _ = sender.unbounded_send(Message::QuickTranslateStreamChunk(
                    QuickTranslateStreamChunk {
                        query_id,
                        service: service.clone(),
                        text: chunk.clone(),
                    },
                ));
            }

            QuickTranslateServiceUpdate {
                query_id,
                outcome: QuickTranslateServiceOutcome {
                    service: request.service,
                    grammar_result: None,
                    streamed_chunks: streamed.chunks,
                    result: Ok(streamed.result),
                },
            }
        }
        Err(error) => service_error_update(request, error.to_string()),
    }
}

fn run_quick_translate_streaming_service_with_native_bing(
    request: QuickTranslateServiceRequest,
    sender: &UnboundedSender<Message>,
) -> QuickTranslateServiceUpdate {
    let mut backend = match ReqwestBingHttpClient::from_settings(&request.settings) {
        Ok(client) => NativeBingQuickTranslateBackend::new(client),
        Err(error) => return service_error_update(request, error.to_string()),
    };

    if let Err(error) = QuickTranslateBackend::configure(&mut backend, &request.settings) {
        return service_error_update(request, error.to_string());
    }

    if request.execution_kind != QuickTranslateExecutionKind::TranslateStream {
        return run_quick_translate_service(&mut backend, &request);
    }

    let query_id = request.query_id;
    let service = request.service.clone();
    match backend.translate_stream(&request.params) {
        Ok(streamed) => {
            for chunk in &streamed.chunks {
                let _ = sender.unbounded_send(Message::QuickTranslateStreamChunk(
                    QuickTranslateStreamChunk {
                        query_id,
                        service: service.clone(),
                        text: chunk.clone(),
                    },
                ));
            }

            QuickTranslateServiceUpdate {
                query_id,
                outcome: QuickTranslateServiceOutcome {
                    service: request.service,
                    grammar_result: None,
                    streamed_chunks: streamed.chunks,
                    result: Ok(streamed.result),
                },
            }
        }
        Err(error) => service_error_update(request, error.to_string()),
    }
}

fn request_uses_native_bing(request: &QuickTranslateServiceRequest) -> bool {
    request.service.id == "bing"
}

fn request_uses_native_mdx(request: &QuickTranslateServiceRequest) -> bool {
    request.execution_kind == QuickTranslateExecutionKind::Translate
        && request.service.id.starts_with("mdx::")
        && native_mdx_lookup_can_route(
            &MdxLookupParams {
                dictionary_id: request.service.id.clone(),
                query: request.params.text.clone(),
                fuzzy: false,
            },
            &request.settings,
        )
}

fn request_uses_native_openai(request: &QuickTranslateServiceRequest) -> bool {
    openai_compatible_service_can_route_natively(&request.service.id, &request.settings)
}

fn request_should_probe_auto_foundry_local(request: &QuickTranslateServiceRequest) -> bool {
    if request.service.id != "windows-local-ai"
        || local_ai_provider_mode(&request.settings) != local_ai_provider_modes::AUTO
    {
        return false;
    }

    match request.settings.foundry_local_endpoint.as_deref() {
        Some(endpoint) if !endpoint.trim().is_empty() => return false,
        _ => {}
    }

    matches!(
        request.execution_kind,
        QuickTranslateExecutionKind::Translate
            | QuickTranslateExecutionKind::TranslateStream
            | QuickTranslateExecutionKind::GrammarCorrection
    )
}

fn request_uses_native_custom_streaming(request: &QuickTranslateServiceRequest) -> bool {
    custom_streaming_config_for_service(&request.service.id, &request.settings).is_some()
}

fn request_uses_native_traditional_http(request: &QuickTranslateServiceRequest) -> bool {
    traditional_http_config_for_request(
        &request.service.id,
        &request.settings,
        &request.params.text,
    )
    .is_some()
}

fn request_uses_local_ai_worker_bridge(request: &QuickTranslateServiceRequest) -> bool {
    request.service.id == "windows-local-ai" && !request_uses_native_openai(request)
}

pub fn local_ai_quick_translate_local_error(
    request: &QuickTranslateServiceRequest,
    worker_policy: RetainedWorkerPolicy,
) -> Option<&'static str> {
    if request.service.id != "windows-local-ai" {
        return None;
    }

    if matches!(
        request.execution_kind,
        QuickTranslateExecutionKind::Translate | QuickTranslateExecutionKind::TranslateStream
    ) {
        let provider_mode = local_ai_provider_mode(&request.settings);
        let to_language = dotnet_language_name_from_code(request.params.to.as_deref(), "English");
        if dotnet_language_name_is_auto(&to_language) {
            return Some("No local AI provider supports this language pair");
        }

        if provider_mode != local_ai_provider_modes::OPENVINO {
            return request_uses_local_ai_worker_bridge(request)
                .then(|| worker_policy.local_ai_worker_disabled_message())
                .flatten();
        }

        let Some(from_language) =
            strict_dotnet_language_name_from_code(request.params.from.as_deref(), "Auto")
        else {
            return Some("No local AI provider supports this language pair");
        };
        let Some(to_language) =
            strict_dotnet_language_name_from_code(request.params.to.as_deref(), "English")
        else {
            return Some("No local AI provider supports this language pair");
        };
        if !openvino_supports_nllb_language_pair(&from_language, &to_language) {
            return Some("No local AI provider supports this language pair");
        }

        if open_vino_cache_status_for_settings(&request.settings) != OpenVinoCacheStatus::Ready {
            return Some(
                "OpenVINO runtime or NLLB-200 model is not downloaded. Open Settings -> Services and click \"Download model\".",
            );
        }

        return request_uses_local_ai_worker_bridge(request)
            .then(|| worker_policy.local_ai_worker_disabled_message())
            .flatten();
    }

    if request.execution_kind == QuickTranslateExecutionKind::GrammarCorrection
        && request.grammar_params.is_some()
        && local_ai_provider_mode(&request.settings) == local_ai_provider_modes::OPENVINO
    {
        return Some("No local AI provider supports grammar correction for this language");
    }

    request_uses_local_ai_worker_bridge(request)
        .then(|| worker_policy.local_ai_worker_disabled_message())
        .flatten()
}

fn openvino_supports_nllb_language_pair(from_language: &str, to_language: &str) -> bool {
    if dotnet_language_name_is_auto(to_language) || !openvino_supports_nllb_language(to_language) {
        return false;
    }

    dotnet_language_name_is_auto(from_language) || openvino_supports_nllb_language(from_language)
}

fn dotnet_language_name_is_auto(language: &str) -> bool {
    language.trim().eq_ignore_ascii_case("Auto")
}

fn openvino_supports_nllb_language(language: &str) -> bool {
    matches!(
        language.trim().to_ascii_lowercase().as_str(),
        "simplifiedchinese"
            | "chinesesimplified"
            | "traditionalchinese"
            | "chinesetraditional"
            | "classicalchinese"
            | "chineseclassical"
            | "japanese"
            | "korean"
            | "english"
            | "german"
            | "dutch"
            | "swedish"
            | "norwegian"
            | "danish"
            | "french"
            | "spanish"
            | "portuguese"
            | "italian"
            | "romanian"
            | "russian"
            | "polish"
            | "czech"
            | "ukrainian"
            | "bulgarian"
            | "slovak"
            | "slovenian"
            | "estonian"
            | "latvian"
            | "lithuanian"
            | "greek"
            | "hungarian"
            | "finnish"
            | "turkish"
            | "arabic"
            | "persian"
            | "hebrew"
            | "hindi"
            | "bengali"
            | "tamil"
            | "telugu"
            | "urdu"
            | "vietnamese"
            | "thai"
            | "indonesian"
            | "malay"
            | "filipino"
    )
}

fn unsupported_rust_native_route_update(
    request: QuickTranslateServiceRequest,
) -> QuickTranslateServiceUpdate {
    if request.execution_kind == QuickTranslateExecutionKind::Translate
        && request.service.id.starts_with("mdx::")
    {
        let params = MdxLookupParams {
            dictionary_id: request.service.id.clone(),
            query: request.params.text.clone(),
            fuzzy: false,
        };

        if native_mdx_lookup_needs_credentials(&params, &request.settings) {
            return service_error_update(
                request,
                "MDX dictionary credentials are required before lookup",
            );
        }

        if let Some(error) = native_mdx_lookup_local_input_error(&params, &request.settings) {
            return service_error_update(request, error.to_string());
        }
    }

    let service_id = request.service.id.clone();
    service_error_update(
        request,
        format!("Service '{service_id}' is not supported by the Rust-native quick translate route"),
    )
}

fn native_openai_service_name(service_id: &str) -> String {
    find_translation_service_descriptor(service_id)
        .map(|descriptor| descriptor.display_name.to_string())
        .unwrap_or_else(|| service_id.to_string())
}

fn local_ai_params_from_translate_params(
    params: &TranslateParams,
    settings: &SettingsSnapshot,
    include_explanations: Option<bool>,
) -> LocalAiTranslateParams {
    LocalAiTranslateParams {
        text: params.text.clone(),
        from_language: dotnet_language_name_from_code(params.from.as_deref(), "Auto"),
        to_language: dotnet_language_name_from_code(params.to.as_deref(), "English"),
        provider_mode: local_ai_provider_mode(settings),
        custom_prompt: params.custom_prompt.clone(),
        include_explanations,
    }
}

fn local_ai_params_from_grammar_params(
    params: &GrammarCorrectParams,
    settings: &SettingsSnapshot,
) -> LocalAiTranslateParams {
    LocalAiTranslateParams {
        text: params.text.clone(),
        from_language: dotnet_language_name_from_code(params.language.as_deref(), "Auto"),
        to_language: "English".to_string(),
        provider_mode: local_ai_provider_mode(settings),
        custom_prompt: None,
        include_explanations: Some(params.include_explanations),
    }
}

fn local_ai_stream_result_to_quick_translate_result(
    result: TranslateStreamResult,
    chunks: Vec<String>,
) -> QuickTranslateStreamResult {
    let translated_text = result.full_text.unwrap_or_else(|| chunks.concat());
    QuickTranslateStreamResult {
        result: TranslationResultDto {
            translated_text,
            service_id: Some("windows-local-ai".to_string()),
            service_name: Some(native_openai_service_name("windows-local-ai")),
            detected_language: None,
            result_kind: Some("Success".to_string()),
            info_message: None,
            timing_ms: None,
            alternatives: None,
            word_result: None,
        },
        chunks,
    }
}

fn local_ai_grammar_stream_result_to_grammar_result(
    params: &GrammarCorrectParams,
    result: TranslateStreamResult,
    chunks: Vec<String>,
) -> GrammarCorrectResultDto {
    let raw_text = result.full_text.unwrap_or_else(|| chunks.concat());
    let service_name = native_openai_service_name("windows-local-ai");
    let parsed = parse_grammar_correction(&raw_text, &params.text, &service_name, 0);
    let has_corrections = parsed.has_corrections();

    GrammarCorrectResultDto {
        original_text: parsed.original_text,
        corrected_text: parsed.corrected_text,
        explanation: parsed.explanation,
        raw_text: Some(raw_text),
        service_id: Some("windows-local-ai".to_string()),
        service_name: Some(parsed.service_name),
        language: params
            .language
            .as_deref()
            .map(dotnet_language_code_from_name),
        timing_ms: Some(parsed.timing_ms),
        has_corrections,
    }
}

fn local_ai_provider_mode(settings: &SettingsSnapshot) -> String {
    match settings
        .local_ai_provider
        .as_deref()
        .unwrap_or(local_ai_provider_modes::AUTO)
        .trim()
        .to_ascii_lowercase()
        .as_str()
    {
        "windowsai" | "windows-ai" | "phi-silica" | "phisilica" => {
            local_ai_provider_modes::WINDOWS_AI.to_string()
        }
        "foundrylocal" | "foundry-local" => local_ai_provider_modes::FOUNDRY_LOCAL.to_string(),
        "openvino" | "open-vino" => local_ai_provider_modes::OPENVINO.to_string(),
        _ => local_ai_provider_modes::AUTO.to_string(),
    }
}

fn dotnet_language_name_from_code(code: Option<&str>, default_name: &str) -> String {
    strict_dotnet_language_name_from_code(code, default_name)
        .unwrap_or_else(|| default_name.to_string())
}

fn strict_dotnet_language_name_from_code(code: Option<&str>, default_name: &str) -> Option<String> {
    let Some(code) = code.map(str::trim).filter(|code| !code.is_empty()) else {
        return Some(default_name.to_string());
    };

    let normalized = code.to_ascii_lowercase();
    let primary_subtag = normalized
        .split_once('-')
        .map(|(primary, _)| primary)
        .unwrap_or(normalized.as_str());
    let language_name = match normalized.as_str() {
        "auto" => "Auto",
        "zh-cn" | "zh-hans" | "zh" | "simplifiedchinese" | "chinesesimplified" => {
            "SimplifiedChinese"
        }
        "zh-tw" | "zh-hant" | "traditionalchinese" | "chinesetraditional" => "TraditionalChinese",
        "zh-classical" | "classicalchinese" | "chineseclassical" => "ClassicalChinese",
        "english" => "English",
        "japanese" => "Japanese",
        "korean" => "Korean",
        "french" => "French",
        "spanish" => "Spanish",
        "portuguese" => "Portuguese",
        "italian" => "Italian",
        "german" => "German",
        "russian" => "Russian",
        "arabic" => "Arabic",
        "swedish" => "Swedish",
        "romanian" => "Romanian",
        "thai" => "Thai",
        "dutch" => "Dutch",
        "hungarian" => "Hungarian",
        "greek" => "Greek",
        "danish" => "Danish",
        "finnish" => "Finnish",
        "polish" => "Polish",
        "czech" => "Czech",
        "turkish" => "Turkish",
        "ukrainian" => "Ukrainian",
        "bulgarian" => "Bulgarian",
        "slovak" => "Slovak",
        "slovenian" => "Slovenian",
        "estonian" => "Estonian",
        "latvian" => "Latvian",
        "lithuanian" => "Lithuanian",
        "indonesian" => "Indonesian",
        "malay" => "Malay",
        "vietnamese" => "Vietnamese",
        "persian" => "Persian",
        "hindi" => "Hindi",
        "telugu" => "Telugu",
        "tamil" => "Tamil",
        "urdu" => "Urdu",
        "filipino" => "Filipino",
        "bengali" => "Bengali",
        "norwegian" => "Norwegian",
        "hebrew" => "Hebrew",
        _ => match primary_subtag {
            "en" => "English",
            "ja" => "Japanese",
            "ko" => "Korean",
            "fr" => "French",
            "es" => "Spanish",
            "pt" => "Portuguese",
            "it" => "Italian",
            "de" => "German",
            "ru" => "Russian",
            "ar" => "Arabic",
            "sv" => "Swedish",
            "ro" => "Romanian",
            "th" => "Thai",
            "nl" => "Dutch",
            "hu" => "Hungarian",
            "el" => "Greek",
            "da" => "Danish",
            "fi" => "Finnish",
            "pl" => "Polish",
            "cs" => "Czech",
            "tr" => "Turkish",
            "uk" => "Ukrainian",
            "bg" => "Bulgarian",
            "sk" => "Slovak",
            "sl" => "Slovenian",
            "et" => "Estonian",
            "lv" => "Latvian",
            "lt" => "Lithuanian",
            "id" => "Indonesian",
            "ms" => "Malay",
            "vi" => "Vietnamese",
            "fa" => "Persian",
            "hi" => "Hindi",
            "te" => "Telugu",
            "ta" => "Tamil",
            "ur" => "Urdu",
            "tl" | "fil" => "Filipino",
            "bn" => "Bengali",
            "no" | "nb" => "Norwegian",
            "he" | "iw" => "Hebrew",
            _ => return None,
        },
    };

    Some(language_name.to_string())
}

fn dotnet_language_code_from_name(name: &str) -> String {
    match name.trim().to_ascii_lowercase().as_str() {
        "auto" => "auto",
        "simplifiedchinese" | "chinesesimplified" | "zh-cn" | "zh-hans" | "zh" => "zh-CN",
        "traditionalchinese" | "chinesetraditional" | "zh-tw" | "zh-hant" => "zh-TW",
        "classicalchinese" | "chineseclassical" | "zh-classical" => "zh-classical",
        "english" | "en" => "en",
        "japanese" | "ja" => "ja",
        "korean" | "ko" => "ko",
        "french" | "fr" => "fr",
        "spanish" | "es" => "es",
        "portuguese" | "pt" => "pt",
        "italian" | "it" => "it",
        "german" | "de" => "de",
        "russian" | "ru" => "ru",
        "arabic" | "ar" => "ar",
        "swedish" | "sv" => "sv",
        "romanian" | "ro" => "ro",
        "thai" | "th" => "th",
        "dutch" | "nl" => "nl",
        "hungarian" | "hu" => "hu",
        "greek" | "el" => "el",
        "danish" | "da" => "da",
        "finnish" | "fi" => "fi",
        "polish" | "pl" => "pl",
        "czech" | "cs" => "cs",
        "turkish" | "tr" => "tr",
        "ukrainian" | "uk" => "uk",
        "bulgarian" | "bg" => "bg",
        "slovak" | "sk" => "sk",
        "slovenian" | "sl" => "sl",
        "estonian" | "et" => "et",
        "latvian" | "lv" => "lv",
        "lithuanian" | "lt" => "lt",
        "indonesian" | "id" => "id",
        "malay" | "ms" => "ms",
        "vietnamese" | "vi" => "vi",
        "persian" | "fa" => "fa",
        "hindi" | "hi" => "hi",
        "telugu" | "te" => "te",
        "tamil" | "ta" => "ta",
        "urdu" | "ur" => "ur",
        "filipino" | "tl" | "fil" => "tl",
        "bengali" | "bn" => "bn",
        "norwegian" | "no" | "nb" => "no",
        "hebrew" | "he" | "iw" => "he",
        other => other,
    }
    .to_string()
}

fn service_error_update(
    request: QuickTranslateServiceRequest,
    message: impl Into<String>,
) -> QuickTranslateServiceUpdate {
    QuickTranslateServiceUpdate {
        query_id: request.query_id,
        outcome: QuickTranslateServiceOutcome {
            service: request.service,
            grammar_result: None,
            streamed_chunks: Vec::new(),
            result: Err(QuickTranslateBackendError::new(message)),
        },
    }
}

pub fn apply_quick_translate_start_error(
    state: &mut EasydictUiState,
    error: QuickTranslateStartError,
) {
    apply_quick_translate_start_error_for_surface(state, QuickTranslateSurface::Main, error);
}

pub fn apply_quick_translate_start_error_for_surface(
    state: &mut EasydictUiState,
    surface: QuickTranslateSurface,
    error: QuickTranslateStartError,
) {
    let mut runtime = state.surface_runtime_mut(surface);
    *runtime.active_query_id = None;
    *runtime.active_query_service_count = 0;
    *runtime.active_query_success_count = 0;
    *runtime.is_translating = false;
    *runtime.status_text = error.to_string();
    *runtime.services_completed = 0;
    *runtime.current_quick_query_mode = QuickQueryMode::Translation;
    *runtime.grammar_correction_fallback = false;
    if let Some(connection_status) = runtime.connection_status.as_deref_mut() {
        *connection_status = ConnectionStatus::Error;
    }
}

pub fn apply_quick_translate_outcome(
    state: &mut EasydictUiState,
    outcome: QuickTranslateOutcome,
) -> bool {
    if state.active_surface_for_query(outcome.query_id).is_none() {
        return false;
    }

    for service_outcome in outcome.results {
        apply_quick_translate_service_update(
            state,
            QuickTranslateServiceUpdate {
                query_id: outcome.query_id,
                outcome: service_outcome,
            },
        );
    }

    true
}

pub fn apply_quick_translate_service_update(
    state: &mut EasydictUiState,
    update: QuickTranslateServiceUpdate,
) -> bool {
    let Some(surface) = state.active_surface_for_query(update.query_id) else {
        return false;
    };

    let hide_empty = state.settings.hide_empty_service_results;
    let runtime = state.surface_runtime_mut(surface);

    let service_id = update.outcome.service.id.as_str();
    let was_pending = service_is_pending(runtime.results, service_id);
    let detected_language = update
        .outcome
        .result
        .as_ref()
        .ok()
        .and_then(|result| {
            result
                .detected_language
                .as_deref()
                .filter(|value| !value.trim().is_empty())
        })
        .map(detected_language_label);
    let succeeded = update.outcome.result.is_ok();

    match update.outcome.result {
        Ok(result) => {
            apply_success(
                runtime.results,
                &update.outcome.service,
                result,
                update.outcome.grammar_result,
                update.outcome.streamed_chunks,
                hide_empty,
            );
        }
        Err(error) => {
            apply_error(runtime.results, &update.outcome.service, error);
        }
    }

    stable_partition_demoted(runtime.results);

    if was_pending {
        *runtime.services_completed = (*runtime.services_completed)
            .saturating_add(1)
            .min(*runtime.active_query_service_count);
        if succeeded {
            *runtime.active_query_success_count =
                (*runtime.active_query_success_count).saturating_add(1);
            if runtime.detected_language.is_none() {
                *runtime.detected_language = detected_language;
            }
        }
    }

    if *runtime.active_query_service_count > 0
        && *runtime.services_completed >= *runtime.active_query_service_count
    {
        finish_active_query(runtime);
    }

    true
}

pub fn apply_quick_translate_stream_chunk(
    state: &mut EasydictUiState,
    chunk: QuickTranslateStreamChunk,
) -> bool {
    let Some(surface) = state.active_surface_for_query(chunk.query_id) else {
        return false;
    };

    let runtime = state.surface_runtime_mut(surface);

    let item = result_slot(runtime.results, &chunk.service);
    item.body.push_str(&chunk.text);
    item.streamed_chunks.push(chunk.text);
    item.grammar_result = None;
    item.alternatives = None;
    item.word_result = None;
    item.no_result = false;
    item.service_name = chunk.service.name;
    item.status = ResultStatus::Streaming;
    item.enabled_query = chunk.service.enabled_query;
    item.has_queried = true;
    item.demoted = false;
    item.expanded = true;
    item.streaming_capable = chunk.service.streaming_capable;

    true
}

fn mark_quick_translate_started(
    state: &mut EasydictUiState,
    surface: QuickTranslateSurface,
    plan: &QuickTranslatePlan,
) {
    let active_ids = plan
        .services
        .iter()
        .map(|service| service.id.as_str())
        .collect::<HashSet<_>>();

    let mut runtime = state.surface_runtime_mut(surface);
    if let Some(connection_status) = runtime.connection_status.as_deref_mut() {
        *connection_status = ConnectionStatus::Connected;
    }
    *runtime.active_query_id = Some(plan.query_id);
    *runtime.status_text = "Translating".to_string();
    *runtime.is_translating = true;
    *runtime.detected_language = None;
    *runtime.services_completed = 0;
    *runtime.active_query_service_count = plan.services.len();
    *runtime.active_query_success_count = 0;
    *runtime.current_quick_query_mode = plan.language_resolution.effective_mode;
    *runtime.grammar_correction_fallback = plan.language_resolution.grammar_correction_fallback;

    for result in runtime.results {
        if active_ids.contains(result.id.as_str()) {
            result.body.clear();
            result.grammar_result = None;
            result.alternatives = None;
            result.word_result = None;
            result.streamed_chunks.clear();
            result.no_result = false;
            result.status = ResultStatus::Loading;
            result.latency_ms = None;
            result.has_queried = true;
            result.expanded = true;
            result.demoted = false;
            result.enabled_query = plan
                .services
                .iter()
                .find(|service| service.id == result.id)
                .map(|service| service.enabled_query)
                .unwrap_or(result.enabled_query);
            result.query_mode = plan
                .services
                .iter()
                .find(|service| service.id == result.id)
                .map(|service| service_query_mode(plan, service))
                .unwrap_or(plan.language_resolution.effective_mode);
        }
    }
}

fn service_query_mode(
    plan: &QuickTranslatePlan,
    service: &QuickTranslateService,
) -> QuickQueryMode {
    if plan.language_resolution.effective_mode == QuickQueryMode::GrammarCorrection
        && service.grammar_capable
    {
        QuickQueryMode::GrammarCorrection
    } else {
        QuickQueryMode::Translation
    }
}

fn service_execution_kind(
    plan: &QuickTranslatePlan,
    service: &QuickTranslateService,
) -> QuickTranslateExecutionKind {
    if service_query_mode(plan, service) == QuickQueryMode::GrammarCorrection {
        QuickTranslateExecutionKind::GrammarCorrection
    } else if service.streaming_capable {
        QuickTranslateExecutionKind::TranslateStream
    } else {
        QuickTranslateExecutionKind::Translate
    }
}

fn run_service_request<B: QuickTranslateBackend>(
    backend: &mut B,
    request: &QuickTranslateServiceRequest,
) -> QuickTranslateServiceOutcome {
    if let Err(error) = backend.configure(&request.settings) {
        return QuickTranslateServiceOutcome {
            service: request.service.clone(),
            grammar_result: None,
            streamed_chunks: Vec::new(),
            result: Err(error),
        };
    }

    if request.service.id.starts_with("mdx::") {
        return match backend.mdx_lookup(&MdxLookupParams {
            dictionary_id: request.service.id.clone(),
            query: request.params.text.clone(),
            fuzzy: false,
        }) {
            Ok(result) => QuickTranslateServiceOutcome {
                service: request.service.clone(),
                grammar_result: None,
                streamed_chunks: Vec::new(),
                result: Ok(mdx_lookup_result_to_translation_result(
                    &request.service,
                    &request.params.text,
                    result,
                )),
            },
            Err(error) => QuickTranslateServiceOutcome {
                service: request.service.clone(),
                grammar_result: None,
                streamed_chunks: Vec::new(),
                result: Err(error),
            },
        };
    }

    if request.execution_kind == QuickTranslateExecutionKind::GrammarCorrection {
        if let Some(params) = &request.grammar_params {
            return match backend.correct_grammar(params) {
                Ok(result) => QuickTranslateServiceOutcome {
                    service: request.service.clone(),
                    grammar_result: Some(grammar_result_to_preview(&result)),
                    streamed_chunks: Vec::new(),
                    result: Ok(grammar_result_to_translation_result(
                        &request.service,
                        result,
                    )),
                },
                Err(error) => QuickTranslateServiceOutcome {
                    service: request.service.clone(),
                    grammar_result: None,
                    streamed_chunks: Vec::new(),
                    result: Err(error),
                },
            };
        }
    }

    if request.execution_kind == QuickTranslateExecutionKind::TranslateStream {
        return match backend.translate_stream(&request.params) {
            Ok(streamed) => QuickTranslateServiceOutcome {
                service: request.service.clone(),
                grammar_result: None,
                streamed_chunks: streamed.chunks,
                result: Ok(streamed.result),
            },
            Err(error) => QuickTranslateServiceOutcome {
                service: request.service.clone(),
                grammar_result: None,
                streamed_chunks: Vec::new(),
                result: Err(error),
            },
        };
    }

    QuickTranslateServiceOutcome {
        service: request.service.clone(),
        grammar_result: None,
        streamed_chunks: Vec::new(),
        result: backend.translate(&request.params),
    }
}

fn mdx_lookup_result_to_translation_result(
    service: &QuickTranslateService,
    query: &str,
    result: MdxLookupResult,
) -> TranslationResultDto {
    if result.entries.is_empty() {
        return TranslationResultDto {
            translated_text: String::new(),
            service_id: Some(service.id.clone()),
            service_name: Some(service.name.clone()),
            detected_language: None,
            result_kind: Some("NoResult".to_string()),
            info_message: Some(format!("No result found in dictionary: {query}")),
            timing_ms: None,
            alternatives: None,
            word_result: None,
        };
    }

    let service_name = result
        .entries
        .first()
        .and_then(|entry| entry.dictionary_name.clone())
        .filter(|name| !name.trim().is_empty())
        .unwrap_or_else(|| service.name.clone());

    TranslationResultDto {
        translated_text: result
            .entries
            .iter()
            .map(mdx_entry_body)
            .collect::<Vec<_>>()
            .join("\n\n"),
        service_id: Some(service.id.clone()),
        service_name: Some(service_name),
        detected_language: None,
        result_kind: Some("Success".to_string()),
        info_message: None,
        timing_ms: None,
        alternatives: None,
        word_result: None,
    }
}

fn mdx_entry_body(entry: &MdxLookupEntry) -> String {
    if entry.key.trim().is_empty() {
        return entry.html.clone();
    }

    format!("{}\n{}", entry.key, entry.html)
}

fn grammar_result_to_translation_result(
    service: &QuickTranslateService,
    result: GrammarCorrectResultDto,
) -> TranslationResultDto {
    TranslationResultDto {
        translated_text: result.corrected_text,
        service_id: result.service_id.or_else(|| Some(service.id.clone())),
        service_name: result.service_name.or_else(|| Some(service.name.clone())),
        detected_language: result.language,
        result_kind: Some("Success".to_string()),
        info_message: None,
        timing_ms: result.timing_ms,
        alternatives: None,
        word_result: None,
    }
}

fn grammar_result_to_preview(result: &GrammarCorrectResultDto) -> GrammarCorrectionPreview {
    GrammarCorrectionPreview::new(
        result.original_text.clone(),
        result.corrected_text.clone(),
        result.explanation.clone(),
        result.has_corrections,
    )
}

fn enabled_services(results: &[TranslationResultPreview]) -> Vec<QuickTranslateService> {
    results
        .iter()
        .filter(|result| result.enabled_query && !result.demoted)
        .map(|result| service_from_result(result, true))
        .collect()
}

fn service_from_result(
    result: &TranslationResultPreview,
    enabled_query: bool,
) -> QuickTranslateService {
    QuickTranslateService {
        id: result.id.clone(),
        name: result.service_name.clone(),
        enabled_query,
        grammar_capable: result.grammar_capable,
        streaming_capable: result.streaming_capable,
    }
}

fn service_is_pending(results: &[TranslationResultPreview], service_id: &str) -> bool {
    results
        .iter()
        .find(|result| result.id == service_id)
        .map(|result| {
            matches!(
                result.status,
                ResultStatus::Loading | ResultStatus::Streaming
            )
        })
        .unwrap_or(true)
}

fn finish_active_query(mut runtime: SurfaceRuntimeMut<'_>) {
    *runtime.active_query_id = None;
    *runtime.active_query_service_count = 0;
    *runtime.is_translating = false;

    if *runtime.active_query_success_count > 0 {
        if let Some(connection_status) = runtime.connection_status.as_deref_mut() {
            *connection_status = ConnectionStatus::Connected;
        }
        *runtime.status_text = "Connected".to_string();
    } else {
        if let Some(connection_status) = runtime.connection_status.as_deref_mut() {
            *connection_status = ConnectionStatus::Error;
        }
        *runtime.status_text = "Error".to_string();
    }

    *runtime.active_query_success_count = 0;
}

fn apply_success(
    results: &mut Vec<TranslationResultPreview>,
    service: &QuickTranslateService,
    result: TranslationResultDto,
    grammar_result: Option<GrammarCorrectionPreview>,
    streamed_chunks: Vec<String>,
    hide_empty_results: bool,
) {
    let item = result_slot(results, service);
    let no_result = is_no_result(&result);
    item.body = if no_result {
        result
            .info_message
            .clone()
            .unwrap_or_else(|| result.translated_text.clone())
    } else if result.translated_text.is_empty() && !streamed_chunks.is_empty() {
        streamed_chunks.join("")
    } else {
        result.translated_text.clone()
    };
    item.grammar_result = grammar_result;
    item.alternatives = if no_result {
        None
    } else {
        result.alternatives.clone()
    };
    item.word_result = if no_result {
        None
    } else {
        result.word_result.clone()
    };
    item.streamed_chunks = streamed_chunks;
    item.no_result = no_result;
    item.service_name = result
        .service_name
        .filter(|value| !value.trim().is_empty())
        .unwrap_or_else(|| service.name.clone());
    item.status = ResultStatus::Ready;
    item.latency_ms = result.timing_ms.and_then(timing_to_u32);
    item.enabled_query = service.enabled_query;
    item.has_queried = true;
    item.demoted = no_result && hide_empty_results;
    item.expanded = !item.demoted;
    item.streaming_capable = service.streaming_capable;
    item.grammar_capable = service.grammar_capable;
}

fn apply_error(
    results: &mut Vec<TranslationResultPreview>,
    service: &QuickTranslateService,
    error: QuickTranslateBackendError,
) {
    let item = result_slot(results, service);
    item.body = error.message;
    item.grammar_result = None;
    item.alternatives = None;
    item.word_result = None;
    item.streamed_chunks.clear();
    item.no_result = false;
    item.service_name = service.name.clone();
    item.status = ResultStatus::Error;
    item.latency_ms = None;
    item.enabled_query = service.enabled_query;
    item.has_queried = true;
    item.demoted = false;
    item.expanded = true;
    item.streaming_capable = service.streaming_capable;
    item.grammar_capable = service.grammar_capable;
}

fn is_no_result(result: &TranslationResultDto) -> bool {
    result
        .result_kind
        .as_deref()
        .map(|value| {
            let normalized = value
                .chars()
                .filter(|character| character.is_ascii_alphanumeric())
                .collect::<String>()
                .to_ascii_lowercase();
            normalized == "noresult"
        })
        .unwrap_or(false)
}

fn result_slot<'a>(
    results: &'a mut Vec<TranslationResultPreview>,
    service: &QuickTranslateService,
) -> &'a mut TranslationResultPreview {
    if let Some(index) = results.iter().position(|result| result.id == service.id) {
        return &mut results[index];
    }

    results.push(TranslationResultPreview::new(
        service.id.clone(),
        service.name.clone(),
        "",
    ));
    let item = results
        .last_mut()
        .expect("a result was just pushed for missing service");
    item.enabled_query = service.enabled_query;
    item.grammar_capable = service.grammar_capable;
    item.streaming_capable = service.streaming_capable;
    item
}

struct SurfaceQueryState<'a> {
    text: &'a str,
    source_language: &'a str,
    target_language: &'a str,
    target_language_manually_selected: bool,
    detected_language: Option<&'a str>,
}

struct SurfaceRuntimeMut<'a> {
    active_query_id: &'a mut Option<u64>,
    active_query_service_count: &'a mut usize,
    active_query_success_count: &'a mut usize,
    is_translating: &'a mut bool,
    status_text: &'a mut String,
    services_completed: &'a mut usize,
    detected_language: &'a mut Option<String>,
    results: &'a mut Vec<TranslationResultPreview>,
    current_quick_query_mode: &'a mut QuickQueryMode,
    grammar_correction_fallback: &'a mut bool,
    connection_status: Option<&'a mut ConnectionStatus>,
}

impl EasydictUiState {
    fn surface_results(&self, surface: QuickTranslateSurface) -> &[TranslationResultPreview] {
        match surface {
            QuickTranslateSurface::Main => &self.results,
            QuickTranslateSurface::Mini => &self.mini.results,
            QuickTranslateSurface::Fixed => &self.fixed.results,
        }
    }

    fn surface_query_state(&self, surface: QuickTranslateSurface) -> SurfaceQueryState<'_> {
        match surface {
            QuickTranslateSurface::Main => SurfaceQueryState {
                text: &self.source_text,
                source_language: &self.source_language,
                target_language: &self.target_language,
                target_language_manually_selected: self.target_language_manually_selected,
                detected_language: self.detected_language.as_deref(),
            },
            QuickTranslateSurface::Mini => SurfaceQueryState {
                text: &self.mini.text,
                source_language: &self.mini.source_language,
                target_language: &self.mini.target_language,
                target_language_manually_selected: self.mini.target_language_manually_selected,
                detected_language: self.mini.detected_language.as_deref(),
            },
            QuickTranslateSurface::Fixed => SurfaceQueryState {
                text: &self.fixed.text,
                source_language: &self.fixed.source_language,
                target_language: &self.fixed.target_language,
                target_language_manually_selected: self.fixed.target_language_manually_selected,
                detected_language: self.fixed.detected_language.as_deref(),
            },
        }
    }

    fn active_surface_for_query(&self, query_id: u64) -> Option<QuickTranslateSurface> {
        if self.active_query_id == Some(query_id) {
            Some(QuickTranslateSurface::Main)
        } else if self.mini.active_query_id == Some(query_id) {
            Some(QuickTranslateSurface::Mini)
        } else if self.fixed.active_query_id == Some(query_id) {
            Some(QuickTranslateSurface::Fixed)
        } else {
            None
        }
    }

    fn surface_runtime_mut(&mut self, surface: QuickTranslateSurface) -> SurfaceRuntimeMut<'_> {
        match surface {
            QuickTranslateSurface::Main => SurfaceRuntimeMut {
                active_query_id: &mut self.active_query_id,
                active_query_service_count: &mut self.active_query_service_count,
                active_query_success_count: &mut self.active_query_success_count,
                is_translating: &mut self.is_translating,
                status_text: &mut self.status_text,
                services_completed: &mut self.services_completed,
                detected_language: &mut self.detected_language,
                results: &mut self.results,
                current_quick_query_mode: &mut self.current_quick_query_mode,
                grammar_correction_fallback: &mut self.grammar_correction_fallback,
                connection_status: Some(&mut self.connection_status),
            },
            QuickTranslateSurface::Mini => SurfaceRuntimeMut {
                active_query_id: &mut self.mini.active_query_id,
                active_query_service_count: &mut self.mini.active_query_service_count,
                active_query_success_count: &mut self.mini.active_query_success_count,
                is_translating: &mut self.mini.is_translating,
                status_text: &mut self.mini.status_text,
                services_completed: &mut self.mini.services_completed,
                detected_language: &mut self.mini.detected_language,
                results: &mut self.mini.results,
                current_quick_query_mode: &mut self.mini.current_quick_query_mode,
                grammar_correction_fallback: &mut self.mini.grammar_correction_fallback,
                connection_status: None,
            },
            QuickTranslateSurface::Fixed => SurfaceRuntimeMut {
                active_query_id: &mut self.fixed.active_query_id,
                active_query_service_count: &mut self.fixed.active_query_service_count,
                active_query_success_count: &mut self.fixed.active_query_success_count,
                is_translating: &mut self.fixed.is_translating,
                status_text: &mut self.fixed.status_text,
                services_completed: &mut self.fixed.services_completed,
                detected_language: &mut self.fixed.detected_language,
                results: &mut self.fixed.results,
                current_quick_query_mode: &mut self.fixed.current_quick_query_mode,
                grammar_correction_fallback: &mut self.fixed.grammar_correction_fallback,
                connection_status: None,
            },
        }
    }
}

fn timing_to_u32(value: i64) -> Option<u32> {
    (0..=u32::MAX as i64)
        .contains(&value)
        .then_some(value as u32)
}

fn source_language_param(value: &str) -> Option<String> {
    let value = normalize_language_code(value);
    if is_auto_language(&value) {
        None
    } else {
        Some(value)
    }
}

fn language_param(value: &str) -> Option<String> {
    let value = normalize_language_code(value);
    (!is_auto_language(&value)).then_some(value)
}

fn detected_language_label(value: &str) -> String {
    format!("Detected: {}", language_display_name(value))
}

fn language_display_name(value: &str) -> String {
    match value.trim().to_ascii_lowercase().as_str() {
        "ar" | "ar-sa" => "Arabic".to_string(),
        "da" | "da-dk" => "Danish".to_string(),
        "de" | "de-de" => "German".to_string(),
        "en" | "en-us" | "en-gb" => "English".to_string(),
        "es" | "es-es" => "Spanish".to_string(),
        "fr" | "fr-fr" => "French".to_string(),
        "hi" | "hi-in" => "Hindi".to_string(),
        "id" | "id-id" => "Indonesian".to_string(),
        "it" | "it-it" => "Italian".to_string(),
        "ja" | "ja-jp" => "Japanese".to_string(),
        "ko" | "ko-kr" => "Korean".to_string(),
        "ms" | "ms-my" => "Malay".to_string(),
        "th" | "th-th" => "Thai".to_string(),
        "vi" | "vi-vn" => "Vietnamese".to_string(),
        "zh" | "zh-cn" | "zh-hans" => "Chinese (Simplified)".to_string(),
        "zh-tw" | "zh-hant" => "Chinese (Traditional)".to_string(),
        "auto" => "Auto Detect".to_string(),
        other if other.is_empty() => "Unknown".to_string(),
        _ => value.to_string(),
    }
}

fn current_app_dir() -> Result<PathBuf, String> {
    let exe = std::env::current_exe()
        .map_err(|error| format!("Could not locate current executable: {error}"))?;
    exe.parent()
        .map(Path::to_path_buf)
        .ok_or_else(|| "Could not locate current executable directory".to_string())
}

fn selected_target_language<'a>(
    query_state: &'a SurfaceQueryState<'a>,
    state: &EasydictUiState,
) -> &'a str {
    if state.settings.auto_select_target_language && !query_state.target_language_manually_selected
    {
        "auto"
    } else {
        query_state.target_language
    }
}

fn effective_source_language(query_state: &SurfaceQueryState<'_>) -> String {
    let selected = normalize_language_code(query_state.source_language);
    if !is_auto_language(&selected) {
        return selected;
    }

    query_state
        .detected_language
        .and_then(language_code_from_detected_label)
        .unwrap_or_else(|| "auto".to_string())
}

fn normalize_language_code(value: &str) -> String {
    match value.trim().to_ascii_lowercase().as_str() {
        "" | "auto" => "auto".to_string(),
        "ar" | "ar-sa" => "ar".to_string(),
        "da" | "da-dk" => "da".to_string(),
        "de" | "de-de" => "de".to_string(),
        "en" | "en-us" | "en-gb" => "en".to_string(),
        "es" | "es-es" => "es".to_string(),
        "fr" | "fr-fr" => "fr".to_string(),
        "hi" | "hi-in" => "hi".to_string(),
        "id" | "id-id" => "id".to_string(),
        "it" | "it-it" => "it".to_string(),
        "ja" | "ja-jp" => "ja".to_string(),
        "ko" | "ko-kr" => "ko".to_string(),
        "ms" | "ms-my" => "ms".to_string(),
        "th" | "th-th" => "th".to_string(),
        "vi" | "vi-vn" => "vi".to_string(),
        "zh" | "zh-cn" | "zh-hans" => "zh-Hans".to_string(),
        "zh-tw" | "zh-hant" => "zh-Hant".to_string(),
        other => other.to_string(),
    }
}

fn is_auto_language(value: &str) -> bool {
    value.trim().is_empty() || value.eq_ignore_ascii_case("auto")
}

fn language_code_from_detected_label(value: &str) -> Option<String> {
    let value = value
        .trim()
        .strip_prefix("Detected:")
        .unwrap_or(value)
        .trim();

    match value.to_ascii_lowercase().as_str() {
        "arabic" => Some("ar".to_string()),
        "danish" => Some("da".to_string()),
        "german" => Some("de".to_string()),
        "english" => Some("en".to_string()),
        "spanish" => Some("es".to_string()),
        "french" => Some("fr".to_string()),
        "hindi" => Some("hi".to_string()),
        "indonesian" => Some("id".to_string()),
        "italian" => Some("it".to_string()),
        "japanese" => Some("ja".to_string()),
        "korean" => Some("ko".to_string()),
        "malay" => Some("ms".to_string()),
        "thai" => Some("th".to_string()),
        "vietnamese" => Some("vi".to_string()),
        "chinese (simplified)" | "simplified chinese" => Some("zh-Hans".to_string()),
        "chinese (traditional)" | "traditional chinese" => Some("zh-Hant".to_string()),
        _ => None,
    }
}

const SELECTABLE_LANGUAGE_CODES: &[&str] = &[
    "ar", "da", "de", "en", "es", "fr", "hi", "id", "it", "ja", "ko", "ms", "th", "vi", "zh-Hans",
    "zh-Hant",
];
