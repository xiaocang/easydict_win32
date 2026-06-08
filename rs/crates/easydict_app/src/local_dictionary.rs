use crate::local_dictionary_index::{
    default_local_dictionary_index_root, LocalDictionaryIndexDescriptor,
    LocalDictionaryIndexService, LocalDictionaryIndexSuggestionItem,
};
use crate::mdx_native::{
    native_mdx_lookup_can_route, native_mdx_lookup_local_input_error,
    native_mdx_lookup_needs_credentials, run_native_mdx_lookup_with_factory_and_mdd_policy,
    NativeMdxDictionaryReader, NativeMdxDictionaryReaderFactory, RsMdictReaderFactory,
};
use crate::protocol::{MdxLookupParams, MdxLookupResult, SettingsSnapshot};
use crate::state::{
    settings_snapshot, EasydictUiState, ImportedMdxDictionary, LocalDictionarySuggestion,
};
use std::collections::HashSet;
use std::fmt;
use std::ops::Range;
use std::path::{Path, PathBuf};

const MAX_SUGGESTIONS: usize = 20;
pub const LOCAL_DICTIONARY_SUGGESTION_DELAY_MS: u64 = 150;

#[derive(Clone, Debug, PartialEq)]
pub struct LocalDictionarySuggestionRequest {
    pub query_id: u64,
    pub query: String,
    pub settings: SettingsSnapshot,
    pub dictionaries: Vec<ImportedMdxDictionary>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct LocalDictionarySuggestionUpdate {
    pub query_id: u64,
    pub query: String,
    pub suggestions: Vec<LocalDictionarySuggestion>,
    pub error: Option<String>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LocalDictionarySuggestionError {
    pub message: String,
}

impl LocalDictionarySuggestionError {
    pub fn new(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
        }
    }
}

impl fmt::Display for LocalDictionarySuggestionError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.message)
    }
}

pub trait LocalDictionarySuggestionBackend {
    fn configure(
        &mut self,
        settings: &SettingsSnapshot,
    ) -> Result<(), LocalDictionarySuggestionError> {
        let _ = settings;
        Ok(())
    }

    fn mdx_lookup(
        &mut self,
        params: &MdxLookupParams,
    ) -> Result<MdxLookupResult, LocalDictionarySuggestionError>;
}

pub struct NativeMdxLocalDictionarySuggestionBackend<F = RsMdictReaderFactory> {
    settings: Option<SettingsSnapshot>,
    reader_factory: F,
}

impl Default for NativeMdxLocalDictionarySuggestionBackend<RsMdictReaderFactory> {
    fn default() -> Self {
        Self::new(RsMdictReaderFactory)
    }
}

impl<F> NativeMdxLocalDictionarySuggestionBackend<F> {
    pub fn new(reader_factory: F) -> Self {
        Self {
            settings: None,
            reader_factory,
        }
    }
}

impl<F: NativeMdxDictionaryReaderFactory> LocalDictionarySuggestionBackend
    for NativeMdxLocalDictionarySuggestionBackend<F>
{
    fn configure(
        &mut self,
        settings: &SettingsSnapshot,
    ) -> Result<(), LocalDictionarySuggestionError> {
        self.settings = Some(settings.clone());
        Ok(())
    }

    fn mdx_lookup(
        &mut self,
        params: &MdxLookupParams,
    ) -> Result<MdxLookupResult, LocalDictionarySuggestionError> {
        let settings = self.settings.as_ref().ok_or_else(|| {
            LocalDictionarySuggestionError::new(
                "MDX native suggestion backend must be configured before use",
            )
        })?;

        run_native_mdx_lookup_with_factory_and_mdd_policy(
            &mut self.reader_factory,
            params,
            settings,
            false,
        )
        .map_err(|error| LocalDictionarySuggestionError::new(error.to_string()))
    }
}

pub fn begin_local_dictionary_suggestions(
    state: &mut EasydictUiState,
) -> Option<LocalDictionarySuggestionRequest> {
    if !state.settings.local_dictionary_suggestions
        || state.settings.imported_mdx_dictionaries.is_empty()
    {
        clear_local_dictionary_suggestions(state);
        return None;
    }

    let Some(query) = local_dictionary_query_token(&state.source_text) else {
        clear_local_dictionary_suggestions(state);
        return None;
    };
    let query_id = state.next_suggestion_query_id;
    state.next_suggestion_query_id = state.next_suggestion_query_id.saturating_add(1);
    state.active_suggestion_query_id = Some(query_id);
    state.local_dictionary_suggestion_query = Some(query.clone());
    state.local_dictionary_suggestions.clear();
    state.local_dictionary_suggestion_active_index = None;
    state.local_dictionary_suggestion_error = None;
    state.source_text_focused = true;

    Some(LocalDictionarySuggestionRequest {
        query_id,
        query,
        settings: settings_snapshot(&state.settings),
        dictionaries: state.settings.imported_mdx_dictionaries.clone(),
    })
}

