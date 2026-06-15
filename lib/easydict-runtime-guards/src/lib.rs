#![forbid(unsafe_code)]

use std::path::Path;
use std::sync::OnceLock;

use aho_corasick::{AhoCorasick, AhoCorasickBuilder};

pub const RUNTIME_PROFILE_ENVIRONMENT_VARIABLE: &str = "EASYDICT_RUNTIME_PROFILE";
pub const GENERIC_RUNTIME_PROFILE_ENVIRONMENT_VARIABLE: &str = "RUNTIME_PROFILE";
pub const DISABLE_LOCAL_AI_WORKER_ENVIRONMENT_VARIABLE: &str = "EASYDICT_DISABLE_LOCALAI_WORKER";
pub const DISABLE_LONGDOC_WORKER_ENVIRONMENT_VARIABLE: &str = "EASYDICT_DISABLE_LONGDOC_WORKER";

pub const LOCAL_AI_RUST_NATIVE_REQUIRED_MESSAGE: &str =
    "Windows Local AI requires a Rust-native route for this request.";
pub const LONGDOC_RUST_NATIVE_REQUIRED_MESSAGE: &str =
    "Long Document translation requires a Rust-native route for this request.";

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct RuntimeRoutePolicy {
    pub local_ai_worker_enabled: bool,
    pub longdoc_worker_enabled: bool,
}

impl RuntimeRoutePolicy {
    pub const fn all_enabled() -> Self {
        Self {
            local_ai_worker_enabled: true,
            longdoc_worker_enabled: true,
        }
    }

    pub const fn all_disabled() -> Self {
        Self {
            local_ai_worker_enabled: false,
            longdoc_worker_enabled: false,
        }
    }

    pub const fn without_local_ai_worker(mut self) -> Self {
        self.local_ai_worker_enabled = false;
        self
    }

    pub const fn without_longdoc_worker(mut self) -> Self {
        self.longdoc_worker_enabled = false;
        self
    }

    #[cfg(not(feature = "retained-dotnet-workers"))]
    pub fn with_hybrid_runtime_profile_from_environment(self) -> Self {
        let _ = self;
        Self::all_disabled()
    }

    #[cfg(feature = "retained-dotnet-workers")]
    pub fn with_hybrid_runtime_profile_from_environment(self) -> Self {
        if runtime_profile_from_environment() == RuntimeProfile::Hybrid {
            return self;
        }

        Self::all_disabled()
    }

    #[cfg(not(feature = "retained-dotnet-workers"))]
    pub fn from_environment() -> Self {
        Self::all_disabled()
    }

    #[cfg(feature = "retained-dotnet-workers")]
    pub fn from_environment() -> Self {
        Self {
            local_ai_worker_enabled: !environment_flag_is_enabled(
                DISABLE_LOCAL_AI_WORKER_ENVIRONMENT_VARIABLE,
            ),
            longdoc_worker_enabled: !environment_flag_is_enabled(
                DISABLE_LONGDOC_WORKER_ENVIRONMENT_VARIABLE,
            ),
        }
        .with_hybrid_runtime_profile_from_environment()
    }

    #[cfg(not(feature = "retained-dotnet-workers"))]
    pub fn hybrid_from_environment() -> Self {
        Self::all_disabled()
    }

    #[cfg(feature = "retained-dotnet-workers")]
    pub fn hybrid_from_environment() -> Self {
        Self {
            local_ai_worker_enabled: !environment_flag_is_enabled(
                DISABLE_LOCAL_AI_WORKER_ENVIRONMENT_VARIABLE,
            ),
            longdoc_worker_enabled: !environment_flag_is_enabled(
                DISABLE_LONGDOC_WORKER_ENVIRONMENT_VARIABLE,
            ),
        }
        .with_hybrid_runtime_profile_from_environment()
    }

    pub fn local_ai_worker_disabled_message(self) -> Option<&'static str> {
        (!self.local_ai_worker_enabled).then_some(LOCAL_AI_RUST_NATIVE_REQUIRED_MESSAGE)
    }

    pub fn longdoc_worker_disabled_message(self) -> Option<&'static str> {
        (!self.longdoc_worker_enabled).then_some(LONGDOC_RUST_NATIVE_REQUIRED_MESSAGE)
    }
}

impl Default for RuntimeRoutePolicy {
    fn default() -> Self {
        Self::all_disabled()
    }
}

#[cfg(feature = "retained-dotnet-workers")]
pub type RetainedWorkerPolicy = RuntimeRoutePolicy;

#[cfg(feature = "retained-dotnet-workers")]
pub const LOCAL_AI_WORKER_DISABLED_MESSAGE: &str = LOCAL_AI_RUST_NATIVE_REQUIRED_MESSAGE;
#[cfg(feature = "retained-dotnet-workers")]
pub const LONGDOC_WORKER_DISABLED_MESSAGE: &str = LONGDOC_RUST_NATIVE_REQUIRED_MESSAGE;

#[cfg(feature = "retained-dotnet-workers")]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum RuntimeProfile {
    Unset,
    RustOnly,
    Hybrid,
}

#[cfg(feature = "retained-dotnet-workers")]
fn runtime_profile_from_environment() -> RuntimeProfile {
    let easydict_profile = std::env::var(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE)
        .ok()
        .map(|value| runtime_profile_from_value(&value, RuntimeProfileSource::Easydict));
    let generic_profile = std::env::var(GENERIC_RUNTIME_PROFILE_ENVIRONMENT_VARIABLE)
        .ok()
        .map(|value| runtime_profile_from_value(&value, RuntimeProfileSource::Generic));
    let profiles = [easydict_profile, generic_profile];

    if profiles
        .iter()
        .any(|profile| matches!(profile, Some(RuntimeProfile::RustOnly)))
    {
        return RuntimeProfile::RustOnly;
    }

    if profiles
        .iter()
        .any(|profile| matches!(profile, Some(RuntimeProfile::Hybrid)))
    {
        return RuntimeProfile::Hybrid;
    }

    RuntimeProfile::Unset
}

#[cfg(feature = "retained-dotnet-workers")]
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum RuntimeProfileSource {
    Easydict,
    Generic,
}

#[cfg(feature = "retained-dotnet-workers")]
fn runtime_profile_from_value(value: &str, source: RuntimeProfileSource) -> RuntimeProfile {
    let normalized = value.trim().to_ascii_lowercase();
    if normalized == "hybrid" && source == RuntimeProfileSource::Easydict {
        return RuntimeProfile::Hybrid;
    }

    if matches_rust_only_profile(&normalized) {
        return RuntimeProfile::RustOnly;
    }

    RuntimeProfile::RustOnly
}

#[cfg(feature = "retained-dotnet-workers")]
fn environment_flag_is_enabled(name: &str) -> bool {
    std::env::var(name)
        .ok()
        .is_some_and(|value| matches_truthy(&value))
}

#[cfg(feature = "retained-dotnet-workers")]
fn matches_rust_only_profile(value: &str) -> bool {
    matches!(
        value.trim().to_ascii_lowercase().as_str(),
        "rust-only" | "rustonly" | "rust_only"
    )
}

