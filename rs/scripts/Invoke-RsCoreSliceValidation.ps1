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
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -RecommendProfiles -Json
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -PlanCloseOut -GstepCommitMessage "Preserve selected text diagnostics"
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -PlanCloseOut -ChangedPath rs/crates/easydict_app/src/text_selection.rs -Json
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -CloseOut
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -CloseOut -DryRun -Json
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -CloseOut -ChangedPath rs/crates/easydict_app/src/text_selection.rs -GstepCommitMessage "Preserve selected text diagnostics"
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -RunRecommendedProfiles
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -RunRecommendedProfiles -AllRecommendedProfiles -DryRun
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -RunRecommendedProfiles -DryRun -Json
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -RunRecommendedProfiles -AllRecommendedProfiles -CheckTrailingWhitespace
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -RunRecommendedProfiles -CheckTrailingWhitespace -GstepCommitMessage "Preserve selected text diagnostics"
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -Profile text-selection -DryRun
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -Profile text-selection -DryRun -Json
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -Profile text-selection -CheckTrailingWhitespace
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -Profile text-selection
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -Profile mouse-selection
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -Profile longdoc-export
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -Profile longdoc-formula
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -Profile longdoc-cli
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -Profile longdoc-script
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -Profile mdx-native
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -Profile translation-cache
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -Profile app-core-catalog
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -Profile app-preview-window
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -Profile openai-compatible
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -Profile foundry-local,rust-only-boundary
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -Profile windows-ai-native
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -Profile startup-activation
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -Profile nllb-native
    rs/scripts/Invoke-RsCoreSliceValidation.ps1 -Profile pdf-overlay
#>

