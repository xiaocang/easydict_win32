#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Runs Rust core validation commands or profiles while isolating parallel UI work.

.DESCRIPTION
  The migration often has UI/parity work dirty in parallel. This wrapper backs
  up those known files, temporarily restores their gstep:@ versions, runs one
  validation command or one named validation profile, then restores the backups
  in a finally block. It also cleans known generated lockfile drift from
  standalone Rust helper crates.

  Example:
    $testArgs = @("cargo", "test", "--manifest-path", "rs/Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "desktop_shell_action")
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -RustTestNocapture @testArgs
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -ListProfiles
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -RecommendProfiles
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -RunRecommendedProfiles
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -Profile text-selection -DryRun
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -Profile text-selection
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -Profile mouse-selection
#>

[CmdletBinding(PositionalBinding = $false)]
param(
    [switch]$NoParallelUiIsolation,

    [switch]$RustTestNocapture,

    [switch]$ListProfiles,

    [switch]$RecommendProfiles,

    [switch]$RunRecommendedProfiles,

    [switch]$DryRun,

    [string]$Profile,

    [string[]]$ChangedPath,

    [string]$DiffFrom = "gstep:@",

    [string]$DiffTo = "worktree",

    [int]$MaxRecommendedProfiles = 1,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Command
)

$ErrorActionPreference = "Stop"

if ($Command.Count -gt 0 -and $Command[0] -eq "--") {
    $Command = @($Command | Select-Object -Skip 1)
}

function New-ValidationStep {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string[]]$Command
    )

    [pscustomobject]@{
        Name = $Name
        Command = $Command
    }
}