#[cfg(feature = "retained-dotnet-workers")]
fn matches_truthy(value: &str) -> bool {
    matches!(
        value.trim().to_ascii_lowercase().as_str(),
        "1" | "true" | "yes" | "on"
    )
}

pub fn path_has_retained_runtime_component(path: &Path) -> bool {
    path.components().any(|component| {
        component
            .as_os_str()
            .to_str()
            .is_some_and(path_component_contains_retained_runtime_marker)
    })
}

pub fn path_component_is_retained_runtime_marker(component: &str) -> bool {
    let value = component.to_ascii_lowercase();
    let executable_stem = value
        .strip_suffix(".exe")
        .or_else(|| value.strip_suffix(".cmd"))
        .or_else(|| value.strip_suffix(".bat"))
        .or_else(|| value.strip_suffix(".com"))
        .unwrap_or(&value);

    encoded_ascii_marker_set_contains(
        RETAINED_RUNTIME_COMPONENT_EXACT_MARKERS_XOR,
        executable_stem,
    ) || encoded_ascii_marker_set_contains(RETAINED_RUNTIME_COMPONENT_EXACT_MARKERS_XOR, &value)
        || encoded_ascii_marker_set_any_prefix(
            RETAINED_RUNTIME_COMPONENT_PREFIX_MARKERS_XOR,
            &value,
        )
}

pub fn path_entry_is_retained_runtime_payload_marker(path: &str) -> bool {
    let normalized = path.replace('\\', "/");
    let components = normalized
        .trim_matches('/')
        .split('/')
        .filter(|component| !component.is_empty())
        .map(str::to_ascii_lowercase)
        .collect::<Vec<_>>();
    if components.is_empty() {
        return false;
    }

    if matches!(components[0].as_str(), "dotnet" | "workers") {
        return true;
    }

    if path_entry_contains_retained_runtime_layout(&components) {
        return true;
    }

    let Some(file_name) = components.last() else {
        return false;
    };

    retained_runtime_payload_file_name_is_forbidden(file_name)
}

pub fn command_target_is_retained_runtime_or_script_marker(value: &str) -> bool {
    let normalized = value.trim().replace('\\', "/");
    let lower = normalized.to_ascii_lowercase();
    if lower.is_empty() {
        return false;
    }

    let command_head = command_target_head_token(&lower);
    let command_head_leaf = command_path_leaf(command_head);
    if path_component_is_retained_runtime_marker(command_head_leaf)
        || retained_runtime_payload_file_name_is_forbidden(command_head_leaf)
        || command_leaf_is_retained_script_marker(command_head_leaf)
    {
        return true;
    }

    let executable_leaf = command_path_leaf(
        lower
            .trim_matches('"')
            .rsplit('/')
            .next()
            .unwrap_or(lower.as_str()),
    );

    if path_component_is_retained_runtime_marker(executable_leaf)
        || retained_runtime_payload_file_name_is_forbidden(executable_leaf)
        || command_leaf_is_retained_script_marker(executable_leaf)
    {
        return true;
    }

    path_entry_is_retained_runtime_payload_marker(&lower)
        || lower
            .split('/')
            .filter(|component| !component.is_empty())
            .any(path_component_is_retained_runtime_marker)
        || lower.contains(".ps1")
}

fn command_target_head_token(value: &str) -> &str {
    let trimmed = value.trim();
    let Some(after_quote) = trimmed.strip_prefix('"') else {
        return trimmed.split_whitespace().next().unwrap_or("");
    };

    after_quote
        .split_once('"')
        .map(|(head, _)| head)
        .unwrap_or(after_quote)
}

fn command_path_leaf(value: &str) -> &str {
    value
        .trim_matches('"')
        .rsplit('/')
        .next()
        .unwrap_or(value)
        .split_whitespace()
        .next()
        .unwrap_or("")
        .trim_matches('"')
}

fn path_entry_contains_retained_runtime_layout(components: &[String]) -> bool {
    components.windows(2).any(|window| {
        let parent = window[0].as_str();
        let child = window[1].as_str();
        (parent == "host" && child == "fxr")
            || (parent == "shared"
                && encoded_ascii_marker_set_contains(RETAINED_DOTNET_SHARED_FRAMEWORKS_XOR, child))
    })
}

fn retained_runtime_payload_file_name_is_forbidden(file_name: &str) -> bool {
    encoded_ascii_marker_set_contains(RETAINED_RUNTIME_FILE_NAMES_XOR, file_name)
        || encoded_ascii_marker_set_any_suffix(RETAINED_RUNTIME_FILE_SUFFIXES_XOR, file_name)
        || encoded_ascii_marker_set_any_prefix(RETAINED_RUNTIME_FILE_PREFIXES_XOR, file_name)
        || forbidden_easydict_winui_runtime_file(file_name)
        || (file_name.starts_with("system.") && file_name.ends_with(".dll"))
        || file_name.starts_with("microsoft.csharp")
        || file_name.starts_with("microsoft.visualbasic")
        || file_name.starts_with("microsoft.win32")
        || encoded_ascii_marker_set_contains(RETAINED_DOTNET_ASSEMBLY_FILE_NAMES_XOR, file_name)
        || encoded_ascii_marker_set_contains(
            RETAINED_WORKER_SHARED_DOTNET_FILE_NAMES_XOR,
            file_name,
        )
}

fn command_leaf_is_retained_script_marker(file_name: &str) -> bool {
    let value = file_name.to_ascii_lowercase();
    let executable_stem = value
        .strip_suffix(".exe")
        .or_else(|| value.strip_suffix(".cmd"))
        .or_else(|| value.strip_suffix(".bat"))
        .or_else(|| value.strip_suffix(".com"))
        .unwrap_or(&value);

    executable_stem == "cmd" || value.ends_with(".ps1")
}

fn forbidden_easydict_winui_runtime_file(file_name: &str) -> bool {
    let Some(suffix) =
        encoded_ascii_marker_strip_prefix(EASYDICT_WINUI_RUNTIME_PREFIX_XOR, file_name)
    else {
        return false;
    };
    matches!(suffix, "exe" | "dll")
        || encoded_ascii_marker_matches(EASYDICT_WINUI_RUNTIME_CONFIG_SUFFIX_XOR, suffix.as_bytes())
        || encoded_ascii_marker_matches(EASYDICT_WINUI_DEPS_SUFFIX_XOR, suffix.as_bytes())
}

fn path_component_contains_retained_runtime_marker(component: &str) -> bool {
    component
        .split(['\\', '/'])
        .any(path_component_is_retained_runtime_marker)
}

const RETAINED_RUNTIME_CONTENT_MARKER_XOR_KEY: u8 = 0xa7;