pub fn apply_local_dictionary_suggestion_update(
    state: &mut EasydictUiState,
    update: LocalDictionarySuggestionUpdate,
) -> bool {
    if state.active_suggestion_query_id != Some(update.query_id)
        || state.local_dictionary_suggestion_query.as_deref() != Some(update.query.as_str())
    {
        return false;
    }

    state.active_suggestion_query_id = None;
    state.local_dictionary_suggestions = update.suggestions;
    state.local_dictionary_suggestion_active_index = None;
    state.local_dictionary_suggestion_error = update.error;
    state.source_text_focused = true;
    true
}

pub fn apply_local_dictionary_suggestion(state: &mut EasydictUiState, suggestion: &str) -> bool {
    let Some(range) = current_token_range(&state.source_text) else {
        return false;
    };

    state.source_text.replace_range(range, suggestion);
    clear_local_dictionary_suggestions(state);
    true
}

pub fn focus_local_dictionary_suggestions(state: &mut EasydictUiState) -> bool {
    if state.local_dictionary_suggestions.is_empty() {
        return false;
    }

    state.local_dictionary_suggestion_active_index = Some(0);
    state.source_text_focused = false;
    true
}

pub fn move_local_dictionary_suggestion(state: &mut EasydictUiState, delta: isize) -> bool {
    let count = state.local_dictionary_suggestions.len();
    if count == 0 {
        return false;
    }

    let current = state
        .local_dictionary_suggestion_active_index
        .unwrap_or(if delta < 0 { 0 } else { count - 1 });
    let next = (current as isize + delta).rem_euclid(count as isize) as usize;
    state.local_dictionary_suggestion_active_index = Some(next);
    state.source_text_focused = false;
    true
}

pub fn apply_active_local_dictionary_suggestion(state: &mut EasydictUiState) -> bool {
    let Some(index) = state.local_dictionary_suggestion_active_index else {
        return false;
    };
    let Some(suggestion) = state.local_dictionary_suggestions.get(index) else {
        return false;
    };

    let key = suggestion.key.clone();
    apply_local_dictionary_suggestion(state, &key)
}

pub fn dismiss_local_dictionary_suggestions(state: &mut EasydictUiState) {
    clear_local_dictionary_suggestions(state);
}

pub fn exit_local_dictionary_suggestions(state: &mut EasydictUiState) {
    state.local_dictionary_suggestion_active_index = None;
    state.source_text_focused = true;
}

pub fn run_local_dictionary_suggestion_request_with_current_app_dir(
    request: LocalDictionarySuggestionRequest,
) -> LocalDictionarySuggestionUpdate {
    match current_app_dir() {
        Ok(app_dir) => run_local_dictionary_suggestion_request_with_app_dir(request, app_dir),
        Err(message) => LocalDictionarySuggestionUpdate {
            query_id: request.query_id,
            query: request.query,
            suggestions: Vec::new(),
            error: Some(message),
        },
    }
}

pub fn run_delayed_local_dictionary_suggestion_request_with_current_app_dir(
    request: LocalDictionarySuggestionRequest,
) -> LocalDictionarySuggestionUpdate {
    std::thread::sleep(std::time::Duration::from_millis(
        LOCAL_DICTIONARY_SUGGESTION_DELAY_MS,
    ));
    run_local_dictionary_suggestion_request_with_current_app_dir(request)
}

pub fn run_local_dictionary_suggestion_request_with_app_dir(
    request: LocalDictionarySuggestionRequest,
    _app_dir: impl AsRef<Path>,
) -> LocalDictionarySuggestionUpdate {
    run_local_dictionary_suggestion_request_with_native_index(request)
}

pub fn run_local_dictionary_suggestion_request_with_native_route(
    request: LocalDictionarySuggestionRequest,
) -> LocalDictionarySuggestionUpdate {
    let mut backend = NativeMdxLocalDictionarySuggestionBackend::default();
    run_local_dictionary_suggestion_request(&mut backend, request)
}

pub fn run_local_dictionary_suggestion_request_with_native_index(
    request: LocalDictionarySuggestionRequest,
) -> LocalDictionarySuggestionUpdate {
    let mut reader_factory = RsMdictReaderFactory;
    run_local_dictionary_suggestion_request_with_native_index_root(
        request,
        default_local_dictionary_index_root(),
        &mut reader_factory,
    )
}

