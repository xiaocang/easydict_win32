use crate::compat_client::{CompatClientError, CompatHostFacade};
use crate::compat_protocol::{ConfigureParams, MdxLookupParams, MdxLookupResult, SettingsSnapshot};
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

impl From<CompatClientError> for LocalDictionarySuggestionError {
    fn from(error: CompatClientError) -> Self {
        Self::new(error.to_string())
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

impl LocalDictionarySuggestionBackend for CompatHostFacade {
    fn configure(
        &mut self,
        settings: &SettingsSnapshot,
    ) -> Result<(), LocalDictionarySuggestionError> {
        CompatHostFacade::configure(
            self,
            &ConfigureParams {
                settings: settings.clone(),
            },
        )
        .map(|_| ())
        .map_err(LocalDictionarySuggestionError::from)
    }

    fn mdx_lookup(
        &mut self,
        params: &MdxLookupParams,
    ) -> Result<MdxLookupResult, LocalDictionarySuggestionError> {
        CompatHostFacade::mdx_lookup(self, params).map_err(LocalDictionarySuggestionError::from)
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
        Ok(app_dir) => run_local_dictionary_suggestion_request_with_packaged_host(request, app_dir),
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

pub fn run_local_dictionary_suggestion_request_with_packaged_host(
    request: LocalDictionarySuggestionRequest,
    app_dir: impl AsRef<Path>,
) -> LocalDictionarySuggestionUpdate {
    match CompatHostFacade::spawn_packaged(app_dir) {
        Ok(mut backend) => run_local_dictionary_suggestion_request(&mut backend, request),
        Err(error) => LocalDictionarySuggestionUpdate {
            query_id: request.query_id,
            query: request.query,
            suggestions: Vec::new(),
            error: Some(error.to_string()),
        },
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

    let mut seen = HashSet::new();
    let mut suggestions = Vec::new();
    let mut last_error = None;

    for dictionary in &request.dictionaries {
        let result = backend.mdx_lookup(&MdxLookupParams {
            dictionary_id: dictionary.service_id.clone(),
            query: request.query.clone(),
            fuzzy: true,
        });

        match result {
            Ok(result) => {
                for entry in result.entries {
                    if suggestions.len() >= MAX_SUGGESTIONS {
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
                    if seen.insert(dedupe_key) {
                        suggestions.push(LocalDictionarySuggestion {
                            key: entry.key,
                            dictionary_name,
                        });
                    }
                }
            }
            Err(error) => {
                last_error = Some(error.to_string());
            }
        }

        if suggestions.len() >= MAX_SUGGESTIONS {
            break;
        }
    }

    let error = suggestions.is_empty().then_some(last_error).flatten();

    LocalDictionarySuggestionUpdate {
        query_id: request.query_id,
        query: request.query,
        suggestions,
        error,
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