const RETAINED_RUNTIME_COMPONENT_EXACT_MARKERS_XOR: &[&[u8]] = &[
    &[0xc3, 0xc8, 0xd3, 0xc9, 0xc2, 0xd3],
    &[0xd7, 0xc8, 0xd0, 0xc2, 0xd5, 0xd4, 0xcf, 0xc2, 0xcb, 0xcb],
    &[0xd7, 0xd0, 0xd4, 0xcf],
    &[0xd0, 0xc8, 0xd5, 0xcc, 0xc2, 0xd5, 0xd4],
    &[
        0xc2, 0xc6, 0xd4, 0xde, 0xc3, 0xce, 0xc4, 0xd3, 0x89, 0xc4, 0xc8, 0xca, 0xd7, 0xc6, 0xd3,
        0xcf, 0xc8, 0xd4, 0xd3,
    ],
    &[
        0xc2, 0xc6, 0xd4, 0xde, 0xc3, 0xce, 0xc4, 0xd3, 0x89, 0xc9, 0xc6, 0xd3, 0xce, 0xd1, 0xc2,
        0xc5, 0xd5, 0xce, 0xc3, 0xc0, 0xc2,
    ],
];

const RETAINED_RUNTIME_COMPONENT_PREFIX_MARKERS_XOR: &[&[u8]] = &[
    &[
        0xc2, 0xc6, 0xd4, 0xde, 0xc3, 0xce, 0xc4, 0xd3, 0x89, 0xc4, 0xc8, 0xca, 0xd7, 0xc6, 0xd3,
        0xcf, 0xc8, 0xd4, 0xd3, 0x89,
    ],
    &[
        0xc2, 0xc6, 0xd4, 0xde, 0xc3, 0xce, 0xc4, 0xd3, 0x89, 0xc9, 0xc6, 0xd3, 0xce, 0xd1, 0xc2,
        0xc5, 0xd5, 0xce, 0xc3, 0xc0, 0xc2, 0x89,
    ],
    &[
        0xc2, 0xc6, 0xd4, 0xde, 0xc3, 0xce, 0xc4, 0xd3, 0x89, 0xd0, 0xc8, 0xd5, 0xcc, 0xc2, 0xd5,
        0xd4, 0x89,
    ],
];

const RETAINED_RUNTIME_FILE_PREFIXES_XOR: &[&[u8]] = &[
    &[
        0xc2, 0xc6, 0xd4, 0xde, 0xc3, 0xce, 0xc4, 0xd3, 0x89, 0xc4, 0xc8, 0xca, 0xd7, 0xc6, 0xd3,
        0xcf, 0xc8, 0xd4, 0xd3,
    ],
    &[
        0xc2, 0xc6, 0xd4, 0xde, 0xc3, 0xce, 0xc4, 0xd3, 0x89, 0xc9, 0xc6, 0xd3, 0xce, 0xd1, 0xc2,
        0xc5, 0xd5, 0xce, 0xc3, 0xc0, 0xc2,
    ],
    &[
        0xc2, 0xc6, 0xd4, 0xde, 0xc3, 0xce, 0xc4, 0xd3, 0x89, 0xd4, 0xce, 0xc3, 0xc2, 0xc4, 0xc6,
        0xd5, 0xc4, 0xcb, 0xce, 0xc2, 0xc9, 0xd3,
    ],
    &[
        0xc2, 0xc6, 0xd4, 0xde, 0xc3, 0xce, 0xc4, 0xd3, 0x89, 0xd0, 0xc8, 0xd5, 0xcc, 0xc2, 0xd5,
        0xd4, 0x89,
    ],
];

const RETAINED_DOTNET_SHARED_FRAMEWORKS_XOR: &[&[u8]] = &[
    &[
        0xca, 0xce, 0xc4, 0xd5, 0xc8, 0xd4, 0xc8, 0xc1, 0xd3, 0x89, 0xc9, 0xc2, 0xd3, 0xc4, 0xc8,
        0xd5, 0xc2, 0x89, 0xc6, 0xd7, 0xd7,
    ],
    &[
        0xca, 0xce, 0xc4, 0xd5, 0xc8, 0xd4, 0xc8, 0xc1, 0xd3, 0x89, 0xd0, 0xce, 0xc9, 0xc3, 0xc8,
        0xd0, 0xd4, 0xc3, 0xc2, 0xd4, 0xcc, 0xd3, 0xc8, 0xd7, 0x89, 0xc6, 0xd7, 0xd7,
    ],
    &[
        0xca, 0xce, 0xc4, 0xd5, 0xc8, 0xd4, 0xc8, 0xc1, 0xd3, 0x89, 0xc6, 0xd4, 0xd7, 0xc9, 0xc2,
        0xd3, 0xc4, 0xc8, 0xd5, 0xc2, 0x89, 0xc6, 0xd7, 0xd7,
    ],
];

const RETAINED_RUNTIME_FILE_NAMES_XOR: &[&[u8]] = &[
    &[
        0xc4, 0xd5, 0xc2, 0xc6, 0xd3, 0xc2, 0xc3, 0xd2, 0xca, 0xd7, 0x89, 0xc2, 0xdf, 0xc2,
    ],
    &[0xc3, 0xc8, 0xd3, 0xc9, 0xc2, 0xd3, 0x89, 0xc2, 0xdf, 0xc2],
    &[
        0xcf, 0xc8, 0xd4, 0xd3, 0xc1, 0xdf, 0xd5, 0x89, 0xc3, 0xcb, 0xcb,
    ],
    &[
        0xc4, 0xc8, 0xd5, 0xc2, 0xc4, 0xcb, 0xd5, 0x89, 0xc3, 0xcb, 0xcb,
    ],
    &[
        0xcf, 0xc8, 0xd4, 0xd3, 0xd7, 0xc8, 0xcb, 0xce, 0xc4, 0xde, 0x89, 0xc3, 0xcb, 0xcb,
    ],
    &[0xc4, 0xcb, 0xd5, 0xcd, 0xce, 0xd3, 0x89, 0xc3, 0xcb, 0xcb],
    &[
        0xca, 0xd4, 0xc4, 0xc8, 0xd5, 0xc3, 0xc6, 0xc4, 0xc4, 0xc8, 0xd5, 0xc2, 0x89, 0xc3, 0xcb,
        0xcb,
    ],
    &[
        0xca, 0xd4, 0xc4, 0xc8, 0xd5, 0xc3, 0xc5, 0xce, 0x89, 0xc3, 0xcb, 0xcb,
    ],
    &[
        0xca, 0xd4, 0xc4, 0xc8, 0xd5, 0xcb, 0xce, 0xc5, 0x89, 0xc3, 0xcb, 0xcb,
    ],
    &[
        0xc9, 0xc2, 0xd3, 0xd4, 0xd3, 0xc6, 0xc9, 0xc3, 0xc6, 0xd5, 0xc3, 0x89, 0xc3, 0xcb, 0xcb,
    ],
    &[
        0xd4, 0xce, 0xc9, 0xc0, 0xcb, 0xc2, 0xc1, 0xce, 0xcb, 0xc2, 0xcf, 0xc8, 0xd4, 0xd3, 0x89,
        0xc2, 0xdf, 0xc2,
    ],
    &[
        0xd4, 0xde, 0xd4, 0xd3, 0xc2, 0xca, 0x89, 0xd7, 0xd5, 0xce, 0xd1, 0xc6, 0xd3, 0xc2, 0x89,
        0xc4, 0xc8, 0xd5, 0xc2, 0xcb, 0xce, 0xc5, 0x89, 0xc3, 0xcb, 0xcb,
    ],
    &[
        0xd0, 0xce, 0xc9, 0xc3, 0xc8, 0xd0, 0xd4, 0xc5, 0xc6, 0xd4, 0xc2, 0x89, 0xc3, 0xcb, 0xcb,
    ],
    &[
        0xd7, 0xd5, 0xc2, 0xd4, 0xc2, 0xc9, 0xd3, 0xc6, 0xd3, 0xce, 0xc8, 0xc9, 0xc4, 0xc8, 0xd5,
        0xc2, 0x89, 0xc3, 0xcb, 0xcb,
    ],
    &[
        0xd7, 0xd5, 0xc2, 0xd4, 0xc2, 0xc9, 0xd3, 0xc6, 0xd3, 0xce, 0xc8, 0xc9, 0xc1, 0xd5, 0xc6,
        0xca, 0xc2, 0xd0, 0xc8, 0xd5, 0xcc, 0x89, 0xc3, 0xcb, 0xcb,
    ],
];

