#[cfg(all(target_os = "windows", easydict_windows_ai_winrt_bindings))]
#[allow(
    non_snake_case,
    non_upper_case_globals,
    non_camel_case_types,
    dead_code,
    clippy::all
)]
mod winrt {
    include!(concat!(env!("OUT_DIR"), "/windows_ai_bindings.rs"));
}

#[cfg(all(target_os = "windows", easydict_windows_ai_winrt_bindings))]
mod imp {
    use super::winrt::Microsoft::Windows::AI::Text::{
        LanguageModel, LanguageModelOptions, LanguageModelResponseResult,
        LanguageModelResponseStatus,
    };
    use super::winrt::Microsoft::Windows::AI::{
        AIFeatureReadyResult, AIFeatureReadyResultState, AIFeatureReadyState,
    };
    use crate::{
        WindowsAiError, WindowsAiGenerationOptions, WindowsAiReadyState, WindowsAiResponse,
        WindowsAiResponseStatus,
    };
    use std::sync::{Arc, Mutex};
    use windows_core::{HRESULT, HSTRING};
    use windows_future::AsyncOperationProgressHandler;

    pub fn ready_state() -> WindowsAiReadyState {
        LanguageModel::GetReadyState()
            .map(map_ready_state)
            .unwrap_or(WindowsAiReadyState::NotSupportedOnCurrentSystem)
    }

    pub fn ensure_ready() -> Result<WindowsAiReadyState, WindowsAiError> {
        let initial_state = ready_state();
        if initial_state != WindowsAiReadyState::NotReady {
            return Ok(initial_state);
        }

        let operation = LanguageModel::EnsureReadyAsync().map_err(map_winrt_error)?;
        let result = operation.join().map_err(map_winrt_error)?;
        if result
            .Status()
            .map(|status| status == AIFeatureReadyResultState::Success)
            .unwrap_or(false)
        {
            return Ok(WindowsAiReadyState::Ready);
        }

        let refreshed_state = ready_state();
        if refreshed_state == WindowsAiReadyState::Ready {
            return Ok(WindowsAiReadyState::Ready);
        }
        if refreshed_state != WindowsAiReadyState::NotReady {
            return Ok(refreshed_state);
        }

        Err(WindowsAiError::new(preparation_error_message(
            &result,
            initial_state,
            refreshed_state,
        )))
    }

    pub fn generate(
        prompt: &str,
        options: WindowsAiGenerationOptions,
    ) -> Result<WindowsAiResponse, WindowsAiError> {
        let model = create_language_model("generate")?;
        let sdk_options = language_model_options(options)?;
        let prompt = HSTRING::from(prompt);
        let operation = model
            .GenerateResponseAsync2(&prompt, &sdk_options)
            .map_err(|error| runtime_error("generate", error))?;
        let result = operation
            .join()
            .map_err(|error| runtime_error("generate", error))?;
        let response = map_response_result(&result);
        let _ = model.Close();
        Ok(response)
    }

    pub fn generate_stream(
        prompt: &str,
        options: WindowsAiGenerationOptions,
    ) -> Result<Vec<String>, WindowsAiError> {
        let model = create_language_model("stream")?;
        let sdk_options = language_model_options(options)?;
        let prompt = HSTRING::from(prompt);
        let operation = model
            .GenerateResponseAsync2(&prompt, &sdk_options)
            .map_err(|error| runtime_error("stream", error))?;
        let chunks = Arc::new(Mutex::new(Vec::new()));
        let progress_chunks = Arc::clone(&chunks);
        operation
            .SetProgress(&AsyncOperationProgressHandler::<
                LanguageModelResponseResult,
                HSTRING,
            >::new(move |_, token| {
                if let Some(token) = token.cloned() {
                    let text = token.to_string_lossy();
                    if !text.is_empty() {
                        if let Ok(mut chunks) = progress_chunks.lock() {
                            chunks.push(text);
                        }
                    }
                }
                Ok(())
            }))
            .map_err(|error| runtime_error("stream", error))?;

        let result = operation
            .join()
            .map_err(|error| runtime_error("stream", error))?;
        let response = map_response_result(&result);
        let _ = model.Close();
        if response.status != WindowsAiResponseStatus::Complete {
            return Err(WindowsAiError::new(response_error_message(&response)));
        }

        Ok(Arc::try_unwrap(chunks)
            .ok()
            .and_then(|chunks| chunks.into_inner().ok())
            .unwrap_or_default())
    }