$validationProfiles = [ordered]@{
    "core-validation-tooling" = [pscustomobject]@{
        Description = "Self-tests for the core slice validation wrapper, profile recommendations, and dry-run wiring."
        Steps = @(
            (New-ValidationStep "validation wrapper self-tests" @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "rs\scripts\Test-RsCoreSliceValidation.ps1"))
        )
    }
    "desktop-settings" = [pscustomobject]@{
        Description = "Desktop shell/integration side-effect diagnostics plus settings-save persistence errors."
        Steps = @(
            (New-ValidationStep "desktop shell diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "desktop_shell_action")),
            (New-ValidationStep "desktop integration diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "desktop_integration_action")),
            (New-ValidationStep "settings save diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "settings_save"))
        )
    }
    "settings-credentials" = [pscustomobject]@{
        Description = "Rust-owned settings storage/migration, credential protection, DPAPI wrapper, and settings-save diagnostics."
        Steps = @(
            (New-ValidationStep "format settings and credential slice" @("rustfmt", "--edition", "2021", "--check", "lib\easydict-windows-credentials\src\lib.rs", "rs\crates\easydict_app\src\credential_protection.rs", "rs\crates\easydict_app\src\settings_storage.rs", "rs\crates\easydict_app\src\settings_migration.rs", "rs\crates\easydict_app\tests\credential_protection_behavior.rs", "rs\crates\easydict_app\tests\settings_storage_behavior.rs", "rs\crates\easydict_app\tests\settings_migration_behavior.rs", "rs\crates\easydict_app\tests\quick_translate_behavior.rs")),
            (New-ValidationStep "Windows credential wrapper contracts" @("cargo", "test", "--manifest-path", "lib\easydict-windows-credentials\Cargo.toml")),
            (New-ValidationStep "credential protection contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "credential_protection_behavior")),
            (New-ValidationStep "settings storage contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "settings_storage_behavior")),
            (New-ValidationStep "settings migration contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "settings_migration_behavior")),
            (New-ValidationStep "settings save app diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "settings_save")),
            (New-ValidationStep "settings path no retained runtime markers" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "default_api_boundary_behavior", "default_process_spawn_surface_has_no_retained_dotnet_runtime_entries"))
        )
    }
    "builtin-ai-registration" = [pscustomobject]@{
        Description = "Built-in AI proxy device registration request planning, app lifecycle, and visible diagnostics."
        Steps = @(
            (New-ValidationStep "format Built-in AI registration slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\state.rs", "rs\crates\easydict_app\tests\quick_translate_behavior.rs")),
            (New-ValidationStep "app Built-in AI registration state/lifecycle" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "builtin_device")),
            (New-ValidationStep "OpenAI-compatible Built-in AI registration contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "openai_compatible_behavior", "builtin_device_registration"))
        )
    }
    "custom-streaming" = [pscustomobject]@{
        Description = "Gemini/Doubao native custom streaming request planning, live chunks, and CLI SSE contracts."
        Steps = @(
            (New-ValidationStep "format custom streaming slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\custom_streaming.rs", "rs\crates\easydict_app\src\quick_translate.rs", "rs\crates\easydict_app\tests\quick_translate_behavior.rs", "rs\crates\easydict_app\tests\cli_translate_behavior.rs")),
            (New-ValidationStep "app custom streaming contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "native_custom_streaming")),
            (New-ValidationStep "CLI Doubao local SSE contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "cli_translate_behavior", "native_doubao_cli_translate_succeeds_against_local_sse_without_worker_wording")),
            (New-ValidationStep "CLI Gemini local SSE contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "cli_translate_behavior", "native_gemini_cli_translate_succeeds_against_local_sse_without_worker_wording"))
        )
    }
    "traditional-http" = [pscustomobject]@{
        Description = "Rust-native traditional HTTP providers, Bing two-phase route, and CLI no CompatHost contracts."
        Steps = @(
            (New-ValidationStep "format traditional HTTP slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\traditional_http.rs", "rs\crates\easydict_app\src\quick_translate.rs", "rs\crates\easydict_app\src\bin\easydict_cli.rs", "rs\crates\easydict_app\tests\traditional_http_behavior.rs", "rs\crates\easydict_app\tests\quick_translate_behavior.rs", "rs\crates\easydict_app\tests\cli_translate_behavior.rs")),
            (New-ValidationStep "traditional HTTP planner/parser/preflight contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "traditional_http_behavior")),
            (New-ValidationStep "Quick Translate traditional HTTP providers" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "native_traditional_http")),
            (New-ValidationStep "Quick Translate Bing two-phase provider" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "native_bing")),
            (New-ValidationStep "CLI traditional providers avoid worker/CompatHost wording" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "cli_translate_behavior", "without_worker_or_compat_host_wording"))
        )
    }
    "foundry-local" = [pscustomobject]@{
        Description = "Foundry Local prepare lifecycle, app-visible diagnostics, and native control-plane contracts."
        Steps = @(
            (New-ValidationStep "format Foundry Local slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\state.rs", "rs\crates\easydict_app\src\quick_translate.rs", "rs\crates\easydict_app\src\long_document.rs", "rs\crates\easydict_app\src\bin\easydict_cli.rs", "rs\crates\easydict_app\src\lib.rs", "rs\crates\easydict_app\tests\quick_translate_behavior.rs", "rs\crates\easydict_app\tests\long_document_behavior.rs", "rs\crates\easydict_app\tests\cli_translate_behavior.rs")),
            (New-ValidationStep "app Foundry Local prepare state/lifecycle" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "app_start_foundry_local")),
            (New-ValidationStep "Quick Translate Auto Foundry route diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "auto_foundry_local")),
            (New-ValidationStep "Quick Translate packaged Auto LocalAI stale app-dir boundary" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "packaged_auto_local_ai_with_stale_dotnet_payload_fails_locally_without_worker_probe")),
            (New-ValidationStep "CLI Auto Foundry route diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "cli_translate_behavior", "auto_local_ai_cli")),
            (New-ValidationStep "LongDoc Auto Foundry route diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "long_document_behavior", "auto_foundry_local_long_document")),
            (New-ValidationStep "OpenAI-compatible Foundry Local prepare contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "openai_compatible_behavior", "foundry_local_prepare"))
        )
    }
    "openvino-download" = [pscustomobject]@{
        Description = "OpenVINO/NLLB native asset download contracts and app-visible diagnostics."
        Steps = @(
            (New-ValidationStep "format OpenVINO download slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\state.rs", "rs\crates\easydict_app\tests\openvino_download_behavior.rs")),
            (New-ValidationStep "OpenVINO download contracts and diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "openvino_download_behavior", "openvino_"))
        )
    }
    "windows-ai-prepare" = [pscustomobject]@{
        Description = "WindowsAI/Phi Silica prepare lifecycle, app status mapping, and lib-owned prepare contracts."
        Steps = @(
            (New-ValidationStep "format WindowsAI prepare slice" @("rustfmt", "--edition", "2021", "--check", "lib\easydict-windows-ai\src\lib.rs", "rs\crates\easydict_app\src\state.rs", "rs\crates\easydict_app\tests\quick_translate_behavior.rs")),
            (New-ValidationStep "app WindowsAI prepare state/lifecycle" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "app_windows_ai_prepare")),
            (New-ValidationStep "WindowsAI lib prepare contract" @("cargo", "test", "--manifest-path", "lib\easydict-windows-ai\Cargo.toml", "prepare_"))
        )
    }
    "browser-support" = [pscustomobject]@{
        Description = "Rust-owned browser native-messaging registrar routing and app-visible diagnostics."
        Steps = @(
            (New-ValidationStep "browser support app diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "browser_support")),
            (New-ValidationStep "browser registrar rs host boundary" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "browser_registrar_behavior", "browser_registrar_defaults_to_rs_native_host_without_overwriting_legacy_host")),
            (New-ValidationStep "extension does not fall back to legacy host" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "browser_registrar_behavior", "browser_extension_defaults_to_rs_native_host_without_legacy_fallback"))
        )
    }
    "native-bridge" = [pscustomobject]@{
        Description = "Rust-owned Native Messaging frame parsing, rs OCR named event signaling, and no legacy .NET bridge wording."
        Steps = @(
            (New-ValidationStep "format native bridge slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\native_bridge.rs", "rs\crates\easydict_app\src\bin\easydict_native_bridge.rs", "rs\crates\easydict_app\tests\native_bridge_behavior.rs")),
            (New-ValidationStep "native bridge frame parser and binary contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "native_bridge_behavior"))
        )
    }
    "protocol-facade" = [pscustomobject]@{
        Description = "Default Rust protocol DTO facade plus retained-worker IPC feature-gating contracts."
        Steps = @(
            (New-ValidationStep "format protocol facade slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\protocol.rs", "rs\crates\easydict_app\src\protocol_core.rs", "rs\crates\easydict_app\src\compat_protocol.rs", "rs\crates\easydict_app\tests\protocol_behavior.rs", "rs\crates\easydict_app\tests\default_api_boundary_behavior.rs")),
            (New-ValidationStep "default protocol facade DTO contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "protocol_behavior")),
            (New-ValidationStep "retained worker protocol feature contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--features", "retained-dotnet-workers", "--test", "protocol_behavior")),
            (New-ValidationStep "crate-root retained protocol exports stay feature-gated" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "default_api_boundary_behavior", "crate_root_retained_worker_exports_are_feature_gated")),
            (New-ValidationStep "default manifests do not enable retained protocol workers" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "default_api_boundary_behavior", "default_cargo_features_do_not_enable_retained_dotnet_workers"))
        )
    }
    "input-actions" = [pscustomobject]@{
        Description = "Rust-owned clipboard read/write/monitor and text insertion side-effect contracts."
        Steps = @(
            (New-ValidationStep "format input action slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\clipboard.rs", "rs\crates\easydict_app\src\text_insertion.rs", "rs\crates\easydict_app\src\state.rs", "rs\crates\easydict_app\tests\quick_translate_behavior.rs", "rs\crates\easydict_app\tests\ocr_behavior.rs")),
            (New-ValidationStep "clipboard facade and monitor contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--lib", "clipboard")),
            (New-ValidationStep "text insertion facade contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--lib", "text_insertion")),
            (New-ValidationStep "quick translate clipboard actions" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "clipboard")),
            (New-ValidationStep "quick translate text insertion actions" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "text_insertion")),
            (New-ValidationStep "result action side effects" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "result_action")),
            (New-ValidationStep "silent OCR clipboard task surface" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "ocr_behavior", "silent_ocr_outcome_uses_rust_clipboard_task"))
        )
    }
    "tts" = [pscustomobject]@{
        Description = "Rust-owned Windows SAPI TTS helper, app facade, speak actions, and legacy PowerShell boundary."
        Steps = @(
            (New-ValidationStep "format TTS slice" @("rustfmt", "--edition", "2021", "--check", "lib\easydict-windows-tts\src\lib.rs", "rs\crates\easydict_app\src\tts.rs", "rs\crates\easydict_app\src\lib.rs", "rs\crates\easydict_app\src\state.rs", "rs\crates\easydict_app\tests\quick_translate_behavior.rs", "rs\crates\easydict_app\tests\default_api_boundary_behavior.rs")),
            (New-ValidationStep "Windows SAPI TTS helper contracts" @("cargo", "test", "--manifest-path", "lib\easydict-windows-tts\Cargo.toml")),
            (New-ValidationStep "app TTS facade contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--lib", "tts")),
            (New-ValidationStep "quick translate speak actions" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "speak")),
            (New-ValidationStep "auto-play translation speech routing" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "auto_play_translation")),
            (New-ValidationStep "legacy PowerShell TTS features stay disabled" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "default_api_boundary_behavior", "legacy_powershell"))
        )
    }
    "file-dialog" = [pscustomobject]@{
        Description = "Native file/folder dialog Result facade and app-level error surfacing."
        Steps = @(
            (New-ValidationStep "file dialog result facade" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--lib", "file_dialog::tests::dialog_result_api_preserves_backend_error_path")),
            (New-ValidationStep "MDX import dialog diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "mdx_dictionary_dialog_error")),
            (New-ValidationStep "LongDoc browse dialog diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "long_document_behavior", "long_document_file_dialog_error"))
        )
    }
    "text-selection" = [pscustomobject]@{
        Description = "UIA/clipboard selected-text capture diagnostics and quick-translate task plumbing."
        Steps = @(
            (New-ValidationStep "backend diagnostic preservation" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "text_selection_behavior", "capture_backend_preserves")),
            (New-ValidationStep "quick translate text-selection capture" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "text_selection_capture")),
            (New-ValidationStep "selected-text capture task" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "selected_text_capture_task")),
            (New-ValidationStep "mouse selection capture result mapping" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "mouse_selection_capture_result_maps_to_existing_pop_button_message"))
        )
    }
    "mouse-selection" = [pscustomobject]@{
        Description = "Rust-owned low-level mouse/keyboard hook helper, mouse-selection reducer/producer, and app runtime mapping."
        Steps = @(
            (New-ValidationStep "format mouse-selection slice" @("rustfmt", "--edition", "2021", "--check", "lib\easydict-windows-text-selection\src\lib.rs", "rs\crates\easydict_app\src\mouse_selection.rs", "rs\crates\easydict_app\src\lib.rs", "rs\crates\easydict_app\src\state.rs", "rs\crates\easydict_app\tests\mouse_selection_behavior.rs", "rs\crates\easydict_app\tests\quick_translate_behavior.rs")),
            (New-ValidationStep "Windows low-level hook helper contracts" @("cargo", "test", "--manifest-path", "lib\easydict-windows-text-selection\Cargo.toml")),
            (New-ValidationStep "mouse-selection reducer and producer contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "mouse_selection_behavior")),
            (New-ValidationStep "quick translate mouse-selection runtime mapping" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "mouse_selection"))
        )
    }
    "ocr-diagnostics" = [pscustomobject]@{
        Description = "OCR HTTP parser failures, native screen capture errors, and window-snapshot diagnostics."
        Steps = @(
            (New-ValidationStep "HTTP backend parse diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "ocr_behavior", "ocr_http_provider")),
            (New-ValidationStep "screen capture result facade" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--lib", "screen_capture_native::tests::capture_region_result_preserves_native_error_diagnostics")),
            (New-ValidationStep "app OCR capture diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "ocr_behavior", "app_ocr_capture_failure_surfaces_native_screen_capture_error")),
            (New-ValidationStep "window snapshot diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "ocr_behavior", "capture_window_snapshot_failure_preserves_manual_region_capture")),
            (New-ValidationStep "snapshot startup contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "ocr_behavior", "ocr_hotkey_captures_window_snapshot_for_double_click_detection")),
            (New-ValidationStep "native capture helper task surface" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "ocr_behavior", "app_ocr_screen_capture_uses_native_helper_instead_of_winfluent_task_surface"))
        )
    }
    "longdoc-layout" = [pscustomobject]@{
        Description = "LongDoc DocLayout-YOLO/TATR/Vision layout configuration and backend diagnostics."
        Steps = @(
            (New-ValidationStep "format LongDoc layout slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\long_document.rs", "rs\crates\easydict_app\src\vision_layout.rs", "rs\crates\easydict_app\src\table_structure_onnx.rs", "rs\crates\easydict_app\tests\layout_model_download_behavior.rs", "rs\crates\easydict_app\tests\vision_layout_behavior.rs", "rs\crates\easydict_app\tests\table_structure_onnx_behavior.rs", "rs\crates\easydict_app\tests\long_document_behavior.rs")),
            (New-ValidationStep "layout model download contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "layout_model_download_behavior", "layout_model")),
            (New-ValidationStep "vision layout request/parser/executor contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "vision_layout_behavior", "vision_layout")),
            (New-ValidationStep "TATR ONNX helper contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "table_structure_onnx_behavior", "tatr_onnx")),
            (New-ValidationStep "explicit VisionLLM config errors" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--lib", "explicit_vision_layout_config_surfaces_missing_required_settings")),
            (New-ValidationStep "vision backend page diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--lib", "vision_layout_backend_errors_preserve_page_number_and_provider_message")),
            (New-ValidationStep "explicit TATR setup diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--lib", "tatr"))
        )
    }
    "local-dictionary-suggestions" = [pscustomobject]@{
        Description = "Rust-native MDX suggestion index routing, encrypted dictionaries, and no CompatHost fallback."
        Steps = @(
            (New-ValidationStep "local dictionary suggestion runner" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "local_dictionary_suggestion_runner")),
            (New-ValidationStep "encrypted native suggestions" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "encrypted_local_dictionary_suggestions")),
            (New-ValidationStep "mixed native prefix before bridge" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "mixed_local_dictionary_suggestions")),
            (New-ValidationStep "stale app-dir markers ignored" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "local_dictionary_suggestions_app_dir_ignores_stale_dotnet_payload_markers"))
        )
    }
    "rs-portable-release" = [pscustomobject]@{
        Description = "First rs portable release/default packaging gates that keep retained .NET payloads out."
        Steps = @(
            (New-ValidationStep "release defaults to rs portable" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_packager", "--test", "release_contract_behavior", "migration_list_acceptance_defaults_to_rs_portable_before_legacy_dotnet")),
            (New-ValidationStep "default packager surface is rust-only" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_packager", "--test", "release_contract_behavior", "rs_portable_release_default_packager_help_exposes_only_rs_portable_no_runtime_paths")),
            (New-ValidationStep "zip validation excludes retained runtime" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_packager", "--test", "release_contract_behavior", "pack_rs_portable_zip_extracts_to_cli_smoke_without_dotnet_or_powershell"))
        )
    }
    "rust-only-boundary" = [pscustomobject]@{
        Description = "Fast default-rs no-runtime boundary checks before closing core migration slices."
        Steps = @(
            (New-ValidationStep "runtime policy defaults stay rust-only" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--lib", "runtime_policy")),
            (New-ValidationStep "default app source has no retained runtime entries" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "default_api_boundary_behavior", "default_process_spawn_surface_has_no_retained_dotnet_runtime_entries")),
            (New-ValidationStep "default app process spawn allowlist stays narrow" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "default_api_boundary_behavior", "default_process_spawn_surface_only_allows_foundry_local_cli_boundary")),
            (New-ValidationStep "default CLI translate stays native" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "cli_translate_behavior", "default_translate_uses_native_google_without_retained_runtime_or_shell_wording")),
            (New-ValidationStep "CLI LocalAI no-worker boundary" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "cli_translate_behavior", "local_ai_cli")),
            (New-ValidationStep "GUI LocalAI stale app-dir stays native" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "packaged_auto_local_ai_with_stale_dotnet_payload_fails_locally_without_worker_probe")),
            (New-ValidationStep "LongDoc CLI stale payload boundaries" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "long_document_cli_behavior", "stale_dotnet_payload")),
            (New-ValidationStep "LongDoc CLI target-auto no-worker boundary" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "long_document_cli_behavior", "target_auto_fails_before_native_or_retained_worker_lookup")),
            (New-ValidationStep "LongDoc current app-dir ignores hybrid env" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "long_document_behavior", "current_app_dir_runner_ignores_hybrid_runtime_profile_before_worker_probe"))
        )
    }
}

