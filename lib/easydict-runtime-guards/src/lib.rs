#![forbid(unsafe_code)]

use std::path::Path;

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

    matches!(executable_stem, "dotnet" | "powershell" | "pwsh")
        || value == "workers"
        || value == "dotnet"
        || value == "easydict.compathost"
        || value.starts_with("easydict.compathost.")
        || value == "easydict.nativebridge"
        || value.starts_with("easydict.nativebridge.")
        || value.starts_with("easydict.workers.")
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
            || (parent == "shared" && RETAINED_DOTNET_SHARED_FRAMEWORKS.contains(&child))
    })
}

fn retained_runtime_payload_file_name_is_forbidden(file_name: &str) -> bool {
    RETAINED_RUNTIME_FILE_NAMES.contains(&file_name)
        || file_name.ends_with(".runtimeconfig.json")
        || file_name.ends_with(".deps.json")
        || file_name.starts_with("easydict.compathost")
        || file_name.starts_with("easydict.nativebridge")
        || file_name.starts_with("easydict.sidecarclient")
        || file_name.starts_with("easydict.workers.")
        || forbidden_easydict_winui_runtime_file(file_name)
        || (file_name.starts_with("system.") && file_name.ends_with(".dll"))
        || file_name.starts_with("microsoft.csharp")
        || file_name.starts_with("microsoft.visualbasic")
        || file_name.starts_with("microsoft.win32")
        || RETAINED_DOTNET_ASSEMBLY_FILE_NAMES.contains(&file_name)
        || RETAINED_WORKER_SHARED_DOTNET_FILE_NAMES.contains(&file_name)
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
    let Some(suffix) = file_name.strip_prefix("easydict.winui.") else {
        return false;
    };
    matches!(suffix, "exe" | "dll" | "runtimeconfig.json" | "deps.json")
}

fn path_component_contains_retained_runtime_marker(component: &str) -> bool {
    component
        .split(['\\', '/'])
        .any(path_component_is_retained_runtime_marker)
}

const RETAINED_DOTNET_SHARED_FRAMEWORKS: &[&str] = &[
    "microsoft.netcore.app",
    "microsoft.windowsdesktop.app",
    "microsoft.aspnetcore.app",
];

const RETAINED_RUNTIME_FILE_NAMES: &[&str] = &[
    "createdump.exe",
    "dotnet.exe",
    "hostfxr.dll",
    "coreclr.dll",
    "hostpolicy.dll",
    "clrjit.dll",
    "mscordaccore.dll",
    "mscordbi.dll",
    "mscorlib.dll",
    "netstandard.dll",
    "singlefilehost.exe",
    "system.private.corelib.dll",
    "windowsbase.dll",
    "presentationcore.dll",
    "presentationframework.dll",
];

const RETAINED_DOTNET_ASSEMBLY_FILE_NAMES: &[&str] = &[
    "easydict.documentexport.dll",
    "easydict.llm.streaming.dll",
    "easydict.openvino.dll",
    "easydict.sidecarclient.dll",
    "easydict.translationservice.dll",
    "easydict.windowsai.dll",
    "lexindex.dll",
    "mdict.csharp.dll",
    "polyglot.textlayout.dll",
];

const RETAINED_WORKER_SHARED_DOTNET_FILE_NAMES: &[&str] = &[
    "microsoft.interactiveexperiences.projection.dll",
    "microsoft.web.webview2.core.projection.dll",
    "microsoft.windows.sdk.net.dll",
    "microsoft.windows.ui.xaml.dll",
    "microsoft.winui.dll",
    "winrt.runtime.dll",
];

const RETAINED_RUNTIME_CONTENT_MARKERS: &[&[u8]] = &[
    b"hostfxr.dll",
    b"hostpolicy.dll",
    b"coreclr.dll",
    b"clrjit.dll",
    b"singlefilehost.exe",
    b"System.Private.CoreLib",
    b"Microsoft.NETCore.App",
    b".runtimeconfig.json",
    b".deps.json",
    b"This application requires .NET",
    b"Easydict.CompatHost",
    b"Easydict.Workers.",
    b"powershell.exe",
    b"pwsh.exe",
    b"System.Speech",
    b"System.Management.Automation",
    b"WIN_FLUENT_TTS_TEXT",
];

pub fn bytes_contain_retained_runtime_marker(bytes: &[u8]) -> bool {
    RETAINED_RUNTIME_CONTENT_MARKERS.iter().any(|marker| {
        bytes_contain_ascii_case_insensitive(bytes, marker)
            || bytes_contain_utf16le_ascii_case_insensitive(bytes, marker)
    })
}

fn bytes_contain_ascii_case_insensitive(bytes: &[u8], marker: &[u8]) -> bool {
    !marker.is_empty()
        && bytes
            .windows(marker.len())
            .any(|window| ascii_bytes_eq_ignore_case(window, marker))
}

fn bytes_contain_utf16le_ascii_case_insensitive(bytes: &[u8], marker: &[u8]) -> bool {
    let marker_len = marker.len().saturating_mul(2);
    !marker.is_empty()
        && bytes.len() >= marker_len
        && bytes.windows(marker_len).any(|window| {
            window.chunks_exact(2).zip(marker).all(|(left, right)| {
                left[1] == 0 && left[0].to_ascii_lowercase() == right.to_ascii_lowercase()
            })
        })
}

fn ascii_bytes_eq_ignore_case(left: &[u8], right: &[u8]) -> bool {
    left.len() == right.len()
        && left
            .iter()
            .zip(right)
            .all(|(left, right)| left.to_ascii_lowercase() == right.to_ascii_lowercase())
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
            b"legacy script backend: powershell.exe".as_slice(),
            b"old TTS command environment WIN_FLUENT_TTS_TEXT".as_slice(),
        ] {
            assert!(bytes_contain_retained_runtime_marker(bytes));
        }

        assert!(!bytes_contain_retained_runtime_marker(
            b"rust native helper without retained runtime markers"
        ));
    }

    #[test]
    fn content_marker_scanner_rejects_utf16le_retained_runtime_markers() {
        let bytes = "System.Private.CoreLib"
            .encode_utf16()
            .flat_map(u16::to_le_bytes)
            .collect::<Vec<_>>();

        assert!(bytes_contain_retained_runtime_marker(&bytes));
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