const RETAINED_DOTNET_ASSEMBLY_FILE_NAMES_XOR: &[&[u8]] = &[
    &[
        0xc2, 0xc6, 0xd4, 0xde, 0xc3, 0xce, 0xc4, 0xd3, 0x89, 0xc3, 0xc8, 0xc4, 0xd2, 0xca, 0xc2,
        0xc9, 0xd3, 0xc2, 0xdf, 0xd7, 0xc8, 0xd5, 0xd3, 0x89, 0xc3, 0xcb, 0xcb,
    ],
    &[
        0xc2, 0xc6, 0xd4, 0xde, 0xc3, 0xce, 0xc4, 0xd3, 0x89, 0xcb, 0xcb, 0xca, 0x89, 0xd4, 0xd3,
        0xd5, 0xc2, 0xc6, 0xca, 0xce, 0xc9, 0xc0, 0x89, 0xc3, 0xcb, 0xcb,
    ],
    &[
        0xc2, 0xc6, 0xd4, 0xde, 0xc3, 0xce, 0xc4, 0xd3, 0x89, 0xc8, 0xd7, 0xc2, 0xc9, 0xd1, 0xce,
        0xc9, 0xc8, 0x89, 0xc3, 0xcb, 0xcb,
    ],
    &[
        0xc2, 0xc6, 0xd4, 0xde, 0xc3, 0xce, 0xc4, 0xd3, 0x89, 0xd4, 0xce, 0xc3, 0xc2, 0xc4, 0xc6,
        0xd5, 0xc4, 0xcb, 0xce, 0xc2, 0xc9, 0xd3, 0x89, 0xc3, 0xcb, 0xcb,
    ],
    &[
        0xc2, 0xc6, 0xd4, 0xde, 0xc3, 0xce, 0xc4, 0xd3, 0x89, 0xd3, 0xd5, 0xc6, 0xc9, 0xd4, 0xcb,
        0xc6, 0xd3, 0xce, 0xc8, 0xc9, 0xd4, 0xc2, 0xd5, 0xd1, 0xce, 0xc4, 0xc2, 0x89, 0xc3, 0xcb,
        0xcb,
    ],
    &[
        0xc2, 0xc6, 0xd4, 0xde, 0xc3, 0xce, 0xc4, 0xd3, 0x89, 0xd0, 0xce, 0xc9, 0xc3, 0xc8, 0xd0,
        0xd4, 0xc6, 0xce, 0x89, 0xc3, 0xcb, 0xcb,
    ],
    &[
        0xcb, 0xc2, 0xdf, 0xce, 0xc9, 0xc3, 0xc2, 0xdf, 0x89, 0xc3, 0xcb, 0xcb,
    ],
    &[
        0xca, 0xc3, 0xce, 0xc4, 0xd3, 0x89, 0xc4, 0xd4, 0xcf, 0xc6, 0xd5, 0xd7, 0x89, 0xc3, 0xcb,
        0xcb,
    ],
    &[
        0xd7, 0xc8, 0xcb, 0xde, 0xc0, 0xcb, 0xc8, 0xd3, 0x89, 0xd3, 0xc2, 0xdf, 0xd3, 0xcb, 0xc6,
        0xde, 0xc8, 0xd2, 0xd3, 0x89, 0xc3, 0xcb, 0xcb,
    ],
];

const RETAINED_WORKER_SHARED_DOTNET_FILE_NAMES_XOR: &[&[u8]] = &[
    &[
        0xca, 0xce, 0xc4, 0xd5, 0xc8, 0xd4, 0xc8, 0xc1, 0xd3, 0x89, 0xce, 0xc9, 0xd3, 0xc2, 0xd5,
        0xc6, 0xc4, 0xd3, 0xce, 0xd1, 0xc2, 0xc2, 0xdf, 0xd7, 0xc2, 0xd5, 0xce, 0xc2, 0xc9, 0xc4,
        0xc2, 0xd4, 0x89, 0xd7, 0xd5, 0xc8, 0xcd, 0xc2, 0xc4, 0xd3, 0xce, 0xc8, 0xc9, 0x89, 0xc3,
        0xcb, 0xcb,
    ],
    &[
        0xca, 0xce, 0xc4, 0xd5, 0xc8, 0xd4, 0xc8, 0xc1, 0xd3, 0x89, 0xd0, 0xc2, 0xc5, 0x89, 0xd0,
        0xc2, 0xc5, 0xd1, 0xce, 0xc2, 0xd0, 0x95, 0x89, 0xc4, 0xc8, 0xd5, 0xc2, 0x89, 0xd7, 0xd5,
        0xc8, 0xcd, 0xc2, 0xc4, 0xd3, 0xce, 0xc8, 0xc9, 0x89, 0xc3, 0xcb, 0xcb,
    ],
    &[
        0xca, 0xce, 0xc4, 0xd5, 0xc8, 0xd4, 0xc8, 0xc1, 0xd3, 0x89, 0xd0, 0xce, 0xc9, 0xc3, 0xc8,
        0xd0, 0xd4, 0x89, 0xd4, 0xc3, 0xcc, 0x89, 0xc9, 0xc2, 0xd3, 0x89, 0xc3, 0xcb, 0xcb,
    ],
    &[
        0xca, 0xce, 0xc4, 0xd5, 0xc8, 0xd4, 0xc8, 0xc1, 0xd3, 0x89, 0xd0, 0xce, 0xc9, 0xc3, 0xc8,
        0xd0, 0xd4, 0x89, 0xd2, 0xce, 0x89, 0xdf, 0xc6, 0xca, 0xcb, 0x89, 0xc3, 0xcb, 0xcb,
    ],
    &[
        0xca, 0xce, 0xc4, 0xd5, 0xc8, 0xd4, 0xc8, 0xc1, 0xd3, 0x89, 0xd0, 0xce, 0xc9, 0xd2, 0xce,
        0x89, 0xc3, 0xcb, 0xcb,
    ],
    &[
        0xd0, 0xce, 0xc9, 0xd5, 0xd3, 0x89, 0xd5, 0xd2, 0xc9, 0xd3, 0xce, 0xca, 0xc2, 0x89, 0xc3,
        0xcb, 0xcb,
    ],
];

