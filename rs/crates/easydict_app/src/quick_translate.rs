use crate::compat_client::{CompatClientError, CompatHostFacade};
use crate::compat_protocol::{
    compat_events, ConfigureParams, GrammarCorrectParams, GrammarCorrectResultDto, MdxLookupEntry,
    MdxLookupParams, MdxLookupResult, SettingsSnapshot, TranslateChunkEventData, TranslateParams,
    TranslationResultDto,
};
use crate::custom_streaming::{
    build_custom_streaming_translation_request_plan, cleanup_custom_streaming_translation_text,
    correct_custom_streaming_grammar, custom_streaming_config_for_service,
    execute_custom_streaming_request, translate_custom_streaming_service,
    CustomStreamingHttpClient, CustomStreamingServiceConfig, ReqwestCustomStreamingHttpClient,
};
use crate::openai_compatible::{
    build_openai_translation_request_plan, cleanup_openai_translation_text,
    correct_grammar_openai_compatible, execute_openai_stream_request,
    openai_compatible_config_for_service, translate_openai_compatible, OpenAiCompatibleConfig,
    OpenAiExecutionError, OpenAiHttpClient, OpenAiTranslationRequest, ReqwestOpenAiHttpClient,
};
use crate::state::{
    settings_snapshot, stable_partition_demoted, ConnectionStatus, EasydictUiState,
    GrammarCorrectionPreview, Message, TranslationResultPreview,
};
use crate::traditional_http::{
    traditional_http_config_for_service, translate_bing_service,
    translate_traditional_http_service, BingHttpClient, ReqwestBingHttpClient,
    ReqwestTraditionalHttpClient, TraditionalHttpClient, TraditionalHttpServiceConfig,
    BING_GLOBAL_HOST,
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

impl From<CompatClientError> for QuickTranslateBackendError {
    fn from(error: CompatClientError) -> Self {
        Self::new(error.to_string())
    }
}

impl From<OpenAiExecutionError> for QuickTranslateBackendError {
    fn from(error: OpenAiExecutionError) -> Self {
        Self::new(error.to_string())
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

pub struct NativeOpenAiQuickTranslateBackend<C> {
    http_client: C,
    settings: Option<SettingsSnapshot>,
}

impl<C> NativeOpenAiQuickTranslateBackend<C> {
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

impl<C: OpenAiHttpClient> QuickTranslateBackend for NativeOpenAiQuickTranslateBackend<C> {
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

impl<C> NativeOpenAiQuickTranslateBackend<C> {
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
            custom_prompt: None,
        })
    }

    fn service_context(
        &self,
        params: &TranslateParams,
    ) -> Result<(String, String, OpenAiCompatibleConfig), QuickTranslateBackendError> {
        self.service_context_for_ids(&params.services)
    }

    fn service_context_for_ids(
        &self,
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

        let config =
            openai_compatible_config_for_service(&service_id, settings).ok_or_else(|| {
                QuickTranslateBackendError::new(format!(
                    "Service '{service_id}' is not handled by the native OpenAI-compatible backend"
                ))
            })?;
        let service_name = native_openai_service_name(&service_id);
        Ok((service_id, service_name, config))
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
            custom_prompt: None,
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

        let config =
            traditional_http_config_for_service(&service_id, settings).ok_or_else(|| {
                QuickTranslateBackendError::new(format!(
                    "Service '{service_id}' is not handled by the native traditional HTTP backend"
                ))
            })?;
        let service_name = native_openai_service_name(&service_id);
        Ok((service_id, service_name, config))
    }
}

/// Native backend for Bing's stateful two-phase free web flow (fetch session
/// credentials from the translator page, then translate). Uses the global host;
/// the region-based China-host toggle is not a persisted setting yet.
pub struct NativeBingQuickTranslateBackend<C> {
    http_client: C,
}

impl<C> NativeBingQuickTranslateBackend<C> {
    pub fn new(http_client: C) -> Self {
        Self { http_client }
    }

    pub fn http_client(&self) -> &C {
        &self.http_client
    }
}

impl<C: BingHttpClient> QuickTranslateBackend for NativeBingQuickTranslateBackend<C> {
    fn configure(
        &mut self,
        _settings: &SettingsSnapshot,
    ) -> Result<(), QuickTranslateBackendError> {
        Ok(())
    }

    fn translate(
        &mut self,
        params: &TranslateParams,
    ) -> Result<TranslationResultDto, QuickTranslateBackendError> {
        translate_bing_service(
            &mut self.http_client,
            BING_GLOBAL_HOST,
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

impl QuickTranslateBackend for CompatHostFacade {
    fn configure(&mut self, settings: &SettingsSnapshot) -> Result<(), QuickTranslateBackendError> {
        CompatHostFacade::configure(
            self,
            &ConfigureParams {
                settings: settings.clone(),
            },
        )
        .map(|_| ())
        .map_err(QuickTranslateBackendError::from)
    }

    fn translate(
        &mut self,
        params: &TranslateParams,
    ) -> Result<TranslationResultDto, QuickTranslateBackendError> {
        CompatHostFacade::translate(self, params).map_err(QuickTranslateBackendError::from)
    }

    fn correct_grammar(
        &mut self,
        params: &GrammarCorrectParams,
    ) -> Result<GrammarCorrectResultDto, QuickTranslateBackendError> {
        CompatHostFacade::grammar_correct(self, params).map_err(QuickTranslateBackendError::from)
    }

    fn translate_stream(
        &mut self,
        params: &TranslateParams,
    ) -> Result<QuickTranslateStreamResult, QuickTranslateBackendError> {
        let result = CompatHostFacade::translate_stream(self, params)
            .map_err(QuickTranslateBackendError::from)?;
        let chunks = self
            .take_events()
            .into_iter()
            .filter(|event| event.event == compat_events::TRANSLATE_CHUNK)
            .filter_map(|event| event.data)
            .filter_map(|data| serde_json::from_value::<TranslateChunkEventData>(data).ok())
            .map(|chunk| chunk.text)
            .collect();

        Ok(QuickTranslateStreamResult { result, chunks })
    }

    fn mdx_lookup(
        &mut self,
        params: &MdxLookupParams,
    ) -> Result<MdxLookupResult, QuickTranslateBackendError> {
        CompatHostFacade::mdx_lookup(self, params).map_err(QuickTranslateBackendError::from)
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
        Ok(app_dir) => run_quick_translate_with_packaged_host(plan, app_dir),
        Err(message) => QuickTranslateOutcome::all_failed(&plan, message),
    }
}

pub fn run_quick_translate_with_packaged_host(
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
                run_quick_translate_service_with_packaged_host(request, &app_dir).outcome
            })
            .collect(),
    }
}

pub fn run_quick_translate_service_with_current_app_dir(
    request: QuickTranslateServiceRequest,
) -> QuickTranslateServiceUpdate {
    match current_app_dir() {
        Ok(app_dir) => run_quick_translate_service_with_packaged_host(request, app_dir),
        Err(message) => service_error_update(request, message),
    }
}

pub fn run_quick_translate_service_with_packaged_host(
    request: QuickTranslateServiceRequest,
    app_dir: impl AsRef<Path>,
) -> QuickTranslateServiceUpdate {
    if request_uses_native_openai(&request) {
        return run_quick_translate_service_with_native_openai(request);
    }

    if request_uses_native_custom_streaming(&request) {
        return run_quick_translate_service_with_native_custom_streaming(request);
    }

    if request_uses_native_traditional_http(&request) {
        return run_quick_translate_service_with_native_traditional_http(request);
    }

    if request_uses_native_bing(&request) {
        return run_quick_translate_service_with_native_bing(request);
    }

    match CompatHostFacade::spawn_packaged(app_dir) {
        Ok(mut backend) => run_quick_translate_service(&mut backend, &request),
        Err(error) => service_error_update(request, error.to_string()),
    }
}

pub fn run_quick_translate_streaming_service_with_current_app_dir(
    request: QuickTranslateServiceRequest,
) -> UnboundedReceiver<Message> {
    let (sender, receiver) = unbounded();

    std::thread::spawn(move || {
        let update = match current_app_dir() {
            Ok(app_dir) => {
                run_quick_translate_streaming_service_with_packaged_host(request, app_dir, &sender)
            }
            Err(message) => service_error_update(request, message),
        };

        let _ = sender.unbounded_send(Message::QuickTranslateServiceFinished(update));
    });

    receiver
}

fn run_quick_translate_streaming_service_with_packaged_host(
    request: QuickTranslateServiceRequest,
    app_dir: impl AsRef<Path>,
    sender: &UnboundedSender<Message>,
) -> QuickTranslateServiceUpdate {
    if request_uses_native_openai(&request) {
        return run_quick_translate_streaming_service_with_native_openai(request, sender);
    }

    if request_uses_native_custom_streaming(&request) {
        return run_quick_translate_streaming_service_with_native_custom_streaming(request, sender);
    }

    if request_uses_native_traditional_http(&request) {
        return run_quick_translate_streaming_service_with_native_traditional_http(request, sender);
    }

    if request_uses_native_bing(&request) {
        return run_quick_translate_streaming_service_with_native_bing(request, sender);
    }

    let mut backend = match CompatHostFacade::spawn_packaged(app_dir) {
        Ok(backend) => backend,
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
    let mut streamed_chunks = Vec::new();
    let result = backend
        .translate_stream_observing_chunks(&request.params, |chunk| {
            streamed_chunks.push(chunk.text.clone());
            let _ = sender.unbounded_send(Message::QuickTranslateStreamChunk(
                QuickTranslateStreamChunk {
                    query_id,
                    service: service.clone(),
                    text: chunk.text,
                },
            ));
        })
        .map_err(QuickTranslateBackendError::from);

    QuickTranslateServiceUpdate {
        query_id,
        outcome: QuickTranslateServiceOutcome {
            service: request.service,
            grammar_result: None,
            streamed_chunks,
            result,
        },
    }
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

fn request_uses_native_openai(request: &QuickTranslateServiceRequest) -> bool {
    openai_compatible_config_for_service(&request.service.id, &request.settings).is_some()
}

fn request_uses_native_custom_streaming(request: &QuickTranslateServiceRequest) -> bool {
    custom_streaming_config_for_service(&request.service.id, &request.settings).is_some()
}

fn request_uses_native_traditional_http(request: &QuickTranslateServiceRequest) -> bool {
    traditional_http_config_for_service(&request.service.id, &request.settings).is_some()
}

fn native_openai_service_name(service_id: &str) -> String {
    find_translation_service_descriptor(service_id)
        .map(|descriptor| descriptor.display_name.to_string())
        .unwrap_or_else(|| service_id.to_string())
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
