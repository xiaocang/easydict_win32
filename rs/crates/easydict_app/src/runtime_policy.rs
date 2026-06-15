#![allow(dead_code)]

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
    let profiles = [
        std::env::var(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE).ok(),
        std::env::var(GENERIC_RUNTIME_PROFILE_ENVIRONMENT_VARIABLE).ok(),
    ]
    .into_iter()
    .map(|value| value.map(|value| runtime_profile_from_value(&value)))
    .collect::<Vec<_>>();

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
fn runtime_profile_from_value(value: &str) -> RuntimeProfile {
    let normalized = value.trim().to_ascii_lowercase();
    if normalized == "hybrid" {
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

#[cfg(test)]
mod tests {
    use std::sync::Mutex;

    use super::*;

    static ENVIRONMENT_LOCK: Mutex<()> = Mutex::new(());

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
    fn generic_hybrid_profile_enables_retained_workers_when_feature_is_compiled() {
        let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
        let snapshot = EnvironmentSnapshot::capture();
        clear_retained_worker_environment();
        std::env::set_var(GENERIC_RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "hybrid");

        let policy = RuntimeRoutePolicy::from_environment();

        assert_eq!(policy, RuntimeRoutePolicy::all_enabled());
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
}