$scriptDir = Split-Path -Parent $PSCommandPath
$repoRoot = Resolve-Path -LiteralPath (Join-Path $scriptDir "..\..")

$parallelUiFiles = @(
    "lib/winfluent-rs/crates/win_fluent/src/a11y.rs",
    "lib/winfluent-rs/crates/win_fluent/src/diff.rs",
    "lib/winfluent-rs/crates/win_fluent/src/schema.rs",
    "lib/winfluent-rs/crates/win_fluent/src/theme.rs",
    "lib/winfluent-rs/crates/win_fluent/src/view.rs",
    "lib/winfluent-rs/crates/win_fluent_backend_iced/src/lib.rs",
    "lib/winfluent-rs/crates/win_fluent_testkit/src/lib.rs",
    "rs/crates/easydict_app/src/theme.rs",
    "rs/crates/easydict_app/src/ui.rs",
    "rs/crates/easydict_app/tests/ui_contract.rs",
    "rs/crates/easydict_ui_parity_analyzer/Cargo.toml",
    "rs/crates/easydict_ui_parity_analyzer/src/bin/easydict_ui_code_parity.rs",
    "rs/crates/easydict_ui_parity_analyzer/src/lib.rs",
    "rs/scripts/Compare-DotnetRustUiCode.ps1",
    "rs/scripts/Measure-SettingsServicesExpanderColors.ps1"
)
$parallelCargoLockFiles = @(
    "rs/Cargo.lock"
)
$generatedCargoLockFiles = @(
    "lib/easydict-windows-credentials/Cargo.lock"
)