    pub fn warm_up(
        prompt: &str,
        options: WindowsAiGenerationOptions,
    ) -> Result<(), WindowsAiError> {
        let response = generate(prompt, options)?;
        if response.status == WindowsAiResponseStatus::Complete {
            Ok(())
        } else {
            Err(WindowsAiError::new(response_error_message(&response)))
        }
    }

    fn create_language_model(operation: &str) -> Result<LanguageModel, WindowsAiError> {
        LanguageModel::CreateAsync()
            .and_then(|operation| operation.join())
            .map_err(|error| runtime_error(operation, error))
    }

    fn language_model_options(
        options: WindowsAiGenerationOptions,
    ) -> Result<LanguageModelOptions, WindowsAiError> {
        let sdk_options = LanguageModelOptions::new().map_err(map_winrt_error)?;
        sdk_options
            .SetTemperature(options.temperature)
            .map_err(map_winrt_error)?;
        sdk_options
            .SetTopK(options.top_k)
            .map_err(map_winrt_error)?;
        sdk_options
            .SetTopP(options.top_p)
            .map_err(map_winrt_error)?;
        Ok(sdk_options)
    }

    fn map_ready_state(state: AIFeatureReadyState) -> WindowsAiReadyState {
        match state {
            state if state == AIFeatureReadyState::Ready => WindowsAiReadyState::Ready,
            state if state == AIFeatureReadyState::NotReady => WindowsAiReadyState::NotReady,
            state if state == AIFeatureReadyState::CapabilityMissing => {
                WindowsAiReadyState::CapabilityMissing
            }
            state if state == AIFeatureReadyState::NotCompatibleWithSystemHardware => {
                WindowsAiReadyState::NotCompatibleWithSystemHardware
            }
            state if state == AIFeatureReadyState::OSUpdateNeeded => {
                WindowsAiReadyState::OsUpdateNeeded
            }
            state if state == AIFeatureReadyState::DisabledByUser => {
                WindowsAiReadyState::DisabledByUser
            }
            _ => WindowsAiReadyState::NotSupportedOnCurrentSystem,
        }
    }

    fn map_response_result(result: &LanguageModelResponseResult) -> WindowsAiResponse {
        let status = result
            .Status()
            .unwrap_or(LanguageModelResponseStatus::Error);
        let text = result
            .Text()
            .map(|text| text.to_string_lossy())
            .unwrap_or_default();
        let error_message = result.ExtendedError().ok().and_then(hresult_message);

        if status == LanguageModelResponseStatus::Complete {
            WindowsAiResponse::complete(text)
        } else if status == LanguageModelResponseStatus::PromptLargerThanContext {
            WindowsAiResponse {
                status: WindowsAiResponseStatus::PromptLargerThanContext,
                text: String::new(),
                error_message,
            }
        } else if status == LanguageModelResponseStatus::BlockedByPolicy
            || status == LanguageModelResponseStatus::PromptBlockedByContentModeration
            || status == LanguageModelResponseStatus::ResponseBlockedByContentModeration
        {
            WindowsAiResponse {
                status: WindowsAiResponseStatus::BlockedByPolicy,
                text: String::new(),
                error_message,
            }
        } else {
            WindowsAiResponse {
                status: WindowsAiResponseStatus::Error,
                text: String::new(),
                error_message: error_message.or_else(|| Some(format!("status={}", status.0))),
            }
        }
    }

