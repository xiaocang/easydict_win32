pub use easydict_runtime_guards::RuntimeRoutePolicy;

#[cfg(not(feature = "retained-dotnet-workers"))]
pub use easydict_runtime_guards::LOCAL_AI_RUST_NATIVE_REQUIRED_MESSAGE;

#[cfg(feature = "retained-dotnet-workers")]
pub use easydict_runtime_guards::{
    RetainedWorkerPolicy, DISABLE_LOCAL_AI_WORKER_ENVIRONMENT_VARIABLE,
    DISABLE_LONGDOC_WORKER_ENVIRONMENT_VARIABLE, GENERIC_RUNTIME_PROFILE_ENVIRONMENT_VARIABLE,
    LOCAL_AI_WORKER_DISABLED_MESSAGE, LONGDOC_WORKER_DISABLED_MESSAGE,
    RUNTIME_PROFILE_ENVIRONMENT_VARIABLE,
};

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_policy_is_owned_by_runtime_guards_lib() {
        assert_eq!(
            RuntimeRoutePolicy::default(),
            easydict_runtime_guards::RuntimeRoutePolicy::all_disabled()
        );
    }
}