$profileRecommendations = [ordered]@{
    "core-validation-tooling" = [pscustomobject]@{
        PathPatterns = @(
            "rs/scripts/Invoke-RsCoreSliceValidation.ps1",
            "rs/scripts/Test-RsCoreSliceValidation.ps1"
        )
        DiffPatterns = @("RunRecommendedProfiles", "DryRun", "validationProfiles", "profileRecommendations", "RecommendProfiles")
    }
    "desktop-settings" = [pscustomobject]@{
        PathPatterns = @(
            "lib/easydict-windows-shell/**",
            "rs/crates/easydict_app/src/desktop*.rs"
        )
        DiffPatterns = @("DesktopShell", "DesktopIntegration", "desktop_shell", "desktop_integration", "settings_save", "SettingsSave")
    }
    "settings-credentials" = [pscustomobject]@{
        PathPatterns = @(
            "lib/easydict-windows-credentials/**",
            "rs/crates/easydict_app/src/credential_protection.rs",
            "rs/crates/easydict_app/src/settings_storage.rs",
            "rs/crates/easydict_app/src/settings_migration.rs",
            "rs/crates/easydict_app/tests/credential_protection_behavior.rs",
            "rs/crates/easydict_app/tests/settings_storage_behavior.rs",
            "rs/crates/easydict_app/tests/settings_migration_behavior.rs"
        )
        DiffPatterns = @("SettingsStorage", "settings_storage", "settings_migration", "CredentialProtection", "credential_protection", "edcred1", "edloc1", "LocalSettingsCredential", "MachineGuid", "UseLocalAiWorker", "UseLongDocWorker", "UseOcrWorker")
    }
    "builtin-ai-registration" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/openai_compatible.rs",
            "rs/crates/easydict_app/tests/openai_compatible_behavior.rs"
        )
        DiffPatterns = @("BuiltInAi", "Built-in AI", "builtin_device", "device_registration")
    }
    "custom-streaming" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/custom_streaming.rs",
            "rs/crates/easydict_app/src/quick_translate.rs",
            "rs/crates/easydict_app/tests/quick_translate_behavior.rs",
            "rs/crates/easydict_app/tests/cli_translate_behavior.rs"
        )
        DiffPatterns = @("CustomStreaming", "custom_streaming", "Gemini", "gemini", "Doubao", "doubao", "response.output_text.delta", "streamGenerateContent")
    }
    "traditional-http" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/traditional_http.rs",
            "rs/crates/easydict_app/tests/traditional_http_behavior.rs",
            "rs/crates/easydict_app/tests/quick_translate_behavior.rs",
            "rs/crates/easydict_app/tests/cli_translate_behavior.rs"
        )
        DiffPatterns = @("TraditionalHttp", "traditional_http", "GoogleWeb", "google_web", "Bing", "bing", "DeepL", "deepl", "Youdao", "youdao", "Caiyun", "caiyun", "NiuTrans", "niutrans", "Volcano", "volcano", "Linguee", "linguee", "without_worker_or_compat_host_wording")
    }
    "foundry-local" = [pscustomobject]@{
        PathPatterns = @(
            "lib/easydict-foundry-local/**",
            "rs/crates/easydict_app/src/quick_translate.rs",
            "rs/crates/easydict_app/src/long_document.rs",
            "rs/crates/easydict_app/src/bin/easydict_cli.rs",
            "rs/crates/easydict_app/src/openai_compatible.rs",
            "rs/crates/easydict_app/tests/openai_compatible_behavior.rs",
            "rs/crates/easydict_app/tests/quick_translate_behavior.rs",
            "rs/crates/easydict_app/tests/long_document_behavior.rs",
            "rs/crates/easydict_app/tests/cli_translate_behavior.rs"
        )
        DiffPatterns = @("FoundryLocal", "Foundry Local", "foundry_local", "foundry-local", "Foundry probe", "Foundry route", "auto_foundry_local_native_probe_request")
    }
    "openvino-download" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/openvino*.rs",
            "rs/crates/easydict_app/tests/openvino_download_behavior.rs"
        )
        DiffPatterns = @("OpenVino", "OpenVINO", "openvino_", "open-vino")
    }
    "windows-ai-prepare" = [pscustomobject]@{
        PathPatterns = @(
            "lib/easydict-windows-ai/**"
        )
        DiffPatterns = @("WindowsAi", "WindowsAI", "windows_ai", "windows-ai", "Phi Silica", "phi_silica")
    }
    "browser-support" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_browser_registrar/**",
            "rs/crates/easydict_app/tests/browser_registrar_behavior.rs",
            "browser-extension/**"
        )
        DiffPatterns = @("BrowserSupport", "browser_support", "browser_registrar", "native-messaging", "NativeMessaging")
    }
    "native-bridge" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/native_bridge.rs",
            "rs/crates/easydict_app/src/bin/easydict_native_bridge.rs",
            "rs/crates/easydict_app/tests/native_bridge_behavior.rs"
        )
        DiffPatterns = @("NativeBridge", "native_bridge", "easydict-native-bridge", "easydict_native_bridge", "run_native_bridge", "OCR_TRANSLATE_EVENT_NAME")
    }
    "protocol-facade" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/protocol.rs",
            "rs/crates/easydict_app/src/protocol_core.rs",
            "rs/crates/easydict_app/src/compat_protocol.rs",
            "rs/crates/easydict_app/tests/protocol_behavior.rs"
        )
        DiffPatterns = @("protocol_core", "compat_protocol", "SettingsSnapshot", "TranslateParams", "TranslateDocumentParams", "MdxLookupParams", "WORKER_PROTOCOL_VERSION_CURRENT", "retained-dotnet-workers")
    }
    "input-actions" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/clipboard.rs",
            "rs/crates/easydict_app/src/text_insertion.rs",
            "rs/crates/easydict_app/tests/quick_translate_behavior.rs",
            "rs/crates/easydict_app/tests/ocr_behavior.rs"
        )
        DiffPatterns = @("ClipboardOperation", "clipboard_monitor", "monitor_clipboard", "TextInsertion", "text_insertion", "result_action", "silent_ocr_outcome_uses_rust_clipboard_task", "ReadClipboardText", "WriteClipboardText", "PlatformCommand::InsertText")
    }
    "tts" = [pscustomobject]@{
        PathPatterns = @(
            "lib/easydict-windows-tts/**",
            "rs/crates/easydict_app/src/tts.rs",
            "rs/crates/easydict_app/tests/default_api_boundary_behavior.rs",
            "rs/crates/easydict_app/tests/quick_translate_behavior.rs"
        )
        DiffPatterns = @("TextToSpeech", "Text-to-Speech", "SpeakResult", "speak_text", "AutoPlayTranslation", "auto_play_translation", "tts", "TTS", "legacy-powershell-tts", "System.Speech", "PlatformCommand::SpeakText")
    }
    "file-dialog" = [pscustomobject]@{
        PathPatterns = @(
            "lib/easydict-windows-dialogs/**",
            "rs/crates/easydict_app/src/file_dialog.rs"
        )
        DiffPatterns = @("FileDialog", "file_dialog", "dialog_result", "MdxDictionaryDialog", "LongDocumentBrowse")
    }
    "text-selection" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/text_selection.rs",
            "rs/crates/easydict_app/tests/text_selection_behavior.rs"
        )
        DiffPatterns = @("TextSelection", "text_selection", "selected_text", "capture_native_selected_text", "UIA", "clipboard backend")
    }
    "mouse-selection" = [pscustomobject]@{
        PathPatterns = @(
            "lib/easydict-windows-text-selection/**",
            "rs/crates/easydict_app/src/mouse_selection.rs",
            "rs/crates/easydict_app/tests/mouse_selection_behavior.rs"
        )
        DiffPatterns = @("MouseSelection", "mouse_selection", "WH_MOUSE_LL", "WH_KEYBOARD_LL", "MouseSelectionProducer", "MouseSelectionInputHookEvent", "MouseSelectionPendingMultiClickElapsed", "EASYDICT_SYNTHETIC_KEY")
    }
    "ocr-diagnostics" = [pscustomobject]@{
        PathPatterns = @(
            "lib/easydict-windows-screen-capture/**",
            "rs/crates/easydict_app/src/ocr.rs",
            "rs/crates/easydict_app/src/screen_capture_native.rs",
            "rs/crates/easydict_app/tests/ocr_behavior.rs"
        )
        DiffPatterns = @("Ocr", "OCR", "screen_capture", "ScreenCapture", "CaptureWindowsSnapshot", "ocr_http_provider")
    }
    "longdoc-layout" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/long_document.rs",
            "rs/crates/easydict_app/src/table_structure_onnx.rs",
            "rs/crates/easydict_app/tests/layout_model_download_behavior.rs",
            "rs/crates/easydict_app/tests/table_structure_onnx_behavior.rs",
            "rs/crates/easydict_app/tests/vision_layout_behavior.rs",
            "rs/crates/easydict_app/tests/long_document_behavior.rs"
        )
        DiffPatterns = @("DocLayout", "TATR", "VisionLLM", "vision_layout", "layout_model", "recognize_bgra", "LongDocumentBackendError")
    }
    "local-dictionary-suggestions" = [pscustomobject]@{
        PathPatterns = @(
            "lib/rs-mdict/**",
            "rs/crates/easydict_app/src/mdx_native.rs",
            "rs/crates/easydict_app/tests/mdx_native_behavior.rs"
        )
        DiffPatterns = @("MDX", "MDD", "mdx", "mdd", "dictionary_suggestion", "local_dictionary")
    }
    "rs-portable-release" = [pscustomobject]@{
        PathPatterns = @(
            ".github/workflows/release.yml",
            "rs/crates/easydict_packager/**",
            "rs/scripts/Package-Portable.ps1",
            "rs/README.md"
        )
        DiffPatterns = @("pack-rs-portable", "rs_portable", "validate-rs-portable", "portable ZIP", "release_flavor")
    }
    "rust-only-boundary" = [pscustomobject]@{
        PathPatterns = @(
            ".github/workflows/**",
            "dotnet/scripts/**",
            "dotnet/Makefile",
            "rs/crates/easydict_app/src/runtime_policy.rs",
            "rs/crates/easydict_app/src/bin/easydict_long_doc.rs",
            "rs/crates/easydict_app/src/long_document_cli.rs",
            "rs/crates/easydict_app/tests/default_api_boundary_behavior.rs",
            "rs/crates/easydict_app/tests/cli_translate_behavior.rs",
            "rs/crates/easydict_app/tests/long_document_cli_behavior.rs",
            "rs/crates/easydict_app/tests/long_document_behavior.rs",
            "rs/crates/easydict_packager/**",
            "lib/easydict-runtime-guards/**",
            "lib/easydict-foundry-local/**"
        )
        DiffPatterns = @("runtime_policy", "retained-dotnet", "retained worker", "CompatHost", "dotnet.exe", "DOTNET_ROOT", "hostfxr", "PowerShell", "pwsh", "process::Command", "WorkerCommand")
    }
}

function Normalize-RepoRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $normalized = $Path.Replace("\", "/")
    while ($normalized.StartsWith("./", [System.StringComparison]::Ordinal)) {
        $normalized = $normalized.Substring(2)
    }
    $normalized.TrimStart("/")
}

function Expand-PathList {
    param(
        [string[]]$Paths
    )

    @($Paths | ForEach-Object {
            $_ -split "," | ForEach-Object { $_.Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        })
}

function Test-PathPattern {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Pattern
    )

    $normalizedPath = Normalize-RepoRelativePath $Path
    $normalizedPattern = (Normalize-RepoRelativePath $Pattern).Replace("**", "*")
    $normalizedPath -like $normalizedPattern
}

function Test-PathMatchesAnyPattern {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string[]]$Patterns
    )

    foreach ($pattern in @($Patterns)) {
        if (Test-PathPattern -Path $Path -Pattern $pattern) {
            return $true
        }
    }

    $false
}

function Get-GstepDirtyPaths {
    param(
        [Parameter(Mandatory = $true)]
        [string]$From,

        [Parameter(Mandatory = $true)]
        [string]$To
    )

    $diffText = (& gstep diff $From $To "--json" | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "gstep diff $From $To --json failed with exit code $LASTEXITCODE"
    }

    $diff = $diffText | ConvertFrom-Json
    @($diff.files | ForEach-Object { Normalize-RepoRelativePath $_.path })
}

function Get-GstepDiffText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$From,

        [Parameter(Mandatory = $true)]
        [string]$To
    )

    $diffText = (& gstep diff $From $To | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "gstep diff $From $To failed with exit code $LASTEXITCODE"
    }
    $diffText
}

function Get-RecommendationDiffText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DiffText
    )

    $ignoredPaths = @($parallelUiFiles) + @($parallelCargoLockFiles) + @($generatedCargoLockFiles) + @(
        "experience.md",
        "migration-list.md",
        "refactor-progress.md"
    )
    $ignoredPaths = @($ignoredPaths | ForEach-Object { Normalize-RepoRelativePath $_ })

    $selectedLines = New-Object System.Collections.Generic.List[string]
    $includeCurrentFile = $false
    foreach ($line in ($DiffText -split "`r?`n")) {
        if ($line -match '^diff --git a/(.+?) b/(.+)$') {
            $currentPath = Normalize-RepoRelativePath $matches[2]
            $includeCurrentFile = $ignoredPaths -notcontains $currentPath
        }

        if ($includeCurrentFile -and
            (($line.StartsWith("+", [System.StringComparison]::Ordinal) -and
                    -not $line.StartsWith("+++", [System.StringComparison]::Ordinal)) -or
                ($line.StartsWith("-", [System.StringComparison]::Ordinal) -and
                    -not $line.StartsWith("---", [System.StringComparison]::Ordinal)))) {
            $selectedLines.Add($line)
        }
    }

    $selectedLines -join "`n"
}

function Get-ProfileRecommendations {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Paths,

        [string]$DiffText = ""
    )

    $normalizedParallel = @($parallelUiFiles | ForEach-Object { Normalize-RepoRelativePath $_ })
    $ignoredCargoLockFiles = @(@($parallelCargoLockFiles) + @($generatedCargoLockFiles) | ForEach-Object { Normalize-RepoRelativePath $_ })
    $nonProfilePaths = @(
        "experience.md",
        "migration-list.md",
        "refactor-progress.md"
    )
    $nonProfilePaths = @($nonProfilePaths | ForEach-Object { Normalize-RepoRelativePath $_ })
    $normalizedPaths = @(Expand-PathList $Paths | ForEach-Object { Normalize-RepoRelativePath $_ } | Select-Object -Unique)
    $ignoredPaths = @($normalizedPaths | Where-Object { $normalizedParallel -contains $_ -or $ignoredCargoLockFiles -contains $_ -or $nonProfilePaths -contains $_ })
    $corePaths = @($normalizedPaths | Where-Object { $normalizedParallel -notcontains $_ -and $ignoredCargoLockFiles -notcontains $_ -and $nonProfilePaths -notcontains $_ })
    $results = @()
    $toolingRules = $profileRecommendations["core-validation-tooling"]
    $onlyValidationToolingPaths = $corePaths.Count -gt 0 -and @($corePaths | Where-Object {
            -not (Test-PathMatchesAnyPattern -Path $_ -Patterns $toolingRules.PathPatterns)
        }).Count -eq 0

    foreach ($profileName in $profileRecommendations.Keys) {
        $rules = $profileRecommendations[$profileName]
        $pathMatches = @()
        foreach ($path in $corePaths) {
            if (Test-PathMatchesAnyPattern -Path $path -Patterns $rules.PathPatterns) {
                $pathMatches += $path
            }
        }

        $textMatches = @()
        if ((-not $onlyValidationToolingPaths -or $profileName -eq "core-validation-tooling") -and -not [string]::IsNullOrWhiteSpace($DiffText)) {
            foreach ($pattern in @($rules.DiffPatterns)) {
                if ($DiffText.IndexOf($pattern, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $textMatches += $pattern
                }
            }
        }

        $pathMatches = @($pathMatches | Select-Object -Unique)
        $textMatches = @($textMatches | Select-Object -Unique)
        $score = ($pathMatches.Count * 3) + $textMatches.Count
        if ($score -gt 0) {
            $results += [pscustomobject]@{
                Profile = $profileName
                Score = $score
                PathMatches = $pathMatches
                TextMatches = $textMatches
            }
        }
    }

    [pscustomobject]@{
        CorePaths = $corePaths
        IgnoredPaths = $ignoredPaths
        Results = @($results | Sort-Object -Property @{ Expression = "Score"; Descending = $true }, @{ Expression = "Profile"; Descending = $false })
    }
}

function Show-ProfileRecommendations {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Recommendation
    )

    if ($Recommendation.IgnoredPaths.Count -gt 0) {
        Write-Host "Ignored parallel UI/parity or profile-exempt path(s):"
        foreach ($path in $Recommendation.IgnoredPaths) {
            Write-Host "  - $path"
        }
    }

    if ($Recommendation.CorePaths.Count -gt 0) {
        Write-Host "Core path(s) considered:"
        foreach ($path in $Recommendation.CorePaths) {
            Write-Host "  - $path"
        }
    }
    else {
        Write-Host "No non-parallel core paths were found."
    }

    if ($Recommendation.Results.Count -eq 0) {
        Write-Host "No validation profile matched. Use a custom single command or add a profile plus recommendation rules for repeated lanes."
        return
    }

    Write-Host "Recommended validation profile(s):"
    foreach ($result in $Recommendation.Results) {
        $profileDefinition = $validationProfiles[$result.Profile]
        Write-Host "  - $($result.Profile) (score $($result.Score))"
        Write-Host "    $($profileDefinition.Description)"
        if ($result.PathMatches.Count -gt 0) {
            Write-Host "    path: $($result.PathMatches -join ', ')"
        }
        if ($result.TextMatches.Count -gt 0) {
            Write-Host "    diff: $($result.TextMatches -join ', ')"
        }
        Write-Host "    run: powershell -NoProfile -ExecutionPolicy Bypass -File rs\scripts\Invoke-RsCoreSliceValidation.ps1 -Profile $($result.Profile)"
    }
}

function Get-CurrentProfileRecommendation {
    param(
        [string[]]$ChangedPath,

        [Parameter(Mandatory = $true)]
        [string]$DiffFrom,

        [Parameter(Mandatory = $true)]
        [string]$DiffTo
    )

    if ($ChangedPath.Count -gt 0) {
        $recommendationPaths = @(Expand-PathList $ChangedPath | ForEach-Object { Normalize-RepoRelativePath $_ })
        $recommendationDiff = ($recommendationPaths -join "`n")
    }
    else {
        $recommendationPaths = Get-GstepDirtyPaths -From $DiffFrom -To $DiffTo
        $recommendationDiff = Get-RecommendationDiffText -DiffText (Get-GstepDiffText -From $DiffFrom -To $DiffTo)
    }

    Get-ProfileRecommendations -Paths $recommendationPaths -DiffText $recommendationDiff
}

if ($RecommendProfiles) {
    if ($ListProfiles -or $RunRecommendedProfiles -or $DryRun -or $Command.Count -ne 0 -or -not [string]::IsNullOrWhiteSpace($Profile)) {
        throw "-RecommendProfiles cannot be combined with -ListProfiles, -RunRecommendedProfiles, -DryRun, -Profile, or a validation command."
    }
    if ($MaxRecommendedProfiles -ne 1) {
        throw "-MaxRecommendedProfiles is only valid with -RunRecommendedProfiles."
    }

    Set-Location $repoRoot
    $recommendation = Get-CurrentProfileRecommendation -ChangedPath $ChangedPath -DiffFrom $DiffFrom -DiffTo $DiffTo
    Show-ProfileRecommendations -Recommendation $recommendation
    exit 0
}

if ($ListProfiles) {
    if ($RunRecommendedProfiles -or $DryRun -or $Command.Count -ne 0 -or -not [string]::IsNullOrWhiteSpace($Profile) -or $ChangedPath.Count -ne 0 -or $DiffFrom -ne "gstep:@" -or $DiffTo -ne "worktree") {
        throw "-ListProfiles cannot be combined with -RunRecommendedProfiles, -DryRun, -Profile, -ChangedPath, diff selectors, or a validation command."
    }
    if ($MaxRecommendedProfiles -ne 1) {
        throw "-MaxRecommendedProfiles is only valid with -RunRecommendedProfiles."
    }

    foreach ($profileName in $validationProfiles.Keys) {
        $profileDefinition = $validationProfiles[$profileName]
        Write-Host $profileName
        Write-Host "  $($profileDefinition.Description)"
        foreach ($step in $profileDefinition.Steps) {
            Write-Host "  - $($step.Name): $($step.Command -join ' ')"
        }
    }
    exit 0
}

if ($ChangedPath.Count -ne 0 -or $DiffFrom -ne "gstep:@" -or $DiffTo -ne "worktree") {
    if (-not $RunRecommendedProfiles) {
        throw "-ChangedPath, -DiffFrom, and -DiffTo are only valid with -RecommendProfiles or -RunRecommendedProfiles."
    }
}

if ($MaxRecommendedProfiles -ne 1 -and -not $RunRecommendedProfiles) {
    throw "-MaxRecommendedProfiles is only valid with -RunRecommendedProfiles."
}
if ($MaxRecommendedProfiles -lt 1) {
    throw "-MaxRecommendedProfiles must be greater than or equal to 1."
}

$modeCount = 0
if (-not [string]::IsNullOrWhiteSpace($Profile)) { $modeCount += 1 }
if ($Command.Count -ne 0) { $modeCount += 1 }
if ($RunRecommendedProfiles) { $modeCount += 1 }
if ($modeCount -gt 1) {
    throw "Use only one of -Profile, -RunRecommendedProfiles, or one validation command. For custom cargo commands with flags such as '-p', pass the child command through a PowerShell argument array splat (for example, `$cmdArgs = @('cargo', 'test', '-p', 'easydict_app'); ...ps1 @cmdArgs`) so wrapper/common parameters do not capture them."
}

$validationSteps = @()
if ($RunRecommendedProfiles) {
    Set-Location $repoRoot
    $recommendation = Get-CurrentProfileRecommendation -ChangedPath $ChangedPath -DiffFrom $DiffFrom -DiffTo $DiffTo
    Show-ProfileRecommendations -Recommendation $recommendation
    if ($recommendation.Results.Count -eq 0) {
        throw "No validation profile matched; run a custom command or add a profile plus recommendation rules for this lane."
    }

    $selectedProfiles = @($recommendation.Results | Select-Object -First $MaxRecommendedProfiles)
    Write-Host "Selected recommended validation profile(s): $((@($selectedProfiles | ForEach-Object { $_.Profile })) -join ', ')"
    foreach ($selectedProfile in $selectedProfiles) {
        foreach ($step in @($validationProfiles[$selectedProfile.Profile].Steps)) {
            $validationSteps += (New-ValidationStep "$($selectedProfile.Profile) / $($step.Name)" $step.Command)
        }
    }
}
elseif (-not [string]::IsNullOrWhiteSpace($Profile)) {
    $profileKey = $Profile.Trim().ToLowerInvariant()
    if (-not $validationProfiles.Contains($profileKey)) {
        throw "Unknown validation profile '$Profile'. Use -ListProfiles to see available profiles."
    }

    $validationSteps = @($validationProfiles[$profileKey].Steps)
}
elseif ($Command.Count -ne 0) {
    $validationSteps = @((New-ValidationStep "custom" $Command))
}
else {
    throw "Provide one validation command, -Profile <name>, -RunRecommendedProfiles, -ListProfiles, or -RecommendProfiles."
}

if ($DryRun) {
    Write-Host "Dry run; validation step(s) that would run:"
    foreach ($step in $validationSteps) {
        Write-Host "  - $($step.Name): $($step.Command -join ' ')"
    }
    exit 0
}

function Invoke-GstepChecked {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & gstep @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "gstep $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Remove-TempTree {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$TempBase
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    $resolvedTempBase = (Resolve-Path -LiteralPath $TempBase).Path
    if (-not $resolvedPath.StartsWith($resolvedTempBase, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove temp path outside temp root: $resolvedPath"
    }

    Remove-Item -LiteralPath $resolvedPath -Recurse -Force
}

Set-Location $repoRoot

$tempBase = [System.IO.Path]::GetTempPath()
$tempRoot = Join-Path $tempBase ("easydict-rs-core-slice-" + [System.Guid]::NewGuid().ToString("N"))
$backupRoot = Join-Path $tempRoot "backup"
$materializedRoot = Join-Path $tempRoot "gstep-at"
$isolatedFiles = @()
$generatedCargoLockFilesAbsentBeforeRun = @($generatedCargoLockFiles | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $repoRoot $_) -PathType Leaf)
    })
$previousRustTestNocapture = $env:RUST_TEST_NOCAPTURE
$enableRustTestNocapture = $RustTestNocapture.IsPresent -or -not [string]::IsNullOrWhiteSpace($Profile)
$commandExitCode = 1
$validationMutex = [System.Threading.Mutex]::new($false, "Local\EasydictRsCoreSliceValidation")
$validationMutexAcquired = $false

try {
    Write-Host "Waiting for core validation isolation lock."
    $validationMutexAcquired = $validationMutex.WaitOne([TimeSpan]::FromMinutes(10))
    if (-not $validationMutexAcquired) {
        throw "Timed out waiting for core validation isolation lock."
    }

    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null

    if (-not $NoParallelUiIsolation) {
        if (-not (Get-Command gstep -ErrorAction SilentlyContinue)) {
            throw "gstep was not found; install or expose gstep before using UI isolation."
        }

        $diffText = (& gstep diff "gstep:@" "worktree" "--json" | Out-String)
        if ($LASTEXITCODE -ne 0) {
            throw "gstep diff gstep:@ worktree --json failed with exit code $LASTEXITCODE"
        }

        $diff = $diffText | ConvertFrom-Json
        $dirtyFiles = @($diff.files)
        $normalizedParallelUiFiles = @($parallelUiFiles | ForEach-Object { Normalize-RepoRelativePath $_ })
        $normalizedParallelCargoLockFiles = @($parallelCargoLockFiles | ForEach-Object { Normalize-RepoRelativePath $_ })
        $normalizedGeneratedCargoLockFiles = @($generatedCargoLockFiles | ForEach-Object { Normalize-RepoRelativePath $_ })
        $dirtyParallelFiles = @($dirtyFiles | Where-Object { $parallelUiFiles -contains $_.path })
        $dirtyCargoManifestFiles = @($dirtyFiles | Where-Object { $_.path -match '(^|/)Cargo\.toml$' })
        $hasParallelCargoManifestChange = @($dirtyCargoManifestFiles | Where-Object { $normalizedParallelUiFiles -contains (Normalize-RepoRelativePath $_.path) }).Count -gt 0
        $hasNonParallelCargoManifestChange = @($dirtyCargoManifestFiles | Where-Object { $normalizedParallelUiFiles -notcontains (Normalize-RepoRelativePath $_.path) }).Count -gt 0
        $dirtyParallelLockFiles = @($dirtyFiles | Where-Object { $normalizedParallelCargoLockFiles -contains (Normalize-RepoRelativePath $_.path) })
        $dirtyGeneratedLockFiles = @($dirtyFiles | Where-Object { $normalizedGeneratedCargoLockFiles -contains (Normalize-RepoRelativePath $_.path) })

        if ($dirtyParallelLockFiles.Count -gt 0) {
            $dirtyParallelLockPaths = @($dirtyParallelLockFiles | ForEach-Object { Normalize-RepoRelativePath $_.path })
            if ($hasParallelCargoManifestChange -and -not $hasNonParallelCargoManifestChange) {
                Write-Host "Treating $($dirtyParallelLockPaths -join ', ') as parallel dependency-lock drift."
                $dirtyParallelFiles = @($dirtyParallelFiles) + @($dirtyParallelLockFiles)
            }
            else {
                Write-Host "Leaving dirty $($dirtyParallelLockPaths -join ', ') in place because dependency changes are not isolated parallel UI/parity manifests."
            }
        }

        if ($dirtyGeneratedLockFiles.Count -gt 0) {
            $dirtyGeneratedLockPaths = @($dirtyGeneratedLockFiles | ForEach-Object { Normalize-RepoRelativePath $_.path })
            Write-Host "Temporarily isolating known generated dependency-lock drift: $($dirtyGeneratedLockPaths -join ', ')."
            $dirtyParallelFiles = @($dirtyParallelFiles) + @($dirtyGeneratedLockFiles)
        }

        if ($dirtyParallelFiles.Count -gt 0) {
            Write-Host "Temporarily isolating $($dirtyParallelFiles.Count) parallel UI/parity or generated file(s)."
            Invoke-GstepChecked @("materialize", "gstep:@", $materializedRoot)

            foreach ($entry in $dirtyParallelFiles) {
                $relativePath = $entry.path
                $workspacePath = Join-Path $repoRoot $relativePath
                $backupPath = Join-Path $backupRoot $relativePath
                $materializedPath = Join-Path $materializedRoot $relativePath

                if (-not (Test-Path -LiteralPath $workspacePath -PathType Leaf)) {
                    throw "Cannot back up missing workspace file: $relativePath"
                }
                $materializedFileExists = Test-Path -LiteralPath $materializedPath -PathType Leaf
                if (-not $materializedFileExists -and $entry.status -ne "A") {
                    throw "gstep:@ materialization does not contain: $relativePath"
                }

                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backupPath) | Out-Null
                Copy-Item -LiteralPath $workspacePath -Destination $backupPath -Force
                if ($materializedFileExists) {
                    Copy-Item -LiteralPath $materializedPath -Destination $workspacePath -Force
                }
                else {
                    Remove-Item -LiteralPath $workspacePath -Force
                }
                $isolatedFiles += $relativePath
            }
        }
    }

    if ($enableRustTestNocapture) {
        $env:RUST_TEST_NOCAPTURE = "1"
    }

    $commandExitCode = 0
    foreach ($step in $validationSteps) {
        $stepCommand = @($step.Command)
        $program = $stepCommand[0]
        $arguments = @($stepCommand | Select-Object -Skip 1)
        Write-Host "Running validation step [$($step.Name)]: $($stepCommand -join ' ')"
        $global:LASTEXITCODE = 0
        & $program @arguments
        $commandSucceeded = $?
        $stepExitCode = if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }
        if (-not $commandSucceeded -and $stepExitCode -eq 0) {
            $stepExitCode = 1
        }
        if ($stepExitCode -ne 0) {
            $commandExitCode = $stepExitCode
            break
        }
    }
}
finally {
    foreach ($relativePath in $isolatedFiles) {
        $workspacePath = Join-Path $repoRoot $relativePath
        $backupPath = Join-Path $backupRoot $relativePath
        if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
            Copy-Item -LiteralPath $backupPath -Destination $workspacePath -Force
        }
    }

    foreach ($relativePath in $generatedCargoLockFilesAbsentBeforeRun) {
        $workspacePath = Join-Path $repoRoot $relativePath
        if (Test-Path -LiteralPath $workspacePath -PathType Leaf) {
            Remove-Item -LiteralPath $workspacePath -Force
        }
    }

    Remove-TempTree -Path $tempRoot -TempBase $tempBase

    if ($enableRustTestNocapture) {
        if ($null -eq $previousRustTestNocapture) {
            Remove-Item Env:RUST_TEST_NOCAPTURE -ErrorAction SilentlyContinue
        }
        else {
            $env:RUST_TEST_NOCAPTURE = $previousRustTestNocapture
        }
    }

    if ($validationMutexAcquired) {
        $validationMutex.ReleaseMutex()
    }
    $validationMutex.Dispose()
}

if ($commandExitCode -ne 0) {
    exit $commandExitCode
}