const RETAINED_RUNTIME_FILE_SUFFIXES_XOR: &[&[u8]] = &[
    &[
        0x89, 0xd5, 0xd2, 0xc9, 0xd3, 0xce, 0xca, 0xc2, 0xc4, 0xc8, 0xc9, 0xc1, 0xce, 0xc0, 0x89,
        0xcd, 0xd4, 0xc8, 0xc9,
    ],
    &[0x89, 0xc3, 0xc2, 0xd7, 0xd4, 0x89, 0xcd, 0xd4, 0xc8, 0xc9],
];

const EASYDICT_WINUI_RUNTIME_PREFIX_XOR: &[u8] = &[
    0xc2, 0xc6, 0xd4, 0xde, 0xc3, 0xce, 0xc4, 0xd3, 0x89, 0xd0, 0xce, 0xc9, 0xd2, 0xce, 0x89,
];
const EASYDICT_WINUI_RUNTIME_CONFIG_SUFFIX_XOR: &[u8] = &[
    0xd5, 0xd2, 0xc9, 0xd3, 0xce, 0xca, 0xc2, 0xc4, 0xc8, 0xc9, 0xc1, 0xce, 0xc0, 0x89, 0xcd, 0xd4,
    0xc8, 0xc9,
];
const EASYDICT_WINUI_DEPS_SUFFIX_XOR: &[u8] =
    &[0xc3, 0xc2, 0xd7, 0xd4, 0x89, 0xcd, 0xd4, 0xc8, 0xc9];

// Keep the forbidden byte markers encoded so applications that link the guard
// helpers do not fail package validation by carrying the scanner's own table.
const RETAINED_RUNTIME_CONTENT_MARKERS_XOR: &[&[u8]] = &[
    &[
        0xcf, 0xc8, 0xd4, 0xd3, 0xc1, 0xdf, 0xd5, 0x89, 0xc3, 0xcb, 0xcb,
    ],
    &[
        0xcf, 0xc8, 0xd4, 0xd3, 0xd7, 0xc8, 0xcb, 0xce, 0xc4, 0xde, 0x89, 0xc3, 0xcb, 0xcb,
    ],
    &[
        0xc4, 0xc8, 0xd5, 0xc2, 0xc4, 0xcb, 0xd5, 0x89, 0xc3, 0xcb, 0xcb,
    ],
    &[0xc4, 0xcb, 0xd5, 0xcd, 0xce, 0xd3, 0x89, 0xc3, 0xcb, 0xcb],
    &[
        0xd4, 0xce, 0xc9, 0xc0, 0xcb, 0xc2, 0xc1, 0xce, 0xcb, 0xc2, 0xcf, 0xc8, 0xd4, 0xd3, 0x89,
        0xc2, 0xdf, 0xc2,
    ],
    &[
        0xf4, 0xde, 0xd4, 0xd3, 0xc2, 0xca, 0x89, 0xf7, 0xd5, 0xce, 0xd1, 0xc6, 0xd3, 0xc2, 0x89,
        0xe4, 0xc8, 0xd5, 0xc2, 0xeb, 0xce, 0xc5,
    ],
    &[
        0xea, 0xce, 0xc4, 0xd5, 0xc8, 0xd4, 0xc8, 0xc1, 0xd3, 0x89, 0xe9, 0xe2, 0xf3, 0xe4, 0xc8,
        0xd5, 0xc2, 0x89, 0xe6, 0xd7, 0xd7,
    ],
    &[
        0x89, 0xd5, 0xd2, 0xc9, 0xd3, 0xce, 0xca, 0xc2, 0xc4, 0xc8, 0xc9, 0xc1, 0xce, 0xc0, 0x89,
        0xcd, 0xd4, 0xc8, 0xc9,
    ],
    &[0x89, 0xc3, 0xc2, 0xd7, 0xd4, 0x89, 0xcd, 0xd4, 0xc8, 0xc9],
    &[
        0xf3, 0xcf, 0xce, 0xd4, 0x87, 0xc6, 0xd7, 0xd7, 0xcb, 0xce, 0xc4, 0xc6, 0xd3, 0xce, 0xc8,
        0xc9, 0x87, 0xd5, 0xc2, 0xd6, 0xd2, 0xce, 0xd5, 0xc2, 0xd4, 0x87, 0x89, 0xe9, 0xe2, 0xf3,
    ],
    &[
        0xe2, 0xc6, 0xd4, 0xde, 0xc3, 0xce, 0xc4, 0xd3, 0x89, 0xe4, 0xc8, 0xca, 0xd7, 0xc6, 0xd3,
        0xef, 0xc8, 0xd4, 0xd3,
    ],
    &[
        0xe2, 0xc6, 0xd4, 0xde, 0xc3, 0xce, 0xc4, 0xd3, 0x89, 0xf0, 0xc8, 0xd5, 0xcc, 0xc2, 0xd5,
        0xd4, 0x89,
    ],
    &[
        0xd7, 0xc8, 0xd0, 0xc2, 0xd5, 0xd4, 0xcf, 0xc2, 0xcb, 0xcb, 0x89, 0xc2, 0xdf, 0xc2,
    ],
    &[0xd7, 0xd0, 0xd4, 0xcf, 0x89, 0xc2, 0xdf, 0xc2],
    &[
        0xf4, 0xde, 0xd4, 0xd3, 0xc2, 0xca, 0x89, 0xf4, 0xd7, 0xc2, 0xc2, 0xc4, 0xcf,
    ],
    &[
        0xf4, 0xde, 0xd4, 0xd3, 0xc2, 0xca, 0x89, 0xea, 0xc6, 0xc9, 0xc6, 0xc0, 0xc2, 0xca, 0xc2,
        0xc9, 0xd3, 0x89, 0xe6, 0xd2, 0xd3, 0xc8, 0xca, 0xc6, 0xd3, 0xce, 0xc8, 0xc9,
    ],
    &[
        0xf0, 0xee, 0xe9, 0xf8, 0xe1, 0xeb, 0xf2, 0xe2, 0xe9, 0xf3, 0xf8, 0xf3, 0xf3, 0xf4, 0xf8,
        0xf3, 0xe2, 0xff, 0xf3,
    ],
    &[
        0xf4, 0xde, 0xd4, 0xd3, 0xc2, 0xca, 0x89, 0xf0, 0xce, 0xc9, 0xc3, 0xc8, 0xd0, 0xd4, 0x89,
        0xe1, 0xc8, 0xd5, 0xca, 0xd4,
    ],
];