pub fn run_local_dictionary_suggestion_request_with_native_index_root<F>(
    request: LocalDictionarySuggestionRequest,
    index_root: impl AsRef<Path>,
    reader_factory: &mut F,
) -> LocalDictionarySuggestionUpdate
where
    F: NativeMdxDictionaryReaderFactory,
{
    let mut index_service =
        match LocalDictionaryIndexService::with_index_root(index_root.as_ref().to_path_buf()) {
            Ok(service) => service,
            Err(error) => {
                return LocalDictionarySuggestionUpdate {
                    query_id: request.query_id,
                    query: request.query,
                    suggestions: Vec::new(),
                    error: Some(error.to_string()),
                };
            }
        };
    let mut accumulator = LocalDictionaryIndexSuggestionAccumulator::default();

    for dictionary in &request.dictionaries {
        let params = MdxLookupParams {
            dictionary_id: dictionary.service_id.clone(),
            query: request.query.clone(),
            fuzzy: true,
        };

        if native_mdx_lookup_needs_credentials(&params, &request.settings) {
            accumulator.push_error("MDX dictionary credentials are required before lookup");
            continue;
        }

        if let Some(error) = native_mdx_lookup_local_input_error(&params, &request.settings) {
            accumulator.push_error(error.to_string());
            continue;
        }

        if !native_mdx_lookup_can_route(&params, &request.settings) {
            accumulator.push_error("MDX dictionary is not supported by the Rust-native MDX reader");
            continue;
        }

        let descriptor = LocalDictionaryIndexDescriptor::from(dictionary);
        let snapshot = dictionary.snapshot();
        let ensure_result = index_service.ensure_index_with_key_loader(&descriptor, true, || {
            let mut reader = reader_factory.open(&snapshot)?;
            reader.all_keys()
        });

        match ensure_result {
            Ok(()) => {
                index_service.register_descriptor(&descriptor);
                let service_ids = [dictionary.service_id.as_str()];
                let items = if local_dictionary_query_uses_wildcards(&request.query) {
                    index_service.match_pattern(&request.query, &service_ids, MAX_SUGGESTIONS)
                } else {
                    index_service.complete(&request.query, &service_ids, MAX_SUGGESTIONS)
                };
                accumulator.push_items(items);
            }
            Err(error) => {
                accumulator.push_error(error.to_string());
            }
        }

        if accumulator.is_full() {
            break;
        }
    }

    accumulator.finish(request.query_id, request.query)
}

#[cfg(feature = "retained-dotnet-workers")]
pub fn run_local_dictionary_suggestion_request_with_routed_backends<N, B>(
    native_backend: &mut N,
    bridge_backend: &mut B,
    request: LocalDictionarySuggestionRequest,
) -> LocalDictionarySuggestionUpdate
where
    N: LocalDictionarySuggestionBackend,
    B: LocalDictionarySuggestionBackend,
{
    let native_config_error = native_backend
        .configure(&request.settings)
        .err()
        .map(|error| error.to_string());
    let bridge_config_error = bridge_backend
        .configure(&request.settings)
        .err()
        .map(|error| error.to_string());
    let mut accumulator = LocalDictionarySuggestionAccumulator::default();

    for dictionary in &request.dictionaries {
        let params = MdxLookupParams {
            dictionary_id: dictionary.service_id.clone(),
            query: request.query.clone(),
            fuzzy: true,
        };

        let result = if local_dictionary_dictionary_can_finish_without_bridge(&request, dictionary)
        {
            if let Some(error) = &native_config_error {
                Err(LocalDictionarySuggestionError::new(error.clone()))
            } else {
                native_backend.mdx_lookup(&params)
            }
        } else if let Some(error) = &bridge_config_error {
            Err(LocalDictionarySuggestionError::new(error.clone()))
        } else {
            bridge_backend.mdx_lookup(&params)
        };

        accumulator.push_result(dictionary, result);

        if accumulator.is_full() {
            break;
        }
    }

    accumulator.finish(request.query_id, request.query)
}

pub fn local_dictionary_suggestion_request_can_route_natively(
    request: &LocalDictionarySuggestionRequest,
) -> bool {
    !request.dictionaries.is_empty()
        && request.dictionaries.iter().all(|dictionary| {
            native_mdx_lookup_can_route(
                &MdxLookupParams {
                    dictionary_id: dictionary.service_id.clone(),
                    query: request.query.clone(),
                    fuzzy: true,
                },
                &request.settings,
            )
        })
}