    fn preparation_error_message(
        result: &AIFeatureReadyResult,
        initial_state: WindowsAiReadyState,
        refreshed_state: WindowsAiReadyState,
    ) -> String {
        let status = result
            .Status()
            .map(|status| format!("{status:?}"))
            .unwrap_or_else(|_| "unknown".to_string());
        let display = result
            .ErrorDisplayText()
            .ok()
            .map(|text| text.to_string_lossy())
            .filter(|text| !text.trim().is_empty());
        let extended = result.ExtendedError().ok().and_then(hresult_message);
        let error = result.Error().ok().and_then(hresult_message);
        let mut diagnostics = vec![
            format!("result={status}"),
            format!("readyBefore={initial_state:?}"),
            format!("readyAfter={refreshed_state:?}"),
        ];
        if let Some(display) = display {
            diagnostics.push(format!("display={display}"));
        }
        if let Some(extended) = extended {
            diagnostics.push(format!("extended={extended}"));
        }
        if let Some(error) = error {
            diagnostics.push(format!("error={error}"));
        }
        format!(
            "Windows could not prepare the Phi Silica language model: {}",
            diagnostics.join("; ")
        )
    }

    fn response_error_message(response: &WindowsAiResponse) -> String {
        response
            .error_message
            .as_deref()
            .map(str::trim)
            .filter(|message| !message.is_empty())
            .map(|message| format!("Phi Silica returned {:?}: {message}", response.status))
            .unwrap_or_else(|| format!("Phi Silica returned {:?}.", response.status))
    }

    fn runtime_error(operation: &str, error: windows_core::Error) -> WindowsAiError {
        WindowsAiError::new(format!(
            "Windows AI runtime failed while running Phi Silica: operation={operation}; {}",
            winrt_error_message(error)
        ))
    }

    fn map_winrt_error(error: windows_core::Error) -> WindowsAiError {
        WindowsAiError::new(winrt_error_message(error))
    }

    fn winrt_error_message(error: windows_core::Error) -> String {
        format!(
            "hResult=0x{:08X}; message={}",
            error.code().0 as u32,
            error.message()
        )
    }

    fn hresult_message(hresult: HRESULT) -> Option<String> {
        (hresult.0 != 0).then(|| format!("hResult=0x{:08X}", hresult.0 as u32))
    }
}

#[cfg(not(all(target_os = "windows", easydict_windows_ai_winrt_bindings)))]
mod imp {
    use crate::{
        WindowsAiError, WindowsAiGenerationOptions, WindowsAiReadyState, WindowsAiResponse,
    };

    pub fn ready_state() -> WindowsAiReadyState {
        WindowsAiReadyState::NotSupportedOnCurrentSystem
    }

    pub fn ensure_ready() -> Result<WindowsAiReadyState, WindowsAiError> {
        Ok(ready_state())
    }

    pub fn generate(
        _prompt: &str,
        _options: WindowsAiGenerationOptions,
    ) -> Result<WindowsAiResponse, WindowsAiError> {
        Err(unsupported_error())
    }

    pub fn generate_stream(
        _prompt: &str,
        _options: WindowsAiGenerationOptions,
    ) -> Result<Vec<String>, WindowsAiError> {
        Err(unsupported_error())
    }

    pub fn warm_up(
        _prompt: &str,
        _options: WindowsAiGenerationOptions,
    ) -> Result<(), WindowsAiError> {
        Err(unsupported_error())
    }

    fn unsupported_error() -> WindowsAiError {
        WindowsAiError::new("Windows AI WinRT bindings are not available in this build.")
    }
}

pub fn ready_state() -> WindowsAiReadyState {
    imp::ready_state()
}

pub fn ensure_ready() -> Result<WindowsAiReadyState, WindowsAiError> {
    imp::ensure_ready()
}

pub fn generate(
    prompt: &str,
    options: WindowsAiGenerationOptions,
) -> Result<WindowsAiResponse, WindowsAiError> {
    imp::generate(prompt, options)
}

pub fn generate_stream(
    prompt: &str,
    options: WindowsAiGenerationOptions,
) -> Result<Vec<String>, WindowsAiError> {
    imp::generate_stream(prompt, options)
}

pub fn warm_up(prompt: &str, options: WindowsAiGenerationOptions) -> Result<(), WindowsAiError> {
    imp::warm_up(prompt, options)
}

use crate::{WindowsAiError, WindowsAiGenerationOptions, WindowsAiReadyState, WindowsAiResponse};