pub fn bytes_contain_retained_runtime_marker(bytes: &[u8]) -> bool {
    retained_runtime_content_matcher().is_match(bytes)
}

static RETAINED_RUNTIME_CONTENT_MATCHER: OnceLock<AhoCorasick> = OnceLock::new();

fn retained_runtime_content_matcher() -> &'static AhoCorasick {
    RETAINED_RUNTIME_CONTENT_MATCHER.get_or_init(build_retained_runtime_content_matcher)
}

fn build_retained_runtime_content_matcher() -> AhoCorasick {
    let ascii_markers = decoded_runtime_content_markers();
    let mut patterns = Vec::with_capacity(ascii_markers.len().saturating_mul(2));
    for marker in ascii_markers {
        patterns.push(utf16le_ascii_marker(&marker));
        patterns.push(marker);
    }

    AhoCorasickBuilder::new()
        .ascii_case_insensitive(true)
        .build(&patterns)
        .expect("retained runtime markers should build an Aho-Corasick matcher")
}

fn decoded_runtime_content_markers() -> Vec<Vec<u8>> {
    RETAINED_RUNTIME_CONTENT_MARKERS_XOR
        .iter()
        .map(|encoded_marker| decoded_runtime_content_marker(encoded_marker))
        .collect()
}

fn decoded_runtime_content_marker(encoded_marker: &[u8]) -> Vec<u8> {
    encoded_marker
        .iter()
        .map(|byte| byte ^ RETAINED_RUNTIME_CONTENT_MARKER_XOR_KEY)
        .collect()
}

fn utf16le_ascii_marker(marker: &[u8]) -> Vec<u8> {
    marker
        .iter()
        .flat_map(|byte| [*byte, 0])
        .collect::<Vec<_>>()
}

fn encoded_ascii_marker_set_contains(markers: &[&[u8]], value: &str) -> bool {
    markers
        .iter()
        .any(|marker| encoded_ascii_marker_matches(marker, value.as_bytes()))
}

fn encoded_ascii_marker_set_any_suffix(markers: &[&[u8]], value: &str) -> bool {
    markers
        .iter()
        .any(|marker| encoded_ascii_marker_is_suffix(marker, value))
}

fn encoded_ascii_marker_set_any_prefix(markers: &[&[u8]], value: &str) -> bool {
    markers
        .iter()
        .any(|marker| encoded_ascii_marker_is_prefix(marker, value))
}

fn encoded_ascii_marker_is_prefix(encoded_marker: &[u8], value: &str) -> bool {
    let bytes = value.as_bytes();
    bytes.len() >= encoded_marker.len()
        && encoded_ascii_marker_matches(encoded_marker, &bytes[..encoded_marker.len()])
}

fn encoded_ascii_marker_is_suffix(encoded_marker: &[u8], value: &str) -> bool {
    let bytes = value.as_bytes();
    bytes.len() >= encoded_marker.len()
        && encoded_ascii_marker_matches(
            encoded_marker,
            &bytes[bytes.len() - encoded_marker.len()..],
        )
}

fn encoded_ascii_marker_strip_prefix<'a>(encoded_marker: &[u8], value: &'a str) -> Option<&'a str> {
    let bytes = value.as_bytes();
    if bytes.len() < encoded_marker.len()
        || !encoded_ascii_marker_matches(encoded_marker, &bytes[..encoded_marker.len()])
    {
        return None;
    }

    Some(&value[encoded_marker.len()..])
}