#[cfg(feature = "retained-dotnet-workers")]
fn local_dictionary_dictionary_can_finish_without_bridge(
    request: &LocalDictionarySuggestionRequest,
    dictionary: &ImportedMdxDictionary,
) -> bool {
    if dictionary.service_id.starts_with("mdx::") {
        return true;
    }

    let params = MdxLookupParams {
        dictionary_id: dictionary.service_id.clone(),
        query: request.query.clone(),
        fuzzy: true,
    };
    native_mdx_lookup_can_route(&params, &request.settings)
        || native_mdx_lookup_local_input_error(&params, &request.settings).is_some()
        || native_mdx_lookup_needs_credentials(&params, &request.settings)
}

#[cfg(feature = "retained-dotnet-workers")]
pub fn run_local_dictionary_suggestion_request_with_lazy_bridge<N, B, F>(
    native_backend: &mut N,
    bridge_backend_factory: F,
    request: LocalDictionarySuggestionRequest,
) -> LocalDictionarySuggestionUpdate
where
    N: LocalDictionarySuggestionBackend,
    B: LocalDictionarySuggestionBackend,
    F: FnOnce() -> Result<B, LocalDictionarySuggestionError>,
{
    let native_config_error = native_backend
        .configure(&request.settings)
        .err()
        .map(|error| error.to_string());
    let mut bridge_backend_factory = Some(bridge_backend_factory);
    let mut bridge_backend: Option<B> = None;
    let mut bridge_config_error: Option<String> = None;
    let mut accumulator = LocalDictionarySuggestionAccumulator::default();

    for dictionary in &request.dictionaries {
        let params = MdxLookupParams {
            dictionary_id: dictionary.service_id.clone(),
            query: request.query.clone(),
            fuzzy: true,
        };

        let result = if local_dictionary_dictionary_can_finish_without_bridge(&request, dictionary)
        {
            if let Some(error) = &native_config_error {
                Err(LocalDictionarySuggestionError::new(error.clone()))
            } else {
                native_backend.mdx_lookup(&params)
            }
        } else {
            ensure_local_dictionary_bridge_backend(
                &mut bridge_backend,
                &mut bridge_backend_factory,
                &mut bridge_config_error,
                &request.settings,
            );

            if let Some(error) = &bridge_config_error {
                Err(LocalDictionarySuggestionError::new(error.clone()))
            } else {
                bridge_backend
                    .as_mut()
                    .expect("bridge backend should be initialized before lookup")
                    .mdx_lookup(&params)
            }
        };

        accumulator.push_result(dictionary, result);

        if accumulator.is_full() {
            break;
        }
    }

    accumulator.finish(request.query_id, request.query)
}

#[cfg(feature = "retained-dotnet-workers")]
fn ensure_local_dictionary_bridge_backend<B, F>(
    bridge_backend: &mut Option<B>,
    bridge_backend_factory: &mut Option<F>,
    bridge_config_error: &mut Option<String>,
    settings: &SettingsSnapshot,
) where
    B: LocalDictionarySuggestionBackend,
    F: FnOnce() -> Result<B, LocalDictionarySuggestionError>,
{
    if bridge_backend.is_some() || bridge_config_error.is_some() {
        return;
    }

    let Some(factory) = bridge_backend_factory.take() else {
        bridge_config_error.get_or_insert_with(|| {
            "Local dictionary bridge backend factory was already consumed".to_string()
        });
        return;
    };

    match factory() {
        Ok(mut backend) => match backend.configure(settings) {
            Ok(()) => {
                *bridge_backend = Some(backend);
            }
            Err(error) => {
                *bridge_config_error = Some(error.to_string());
            }
        },
        Err(error) => {
            *bridge_config_error = Some(error.to_string());
        }
    }
}

pub fn run_local_dictionary_suggestion_request<B: LocalDictionarySuggestionBackend>(
    backend: &mut B,
    request: LocalDictionarySuggestionRequest,
) -> LocalDictionarySuggestionUpdate {
    if let Err(error) = backend.configure(&request.settings) {
        return LocalDictionarySuggestionUpdate {
            query_id: request.query_id,
            query: request.query,
            suggestions: Vec::new(),
            error: Some(error.to_string()),
        };
    }

    let mut accumulator = LocalDictionarySuggestionAccumulator::default();

    for dictionary in &request.dictionaries {
        let result = backend.mdx_lookup(&MdxLookupParams {
            dictionary_id: dictionary.service_id.clone(),
            query: request.query.clone(),
            fuzzy: true,
        });

        accumulator.push_result(dictionary, result);

        if accumulator.is_full() {
            break;
        }
    }

    accumulator.finish(request.query_id, request.query)
}

