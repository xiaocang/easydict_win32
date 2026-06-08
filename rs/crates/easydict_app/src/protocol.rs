//! Rust-native request/result DTOs shared by the app, CLI, and tests.
//!
//! The wire shape intentionally remains camelCase-compatible with the legacy
//! settings and translation JSON. Legacy compatibility IPC is isolated behind
//! the optional `retained-dotnet-workers` feature.

pub use crate::protocol_core::{
    deserialize_json, local_ai_provider_modes, serialize_json, BlockTranslatedEventData,
    DefinitionDto, GrammarCorrectParams, GrammarCorrectResultDto, ImportedMdxDictionarySnapshot,
    MdxLookupEntry, MdxLookupParams, MdxLookupResult, PhoneticDto, ProgressEventData,
    SettingsSnapshot, StatusEventData, SynonymDto, TranslateDocumentParams,
    TranslateDocumentResult, TranslateParams, TranslationResultDto, WordFormDto, WordResultDto,
};