[CmdletBinding(PositionalBinding = $false)]
param(
    [switch]$NoParallelUiIsolation,

    [switch]$RustTestNocapture,

    [switch]$ListProfiles,

    [switch]$RecommendProfiles,

    [switch]$Json,

    [switch]$PlanCloseOut,

    [switch]$CloseOut,

    [switch]$RunRecommendedProfiles,

    [switch]$AllRecommendedProfiles,

    [switch]$DryRun,

    [switch]$CheckTrailingWhitespace,

    [string]$GstepCommitMessage,

    [string[]]$Profile,

    [string[]]$ChangedPath,

    [string]$DiffFrom = "gstep:@",

    [string]$DiffTo = "worktree",

    [int]$MaxRecommendedProfiles = 0,

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
            (New-ValidationStep "format desktop shell and integration slice" @("rustfmt", "--edition", "2021", "--check", "lib\easydict-windows-shell\src\lib.rs", "rs\crates\easydict_app\src\desktop_shell.rs", "rs\crates\easydict_app\src\desktop_integration.rs", "rs\crates\easydict_app\src\state.rs", "rs\crates\easydict_app\src\lib.rs", "rs\crates\easydict_app\tests\quick_translate_behavior.rs", "rs\crates\easydict_app\tests\default_api_boundary_behavior.rs")),
            (New-ValidationStep "Windows shell helper contracts" @("cargo", "test", "--manifest-path", "lib\easydict-windows-shell\Cargo.toml")),
            (New-ValidationStep "desktop integration registry contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--lib", "desktop_integration")),
            (New-ValidationStep "desktop shell route ownership" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "browser_support_and_external_links_use_rust_owned_desktop_shell_helper")),
            (New-ValidationStep "desktop integration route ownership" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "uses_rust_owned_desktop_integration_helper")),
            (New-ValidationStep "desktop shell diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "desktop_shell_action")),
            (New-ValidationStep "desktop integration diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "desktop_integration_action")),
            (New-ValidationStep "settings save diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "settings_save")),
            (New-ValidationStep "default bundled helper process boundary" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "default_api_boundary_behavior", "default_bundled_helper_process_boundary_stays_inside_windows_shell_lib")),
            (New-ValidationStep "default shell URL boundary" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "default_api_boundary_behavior", "default_shell_open_url_boundary_rejects_non_web_and_retained_targets")),
            (New-ValidationStep "default app shell task boundary" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "default_api_boundary_behavior", "default_app_shell_actions_do_not_bypass_windows_shell_lib")),
            (New-ValidationStep "default desktop registry boundary scan" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "default_api_boundary_behavior", "default_desktop_registry"))
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
    "settings-runtime-status" = [pscustomobject]@{
        Description = "Settings runtime-status probes for Rust-owned layout/font/OpenVINO/Foundry/WindowsAI availability and app writeback."
        Steps = @(
            (New-ValidationStep "format settings runtime-status slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\settings_status.rs", "rs\crates\easydict_app\src\state.rs", "rs\crates\easydict_app\tests\quick_translate_behavior.rs")),
            (New-ValidationStep "settings runtime-status filesystem/provider contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--lib", "settings_status")),
            (New-ValidationStep "settings runtime-status app writeback contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "settings_runtime_status"))
        )
    }
    "app-core-catalog" = [pscustomobject]@{
        Description = "Default Rust app-data root, app-visible translation service catalog, and default no-retained-runtime service boundary."
        Steps = @(
            (New-ValidationStep "format app data and service catalog slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\app_data.rs", "rs\crates\easydict_app\src\translation_services.rs", "rs\crates\easydict_app\tests\translation_services_behavior.rs")),
            (New-ValidationStep "app data root contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--lib", "app_data")),
            (New-ValidationStep "translation service catalog contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "translation_services_behavior")),
            (New-ValidationStep "default CLI translate catalog boundary" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "cli_translate_behavior", "default_translate_uses_native_google_without_retained_runtime_or_shell_wording")),
            (New-ValidationStep "default process spawn no-runtime boundary" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "default_api_boundary_behavior", "default_process_spawn_surface_has_no_retained_dotnet_runtime_entries"))
        )
    }
    "app-preview-window" = [pscustomobject]@{
        Description = "Default Rust app preview binary, view snapshot smoke, window options, and no retained-runtime process boundary."
        Steps = @(
            (New-ValidationStep "format app preview/window slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\main.rs", "rs\crates\easydict_app\src\window_options.rs", "rs\crates\easydict_app\tests\default_api_boundary_behavior.rs", "rs\crates\easydict_preview_iced\src\main.rs")),
            (New-ValidationStep "app preview binary builds" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--bin", "easydict_app")),
            (New-ValidationStep "preview iced portable GUI contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_preview_iced", "--all-targets")),
            (New-ValidationStep "preview iced portable GUI builds" @("cargo", "check", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_preview_iced", "--all-targets")),
            (New-ValidationStep "main window preview scenarios render" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "ui_contract", "main_window_preview_scenarios_cover_translation_states")),
            (New-ValidationStep "window option and window-specific contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "ui_contract", "window_")),
            (New-ValidationStep "default process spawn no-runtime boundary" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "default_api_boundary_behavior", "default_process_spawn_surface_has_no_retained_dotnet_runtime_entries"))
        )
    }
    "cli-translate" = [pscustomobject]@{
        Description = "Default Rust CLI argument parser, native translate smoke, legacy flag rejection, and no retained-worker boundary."
        Steps = @(
            (New-ValidationStep "format CLI translate slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\cli_translate.rs", "rs\crates\easydict_app\src\bin\easydict_cli.rs", "rs\crates\easydict_app\tests\cli_translate_behavior.rs", "rs\crates\easydict_app\tests\default_api_boundary_behavior.rs")),
            (New-ValidationStep "CLI parser contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--lib", "cli_translate")),
            (New-ValidationStep "default CLI rejects legacy retained-worker flags" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "cli_translate_behavior", "default_cli_rejects_legacy_retained_worker_options")),
            (New-ValidationStep "default CLI native Google smoke" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "cli_translate_behavior", "default_translate_uses_native_google_without_retained_runtime_or_shell_wording")),
            (New-ValidationStep "CLI LocalAI default no-worker boundary" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "cli_translate_behavior", "local_ai_cli_without_app_dir_fails_native_only_without_worker_lookup")),
            (New-ValidationStep "CLI legacy flags stay feature-gated" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "default_api_boundary_behavior", "default_cli_rejects_legacy_retained_worker_options_unless_feature_gated")),
            (New-ValidationStep "default process spawn no-runtime boundary" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "default_api_boundary_behavior", "default_process_spawn_surface_has_no_retained_dotnet_runtime_entries"))
        )
    }
    "longdoc-cli" = [pscustomobject]@{
        Description = "Default Rust LongDoc CLI entrypoint, parser smoke, native preflight, and no retained-worker boundary."
        Steps = @(
            (New-ValidationStep "format LongDoc CLI slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\long_document_cli.rs", "rs\crates\easydict_app\src\bin\easydict_long_doc.rs", "rs\crates\easydict_app\tests\long_document_cli_behavior.rs", "rs\crates\easydict_app\tests\default_api_boundary_behavior.rs")),
            (New-ValidationStep "LongDoc CLI help omits legacy app-dir" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "long_document_cli_behavior", "help_lists_long_document_options")),
            (New-ValidationStep "LongDoc CLI service list stays native" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "long_document_cli_behavior", "list_services_succeeds_without_document_arguments")),
            (New-ValidationStep "LongDoc CLI stale payload boundaries" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "long_document_cli_behavior", "stale_dotnet_payload")),
            (New-ValidationStep "LongDoc CLI target-auto no-worker boundary" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "long_document_cli_behavior", "target_auto_fails_before_native_or_retained_worker_lookup")),
            (New-ValidationStep "LongDoc CLI LocalAI native preflight boundary" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "long_document_cli_behavior", "env_overrides_local_ai_provider_and_openvino_cache_dir_for_native_preflight")),
            (New-ValidationStep "default process spawn no-runtime boundary" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "default_api_boundary_behavior", "default_process_spawn_surface_has_no_retained_dotnet_runtime_entries"))
        )
    }
    "longdoc-script" = [pscustomobject]@{
        Description = "Root LongDoc PowerShell shim parser, Rust helper/cargo forwarding, retired legacy switch, and retained-runtime helper guards."
        Steps = @(
            (New-ValidationStep "PowerShell parse LongDoc script shim" @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", '$errors = $null; [System.Management.Automation.Language.Parser]::ParseFile(''scripts\translate-long-doc.ps1'', [ref]$null, [ref]$errors) > $null; if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Error $_.Message }; exit 1 }')),
            (New-ValidationStep "LongDoc script Rust-only and helper forwarding contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_packager", "--test", "release_contract_behavior", "translate_long_doc_script"))
        )
    }
    "asset-downloads" = [pscustomobject]@{
        Description = "Shared Rust-owned asset download policy, CJK font cache, layout model assets, and OpenVINO asset contracts."
        Steps = @(
            (New-ValidationStep "format shared asset download slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\resource_download.rs", "rs\crates\easydict_app\src\font_download.rs", "rs\crates\easydict_app\src\layout_model_download.rs", "rs\crates\easydict_app\src\openvino_download.rs", "rs\crates\easydict_app\tests\resource_download_behavior.rs", "rs\crates\easydict_app\tests\layout_model_download_behavior.rs", "rs\crates\easydict_app\tests\openvino_download_behavior.rs")),
            (New-ValidationStep "shared resource download policy contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "resource_download_behavior", "resource_download_")),
            (New-ValidationStep "CJK font download contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "resource_download_behavior", "font_download_")),
            (New-ValidationStep "LongDoc layout model asset contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "layout_model_download_behavior", "layout_model")),
            (New-ValidationStep "OpenVINO asset download contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "openvino_download_behavior", "openvino_"))
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
    "openai-compatible" = [pscustomobject]@{
        Description = "Rust-native OpenAI-compatible request planning, SSE parsing, Quick Translate, and CLI provider contracts."
        Steps = @(
            (New-ValidationStep "format OpenAI-compatible slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\openai_compatible.rs", "rs\crates\easydict_app\src\llm_streaming.rs", "rs\crates\easydict_app\src\quick_translate.rs", "rs\crates\easydict_app\src\bin\easydict_cli.rs", "rs\crates\easydict_app\tests\openai_compatible_behavior.rs", "rs\crates\easydict_app\tests\llm_streaming_behavior.rs", "rs\crates\easydict_app\tests\quick_translate_behavior.rs", "rs\crates\easydict_app\tests\cli_translate_behavior.rs")),
            (New-ValidationStep "OpenAI SSE parser contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "llm_streaming_behavior")),
            (New-ValidationStep "OpenAI-compatible planner and executor contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "openai_compatible_behavior")),
            (New-ValidationStep "Quick Translate OpenAI-compatible routes" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "native_openai_quick_translate")),
            (New-ValidationStep "CLI OpenAI translate/grammar/batch contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "cli_translate_behavior", "native_openai_cli")),
            (New-ValidationStep "CLI OpenAI streaming latency contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "cli_translate_behavior", "stream_command_writes_openai_chunks_before_sse_response_completes")),
            (New-ValidationStep "CLI Ollama native contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "cli_translate_behavior", "native_ollama_cli")),
            (New-ValidationStep "CLI Custom OpenAI native contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "cli_translate_behavior", "native_custom_openai_cli")),
            (New-ValidationStep "CLI DeepSeek native contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "cli_translate_behavior", "native_deepseek_cli")),
            (New-ValidationStep "CLI Groq native contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "cli_translate_behavior", "native_groq_cli")),
            (New-ValidationStep "CLI Zhipu native contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "cli_translate_behavior", "native_zhipu_cli")),
            (New-ValidationStep "CLI GitHub Models native contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "cli_translate_behavior", "native_github_models_cli"))
        )
    }
    "custom-streaming" = [pscustomobject]@{
        Description = "Gemini/Doubao native custom streaming request planning, live chunks, and CLI SSE contracts."
        Steps = @(
            (New-ValidationStep "format custom streaming slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\custom_streaming.rs", "rs\crates\easydict_app\src\quick_translate.rs", "rs\crates\easydict_app\tests\quick_translate_behavior.rs", "rs\crates\easydict_app\tests\cli_translate_behavior.rs")),
            (New-ValidationStep "app custom streaming contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "native_custom_streaming")),
            (New-ValidationStep "CLI Doubao local SSE contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "cli_translate_behavior", "native_doubao_cli_translate_succeeds_against_local_sse_without_worker_wording")),
            (New-ValidationStep "CLI Gemini local SSE contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "cli_translate_behavior", "native_gemini_cli_translate_succeeds_against_local_sse_without_worker_wording")),
            (New-ValidationStep "CLI custom streaming latency contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "cli_translate_behavior", "custom_streaming_stream_command_writes"))
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
    "translation-cache" = [pscustomobject]@{
        Description = "Rust-owned memory/persistent translation cache, Quick Translate clear/status, LongDoc cache, and phonetic-cache contracts."
        Steps = @(
            (New-ValidationStep "format translation cache slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\translation_cache.rs", "rs\crates\easydict_app\src\long_document.rs", "rs\crates\easydict_app\src\state.rs", "rs\crates\easydict_app\src\lib.rs", "rs\crates\easydict_app\tests\translation_cache_behavior.rs", "rs\crates\easydict_app\tests\quick_translate_behavior.rs", "rs\crates\easydict_app\tests\long_document_behavior.rs")),
            (New-ValidationStep "translation cache core contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "translation_cache_behavior")),
            (New-ValidationStep "Quick Translate cache clear/status contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "clear_")),
            (New-ValidationStep "LongDoc persistent cache contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "long_document_behavior", "native_text_long_document_cache")),
            (New-ValidationStep "LongDoc formula cache hash contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "long_document_behavior", "native_text_long_document_formula_cache"))
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
            (New-ValidationStep "format OpenVINO download slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\openvino_download.rs", "rs\crates\easydict_app\src\resource_download.rs", "rs\crates\easydict_app\src\state.rs", "rs\crates\easydict_app\tests\openvino_download_behavior.rs", "rs\crates\easydict_app\tests\resource_download_behavior.rs")),
            (New-ValidationStep "OpenVINO download contracts and diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "openvino_download_behavior", "openvino_"))
        )
    }
    "windows-ai-native" = [pscustomobject]@{
        Description = "WindowsAI/Phi Silica native client, prepare/status, Quick Translate, CLI, and LongDoc route contracts."
        Steps = @(
            (New-ValidationStep "format WindowsAI native slice" @("rustfmt", "--edition", "2021", "--check", "lib\easydict-windows-ai\src\lib.rs", "lib\easydict-windows-ai\src\winrt_language_model.rs", "rs\crates\easydict_app\src\state.rs", "rs\crates\easydict_app\src\quick_translate.rs", "rs\crates\easydict_app\src\long_document.rs", "rs\crates\easydict_app\src\bin\easydict_cli.rs", "rs\crates\easydict_app\tests\quick_translate_behavior.rs", "rs\crates\easydict_app\tests\cli_translate_behavior.rs", "rs\crates\easydict_app\tests\long_document_behavior.rs")),
            (New-ValidationStep "WindowsAI lib native contracts" @("cargo", "test", "--manifest-path", "lib\easydict-windows-ai\Cargo.toml")),
            (New-ValidationStep "app WindowsAI prepare state/lifecycle" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "app_windows_ai_prepare")),
            (New-ValidationStep "Quick Translate WindowsAI route decisions" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "local_ai_route_decision")),
            (New-ValidationStep "Quick Translate native WindowsAI client routes" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "windows_ai")),
            (New-ValidationStep "CLI native WindowsAI route contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "cli_translate_behavior", "explicit_windows_ai")),
            (New-ValidationStep "CLI LocalAI native-only boundary" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "cli_translate_behavior", "local_ai_cli")),
            (New-ValidationStep "LongDoc native WindowsAI routes" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "long_document_behavior", "windows_ai")),
            (New-ValidationStep "LongDoc LocalAI route matrix" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "long_document_behavior", "local_ai_long_document_route_matrix"))
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
            (New-ValidationStep "format browser support and extension packaging slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\browser_registrar.rs", "rs\crates\easydict_app\src\bin\easydict_browser_registrar.rs", "rs\crates\easydict_app\src\state.rs", "rs\crates\easydict_app\src\lib.rs", "rs\crates\easydict_app\tests\browser_registrar_behavior.rs", "rs\crates\easydict_app\tests\quick_translate_behavior.rs", "rs\crates\easydict_packager\src\lib.rs", "rs\crates\easydict_packager\src\main.rs", "rs\crates\easydict_packager\tests\release_contract_behavior.rs")),
            (New-ValidationStep "PowerShell parse browser extension package shim" @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", '$errors = $null; [System.Management.Automation.Language.Parser]::ParseFile(''browser-extension\scripts\Package-Extension.ps1'', [ref]$null, [ref]$errors) > $null; if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Error $_.Message }; exit 1 }')),
            (New-ValidationStep "browser support app diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "browser_support")),
            (New-ValidationStep "browser registrar behavior contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "browser_registrar_behavior")),
            (New-ValidationStep "browser registrar binary contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--bin", "easydict_browser_registrar")),
            (New-ValidationStep "browser extension default release contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_packager", "--test", "release_contract_behavior", "default_browser_extension")),
            (New-ValidationStep "browser extension PowerShell shim forwarding contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_packager", "--test", "release_contract_behavior", "browser_extension_powershell_shim")),
            (New-ValidationStep "browser extension package scanning contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_packager", "--lib", "package_browser_extension"))
        )
    }
    "native-bridge" = [pscustomobject]@{
        Description = "Rust-owned Native Messaging frame parsing, rs OCR named event signaling, and no legacy .NET bridge wording."
        Steps = @(
            (New-ValidationStep "format native bridge and named-event slice" @("rustfmt", "--edition", "2021", "--check", "lib\easydict-windows-ipc\src\lib.rs", "rs\crates\easydict_app\src\native_bridge.rs", "rs\crates\easydict_app\src\named_event.rs", "rs\crates\easydict_app\src\bin\easydict_native_bridge.rs", "rs\crates\easydict_app\src\lib.rs", "rs\crates\easydict_app\tests\native_bridge_behavior.rs", "rs\crates\easydict_app\tests\quick_translate_behavior.rs")),
            (New-ValidationStep "Windows IPC named-event helper contracts" @("cargo", "test", "--manifest-path", "lib\easydict-windows-ipc\Cargo.toml", "--all-targets")),
            (New-ValidationStep "native bridge frame parser and binary contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "native_bridge_behavior")),
            (New-ValidationStep "app named-event receiver ownership" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "shell_and_protocol_entries_cover_ocr_activation_contract"))
        )
    }
    "startup-activation" = [pscustomobject]@{
        Description = "Rust-owned shell/protocol OCR startup activation parsing, task routing, and default no-legacy activation boundary."
        Steps = @(
            (New-ValidationStep "format startup activation slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\activation.rs", "rs\crates\easydict_app\src\lib.rs", "rs\crates\easydict_app\tests\quick_translate_behavior.rs", "rs\crates\easydict_app\tests\default_api_boundary_behavior.rs")),
            (New-ValidationStep "startup activation parser contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--lib", "activation")),
            (New-ValidationStep "startup activation app routing" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "startup_activation")),
            (New-ValidationStep "shell/protocol OCR activation contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "shell_and_protocol_entries_cover_ocr_activation_contract")),
            (New-ValidationStep "startup activation stays app-core only" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "default_api_boundary_behavior", "startup_activation_core_stays_decoupled_from_winfluent_task"))
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
    "retained-worker-ipc" = [pscustomobject]@{
        Description = "Explicit retained-worker IPC compatibility tests using the Rust mock helper, feature gates, and no PowerShell/.NET mock runtime."
        Steps = @(
            (New-ValidationStep "format retained worker IPC slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\compat_client.rs", "rs\crates\easydict_app\src\bin\easydict_ipc_mock.rs", "rs\crates\easydict_app\tests\compat_client.rs", "rs\crates\easydict_app\tests\default_api_boundary_behavior.rs")),
            (New-ValidationStep "retained worker IPC Rust mock binary builds" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--features", "retained-dotnet-workers", "--bin", "easydict-ipc-mock")),
            (New-ValidationStep "retained worker IPC compatibility contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--features", "retained-dotnet-workers", "--test", "compat_client")),
            (New-ValidationStep "retained IPC mock helper stays feature gated" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "default_api_boundary_behavior", "default_app_manifest_disables_auto_discovered_binary_entrypoints")),
            (New-ValidationStep "retained IPC tests avoid shell mock runtime" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "default_api_boundary_behavior", "default_process_spawn_surface_has_no_retained_dotnet_runtime_entries"))
        )
    }
    "input-actions" = [pscustomobject]@{
        Description = "Rust-owned clipboard read/write/monitor and text insertion side-effect contracts."
        Steps = @(
            (New-ValidationStep "format input action slice" @("rustfmt", "--edition", "2021", "--check", "lib\easydict-windows-text-selection\src\lib.rs", "rs\crates\easydict_app\src\clipboard.rs", "rs\crates\easydict_app\src\text_insertion.rs", "rs\crates\easydict_app\src\state.rs", "rs\crates\easydict_app\tests\quick_translate_behavior.rs", "rs\crates\easydict_app\tests\ocr_behavior.rs")),
            (New-ValidationStep "Windows text-selection clipboard/insertion helper contracts" @("cargo", "test", "--manifest-path", "lib\easydict-windows-text-selection\Cargo.toml")),
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
            (New-ValidationStep "format file dialog slice" @("rustfmt", "--edition", "2021", "--check", "lib\easydict-windows-dialogs\src\lib.rs", "rs\crates\easydict_app\src\file_dialog.rs", "rs\crates\easydict_app\src\lib.rs", "rs\crates\easydict_app\tests\quick_translate_behavior.rs", "rs\crates\easydict_app\tests\long_document_behavior.rs")),
            (New-ValidationStep "Windows native dialog helper contracts" @("cargo", "test", "--manifest-path", "lib\easydict-windows-dialogs\Cargo.toml", "--all-targets")),
            (New-ValidationStep "app file dialog facade contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--lib", "file_dialog")),
            (New-ValidationStep "app file dialog route ownership" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "file_dialogs_to_rust_owned_helpers")),
            (New-ValidationStep "MDX import dialog diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "mdx_dictionary_dialog_error")),
            (New-ValidationStep "LongDoc browse dialog routing" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "long_document_behavior", "app_update_long_document_browse_starts_file_dialog_only_in_long_document_mode")),
            (New-ValidationStep "LongDoc browse dialog diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "long_document_behavior", "long_document_file_dialog_error"))
        )
    }
    "text-selection" = [pscustomobject]@{
        Description = "UIA/clipboard selected-text capture diagnostics and quick-translate task plumbing."
        Steps = @(
            (New-ValidationStep "format text-selection slice" @("rustfmt", "--edition", "2021", "--check", "lib\easydict-windows-text-selection\src\lib.rs", "rs\crates\easydict_app\src\text_selection.rs", "rs\crates\easydict_app\src\state.rs", "rs\crates\easydict_app\src\lib.rs", "rs\crates\easydict_app\tests\text_selection_behavior.rs", "rs\crates\easydict_app\tests\quick_translate_behavior.rs")),
            (New-ValidationStep "Windows text-selection selected-text helper contracts" @("cargo", "test", "--manifest-path", "lib\easydict-windows-text-selection\Cargo.toml")),
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
            (New-ValidationStep "format OCR diagnostics slice" @("rustfmt", "--edition", "2021", "--check", "lib\easydict-windows-screen-capture\src\lib.rs", "rs\crates\easydict_windows_ocr\src\lib.rs", "rs\crates\easydict_app\src\screen_capture_native.rs", "rs\crates\easydict_app\src\ocr.rs", "rs\crates\easydict_app\src\lib.rs", "rs\crates\easydict_app\src\state.rs", "rs\crates\easydict_app\tests\ocr_behavior.rs")),
            (New-ValidationStep "Windows screen capture helper contracts" @("cargo", "test", "--manifest-path", "lib\easydict-windows-screen-capture\Cargo.toml", "--all-targets")),
            (New-ValidationStep "Windows native OCR helper contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_windows_ocr")),
            (New-ValidationStep "HTTP backend parse diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "ocr_behavior", "ocr_http_provider")),
            (New-ValidationStep "app screen capture facade contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--lib", "screen_capture_native")),
            (New-ValidationStep "app OCR capture diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "ocr_behavior", "app_ocr_capture_failure_surfaces_native_screen_capture_error")),
            (New-ValidationStep "capture background diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "ocr_behavior", "capture_background_failure_preserves_overlay_and_success_clears_only_background_error")),
            (New-ValidationStep "window snapshot diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "ocr_behavior", "capture_window_snapshot_failure_preserves_manual_region_capture")),
            (New-ValidationStep "snapshot startup contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "ocr_behavior", "ocr_hotkey_captures_window_snapshot_for_double_click_detection")),
            (New-ValidationStep "native capture helper task surface" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "ocr_behavior", "app_ocr_screen_capture_uses_native_helper_instead_of_winfluent_task_surface"))
        )
    }
    "longdoc-layout" = [pscustomobject]@{
        Description = "LongDoc DocLayout-YOLO/TATR/Vision layout configuration and backend diagnostics."
        Steps = @(
            (New-ValidationStep "format LongDoc layout slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\long_document.rs", "rs\crates\easydict_app\src\layout_model_download.rs", "rs\crates\easydict_app\src\doc_layout_yolo.rs", "rs\crates\easydict_app\src\doc_layout_yolo_onnx.rs", "rs\crates\easydict_app\src\vision_layout.rs", "rs\crates\easydict_app\src\table_structure.rs", "rs\crates\easydict_app\src\table_structure_onnx.rs", "rs\crates\easydict_app\tests\layout_model_download_behavior.rs", "rs\crates\easydict_app\tests\doc_layout_yolo_behavior.rs", "rs\crates\easydict_app\tests\doc_layout_yolo_onnx_behavior.rs", "rs\crates\easydict_app\tests\vision_layout_behavior.rs", "rs\crates\easydict_app\tests\table_structure_behavior.rs", "rs\crates\easydict_app\tests\table_structure_onnx_behavior.rs", "rs\crates\easydict_app\tests\long_document_behavior.rs")),
            (New-ValidationStep "layout model download contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "layout_model_download_behavior", "layout_model")),
            (New-ValidationStep "DocLayout-YOLO preprocessing contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "doc_layout_yolo_behavior", "doc_layout_yolo")),
            (New-ValidationStep "DocLayout-YOLO ONNX helper contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "doc_layout_yolo_onnx_behavior", "doc_layout_yolo_onnx")),
            (New-ValidationStep "vision layout request/parser/executor contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "vision_layout_behavior", "vision_layout")),
            (New-ValidationStep "TATR table structure contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "table_structure_behavior", "table_")),
            (New-ValidationStep "TATR ONNX helper contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "table_structure_onnx_behavior", "tatr_onnx")),
            (New-ValidationStep "explicit VisionLLM config errors" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--lib", "explicit_vision_layout_config_surfaces_missing_required_settings")),
            (New-ValidationStep "vision backend page diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--lib", "vision_layout_backend_errors_preserve_page_number_and_provider_message")),
            (New-ValidationStep "explicit TATR setup diagnostics" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--lib", "tatr"))
        )
    }
    "longdoc-export" = [pscustomobject]@{
        Description = "Rust-native LongDoc TXT/Markdown/PDF export, PDF content-stream patching, and source-block export metadata."
        Steps = @(
            (New-ValidationStep "format LongDoc export slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\long_document_export.rs", "rs\crates\easydict_app\src\pdf_content_stream.rs", "rs\crates\easydict_app\src\pdf_native_export.rs", "rs\crates\easydict_app\src\pdf_export_blocks.rs", "rs\crates\easydict_app\src\pdf_source_extraction.rs", "rs\crates\easydict_app\src\long_document.rs", "rs\crates\easydict_app\tests\long_document_export_behavior.rs", "rs\crates\easydict_app\tests\pdf_content_stream_behavior.rs", "rs\crates\easydict_app\tests\pdf_native_export_behavior.rs", "rs\crates\easydict_app\tests\pdf_export_blocks_behavior.rs", "rs\crates\easydict_app\tests\pdf_source_extraction_behavior.rs")),
            (New-ValidationStep "LongDoc text and markdown export composers" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "long_document_export_behavior")),
            (New-ValidationStep "PDF content-stream patch contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "pdf_content_stream_behavior")),
            (New-ValidationStep "native PDF export contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "pdf_native_export_behavior", "native_pdf_export")),
            (New-ValidationStep "PDF export block overlay metadata" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "pdf_export_blocks_behavior")),
            (New-ValidationStep "PDF source extraction export metadata" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "pdf_source_extraction_behavior"))
        )
    }
    "longdoc-formula" = [pscustomobject]@{
        Description = "Rust-native LongDoc formula preservation, text layout/font metrics, and PDF formula evidence."
        Steps = @(
            (New-ValidationStep "format LongDoc formula/layout slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\text_layout.rs", "rs\crates\easydict_app\src\font_metrics.rs", "rs\crates\easydict_app\src\document_layout.rs", "rs\crates\easydict_app\src\latex_formula.rs", "rs\crates\easydict_app\src\formula_protection.rs", "rs\crates\easydict_app\src\content_preservation.rs", "rs\crates\easydict_app\src\formula_text_reconstruction.rs", "rs\crates\easydict_app\src\character_paragraph.rs", "rs\crates\easydict_app\src\pdf_formula_adapter.rs", "rs\crates\easydict_app\src\long_document.rs", "rs\crates\easydict_app\tests\text_layout_behavior.rs", "rs\crates\easydict_app\tests\font_metrics_behavior.rs", "rs\crates\easydict_app\tests\document_layout_behavior.rs", "rs\crates\easydict_app\tests\latex_formula_behavior.rs", "rs\crates\easydict_app\tests\formula_protection_behavior.rs", "rs\crates\easydict_app\tests\content_preservation_behavior.rs", "rs\crates\easydict_app\tests\formula_text_reconstruction_behavior.rs", "rs\crates\easydict_app\tests\character_paragraph_behavior.rs", "rs\crates\easydict_app\tests\pdf_formula_adapter_behavior.rs", "rs\crates\easydict_app\tests\long_document_behavior.rs")),
            (New-ValidationStep "text layout wrapping and fit contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "text_layout_behavior")),
            (New-ValidationStep "font metrics contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "font_metrics_behavior")),
            (New-ValidationStep "document layout geometry contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "document_layout_behavior")),
            (New-ValidationStep "LaTeX render-text simplifier" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "latex_formula_behavior")),
            (New-ValidationStep "formula protection contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "formula_protection_behavior")),
            (New-ValidationStep "content preservation service contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "content_preservation_behavior")),
            (New-ValidationStep "formula-aware text reconstruction contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "formula_text_reconstruction_behavior")),
            (New-ValidationStep "character paragraph evidence contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "character_paragraph_behavior")),
            (New-ValidationStep "PDF formula adapter contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "pdf_formula_adapter_behavior")),
            (New-ValidationStep "native LongDoc formula integration" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "long_document_behavior", "native_text_long_document_formula"))
        )
    }
    "mdx-native" = [pscustomobject]@{
        Description = "Rust-native MDX/MDD lookup, encrypted dictionary routing, MDD resource inlining, and real-corpus gates."
        Steps = @(
            (New-ValidationStep "format rs-mdict crate" @("cargo", "fmt", "--manifest-path", "lib\rs-mdict\Cargo.toml", "--check")),
            (New-ValidationStep "format app MDX native slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\mdx_native.rs", "rs\crates\easydict_app\tests\mdx_native_behavior.rs", "rs\crates\easydict_app\tests\quick_translate_behavior.rs", "rs\crates\easydict_app\tests\settings_storage_behavior.rs")),
            (New-ValidationStep "rs-mdict default contracts" @("cargo", "test", "--manifest-path", "lib\rs-mdict\Cargo.toml")),
            (New-ValidationStep "optional Collins real-corpus MDX/MDD contracts" @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "rs\scripts\Invoke-MdxRealCorpusValidation.ps1")),
            (New-ValidationStep "app native MDX/MDD lookup contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "mdx_native_behavior")),
            (New-ValidationStep "quick translate MDX service contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "mdx")),
            (New-ValidationStep "settings MDD companion discovery contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "settings_storage_behavior", "mdd"))
        )
    }
    "local-dictionary-suggestions" = [pscustomobject]@{
        Description = "Rust-native MDX suggestion index routing, encrypted dictionaries, and no CompatHost fallback."
        Steps = @(
            (New-ValidationStep "format local dictionary suggestion/index slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\local_dictionary.rs", "rs\crates\easydict_app\src\local_dictionary_index.rs", "rs\crates\easydict_app\src\lex_index.rs", "rs\crates\easydict_app\src\bin\easydict_lex_index.rs", "rs\crates\easydict_app\tests\local_dictionary_index_behavior.rs", "rs\crates\easydict_app\tests\lex_index_behavior.rs", "rs\crates\easydict_app\tests\quick_translate_behavior.rs")),
            (New-ValidationStep "LexIndex LXDX contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "lex_index_behavior")),
            (New-ValidationStep "LexIndex CLI contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--bin", "easydict-lex-index")),
            (New-ValidationStep "persistent local dictionary index contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "local_dictionary_index_behavior")),
            (New-ValidationStep "Quick Translate local dictionary suggestion contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_app", "--test", "quick_translate_behavior", "local_dictionary_suggestion"))
        )
    }
    "pdf-to-images" = [pscustomobject]@{
        Description = "Rust PDFium rendering wrapper, PDF-to-images diagnostic CLI, and PowerShell shim forwarding."
        Steps = @(
            (New-ValidationStep "format PDF-to-images slice" @("rustfmt", "--edition", "2021", "--check", "lib\easydict-pdf-render\src\lib.rs", "rs\crates\easydict_pdf_to_images\src\main.rs", "rs\crates\easydict_pdf_to_images\tests\cli_behavior.rs", "rs\crates\easydict_pdf_to_images\tests\pdf_render_contract.rs")),
            (New-ValidationStep "PowerShell parse PDF-to-images shim" @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", '$errors = $null; [System.Management.Automation.Language.Parser]::ParseFile(''dotnet\scripts\pdf-to-images.ps1'', [ref]$null, [ref]$errors) > $null; if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Error $_.Message }; exit 1 }')),
            (New-ValidationStep "Rust PDF render helper contracts" @("cargo", "test", "--manifest-path", "lib\easydict-pdf-render\Cargo.toml")),
            (New-ValidationStep "PDF-to-images CLI contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_pdf_to_images"))
        )
    }
    "store-listings" = [pscustomobject]@{
        Description = "Rust-owned Microsoft Store listing metadata validation, preview/summary payload generation, workflow, and shim."
        Steps = @(
            (New-ValidationStep "format Store listing tool slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_store_listings\src\lib.rs", "rs\crates\easydict_store_listings\src\main.rs")),
            (New-ValidationStep "PowerShell parse Store listing shim" @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", '$errors = $null; [System.Management.Automation.Language.Parser]::ParseFile(''.winstore\scripts\Sync-StoreListings.ps1'', [ref]$null, [ref]$errors) > $null; if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Error $_.Message }; exit 1 }')),
            (New-ValidationStep "Store listing Rust contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_store_listings")),
            (New-ValidationStep "Store listing metadata validates" @("cargo", "run", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_store_listings", "--", "validate", "--winstore-root", ".winstore"))
        )
    }
    "encrypt-secret" = [pscustomobject]@{
        Description = "Rust-compatible built-in secret encryption helper, CLI output, and retired .NET EncryptSecret boundary."
        Steps = @(
            (New-ValidationStep "format encrypt-secret slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_encrypt_secret\src\lib.rs", "rs\crates\easydict_encrypt_secret\src\main.rs", "rs\crates\easydict_encrypt_secret\tests\cli_behavior.rs")),
            (New-ValidationStep "Rust secret encryption contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_encrypt_secret"))
        )
    }
    "msix-validate" = [pscustomobject]@{
        Description = "Rust MSIX validator, package preparation, bundle min-version, retained payload policy, and maintenance subcommands."
        Steps = @(
            (New-ValidationStep "format MSIX validator slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_msix_validate\src\lib.rs", "rs\crates\easydict_msix_validate\src\main.rs")),
            (New-ValidationStep "MSIX validator Rust contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_msix_validate"))
        )
    }
    "icon-generator" = [pscustomobject]@{
        Description = "Rust WinUI app icon, Windows asset, and service icon generation plus PowerShell shim forwarding."
        Steps = @(
            (New-ValidationStep "format icon generator slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_icon_generator\src\lib.rs", "rs\crates\easydict_icon_generator\src\main.rs")),
            (New-ValidationStep "PowerShell parse icon generator shims" @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", '$paths = @(''dotnet\scripts\generate-windows-assets.ps1'', ''dotnet\scripts\generate-assets-from-macos-icon.ps1'', ''dotnet\scripts\convert-service-icons.ps1''); foreach ($path in $paths) { $errors = $null; [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$errors) > $null; if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Error (''{0}: {1}'' -f $path, $_.Message) }; exit 1 } }')),
            (New-ValidationStep "icon generator Rust contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_icon_generator"))
        )
    }
    "rust-helper-build" = [pscustomobject]@{
        Description = "Rust helper build shim, child cargo runtime-profile isolation, and legacy registrar alias opt-in guards."
        Steps = @(
            (New-ValidationStep "format Rust helper build release-contract slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_packager\src\lib.rs", "rs\crates\easydict_packager\src\main.rs", "rs\crates\easydict_packager\tests\release_contract_behavior.rs")),
            (New-ValidationStep "PowerShell parse Rust helper build shim" @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", '$errors = $null; [System.Management.Automation.Language.Parser]::ParseFile(''dotnet\scripts\Build-RustHelpers.ps1'', [ref]$null, [ref]$errors) > $null; if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Error $_.Message }; exit 1 }')),
            (New-ValidationStep "release orchestration uses Rust helpers" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_packager", "--test", "release_contract_behavior", "release_orchestration_uses_rust_helpers_not_retired_dotnet_helper_projects")),
            (New-ValidationStep "Rust helper build child env and shim contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_packager", "--test", "release_contract_behavior", "build_rust_helpers"))
        )
    }
    "dotnet-runtime-extract" = [pscustomobject]@{
        Description = "Hybrid-only .NET runtime extraction shim, feature gate, and Rust extractor contracts."
        Steps = @(
            (New-ValidationStep "format .NET runtime extraction release-contract slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_packager\src\lib.rs", "rs\crates\easydict_packager\src\main.rs", "rs\crates\easydict_packager\tests\release_contract_behavior.rs")),
            (New-ValidationStep "PowerShell parse .NET runtime extraction shim" @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", '$errors = $null; [System.Management.Automation.Language.Parser]::ParseFile(''dotnet\scripts\Extract-DotnetRuntime.ps1'', [ref]$null, [ref]$errors) > $null; if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Error $_.Message }; exit 1 }')),
            (New-ValidationStep ".NET runtime extraction stays hybrid-gated" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_packager", "--test", "release_contract_behavior", "dotnet_runtime_extraction")),
            (New-ValidationStep ".NET runtime extraction PowerShell shim contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_packager", "--test", "release_contract_behavior", "extract_dotnet_runtime_powershell_shim")),
            (New-ValidationStep "Rust .NET runtime extractor contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_packager", "--features", "hybrid-dotnet-runtime-packaging", "--lib", "extract_dotnet_runtime"))
        )
    }
    "msix-runtime-profile" = [pscustomobject]@{
        Description = "MSIX diagnostic install, QDC, and UI automation runtime-profile checks that keep default payload validation rust-only."
        Steps = @(
            (New-ValidationStep "format MSIX runtime-profile contracts" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_packager\tests\msix_runtime_profile_contract_behavior.rs", "rs\crates\easydict_packager\tests\release_contract_behavior.rs")),
            (New-ValidationStep "PowerShell parse MSIX/QDC install shims" @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", '$paths = @(''dotnet\scripts\sign-and-install.ps1'', ''dotnet\scripts\qdc\Deploy-ToQdc.ps1'', ''dotnet\scripts\qdc\Install-OnQdc.ps1''); foreach ($path in $paths) { $errors = $null; [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$errors) > $null; if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Error (''{0}: {1}'' -f $path, $_.Message) }; exit 1 } }')),
            (New-ValidationStep "MSIX runtime-profile static contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_packager", "--test", "msix_runtime_profile_contract_behavior")),
            (New-ValidationStep "sign-and-install validator ordering contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_packager", "--test", "release_contract_behavior", "sign_and_install")),
            (New-ValidationStep "QDC machine install validator ordering contract" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_packager", "--test", "release_contract_behavior", "qdc_install_machine_scope"))
        )
    }
    "rs-portable-release" = [pscustomobject]@{
        Description = "First rs portable release/default packaging gates that keep retained .NET payloads out."
        Steps = @(
            (New-ValidationStep "format rs portable release slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_packager\src\lib.rs", "rs\crates\easydict_packager\src\main.rs", "rs\crates\easydict_packager\tests\release_contract_behavior.rs")),
            (New-ValidationStep "release defaults to rs portable" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_packager", "--test", "release_contract_behavior", "migration_list_acceptance_defaults_to_rs_portable_before_legacy_dotnet")),
            (New-ValidationStep "rs portable release workflow and release-asset contracts" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_packager", "--test", "release_contract_behavior", "rs_portable_release")),
            (New-ValidationStep "zip validation excludes retained runtime for CLI entrypoint" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_packager", "--test", "release_contract_behavior", "pack_rs_portable_zip_extracts_to_cli_smoke_without_dotnet_or_powershell")),
            (New-ValidationStep "zip validation excludes retained runtime for GUI entrypoint" @("cargo", "test", "--manifest-path", "rs\Cargo.toml", "-p", "easydict_packager", "--test", "release_contract_behavior", "pack_rs_portable_zip_extracts_to_gui_entrypoint_smoke_without_dotnet_or_powershell"))
        )
    }
    "runtime-guards" = [pscustomobject]@{
        Description = "Shared retained .NET runtime/script classifier and runtime-profile policy contracts."
        Steps = @(
            (New-ValidationStep "format runtime guards crate" @("cargo", "fmt", "--manifest-path", "lib\easydict-runtime-guards\Cargo.toml", "--check")),
            (New-ValidationStep "runtime guards default contracts" @("cargo", "test", "--manifest-path", "lib\easydict-runtime-guards\Cargo.toml", "--all-targets")),
            (New-ValidationStep "runtime guards retained-feature contracts" @("cargo", "test", "--manifest-path", "lib\easydict-runtime-guards\Cargo.toml", "--features", "retained-dotnet-workers", "--all-targets"))
        )
    }
    "windows-registry" = [pscustomobject]@{
        Description = "Rust-owned HKCU registry helper contracts for browser, shell, protocol, and startup registration."
        Steps = @(
            (New-ValidationStep "format Windows registry helper crate" @("cargo", "fmt", "--manifest-path", "lib\easydict-windows-registry\Cargo.toml", "--check")),
            (New-ValidationStep "Windows registry helper contracts" @("cargo", "test", "--manifest-path", "lib\easydict-windows-registry\Cargo.toml", "--all-targets"))
        )
    }
    "nllb-native" = [pscustomobject]@{
        Description = "Rust NLLB/OpenVINO helper contracts for cache manifests, language mapping, tokenizer, streaming, and ORT feature wiring."
        Steps = @(
            (New-ValidationStep "format NLLB/OpenVINO helper crate" @("cargo", "fmt", "--manifest-path", "lib\easydict-nllb\Cargo.toml", "--check")),
            (New-ValidationStep "NLLB default contracts" @("cargo", "test", "--manifest-path", "lib\easydict-nllb\Cargo.toml", "--all-targets")),
            (New-ValidationStep "NLLB ORT/OpenVINO feature contracts" @("cargo", "test", "--manifest-path", "lib\easydict-nllb\Cargo.toml", "--features", "ort-openvino", "--all-targets"))
        )
    }
    "pdf-overlay" = [pscustomobject]@{
        Description = "Rust PDF overlay helper contracts for path validation, geometry validation, CJK font embedding, and selected-page retention."
        Steps = @(
            (New-ValidationStep "format PDF overlay helper crate" @("cargo", "fmt", "--manifest-path", "lib\easydict-pdf-overlay\Cargo.toml", "--check")),
            (New-ValidationStep "PDF overlay helper contracts" @("cargo", "test", "--manifest-path", "lib\easydict-pdf-overlay\Cargo.toml", "--all-targets"))
        )
    }
    "rust-only-boundary" = [pscustomobject]@{
        Description = "Fast default-rs no-runtime boundary checks before closing core migration slices."
        Steps = @(
            (New-ValidationStep "format default runtime boundary slice" @("rustfmt", "--edition", "2021", "--check", "rs\crates\easydict_app\src\runtime_policy.rs", "rs\crates\easydict_app\src\state.rs", "rs\crates\easydict_app\src\lib.rs", "rs\crates\easydict_app\src\quick_translate.rs", "rs\crates\easydict_app\src\long_document.rs", "rs\crates\easydict_app\src\long_document_cli.rs", "rs\crates\easydict_app\src\bin\easydict_cli.rs", "rs\crates\easydict_app\tests\default_api_boundary_behavior.rs", "rs\crates\easydict_app\tests\cli_translate_behavior.rs", "rs\crates\easydict_app\tests\long_document_behavior.rs", "rs\crates\easydict_app\tests\long_document_cli_behavior.rs")),
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
    "lib/winfluent-rs/crates/win_fluent/src/platform.rs",
    "lib/winfluent-rs/crates/win_fluent/src/schema.rs",
    "lib/winfluent-rs/crates/win_fluent/src/theme.rs",
    "lib/winfluent-rs/crates/win_fluent/src/view.rs",
    "lib/winfluent-rs/crates/win_fluent_backend_iced/src/lib.rs",
    "lib/winfluent-rs/crates/win_fluent_platform_win/src/lib.rs",
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
    "lib/easydict-windows-dialogs/Cargo.lock",
    "lib/easydict-windows-credentials/Cargo.lock",
    "lib/easydict-pdf-overlay/Cargo.lock",
    "lib/easydict-windows-registry/Cargo.lock"
)

$profileRecommendations = [ordered]@{
    "core-validation-tooling" = [pscustomobject]@{
        PathPatterns = @(
            "rs/scripts/Invoke-RsCoreSliceValidation.ps1",
            "rs/scripts/Test-RsCoreSliceValidation.ps1"
        )
        DiffPatterns = @("CloseOut", "RunRecommendedProfiles", "AllRecommendedProfiles", "DryRun", "validationProfiles", "profileRecommendations", "RecommendProfiles")
    }
    "desktop-settings" = [pscustomobject]@{
        PathPatterns = @(
            "lib/easydict-windows-shell/**",
            "rs/crates/easydict_app/src/desktop*.rs",
            "rs/crates/easydict_app/tests/default_api_boundary_behavior.rs"
        )
        DiffPatterns = @("DesktopShell", "DesktopIntegration", "desktop_shell", "desktop_integration", "settings_save", "SettingsSave", "WindowsShell", "windows_shell", "OpenUrl", "RunBundledExecutable", "ShellExecuteW", "register_shell_verb", "register_protocol")
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
    "settings-runtime-status" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/settings_status.rs"
        )
        DiffPatterns = @("SettingsRuntimeStatus", "settings_runtime_status", "settings_status", "load_runtime_status", "foundry_local_status", "open_vino_status", "windows_ai_status", "OpenVinoCacheStatus")
    }
    "app-core-catalog" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/app_data.rs",
            "rs/crates/easydict_app/src/translation_services.rs",
            "rs/crates/easydict_app/tests/translation_services_behavior.rs"
        )
        DiffPatterns = @("RUST_APP_DATA_ROOT_NAME", "LEGACY_APP_DATA_ROOT_NAME", "default_user_data_directory", "legacy_user_data_directory", "default_translation_service_descriptors", "TranslationServiceDescriptor", "TranslationServiceKind", "DEFAULT_SERVICE_ID", "DEFAULT_MAIN_WINDOW_SERVICE_IDS", "DEFAULT_FLOATING_WINDOW_SERVICE_IDS", "app_visible_translation_service_ids", "openai_compatible_service_ids", "windows-local-ai", "service catalog")
    }
    "app-preview-window" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/main.rs",
            "rs/crates/easydict_app/src/window_options.rs",
            "rs/crates/easydict_preview_iced/**"
        )
        DiffPatterns = @("preview_from_env", "PreviewScenario", "view_schema", "main_window_options", "main_window_options_for_settings", "settings_window_options", "mini_window_options", "fixed_window_options", "capture_overlay_window_options", "pop_button_window_options", "visible_on_start", "WindowOptions", "easydict_preview_iced", "PreviewApp", "preview_mode_requested")
    }
    "cli-translate" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/cli_translate.rs",
            "rs/crates/easydict_app/src/bin/easydict_cli.rs"
        )
        DiffPatterns = @("CliOptions", "CliMode", "CliParseError", "parse_args", "usage()", "easydict_cli", "default_cli", "default_translate_uses_native_google", "default CLI", "legacy retained-worker", "local_ai_cli_without_app_dir", "--host", "--host-arg", "--app-dir")
    }
    "longdoc-cli" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/long_document_cli.rs",
            "rs/crates/easydict_app/src/bin/easydict_long_doc.rs",
            "rs/crates/easydict_app/tests/long_document_cli_behavior.rs"
        )
        DiffPatterns = @("LongDocumentCli", "long_document_cli", "easydict_long_doc", "list_services", "retry_failed", "target_auto", "stale_dotnet_payload", "--result-json", "--retry-failed", "--app-dir", "LocalAIProvider", "openvino_cache_dir")
    }
    "longdoc-script" = [pscustomobject]@{
        PathPatterns = @(
            "scripts/translate-long-doc.ps1"
        )
        DiffPatterns = @("translate-long-doc", "UseDotnetLegacy", "RustHelperPath", "UseCargo", "Invoke-WithRustOnlyRuntimeProfile", "Test-RetainedDotnetRuntimeOrWorkerPath", "Assert-RustHelperPathAllowed", "retained .NET runtime or worker", "translate_long_doc_script")
    }
    "asset-downloads" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/resource_download.rs",
            "rs/crates/easydict_app/src/font_download.rs",
            "rs/crates/easydict_app/tests/resource_download_behavior.rs"
        )
        DiffPatterns = @("ResourceDownload", "resource_download", "ResourceDownloadClient", "ResourceDownloadProgress", "ResourceDownloadError", "download_with_retry", "ordered_urls_by_probe", "font_download", "FontDownload", "ensure_font", "cjk font", "truncated content")
    }
    "builtin-ai-registration" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/openai_compatible.rs",
            "rs/crates/easydict_app/tests/openai_compatible_behavior.rs"
        )
        DiffPatterns = @("BuiltInAi", "Built-in AI", "builtin_device", "device_registration")
    }
    "openai-compatible" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/openai_compatible.rs",
            "rs/crates/easydict_app/src/llm_streaming.rs",
            "rs/crates/easydict_app/tests/openai_compatible_behavior.rs",
            "rs/crates/easydict_app/tests/llm_streaming_behavior.rs"
        )
        DiffPatterns = @("OpenAi", "OpenAI-compatible", "openai_compatible", "native_openai", "llm_streaming", "ChatCompletions", "Responses", "SSE", "ollama", "custom-openai", "DeepSeek", "Groq", "Zhipu", "GitHub Models", "github_models", "OpenAiApiFormat", "OpenAiCompatibleConfig", "execute_openai_stream_request")
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
    "translation-cache" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/translation_cache.rs",
            "rs/crates/easydict_app/tests/translation_cache_behavior.rs"
        )
        DiffPatterns = @("TranslationCache", "translation_cache", "translation cache", "TranslationMemoryCache", "LongDocumentTranslationCache", "ClearTranslationCache", "clear_persistent_translation_cache", "translation_cache_status", "EnableTranslationCache", "enable_translation_cache", "cache_warnings", "from_cache", "PhoneticMemoryCache", "PhoneticFlightTracker")
    }
    "foundry-local" = [pscustomobject]@{
        PathPatterns = @(
            "lib/easydict-foundry-local/**",
            "rs/crates/easydict_app/src/quick_translate.rs",
            "rs/crates/easydict_app/src/long_document.rs",
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
    "windows-ai-native" = [pscustomobject]@{
        PathPatterns = @(
            "lib/easydict-windows-ai/**"
        )
        DiffPatterns = @("WindowsAi", "WindowsAI", "windows_ai", "windows-ai", "Phi Silica", "PhiSilica", "phi_silica", "windows-local-ai", "LanguageModel", "WindowsAiLanguageModelClient", "DefaultWindowsAiLanguageModelClient", "local_ai_route_decision", "explicit_windows_ai", "auto_windows_ai")
    }
    "windows-ai-prepare" = [pscustomobject]@{
        PathPatterns = @()
        DiffPatterns = @("app_windows_ai_prepare", "prepare_status", "prepare_", "PrepareWindowsAi", "WindowsAI prepare")
    }
    "browser-support" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/browser_registrar.rs",
            "rs/crates/easydict_app/src/bin/easydict_browser_registrar.rs",
            "rs/crates/easydict_app/tests/browser_registrar_behavior.rs",
            "browser-extension/**"
        )
        DiffPatterns = @("BrowserSupport", "browser_support", "browser_registrar", "native-messaging", "NativeMessaging", "package_browser_extension", "default_browser_extension", "com.easydict.rs.bridge")
    }
    "native-bridge" = [pscustomobject]@{
        PathPatterns = @(
            "lib/easydict-windows-ipc/**",
            "rs/crates/easydict_app/src/native_bridge.rs",
            "rs/crates/easydict_app/src/named_event.rs",
            "rs/crates/easydict_app/src/bin/easydict_native_bridge.rs",
            "rs/crates/easydict_app/tests/native_bridge_behavior.rs"
        )
        DiffPatterns = @("NativeBridge", "native_bridge", "easydict-native-bridge", "easydict_native_bridge", "run_native_bridge", "named_event", "easydict_windows_ipc", "OCR_TRANSLATE_EVENT_NAME", "Local\\EasydictRs-OcrTranslate", "Subscription::named_event")
    }
    "startup-activation" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/activation.rs"
        )
        DiffPatterns = @("StartupActivation", "startup_activation", "parse_startup_activation", "startup_activation_task_for_args", "resolve_startup_activation_disposition", "OCR_TRANSLATE_ARGUMENT", "OCR_TRANSLATE_PROTOCOL_PAYLOAD", "ocr-translate", "easydict-rs://")
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
    "retained-worker-ipc" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/compat_client.rs",
            "rs/crates/easydict_app/src/bin/easydict_ipc_mock.rs",
            "rs/crates/easydict_app/tests/compat_client.rs"
        )
        DiffPatterns = @("WorkerCommand", "WorkerClient", "DirectWorkerFacade", "easydict-ipc-mock", "easydict_ipc_mock", "retained worker IPC", "mock IPC", "MOCK_WORKER_KIND", "MOCK_WORKER_PROTOCOL_VERSION", "retained-dotnet-workers")
    }
    "input-actions" = [pscustomobject]@{
        PathPatterns = @(
            "lib/easydict-windows-text-selection/**",
            "rs/crates/easydict_app/src/clipboard.rs",
            "rs/crates/easydict_app/src/text_insertion.rs",
            "rs/crates/easydict_app/tests/quick_translate_behavior.rs",
            "rs/crates/easydict_app/tests/ocr_behavior.rs"
        )
        DiffPatterns = @("ClipboardOperation", "clipboard_monitor", "monitor_clipboard", "TextInsertion", "text_insertion", "foreground_text_selection_target", "clipboard_text_snapshot", "set_clipboard_text", "result_action", "silent_ocr_outcome_uses_rust_clipboard_task", "ReadClipboardText", "WriteClipboardText", "PlatformCommand::InsertText")
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
        DiffPatterns = @("FileDialog", "file_dialog", "dialog_result", "MdxDictionaryDialog", "LongDocumentBrowse", "open_file_dialog_task", "open_folder_dialog_task", "Task::OpenFileDialog", "Task::OpenFolderDialog", "System.Windows.Forms")
    }
    "text-selection" = [pscustomobject]@{
        PathPatterns = @(
            "lib/easydict-windows-text-selection/**",
            "rs/crates/easydict_app/src/text_selection.rs",
            "rs/crates/easydict_app/tests/text_selection_behavior.rs"
        )
        DiffPatterns = @("TextSelection", "text_selection", "selected_text", "capture_native_selected_text", "capture_native_selected_text_result", "selected_text_from_capture_result", "TextSelectionBackendError", "UIA", "clipboard backend", "clipboard fallback")
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
            "rs/crates/easydict_windows_ocr/**",
            "rs/crates/easydict_app/src/ocr.rs",
            "rs/crates/easydict_app/src/screen_capture_native.rs",
            "rs/crates/easydict_app/tests/ocr_behavior.rs"
        )
        DiffPatterns = @("Ocr", "OCR", "screen_capture", "ScreenCapture", "capture_screen_background", "CAPTURE_BACKGROUND", "last_ocr_error", "CaptureWindowsSnapshot", "ocr_http_provider")
    }
    "longdoc-layout" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/long_document.rs",
            "rs/crates/easydict_app/src/layout_model_download.rs",
            "rs/crates/easydict_app/src/doc_layout_yolo.rs",
            "rs/crates/easydict_app/src/doc_layout_yolo_onnx.rs",
            "rs/crates/easydict_app/src/vision_layout.rs",
            "rs/crates/easydict_app/src/table_structure.rs",
            "rs/crates/easydict_app/src/table_structure_onnx.rs",
            "rs/crates/easydict_app/tests/layout_model_download_behavior.rs",
            "rs/crates/easydict_app/tests/doc_layout_yolo_behavior.rs",
            "rs/crates/easydict_app/tests/doc_layout_yolo_onnx_behavior.rs",
            "rs/crates/easydict_app/tests/table_structure_behavior.rs",
            "rs/crates/easydict_app/tests/table_structure_onnx_behavior.rs",
            "rs/crates/easydict_app/tests/vision_layout_behavior.rs",
            "rs/crates/easydict_app/tests/long_document_behavior.rs"
        )
        DiffPatterns = @("DocLayout", "doc_layout_yolo", "TATR", "table_structure", "VisionLLM", "vision_layout", "layout_model", "recognize_bgra", "LongDocumentBackendError")
    }
    "longdoc-export" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/long_document_export.rs",
            "rs/crates/easydict_app/src/pdf_content_stream.rs",
            "rs/crates/easydict_app/src/pdf_native_export.rs",
            "rs/crates/easydict_app/src/pdf_export_blocks.rs",
            "rs/crates/easydict_app/src/pdf_source_extraction.rs",
            "rs/crates/easydict_app/tests/long_document_export_behavior.rs",
            "rs/crates/easydict_app/tests/pdf_content_stream_behavior.rs",
            "rs/crates/easydict_app/tests/pdf_native_export_behavior.rs",
            "rs/crates/easydict_app/tests/pdf_export_blocks_behavior.rs",
            "rs/crates/easydict_app/tests/pdf_source_extraction_behavior.rs"
        )
        DiffPatterns = @("LongDocumentExport", "long_document_export", "PdfExport", "pdf_export", "PdfExportCheckpoint", "pdf_native_export", "pdf_content_stream", "ContentStreamReplacement", "NeedsFontEmbedding", "PdfOcr", "resultJsonPath", "result_json", "sidecar", "qualityReport", "quality_report", "NativeLongDocumentQualityReport", "LongDocumentTranslationCache", "translation cache", "cache_warnings", "read_native_result_json_sidecar", "write_native_result_json_sidecar", "pdf_export_mode")
    }
    "longdoc-formula" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/text_layout.rs",
            "rs/crates/easydict_app/src/font_metrics.rs",
            "rs/crates/easydict_app/src/document_layout.rs",
            "rs/crates/easydict_app/src/latex_formula.rs",
            "rs/crates/easydict_app/src/formula_protection.rs",
            "rs/crates/easydict_app/src/content_preservation.rs",
            "rs/crates/easydict_app/src/formula_text_reconstruction.rs",
            "rs/crates/easydict_app/src/character_paragraph.rs",
            "rs/crates/easydict_app/src/pdf_formula_adapter.rs",
            "rs/crates/easydict_app/tests/text_layout_behavior.rs",
            "rs/crates/easydict_app/tests/font_metrics_behavior.rs",
            "rs/crates/easydict_app/tests/document_layout_behavior.rs",
            "rs/crates/easydict_app/tests/latex_formula_behavior.rs",
            "rs/crates/easydict_app/tests/formula_protection_behavior.rs",
            "rs/crates/easydict_app/tests/content_preservation_behavior.rs",
            "rs/crates/easydict_app/tests/formula_text_reconstruction_behavior.rs",
            "rs/crates/easydict_app/tests/character_paragraph_behavior.rs",
            "rs/crates/easydict_app/tests/pdf_formula_adapter_behavior.rs"
        )
        DiffPatterns = @("FormulaProtection", "formula_protection", "content_preservation", "FormulaPreservation", "TextLayout", "text_layout", "FontMetrics", "font_metrics", "DocumentLayout", "document_layout", "latex_formula", "FormulaAwareText", "formula_text_reconstruction", "CharacterParagraph", "character_paragraph", "pdf_formula_adapter", "native_text_long_document_formula")
    }
    "mdx-native" = [pscustomobject]@{
        PathPatterns = @(
            "lib/rs-mdict/**",
            "rs/crates/easydict_app/src/mdx_native.rs",
            "rs/crates/easydict_app/tests/mdx_native_behavior.rs",
            "rs/scripts/Invoke-MdxRealCorpusValidation.ps1"
        )
        DiffPatterns = @("MDX", "MDD", "mdx", "mdd", "rs-mdict", "rust_mdict", "MdxLookupParams", "NativeMdx", "NativeMdd", "mdd_resources_inlined", "RS_MDICT_TEST", "Collins")
    }
    "local-dictionary-suggestions" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_app/src/local_dictionary.rs",
            "rs/crates/easydict_app/src/local_dictionary_index.rs",
            "rs/crates/easydict_app/src/lex_index.rs",
            "rs/crates/easydict_app/src/bin/easydict_lex_index.rs",
            "rs/crates/easydict_app/tests/local_dictionary_index_behavior.rs",
            "rs/crates/easydict_app/tests/lex_index_behavior.rs"
        )
        DiffPatterns = @("dictionary_suggestion", "local_dictionary", "local_dictionary_suggestion", "lex_index", "mdx_index", "fuzzy_hits")
    }
    "pdf-to-images" = [pscustomobject]@{
        PathPatterns = @(
            "lib/easydict-pdf-render/**",
            "rs/crates/easydict_pdf_to_images/**",
            "dotnet/scripts/pdf-to-images.ps1"
        )
        DiffPatterns = @("PdfToImages", "pdf-to-images", "pdf_to_images", "easydict_pdf_to_images", "easydict-pdf-render", "PdfImageFormat", "PdfToImagesOptions", "PdfToBgraOptions", "pdfium-render", "render_pdf_to_images", "render_pdf_pages_to_bgra_files")
    }
    "store-listings" = [pscustomobject]@{
        PathPatterns = @(
            ".github/workflows/store-listings.yml",
            ".winstore/store-config.json",
            ".winstore/listings/**",
            ".winstore/scripts/Sync-StoreListings.ps1",
            ".winstore/README.md",
            "rs/crates/easydict_store_listings/**"
        )
        DiffPatterns = @("easydict_store_listings", "store-listings", "StoreListing", "Store Listings", "Sync-StoreListings.ps1", "MSStore.CLI", "msstore", "winstore", "powershell-yaml", "ConvertFrom-Yaml", "SUPPORTED_STORE_LANGUAGES", "FORBIDDEN_KEYWORD_NAMES", "third-party product", "shortTitle", "voiceTitle", "releaseNotes")
    }
    "encrypt-secret" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_encrypt_secret/**"
        )
        DiffPatterns = @("EncryptSecret", "encrypt-secret", "encrypt_secret", "easydict_encrypt_secret", "-p easydict_encrypt_secret", "easydict_encrypt_secret --", "cargo run --manifest-path ../rs/Cargo.toml -p easydict_encrypt_secret", "SECRET=your-secret", "SECRET is required", "EncryptedSecrets.json", "SecretKeyManager", "AES-128-CBC", "PKCS7", "my-api-key", "SNtcOSNOR+8Y18pItZdXlg")
    }
    "msix-validate" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_msix_validate/**"
        )
        DiffPatterns = @("validate_msix(", "prepare_package_inputs(", "fix_msix_min_version", "verify_bundle_min_version", "dedupe_worker_shared_files", "PackagePayloadLayoutValidator", "AppxManifest", "TargetDeviceFamily", "MinVersion")
    }
    "icon-generator" = [pscustomobject]@{
        PathPatterns = @(
            "rs/crates/easydict_icon_generator/**",
            "dotnet/scripts/generate-windows-assets.ps1",
            "dotnet/scripts/generate-assets-from-macos-icon.ps1",
            "dotnet/scripts/convert-service-icons.ps1"
        )
        DiffPatterns = @("easydict_icon_generator", "icon-generator", "generate-app-icon-ico.ps1", "generate-windows-assets.ps1", "generate-assets-from-macos-icon.ps1", "convert-service-icons.ps1", "windows-assets", "refresh-assets-from-macos-icon", "service-icons", "System.Drawing", "AppIcon.ico", "TrayIcon.png", "ServiceIcons")
    }
    "rust-helper-build" = [pscustomobject]@{
        PathPatterns = @(
            "dotnet/scripts/Build-RustHelpers.ps1"
        )
        DiffPatterns = @("Build-RustHelpers.ps1", "BuildRustHelpers", "build-rust-helpers", "build_rust_helpers", "IncludeLegacyRegistrarAlias", "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS")
    }
    "dotnet-runtime-extract" = [pscustomobject]@{
        PathPatterns = @(
            "dotnet/scripts/Extract-DotnetRuntime.ps1"
        )
        DiffPatterns = @("Extract-DotnetRuntime.ps1", "extract-dotnet-runtime", "ExtractDotnetRuntime", "extract_dotnet_runtime", "hybrid-dotnet-runtime-packaging", "download_and_extract_dotnet_runtime")
    }
    "msix-runtime-profile" = [pscustomobject]@{
        PathPatterns = @(
            ".github/workflows/ui-automation.yml",
            "dotnet/scripts/sign-and-install.ps1",
            "dotnet/scripts/qdc/Deploy-ToQdc.ps1",
            "dotnet/scripts/qdc/Install-OnQdc.ps1",
            "rs/crates/easydict_packager/tests/msix_runtime_profile_contract_behavior.rs"
        )
        DiffPatterns = @("msix_runtime_profile_contract_behavior", "ui_automation_msix_path", "Deploy-ToQdc.ps1", "Install-OnQdc.ps1", "sign-and-install.ps1", "sign_and_install", "qdc_install_machine_scope", "RuntimeProfile", "easydict_msix_validate", "Add-AppxPackage", "Add-AppxProvisionedPackage")
    }
    "rs-portable-release" = [pscustomobject]@{
        PathPatterns = @(
            ".github/workflows/release.yml",
            ".github/workflows/release-publish.yml",
            "rs/crates/easydict_packager/**",
            "rs/scripts/Package-Portable.ps1",
            "rs/README.md"
        )
        DiffPatterns = @("pack-rs-portable", "rs_portable", "validate-rs-portable", "portable ZIP", "release_flavor")
    }
    "runtime-guards" = [pscustomobject]@{
        PathPatterns = @(
            "lib/easydict-runtime-guards/**"
        )
        DiffPatterns = @("easydict-runtime-guards", "easydict_runtime_guards", "RuntimeRoutePolicy", "command_target_is_retained_runtime_or_script_marker", "bytes_contain_retained_runtime_marker", "path_entry_is_retained_runtime_payload_marker", "retained-dotnet-workers")
    }
    "windows-registry" = [pscustomobject]@{
        PathPatterns = @(
            "lib/easydict-windows-registry/**"
        )
        DiffPatterns = @("easydict-windows-registry", "easydict_windows_registry", "WindowsRegistryError", "write_current_user_default_string", "write_current_user_string_value", "read_current_user_default_string", "delete_current_user_tree")
    }
    "nllb-native" = [pscustomobject]@{
        PathPatterns = @(
            "lib/easydict-nllb/**"
        )
        DiffPatterns = @("easydict-nllb", "easydict_nllb", "NllbModelPaths", "NllbTranslator", "HuggingFaceNllbTokenizer", "OrtNllbInferenceEngine", "nllb_language_name_from_code", "ort-openvino", "OpenVINO")
    }
    "pdf-overlay" = [pscustomobject]@{
        PathPatterns = @(
            "lib/easydict-pdf-overlay/**"
        )
        DiffPatterns = @("easydict-pdf-overlay", "easydict_pdf_overlay", "PdfOverlay", "overlay_pdf_text_blocks", "harumi")
    }
    "rust-only-boundary" = [pscustomobject]@{
        PathPatterns = @(
            ".github/workflows/**",
            "dotnet/scripts/**",
            "dotnet/Makefile",
            "rs/crates/easydict_app/src/runtime_policy.rs",
            "rs/crates/easydict_app/tests/default_api_boundary_behavior.rs",
            "rs/crates/easydict_app/tests/cli_translate_behavior.rs",
            "rs/crates/easydict_app/tests/long_document_behavior.rs",
            "rs/crates/easydict_packager/**",
            "lib/easydict-foundry-local/**"
        )
        FallbackPathPatterns = @(
            "rs/crates/easydict_app/src/state.rs",
            "rs/crates/easydict_app/src/lib.rs"
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

function Expand-ProfileList {
    param(
        [string[]]$Profiles
    )

    @($Profiles | ForEach-Object {
            $_ -split "," | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        } | Select-Object -Unique)
}

function Get-ValidationStepCommandKey {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Step
    )

    $Step.Command -join ([char]0)
}

function Select-UniqueValidationSteps {
    param(
        [pscustomobject[]]$Steps
    )

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    @($Steps | Where-Object {
            $key = Get-ValidationStepCommandKey -Step $_
            $seen.Add($key)
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

        [string[]]$Patterns
    )

    if ($null -eq $Patterns -or $Patterns.Count -eq 0) {
        return $false
    }

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

function Test-TrailingWhitespaceCandidatePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $normalized = Normalize-RepoRelativePath $Path
    if ($normalized -match '(^|/)(\.git|target|node_modules)(/|$)') {
        return $false
    }

    $extension = [System.IO.Path]::GetExtension($normalized).ToLowerInvariant()
    $binaryExtensions = @(
        ".7z",
        ".appx",
        ".bin",
        ".dll",
        ".exe",
        ".gif",
        ".ico",
        ".ilk",
        ".jpg",
        ".jpeg",
        ".lib",
        ".mdd",
        ".mdx",
        ".msix",
        ".obj",
        ".onnx",
        ".ort",
        ".pdb",
        ".pdf",
        ".png",
        ".zip"
    )

    $binaryExtensions -notcontains $extension
}

function Get-TrailingWhitespaceCheckPaths {
    param(
        [string[]]$ChangedPath,

        [Parameter(Mandatory = $true)]
        [string]$DiffFrom,

        [Parameter(Mandatory = $true)]
        [string]$DiffTo
    )

    $rawPaths = if ($ChangedPath.Count -gt 0) {
        @(Expand-PathList $ChangedPath)
    }
    else {
        @(Get-GstepDirtyPaths -From $DiffFrom -To $DiffTo)
    }

    $ignoredPaths = @($parallelUiFiles) + @($parallelCargoLockFiles) + @($generatedCargoLockFiles)
    $ignoredPaths = @($ignoredPaths | ForEach-Object { Normalize-RepoRelativePath $_ })
    $selectedPaths = [System.Collections.Generic.List[string]]::new()

    foreach ($rawPath in @($rawPaths)) {
        $normalizedPath = Normalize-RepoRelativePath $rawPath
        if ($ignoredPaths -contains $normalizedPath) {
            continue
        }

        $workspacePath = Join-Path $repoRoot $normalizedPath
        if (Test-Path -LiteralPath $workspacePath -PathType Container) {
            foreach ($file in @(Get-ChildItem -LiteralPath $workspacePath -Recurse -File)) {
                $relativePath = Normalize-RepoRelativePath ([System.IO.Path]::GetRelativePath([string]$repoRoot, $file.FullName))
                if (Test-TrailingWhitespaceCandidatePath -Path $relativePath) {
                    $selectedPaths.Add($relativePath)
                }
            }
        }
        elseif ((Test-Path -LiteralPath $workspacePath -PathType Leaf) -and
            (Test-TrailingWhitespaceCandidatePath -Path $normalizedPath)) {
            $selectedPaths.Add($normalizedPath)
        }
    }

    @($selectedPaths | Select-Object -Unique)
}

function Invoke-TrailingWhitespaceCheck {
    param(
        [string[]]$Paths
    )

    if ($Paths.Count -eq 0) {
        Write-Host "No changed text file(s) need trailing whitespace validation."
        return
    }

    if (-not (Get-Command rg -ErrorAction SilentlyContinue)) {
        throw "rg was not found; install ripgrep or run trailing whitespace validation manually."
    }

    Write-Host "Running validation step [trailing whitespace check]: rg -n ""[ \t]+$"" -- $($Paths -join ' ')"
    & rg "-n" '[ \t]+$' "--" @Paths
    $rgExitCode = if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }
    if ($rgExitCode -eq 0) {
        throw "Trailing whitespace found in changed text file(s)."
    }
    if ($rgExitCode -ne 1) {
        throw "Trailing whitespace check failed with exit code $rgExitCode."
    }

    Write-Host "No trailing whitespace found in changed text file(s)."
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
        [AllowEmptyString()]
        [string]$DiffText,

        [string[]]$AllowedPaths
    )

    $ignoredPaths = @($parallelUiFiles) + @($parallelCargoLockFiles) + @($generatedCargoLockFiles) + @(
        "experience.md",
        "migration-list.md",
        "refactor-progress.md"
    )
    $ignoredPaths = @($ignoredPaths | ForEach-Object { Normalize-RepoRelativePath $_ })
    $allowedPathSet = $null
    $expandedAllowedPaths = @(Expand-PathList $AllowedPaths |
        ForEach-Object { Normalize-RepoRelativePath $_ } |
        Select-Object -Unique)
    if ($expandedAllowedPaths.Count -gt 0) {
        $allowedPathSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($path in $expandedAllowedPaths) {
            [void]$allowedPathSet.Add($path)
        }
    }

    $selectedLines = New-Object System.Collections.Generic.List[string]
    $includeCurrentFile = $false
    foreach ($line in ($DiffText -split "`r?`n")) {
        if ($line -match '^diff --git a/(.+?) b/(.+)$') {
            $currentPath = Normalize-RepoRelativePath $matches[2]
            $allowedBySelector = $null -eq $allowedPathSet -or $allowedPathSet.Contains($currentPath)
            $includeCurrentFile = $allowedBySelector -and $ignoredPaths -notcontains $currentPath
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

        $fallbackPathMatches = @()
        if ($pathMatches.Count -eq 0 -and $null -ne $rules.FallbackPathPatterns) {
            foreach ($path in $corePaths) {
                if (Test-PathMatchesAnyPattern -Path $path -Patterns $rules.FallbackPathPatterns) {
                    $fallbackPathMatches += $path
                }
            }
        }

        $textMatches = @()
        if ($corePaths.Count -gt 0 -and
            (-not $onlyValidationToolingPaths -or $profileName -eq "core-validation-tooling") -and
            -not [string]::IsNullOrWhiteSpace($DiffText)) {
            foreach ($pattern in @($rules.DiffPatterns)) {
                if ($DiffText.IndexOf($pattern, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $textMatches += $pattern
                }
            }
        }

        $pathMatches = @($pathMatches | Select-Object -Unique)
        $fallbackPathMatches = @($fallbackPathMatches | Select-Object -Unique)
        $textMatches = @($textMatches | Select-Object -Unique)
        $score = ($pathMatches.Count * 3) + $fallbackPathMatches.Count + $textMatches.Count
        if ($score -gt 0) {
            $results += [pscustomobject]@{
                Profile = $profileName
                Score = $score
                PathMatches = $pathMatches
                FallbackPathMatches = $fallbackPathMatches
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

function Format-PowerShellCommandArgument {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    if ($Value -match "[\s'`"]") {
        return "'$($Value -replace "'", "''")'"
    }

    $Value
}

function Format-ValidationWrapperCommand {
    param(
        [string[]]$Arguments
    )

    $scriptCommandPrefix = "powershell -NoProfile -ExecutionPolicy Bypass -File rs\scripts\Invoke-RsCoreSliceValidation.ps1"
    $formattedArguments = @($Arguments | ForEach-Object { Format-PowerShellCommandArgument $_ })
    if ($formattedArguments.Count -eq 0) {
        return $scriptCommandPrefix
    }

    "$scriptCommandPrefix $($formattedArguments -join ' ')"
}

function Get-RecommendationSelectorArgumentList {
    param(
        [string[]]$ChangedPath,

        [string]$DiffFrom = "gstep:@",

        [string]$DiffTo = "worktree"
    )

    $selectorArgs = @()
    $expandedChangedPaths = @(Expand-PathList $ChangedPath |
        ForEach-Object { Normalize-RepoRelativePath $_ } |
        Select-Object -Unique)
    if ($expandedChangedPaths.Count -gt 0) {
        $selectorArgs += "-ChangedPath"
        $selectorArgs += ($expandedChangedPaths -join ",")
    }
    if ($DiffFrom -ne "gstep:@") {
        $selectorArgs += "-DiffFrom"
        $selectorArgs += $DiffFrom
    }
    if ($DiffTo -ne "worktree") {
        $selectorArgs += "-DiffTo"
        $selectorArgs += $DiffTo
    }

    @($selectorArgs)
}

function Get-RecommendationSelectorArguments {
    param(
        [string[]]$ChangedPath,

        [string]$DiffFrom = "gstep:@",

        [string]$DiffTo = "worktree"
    )

    $selectorArgs = @(Get-RecommendationSelectorArgumentList -ChangedPath $ChangedPath -DiffFrom $DiffFrom -DiffTo $DiffTo)

    if ($selectorArgs.Count -eq 0) {
        return ""
    }

    " " + (@($selectorArgs | ForEach-Object { Format-PowerShellCommandArgument $_ }) -join " ")
}

function Get-SelectedRecommendationResults {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Recommendation,

        [switch]$AllRecommendedProfiles,

        [int]$MaxRecommendedProfiles = 0
    )

    if ($Recommendation.Results.Count -eq 0) {
        throw "No validation profile matched."
    }

    if ($AllRecommendedProfiles) {
        return @($Recommendation.Results)
    }

    if ($MaxRecommendedProfiles -eq 0) {
        $nonToolingResults = @($Recommendation.Results | Where-Object { $_.Profile -ne "core-validation-tooling" })
        if ($nonToolingResults.Count -gt 0) {
            $topScore = $nonToolingResults[0].Score
            $selected = @($nonToolingResults | Where-Object { $_.Score -eq $topScore })
        }
        else {
            $topScore = $Recommendation.Results[0].Score
            $selected = @($Recommendation.Results | Where-Object { $_.Score -eq $topScore })
        }

        $selectedProfileNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($result in $selected) {
            [void]$selectedProfileNames.Add($result.Profile)
        }

        foreach ($result in @($Recommendation.Results | Where-Object { $_.Profile -eq "core-validation-tooling" -and @($_.PathMatches).Count -gt 0 })) {
            if ($selectedProfileNames.Add($result.Profile)) {
                $selected += $result
            }
        }

        return @($selected)
    }

    @($Recommendation.Results | Select-Object -First $MaxRecommendedProfiles)
}

function New-RecommendedValidationPlan {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Recommendation,

        [switch]$AllRecommendedProfiles,

        [int]$MaxRecommendedProfiles = 0
    )

    $selectedResults = @(Get-SelectedRecommendationResults `
            -Recommendation $Recommendation `
            -AllRecommendedProfiles:$AllRecommendedProfiles `
            -MaxRecommendedProfiles $MaxRecommendedProfiles)

    $steps = @()
    foreach ($selectedResult in $selectedResults) {
        foreach ($step in @($validationProfiles[$selectedResult.Profile].Steps)) {
            $steps += (New-ValidationStep "$($selectedResult.Profile) / $($step.Name)" $step.Command)
        }
    }

    [pscustomobject]@{
        SelectedResults = $selectedResults
        Steps = @(Select-UniqueValidationSteps -Steps $steps)
    }
}

function New-RecommendationCommandReport {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Recommendation,

        [string[]]$ChangedPath,

        [string]$DiffFrom = "gstep:@",

        [string]$DiffTo = "worktree"
    )

    $profileCsv = (@($Recommendation.Results | ForEach-Object { $_.Profile }) -join ",")
    $recommendationSelectorArgList = @(Get-RecommendationSelectorArgumentList -ChangedPath $ChangedPath -DiffFrom $DiffFrom -DiffTo $DiffTo)
    $recommendationSelectorArgs = Get-RecommendationSelectorArguments -ChangedPath $ChangedPath -DiffFrom $DiffFrom -DiffTo $DiffTo

    [pscustomobject]@{
        CombinedCloseOut = if ([string]::IsNullOrWhiteSpace($profileCsv)) { $null } else { Format-ValidationWrapperCommand @("-Profile", $profileCsv) }
        CombinedCloseOutDryRun = if ([string]::IsNullOrWhiteSpace($profileCsv)) { $null } else { Format-ValidationWrapperCommand @("-Profile", $profileCsv, "-DryRun") }
        DefaultFastCloseOut = if ([string]::IsNullOrWhiteSpace($profileCsv)) { $null } else { Format-ValidationWrapperCommand (@("-CloseOut") + $recommendationSelectorArgList) }
        DefaultFastCloseOutDryRun = if ([string]::IsNullOrWhiteSpace($profileCsv)) { $null } else { Format-ValidationWrapperCommand (@("-CloseOut") + $recommendationSelectorArgList + @("-DryRun")) }
        DefaultRecommendedCloseOut = if ([string]::IsNullOrWhiteSpace($profileCsv)) { $null } else { "powershell -NoProfile -ExecutionPolicy Bypass -File rs\scripts\Invoke-RsCoreSliceValidation.ps1 -RunRecommendedProfiles$recommendationSelectorArgs -CheckTrailingWhitespace" }
        DefaultRecommendedCloseOutDryRun = if ([string]::IsNullOrWhiteSpace($profileCsv)) { $null } else { "powershell -NoProfile -ExecutionPolicy Bypass -File rs\scripts\Invoke-RsCoreSliceValidation.ps1 -RunRecommendedProfiles$recommendationSelectorArgs -CheckTrailingWhitespace -DryRun" }
        AllRecommended = if ([string]::IsNullOrWhiteSpace($profileCsv)) { $null } else { "powershell -NoProfile -ExecutionPolicy Bypass -File rs\scripts\Invoke-RsCoreSliceValidation.ps1 -RunRecommendedProfiles$recommendationSelectorArgs -AllRecommendedProfiles" }
        AllRecommendedDryRun = if ([string]::IsNullOrWhiteSpace($profileCsv)) { $null } else { "powershell -NoProfile -ExecutionPolicy Bypass -File rs\scripts\Invoke-RsCoreSliceValidation.ps1 -RunRecommendedProfiles$recommendationSelectorArgs -AllRecommendedProfiles -DryRun" }
        AllRecommendedCloseOut = if ([string]::IsNullOrWhiteSpace($profileCsv)) { $null } else { Format-ValidationWrapperCommand (@("-CloseOut") + $recommendationSelectorArgList + @("-AllRecommendedProfiles")) }
    }
}

function Add-ValidationCloseOutArguments {
    param(
        [string[]]$Arguments,

        [bool]$CheckTrailingWhitespace = $false,

        [string]$GstepCommitMessage
    )

    $result = @($Arguments)
    if ($CheckTrailingWhitespace) {
        $result += "-CheckTrailingWhitespace"
    }
    if (-not [string]::IsNullOrWhiteSpace($GstepCommitMessage)) {
        $result += "-GstepCommitMessage"
        $result += $GstepCommitMessage
    }

    @($result)
}

function Add-ValidationDryRunJsonArguments {
    param(
        [string[]]$Arguments
    )

    @($Arguments) + @("-DryRun", "-Json")
}

function New-ValidationDryRunCommandReport {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Mode,

        [string[]]$SelectedProfiles,

        [bool]$CheckTrailingWhitespace = $false,

        [string]$GstepCommitMessage,

        [string[]]$ChangedPath,

        [string]$DiffFrom = "gstep:@",

        [string]$DiffTo = "worktree",

        [bool]$AllRecommendedProfiles = $false,

        [int]$MaxRecommendedProfiles = 0
    )

    $selectorArgs = @(Get-RecommendationSelectorArgumentList -ChangedPath $ChangedPath -DiffFrom $DiffFrom -DiffTo $DiffTo)
    $currentModeBaseArgs = $null
    switch ($Mode) {
        "run-recommended" {
            $currentModeBaseArgs = @("-RunRecommendedProfiles") + $selectorArgs
            if ($AllRecommendedProfiles) {
                $currentModeBaseArgs += "-AllRecommendedProfiles"
            }
            if ($MaxRecommendedProfiles -ne 0) {
                $currentModeBaseArgs += "-MaxRecommendedProfiles"
                $currentModeBaseArgs += [string]$MaxRecommendedProfiles
            }
        }
        "close-out" {
            $currentModeBaseArgs = @("-CloseOut") + $selectorArgs
            if ($AllRecommendedProfiles) {
                $currentModeBaseArgs += "-AllRecommendedProfiles"
            }
            if ($MaxRecommendedProfiles -ne 0) {
                $currentModeBaseArgs += "-MaxRecommendedProfiles"
                $currentModeBaseArgs += [string]$MaxRecommendedProfiles
            }
        }
        "profile" {
            if (@($SelectedProfiles).Count -gt 0) {
                $currentModeBaseArgs = @("-Profile", (@($SelectedProfiles) -join ","))
                if ($CheckTrailingWhitespace) {
                    $currentModeBaseArgs += $selectorArgs
                }
            }
        }
        "trailing-whitespace" {
            $currentModeBaseArgs = @($selectorArgs)
        }
    }

    $selectedProfileBaseArgs = $null
    if (@($SelectedProfiles).Count -gt 0) {
        $selectedProfileBaseArgs = @("-Profile", (@($SelectedProfiles) -join ","))
        if ($CheckTrailingWhitespace) {
            $selectedProfileBaseArgs += $selectorArgs
        }
    }

    $currentModeAddsExplicitWhitespace = $CheckTrailingWhitespace -and $Mode -ne "close-out"
    $currentModeCloseOutArgs = if ($null -eq $currentModeBaseArgs) {
        $null
    }
    else {
        Add-ValidationCloseOutArguments -Arguments $currentModeBaseArgs -CheckTrailingWhitespace $currentModeAddsExplicitWhitespace -GstepCommitMessage $GstepCommitMessage
    }
    $selectedProfileCloseOutArgs = if ($null -eq $selectedProfileBaseArgs) {
        $null
    }
    else {
        Add-ValidationCloseOutArguments -Arguments $selectedProfileBaseArgs -CheckTrailingWhitespace $CheckTrailingWhitespace -GstepCommitMessage $GstepCommitMessage
    }

    [pscustomobject]@{
        CurrentCloseOut = if ($null -eq $currentModeCloseOutArgs) { $null } else { Format-ValidationWrapperCommand $currentModeCloseOutArgs }
        CurrentDryRunJson = if ($null -eq $currentModeCloseOutArgs) { $null } else { Format-ValidationWrapperCommand (Add-ValidationDryRunJsonArguments $currentModeCloseOutArgs) }
        SelectedProfileCloseOut = if ($null -eq $selectedProfileCloseOutArgs) { $null } else { Format-ValidationWrapperCommand $selectedProfileCloseOutArgs }
        SelectedProfileDryRunJson = if ($null -eq $selectedProfileCloseOutArgs) { $null } else { Format-ValidationWrapperCommand (Add-ValidationDryRunJsonArguments $selectedProfileCloseOutArgs) }
    }
}

function New-StableJsonArray {
    param(
        [AllowEmptyCollection()]
        [object[]]$Items
    )

    return ,[object[]]@($Items)
}

function New-ProfileStepCoverage {
    param(
        [string[]]$Profiles
    )

    @($Profiles | ForEach-Object {
            if (-not $validationProfiles.Contains($_)) {
                return
            }

            $profileName = $_
            $profileSteps = @($validationProfiles[$profileName].Steps)
            [pscustomobject]@{
                Profile = $profileName
                StepCount = $profileSteps.Count
                Steps = @($profileSteps | ForEach-Object {
                        [pscustomobject]@{
                            Name = $_.Name
                            Command = @($_.Command)
                        }
                    })
            }
        })
}

function New-RecommendationReport {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Recommendation,

        [string[]]$ChangedPath,

        [string]$DiffFrom = "gstep:@",

        [string]$DiffTo = "worktree"
    )

    $expandedChangedPaths = @(Expand-PathList $ChangedPath | ForEach-Object { Normalize-RepoRelativePath $_ } | Select-Object -Unique)
    $defaultSelectedPlan = $null
    if ($Recommendation.Results.Count -gt 0) {
        $defaultSelectedPlan = New-RecommendedValidationPlan -Recommendation $Recommendation
    }
    $defaultSelectedProfiles = @()
    $defaultSelectedSteps = @()
    if ($null -ne $defaultSelectedPlan) {
        $defaultSelectedProfiles = @($defaultSelectedPlan.SelectedResults | ForEach-Object { $_.Profile })
        $defaultSelectedSteps = @($defaultSelectedPlan.Steps | ForEach-Object {
                [pscustomobject]@{
                    Name = $_.Name
                    Command = @($_.Command)
                }
            })
    }

    [pscustomobject]@{
        Selector = [pscustomobject]@{
            ChangedPath = $expandedChangedPaths
            DiffFrom = $DiffFrom
            DiffTo = $DiffTo
        }
        IgnoredPaths = @($Recommendation.IgnoredPaths)
        CorePaths = @($Recommendation.CorePaths)
        DefaultSelectedProfiles = (New-StableJsonArray -Items $defaultSelectedProfiles)
        DefaultSelectedStepCount = if ($null -eq $defaultSelectedPlan) { 0 } else { @($defaultSelectedPlan.Steps).Count }
        DefaultSelectedSteps = (New-StableJsonArray -Items $defaultSelectedSteps)
        Results = @($Recommendation.Results | ForEach-Object {
                $profileDefinition = $validationProfiles[$_.Profile]
                [pscustomobject]@{
                    Profile = $_.Profile
                    Score = $_.Score
                    Description = $profileDefinition.Description
                    PathMatches = @($_.PathMatches)
                    FallbackPathMatches = @($_.FallbackPathMatches)
                    TextMatches = @($_.TextMatches)
                    Steps = @($profileDefinition.Steps | ForEach-Object {
                            [pscustomobject]@{
                                Name = $_.Name
                                Command = @($_.Command)
                            }
                        })
                }
            })
        Commands = New-RecommendationCommandReport -Recommendation $Recommendation -ChangedPath $ChangedPath -DiffFrom $DiffFrom -DiffTo $DiffTo
    }
}

function Format-ValidationDryRunText {
    param(
        [pscustomobject[]]$Steps,

        [bool]$CheckTrailingWhitespace = $false,

        [string[]]$TrailingWhitespacePaths,

        [string]$GstepCommitMessage,

        [string]$Header = "Dry run; validation step(s) that would run:",

        [pscustomobject[]]$ProfileStepCoverage,

        [string]$ReadyCloseOutCommand
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add($Header)
    foreach ($step in @($Steps)) {
        $lines.Add("  - $($step.Name): $($step.Command -join ' ')")
    }

    if (@($ProfileStepCoverage).Count -gt 0) {
        $lines.Add("Profile coverage:")
        foreach ($profile in @($ProfileStepCoverage)) {
            $lines.Add("  - $($profile.Profile): $($profile.StepCount) raw step(s)")
        }
    }

    if ($CheckTrailingWhitespace) {
        if (@($TrailingWhitespacePaths).Count -eq 0) {
            $lines.Add("  - trailing whitespace check: no changed text files would be scanned")
        }
        else {
            $lines.Add("  - trailing whitespace check: rg -n ""[ \t]+$"" -- $($TrailingWhitespacePaths -join ' ')")
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($GstepCommitMessage)) {
        $lines.Add("  - gstep checkpoint after successful validation: $(Format-GstepCommitCommandForDisplay -Message $GstepCommitMessage)")
    }

    if (-not [string]::IsNullOrWhiteSpace($ReadyCloseOutCommand)) {
        $lines.Add("  - ready close-out command: $ReadyCloseOutCommand")
    }

    $lines -join "`n"
}

function New-ValidationDryRunReport {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Mode,

        [string[]]$SelectedProfiles,

        [pscustomobject[]]$Steps,

        [bool]$CheckTrailingWhitespace = $false,

        [string[]]$TrailingWhitespacePaths,

        [string]$GstepCommitMessage,

        [pscustomobject]$Recommendation,

        [string[]]$ChangedPath,

        [string]$DiffFrom = "gstep:@",

        [string]$DiffTo = "worktree",

        [bool]$AllRecommendedProfiles = $false,

        [int]$MaxRecommendedProfiles = 0
    )

    $expandedChangedPaths = @(Expand-PathList $ChangedPath |
        ForEach-Object { Normalize-RepoRelativePath $_ } |
        Select-Object -Unique)

    [pscustomobject]@{
        Mode = $Mode
        Selector = [pscustomobject]@{
            ChangedPath = $expandedChangedPaths
            DiffFrom = $DiffFrom
            DiffTo = $DiffTo
        }
        SelectedProfiles = @($SelectedProfiles)
        StepCount = @($Steps).Count
        Steps = @($Steps | ForEach-Object {
                [pscustomobject]@{
                    Name = $_.Name
                    Command = @($_.Command)
                }
            })
        ProfileStepCoverage = (New-StableJsonArray -Items (New-ProfileStepCoverage -Profiles $SelectedProfiles))
        CheckTrailingWhitespace = $CheckTrailingWhitespace
        TrailingWhitespacePaths = @($TrailingWhitespacePaths)
        Commands = New-ValidationDryRunCommandReport `
            -Mode $Mode `
            -SelectedProfiles $SelectedProfiles `
            -CheckTrailingWhitespace $CheckTrailingWhitespace `
            -GstepCommitMessage $GstepCommitMessage `
            -ChangedPath $ChangedPath `
            -DiffFrom $DiffFrom `
            -DiffTo $DiffTo `
            -AllRecommendedProfiles $AllRecommendedProfiles `
            -MaxRecommendedProfiles $MaxRecommendedProfiles
        GstepCheckpoint = if ([string]::IsNullOrWhiteSpace($GstepCommitMessage)) {
            $null
        }
        else {
            [pscustomobject]@{
                Message = $GstepCommitMessage
                Command = @("gstep", "commit", "-m", $GstepCommitMessage)
                Display = Format-GstepCommitCommandForDisplay -Message $GstepCommitMessage
            }
        }
        Recommendation = if ($null -eq $Recommendation) {
            $null
        }
        else {
            [pscustomobject]@{
                CorePaths = @($Recommendation.CorePaths)
                IgnoredPaths = @($Recommendation.IgnoredPaths)
                Results = @($Recommendation.Results | ForEach-Object {
                        [pscustomobject]@{
                            Profile = $_.Profile
                            Score = $_.Score
                            PathMatches = @($_.PathMatches)
                            FallbackPathMatches = @($_.FallbackPathMatches)
                            TextMatches = @($_.TextMatches)
                        }
                    })
            }
        }
    }
}

function Show-ProfileRecommendations {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Recommendation,

        [string[]]$ChangedPath,

        [string]$DiffFrom = "gstep:@",

        [string]$DiffTo = "worktree"
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
        if ($result.FallbackPathMatches.Count -gt 0) {
            Write-Host "    fallback path: $($result.FallbackPathMatches -join ', ')"
        }
        if ($result.TextMatches.Count -gt 0) {
            Write-Host "    diff: $($result.TextMatches -join ', ')"
        }
        Write-Host "    run: powershell -NoProfile -ExecutionPolicy Bypass -File rs\scripts\Invoke-RsCoreSliceValidation.ps1 -Profile $($result.Profile)"
    }

    $defaultSelectedPlan = New-RecommendedValidationPlan -Recommendation $Recommendation
    Write-Host "Default selected profile(s) for -RunRecommendedProfiles:"
    Write-Host "  $((@($defaultSelectedPlan.SelectedResults | ForEach-Object { $_.Profile })) -join ', ')"
    Write-Host "Default selected unique validation step count:"
    Write-Host "  $(@($defaultSelectedPlan.Steps).Count)"
    Write-Host "Default selected validation step(s):"
    foreach ($step in @($defaultSelectedPlan.Steps)) {
        Write-Host "  - $($step.Name): $($step.Command -join ' ')"
    }

    $commandReport = New-RecommendationCommandReport -Recommendation $Recommendation -ChangedPath $ChangedPath -DiffFrom $DiffFrom -DiffTo $DiffTo
    Write-Host "Combined close-out command for listed profile(s):"
    Write-Host "  $($commandReport.CombinedCloseOut)"
    Write-Host "Combined close-out dry-run:"
    Write-Host "  $($commandReport.CombinedCloseOutDryRun)"
    Write-Host "Default fast close-out:"
    Write-Host "  $($commandReport.DefaultFastCloseOut)"
    Write-Host "Default fast close-out dry-run:"
    Write-Host "  $($commandReport.DefaultFastCloseOutDryRun)"
    Write-Host "Default recommended close-out:"
    Write-Host "  $($commandReport.DefaultRecommendedCloseOut)"
    Write-Host "Default recommended close-out dry-run:"
    Write-Host "  $($commandReport.DefaultRecommendedCloseOutDryRun)"
    Write-Host "Run all recommendations through the recommender:"
    Write-Host "  $($commandReport.AllRecommended)"
    Write-Host "Run all recommendations dry-run:"
    Write-Host "  $($commandReport.AllRecommendedDryRun)"
    Write-Host "Run all recommendations with trailing whitespace close-out:"
    Write-Host "  $($commandReport.AllRecommendedCloseOut)"
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
        $changedPathDiff = Get-RecommendationDiffText `
            -DiffText (Get-GstepDiffText -From $DiffFrom -To $DiffTo) `
            -AllowedPaths $recommendationPaths
        if (-not [string]::IsNullOrWhiteSpace($changedPathDiff)) {
            $recommendationDiff = "$recommendationDiff`n$changedPathDiff"
        }
    }
    else {
        $recommendationPaths = Get-GstepDirtyPaths -From $DiffFrom -To $DiffTo
        $recommendationDiff = Get-RecommendationDiffText -DiffText (Get-GstepDiffText -From $DiffFrom -To $DiffTo)
    }

    Get-ProfileRecommendations -Paths $recommendationPaths -DiffText $recommendationDiff
}

function Format-GstepCommitCommandForDisplay {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    $escapedMessage = $Message -replace '"', '\"'
    "gstep commit -m ""$escapedMessage"""
}

function Get-GstepCheckpointAllowedPaths {
    param(
        [string[]]$ChangedPath,

        [Parameter(Mandatory = $true)]
        [string]$DiffFrom,

        [Parameter(Mandatory = $true)]
        [string]$DiffTo
    )

    if ($ChangedPath.Count -gt 0) {
        return @(Expand-PathList $ChangedPath |
            ForEach-Object { Normalize-RepoRelativePath $_ } |
            Select-Object -Unique)
    }

    @(Get-GstepDirtyPaths -From $DiffFrom -To $DiffTo |
        ForEach-Object { Normalize-RepoRelativePath $_ } |
        Select-Object -Unique)
}

function Get-UnexpectedCheckpointPaths {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$AllowedPaths,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$DirtyPaths
    )

    $allowed = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($path in @($AllowedPaths)) {
        [void]$allowed.Add((Normalize-RepoRelativePath $path))
    }

    @($DirtyPaths |
        ForEach-Object { Normalize-RepoRelativePath $_ } |
        Select-Object -Unique |
        Where-Object { -not $allowed.Contains($_) })
}

function Assert-GstepCheckpointScope {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$AllowedPaths
    )

    $dirtyPaths = @(Get-GstepDirtyPaths -From "gstep:@" -To "worktree")
    $unexpectedPaths = @(Get-UnexpectedCheckpointPaths -AllowedPaths $AllowedPaths -DirtyPaths $dirtyPaths)
    if ($unexpectedPaths.Count -gt 0) {
        throw "Refusing to create gstep checkpoint because unexpected path(s) changed during validation: $($unexpectedPaths -join ', '). Include them in -ChangedPath if they belong to this slice, or rerun after isolating parallel work."
    }
}

function Remove-GeneratedCargoLockDrift {
    param(
        [string[]]$Paths
    )

    foreach ($relativePath in @($Paths)) {
        $workspacePath = Join-Path $repoRoot $relativePath
        if (Test-Path -LiteralPath $workspacePath -PathType Leaf) {
            Remove-Item -LiteralPath $workspacePath -Force
        }
    }
}

$profileKeys = @(Expand-ProfileList $Profile)
$hasGstepCommitMessage = $PSBoundParameters.ContainsKey("GstepCommitMessage")

if ($hasGstepCommitMessage -and [string]::IsNullOrWhiteSpace($GstepCommitMessage)) {
    throw "-GstepCommitMessage cannot be blank."
}

if ($Json -and -not $RecommendProfiles -and -not $DryRun -and -not $PlanCloseOut) {
    throw "-Json is only valid with -RecommendProfiles, -PlanCloseOut, or -DryRun."
}

if ($AllRecommendedProfiles -and -not $RunRecommendedProfiles -and -not $CloseOut -and -not $PlanCloseOut) {
    throw "-AllRecommendedProfiles is only valid with -RunRecommendedProfiles, -CloseOut, or -PlanCloseOut."
}
if ($AllRecommendedProfiles -and $MaxRecommendedProfiles -ne 0) {
    throw "-AllRecommendedProfiles cannot be combined with -MaxRecommendedProfiles."
}
if ($MaxRecommendedProfiles -lt 0) {
    throw "-MaxRecommendedProfiles must be greater than or equal to 0."
}

if ($PlanCloseOut) {
    if ($ListProfiles -or $RecommendProfiles -or $CloseOut -or $RunRecommendedProfiles -or $DryRun -or $CheckTrailingWhitespace -or $Command.Count -ne 0 -or $profileKeys.Count -ne 0) {
        throw "-PlanCloseOut cannot be combined with -ListProfiles, -RecommendProfiles, -CloseOut, -RunRecommendedProfiles, -DryRun, -CheckTrailingWhitespace, -Profile, or a validation command."
    }

    Set-Location $repoRoot
    $recommendation = Get-CurrentProfileRecommendation -ChangedPath $ChangedPath -DiffFrom $DiffFrom -DiffTo $DiffTo
    if ($recommendation.Results.Count -eq 0) {
        throw "No validation profile matched; run a custom command or add a profile plus recommendation rules for this lane."
    }

    $recommendedPlan = New-RecommendedValidationPlan `
            -Recommendation $recommendation `
            -AllRecommendedProfiles:$AllRecommendedProfiles `
            -MaxRecommendedProfiles $MaxRecommendedProfiles
    $selectedValidationProfiles = @($recommendedPlan.SelectedResults | ForEach-Object { $_.Profile })
    $trailingWhitespacePaths = @(Get-TrailingWhitespaceCheckPaths -ChangedPath $ChangedPath -DiffFrom $DiffFrom -DiffTo $DiffTo)
    $report = New-ValidationDryRunReport `
        -Mode "close-out" `
        -SelectedProfiles $selectedValidationProfiles `
        -Steps $recommendedPlan.Steps `
        -CheckTrailingWhitespace $true `
        -TrailingWhitespacePaths $trailingWhitespacePaths `
        -GstepCommitMessage $GstepCommitMessage `
        -Recommendation $recommendation `
        -ChangedPath $ChangedPath `
        -DiffFrom $DiffFrom `
        -DiffTo $DiffTo `
        -AllRecommendedProfiles $AllRecommendedProfiles.IsPresent `
        -MaxRecommendedProfiles $MaxRecommendedProfiles

    if ($Json) {
        $report | ConvertTo-Json -Depth 16
    }
    else {
        Show-ProfileRecommendations -Recommendation $recommendation -ChangedPath $ChangedPath -DiffFrom $DiffFrom -DiffTo $DiffTo
        Write-Host (Format-ValidationDryRunText `
                -Steps $recommendedPlan.Steps `
                -CheckTrailingWhitespace $true `
                -TrailingWhitespacePaths $trailingWhitespacePaths `
                -GstepCommitMessage $GstepCommitMessage `
                -Header "Close-out plan; validation step(s) that would run:" `
                -ProfileStepCoverage (New-ProfileStepCoverage -Profiles $selectedValidationProfiles) `
                -ReadyCloseOutCommand $report.Commands.CurrentCloseOut)
    }
    exit 0
}

if ($RecommendProfiles) {
    if ($ListProfiles -or $CloseOut -or $RunRecommendedProfiles -or $DryRun -or $CheckTrailingWhitespace -or $hasGstepCommitMessage -or $Command.Count -ne 0 -or $profileKeys.Count -ne 0) {
        throw "-RecommendProfiles cannot be combined with -ListProfiles, -CloseOut, -RunRecommendedProfiles, -DryRun, -CheckTrailingWhitespace, -GstepCommitMessage, -Profile, or a validation command."
    }
    if ($MaxRecommendedProfiles -ne 0) {
        throw "-MaxRecommendedProfiles is only valid with -RunRecommendedProfiles."
    }

    Set-Location $repoRoot
    $recommendation = Get-CurrentProfileRecommendation -ChangedPath $ChangedPath -DiffFrom $DiffFrom -DiffTo $DiffTo
    if ($Json) {
        New-RecommendationReport -Recommendation $recommendation -ChangedPath $ChangedPath -DiffFrom $DiffFrom -DiffTo $DiffTo |
            ConvertTo-Json -Depth 16
    }
    else {
        Show-ProfileRecommendations -Recommendation $recommendation -ChangedPath $ChangedPath -DiffFrom $DiffFrom -DiffTo $DiffTo
    }
    exit 0
}

if ($ListProfiles) {
    if ($CloseOut -or $RunRecommendedProfiles -or $DryRun -or $CheckTrailingWhitespace -or $hasGstepCommitMessage -or $Command.Count -ne 0 -or $profileKeys.Count -ne 0 -or $ChangedPath.Count -ne 0 -or $DiffFrom -ne "gstep:@" -or $DiffTo -ne "worktree") {
        throw "-ListProfiles cannot be combined with -CloseOut, -RunRecommendedProfiles, -DryRun, -CheckTrailingWhitespace, -GstepCommitMessage, -Profile, -ChangedPath, diff selectors, or a validation command."
    }
    if ($MaxRecommendedProfiles -ne 0) {
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
    if (-not $PlanCloseOut -and -not $CloseOut -and -not $RunRecommendedProfiles -and -not $CheckTrailingWhitespace) {
        throw "-ChangedPath, -DiffFrom, and -DiffTo are only valid with -RecommendProfiles, -PlanCloseOut, -CloseOut, -RunRecommendedProfiles, or -CheckTrailingWhitespace."
    }
}

if ($MaxRecommendedProfiles -ne 0 -and -not $CloseOut -and -not $RunRecommendedProfiles -and -not $PlanCloseOut) {
    throw "-MaxRecommendedProfiles is only valid with -RunRecommendedProfiles, -CloseOut, or -PlanCloseOut."
}
if ($hasGstepCommitMessage -and $NoParallelUiIsolation) {
    throw "-GstepCommitMessage cannot be combined with -NoParallelUiIsolation; keep UI/parity isolation enabled so checkpoints do not absorb parallel work."
}

$modeCount = 0
if ($profileKeys.Count -ne 0) { $modeCount += 1 }
if ($Command.Count -ne 0) { $modeCount += 1 }
if ($RunRecommendedProfiles) { $modeCount += 1 }
if ($CloseOut) { $modeCount += 1 }
if ($modeCount -gt 1) {
    throw "Use only one of -Profile, -CloseOut, -RunRecommendedProfiles, or one validation command. For custom cargo commands with flags such as '-p', pass the child command through a PowerShell argument array splat (for example, `$cmdArgs = @('cargo', 'test', '-p', 'easydict_app'); ...ps1 @cmdArgs`) so wrapper/common parameters do not capture them."
}

$validationMode = $null
$selectedValidationProfiles = @()
$recommendationForDryRun = $null
$validationSteps = @()
$effectiveCheckTrailingWhitespace = $CheckTrailingWhitespace.IsPresent -or $CloseOut.IsPresent
if ($RunRecommendedProfiles -or $CloseOut) {
    Set-Location $repoRoot
    $recommendation = Get-CurrentProfileRecommendation -ChangedPath $ChangedPath -DiffFrom $DiffFrom -DiffTo $DiffTo
    $recommendationForDryRun = $recommendation
    if (-not $Json) {
        Show-ProfileRecommendations -Recommendation $recommendation -ChangedPath $ChangedPath -DiffFrom $DiffFrom -DiffTo $DiffTo
    }
    if ($recommendation.Results.Count -eq 0) {
        throw "No validation profile matched; run a custom command or add a profile plus recommendation rules for this lane."
    }

    $recommendedPlan = New-RecommendedValidationPlan `
            -Recommendation $recommendation `
            -AllRecommendedProfiles:$AllRecommendedProfiles `
            -MaxRecommendedProfiles $MaxRecommendedProfiles
    $selectedValidationProfiles = @($recommendedPlan.SelectedResults | ForEach-Object { $_.Profile })
    if (-not $Json) {
        Write-Host "Selected recommended validation profile(s): $($selectedValidationProfiles -join ', ')"
    }
    $validationMode = if ($CloseOut) { "close-out" } else { "run-recommended" }
    $validationSteps = @($recommendedPlan.Steps)
}
elseif ($profileKeys.Count -ne 0) {
    foreach ($profileKey in $profileKeys) {
        if (-not $validationProfiles.Contains($profileKey)) {
            throw "Unknown validation profile '$profileKey'. Use -ListProfiles to see available profiles."
        }
    }

    if ($profileKeys.Count -eq 1) {
        $validationSteps = @($validationProfiles[$profileKeys[0]].Steps)
    }
    else {
        foreach ($profileKey in $profileKeys) {
            foreach ($step in @($validationProfiles[$profileKey].Steps)) {
                $validationSteps += (New-ValidationStep "$profileKey / $($step.Name)" $step.Command)
            }
        }
    }
    $selectedValidationProfiles = @($profileKeys)
    $validationMode = "profile"
}
elseif ($Command.Count -ne 0) {
    $validationSteps = @((New-ValidationStep "custom" $Command))
    $validationMode = "custom"
}
elseif ($effectiveCheckTrailingWhitespace) {
    $validationSteps = @()
    $validationMode = "trailing-whitespace"
}
else {
    throw "Provide one validation command, -Profile <name>, -CloseOut, -RunRecommendedProfiles, -ListProfiles, -RecommendProfiles, or -CheckTrailingWhitespace."
}

$validationSteps = @(Select-UniqueValidationSteps -Steps $validationSteps)
$trailingWhitespacePaths = @()
if ($effectiveCheckTrailingWhitespace) {
    Set-Location $repoRoot
    $trailingWhitespacePaths = @(Get-TrailingWhitespaceCheckPaths -ChangedPath $ChangedPath -DiffFrom $DiffFrom -DiffTo $DiffTo)
}

if ($DryRun) {
    if ($Json) {
        New-ValidationDryRunReport `
            -Mode $validationMode `
            -SelectedProfiles $selectedValidationProfiles `
            -Steps $validationSteps `
            -CheckTrailingWhitespace $effectiveCheckTrailingWhitespace `
            -TrailingWhitespacePaths $trailingWhitespacePaths `
            -GstepCommitMessage $GstepCommitMessage `
            -Recommendation $recommendationForDryRun `
            -ChangedPath $ChangedPath `
            -DiffFrom $DiffFrom `
            -DiffTo $DiffTo `
            -AllRecommendedProfiles $AllRecommendedProfiles.IsPresent `
            -MaxRecommendedProfiles $MaxRecommendedProfiles |
            ConvertTo-Json -Depth 16
        exit 0
    }

    Write-Host (Format-ValidationDryRunText `
            -Steps $validationSteps `
            -CheckTrailingWhitespace $effectiveCheckTrailingWhitespace `
            -TrailingWhitespacePaths $trailingWhitespacePaths `
            -GstepCommitMessage $GstepCommitMessage `
            -ProfileStepCoverage (New-ProfileStepCoverage -Profiles $selectedValidationProfiles))
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

function Copy-ItemWithRetry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LiteralPath,

        [Parameter(Mandatory = $true)]
        [string]$Destination,

        [Parameter(Mandatory = $true)]
        [string]$OperationName,

        [int]$MaxAttempts = 5
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Force
            return
        }
        catch {
            if ($attempt -eq $MaxAttempts) {
                throw
            }

            Write-Host "Retrying $OperationName after transient file copy failure: $($_.Exception.Message)"
            Start-Sleep -Milliseconds (250 * $attempt)
        }
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

$checkpointAllowedPaths = @()
if ($hasGstepCommitMessage) {
    $checkpointAllowedPaths = @(Get-GstepCheckpointAllowedPaths -ChangedPath $ChangedPath -DiffFrom $DiffFrom -DiffTo $DiffTo)
}

$tempBase = [System.IO.Path]::GetTempPath()
$tempRoot = Join-Path $tempBase ("easydict-rs-core-slice-" + [System.Guid]::NewGuid().ToString("N"))
$backupRoot = Join-Path $tempRoot "backup"
$materializedRoot = Join-Path $tempRoot "gstep-at"
$isolatedFiles = @()
$generatedCargoLockFilesAbsentBeforeRun = @($generatedCargoLockFiles | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $repoRoot $_) -PathType Leaf)
    })
$previousRustTestNocapture = $env:RUST_TEST_NOCAPTURE
$enableRustTestNocapture = $RustTestNocapture.IsPresent -or $profileKeys.Count -ne 0
$commandExitCode = 1
$validationMutex = [System.Threading.Mutex]::new($false, "Local\EasydictRsCoreSliceValidation")
$validationMutexAcquired = $false
$cleanupErrors = [System.Collections.Generic.List[string]]::new()

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
                Copy-ItemWithRetry -LiteralPath $workspacePath -Destination $backupPath -OperationName "backup $relativePath"
                if ($materializedFileExists) {
                    Copy-ItemWithRetry -LiteralPath $materializedPath -Destination $workspacePath -OperationName "isolate $relativePath"
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

    if ($commandExitCode -eq 0 -and $effectiveCheckTrailingWhitespace) {
        try {
            Invoke-TrailingWhitespaceCheck -Paths $trailingWhitespacePaths
        }
        catch {
            Write-Host "Trailing whitespace check failed: $($_.Exception.Message)"
            $commandExitCode = 1
        }
    }

    if ($commandExitCode -eq 0 -and $hasGstepCommitMessage) {
        Remove-GeneratedCargoLockDrift -Paths $generatedCargoLockFilesAbsentBeforeRun
        if (-not $NoParallelUiIsolation) {
            $checkpointDiffText = (& gstep diff "gstep:@" "worktree" "--json" | Out-String)
            if ($LASTEXITCODE -ne 0) {
                throw "gstep diff gstep:@ worktree --json failed with exit code $LASTEXITCODE"
            }

            $checkpointDiff = $checkpointDiffText | ConvertFrom-Json
            $checkpointDirtyFiles = @($checkpointDiff.files)
            $normalizedParallelUiFiles = @($parallelUiFiles | ForEach-Object { Normalize-RepoRelativePath $_ })
            $normalizedGeneratedCargoLockFiles = @($generatedCargoLockFiles | ForEach-Object { Normalize-RepoRelativePath $_ })
            $normalizedAlreadyIsolatedFiles = @($isolatedFiles | ForEach-Object { Normalize-RepoRelativePath $_ })
            $lateParallelFiles = @($checkpointDirtyFiles | Where-Object {
                    $normalizedPath = Normalize-RepoRelativePath $_.path
                    $normalizedParallelUiFiles -contains $normalizedPath -or $normalizedGeneratedCargoLockFiles -contains $normalizedPath
                })

            if ($lateParallelFiles.Count -gt 0) {
                Write-Host "Temporarily isolating $($lateParallelFiles.Count) late parallel UI/parity or generated file(s) before checkpoint."
                $lateMaterializedRoot = Join-Path $tempRoot "gstep-at-checkpoint"
                Invoke-GstepChecked @("materialize", "gstep:@", $lateMaterializedRoot)

                foreach ($entry in $lateParallelFiles) {
                    $relativePath = $entry.path
                    $normalizedRelativePath = Normalize-RepoRelativePath $relativePath
                    $alreadyIsolated = $normalizedAlreadyIsolatedFiles -contains $normalizedRelativePath
                    $workspacePath = Join-Path $repoRoot $relativePath
                    $backupPath = Join-Path $backupRoot $relativePath
                    $materializedPath = Join-Path $lateMaterializedRoot $relativePath

                    if (-not (Test-Path -LiteralPath $workspacePath -PathType Leaf)) {
                        throw "Cannot back up missing workspace file: $relativePath"
                    }
                    $materializedFileExists = Test-Path -LiteralPath $materializedPath -PathType Leaf
                    if (-not $materializedFileExists -and $entry.status -ne "A") {
                        throw "gstep:@ materialization does not contain: $relativePath"
                    }

                    if (-not $alreadyIsolated) {
                        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backupPath) | Out-Null
                        Copy-ItemWithRetry -LiteralPath $workspacePath -Destination $backupPath -OperationName "backup late $relativePath"
                    }
                    if ($materializedFileExists) {
                        Copy-ItemWithRetry -LiteralPath $materializedPath -Destination $workspacePath -OperationName "re-isolate late $relativePath"
                    }
                    else {
                        Remove-Item -LiteralPath $workspacePath -Force
                    }
                    if (-not $alreadyIsolated) {
                        $isolatedFiles += $relativePath
                    }
                }
            }
        }

        $checkpointDirtyPaths = @(Get-GstepDirtyPaths -From "gstep:@" -To "worktree")
        $unexpectedPaths = @(Get-UnexpectedCheckpointPaths -AllowedPaths $checkpointAllowedPaths -DirtyPaths $checkpointDirtyPaths)
        if ($unexpectedPaths.Count -gt 0) {
            throw "Refusing to create gstep checkpoint because unexpected path(s) changed during validation: $($unexpectedPaths -join ', '). Include them in -ChangedPath if they belong to this slice, or rerun after isolating parallel work."
        }
        if ($checkpointDirtyPaths.Count -eq 0) {
            Write-Host "Skipping post-validation checkpoint because no dirty paths remain after validation."
        }
        else {
            Write-Host "Running post-validation checkpoint: $(Format-GstepCommitCommandForDisplay -Message $GstepCommitMessage)"
            Invoke-GstepChecked @("commit", "-m", $GstepCommitMessage)
        }
    }
}
finally {
    foreach ($relativePath in $isolatedFiles) {
        $workspacePath = Join-Path $repoRoot $relativePath
        $backupPath = Join-Path $backupRoot $relativePath
        if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
            try {
                Copy-ItemWithRetry -LiteralPath $backupPath -Destination $workspacePath -OperationName "restore $relativePath"
            }
            catch {
                $cleanupErrors.Add("Failed to restore ${relativePath}: $($_.Exception.Message)")
            }
        }
    }

    foreach ($relativePath in $generatedCargoLockFilesAbsentBeforeRun) {
        $workspacePath = Join-Path $repoRoot $relativePath
        if (Test-Path -LiteralPath $workspacePath -PathType Leaf) {
            try {
                Remove-Item -LiteralPath $workspacePath -Force
            }
            catch {
                $cleanupErrors.Add("Failed to remove generated lock drift ${relativePath}: $($_.Exception.Message)")
            }
        }
    }

    try {
        Remove-TempTree -Path $tempRoot -TempBase $tempBase
    }
    catch {
        $cleanupErrors.Add("Failed to remove temporary validation tree ${tempRoot}: $($_.Exception.Message)")
    }

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

if ($cleanupErrors.Count -gt 0) {
    foreach ($cleanupError in $cleanupErrors) {
        Write-Warning $cleanupError
    }
    exit 1
}

if ($commandExitCode -ne 0) {
    exit $commandExitCode
}