#[derive(Default)]
struct LocalDictionarySuggestionAccumulator {
    seen: HashSet<String>,
    suggestions: Vec<LocalDictionarySuggestion>,
    last_error: Option<String>,
}

impl LocalDictionarySuggestionAccumulator {
    fn push_result(
        &mut self,
        dictionary: &ImportedMdxDictionary,
        result: Result<MdxLookupResult, LocalDictionarySuggestionError>,
    ) {
        match result {
            Ok(result) => {
                for entry in result.entries {
                    if self.is_full() {
                        break;
                    }

                    if entry.key.trim().is_empty() {
                        continue;
                    }

                    let dictionary_name = entry
                        .dictionary_name
                        .filter(|name| !name.trim().is_empty())
                        .unwrap_or_else(|| dictionary.display_name.clone());
                    let dedupe_key = format!("{}\n{}", entry.key, dictionary_name);
                    if self.seen.insert(dedupe_key) {
                        self.suggestions.push(LocalDictionarySuggestion {
                            key: entry.key,
                            dictionary_name,
                        });
                    }
                }
            }
            Err(error) => {
                self.last_error = Some(error.to_string());
            }
        }
    }

    fn is_full(&self) -> bool {
        self.suggestions.len() >= MAX_SUGGESTIONS
    }

    fn finish(self, query_id: u64, query: String) -> LocalDictionarySuggestionUpdate {
        let error = self
            .suggestions
            .is_empty()
            .then_some(self.last_error)
            .flatten();

        LocalDictionarySuggestionUpdate {
            query_id,
            query,
            suggestions: self.suggestions,
            error,
        }
    }
}

#[derive(Default)]
struct LocalDictionaryIndexSuggestionAccumulator {
    seen_keys: HashSet<String>,
    suggestions: Vec<LocalDictionarySuggestion>,
    last_error: Option<String>,
}

impl LocalDictionaryIndexSuggestionAccumulator {
    fn push_items(&mut self, items: Vec<LocalDictionaryIndexSuggestionItem>) {
        for item in items {
            if self.is_full() {
                break;
            }

            if item.key.trim().is_empty() {
                continue;
            }

            if self.seen_keys.insert(item.key.to_lowercase()) {
                self.suggestions.push(LocalDictionarySuggestion {
                    key: item.key,
                    dictionary_name: item.dict_display_name,
                });
            }
        }
    }

    fn push_error(&mut self, error: impl Into<String>) {
        self.last_error = Some(error.into());
    }

    fn is_full(&self) -> bool {
        self.suggestions.len() >= MAX_SUGGESTIONS
    }

    fn finish(self, query_id: u64, query: String) -> LocalDictionarySuggestionUpdate {
        let error = self
            .suggestions
            .is_empty()
            .then_some(self.last_error)
            .flatten();

        LocalDictionarySuggestionUpdate {
            query_id,
            query,
            suggestions: self.suggestions,
            error,
        }
    }
}

pub fn local_dictionary_query_token(text: &str) -> Option<String> {
    let range = current_token_range(text)?;
    let token = text[range].trim();
    if token.is_empty()
        || token.contains('/')
        || token.contains('\\')
        || token.contains(':')
        || token.starts_with('@')
    {
        return None;
    }

    Some(token.to_string())
}

fn local_dictionary_query_uses_wildcards(query: &str) -> bool {
    query.contains('*') || query.contains('?')
}

fn clear_local_dictionary_suggestions(state: &mut EasydictUiState) {
    state.active_suggestion_query_id = None;
    state.local_dictionary_suggestion_query = None;
    state.local_dictionary_suggestions.clear();
    state.local_dictionary_suggestion_active_index = None;
    state.local_dictionary_suggestion_error = None;
    state.source_text_focused = true;
}

fn current_token_range(text: &str) -> Option<Range<usize>> {
    let end = text.trim_end().len();
    if end == 0 {
        return None;
    }

    let start = text[..end]
        .char_indices()
        .rev()
        .find(|(_, character)| character.is_whitespace())
        .map(|(index, character)| index + character.len_utf8())
        .unwrap_or(0);

    (start < end).then_some(start..end)
}

fn current_app_dir() -> Result<PathBuf, String> {
    std::env::current_exe()
        .map_err(|error| error.to_string())
        .and_then(|path| {
            path.parent()
                .map(Path::to_path_buf)
                .ok_or_else(|| "current executable has no parent directory".to_string())
        })
}
