pub const RUNTIME_PROFILE_ENVIRONMENT_VARIABLE: &str = "EASYDICT_RUNTIME_PROFILE";
pub const DISABLE_LOCAL_AI_WORKER_ENVIRONMENT_VARIABLE: &str = "EASYDICT_DISABLE_LOCALAI_WORKER";
pub const DISABLE_LONGDOC_WORKER_ENVIRONMENT_VARIABLE: &str = "EASYDICT_DISABLE_LONGDOC_WORKER";

pub const LOCAL_AI_WORKER_DISABLED_MESSAGE: &str =
    "Windows Local AI requires a Rust-native route; retained .NET Local AI workers are disabled.";
pub const LONGDOC_WORKER_DISABLED_MESSAGE: &str =
    "Long Document translation requires a Rust-native route; retained .NET Long Document workers are disabled.";

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct RetainedWorkerPolicy {
    pub local_ai_worker_enabled: bool,
    pub longdoc_worker_enabled: bool,
}

impl RetainedWorkerPolicy {
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

    pub fn from_environment() -> Self {
        match runtime_profile_from_environment() {
            RuntimeProfile::RustOnly | RuntimeProfile::Unset => return Self::all_disabled(),
            RuntimeProfile::Hybrid => {}
        }

        Self {
            local_ai_worker_enabled: !environment_flag_is_enabled(
                DISABLE_LOCAL_AI_WORKER_ENVIRONMENT_VARIABLE,
            ),
            longdoc_worker_enabled: !environment_flag_is_enabled(
                DISABLE_LONGDOC_WORKER_ENVIRONMENT_VARIABLE,
            ),
        }
    }

    pub fn hybrid_from_environment() -> Self {
        if matches!(
            runtime_profile_from_environment(),
            RuntimeProfile::RustOnly | RuntimeProfile::Unset
        ) {
            return Self::all_disabled();
        }

        Self {
            local_ai_worker_enabled: !environment_flag_is_enabled(
                DISABLE_LOCAL_AI_WORKER_ENVIRONMENT_VARIABLE,
            ),
            longdoc_worker_enabled: !environment_flag_is_enabled(
                DISABLE_LONGDOC_WORKER_ENVIRONMENT_VARIABLE,
            ),
        }
    }

    pub fn local_ai_worker_disabled_message(self) -> Option<&'static str> {
        (!self.local_ai_worker_enabled).then_some(LOCAL_AI_WORKER_DISABLED_MESSAGE)
    }

    pub fn longdoc_worker_disabled_message(self) -> Option<&'static str> {
        (!self.longdoc_worker_enabled).then_some(LONGDOC_WORKER_DISABLED_MESSAGE)
    }
}

impl Default for RetainedWorkerPolicy {
    fn default() -> Self {
        Self::all_enabled()
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum RuntimeProfile {
    Unset,
    RustOnly,
    Hybrid,
}

fn runtime_profile_from_environment() -> RuntimeProfile {
    std::env::var(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE)
        .ok()
        .map(|value| runtime_profile_from_value(&value))
        .unwrap_or(RuntimeProfile::Unset)
}

fn runtime_profile_from_value(value: &str) -> RuntimeProfile {
    let normalized = value.trim().to_ascii_lowercase();
    if matches!(normalized.as_str(), "hybrid" | "dotnet" | "dotnet-hybrid") {
        return RuntimeProfile::Hybrid;
    }

    if matches_rust_only_profile(&normalized) {
        return RuntimeProfile::RustOnly;
    }

    RuntimeProfile::RustOnly
}

fn environment_flag_is_enabled(name: &str) -> bool {
    std::env::var(name)
        .ok()
        .is_some_and(|value| matches_truthy(&value))
}

fn matches_rust_only_profile(value: &str) -> bool {
    matches!(
        value.trim().to_ascii_lowercase().as_str(),
        "rust-only" | "rustonly" | "rust_only"
    )
}

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

        let policy = RetainedWorkerPolicy::from_environment();

        assert_eq!(policy, RetainedWorkerPolicy::all_disabled());
        assert_eq!(
            policy.local_ai_worker_disabled_message(),
            Some(LOCAL_AI_WORKER_DISABLED_MESSAGE)
        );
        assert_eq!(
            policy.longdoc_worker_disabled_message(),
            Some(LONGDOC_WORKER_DISABLED_MESSAGE)
        );
        snapshot.restore();
    }

    #[test]
    fn rust_only_runtime_profile_disables_all_retained_workers() {
        let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
        let snapshot = EnvironmentSnapshot::capture();
        clear_retained_worker_environment();
        std::env::set_var(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "rust-only");

        let policy = RetainedWorkerPolicy::from_environment();

        assert_eq!(policy, RetainedWorkerPolicy::all_disabled());
        assert_eq!(
            policy.local_ai_worker_disabled_message(),
            Some(LOCAL_AI_WORKER_DISABLED_MESSAGE)
        );
        assert_eq!(
            policy.longdoc_worker_disabled_message(),
            Some(LONGDOC_WORKER_DISABLED_MESSAGE)
        );
        snapshot.restore();
    }

    #[test]
    fn hybrid_runtime_profile_preserves_individual_disable_flags() {
        let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
        let snapshot = EnvironmentSnapshot::capture();
        clear_retained_worker_environment();
        std::env::set_var(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE, "hybrid");
        std::env::set_var(DISABLE_LOCAL_AI_WORKER_ENVIRONMENT_VARIABLE, "yes");

        let policy = RetainedWorkerPolicy::from_environment();

        assert_eq!(
            policy,
            RetainedWorkerPolicy {
                local_ai_worker_enabled: false,
                longdoc_worker_enabled: true,
            }
        );
        snapshot.restore();
    }

    struct EnvironmentSnapshot {
        runtime_profile: Option<String>,
        disable_local_ai_worker: Option<String>,
        disable_longdoc_worker: Option<String>,
    }

    impl EnvironmentSnapshot {
        fn capture() -> Self {
            Self {
                runtime_profile: std::env::var(RUNTIME_PROFILE_ENVIRONMENT_VARIABLE).ok(),
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