fn encoded_ascii_marker_matches(encoded_marker: &[u8], value: &[u8]) -> bool {
    encoded_marker.len() == value.len()
        && encoded_marker.iter().zip(value).all(|(left, right)| {
            (left ^ RETAINED_RUNTIME_CONTENT_MARKER_XOR_KEY).to_ascii_lowercase()
                == right.to_ascii_lowercase()
        })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unset_runtime_profile_defaults_to_rust_only_for_rs_packages() {
        let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
        let snapshot = EnvironmentSnapshot::capture();
        clear_retained_worker_environment();

        let policy = RuntimeRoutePolicy::from_environment();

        assert_eq!(policy, RuntimeRoutePolicy::all_disabled());
        assert_eq!(
            policy.local_ai_worker_disabled_message(),
            Some(LOCAL_AI_RUST_NATIVE_REQUIRED_MESSAGE)
        );
        assert_eq!(
            policy.longdoc_worker_disabled_message(),
            Some(LONGDOC_RUST_NATIVE_REQUIRED_MESSAGE)
        );
        snapshot.restore();
    }

    #[test]
    fn default_policy_is_rust_only_to_avoid_accidental_retained_worker_startup() {
        assert_eq!(
            RuntimeRoutePolicy::default(),
            RuntimeRoutePolicy::all_disabled()
        );
    }

    #[test]
    fn rust_only_runtime_profile_disables_all_retained_workers() {
        let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
        let snapshot = EnvironmentSnapshot::capture();
        clear_retained_worker_environment();
        std::env::set_var(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only");

        let policy = RuntimeRoutePolicy::from_environment();

        assert_eq!(policy, RuntimeRoutePolicy::all_disabled());
        assert_eq!(
            policy.local_ai_worker_disabled_message(),
            Some(LOCAL_AI_RUST_NATIVE_REQUIRED_MESSAGE)
        );
        assert_eq!(
            policy.longdoc_worker_disabled_message(),
            Some(LONGDOC_RUST_NATIVE_REQUIRED_MESSAGE)
        );
        snapshot.restore();
    }

    #[test]
    fn generic_rust_only_runtime_profile_disables_all_retained_workers() {
        let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
        let snapshot = EnvironmentSnapshot::capture();
        clear_retained_worker_environment();
        std::env::set_var(GENERIC_RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only");

        let policy = RuntimeRoutePolicy::from_environment();

        assert_eq!(policy, RuntimeRoutePolicy::all_disabled());
        snapshot.restore();
    }

    #[test]
    fn explicit_policy_is_still_rust_only_without_hybrid_runtime_profile() {
        let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
        let snapshot = EnvironmentSnapshot::capture();
        clear_retained_worker_environment();

        let policy =
            RuntimeRoutePolicy::all_enabled().with_hybrid_runtime_profile_from_environment();

        assert_eq!(policy, RuntimeRoutePolicy::all_disabled());
        snapshot.restore();
    }

    #[cfg(not(feature = "retained-dotnet-workers"))]
    #[test]
    fn hybrid_runtime_profile_stays_disabled_when_retained_worker_bridge_is_not_compiled() {
        let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
        let snapshot = EnvironmentSnapshot::capture();
        clear_retained_worker_environment();
        std::env::set_var(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "hybrid");

        let policy = RuntimeRoutePolicy::from_environment();

        assert_eq!(policy, RuntimeRoutePolicy::all_disabled());
        assert_eq!(RuntimeRoutePolicy::hybrid_from_environment(), policy);
        snapshot.restore();
    }

    #[cfg(feature = "retained-dotnet-workers")]
    #[test]
    fn hybrid_runtime_profile_preserves_individual_disable_flags() {
        let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
        let snapshot = EnvironmentSnapshot::capture();
        clear_retained_worker_environment();
        std::env::set_var(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "hybrid");
        std::env::set_var(DISABLE_LOCAL_AI_WORKER_ENVIRONMENT_VARIABLE, "yes");

        let policy = RuntimeRoutePolicy::from_environment();

        assert_eq!(
            policy,
            RuntimeRoutePolicy {
                local_ai_worker_enabled: false,
                longdoc_worker_enabled: true,
            }
        );
        snapshot.restore();
    }

    #[cfg(feature = "retained-dotnet-workers")]
    #[test]
    fn explicit_policy_requires_hybrid_runtime_profile_to_enable_workers() {
        let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
        let snapshot = EnvironmentSnapshot::capture();
        clear_retained_worker_environment();

        for value in ["rust-only", "dotnet", ""] {
            std::env::set_var(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, value);

            let policy =
                RuntimeRoutePolicy::all_enabled().with_hybrid_runtime_profile_from_environment();

            assert_eq!(
                policy,
                RuntimeRoutePolicy::all_disabled(),
                "{value:?} must not let injected policies enable retained workers"
            );
        }

        std::env::set_var(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "hybrid");
        assert_eq!(
            RuntimeRoutePolicy::all_enabled()
                .without_local_ai_worker()
                .with_hybrid_runtime_profile_from_environment(),
            RuntimeRoutePolicy {
                local_ai_worker_enabled: false,
                longdoc_worker_enabled: true,
            }
        );

        snapshot.restore();
    }

    #[cfg(feature = "retained-dotnet-workers")]
    #[test]
    fn generic_rust_only_profile_overrides_easydict_hybrid_profile() {
        let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
        let snapshot = EnvironmentSnapshot::capture();
        clear_retained_worker_environment();
        std::env::set_var(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "hybrid");
        std::env::set_var(GENERIC_RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only");

        let policy = RuntimeRoutePolicy::from_environment();

        assert_eq!(policy, RuntimeRoutePolicy::all_disabled());
        snapshot.restore();
    }

    #[cfg(feature = "retained-dotnet-workers")]
    #[test]
    fn generic_hybrid_profile_does_not_enable_retained_workers_when_feature_is_compiled() {
        let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
        let snapshot = EnvironmentSnapshot::capture();
        clear_retained_worker_environment();
        std::env::set_var(GENERIC_RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "hybrid");

        let policy = RuntimeRoutePolicy::from_environment();

        assert_eq!(policy, RuntimeRoutePolicy::all_disabled());
        snapshot.restore();
    }

    #[cfg(feature = "retained-dotnet-workers")]
    #[test]
    fn unknown_runtime_profile_overrides_other_hybrid_profile() {
        let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
        let snapshot = EnvironmentSnapshot::capture();
        clear_retained_worker_environment();

        for (easydict_profile, generic_profile) in [
            ("dotnet", "hybrid"),
            ("hybrid", "dotnet"),
            ("dotnet-hybrid", "hybrid"),
            ("hybrid", "dotnet-hybrid"),
        ] {
            std::env::set_var(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, easydict_profile);
            std::env::set_var(
                GENERIC_RUNTIME_PROFILE_ENVIRONMENT_VARIABLE,
                generic_profile,
            );

            let policy = RuntimeRoutePolicy::from_environment();

            assert_eq!(
                policy,
                RuntimeRoutePolicy::all_disabled(),
                "unknown profile pair EASYDICT_RUNTIME_PROFILE={easydict_profile:?}, RUNTIME_PROFILE={generic_profile:?} must not enable retained workers"
            );
        }

        snapshot.restore();
    }

    #[cfg(feature = "retained-dotnet-workers")]
    #[test]
    fn dotnet_named_runtime_profiles_do_not_enable_retained_workers() {
        let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
        let snapshot = EnvironmentSnapshot::capture();
        clear_retained_worker_environment();

        for value in ["dotnet", "dotnet-hybrid"] {
            std::env::set_var(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, value);

            let policy = RuntimeRoutePolicy::from_environment();

            assert_eq!(
                policy,
                RuntimeRoutePolicy::all_disabled(),
                "{value} must not opt the first rs package into retained .NET workers"
            );
        }
        snapshot.restore();
    }

    #[test]
    fn path_component_classifier_rejects_retained_runtime_roots() {
        for component in [
            "workers",
            "dotnet",
            "dotnet.exe",
            "dotnet.cmd",
            "powershell.exe",
            "pwsh.cmd",
            "Easydict.CompatHost",
            "Easydict.CompatHost.exe",
            "Easydict.NativeBridge",
            "Easydict.NativeBridge.exe",
            "Easydict.Workers.LongDoc",
            "Easydict.Workers.LocalAi.exe",
        ] {
            assert!(
                path_component_is_retained_runtime_marker(component),
                "{component} should be rejected as a retained runtime marker"
            );
        }
    }

    #[test]
    fn path_component_classifier_allows_rust_native_names() {
        for component in [
            "EasydictRs",
            "browser-bridge",
            "easydict-native-bridge.exe",
            "easydict_browser_registrar.exe",
            "easydict_cli.exe",
        ] {
            assert!(
                !path_component_is_retained_runtime_marker(component),
                "{component} should be allowed as a Rust-native component"
            );
        }
    }

    #[test]
    fn path_classifier_rejects_renamed_bridge_under_retained_root() {
        assert!(path_has_retained_runtime_component(Path::new(
            r"C:\Payload\dotnet\host\fxr\easydict-native-bridge.exe"
        )));
        assert!(path_has_retained_runtime_component(Path::new(
            r"C:\Payload\workers\ocr\easydict-native-bridge.exe"
        )));
        assert!(path_has_retained_runtime_component(Path::new(
            r"C:\Payload\Easydict.Workers.LocalAi\easydict-native-bridge.exe"
        )));
    }

    #[test]
    fn path_classifier_allows_native_bridge_under_rs_root() {
        assert!(!path_has_retained_runtime_component(Path::new(
            r"C:\Tools\EasydictRs\browser-bridge\easydict-native-bridge.exe"
        )));
    }

    #[test]
    fn path_entry_classifier_rejects_retained_runtime_payload_entries() {
        for entry in [
            "dotnet/host/fxr/8.0.11/hostfxr.dll",
            "workers/localai/Easydict.Workers.LocalAi.exe",
            "nested/host/fxr/8.0.11/hostfxr.dll",
            "nested/shared/Microsoft.WindowsDesktop.App/8.0.11/coreclr.dll",
            "Easydict.WinUI.deps.json",
            "Easydict.CompatHost.exe",
            "System.Private.CoreLib.dll",
            "Microsoft.Web.WebView2.Core.Projection.dll",
            "assets/LexIndex.dll",
        ] {
            assert!(
                path_entry_is_retained_runtime_payload_marker(entry),
                "{entry} should be rejected as retained runtime payload"
            );
        }
    }

    #[test]
    fn path_entry_classifier_allows_first_release_rust_payload_entries() {
        for entry in [
            "Easydict.Rust.exe",
            "easydict_cli.exe",
            "easydict_long_doc.exe",
            "easydict-native-bridge.exe",
            "easydict_browser_registrar.exe",
            "README-portable.txt",
        ] {
            assert!(
                !path_entry_is_retained_runtime_payload_marker(entry),
                "{entry} should be allowed as first-release Rust payload"
            );
        }
    }

    #[test]
    fn command_target_classifier_rejects_retained_runtime_and_script_targets() {
        for target in [
            "dotnet.exe",
            "dotnet.cmd",
            r"C:\Program Files\dotnet\dotnet.exe",
            "cmd /c dotnet.exe",
            "cmd.exe /c powershell.exe",
            r"C:\Windows\System32\cmd.exe /c C:\Easydict\dotnet\dotnet.exe",
            "powershell -NoProfile",
            "pwsh.cmd",
            r"C:\Easydict\workers\localai\Easydict.Workers.LocalAi.exe",
            r"C:\Easydict\dotnet\host\fxr\8.0.11\hostfxr.dll",
            r"C:\Easydict\dotnet\shared\Microsoft.NETCore.App\8.0.11\foundry.exe",
            "scripts/legacy-backend.ps1",
            "Easydict.CompatHost.exe",
            "Easydict.WinUI.runtimeconfig.json",
        ] {
            assert!(
                command_target_is_retained_runtime_or_script_marker(target),
                "{target} should be rejected as a retained runtime or script command target"
            );
        }
    }

    #[test]
    fn command_target_classifier_allows_native_foundry_targets() {
        for target in [
            "foundry",
            "foundry.exe",
            "foundry.cmd",
            r"C:\Program Files\Microsoft Foundry Local\foundry.exe",
            "/usr/local/bin/foundry",
        ] {
            assert!(
                !command_target_is_retained_runtime_or_script_marker(target),
                "{target} should be allowed as a native Foundry Local command target"
            );
        }
    }

    #[test]
    fn content_marker_scanner_rejects_ascii_retained_runtime_and_script_markers() {
        for bytes in [
            b"native apphost still references hostfxr.dll".as_slice(),
            b"bootstrap says This application requires .NET".as_slice(),
            b"legacy script backend: powershell.exe".as_slice(),
            b"old TTS command environment WIN_FLUENT_TTS_TEXT".as_slice(),
            b"old TTS backend: System.Speech.Synthesis".as_slice(),
            b"old dialog backend: System.Windows.Forms".as_slice(),
        ] {
            assert!(bytes_contain_retained_runtime_marker(bytes));
        }

        assert!(!bytes_contain_retained_runtime_marker(
            b"rust native helper without retained runtime markers"
        ));
    }

    #[test]
    fn content_marker_scanner_rejects_utf16le_retained_runtime_markers() {
        for marker in [
            "hostfxr.dll",
            "System.Private.CoreLib",
            "sYsTeM.pRiVaTe.CoReLiB",
            "System.Speech.Synthesis",
            "System.Windows.Forms",
            "This application requires .NET",
        ] {
            let bytes = marker
                .encode_utf16()
                .flat_map(u16::to_le_bytes)
                .collect::<Vec<_>>();

            assert!(
                bytes_contain_retained_runtime_marker(&bytes),
                "{marker} should be rejected as UTF-16LE retained runtime content"
            );
        }
    }

    #[test]
    fn encoded_marker_tables_do_not_trigger_content_scanner_by_themselves() {
        let mut bytes = Vec::new();

        for table in [
            RETAINED_RUNTIME_COMPONENT_EXACT_MARKERS_XOR,
            RETAINED_RUNTIME_COMPONENT_PREFIX_MARKERS_XOR,
            RETAINED_RUNTIME_FILE_PREFIXES_XOR,
            RETAINED_DOTNET_SHARED_FRAMEWORKS_XOR,
            RETAINED_RUNTIME_FILE_NAMES_XOR,
            RETAINED_DOTNET_ASSEMBLY_FILE_NAMES_XOR,
            RETAINED_WORKER_SHARED_DOTNET_FILE_NAMES_XOR,
            RETAINED_RUNTIME_FILE_SUFFIXES_XOR,
            RETAINED_RUNTIME_CONTENT_MARKERS_XOR,
        ] {
            for marker in table {
                bytes.extend_from_slice(marker);
                bytes.push(0);
            }
        }

        for marker in [
            EASYDICT_WINUI_RUNTIME_PREFIX_XOR,
            EASYDICT_WINUI_RUNTIME_CONFIG_SUFFIX_XOR,
            EASYDICT_WINUI_DEPS_SUFFIX_XOR,
        ] {
            bytes.extend_from_slice(marker);
            bytes.push(0);
        }

        assert!(!bytes_contain_retained_runtime_marker(&bytes));
    }

    struct EnvironmentSnapshot {
        runtime_profile: Option<String>,
        generic_runtime_profile: Option<String>,
        disable_local_ai_worker: Option<String>,
        disable_longdoc_worker: Option<String>,
    }

    impl EnvironmentSnapshot {
        fn capture() -> Self {
            Self {
                runtime_profile: std::env::var(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE).ok(),
                generic_runtime_profile: std::env::var(
                    GENERIC_RUNTIME_PROFILE_ENVIRONMENT_VARIABLE,
                )
                .ok(),
                disable_local_ai_worker: std::env::var(
                    DISABLE_LOCAL_AI_WORKER_ENVIRONMENT_VARIABLE,
                )
                .ok(),
                disable_longdoc_worker: std::env::var(DISABLE_LONGDOC_WORKER_ENVIRONMENT_VARIABLE)
                    .ok(),
            }
        }

        fn restore(self) {
            restore_environment_value(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, self.runtime_profile);
            restore_environment_value(
                GENERIC_RUNTIME_PROFILE_ENVIRONMENT_VARIABLE,
                self.generic_runtime_profile,
            );
            restore_environment_value(
                DISABLE_LOCAL_AI_WORKER_ENVIRONMENT_VARIABLE,
                self.disable_local_ai_worker,
            );
            restore_environment_value(
                DISABLE_LONGDOC_WORKER_ENVIRONMENT_VARIABLE,
                self.disable_longdoc_worker,
            );
        }
    }

    fn clear_retained_worker_environment() {
        std::env::remove_var(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE);
        std::env::remove_var(GENERIC_RUNTIME_PROFILE_ENVIRONMENT_VARIABLE);
        std::env::remove_var(DISABLE_LOCAL_AI_WORKER_ENVIRONMENT_VARIABLE);
        std::env::remove_var(DISABLE_LONGDOC_WORKER_ENVIRONMENT_VARIABLE);
    }

    fn restore_environment_value(name: &str, value: Option<String>) {
        if let Some(value) = value {
            std::env::set_var(name, value);
        } else {
            std::env::remove_var(name);
        }
    }

    static ENVIRONMENT_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());
}
