use easydict_packager::{
    build_rust_helpers, pack_rs_portable, validate_rs_portable_payload, BuildRustHelpersOptions,
    PackRustPortableOptions, ValidateRustPortableOptions,
};
use std::ffi::OsString;
use std::fs;
use std::io::BufReader;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use zip::ZipArchive;

static ENVIRONMENT_LOCK: Mutex<()> = Mutex::new(());

#[test]
fn rs_portable_release_path_forces_rust_only_runtime_profile() {
    let root = repo_root();
    let workflow = read_text(&root.join(".github/workflows/release-publish.yml"));
    let portable_script = read_text(&root.join("rs/scripts/Package-Portable.ps1"));

    assert_contains(
        &workflow,
        "default: 'rs-portable'",
        "release workflow should default to rs portable, not hybrid",
    );
    assert_contains(
        &workflow,
        "publish-rs-portable:",
        "release workflow should keep a dedicated rs portable job",
    );
    assert_contains(
        &workflow,
        "EASYDICT_RUNTIME_PROFILE: rust-only",
        "rs portable workflow job should force Easydict runtime profile",
    );
    assert_contains(
        &workflow,
        "RUNTIME_PROFILE: rust-only",
        "rs portable workflow job should force generic runtime profile",
    );
    assert_contains(
        &workflow,
        "Package-Portable.ps1",
        "workflow should use the Rust portable packaging shim",
    );
    assert_contains(
        &workflow,
        "validate-rs-portable",
        "workflow should validate the staged/ZIP rs portable payload",
    );
    assert_contains(
        &workflow,
        "RETAINED_WORKERS_ENABLED=true",
        "workflow should mark retained worker steps only after explicit hybrid validation",
    );
    assert!(
        !workflow.contains("if: env.RUNTIME_PROFILE != 'rust-only'"),
        "release workflow should not use negative rust-only checks for retained worker/runtime steps"
    );

    assert_contains(
        &portable_script,
        "$env:EASYDICT_RUNTIME_PROFILE = \"rust-only\"",
        "portable shim should force Easydict runtime profile before invoking cargo",
    );
    assert_contains(
        &portable_script,
        "$env:RUNTIME_PROFILE = \"rust-only\"",
        "portable shim should force generic runtime profile before invoking cargo",
    );
    assert_contains(
        &portable_script,
        "pack-rs-portable",
        "portable shim should delegate staging and validation to the Rust packager",
    );
    assert_contains(
        &portable_script,
        "Remove-Item Env:EASYDICT_RUNTIME_PROFILE",
        "portable shim should restore absent Easydict runtime profile after packaging",
    );
    assert_contains(
        &portable_script,
        "Remove-Item Env:RUNTIME_PROFILE",
        "portable shim should restore absent generic runtime profile after packaging",
    );
}

#[test]
fn arm64_msix_smoke_requires_explicit_hybrid_runtime_profile() {
    let root = repo_root();
    let workflow = read_text(&root.join(".github/workflows/arm64-msix-smoke.yml"));
    let workflow_dispatch = text_between(&workflow, "  workflow_dispatch:", "\npermissions:");

    assert_contains(
        workflow_dispatch,
        "runtime_profile:",
        "ARM64 MSIX smoke should keep a runtime profile input for explicit hybrid runs",
    );
    assert_contains(
        workflow_dispatch,
        "default: ''",
        "ARM64 MSIX smoke should require the caller to enter hybrid explicitly",
    );
    assert_contains(
        workflow_dispatch,
        "type: string",
        "ARM64 MSIX smoke runtime_profile should allow a blank default",
    );
    assert_not_contains(
        workflow_dispatch,
        "default: 'hybrid'",
        "ARM64 MSIX smoke must not silently default retained runtime packaging to hybrid",
    );
    assert_contains(
        &workflow,
        "RUNTIME_PROFILE: ${{ github.event.inputs.runtime_profile || '' }}",
        "ARM64 MSIX smoke should not turn an omitted runtime_profile into hybrid before validation",
    );
    assert_not_contains(
        &workflow,
        "github.event.inputs.runtime_profile || 'hybrid'",
        "ARM64 MSIX smoke must not use a fallback hybrid runtime profile",
    );
}

#[test]
fn release_workflow_default_tag_path_runs_only_rs_portable_jobs_and_gates_hybrid_assets() {
    let root = repo_root();
    let workflow = read_text(&root.join(".github/workflows/release-publish.yml"));
    let workflow_header = text_between(&workflow, "name: Release and Publish", "\non:");
    let workflow_dispatch = text_between(&workflow, "  workflow_dispatch:", "\npermissions:");
    let integration_tests_job = text_between(&workflow, "  integration-tests:", "  publish-msix:");
    let publish_msix_job = text_between(&workflow, "  publish-msix:", "  publish-rs-portable:");
    let publish_rs_portable_job =
        text_between(&workflow, "  publish-rs-portable:", "  create-bundle:");
    let create_bundle_job = text_between(
        &workflow,
        "  create-bundle:",
        "  create-rs-portable-release:",
    );
    let create_rs_portable_release_job = text_between(
        &workflow,
        "  create-rs-portable-release:",
        "  publish-winget:",
    );
    let publish_winget_job = workflow
        .split_once("  publish-winget:")
        .unwrap_or_else(|| panic!("missing section start: publish-winget"))
        .1;

    assert_contains(
        &workflow,
        "default: 'rs-portable'",
        "release workflow dispatch should default to the rs portable artifact set",
    );
    assert_contains(
        workflow_dispatch,
        "runtime_profile:",
        "release workflow dispatch should still expose a runtime profile for explicit hybrid runs",
    );
    assert_contains(
        workflow_dispatch,
        "default: ''",
        "release workflow dispatch should require a caller-provided hybrid runtime profile",
    );
    assert_contains(
        workflow_dispatch,
        "type: string",
        "release workflow dispatch runtime_profile should allow blank default instead of a default hybrid choice",
    );
    assert_not_contains(
        workflow_dispatch,
        "default: 'hybrid'",
        "release workflow dispatch must not silently default retained runtime packaging to hybrid",
    );
    assert_contains(
        &workflow,
        "RELEASE_FLAVOR: ${{ github.event.inputs.release_flavor || 'rs-portable' }}",
        "tag-triggered releases should normalize the absent release flavor to rs-portable",
    );
    assert_contains(
        workflow_header,
        "formal rs portable release by default",
        "release workflow comments should describe stable tag pushes as rs portable by default",
    );
    assert_contains(
        workflow_header,
        "WinGet is submitted only for stable tags when release_flavor=hybrid",
        "release workflow comments should keep WinGet tied to explicit hybrid release flavor",
    );
    assert_not_contains(
        workflow_header,
        "formal release (e.g. v1.2.3), submitted to WinGet",
        "release workflow comments must not imply default stable tags publish the legacy WinGet package",
    );

    let publish_rs_portable_header =
        text_between(publish_rs_portable_job, "    name:", "    steps:");
    assert_contains(
        publish_rs_portable_header,
        "if: ${{ (github.event.inputs.release_flavor || 'rs-portable') == 'rs-portable' }}",
        "publish-rs-portable should be positively gated to the default/tag rs-portable flavor",
    );
    assert_contains(
        publish_rs_portable_job,
        "needs: prepare",
        "publish-rs-portable should only depend on the shared version preparation job",
    );
    assert_contains(
        create_rs_portable_release_job,
        "if: ${{ (github.event.inputs.release_flavor || 'rs-portable') == 'rs-portable' }}",
        "the rs release upload job should be positively gated to the default rs-portable flavor",
    );
    assert_contains(
        create_rs_portable_release_job,
        "needs: [prepare, publish-rs-portable]",
        "the default release upload path should not wait on hybrid .NET/MSIX jobs",
    );

    for (job_name, job) in [
        ("integration-tests", integration_tests_job),
        ("publish-msix", publish_msix_job),
        ("create-bundle", create_bundle_job),
        ("publish-winget", publish_winget_job),
    ] {
        let gate_line = job
            .lines()
            .find(|line| line.trim_start().starts_with("if:"))
            .unwrap_or_else(|| panic!("{job_name} should define a job-level release_flavor gate"));
        assert_contains(
            gate_line,
            "(github.event.inputs.release_flavor || 'rs-portable') == 'hybrid'",
            &format!("{job_name} should be positively gated to release_flavor == 'hybrid'"),
        );
        for forbidden_condition in [
            "!= 'rs-portable'",
            "!= \"rs-portable\"",
            "!= 'rust'",
            "!= \"rust\"",
            "!= 'rust-only'",
            "!= \"rust-only\"",
        ] {
            assert_not_contains(
                gate_line,
                forbidden_condition,
                &format!("{job_name} should not use a negative rust/rs-portable gate"),
            );
        }
    }

    let publish_rs_portable_gate_line = publish_rs_portable_header
        .lines()
        .find(|line| line.trim_start().starts_with("if:"))
        .unwrap_or_else(|| {
            panic!("publish-rs-portable should define a job-level release_flavor gate")
        });
    assert_contains(
        publish_rs_portable_gate_line,
        "(github.event.inputs.release_flavor || 'rs-portable') == 'rs-portable'",
        "publish-rs-portable should be positively gated to release_flavor == 'rs-portable'",
    );
    assert_contains(
        publish_msix_job,
        "RUNTIME_PROFILE: ${{ github.event.inputs.runtime_profile || '' }}",
        "publish-msix should require explicit runtime_profile input instead of defaulting to hybrid",
    );
    assert_contains(
        create_bundle_job,
        "RUNTIME_PROFILE: ${{ github.event.inputs.runtime_profile || '' }}",
        "create-bundle should require explicit runtime_profile input instead of defaulting to hybrid",
    );
    for section in [publish_msix_job, create_bundle_job] {
        assert_not_contains(
            section,
            "github.event.inputs.runtime_profile || 'hybrid'",
            "hybrid release jobs must not silently default runtime_profile to hybrid",
        );
    }
    for forbidden_condition in [
        "!= 'hybrid'",
        "!= \"hybrid\"",
        "!= 'dotnet'",
        "!= \"dotnet\"",
        "!= 'dotnet-hybrid'",
        "!= \"dotnet-hybrid\"",
    ] {
        assert_not_contains(
            publish_rs_portable_gate_line,
            forbidden_condition,
            "publish-rs-portable should not use a negative hybrid/dotnet gate",
        );
    }
}

#[test]
fn release_workflow_hybrid_flavor_does_not_build_or_upload_rs_portable_assets() {
    let root = repo_root();
    let workflow = read_text(&root.join(".github/workflows/release-publish.yml"));
    let publish_rs_portable_job =
        text_between(&workflow, "  publish-rs-portable:", "  create-bundle:");
    let create_bundle_job = text_between(
        &workflow,
        "  create-bundle:",
        "  create-rs-portable-release:",
    );
    let create_rs_portable_release_job = text_between(
        &workflow,
        "  create-rs-portable-release:",
        "  publish-winget:",
    );

    assert_contains(
        publish_rs_portable_job,
        "if: ${{ (github.event.inputs.release_flavor || 'rs-portable') == 'rs-portable' }}",
        "rs portable packaging should only run for the rs-portable artifact set",
    );
    assert_contains(
        create_bundle_job,
        "if: ${{ (github.event.inputs.release_flavor || 'rs-portable') == 'hybrid' }}",
        "hybrid bundle publishing should only run for the hybrid artifact set",
    );
    assert_contains(
        create_bundle_job,
        "needs: [prepare, publish-msix]",
        "hybrid bundle publishing should not wait on rs portable packaging",
    );
    assert_not_contains(
        create_bundle_job,
        "publish-rs-portable",
        "hybrid bundle publishing should not depend on the rs portable job",
    );
    for forbidden_marker in [
        "pattern: easydict-rs-portable-*",
        "path: rs-portable",
        "rs-portable/*.zip",
    ] {
        assert_not_contains(
            create_bundle_job,
            forbidden_marker,
            &format!(
                "hybrid bundle publishing should not download or upload rs portable marker {forbidden_marker}"
            ),
        );
    }
    assert_contains(
        create_rs_portable_release_job,
        "needs: [prepare, publish-rs-portable]",
        "the rs portable release upload job should keep owning rs portable assets",
    );
}

#[test]
fn rs_portable_release_jobs_stay_isolated_from_dotnet_artifacts() {
    let root = repo_root();
    let workflow = read_text(&root.join(".github/workflows/release-publish.yml"));
    let publish_job = text_between(&workflow, "  publish-rs-portable:", "  create-bundle:");
    let release_job = text_between(
        &workflow,
        "  create-rs-portable-release:",
        "  publish-winget:",
    );

    assert_contains(
        publish_job,
        "working-directory: rs",
        "rs portable packaging should run from the Rust workspace",
    );
    assert_contains(
        publish_job,
        "Package-Portable.ps1",
        "rs portable packaging should delegate to the Rust portable shim",
    );
    assert_contains(
        publish_job,
        "cargo test -p easydict_packager --test release_contract_behavior",
        "rs portable release should run packager release-contract tests before packaging",
    );
    assert_contains(
        publish_job,
        "cargo test -p easydict_app runtime_policy --lib",
        "rs portable release should run default runtime-policy unit tests before packaging",
    );
    assert_contains(
        publish_job,
        "cargo test -p easydict_app --features retained-dotnet-workers runtime_policy --lib",
        "rs portable release should prove retained-worker feature builds still require explicit runtime opt-in",
    );
    assert_contains(
        publish_job,
        "cargo test -p easydict_app --features retained-dotnet-workers --test quick_translate_behavior explicit_worker_policy_without_hybrid_runtime_profile_stays_rust_only -- --exact --nocapture",
        "rs portable release should prove Quick Translate retained-worker policy injection still requires explicit hybrid runtime",
    );
    assert_contains(
        publish_job,
        "cargo test -p easydict_app --features retained-dotnet-workers --test long_document_behavior explicit_longdoc_worker_policy_without_hybrid_runtime_profile_stays_rust_only -- --exact --nocapture",
        "rs portable release should prove LongDoc retained-worker policy injection still requires explicit hybrid runtime",
    );
    assert_contains(
        publish_job,
        "cargo test -p easydict_app --test default_api_boundary_behavior",
        "rs portable release should run default app API/runtime boundary tests before packaging",
    );
    assert_contains(
        publish_job,
        "cargo test -p easydict_app --test protocol_behavior",
        "rs portable release should run default protocol facade tests before packaging",
    );
    assert_contains(
        publish_job,
        "cargo test -p easydict_app --test cli_translate_behavior local_ai_cli_app_dir_ignores_stale_dotnet_payload_markers -- --exact --nocapture",
        "rs portable release should prove CLI --app-dir ignores stale retained .NET payload markers",
    );
    assert_contains(
        publish_job,
        "cargo test -p easydict_app --test long_document_cli_behavior app_dir_ignores_stale_dotnet_payload_markers_and_does_not_enable_worker_lookup -- --exact --nocapture",
        "rs portable release should prove LongDoc --app-dir ignores stale retained .NET payload markers",
    );
    assert_contains(
        publish_job,
        "validate-rs-portable",
        "rs portable packaging should validate the ZIP before upload",
    );
    assert_contains(
        publish_job,
        "easydict-rs-portable-${{ matrix.platform }}",
        "rs portable upload artifact should remain separately named from legacy assets",
    );

    assert_contains(
        release_job,
        "needs: [prepare, publish-rs-portable]",
        "first rs release should depend only on version parsing and rs portable packaging",
    );
    assert_contains(
        release_job,
        "pattern: easydict-rs-portable-*",
        "first rs release should download only rs portable artifacts",
    );
    assert_contains(
        release_job,
        "rs-portable/*.zip",
        "first rs release should upload only rs portable ZIP assets",
    );

    for (section_name, section) in [
        ("publish-rs-portable", publish_job),
        ("create-rs-portable-release", release_job),
    ] {
        for forbidden_marker in [
            "actions/setup-dotnet",
            "setup-WinAppCli",
            "dotnet/",
            "dotnet\\",
            "dotnet ",
            "publish-msix",
            "create-bundle",
            "easydict-msix",
            "easydict-installer",
            "installer-packages",
            "Package-Msix.ps1",
            "Extract-DotnetRuntime.ps1",
            "Easydict.Workers.",
            "Easydict.CompatHost",
            "WinGet",
            "winget",
        ] {
            assert_not_contains(
                section,
                forbidden_marker,
                &format!(
                    "{section_name} should not touch legacy .NET/MSIX/installer release marker {forbidden_marker}"
                ),
            );
        }
    }
}

#[test]
fn ci_workflow_runs_default_rs_rust_only_boundary_tests() {
    let root = repo_root();
    let workflow = read_text(&root.join(".github/workflows/ci.yml"));
    let rust_only_job = text_between(&workflow, "  rust-only-boundary:", "  build-and-test:");

    assert_contains(
        rust_only_job,
        "EASYDICT_RUNTIME_PROFILE: rust-only",
        "default CI Rust boundary job should force the Easydict runtime profile",
    );
    assert_contains(
        rust_only_job,
        "RUNTIME_PROFILE: rust-only",
        "default CI Rust boundary job should force the generic runtime profile",
    );
    assert_contains(
        rust_only_job,
        "working-directory: rs",
        "default CI Rust boundary tests should run from the Rust workspace",
    );
    assert_contains(
        rust_only_job,
        "cargo test -p easydict_packager --test release_contract_behavior ci_workflow_runs_default_rs_rust_only_boundary_tests",
        "default CI should keep a self-check for the Rust-only boundary job",
    );
    assert_contains(
        rust_only_job,
        "cargo test -p easydict_packager --test release_contract_behavior pack_rs_portable_creates_and_validates_zip_without_retained_dotnet_payload -- --exact --nocapture",
        "default CI should validate a real rs portable ZIP payload before release tags",
    );
    assert_contains(
        rust_only_job,
        "cargo test -p easydict_app runtime_policy --lib",
        "default CI should run Rust runtime-policy tests",
    );
    assert_contains(
        rust_only_job,
        "cargo test -p easydict_app --test default_api_boundary_behavior",
        "default CI should run default API/runtime boundary tests",
    );
    assert_contains(
        rust_only_job,
        "cargo test -p easydict_app --test cli_translate_behavior local_ai_cli_app_dir_ignores_stale_dotnet_payload_markers -- --exact --nocapture",
        "default CI should prove CLI --app-dir ignores stale retained .NET payload markers",
    );
    assert_contains(
        rust_only_job,
        "cargo test -p easydict_app --test long_document_cli_behavior app_dir_ignores_stale_dotnet_payload_markers_and_does_not_enable_worker_lookup -- --exact --nocapture",
        "default CI should prove LongDoc --app-dir ignores stale retained .NET payload markers",
    );
    assert_not_contains(
        rust_only_job,
        "retained-dotnet-workers",
        "default CI Rust boundary job must not enable retained worker features",
    );
    assert_not_contains(
        rust_only_job,
        "setup-dotnet",
        "default CI Rust boundary job must not set up .NET",
    );
}

#[test]
fn root_readmes_mark_winget_as_legacy_hybrid_not_default_rs_install() {
    let root = repo_root();

    for (relative_path, expected_notice) in [
        ("README.md", "WinGet is a legacy/hybrid install path"),
        ("README_ZH.md", "legacy/hybrid"),
    ] {
        let text = read_text(&root.join(relative_path));
        let portable_index = text
            .find("easydict-rs-portable-vX.Y.Z-win-x64.zip")
            .unwrap_or_else(|| panic!("{relative_path} should recommend the rs portable ZIP"));
        let before_default_install = &text[..portable_index];
        let legacy_index = text.find("Legacy/Hybrid").unwrap_or_else(|| {
            panic!("{relative_path} should keep a Legacy/Hybrid install section")
        });
        let winget_index = text
            .find("winget install xiaocang.EasydictforWindows")
            .unwrap_or_else(|| panic!("{relative_path} should still document the winget command"));

        assert!(
            portable_index < legacy_index,
            "{relative_path} should present the default Rust portable ZIP before legacy/hybrid installs",
        );
        assert!(
            legacy_index < winget_index,
            "{relative_path} should place WinGet under Legacy/Hybrid, not before the default rs portable install",
        );
        assert_contains(
            &text,
            expected_notice,
            &format!("{relative_path} should label WinGet as legacy/hybrid instead of default rs install"),
        );
        for forbidden_marker in [
            "WinGet",
            "winget",
            "apps.microsoft.com",
            "get.microsoft.com",
            "Microsoft Store",
        ] {
            assert_not_contains(
                before_default_install,
                forbidden_marker,
                &format!(
                    "{relative_path} should not advertise legacy store/install CTAs before the default rs portable ZIP"
                ),
            );
        }
        let legacy_install_section = &text[legacy_index..winget_index];
        assert_contains(
            legacy_install_section,
            ".NET",
            &format!("{relative_path} should tie WinGet to the retained .NET package"),
        );
        assert_contains(
            legacy_install_section,
            "Rust",
            &format!("{relative_path} should say WinGet is not the default Rust portable install"),
        );
    }
}

#[test]
fn root_readmes_build_from_source_default_to_rs_portable_before_legacy_dotnet() {
    let root = repo_root();

    for (relative_path, build_heading, legacy_heading) in [
        (
            "README.md",
            "### Build from Source",
            "#### Legacy/Hybrid .NET Build",
        ),
        (
            "README_ZH.md",
            "### 从源码构建",
            "#### Legacy/Hybrid .NET 构建",
        ),
    ] {
        let text = read_text(&root.join(relative_path));
        let build_index = text
            .find(build_heading)
            .unwrap_or_else(|| panic!("{relative_path} should keep a build-from-source section"));
        let legacy_index = text
            .find(legacy_heading)
            .unwrap_or_else(|| panic!("{relative_path} should keep a legacy .NET build section"));
        let portable_command_index = text[build_index..]
            .find(r".\rs\scripts\Package-Portable.ps1")
            .map(|offset| build_index + offset)
            .unwrap_or_else(|| {
                panic!("{relative_path} should build the rs portable package by default")
            });
        let cargo_run_index = text[build_index..]
            .find("cargo run -p easydict_app --bin easydict_preview")
            .map(|offset| build_index + offset)
            .unwrap_or_else(|| {
                panic!("{relative_path} should document the Rust app development command")
            });
        let dotnet_build_index = text[build_index..]
            .find("dotnet build src/Easydict.WinUI/Easydict.WinUI.csproj")
            .map(|offset| build_index + offset)
            .unwrap_or_else(|| panic!("{relative_path} should still document legacy .NET build"));

        assert!(
            build_index < portable_command_index,
            "{relative_path} should put the rs portable command in the build-from-source section",
        );
        assert!(
            portable_command_index < cargo_run_index,
            "{relative_path} should show packaging before development run",
        );
        assert!(
            cargo_run_index < legacy_index && legacy_index < dotnet_build_index,
            "{relative_path} should place dotnet build commands only under the legacy/hybrid subsection",
        );

        let default_build_section = &text[build_index..legacy_index];
        assert_not_contains(
            default_build_section,
            "dotnet build",
            &format!("{relative_path} default source build should not invoke dotnet"),
        );
        assert_not_contains(
            default_build_section,
            "dotnet run",
            &format!("{relative_path} default source run should not invoke dotnet"),
        );
    }
}

#[test]
fn root_readmes_tech_stack_and_distribution_keep_rs_portable_as_default() {
    let root = repo_root();

    for (
        relative_path,
        tech_heading,
        distribution_heading,
        expected_rust_stack,
        expected_distribution,
    ) in [
        (
            "README.md",
            "## Tech Stack",
            "### Distribution",
            "**Rust** - Default portable app",
            "**Rust portable ZIP** - Default first rs release path",
        ),
        (
            "README_ZH.md",
            "## 技术栈",
            "### 分发",
            "**Rust** - 默认便携应用",
            "**Rust 便携 ZIP** - 第一版 rs 默认发布路径",
        ),
    ] {
        let text = read_text(&root.join(relative_path));
        let tech_index = text
            .find(tech_heading)
            .unwrap_or_else(|| panic!("{relative_path} should keep a tech stack section"));
        let distribution_index = text
            .find(distribution_heading)
            .unwrap_or_else(|| panic!("{relative_path} should keep a distribution section"));
        let next_section = text[distribution_index + distribution_heading.len()..]
            .find("\n## ")
            .map(|offset| distribution_index + distribution_heading.len() + offset)
            .unwrap_or(text.len());

        let tech_section = &text[tech_index..distribution_index];
        let distribution_section = &text[distribution_index..next_section];

        assert_contains(
            tech_section,
            expected_rust_stack,
            &format!("{relative_path} should present Rust as the default runtime"),
        );
        assert_contains(
            tech_section,
            "Legacy/hybrid",
            &format!("{relative_path} should label .NET/WinUI as legacy/hybrid"),
        );
        let rust_index = tech_section
            .find("**Rust**")
            .expect("Rust stack entry should exist");
        let dotnet_index = tech_section
            .find(".NET 8 + WinUI 3")
            .expect(".NET legacy stack entry should exist");
        assert!(
            rust_index < dotnet_index,
            "{relative_path} should list Rust before the legacy .NET stack entry",
        );

        assert_contains(
            distribution_section,
            expected_distribution,
            &format!("{relative_path} should list the rs portable ZIP as the default distribution"),
        );
        assert_contains(
            distribution_section,
            "Legacy/hybrid",
            &format!("{relative_path} should label Store/WinGet distribution as legacy/hybrid"),
        );
        let portable_index = distribution_section
            .find("Rust")
            .expect("Rust distribution entry should exist");
        let store_index = distribution_section
            .find("Store")
            .or_else(|| distribution_section.find("Windows 商店"))
            .expect("Store distribution entry should exist");
        assert!(
            portable_index < store_index,
            "{relative_path} should list the Rust portable distribution before Store/WinGet",
        );
    }
}

#[test]
fn windows_ai_build_script_has_strict_rs_portable_binding_gate() {
    let root = repo_root();
    let build_script = read_text(&root.join("lib/easydict-windows-ai/build.rs"));

    assert_contains(
        &build_script,
        "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS",
        "WindowsAI build script should expose an opt-in strict binding gate for release packaging",
    );
    assert_contains(
        &build_script,
        "cargo:rerun-if-env-changed={WINDOWS_AI_REQUIRE_BINDINGS_ENV}",
        "WindowsAI build script should make Cargo rerun when the strict binding gate changes",
    );
    assert_contains(
        &build_script,
        "panic!",
        "strict WindowsAI binding mode should fail the build instead of silently shipping the unsupported fallback",
    );
    assert_contains(
        &build_script,
        "easydict_windows_ai_winrt_bindings",
        "successful binding generation should still set the native WinRT cfg",
    );
}

#[test]
fn rs_portable_release_provisions_windows_ai_winmd_metadata_before_cargo() {
    let root = repo_root();
    let workflow = read_text(&root.join(".github/workflows/release-publish.yml"));
    let publish_job = text_between(&workflow, "  publish-rs-portable:", "  create-bundle:");

    let prepare_metadata_index = publish_job
        .find("Prepare Windows App SDK AI metadata")
        .expect("publish-rs-portable should prepare WindowsAI WinMD metadata");
    let verify_contracts_index = publish_job
        .find("Verify Rust-only release contracts")
        .expect("publish-rs-portable should run release contract tests");
    let package_index = publish_job
        .find("Build Rust portable ZIP")
        .expect("publish-rs-portable should build the portable ZIP");
    assert!(
        prepare_metadata_index < verify_contracts_index && prepare_metadata_index < package_index,
        "WindowsAI WinMD metadata must be available before cargo tests or packaging run"
    );

    for required in [
        "microsoft.windowsappsdk.ai",
        "https://api.nuget.org/v3-flatcontainer/$packageId/index.json",
        "Microsoft.Windows.AI.winmd",
        "Microsoft.Windows.AI.Foundation.winmd",
        "Microsoft.Windows.AI.Text.winmd",
        "EASYDICT_WINDOWS_APP_SDK_AI_METADATA_DIR=$metadataDir",
        "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS=1",
        "$env:GITHUB_ENV",
    ] {
        assert_contains(
            publish_job,
            required,
            &format!(
                "publish-rs-portable should provision official WindowsAI metadata: {required}"
            ),
        );
    }

    assert_not_contains(
        publish_job,
        "actions/setup-dotnet",
        "rs portable release should not require setup-dotnet to fetch WindowsAI WinMD metadata",
    );
}

#[test]
fn hybrid_msix_paths_provision_windows_ai_winmd_metadata_before_rust_helpers() {
    let root = repo_root();
    let release_workflow = read_text(&root.join(".github/workflows/release-publish.yml"));
    let publish_msix_job = text_between(
        &release_workflow,
        "  publish-msix:",
        "  publish-rs-portable:",
    );
    let arm64_smoke_workflow = read_text(&root.join(".github/workflows/arm64-msix-smoke.yml"));

    for (section_name, section) in [
        ("publish-msix", publish_msix_job),
        ("arm64-msix-smoke", arm64_smoke_workflow.as_str()),
    ] {
        let prepare_metadata_index = section
            .find("Prepare Windows App SDK AI metadata")
            .unwrap_or_else(|| panic!("{section_name} should prepare WindowsAI WinMD metadata"));
        let build_helpers_index = section
            .find("Build-RustHelpers.ps1")
            .unwrap_or_else(|| panic!("{section_name} should build Rust helper executables"));
        assert!(
            prepare_metadata_index < build_helpers_index,
            "{section_name} must provision WindowsAI metadata before Build-RustHelpers.ps1"
        );

        for required in [
            "microsoft.windowsappsdk.ai",
            "https://api.nuget.org/v3-flatcontainer/$packageId/index.json",
            "Microsoft.Windows.AI.winmd",
            "Microsoft.Windows.AI.Foundation.winmd",
            "Microsoft.Windows.AI.Text.winmd",
            "EASYDICT_WINDOWS_APP_SDK_AI_METADATA_DIR=$metadataDir",
            "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS=1",
            "$env:GITHUB_ENV",
        ] {
            assert_contains(
                section,
                required,
                &format!("{section_name} should provision official WindowsAI metadata: {required}"),
            );
        }
    }
}

#[test]
fn release_orchestration_uses_rust_helpers_not_retired_dotnet_helper_projects() {
    let root = repo_root();
    let build_helpers = read_text(&root.join("dotnet/scripts/Build-RustHelpers.ps1"));

    assert_contains(
        &build_helpers,
        "-p",
        "Build-RustHelpers shim should invoke cargo with an explicit package",
    );
    assert_contains(
        &build_helpers,
        "easydict_packager",
        "Build-RustHelpers shim should delegate helper build/copy logic to the Rust packager",
    );
    assert_contains(
        &build_helpers,
        "build-rust-helpers",
        "Build-RustHelpers shim should use the Rust build-rust-helpers subcommand",
    );

    for relative_path in [
        ".github/workflows/release-publish.yml",
        ".github/workflows/arm64-msix-smoke.yml",
        "dotnet/Makefile",
        "dotnet/scripts/Build-RustHelpers.ps1",
        "dotnet/scripts/publish.ps1",
        "dotnet/scripts/package-and-install.ps1",
        "dotnet/scripts/Package-Msix.ps1",
        "rs/scripts/Package-Portable.ps1",
    ] {
        let text = read_text(&root.join(relative_path));
        for retired_marker in [
            "src/Easydict.NativeBridge",
            "src/Easydict.BrowserRegistrar",
            "src/Easydict.CompatHost",
            "Easydict.NativeBridge.csproj",
            "Easydict.BrowserRegistrar.csproj",
            "Easydict.CompatHost.csproj",
        ] {
            assert_not_contains(
                &text,
                retired_marker,
                &format!(
                    "{relative_path} must not resurrect retired .NET helper project {retired_marker}"
                ),
            );
        }
    }
}

#[test]
fn build_rust_helpers_child_cargo_is_forced_to_rust_only_runtime_profile() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture([
        "PATH",
        "EASYDICT_RUNTIME_PROFILE",
        "RUNTIME_PROFILE",
        "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS",
        "EASYDICT_FAKE_CARGO_RECORD",
    ]);
    let test_root = tempfile_dir("packager-build-rust-helpers-env");
    let fake_bin = test_root.join("bin");
    let workspace = test_root.join("workspace");
    let output_dir = test_root.join("out");
    fs::create_dir_all(&workspace).expect("create fake workspace");
    fs::write(workspace.join("Cargo.toml"), "[workspace]\n").expect("write fake Cargo.toml");
    write_fake_windows_ai_manifest_for_workspace(&workspace);
    fs::create_dir_all(&output_dir).expect("create output dir");
    write_fake_tooling_scripts(&fake_bin);
    let record_path = test_root.join("cargo-env.txt");

    let path_with_fake_tools = prepend_path(&fake_bin, environment.original_path());
    std::env::set_var("PATH", path_with_fake_tools);
    std::env::set_var("EASYDICT_RUNTIME_PROFILE", "hybrid");
    std::env::set_var("RUNTIME_PROFILE", "hybrid");
    std::env::set_var("EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS", "0");
    std::env::set_var("EASYDICT_FAKE_CARGO_RECORD", &record_path);

    let outcome = build_rust_helpers(&BuildRustHelpersOptions {
        rust_workspace: workspace.clone(),
        platform: "x64".to_string(),
        configuration: "Release".to_string(),
        output_dir: output_dir.clone(),
    })
    .expect("build helpers should run fake cargo and copy generated helpers");

    assert_eq!(outcome.cargo_target, "x86_64-pc-windows-msvc");
    assert_eq!(outcome.profile_dir, "release");
    let record = read_text(&record_path);
    assert_contains(
        &record,
        "EASYDICT_RUNTIME_PROFILE=rust-only",
        "build-rust-helpers child cargo should override inherited Easydict runtime profile",
    );
    assert_contains(
        &record,
        "RUNTIME_PROFILE=rust-only",
        "build-rust-helpers child cargo should override inherited generic runtime profile",
    );
    assert_eq!(
        record
            .lines()
            .filter(|line| *line == "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS=1")
            .count(),
        2,
        "build-rust-helpers should require WindowsAI WinRT bindings for the preflight and helper build:\n{record}"
    );
    assert_contains(
        &record,
        "ARGS=check --manifest-path",
        "build-rust-helpers should preflight WindowsAI WinRT bindings before helper builds",
    );
    assert_contains(
        &record,
        "easydict-windows-ai",
        "WindowsAI preflight should target the easydict-windows-ai manifest",
    );
    assert_contains(
        &record,
        "build -p easydict_app --target x86_64-pc-windows-msvc --bin easydict-native-bridge --bin easydict_browser_registrar --bin easydict_cli --bin easydict_long_doc --release",
        "build-rust-helpers should keep the expected helper cargo command line",
    );
    for exe_name in [
        "easydict-native-bridge.exe",
        "easydict_browser_registrar.exe",
        "easydict_cli.exe",
        "easydict_long_doc.exe",
    ] {
        assert!(
            output_dir.join(exe_name).is_file(),
            "{exe_name} should be copied from fake cargo output"
        );
    }

    let _ = fs::remove_dir_all(test_root);
}

#[test]
fn rustup_target_add_is_forced_to_rust_only_runtime_profile() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture([
        "PATH",
        "EASYDICT_RUNTIME_PROFILE",
        "RUNTIME_PROFILE",
        "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS",
        "EASYDICT_FAKE_CARGO_RECORD",
        "EASYDICT_FAKE_RUSTUP_RECORD",
    ]);
    let test_root = tempfile_dir("packager-rustup-env");
    let fake_bin = test_root.join("bin");
    let workspace = test_root.join("workspace");
    let output_dir = test_root.join("out");
    fs::create_dir_all(&workspace).expect("create fake workspace");
    fs::write(workspace.join("Cargo.toml"), "[workspace]\n").expect("write fake Cargo.toml");
    write_fake_windows_ai_manifest_for_workspace(&workspace);
    fs::create_dir_all(&output_dir).expect("create output dir");
    write_fake_tooling_scripts(&fake_bin);
    let cargo_record_path = test_root.join("cargo-env.txt");
    let rustup_record_path = test_root.join("rustup-env.txt");

    let path_with_fake_tools = prepend_path(&fake_bin, environment.original_path());
    std::env::set_var("PATH", path_with_fake_tools);
    std::env::set_var("EASYDICT_RUNTIME_PROFILE", "hybrid");
    std::env::set_var("RUNTIME_PROFILE", "hybrid");
    std::env::set_var("EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS", "0");
    std::env::set_var("EASYDICT_FAKE_CARGO_RECORD", &cargo_record_path);
    std::env::set_var("EASYDICT_FAKE_RUSTUP_RECORD", &rustup_record_path);

    build_rust_helpers(&BuildRustHelpersOptions {
        rust_workspace: workspace,
        platform: "x64".to_string(),
        configuration: "Release".to_string(),
        output_dir,
    })
    .expect("build helpers should run fake rustup and cargo");

    let record = read_text(&rustup_record_path);
    assert_contains(
        &record,
        "TOOL=rustup",
        "fake rustup should record the target-add invocation",
    );
    assert_contains(
        &record,
        "EASYDICT_RUNTIME_PROFILE=rust-only",
        "rustup target add should override inherited Easydict runtime profile",
    );
    assert_contains(
        &record,
        "RUNTIME_PROFILE=rust-only",
        "rustup target add should override inherited generic runtime profile",
    );
    assert_contains(
        &record,
        "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS=1",
        "rustup target add should share the strict rs portable child-tool environment",
    );
    assert_contains(
        &record,
        "ARGS=target add x86_64-pc-windows-msvc",
        "rustup should still receive the expected target-add command line",
    );
    assert_not_contains(
        &record,
        "hybrid",
        "rustup target add must not inherit hybrid runtime-profile values",
    );

    let _ = fs::remove_dir_all(test_root);
}

#[test]
fn pack_rs_portable_child_cargo_is_forced_to_rust_only_runtime_profile() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture([
        "PATH",
        "EASYDICT_RUNTIME_PROFILE",
        "RUNTIME_PROFILE",
        "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS",
        "EASYDICT_FAKE_CARGO_RECORD",
    ]);
    let test_root = tempfile_dir("packager-pack-rs-portable-env");
    let fake_bin = test_root.join("bin");
    let workspace = test_root.join("workspace");
    let output_root = test_root.join("out");
    fs::create_dir_all(&workspace).expect("create fake workspace");
    fs::write(workspace.join("Cargo.toml"), "[workspace]\n").expect("write fake Cargo.toml");
    write_fake_windows_ai_manifest_for_workspace(&workspace);
    fs::create_dir_all(&output_root).expect("create output root");
    write_fake_tooling_scripts(&fake_bin);
    let record_path = test_root.join("cargo-env.txt");

    let path_with_fake_tools = prepend_path(&fake_bin, environment.original_path());
    std::env::set_var("PATH", path_with_fake_tools);
    std::env::set_var("EASYDICT_RUNTIME_PROFILE", "hybrid");
    std::env::set_var("RUNTIME_PROFILE", "hybrid");
    std::env::set_var("EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS", "0");
    std::env::set_var("EASYDICT_FAKE_CARGO_RECORD", &record_path);

    let outcome = pack_rs_portable(&PackRustPortableOptions {
        rust_workspace: workspace.clone(),
        platform: "x64".to_string(),
        configuration: "Release".to_string(),
        output_root: output_root.clone(),
        package_version: Some("v0.0.0-test".to_string()),
        create_zip: false,
    })
    .expect("pack-rs-portable should run fake cargo and stage a validated payload");

    assert_eq!(
        outcome.package_name,
        "easydict-rs-portable-v0.0.0-test-win-x64"
    );
    assert!(outcome.zip_path.is_none());
    assert_eq!(outcome.file_count, 6);
    assert_eq!(outcome.directory_validation_entries, 6);

    let record = read_text(&record_path);
    assert_eq!(
        record
            .lines()
            .filter(|line| *line == "EASYDICT_RUNTIME_PROFILE=rust-only")
            .count(),
        3,
        "pack-rs-portable should force Easydict rust-only env for the preflight and both child cargo builds:\n{record}"
    );
    assert_eq!(
        record
            .lines()
            .filter(|line| *line == "RUNTIME_PROFILE=rust-only")
            .count(),
        3,
        "pack-rs-portable should force generic rust-only env for the preflight and both child cargo builds:\n{record}"
    );
    assert_eq!(
        record
            .lines()
            .filter(|line| *line == "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS=1")
            .count(),
        3,
        "pack-rs-portable should require WindowsAI WinRT bindings for preflight, preview, and helper builds:\n{record}"
    );
    let cargo_args = record
        .lines()
        .filter(|line| line.starts_with("ARGS="))
        .collect::<Vec<_>>();
    assert!(
        cargo_args
            .first()
            .is_some_and(|line| line.contains("check --manifest-path")
                && line.contains("easydict-windows-ai")
                && line.contains("--target x86_64-pc-windows-msvc")),
        "pack-rs-portable should preflight WindowsAI WinRT bindings before package builds:\n{record}"
    );
    assert_contains(
        &record,
        "ARGS=build -p easydict_preview_iced --target x86_64-pc-windows-msvc --release",
        "pack-rs-portable should build the preview app without enabling retained features",
    );
    assert_contains(
        &record,
        "ARGS=build -p easydict_app --target x86_64-pc-windows-msvc --bin easydict-native-bridge --bin easydict_browser_registrar --bin easydict_cli --bin easydict_long_doc --release",
        "pack-rs-portable should build Rust helpers without enabling retained features",
    );
    for forbidden_marker in [
        "retained-dotnet-workers",
        "--features",
        "--all-features",
        "Easydict.Workers",
        "CompatHost",
    ] {
        assert_not_contains(
            &record,
            forbidden_marker,
            &format!("pack-rs-portable child cargo must not enable retained runtime marker {forbidden_marker}"),
        );
    }

    let package_dir = outcome.package_dir;
    for entry in [
        "Easydict.Rust.exe",
        "easydict-native-bridge.exe",
        "easydict_browser_registrar.exe",
        "easydict_cli.exe",
        "easydict_long_doc.exe",
        "README-portable.txt",
    ] {
        assert!(
            package_dir.join(entry).is_file(),
            "{entry} should be staged in the first rs portable payload"
        );
    }

    let _ = fs::remove_dir_all(test_root);
}

#[test]
fn pack_rs_portable_creates_and_validates_zip_without_retained_dotnet_payload() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture([
        "PATH",
        "EASYDICT_RUNTIME_PROFILE",
        "RUNTIME_PROFILE",
        "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS",
        "EASYDICT_FAKE_CARGO_RECORD",
    ]);
    let test_root = tempfile_dir("packager-pack-rs-portable-zip");
    let fake_bin = test_root.join("bin");
    let workspace = test_root.join("workspace");
    let output_root = test_root.join("out");
    let target_release = workspace
        .join("target")
        .join("x86_64-pc-windows-msvc")
        .join("release");
    fs::create_dir_all(&workspace).expect("create fake workspace");
    fs::write(workspace.join("Cargo.toml"), "[workspace]\n").expect("write fake Cargo.toml");
    write_fake_windows_ai_manifest_for_workspace(&workspace);
    write_stale_dotnet_payload_markers(&target_release);
    fs::create_dir_all(&output_root).expect("create output root");
    write_fake_tooling_scripts(&fake_bin);
    let record_path = test_root.join("cargo-env.txt");

    std::env::set_var("PATH", prepend_path(&fake_bin, environment.original_path()));
    std::env::set_var("EASYDICT_RUNTIME_PROFILE", "hybrid");
    std::env::set_var("RUNTIME_PROFILE", "hybrid");
    std::env::set_var("EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS", "0");
    std::env::set_var("EASYDICT_FAKE_CARGO_RECORD", &record_path);

    let outcome = pack_rs_portable(&PackRustPortableOptions {
        rust_workspace: workspace.clone(),
        platform: "x64".to_string(),
        configuration: "Release".to_string(),
        output_root: output_root.clone(),
        package_version: Some("v0.0.0-zip".to_string()),
        create_zip: true,
    })
    .expect("pack-rs-portable should stage, zip, and validate the first rs portable payload");

    let zip_path = outcome
        .zip_path
        .as_ref()
        .expect("pack-rs-portable should report the created ZIP path");
    assert!(
        zip_path.is_file(),
        "pack-rs-portable should create a real ZIP at {}",
        zip_path.display()
    );
    assert!(
        outcome.package_dir.is_dir(),
        "pack-rs-portable should keep the staged directory for validation"
    );
    assert_eq!(
        outcome.zip_validation_entries,
        Some(6),
        "pack-rs-portable should validate the ZIP payload after creating it"
    );

    let directory_validation = validate_rs_portable_payload(&ValidateRustPortableOptions {
        package_path: outcome.package_dir.clone(),
    })
    .expect("staged rs portable directory should validate");
    let zip_validation = validate_rs_portable_payload(&ValidateRustPortableOptions {
        package_path: zip_path.clone(),
    })
    .expect("created rs portable ZIP should validate");
    assert_eq!(directory_validation.checked_entries, 6);
    assert_eq!(zip_validation.checked_entries, 6);

    let expected_entries = vec![
        "Easydict.Rust.exe".to_string(),
        "README-portable.txt".to_string(),
        "easydict-native-bridge.exe".to_string(),
        "easydict_browser_registrar.exe".to_string(),
        "easydict_cli.exe".to_string(),
        "easydict_long_doc.exe".to_string(),
    ];
    let directory_entries = directory_entry_names(&outcome.package_dir);
    let zip_entries = zip_entry_names(zip_path);
    assert_eq!(
        directory_entries, expected_entries,
        "staged rs portable directory should contain only the first-release Rust payload"
    );
    assert_eq!(
        zip_entries, expected_entries,
        "created rs portable ZIP should contain only the first-release Rust payload"
    );
    assert_entries_do_not_contain_retained_dotnet_payload(&directory_entries, "staged directory");
    assert_entries_do_not_contain_retained_dotnet_payload(&zip_entries, "created ZIP");

    let _ = fs::remove_dir_all(test_root);
}

#[cfg(windows)]
#[test]
fn pack_rs_portable_zip_extracts_to_cli_smoke_without_dotnet_or_powershell() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture([
        "PATH",
        "EASYDICT_RUNTIME_PROFILE",
        "RUNTIME_PROFILE",
        "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS",
        "EASYDICT_FAKE_CARGO_RECORD",
        "EASYDICT_PACKAGED_CLI_RECORD",
        "EASYDICT_RELEASE_FORBIDDEN_TOOL_RECORD",
    ]);
    let test_root = tempfile_dir("packager-pack-rs-portable-cli-smoke");
    let fake_bin = test_root.join("bin");
    let workspace = test_root.join("workspace");
    let output_root = test_root.join("out");
    let extract_dir = test_root.join("extract");
    fs::create_dir_all(&workspace).expect("create fake workspace");
    fs::write(workspace.join("Cargo.toml"), "[workspace]\n").expect("write fake Cargo.toml");
    write_fake_windows_ai_manifest_for_workspace(&workspace);
    fs::create_dir_all(&output_root).expect("create output root");
    fs::create_dir_all(&extract_dir).expect("create extract root");
    write_fake_tooling_scripts(&fake_bin);
    write_fake_release_forbidden_tool_exes(&fake_bin);
    let cargo_record_path = test_root.join("cargo-env.txt");
    let cli_record_path = test_root.join("packaged-cli.txt");
    let forbidden_tool_record = test_root.join("forbidden-tools.txt");

    std::env::set_var("PATH", prepend_path(&fake_bin, environment.original_path()));
    std::env::set_var("EASYDICT_RUNTIME_PROFILE", "hybrid");
    std::env::set_var("RUNTIME_PROFILE", "hybrid");
    std::env::set_var("EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS", "0");
    std::env::set_var("EASYDICT_FAKE_CARGO_RECORD", &cargo_record_path);

    let outcome = pack_rs_portable(&PackRustPortableOptions {
        rust_workspace: workspace.clone(),
        platform: "x64".to_string(),
        configuration: "Release".to_string(),
        output_root: output_root.clone(),
        package_version: Some("v0.0.0-cli-smoke".to_string()),
        create_zip: true,
    })
    .expect("pack-rs-portable should create a validated ZIP with executable Rust helpers");

    let zip_path = outcome.zip_path.as_ref().expect("created ZIP path");
    extract_zip(zip_path, &extract_dir);
    let packaged_cli = extract_dir.join("easydict_cli.exe");
    assert!(
        packaged_cli.is_file(),
        "extracted rs portable ZIP should contain easydict_cli.exe"
    );

    let output = std::process::Command::new(&packaged_cli)
        .arg("--help")
        .env("PATH", prepend_path(&fake_bin, environment.original_path()))
        .env("EASYDICT_RUNTIME_PROFILE", "rust-only")
        .env("RUNTIME_PROFILE", "rust-only")
        .env("EASYDICT_PACKAGED_CLI_RECORD", &cli_record_path)
        .env(
            "EASYDICT_RELEASE_FORBIDDEN_TOOL_RECORD",
            &forbidden_tool_record,
        )
        .output()
        .expect("run extracted packaged easydict_cli.exe");

    assert!(
        output.status.success(),
        "extracted packaged CLI smoke should succeed\nstdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    assert!(
        !forbidden_tool_record.exists(),
        "extracted packaged CLI smoke must not invoke dotnet.exe, powershell.exe, or pwsh.exe"
    );
    let cli_record = read_text(&cli_record_path);
    assert_contains(
        &cli_record,
        "CLI=easydict_cli",
        "extracted packaged CLI smoke should execute the helper from the ZIP",
    );
    assert_contains(
        &cli_record,
        "ARGS=--help",
        "extracted packaged CLI smoke should pass through the requested arguments",
    );
    assert_contains(
        &cli_record,
        "EASYDICT_RUNTIME_PROFILE=rust-only",
        "extracted packaged CLI smoke should run under the first-release rust-only profile",
    );
    assert_contains(
        &cli_record,
        "RUNTIME_PROFILE=rust-only",
        "extracted packaged CLI smoke should run under the generic rust-only profile",
    );

    let _ = fs::remove_dir_all(test_root);
}

#[cfg(windows)]
#[test]
fn pack_rs_portable_zip_extracts_to_gui_entrypoint_smoke_without_dotnet_or_powershell() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture([
        "PATH",
        "EASYDICT_RUNTIME_PROFILE",
        "RUNTIME_PROFILE",
        "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS",
        "EASYDICT_FAKE_CARGO_RECORD",
        "EASYDICT_PACKAGED_GUI_RECORD",
        "EASYDICT_RELEASE_FORBIDDEN_TOOL_RECORD",
    ]);
    let test_root = tempfile_dir("packager-pack-rs-portable-gui-smoke");
    let fake_bin = test_root.join("bin");
    let workspace = test_root.join("workspace");
    let output_root = test_root.join("out");
    let extract_dir = test_root.join("extract");
    fs::create_dir_all(&workspace).expect("create fake workspace");
    fs::write(workspace.join("Cargo.toml"), "[workspace]\n").expect("write fake Cargo.toml");
    write_fake_windows_ai_manifest_for_workspace(&workspace);
    fs::create_dir_all(&output_root).expect("create output root");
    fs::create_dir_all(&extract_dir).expect("create extract root");
    write_fake_tooling_scripts(&fake_bin);
    write_fake_release_forbidden_tool_exes(&fake_bin);
    let cargo_record_path = test_root.join("cargo-env.txt");
    let gui_record_path = test_root.join("packaged-gui.txt");
    let forbidden_tool_record = test_root.join("forbidden-tools.txt");

    std::env::set_var("PATH", prepend_path(&fake_bin, environment.original_path()));
    std::env::set_var("EASYDICT_RUNTIME_PROFILE", "hybrid");
    std::env::set_var("RUNTIME_PROFILE", "hybrid");
    std::env::set_var("EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS", "0");
    std::env::set_var("EASYDICT_FAKE_CARGO_RECORD", &cargo_record_path);

    let outcome = pack_rs_portable(&PackRustPortableOptions {
        rust_workspace: workspace.clone(),
        platform: "x64".to_string(),
        configuration: "Release".to_string(),
        output_root: output_root.clone(),
        package_version: Some("v0.0.0-gui-smoke".to_string()),
        create_zip: true,
    })
    .expect("pack-rs-portable should create a validated ZIP with executable Rust entrypoint");

    let zip_path = outcome.zip_path.as_ref().expect("created ZIP path");
    extract_zip(zip_path, &extract_dir);
    let packaged_gui = extract_dir.join("Easydict.Rust.exe");
    assert!(
        packaged_gui.is_file(),
        "extracted rs portable ZIP should contain Easydict.Rust.exe"
    );
    assert!(
        !extract_dir.join("Easydict.WinUI.exe").exists(),
        "first rs portable ZIP should not contain the legacy WinUI entrypoint"
    );

    let output = std::process::Command::new(&packaged_gui)
        .env("PATH", prepend_path(&fake_bin, environment.original_path()))
        .env("EASYDICT_RUNTIME_PROFILE", "rust-only")
        .env("RUNTIME_PROFILE", "rust-only")
        .env("EASYDICT_PACKAGED_GUI_RECORD", &gui_record_path)
        .env(
            "EASYDICT_RELEASE_FORBIDDEN_TOOL_RECORD",
            &forbidden_tool_record,
        )
        .output()
        .expect("run extracted packaged Easydict.Rust.exe");

    assert!(
        output.status.success(),
        "extracted packaged GUI entrypoint smoke should succeed\nstdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    assert!(
        !forbidden_tool_record.exists(),
        "extracted packaged GUI entrypoint smoke must not invoke dotnet.exe, powershell.exe, or pwsh.exe"
    );
    let gui_record = read_text(&gui_record_path);
    assert_contains(
        &gui_record,
        "GUI=Easydict.Rust",
        "extracted packaged GUI smoke should execute the public entrypoint from the ZIP",
    );
    assert_contains(
        &gui_record,
        "EASYDICT_RUNTIME_PROFILE=rust-only",
        "extracted packaged GUI smoke should run under the first-release rust-only profile",
    );
    assert_contains(
        &gui_record,
        "RUNTIME_PROFILE=rust-only",
        "extracted packaged GUI smoke should run under the generic rust-only profile",
    );

    let _ = fs::remove_dir_all(test_root);
}

#[cfg(windows)]
#[test]
fn build_rust_helpers_powershell_shim_delegates_and_forces_runtime_profile() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture([
        "PATH",
        "EASYDICT_RUNTIME_PROFILE",
        "RUNTIME_PROFILE",
        "EASYDICT_FAKE_CARGO_RECORD",
    ]);
    let root = repo_root();
    let test_root = tempfile_dir("build-rust-helpers-shim-runtime-profile");
    let fake_bin = test_root.join("bin");
    let output_dir = test_root.join("out");
    let wrapper_path = test_root.join("run-build-rust-helpers.ps1");
    let cargo_record_path = test_root.join("cargo-record.txt");
    let post_env_record_path = test_root.join("post-env.txt");
    fs::create_dir_all(&test_root).expect("create test root");
    fs::create_dir_all(&output_dir).expect("create output dir");
    write_fake_package_portable_tool_scripts(&fake_bin);
    write_build_rust_helpers_wrapper(
        &wrapper_path,
        &root.join("dotnet/scripts/Build-RustHelpers.ps1"),
        &output_dir,
        &post_env_record_path,
    );

    std::env::set_var("PATH", prepend_path(&fake_bin, environment.original_path()));
    std::env::set_var("EASYDICT_RUNTIME_PROFILE", "outer-parent");
    std::env::set_var("RUNTIME_PROFILE", "outer-parent");
    std::env::set_var("EASYDICT_FAKE_CARGO_RECORD", &cargo_record_path);

    let output = powershell_script_command(&wrapper_path)
        .output()
        .expect("run Build-RustHelpers wrapper");

    assert!(
        output.status.success(),
        "Build-RustHelpers shim wrapper should succeed with fake cargo\nstdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let cargo_record = read_text(&cargo_record_path);
    assert_contains(
        &cargo_record,
        "EASYDICT_RUNTIME_PROFILE=rust-only",
        "Build-RustHelpers shim should force Easydict runtime profile while cargo runs",
    );
    assert_contains(
        &cargo_record,
        "RUNTIME_PROFILE=rust-only",
        "Build-RustHelpers shim should force generic runtime profile while cargo runs",
    );
    assert_contains(
        &cargo_record,
        "ARGS=run --manifest-path",
        "Build-RustHelpers shim should invoke cargo run through the Rust packager",
    );
    let output_dir_text = output_dir.display().to_string();
    for expected in [
        "-p",
        "easydict_packager",
        "build-rust-helpers",
        "--workspace",
        "--platform arm64",
        "--configuration Debug",
        "--output-dir",
        output_dir_text.as_str(),
    ] {
        assert_contains(
            &cargo_record,
            expected,
            "Build-RustHelpers shim should pass the expected build-rust-helpers arguments",
        );
    }
    for forbidden in [
        "retained-dotnet-workers",
        "--features",
        "--all-features",
        "CompatHost",
    ] {
        assert_not_contains(
            &cargo_record,
            forbidden,
            "Build-RustHelpers shim must not enable retained runtime features",
        );
    }

    let post_env_record = read_text(&post_env_record_path);
    assert_contains(
        &post_env_record,
        "POST_EASYDICT_RUNTIME_PROFILE=hybrid",
        "Build-RustHelpers shim should restore the caller's Easydict runtime profile",
    );
    assert_contains(
        &post_env_record,
        "POST_RUNTIME_PROFILE=hybrid",
        "Build-RustHelpers shim should restore the caller's generic runtime profile",
    );

    let _ = fs::remove_dir_all(test_root);
}

#[cfg(windows)]
#[test]
fn package_portable_powershell_shim_forces_and_restores_runtime_profile() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture([
        "PATH",
        "EASYDICT_RUNTIME_PROFILE",
        "RUNTIME_PROFILE",
        "EASYDICT_FAKE_CARGO_RECORD",
    ]);
    let root = repo_root();
    let test_root = tempfile_dir("package-portable-shim-runtime-profile");
    let fake_bin = test_root.join("bin");
    let output_root = test_root.join("out");
    let wrapper_path = test_root.join("run-package-portable.ps1");
    let cargo_record_path = test_root.join("cargo-record.txt");
    let post_env_record_path = test_root.join("post-env.txt");
    fs::create_dir_all(&test_root).expect("create test root");
    fs::create_dir_all(&output_root).expect("create output root");
    write_fake_package_portable_tool_scripts(&fake_bin);
    write_package_portable_wrapper(
        &wrapper_path,
        &root.join("rs/scripts/Package-Portable.ps1"),
        &output_root,
        &post_env_record_path,
    );

    std::env::set_var("PATH", prepend_path(&fake_bin, environment.original_path()));
    std::env::set_var("EASYDICT_RUNTIME_PROFILE", "outer-parent");
    std::env::set_var("RUNTIME_PROFILE", "outer-parent");
    std::env::set_var("EASYDICT_FAKE_CARGO_RECORD", &cargo_record_path);

    let output = powershell_script_command(&wrapper_path)
        .output()
        .expect("run Package-Portable wrapper");

    assert!(
        output.status.success(),
        "Package-Portable shim wrapper should succeed with fake cargo\nstdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let cargo_record = read_text(&cargo_record_path);
    assert_contains(
        &cargo_record,
        "EASYDICT_RUNTIME_PROFILE=rust-only",
        "Package-Portable shim should force Easydict runtime profile while cargo runs",
    );
    assert_contains(
        &cargo_record,
        "RUNTIME_PROFILE=rust-only",
        "Package-Portable shim should force generic runtime profile while cargo runs",
    );
    assert_contains(
        &cargo_record,
        "ARGS=run --manifest-path",
        "Package-Portable shim should invoke cargo run through the Rust packager",
    );
    for expected in [
        "-p",
        "easydict_packager",
        "pack-rs-portable",
        "--workspace",
        "--platform x64",
        "--configuration Debug",
        "--output-root",
        "--package-version v0.0.0-shim",
        "--no-zip",
    ] {
        assert_contains(
            &cargo_record,
            expected,
            "Package-Portable shim should pass the expected pack-rs-portable arguments",
        );
    }
    for forbidden in [
        "retained-dotnet-workers",
        "--features",
        "--all-features",
        "CompatHost",
    ] {
        assert_not_contains(
            &cargo_record,
            forbidden,
            "Package-Portable shim must not enable retained runtime features",
        );
    }

    let post_env_record = read_text(&post_env_record_path);
    assert_contains(
        &post_env_record,
        "POST_EASYDICT_RUNTIME_PROFILE=hybrid",
        "Package-Portable shim should restore the caller's Easydict runtime profile",
    );
    assert_contains(
        &post_env_record,
        "POST_RUNTIME_PROFILE=hybrid",
        "Package-Portable shim should restore the caller's generic runtime profile",
    );

    let _ = fs::remove_dir_all(test_root);
}

#[test]
fn translate_long_doc_script_is_rust_only_and_rejects_dotnet_legacy_mode() {
    let root = repo_root();
    let script = read_text(&root.join("scripts/translate-long-doc.ps1"));

    assert_contains(
        &script,
        "Invoke-RustHelper",
        "LongDoc helper script should keep the packaged Rust helper path",
    );
    assert_contains(
        &script,
        "Invoke-RustCargo",
        "LongDoc helper script should keep Rust cargo development mode",
    );
    assert_contains(
        &script,
        "[string]$ResultJsonPath",
        "LongDoc helper script should accept the Rust result JSON sidecar path",
    );
    assert_contains(
        &script,
        "[switch]$RetryFailed",
        "LongDoc helper script should accept the Rust retry-failed switch",
    );
    assert_contains(
        &script,
        "\"--result-json\", $ResultJsonPath",
        "LongDoc helper script should pass the result JSON sidecar path to Rust",
    );
    assert_contains(
        &script,
        "\"--retry-failed\"",
        "LongDoc helper script should pass retry-failed to Rust",
    );
    assert_contains(
        &script,
        "ResultJsonPath is required when -RetryFailed is used.",
        "LongDoc helper script should keep retry-failed tied to a Rust sidecar",
    );
    assert_contains(
        &script,
        "-UseDotnetLegacy has been retired",
        "legacy dotnet mode should fail locally instead of launching WinUI",
    );
    assert_contains(
        &script,
        "[Parameter(DontShow = $true)]",
        "retired legacy dotnet mode should stay hidden from normal PowerShell help",
    );
    assert_contains(
        &script,
        "Invoke-WithRustOnlyRuntimeProfile",
        "LongDoc helper script should wrap child helper/cargo invocations in a Rust-only profile",
    );
    assert_contains(
        &script,
        "$env:EASYDICT_RUNTIME_PROFILE = \"rust-only\"",
        "LongDoc helper script should force the Easydict runtime profile for child processes",
    );
    assert_contains(
        &script,
        "$env:RUNTIME_PROFILE = \"rust-only\"",
        "LongDoc helper script should force the generic runtime profile for child processes",
    );
    assert_contains(
        &script,
        "Remove-Item Env:EASYDICT_RUNTIME_PROFILE",
        "LongDoc helper script should restore an absent Easydict runtime profile",
    );
    assert_contains(
        &script,
        "Remove-Item Env:RUNTIME_PROFILE",
        "LongDoc helper script should restore an absent generic runtime profile",
    );
    assert_contains(
        &script,
        "Test-RetainedDotnetRuntimeOrWorkerPath",
        "LongDoc helper script should classify explicit retained runtime helper paths locally",
    );
    assert_contains(
        &script,
        "\"dotnet.exe\"",
        "LongDoc helper script should reject explicit RustHelperPath values pointing at dotnet.exe",
    );
    assert_contains(
        &script,
        "easydict.workers.",
        "LongDoc helper script should reject explicit RustHelperPath values pointing at retained workers",
    );
    assert_contains(
        &script,
        "easydict.compathost",
        "LongDoc helper script should reject explicit RustHelperPath values pointing at CompatHost",
    );

    for retired_marker in [
        "Invoke-DotnetLegacy",
        "New-LegacyLongDocArguments",
        "& dotnet",
        "dotnet run",
        "Start-Process dotnet",
        "dotnetArguments",
        "dotnet\\src\\Easydict.WinUI",
        "dotnet/src/Easydict.WinUI",
        "Easydict.WinUI.csproj",
        "Easydict.Workers.LongDocument",
        "Easydict.CompatHost",
        "--translate-long-doc",
    ] {
        assert_not_contains(
            &script,
            retired_marker,
            &format!("scripts/translate-long-doc.ps1 must not launch legacy .NET LongDoc mode"),
        );
    }
}

#[cfg(windows)]
#[test]
fn translate_long_doc_script_rejects_retained_runtime_rust_helper_paths_before_spawn() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture([
        "EASYDICT_LONG_DOC_FORBIDDEN_TOOL_RECORD",
        "EASYDICT_RUNTIME_PROFILE",
        "RUNTIME_PROFILE",
    ]);
    let root = repo_root();
    let test_root = tempfile_dir("translate-long-doc-retained-helper-path");
    let app_dir = test_root.join("app");
    let forbidden_tool_record = test_root.join("forbidden-retained-helper.txt");
    let retained_helper_paths = vec![
        app_dir.join("dotnet").join("dotnet.cmd"),
        app_dir
            .join("workers")
            .join("longdoc")
            .join("Easydict.Workers.LongDoc.cmd"),
        app_dir.join("Easydict.CompatHost.cmd"),
    ];

    fs::create_dir_all(&test_root).expect("create test root");
    for retained_helper_path in &retained_helper_paths {
        write_fake_retained_long_doc_entrypoint(retained_helper_path);
    }
    std::env::set_var(
        "EASYDICT_LONG_DOC_FORBIDDEN_TOOL_RECORD",
        &forbidden_tool_record,
    );
    std::env::set_var("EASYDICT_RUNTIME_PROFILE", "hybrid");
    std::env::set_var("RUNTIME_PROFILE", "hybrid");

    for retained_helper_path in retained_helper_paths {
        let _ = fs::remove_file(&forbidden_tool_record);
        let output = translate_long_doc_script_command(&root)
            .arg("-ListServices")
            .arg("-RustHelperPath")
            .arg(&retained_helper_path)
            .output()
            .expect("run translate-long-doc shim");

        assert!(
            !output.status.success(),
            "retained RustHelperPath should fail before spawning {}\nstdout:\n{}\nstderr:\n{}",
            retained_helper_path.display(),
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        );
        let stderr = String::from_utf8_lossy(&output.stderr);
        assert_contains(
            &stderr,
            "retained .NET runtime or worker entry",
            "retained RustHelperPath rejection should explain the no-runtime boundary",
        );
        assert!(
            !forbidden_tool_record.exists(),
            "retained RustHelperPath must be rejected before invoking {}",
            retained_helper_path.display()
        );
    }

    let _ = fs::remove_dir_all(test_root);
    drop(environment);
}

#[cfg(windows)]
#[test]
fn translate_long_doc_script_rejects_renamed_helper_under_retained_payload_root_before_spawn() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture([
        "EASYDICT_LONG_DOC_FORBIDDEN_TOOL_RECORD",
        "EASYDICT_RUNTIME_PROFILE",
        "RUNTIME_PROFILE",
    ]);
    let root = repo_root();
    let test_root = tempfile_dir("translate-long-doc-renamed-retained-helper-path");
    let forbidden_tool_record = test_root.join("forbidden-renamed-helper.txt");
    let explicit_helper_paths = vec![
        test_root
            .join("dotnet")
            .join("host")
            .join("fxr")
            .join("8.0.0")
            .join("easydict_long_doc.exe"),
        test_root
            .join("workers")
            .join("longdoc")
            .join("easydict_long_doc.exe"),
        test_root
            .join("Easydict.Workers.LongDoc")
            .join("easydict_long_doc.exe"),
    ];
    let app_dirs = vec![
        test_root.join("app-from-dotnet").join("dotnet"),
        test_root.join("app-from-workers").join("workers"),
    ];

    fs::create_dir_all(&test_root).expect("create test root");
    for helper_path in &explicit_helper_paths {
        write_fake_retained_long_doc_entrypoint(helper_path);
    }
    for app_dir in &app_dirs {
        write_fake_retained_long_doc_entrypoint(&app_dir.join("easydict_long_doc.exe"));
    }
    std::env::set_var(
        "EASYDICT_LONG_DOC_FORBIDDEN_TOOL_RECORD",
        &forbidden_tool_record,
    );
    std::env::set_var("EASYDICT_RUNTIME_PROFILE", "hybrid");
    std::env::set_var("RUNTIME_PROFILE", "hybrid");

    for helper_path in explicit_helper_paths {
        let _ = fs::remove_file(&forbidden_tool_record);
        let output = translate_long_doc_script_command(&root)
            .arg("-ListServices")
            .arg("-RustHelperPath")
            .arg(&helper_path)
            .output()
            .expect("run translate-long-doc shim");
        let output_text = powershell_output_text(&output);

        assert!(
            !output.status.success(),
            "renamed retained RustHelperPath should fail before spawning {}\n{output_text}",
            helper_path.display()
        );
        assert_contains(
            &output_text,
            "retained .NET runtime or worker entry",
            "renamed retained RustHelperPath rejection should explain the no-runtime boundary",
        );
        assert!(
            !forbidden_tool_record.exists(),
            "renamed retained RustHelperPath must be rejected before invoking {}",
            helper_path.display()
        );
    }

    for app_dir in app_dirs {
        let _ = fs::remove_file(&forbidden_tool_record);
        let output = translate_long_doc_script_command(&root)
            .arg("-ListServices")
            .arg("-AppDir")
            .arg(&app_dir)
            .output()
            .expect("run translate-long-doc shim");
        let output_text = powershell_output_text(&output);

        assert!(
            !output.status.success(),
            "AppDir helper under retained payload root should fail before spawning {}\n{output_text}",
            app_dir.display()
        );
        assert_contains(
            &output_text,
            "retained .NET runtime or worker entry",
            "AppDir retained helper rejection should explain the no-runtime boundary",
        );
        assert!(
            !forbidden_tool_record.exists(),
            "AppDir retained helper must be rejected before invoking {}",
            app_dir.display()
        );
    }

    let _ = fs::remove_dir_all(test_root);
    drop(environment);
}

#[cfg(windows)]
#[test]
fn translate_long_doc_script_invokes_rust_helper_with_retry_sidecar_arguments() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture([
        "PATH",
        "EASYDICT_LONG_DOC_HELPER_RECORD",
        "EASYDICT_LONG_DOC_FORBIDDEN_TOOL_RECORD",
        "EASYDICT_RUNTIME_PROFILE",
        "RUNTIME_PROFILE",
    ]);
    let root = repo_root();
    let test_root = tempfile_dir("translate-long-doc-rust-helper-smoke");
    let fake_bin = test_root.join("bin");
    let app_dir = test_root.join("app");
    let helper_path = test_root.join("fake-easydict-long-doc.cmd");
    let record_path = test_root.join("helper-args.txt");
    let forbidden_tool_record = test_root.join("forbidden-tools.txt");
    let input_path = test_root.join("input.pdf");
    let output_path = test_root.join("translated.pdf");
    let result_json_path = test_root.join("translated-result.json");

    fs::create_dir_all(&test_root).expect("create test root");
    write_fake_long_doc_helper(&helper_path);
    write_fake_forbidden_tool_scripts(&fake_bin);
    write_stale_dotnet_payload_markers(&app_dir);
    fs::write(&input_path, b"%PDF-1.7\n").expect("write input");

    std::env::set_var("PATH", prepend_path(&fake_bin, environment.original_path()));
    std::env::set_var("EASYDICT_LONG_DOC_HELPER_RECORD", &record_path);
    std::env::set_var(
        "EASYDICT_LONG_DOC_FORBIDDEN_TOOL_RECORD",
        &forbidden_tool_record,
    );
    std::env::set_var("EASYDICT_RUNTIME_PROFILE", "hybrid");
    std::env::set_var("RUNTIME_PROFILE", "hybrid");

    let output = translate_long_doc_script_command(&root)
        .arg("-InputFile")
        .arg(&input_path)
        .args(["-TargetLanguage", "zh-Hans", "-SourceLanguage", "en"])
        .arg("-OutputFile")
        .arg(&output_path)
        .arg("-ResultJsonPath")
        .arg(&result_json_path)
        .arg("-RetryFailed")
        .args(["-ServiceId", "google", "-OutputMode", "both"])
        .args(["-PdfExportMode", "Overlay", "-PageRange", "2-3"])
        .arg("-AppDir")
        .arg(&app_dir)
        .arg("-RustHelperPath")
        .arg(&helper_path)
        .output()
        .expect("run translate-long-doc shim");

    assert!(
        output.status.success(),
        "translate-long-doc shim should invoke fake Rust helper successfully\nstdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    assert!(
        !forbidden_tool_record.exists(),
        "Rust-helper shim path must not launch cargo/dotnet/pwsh from PATH"
    );
    let record = read_text(&record_path);
    let expected_arguments = vec![
        "--input".to_string(),
        input_path.display().to_string(),
        "--target-language".to_string(),
        "zh-Hans".to_string(),
        "--from".to_string(),
        "en".to_string(),
        "--output".to_string(),
        output_path.display().to_string(),
        "--result-json".to_string(),
        result_json_path.display().to_string(),
        "--retry-failed".to_string(),
        "--service".to_string(),
        "google".to_string(),
        "--output-mode".to_string(),
        "both".to_string(),
        "--pdf-export-mode".to_string(),
        "Overlay".to_string(),
        "--page-range".to_string(),
        "2-3".to_string(),
        "--app-dir".to_string(),
        app_dir.display().to_string(),
    ];
    for expected in expected_arguments {
        assert_contains(
            &record,
            &expected,
            "translate-long-doc shim should pass the expected Rust helper argument",
        );
    }
    assert_contains(
        &record,
        "EASYDICT_RUNTIME_PROFILE=rust-only",
        "translate-long-doc Rust helper path should force the Easydict runtime profile",
    );
    assert_contains(
        &record,
        "RUNTIME_PROFILE=rust-only",
        "translate-long-doc Rust helper path should force the generic runtime profile",
    );

    let _ = fs::remove_dir_all(test_root);
}

#[cfg(windows)]
#[test]
fn translate_long_doc_script_forwards_layout_vision_env_file_and_max_concurrency() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture([
        "PATH",
        "EASYDICT_LONG_DOC_HELPER_RECORD",
        "EASYDICT_LONG_DOC_FORBIDDEN_TOOL_RECORD",
        "EASYDICT_RUNTIME_PROFILE",
        "RUNTIME_PROFILE",
    ]);
    let root = repo_root();
    let test_root = tempfile_dir("translate-long-doc-full-args");
    let fake_bin = test_root.join("bin");
    let helper_path = test_root.join("fake-easydict-long-doc.cmd");
    let record_path = test_root.join("helper-args.txt");
    let forbidden_tool_record = test_root.join("forbidden-tools.txt");
    let input_path = test_root.join("input.pdf");
    let output_path = test_root.join("translated.pdf");
    let env_file_path = test_root.join("longdoc.env");

    fs::create_dir_all(&test_root).expect("create test root");
    write_fake_long_doc_helper(&helper_path);
    write_fake_forbidden_tool_scripts(&fake_bin);
    fs::write(&input_path, b"%PDF-1.7\n").expect("write input");
    fs::write(&env_file_path, "EASYDICT_TEST=1\n").expect("write env file");

    std::env::set_var("PATH", prepend_path(&fake_bin, environment.original_path()));
    std::env::set_var("EASYDICT_LONG_DOC_HELPER_RECORD", &record_path);
    std::env::set_var(
        "EASYDICT_LONG_DOC_FORBIDDEN_TOOL_RECORD",
        &forbidden_tool_record,
    );
    std::env::set_var("EASYDICT_RUNTIME_PROFILE", "hybrid");
    std::env::set_var("RUNTIME_PROFILE", "hybrid");

    let output = translate_long_doc_script_command(&root)
        .arg("-InputFile")
        .arg(&input_path)
        .args(["-TargetLanguage", "zh-Hans", "-SourceLanguage", "en"])
        .arg("-EnvFile")
        .arg(&env_file_path)
        .arg("-OutputFile")
        .arg(&output_path)
        .args(["-ServiceId", "google", "-OutputMode", "monolingual"])
        .args(["-Layout", "VisionLLM", "-PdfExportMode", "Overlay"])
        .args(["-Page", "2", "-MaxConcurrency", "4"])
        .args([
            "-VisionEndpoint",
            "http://localhost:11434/v1/chat/completions",
            "-VisionApiKey",
            "vision-test-key",
            "-VisionModel",
            "layout-model",
        ])
        .arg("-RustHelperPath")
        .arg(&helper_path)
        .output()
        .expect("run translate-long-doc shim");

    assert!(
        output.status.success(),
        "translate-long-doc shim should invoke fake Rust helper successfully\nstdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    assert!(
        !forbidden_tool_record.exists(),
        "full-argument LongDoc shim path must not launch cargo/dotnet tools from PATH"
    );
    let record = read_text(&record_path);
    for expected in [
        "--input".to_string(),
        input_path.display().to_string(),
        "--target-language".to_string(),
        "zh-Hans".to_string(),
        "--from".to_string(),
        "en".to_string(),
        "--env-file".to_string(),
        env_file_path.display().to_string(),
        "--output".to_string(),
        output_path.display().to_string(),
        "--service".to_string(),
        "google".to_string(),
        "--output-mode".to_string(),
        "monolingual".to_string(),
        "--layout".to_string(),
        "VisionLLM".to_string(),
        "--pdf-export-mode".to_string(),
        "Overlay".to_string(),
        "--page".to_string(),
        "2".to_string(),
        "--max-concurrency".to_string(),
        "4".to_string(),
        "--vision-endpoint".to_string(),
        "http://localhost:11434/v1/chat/completions".to_string(),
        "--vision-api-key".to_string(),
        "vision-test-key".to_string(),
        "--vision-model".to_string(),
        "layout-model".to_string(),
    ] {
        assert_contains(
            &record,
            &expected,
            "translate-long-doc shim should pass the full Rust LongDoc argument surface",
        );
    }
    assert_contains(
        &record,
        "EASYDICT_RUNTIME_PROFILE=rust-only",
        "full-argument shim should force the Easydict runtime profile",
    );
    assert_contains(
        &record,
        "RUNTIME_PROFILE=rust-only",
        "full-argument shim should force the generic runtime profile",
    );

    let _ = fs::remove_dir_all(test_root);
}

#[cfg(windows)]
#[test]
fn translate_long_doc_script_retry_failed_only_requires_result_json_path() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let _environment = EnvironmentSnapshot::capture([
        "PATH",
        "EASYDICT_LONG_DOC_HELPER_RECORD",
        "EASYDICT_RUNTIME_PROFILE",
        "RUNTIME_PROFILE",
    ]);
    let root = repo_root();
    let test_root = tempfile_dir("translate-long-doc-retry-sidecar-only");
    let helper_path = test_root.join("fake-easydict-long-doc.cmd");
    let record_path = test_root.join("helper-args.txt");
    let result_json_path = test_root.join("translated-result.json");

    fs::create_dir_all(&test_root).expect("create test root");
    write_fake_long_doc_helper(&helper_path);

    std::env::set_var("EASYDICT_LONG_DOC_HELPER_RECORD", &record_path);
    std::env::set_var("EASYDICT_RUNTIME_PROFILE", "hybrid");
    std::env::set_var("RUNTIME_PROFILE", "hybrid");

    let output = translate_long_doc_script_command(&root)
        .arg("-ResultJsonPath")
        .arg(&result_json_path)
        .arg("-RetryFailed")
        .arg("-RustHelperPath")
        .arg(&helper_path)
        .output()
        .expect("run translate-long-doc shim");

    assert!(
        output.status.success(),
        "retry-only translate-long-doc shim should invoke fake Rust helper successfully\nstdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let record = read_text(&record_path);
    assert_contains(
        &record,
        "--result-json",
        "retry-only shim should pass the result sidecar path",
    );
    assert_contains(
        &record,
        &result_json_path.display().to_string(),
        "retry-only shim should pass the selected result sidecar",
    );
    assert_contains(
        &record,
        "--retry-failed",
        "retry-only shim should pass retry-failed to Rust",
    );
    assert_not_contains(
        &record,
        "--input",
        "retry-only shim should not require or pass an input file",
    );
    assert_not_contains(
        &record,
        "--target-language",
        "retry-only shim should not require or pass a target language",
    );
    assert_contains(
        &record,
        "EASYDICT_RUNTIME_PROFILE=rust-only",
        "retry-only shim should force the Easydict runtime profile",
    );
    assert_contains(
        &record,
        "RUNTIME_PROFILE=rust-only",
        "retry-only shim should force the generic runtime profile",
    );
    let _ = fs::remove_dir_all(test_root);
}

#[cfg(windows)]
#[test]
fn translate_long_doc_script_resolves_app_dir_helper_without_cargo_or_dotnet_tools() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture([
        "PATH",
        "EASYDICT_LONG_DOC_HELPER_RECORD",
        "EASYDICT_LONG_DOC_FORBIDDEN_TOOL_RECORD",
        "EASYDICT_RUNTIME_PROFILE",
        "RUNTIME_PROFILE",
    ]);
    let root = repo_root();
    let test_root = tempfile_dir("translate-long-doc-app-dir-helper");
    let fake_bin = test_root.join("bin");
    let app_dir = test_root.join("app");
    let helper_path = app_dir.join("easydict_long_doc.exe");
    let record_path = test_root.join("helper-args.txt");
    let forbidden_tool_record = test_root.join("forbidden-tools.txt");
    let input_path = test_root.join("input.md");
    let output_path = test_root.join("translated.md");

    fs::create_dir_all(&test_root).expect("create test root");
    write_fake_forbidden_tool_scripts(&fake_bin);
    write_stale_dotnet_payload_markers(&app_dir);
    write_fake_long_doc_helper_exe(&helper_path);
    fs::write(&input_path, "# hello\n").expect("write input");

    std::env::set_var("PATH", prepend_path(&fake_bin, environment.original_path()));
    std::env::set_var("EASYDICT_LONG_DOC_HELPER_RECORD", &record_path);
    std::env::set_var(
        "EASYDICT_LONG_DOC_FORBIDDEN_TOOL_RECORD",
        &forbidden_tool_record,
    );
    std::env::set_var("EASYDICT_RUNTIME_PROFILE", "hybrid");
    std::env::set_var("RUNTIME_PROFILE", "hybrid");

    let output = translate_long_doc_script_command(&root)
        .arg("-InputFile")
        .arg(&input_path)
        .args(["-TargetLanguage", "zh-Hans", "-SourceLanguage", "en"])
        .arg("-OutputFile")
        .arg(&output_path)
        .args(["-ServiceId", "google", "-OutputMode", "bilingual"])
        .arg("-AppDir")
        .arg(&app_dir)
        .output()
        .expect("run translate-long-doc shim");

    assert!(
        output.status.success(),
        "translate-long-doc shim should invoke app-dir fake Rust helper successfully\nstdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    assert!(
        !forbidden_tool_record.exists(),
        "app-dir LongDoc shim must not launch cargo/dotnet tools from PATH"
    );
    let record = read_text(&record_path);
    for expected in [
        "HELPER=".to_string(),
        "easydict_long_doc.exe".to_string(),
        "EASYDICT_RUNTIME_PROFILE=rust-only".to_string(),
        "RUNTIME_PROFILE=rust-only".to_string(),
        "--input".to_string(),
        input_path.display().to_string(),
        "--target-language".to_string(),
        "zh-Hans".to_string(),
        "--output".to_string(),
        output_path.display().to_string(),
        "--service".to_string(),
        "google".to_string(),
        "--output-mode".to_string(),
        "bilingual".to_string(),
        "--app-dir".to_string(),
        app_dir.display().to_string(),
    ] {
        assert_contains(
            &record,
            &expected,
            "translate-long-doc shim should pass through app-dir helper arguments",
        );
    }

    let _ = fs::remove_dir_all(test_root);
}

#[cfg(windows)]
#[test]
fn translate_long_doc_script_use_cargo_forwards_retry_sidecar_without_dotnet_tools() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture([
        "PATH",
        "EASYDICT_LONG_DOC_HELPER_RECORD",
        "EASYDICT_LONG_DOC_FORBIDDEN_TOOL_RECORD",
        "EASYDICT_RUNTIME_PROFILE",
        "RUNTIME_PROFILE",
    ]);
    let root = repo_root();
    let test_root = tempfile_dir("translate-long-doc-use-cargo-retry");
    let fake_bin = test_root.join("bin");
    let app_dir = test_root.join("app");
    let retained_helper_path = app_dir.join("dotnet").join("dotnet.cmd");
    let cargo_record_path = test_root.join("cargo-args.txt");
    let forbidden_tool_record = test_root.join("forbidden-tools.txt");
    let input_path = test_root.join("input.pdf");
    let output_path = test_root.join("translated.pdf");
    let result_json_path = test_root.join("translated-result.json");

    fs::create_dir_all(&test_root).expect("create test root");
    write_fake_long_doc_cargo_script(&fake_bin);
    write_fake_dotnet_forbidden_tool_script(&fake_bin);
    write_stale_dotnet_payload_markers(&app_dir);
    write_fake_retained_long_doc_entrypoint(&retained_helper_path);
    fs::write(&input_path, b"%PDF-1.7\n").expect("write input");

    std::env::set_var("PATH", prepend_path(&fake_bin, environment.original_path()));
    std::env::set_var("EASYDICT_LONG_DOC_HELPER_RECORD", &cargo_record_path);
    std::env::set_var(
        "EASYDICT_LONG_DOC_FORBIDDEN_TOOL_RECORD",
        &forbidden_tool_record,
    );
    std::env::set_var("EASYDICT_RUNTIME_PROFILE", "hybrid");
    std::env::set_var("RUNTIME_PROFILE", "hybrid");

    let output = translate_long_doc_script_command(&root)
        .arg("-InputFile")
        .arg(&input_path)
        .args(["-TargetLanguage", "zh-Hans", "-SourceLanguage", "en"])
        .arg("-OutputFile")
        .arg(&output_path)
        .arg("-ResultJsonPath")
        .arg(&result_json_path)
        .arg("-RetryFailed")
        .args(["-ServiceId", "google", "-OutputMode", "both"])
        .args(["-PdfExportMode", "Overlay", "-PageRange", "2-3"])
        .arg("-AppDir")
        .arg(&app_dir)
        .arg("-RustHelperPath")
        .arg(&retained_helper_path)
        .arg("-UseCargo")
        .output()
        .expect("run translate-long-doc shim");

    assert!(
        output.status.success(),
        "translate-long-doc -UseCargo shim should invoke fake cargo successfully\nstdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    assert!(
        !forbidden_tool_record.exists(),
        "-UseCargo LongDoc shim must not launch dotnet or retained runtime tools"
    );
    let record = read_text(&cargo_record_path);
    let expected_arguments = vec![
        "ARGS=run -p easydict_app --bin easydict_long_doc --".to_string(),
        "--input".to_string(),
        input_path.display().to_string(),
        "--target-language".to_string(),
        "zh-Hans".to_string(),
        "--from".to_string(),
        "en".to_string(),
        "--output".to_string(),
        output_path.display().to_string(),
        "--result-json".to_string(),
        result_json_path.display().to_string(),
        "--retry-failed".to_string(),
        "--service".to_string(),
        "google".to_string(),
        "--output-mode".to_string(),
        "both".to_string(),
        "--pdf-export-mode".to_string(),
        "Overlay".to_string(),
        "--page-range".to_string(),
        "2-3".to_string(),
        "--app-dir".to_string(),
        app_dir.display().to_string(),
    ];
    for expected in expected_arguments {
        assert_contains(
            &record,
            &expected,
            "translate-long-doc -UseCargo should pass the expected Rust CLI argument",
        );
    }
    assert_contains(
        &record,
        "EASYDICT_RUNTIME_PROFILE=rust-only",
        "translate-long-doc -UseCargo should force the Easydict runtime profile while cargo runs",
    );
    assert_contains(
        &record,
        "RUNTIME_PROFILE=rust-only",
        "translate-long-doc -UseCargo should force the generic runtime profile while cargo runs",
    );
    for forbidden in ["dotnet", "Easydict.Workers", "CompatHost"] {
        assert_not_contains(
            &record,
            forbidden,
            "-UseCargo argument forwarding must stay on the Rust helper path",
        );
    }

    let _ = fs::remove_dir_all(test_root);
}

#[cfg(windows)]
#[test]
fn translate_long_doc_script_rejects_retry_failed_without_sidecar_before_helper() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture(["EASYDICT_LONG_DOC_HELPER_RECORD"]);
    let root = repo_root();
    let test_root = tempfile_dir("translate-long-doc-retry-without-sidecar");
    let helper_path = test_root.join("fake-easydict-long-doc.cmd");
    let record_path = test_root.join("helper-args.txt");
    let input_path = test_root.join("input.txt");

    fs::create_dir_all(&test_root).expect("create test root");
    write_fake_long_doc_helper(&helper_path);
    fs::write(&input_path, "retry me").expect("write input");
    std::env::set_var("EASYDICT_LONG_DOC_HELPER_RECORD", &record_path);

    let output = translate_long_doc_script_command(&root)
        .arg("-InputFile")
        .arg(&input_path)
        .args(["-TargetLanguage", "zh-Hans"])
        .arg("-RetryFailed")
        .arg("-RustHelperPath")
        .arg(&helper_path)
        .output()
        .expect("run translate-long-doc shim");

    assert!(
        !output.status.success(),
        "missing ResultJsonPath should fail before helper"
    );
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert_contains(
        &stderr,
        "ResultJsonPath is required when -RetryFailed is used.",
        "retry-failed validation should explain the missing sidecar",
    );
    assert!(
        !record_path.exists(),
        "retry-failed validation must fail before invoking the Rust helper"
    );

    let _ = fs::remove_dir_all(test_root);
    drop(environment);
}

#[cfg(windows)]
#[test]
fn translate_long_doc_script_rejects_legacy_dotnet_mode_before_helper() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture(["EASYDICT_LONG_DOC_HELPER_RECORD"]);
    let root = repo_root();
    let test_root = tempfile_dir("translate-long-doc-legacy-retired");
    let helper_path = test_root.join("fake-easydict-long-doc.cmd");
    let record_path = test_root.join("helper-args.txt");

    fs::create_dir_all(&test_root).expect("create test root");
    write_fake_long_doc_helper(&helper_path);
    std::env::set_var("EASYDICT_LONG_DOC_HELPER_RECORD", &record_path);

    let output = translate_long_doc_script_command(&root)
        .arg("-ListServices")
        .arg("-UseDotnetLegacy")
        .arg("-RustHelperPath")
        .arg(&helper_path)
        .output()
        .expect("run translate-long-doc shim");

    assert!(
        !output.status.success(),
        "retired legacy mode should fail before helper"
    );
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert_contains(
        &stderr,
        "-UseDotnetLegacy has been retired",
        "legacy dotnet mode should be rejected locally",
    );
    assert!(
        !record_path.exists(),
        "legacy mode validation must fail before invoking the Rust helper"
    );

    let _ = fs::remove_dir_all(test_root);
    drop(environment);
}

#[test]
fn legacy_dotnet_packaging_paths_reject_rust_only_and_require_hybrid_profile() {
    let root = repo_root();
    let makefile = read_text(&root.join("dotnet/Makefile"));
    let release_workflow = read_text(&root.join(".github/workflows/release-publish.yml"));
    let winui_csproj = read_text(&root.join("dotnet/src/Easydict.WinUI/Easydict.WinUI.csproj"));
    assert_contains(
        &makefile,
        "RUNTIME_PROFILE ?=",
        "Makefile should keep the runtime profile variable explicit for callers",
    );
    assert_not_contains(
        &makefile,
        "RUNTIME_PROFILE ?= hybrid",
        "Makefile must not silently default legacy .NET/MSIX targets to hybrid",
    );
    assert_contains(
        &winui_csproj,
        "<RuntimeProfile Condition=\"'$(RuntimeProfile)' == ''\">RustOnly</RuntimeProfile>",
        "WinUI project should treat an omitted RuntimeProfile as RustOnly",
    );
    assert_not_contains(
        &winui_csproj,
        "<RuntimeProfile Condition=\"'$(RuntimeProfile)' == ''\">Hybrid</RuntimeProfile>",
        "WinUI project must not silently default omitted RuntimeProfile to Hybrid",
    );
    assert_contains(
        &makefile,
        "if [ \"$$runtime_profile\" = \"hybrid\" ]; then",
        "Makefile retained worker/runtime branches should require explicit hybrid",
    );
    assert_contains(
        &makefile,
        "only hybrid is supported for legacy .NET packaging",
        "Makefile publish targets should reject unknown runtime profiles before worker publish",
    );
    assert_contains(
        &makefile,
        "only hybrid is supported for legacy .NET/MSIX packaging",
        "Makefile MSIX targets should reject unknown runtime profiles before runtime extraction",
    );
    assert!(
        !makefile.contains(
            "if [ \"$$runtime_profile\" != \"rust-only\" ] && [ \"$$runtime_profile\" != \"rustonly\" ]"
        ),
        "Makefile should not use negative rust-only checks for retained worker/runtime branches"
    );
    let validate_msix_target = text_between(&makefile, "validate-msix:", "# Encrypt");
    assert_contains(
        validate_msix_target,
        "if [ -n \"$$runtime_profile\" ]; then",
        "Makefile validate-msix should pass runtime profile only when the caller provided one",
    );
    assert_contains(
        validate_msix_target,
        "easydict_msix_validate -- \"$(MSIX)\" --runtime-profile \"$$runtime_profile\"",
        "Makefile validate-msix should pass a normalized explicit profile when provided",
    );
    assert_contains(
        validate_msix_target,
        "easydict_msix_validate -- \"$(MSIX)\";",
        "Makefile validate-msix should omit --runtime-profile when unset so the Rust validator uses its Rust-only default",
    );
    assert_not_contains(
        validate_msix_target,
        "--runtime-profile \"$(RUNTIME_PROFILE)\"",
        "Makefile validate-msix must not pass an empty runtime profile through to the Rust validator",
    );
    for (target_name, start, end, msix_path) in [
        (
            "msix-x64",
            "msix-x64: publish-msix-x64",
            "# Create MSIX package for x86",
            "./msix/Easydict-x64.msix",
        ),
        (
            "msix-x86",
            "msix-x86: publish-msix-x86",
            "# Create MSIX package for ARM64",
            "./msix/Easydict-x86.msix",
        ),
        (
            "msix-arm64",
            "msix-arm64: publish-msix-arm64",
            "# Create MSIX package for current platform",
            "./msix/Easydict-arm64.msix",
        ),
    ] {
        let target = text_between(&makefile, start, end);
        assert_contains(
            target,
            "if [ -n \"$$runtime_profile\" ]; then",
            &format!(
                "Makefile {target_name} should pass runtime profile only when explicitly provided"
            ),
        );
        assert_contains(
            target,
            &format!(
                "easydict_msix_validate -- {msix_path} --runtime-profile \"$$runtime_profile\" --allow-unsigned"
            ),
            &format!("Makefile {target_name} should pass the normalized explicit profile"),
        );
        assert_contains(
            target,
            &format!("easydict_msix_validate -- {msix_path} --allow-unsigned"),
            &format!(
                "Makefile {target_name} should omit --runtime-profile when unset so the Rust validator uses its Rust-only default"
            ),
        );
        assert_not_contains(
            target,
            "--runtime-profile \"$(RUNTIME_PROFILE)\"",
            &format!(
                "Makefile {target_name} must not pass an empty runtime profile through to the Rust validator"
            ),
        );
    }
    assert_contains(
        &release_workflow,
        "verify-bundle-minversion",
        "release workflow should validate final MSIX bundle MinVersion through Rust",
    );
    assert_contains(
        &release_workflow,
        "--runtime-profile \"${{ env.RUNTIME_PROFILE }}\"",
        "release workflow bundle validation should reuse the normalized runtime profile",
    );
    let create_bundle_job =
        text_between(&release_workflow, "  create-bundle:", "  publish-winget:");
    assert_contains(
        create_bundle_job,
        "RUNTIME_PROFILE: ${{ github.event.inputs.runtime_profile || '' }}",
        "create-bundle should require the caller-provided runtime profile used by bundle payload validation",
    );
    assert_not_contains(
        create_bundle_job,
        "github.event.inputs.runtime_profile || 'hybrid'",
        "create-bundle must not silently default retained runtime packaging to hybrid",
    );
    assert_contains(
        create_bundle_job,
        "--runtime-profile \"${{ env.RUNTIME_PROFILE }}\"",
        "create-bundle should pass its job runtime profile into bundle validation",
    );

    for relative_path in [
        ".github/workflows/release-publish.yml",
        ".github/workflows/arm64-msix-smoke.yml",
    ] {
        let text = read_text(&root.join(relative_path));
        assert_contains(
            &text,
            "RETAINED_WORKERS_ENABLED=true",
            &format!("{relative_path} should only enable retained workers after hybrid validation"),
        );
        assert_contains(
            &text,
            "if: env.RETAINED_WORKERS_ENABLED == 'true'",
            &format!("{relative_path} should gate retained worker/runtime steps positively"),
        );
        assert!(
            !text.contains("if: env.RUNTIME_PROFILE != 'rust-only'"),
            "{relative_path} should not use negative rust-only checks for retained worker/runtime steps"
        );
    }

    for relative_path in [
        "dotnet/scripts/publish.ps1",
        "dotnet/scripts/package-and-install.ps1",
        "dotnet/scripts/Package-Msix.ps1",
        "dotnet/scripts/Build-Installer.ps1",
    ] {
        let text = read_text(&root.join(relative_path));
        assert_contains(
            &text,
            "Test-RustOnlyRuntimeProfile",
            &format!("{relative_path} should explicitly detect rust-only profile"),
        );
        assert_contains(
            &text,
            "first rs release is portable-only",
            &format!("{relative_path} should redirect rust-only callers to rs portable"),
        );
        assert_contains(
            &text,
            "Test-HybridRuntimeProfile",
            &format!("{relative_path} should explicitly detect the hybrid profile"),
        );
        assert_contains(
            &text,
            "Only Hybrid is supported",
            &format!("{relative_path} should reject unknown legacy packaging profiles"),
        );
        assert_contains(
            &text,
            "[string]$RuntimeProfile = \"\"",
            &format!("{relative_path} should require an explicit hybrid profile"),
        );
        assert_contains(
            &text,
            "RuntimeProfile must be explicitly set to Hybrid",
            &format!("{relative_path} should fail when RuntimeProfile is omitted"),
        );
        assert!(
            !text.contains("[string]$RuntimeProfile = \"Hybrid\""),
            "{relative_path} should not silently default to a runtime-producing profile"
        );
    }

    let package_msix = read_text(&root.join("dotnet/scripts/Package-Msix.ps1"));
    let prepare_args = text_between(&package_msix, "$prepareArgs = @(", "if ($MsixVersion)");
    assert_contains(
        prepare_args,
        "prepare-package-inputs",
        "Package-Msix.ps1 should prepare package inputs through the Rust MSIX helper",
    );
    assert_contains(
        prepare_args,
        "\"--runtime-profile\",",
        "Package-Msix.ps1 should pass an explicit runtime profile into prepare-package-inputs",
    );
    assert_contains(
        prepare_args,
        "\"hybrid\"",
        "legacy Package-Msix.ps1 should keep prepare-package-inputs on the explicit hybrid path",
    );

    assert_contains(
        &makefile,
        "scripts/Build-Installer.ps1 -Platform x64 -Version $(VERSION) -RuntimeProfile $(RUNTIME_PROFILE)",
        "Makefile installer-x64 should pass the explicit runtime profile into the legacy installer script",
    );
    assert_contains(
        &makefile,
        "scripts/Build-Installer.ps1 -Platform x86 -Version $(VERSION) -RuntimeProfile $(RUNTIME_PROFILE)",
        "Makefile installer-x86 should pass the explicit runtime profile into the legacy installer script",
    );
    assert_contains(
        &makefile,
        "scripts/Build-Installer.ps1 -Platform arm64 -Version $(VERSION) -RuntimeProfile $(RUNTIME_PROFILE)",
        "Makefile installer-arm64 should pass the explicit runtime profile into the legacy installer script",
    );
}

#[test]
fn legacy_publish_create_zip_is_hybrid_named_and_excluded_from_rs_release_contract() {
    let root = repo_root();
    let publish_script = read_text(&root.join("dotnet/scripts/publish.ps1"));
    let release_workflow = read_text(&root.join(".github/workflows/release-publish.yml"));
    let rs_portable_job = text_between(
        &release_workflow,
        "  publish-rs-portable:",
        "  create-bundle:",
    );
    let create_rs_release_job = text_between(
        &release_workflow,
        "  create-rs-portable-release:",
        "  publish-winget:",
    );

    assert_contains(
        &publish_script,
        "Easydict-legacy-hybrid-win-$Platform-$Configuration.zip",
        "legacy dotnet publish -CreateZip output should be visibly separated from the first rs portable ZIP",
    );
    assert_not_contains(
        &publish_script,
        "Easydict-win-$Platform-$Configuration.zip",
        "legacy dotnet publish -CreateZip must not produce the old portable-looking ZIP name",
    );
    assert_not_contains(
        rs_portable_job,
        "Easydict-legacy-hybrid-win-",
        "rs portable build job must not upload legacy/hybrid dotnet ZIPs",
    );
    assert_not_contains(
        create_rs_release_job,
        "Easydict-legacy-hybrid-win-",
        "first rs release upload job must not include legacy/hybrid dotnet ZIPs",
    );
}

#[test]
fn dotnet_runtime_extraction_shim_requires_explicit_hybrid_profile() {
    let root = repo_root();
    let script = read_text(&root.join("dotnet/scripts/Extract-DotnetRuntime.ps1"));
    let manifest = read_text(&root.join("rs/crates/easydict_packager/Cargo.toml"));
    let packager_main = read_text(&root.join("rs/crates/easydict_packager/src/main.rs"));
    let packager_lib = read_text(&root.join("rs/crates/easydict_packager/src/lib.rs"));

    assert_contains(
        &script,
        "[string]$RuntimeProfile = \"\"",
        "runtime extraction shim should not silently default to a runtime-producing profile",
    );
    assert_contains(
        &script,
        "requires -RuntimeProfile Hybrid",
        "runtime extraction shim should fail when the hybrid profile is not explicit",
    );
    assert_contains(
        &script,
        "$validRuntimeProfiles = @(\"Hybrid\", \"hybrid\")",
        "runtime extraction shim should only accept the explicit hybrid spelling",
    );
    assert_contains(
        &script,
        "extract-dotnet-runtime",
        "runtime extraction shim should delegate to the Rust packager",
    );
    assert_contains(
        &script,
        "--runtime-profile",
        "runtime extraction shim must pass the runtime profile to the Rust packager",
    );
    assert_contains(
        &script,
        "$RuntimeProfile",
        "runtime extraction shim should pass the caller-provided profile value",
    );
    assert_contains(
        &script,
        "--features",
        "runtime extraction shim should opt into the hybrid-only downloader feature",
    );
    assert_contains(
        &script,
        "hybrid-dotnet-runtime-packaging",
        "runtime extraction shim should build the packager with the hybrid-only downloader feature",
    );
    assert_contains(
        &manifest,
        "reqwest = { version = \"0.12\", default-features = false, features = [\"blocking\", \"rustls-tls\"], optional = true }",
        "default easydict_packager builds should not compile the .NET runtime downloader HTTP client",
    );
    assert_contains(
        &manifest,
        "hybrid-dotnet-runtime-packaging = [\"dep:reqwest\"]",
        ".NET runtime downloading should live behind an explicit hybrid packaging feature",
    );
    assert_not_contains(
        &packager_main,
        "run_extract_dotnet_runtime_feature_disabled",
        "default packager CLI should not keep a named .NET runtime extraction fallback",
    );
    assert_source_line_is_feature_gated(
        &packager_main,
        "\"extract-dotnet-runtime\" => run_extract_dotnet_runtime",
        "runtime extraction CLI command should exist only in explicit hybrid builds",
    );
    for needle in [
        "pub struct ExtractDotnetRuntimeOptions",
        "pub struct ExtractDotnetRuntimeOutcome",
        "pub enum PackageRuntimeProfile",
        "pub enum ExtractDotnetRuntimeError",
        "pub fn extract_dotnet_runtime_archive",
        "pub fn dotnet_runtime_url",
    ] {
        assert_source_line_is_feature_gated(
            &packager_lib,
            needle,
            "default packager library API must not expose .NET runtime extraction symbols",
        );
    }
}

#[cfg(windows)]
#[test]
fn legacy_packaging_scripts_reject_non_hybrid_profiles_before_invoking_external_tools() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment =
        EnvironmentSnapshot::capture(["PATH", "EASYDICT_LEGACY_PACKAGING_FORBIDDEN_TOOL_RECORD"]);
    let root = repo_root();
    let test_root = tempfile_dir("legacy-packaging-profile-guard");
    let fake_bin = test_root.join("bin");
    let publish_dir = test_root.join("publish");
    let manifest_path = test_root.join("Package.appxmanifest");
    let output_msix_path = test_root.join("out").join("Easydict.msix");
    let runtime_output_dir = test_root.join("dotnet-runtime");
    let record_path = test_root.join("forbidden-tools.txt");

    fs::create_dir_all(&publish_dir).expect("create fake publish dir");
    fs::write(&manifest_path, "<Package></Package>").expect("write fake manifest");
    write_fake_legacy_packaging_forbidden_tool_scripts(&fake_bin);
    std::env::set_var("PATH", prepend_path(&fake_bin, environment.original_path()));
    std::env::set_var(
        "EASYDICT_LEGACY_PACKAGING_FORBIDDEN_TOOL_RECORD",
        &record_path,
    );

    for runtime_profile in [
        None,
        Some("rust-only"),
        Some("dotnet"),
        Some("dotnet-hybrid"),
        Some("unexpected"),
    ] {
        let mut publish = powershell_script_command(&root.join("dotnet/scripts/publish.ps1"));
        if let Some(profile) = runtime_profile {
            publish.args(["-RuntimeProfile", profile]);
        }
        assert_legacy_packaging_profile_rejected_before_tools(
            publish,
            "dotnet/scripts/publish.ps1",
            runtime_profile,
            &record_path,
        );

        let mut package_and_install =
            powershell_script_command(&root.join("dotnet/scripts/package-and-install.ps1"));
        package_and_install
            .args(["-Version", "0.0.0-test"])
            .arg("-SkipInstall");
        if let Some(profile) = runtime_profile {
            package_and_install.args(["-RuntimeProfile", profile]);
        }
        assert_legacy_packaging_profile_rejected_before_tools(
            package_and_install,
            "dotnet/scripts/package-and-install.ps1",
            runtime_profile,
            &record_path,
        );

        let mut package_msix =
            powershell_script_command(&root.join("dotnet/scripts/Package-Msix.ps1"));
        package_msix
            .args(["-Platform", "x64"])
            .arg("-PublishDir")
            .arg(&publish_dir)
            .arg("-ManifestPath")
            .arg(&manifest_path)
            .arg("-OutputMsixPath")
            .arg(&output_msix_path);
        if let Some(profile) = runtime_profile {
            package_msix.args(["-RuntimeProfile", profile]);
        }
        assert_legacy_packaging_profile_rejected_before_tools(
            package_msix,
            "dotnet/scripts/Package-Msix.ps1",
            runtime_profile,
            &record_path,
        );

        let mut build_installer =
            powershell_script_command(&root.join("dotnet/scripts/Build-Installer.ps1"));
        build_installer.args(["-Platform", "x64", "-Version", "0.0.0-test"]);
        if let Some(profile) = runtime_profile {
            build_installer.args(["-RuntimeProfile", profile]);
        }
        assert_legacy_packaging_profile_rejected_before_tools(
            build_installer,
            "dotnet/scripts/Build-Installer.ps1",
            runtime_profile,
            &record_path,
        );

        let mut extract_runtime =
            powershell_script_command(&root.join("dotnet/scripts/Extract-DotnetRuntime.ps1"));
        extract_runtime
            .args(["-Rid", "win-x64"])
            .arg("-OutputDir")
            .arg(&runtime_output_dir);
        if let Some(profile) = runtime_profile {
            extract_runtime.args(["-RuntimeProfile", profile]);
        }
        assert_legacy_packaging_profile_rejected_before_tools(
            extract_runtime,
            "dotnet/scripts/Extract-DotnetRuntime.ps1",
            runtime_profile,
            &record_path,
        );
    }

    let _ = fs::remove_dir_all(test_root);
}

#[cfg(windows)]
#[test]
fn extract_dotnet_runtime_powershell_shim_delegates_to_hybrid_rust_packager() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture([
        "PATH",
        "EASYDICT_FAKE_CARGO_RECORD",
        "EASYDICT_RUNTIME_PROFILE",
        "RUNTIME_PROFILE",
    ]);
    let root = repo_root();
    let test_root = tempfile_dir("extract-runtime-shim-hybrid");
    let fake_bin = test_root.join("bin");
    let runtime_output_dir = test_root.join("dotnet-runtime");
    let record_path = test_root.join("cargo-record.txt");

    write_fake_package_portable_tool_scripts(&fake_bin);
    std::env::set_var("PATH", prepend_path(&fake_bin, environment.original_path()));
    std::env::set_var("EASYDICT_FAKE_CARGO_RECORD", &record_path);
    std::env::remove_var("EASYDICT_RUNTIME_PROFILE");
    std::env::remove_var("RUNTIME_PROFILE");

    let output = powershell_script_command(&root.join("dotnet/scripts/Extract-DotnetRuntime.ps1"))
        .args(["-Rid", "win-arm64"])
        .arg("-OutputDir")
        .arg(&runtime_output_dir)
        .args(["-Version", "8.0.99"])
        .args(["-RuntimeProfile", "Hybrid"])
        .output()
        .expect("run Extract-DotnetRuntime shim");

    assert!(
        output.status.success(),
        "Extract-DotnetRuntime shim should delegate to fake cargo for explicit Hybrid\n{}",
        powershell_output_text(&output)
    );

    let record = read_text(&record_path);
    let runtime_output_dir_text = runtime_output_dir.display().to_string();
    for expected in [
        "-p easydict_packager",
        "--features hybrid-dotnet-runtime-packaging",
        "extract-dotnet-runtime",
        "--rid win-arm64",
        "--output-dir",
        runtime_output_dir_text.as_str(),
        "--version 8.0.99",
        "--runtime-profile Hybrid",
    ] {
        assert_contains(
            &record,
            expected,
            "Extract-DotnetRuntime should delegate the hybrid runtime extraction to Rust cargo",
        );
    }
    assert_not_contains(
        &record,
        "FORBIDDEN_DOTNET",
        "Extract-DotnetRuntime shim must not invoke dotnet directly",
    );

    let script = read_text(&root.join("dotnet/scripts/Extract-DotnetRuntime.ps1"));
    for forbidden in [
        "Invoke-WebRequest",
        "Expand-Archive",
        "System.IO.Compression",
    ] {
        assert_not_contains(
            &script,
            forbidden,
            "Extract-DotnetRuntime should not reintroduce PowerShell download/extract logic",
        );
    }

    let _ = fs::remove_dir_all(test_root);
}

#[cfg(windows)]
#[test]
fn msix_maintenance_powershell_shims_delegate_to_rust_cli() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture(["PATH", "EASYDICT_FAKE_CARGO_RECORD"]);
    let root = repo_root();
    let test_root = tempfile_dir("msix-maintenance-shims");
    let fake_bin = test_root.join("bin");
    let publish_dir = test_root.join("publish");
    let msix_path = test_root.join("Easydict.msix");
    let record_path = test_root.join("cargo-record.txt");

    fs::create_dir_all(&publish_dir).expect("create fake publish dir");
    fs::write(&msix_path, b"fake msix").expect("write fake MSIX path");
    write_fake_package_portable_tool_scripts(&fake_bin);
    std::env::set_var("PATH", prepend_path(&fake_bin, environment.original_path()));
    std::env::set_var("EASYDICT_FAKE_CARGO_RECORD", &record_path);

    let fix_output = powershell_script_command(&root.join("dotnet/scripts/Fix-MsixMinVersion.ps1"))
        .arg("-MsixPath")
        .arg(&msix_path)
        .args(["-MinVersion", "10.0.19041.0"])
        .output()
        .expect("run Fix-MsixMinVersion shim");
    assert!(
        fix_output.status.success(),
        "Fix-MsixMinVersion shim should delegate to fake cargo\n{}",
        powershell_output_text(&fix_output)
    );

    let dedupe_output =
        powershell_script_command(&root.join("dotnet/scripts/Dedupe-WorkerSharedFiles.ps1"))
            .arg("-PublishDir")
            .arg(&publish_dir)
            .output()
            .expect("run Dedupe-WorkerSharedFiles shim");
    assert!(
        dedupe_output.status.success(),
        "Dedupe-WorkerSharedFiles shim should delegate to fake cargo\n{}",
        powershell_output_text(&dedupe_output)
    );

    let record = read_text(&record_path);
    assert_eq!(
        record
            .lines()
            .filter(|line| line.starts_with("ARGS=run --manifest-path "))
            .count(),
        2,
        "both MSIX maintenance shims should call cargo run:\n{record}"
    );
    for expected in [
        "-p easydict_msix_validate",
        "fix-minversion",
        msix_path.display().to_string().as_str(),
        "--min-version 10.0.19041.0",
        "dedupe-worker-shared",
        publish_dir.display().to_string().as_str(),
    ] {
        assert_contains(
            &record,
            expected,
            "MSIX maintenance shims should pass through the Rust CLI subcommands and paths",
        );
    }
    assert_not_contains(
        &record,
        "FORBIDDEN_DOTNET",
        "MSIX maintenance shims should not invoke dotnet directly",
    );

    let _ = fs::remove_dir_all(test_root);
}

#[cfg(windows)]
#[test]
fn package_msix_powershell_shim_runs_rust_prepare_winapp_then_rust_fix() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture(["PATH", "EASYDICT_FAKE_CARGO_RECORD"]);
    let root = repo_root();
    let test_root = tempfile_dir("package-msix-shim-hybrid");
    let fake_bin = test_root.join("bin");
    let publish_dir = test_root.join("publish");
    let manifest_path = test_root.join("Package.appxmanifest");
    let output_msix_path = test_root.join("out").join("Easydict.msix");
    let record_path = test_root.join("package-msix-record.txt");

    fs::create_dir_all(&publish_dir).expect("create fake publish dir");
    fs::write(&manifest_path, "<Package></Package>").expect("write fake manifest");
    write_fake_package_msix_success_tool_scripts(&fake_bin);
    std::env::set_var("PATH", prepend_path(&fake_bin, environment.original_path()));
    std::env::set_var("EASYDICT_FAKE_CARGO_RECORD", &record_path);

    let output = powershell_script_command(&root.join("dotnet/scripts/Package-Msix.ps1"))
        .args(["-Platform", "x64"])
        .arg("-PublishDir")
        .arg(&publish_dir)
        .arg("-ManifestPath")
        .arg(&manifest_path)
        .arg("-OutputMsixPath")
        .arg(&output_msix_path)
        .args(["-RuntimeProfile", "Hybrid"])
        .args(["-MsixVersion", "9.8.7.6"])
        .arg("-VerifyTargetsizeIcons")
        .output()
        .expect("run Package-Msix shim");

    assert!(
        output.status.success(),
        "Package-Msix shim should complete with fake cargo and winapp\n{}",
        powershell_output_text(&output)
    );
    assert!(
        output_msix_path.is_file(),
        "fake winapp should create the package path for the MinVersion fixer"
    );

    let record = read_text(&record_path);
    let prepare_index = record
        .find("prepare-package-inputs")
        .expect("Package-Msix should call Rust prepare-package-inputs");
    let winapp_index = record
        .find("WINAPP_ARGS=package")
        .expect("Package-Msix should call winapp package after Rust prepare");
    let fix_index = record
        .find("fix-minversion")
        .expect("Package-Msix should call Rust fix-minversion after winapp");
    assert!(
        prepare_index < winapp_index && winapp_index < fix_index,
        "Package-Msix should run Rust prepare, winapp package, then Rust fix-minversion:\n{record}"
    );

    let publish_dir_text = publish_dir.display().to_string();
    let manifest_path_text = manifest_path.display().to_string();
    let output_msix_text = output_msix_path.display().to_string();
    for expected in [
        "-p easydict_msix_validate",
        "prepare-package-inputs",
        "--platform x64",
        "--publish-dir",
        publish_dir_text.as_str(),
        "--manifest",
        manifest_path_text.as_str(),
        "--runtime-profile hybrid",
        "--msix-version 9.8.7.6",
        "--verify-targetsize-icons",
        "WINAPP_ARGS=package",
        "--output",
        output_msix_text.as_str(),
        "--skip-pri --verbose",
        "fix-minversion",
        "--min-version 10.0.19041.0",
    ] {
        assert_contains(
            &record,
            expected,
            "Package-Msix shim should pass the expected Rust/winapp arguments",
        );
    }

    let package_script = read_text(&root.join("dotnet/scripts/Package-Msix.ps1"));
    assert_not_contains(
        &package_script,
        "[xml]",
        "Package-Msix should not fall back to PowerShell XML manifest rewriting",
    );
    assert_not_contains(
        &package_script,
        "System.Xml",
        "Package-Msix should keep manifest editing in the Rust MSIX helper",
    );
    assert_not_contains(
        &record,
        "FORBIDDEN_DOTNET",
        "Package-Msix should not invoke dotnet directly",
    );

    let _ = fs::remove_dir_all(test_root);
}

#[cfg(windows)]
#[test]
fn sign_and_install_runs_rust_msix_validator_before_install_with_rust_only_default() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture(["PATH", "EASYDICT_FAKE_CARGO_RECORD"]);
    let root = repo_root();
    let test_root = tempfile_dir("sign-and-install-rust-only-validator");
    let fake_bin = test_root.join("bin");
    let package_path = test_root.join("Easydict.msix");
    let cert_path = test_root.join("dev-signing.pfx");
    let record_path = test_root.join("sign-install-record.txt");
    let wrapper_path = test_root.join("invoke-sign-and-install.ps1");

    fs::create_dir_all(&test_root).expect("create fake sign/install root");
    fs::write(&package_path, b"fake msix").expect("write fake MSIX path");
    fs::write(&cert_path, b"fake certificate").expect("write fake signing certificate");
    write_fake_sign_and_install_tool_scripts(&fake_bin);
    write_sign_and_install_wrapper(
        &wrapper_path,
        &root.join("dotnet/scripts/sign-and-install.ps1"),
        &package_path,
        &cert_path,
        None,
        &record_path,
    );
    std::env::set_var("PATH", prepend_path(&fake_bin, environment.original_path()));
    std::env::set_var("EASYDICT_FAKE_CARGO_RECORD", &record_path);

    let output = powershell_script_command(&wrapper_path)
        .output()
        .expect("run sign-and-install shim");
    assert!(
        output.status.success(),
        "sign-and-install shim should complete with fake winapp/cargo/Appx cmdlets\n{}",
        powershell_output_text(&output)
    );

    let record = read_text(&record_path);
    let sign_index = record
        .find("WINAPP_ARGS=sign")
        .expect("sign-and-install should sign before validation");
    let validator_index = record
        .find("-p easydict_msix_validate")
        .expect("sign-and-install should call the Rust MSIX validator");
    let install_index = record
        .find("ADD_APPX_PACKAGE=")
        .expect("sign-and-install should install after validation");
    assert!(
        sign_index < validator_index && validator_index < install_index,
        "sign-and-install should sign, validate, then Add-AppxPackage:\n{record}"
    );
    assert_contains(
        &record,
        package_path.display().to_string().as_str(),
        "sign-and-install validator should receive the package path",
    );
    assert_not_contains(
        &record,
        "--runtime-profile",
        "omitted RuntimeProfile should let easydict_msix_validate use its rust-only default",
    );
    assert_not_contains(
        &record,
        "FORBIDDEN_DOTNET",
        "sign-and-install should not invoke dotnet directly",
    );

    let _ = fs::remove_dir_all(test_root);
}

#[cfg(windows)]
#[test]
fn sign_and_install_passes_hybrid_profile_to_validator_only_when_explicit() {
    let _guard = ENVIRONMENT_LOCK.lock().expect("environment lock poisoned");
    let environment = EnvironmentSnapshot::capture(["PATH", "EASYDICT_FAKE_CARGO_RECORD"]);
    let root = repo_root();
    let test_root = tempfile_dir("sign-and-install-hybrid-validator");
    let fake_bin = test_root.join("bin");
    let package_path = test_root.join("Easydict.msix");
    let cert_path = test_root.join("dev-signing.pfx");
    let record_path = test_root.join("sign-install-record.txt");
    let wrapper_path = test_root.join("invoke-sign-and-install.ps1");

    fs::create_dir_all(&test_root).expect("create fake sign/install root");
    fs::write(&package_path, b"fake msix").expect("write fake MSIX path");
    fs::write(&cert_path, b"fake certificate").expect("write fake signing certificate");
    write_fake_sign_and_install_tool_scripts(&fake_bin);
    write_sign_and_install_wrapper(
        &wrapper_path,
        &root.join("dotnet/scripts/sign-and-install.ps1"),
        &package_path,
        &cert_path,
        Some("Hybrid"),
        &record_path,
    );
    std::env::set_var("PATH", prepend_path(&fake_bin, environment.original_path()));
    std::env::set_var("EASYDICT_FAKE_CARGO_RECORD", &record_path);

    let output = powershell_script_command(&wrapper_path)
        .output()
        .expect("run sign-and-install shim with Hybrid profile");
    assert!(
        output.status.success(),
        "sign-and-install Hybrid shim should complete with fake tools\n{}",
        powershell_output_text(&output)
    );

    let record = read_text(&record_path);
    let validator_index = record
        .find("-p easydict_msix_validate")
        .expect("sign-and-install should call the Rust MSIX validator");
    let install_index = record
        .find("ADD_APPX_PACKAGE=")
        .expect("sign-and-install should install after validation");
    assert!(
        validator_index < install_index,
        "sign-and-install should validate before Add-AppxPackage:\n{record}"
    );
    assert_contains(
        &record,
        "--runtime-profile hybrid",
        "explicit Hybrid should be forwarded to easydict_msix_validate",
    );
    assert_not_contains(
        &record,
        "FORBIDDEN_DOTNET",
        "sign-and-install should not invoke dotnet directly",
    );

    let _ = fs::remove_dir_all(test_root);
}

fn repo_root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .ancestors()
        .nth(3)
        .expect("packager crate should live under rs/crates/easydict_packager")
        .to_path_buf()
}

fn read_text(path: &Path) -> String {
    std::fs::read_to_string(path)
        .unwrap_or_else(|error| panic!("failed to read {}: {error}", path.display()))
}

fn assert_contains(haystack: &str, needle: &str, message: &str) {
    assert!(haystack.contains(needle), "{message}\nmissing: {needle}");
}

fn assert_not_contains(haystack: &str, needle: &str, message: &str) {
    assert!(!haystack.contains(needle), "{message}\nforbidden: {needle}");
}

fn assert_source_line_is_feature_gated(source: &str, needle: &str, message: &str) {
    const FEATURE_CFG: &str = "#[cfg(feature = \"hybrid-dotnet-runtime-packaging\")]";

    let lines = source.lines().collect::<Vec<_>>();
    let line_index = lines
        .iter()
        .position(|line| line.contains(needle))
        .unwrap_or_else(|| panic!("{message}\nmissing: {needle}"));
    let preceding_attributes = lines[..line_index]
        .iter()
        .rev()
        .map(|line| line.trim())
        .take_while(|line| line.starts_with("#[") || line.is_empty())
        .collect::<Vec<_>>();

    assert!(
        preceding_attributes.contains(&FEATURE_CFG),
        "{message}\n{needle} must be immediately preceded by {FEATURE_CFG}"
    );
}

fn directory_entry_names(root: &Path) -> Vec<String> {
    let mut entries = Vec::new();
    collect_directory_entry_names(root, root, &mut entries);
    entries.sort();
    entries
}

fn collect_directory_entry_names(root: &Path, current: &Path, entries: &mut Vec<String>) {
    let mut children = fs::read_dir(current)
        .unwrap_or_else(|error| panic!("read directory {}: {error}", current.display()))
        .collect::<Result<Vec<_>, _>>()
        .unwrap_or_else(|error| panic!("collect directory {}: {error}", current.display()));
    children.sort_by_key(|entry| entry.path());

    for child in children {
        let path = child.path();
        entries.push(relative_entry_name(root, &path));
        if path.is_dir() {
            collect_directory_entry_names(root, &path, entries);
        }
    }
}

fn zip_entry_names(zip_path: &Path) -> Vec<String> {
    let file = fs::File::open(zip_path)
        .unwrap_or_else(|error| panic!("open ZIP {}: {error}", zip_path.display()));
    let mut archive = ZipArchive::new(BufReader::new(file))
        .unwrap_or_else(|error| panic!("read ZIP {}: {error}", zip_path.display()));
    let mut entries = Vec::new();
    for index in 0..archive.len() {
        let entry = archive
            .by_index(index)
            .unwrap_or_else(|error| panic!("read ZIP entry {index}: {error}"));
        entries.push(entry.name().trim_end_matches('/').replace('\\', "/"));
    }
    entries.sort();
    entries
}

fn extract_zip(zip_path: &Path, destination: &Path) {
    let file = fs::File::open(zip_path)
        .unwrap_or_else(|error| panic!("open ZIP {}: {error}", zip_path.display()));
    let mut archive = ZipArchive::new(BufReader::new(file))
        .unwrap_or_else(|error| panic!("read ZIP {}: {error}", zip_path.display()));
    archive.extract(destination).unwrap_or_else(|error| {
        panic!(
            "extract ZIP {} to {}: {error}",
            zip_path.display(),
            destination.display()
        )
    });
}

fn relative_entry_name(root: &Path, path: &Path) -> String {
    path.strip_prefix(root)
        .unwrap_or_else(|error| {
            panic!(
                "entry {} should be under {}: {error}",
                path.display(),
                root.display()
            )
        })
        .components()
        .map(|component| component.as_os_str().to_string_lossy())
        .collect::<Vec<_>>()
        .join("/")
}

fn assert_entries_do_not_contain_retained_dotnet_payload(entries: &[String], label: &str) {
    for entry in entries {
        let normalized = entry
            .replace('\\', "/")
            .trim_matches('/')
            .to_ascii_lowercase();
        let components = normalized.split('/').collect::<Vec<_>>();
        let first = components.first().copied().unwrap_or_default();
        let file_name = components.last().copied().unwrap_or_default();
        let contains_retained_payload = first == "dotnet"
            || first == "workers"
            || normalized.contains("/host/fxr/")
            || normalized.contains("/shared/microsoft.netcore.app/")
            || normalized.contains("/shared/microsoft.windowsdesktop.app/")
            || normalized.contains("/shared/microsoft.aspnetcore.app/")
            || matches!(
                file_name,
                "createdump.exe"
                    | "dotnet.exe"
                    | "hostfxr.dll"
                    | "coreclr.dll"
                    | "hostpolicy.dll"
                    | "clrjit.dll"
                    | "mscordaccore.dll"
                    | "mscordbi.dll"
                    | "mscorlib.dll"
                    | "netstandard.dll"
                    | "system.private.corelib.dll"
            )
            || file_name.ends_with(".runtimeconfig.json")
            || file_name.ends_with(".deps.json")
            || file_name.starts_with("easydict.compathost")
            || file_name.starts_with("easydict.nativebridge")
            || file_name.starts_with("easydict.sidecarclient")
            || file_name.starts_with("easydict.workers.")
            || file_name.starts_with("easydict.winui");
        assert!(
            !contains_retained_payload,
            "{label} should not contain retained .NET runtime/worker payload entry: {entry}"
        );
    }
}

#[cfg(windows)]
fn translate_long_doc_script_command(root: &Path) -> std::process::Command {
    powershell_script_command(&root.join("scripts/translate-long-doc.ps1"))
}

#[cfg(windows)]
fn powershell_script_command(script_path: &Path) -> std::process::Command {
    let shell = if std::process::Command::new("pwsh")
        .args(["-NoProfile", "-Command", "$PSVersionTable.PSVersion"])
        .output()
        .is_ok_and(|output| output.status.success())
    {
        "pwsh"
    } else {
        "powershell"
    };
    let mut command = std::process::Command::new(shell);
    command
        .args(["-NoProfile", "-ExecutionPolicy", "Bypass", "-File"])
        .arg(script_path);
    command
}

#[cfg(windows)]
fn write_fake_package_portable_tool_scripts(fake_bin: &Path) {
    fs::create_dir_all(fake_bin).expect("create fake Package-Portable tool dir");
    fs::write(
        fake_bin.join("cargo.cmd"),
        "@echo off\r\n\
>>\"%EASYDICT_FAKE_CARGO_RECORD%\" echo EASYDICT_RUNTIME_PROFILE=%EASYDICT_RUNTIME_PROFILE%\r\n\
>>\"%EASYDICT_FAKE_CARGO_RECORD%\" echo RUNTIME_PROFILE=%RUNTIME_PROFILE%\r\n\
>>\"%EASYDICT_FAKE_CARGO_RECORD%\" echo EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS=%EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS%\r\n\
>>\"%EASYDICT_FAKE_CARGO_RECORD%\" echo ARGS=%*\r\n\
exit /b 0\r\n",
    )
    .expect("write fake Package-Portable cargo");
    fs::write(
        fake_bin.join("dotnet.cmd"),
        "@echo off\r\n\
>>\"%EASYDICT_FAKE_CARGO_RECORD%\" echo FORBIDDEN_DOTNET=%*\r\n\
exit /b 87\r\n",
    )
    .expect("write fake Package-Portable dotnet");
}

#[cfg(windows)]
fn write_fake_package_msix_success_tool_scripts(fake_bin: &Path) {
    write_fake_package_portable_tool_scripts(fake_bin);
    fs::write(
        fake_bin.join("winapp.cmd"),
        "@echo off\r\n\
>>\"%EASYDICT_FAKE_CARGO_RECORD%\" echo WINAPP_ARGS=%*\r\n\
set \"out=\"\r\n\
set \"nextIsOutput=\"\r\n\
:parse\r\n\
if \"%~1\"==\"\" goto done\r\n\
if defined nextIsOutput set \"out=%~1\" & set \"nextIsOutput=\" & shift & goto parse\r\n\
if /I \"%~1\"==\"--output\" set \"nextIsOutput=1\"\r\n\
shift\r\n\
goto parse\r\n\
:done\r\n\
if \"%out%\"==\"\" exit /b 0\r\n\
for %%I in (\"%out%\") do if not exist \"%%~dpI\" mkdir \"%%~dpI\"\r\n\
>\"%out%\" echo fake msix\r\n\
exit /b 0\r\n",
    )
    .expect("write fake winapp");
}

#[cfg(windows)]
fn write_fake_sign_and_install_tool_scripts(fake_bin: &Path) {
    fs::create_dir_all(fake_bin).expect("create fake sign/install tool dir");
    fs::write(
        fake_bin.join("cargo.cmd"),
        "@echo off\r\n\
>>\"%EASYDICT_FAKE_CARGO_RECORD%\" echo ARGS=%*\r\n\
exit /b 0\r\n",
    )
    .expect("write fake sign-and-install cargo");
    fs::write(
        fake_bin.join("winapp.cmd"),
        "@echo off\r\n\
>>\"%EASYDICT_FAKE_CARGO_RECORD%\" echo WINAPP_ARGS=%*\r\n\
exit /b 0\r\n",
    )
    .expect("write fake sign-and-install winapp");
    fs::write(
        fake_bin.join("dotnet.cmd"),
        "@echo off\r\n\
>>\"%EASYDICT_FAKE_CARGO_RECORD%\" echo FORBIDDEN_DOTNET=%*\r\n\
exit /b 87\r\n",
    )
    .expect("write fake sign-and-install dotnet");
}

#[cfg(windows)]
fn write_sign_and_install_wrapper(
    wrapper_path: &Path,
    script_path: &Path,
    package_path: &Path,
    cert_path: &Path,
    runtime_profile: Option<&str>,
    record_path: &Path,
) {
    let runtime_profile_arg = runtime_profile
        .map(|profile| format!(" -RuntimeProfile {}", powershell_string_literal(profile)))
        .unwrap_or_default();
    fs::write(
        wrapper_path,
        format!(
            "$ErrorActionPreference = 'Stop'\r\n\
function Get-AppxPackage {{\r\n\
    param([string]$Name, [System.Management.Automation.ActionPreference]$ErrorAction)\r\n\
    Add-Content -LiteralPath {} -Value \"GET_APPX_PACKAGE=$Name\"\r\n\
    return $null\r\n\
}}\r\n\
function Remove-AppxPackage {{\r\n\
    param([string]$Package)\r\n\
    Add-Content -LiteralPath {} -Value \"REMOVE_APPX_PACKAGE=$Package\"\r\n\
}}\r\n\
function Add-AppxPackage {{\r\n\
    param([string]$Path)\r\n\
    Add-Content -LiteralPath {} -Value \"ADD_APPX_PACKAGE=$Path\"\r\n\
}}\r\n\
& {} -PackagePath {} -CertPath {}{}\r\n",
            powershell_literal(record_path),
            powershell_literal(record_path),
            powershell_literal(record_path),
            powershell_literal(script_path),
            powershell_literal(package_path),
            powershell_literal(cert_path),
            runtime_profile_arg,
        ),
    )
    .expect("write sign-and-install wrapper");
}

#[cfg(windows)]
fn write_package_portable_wrapper(
    wrapper_path: &Path,
    package_script: &Path,
    output_root: &Path,
    post_env_record_path: &Path,
) {
    fs::write(
        wrapper_path,
        format!(
            "$ErrorActionPreference = 'Stop'\r\n\
$env:EASYDICT_RUNTIME_PROFILE = 'hybrid'\r\n\
$env:RUNTIME_PROFILE = 'hybrid'\r\n\
& {} -Platform x64 -Configuration Debug -OutputRoot {} -PackageVersion v0.0.0-shim -NoZip\r\n\
Add-Content -LiteralPath {} -Value \"POST_EASYDICT_RUNTIME_PROFILE=$env:EASYDICT_RUNTIME_PROFILE\"\r\n\
Add-Content -LiteralPath {} -Value \"POST_RUNTIME_PROFILE=$env:RUNTIME_PROFILE\"\r\n",
            powershell_literal(package_script),
            powershell_literal(output_root),
            powershell_literal(post_env_record_path),
            powershell_literal(post_env_record_path),
        ),
    )
    .expect("write Package-Portable wrapper");
}

#[cfg(windows)]
fn write_build_rust_helpers_wrapper(
    wrapper_path: &Path,
    build_script: &Path,
    output_dir: &Path,
    post_env_record_path: &Path,
) {
    fs::write(
        wrapper_path,
        format!(
            "$ErrorActionPreference = 'Stop'\r\n\
$env:EASYDICT_RUNTIME_PROFILE = 'hybrid'\r\n\
$env:RUNTIME_PROFILE = 'hybrid'\r\n\
& {} -Platform arm64 -Configuration Debug -OutputDir {}\r\n\
Add-Content -LiteralPath {} -Value \"POST_EASYDICT_RUNTIME_PROFILE=$env:EASYDICT_RUNTIME_PROFILE\"\r\n\
Add-Content -LiteralPath {} -Value \"POST_RUNTIME_PROFILE=$env:RUNTIME_PROFILE\"\r\n",
            powershell_literal(build_script),
            powershell_literal(output_dir),
            powershell_literal(post_env_record_path),
            powershell_literal(post_env_record_path),
        ),
    )
    .expect("write Build-RustHelpers wrapper");
}

#[cfg(windows)]
fn powershell_literal(path: &Path) -> String {
    powershell_string_literal(&path.display().to_string())
}

#[cfg(windows)]
fn powershell_string_literal(value: &str) -> String {
    format!("'{}'", value.replace('\'', "''"))
}

#[cfg(windows)]
fn assert_legacy_packaging_profile_rejected_before_tools(
    mut command: std::process::Command,
    script_name: &str,
    runtime_profile: Option<&str>,
    record_path: &Path,
) {
    let output = command.output().expect("run legacy packaging script");
    let output_text = powershell_output_text(&output);
    assert!(
        !output.status.success(),
        "{script_name} should reject {:?} before external tools\n{output_text}",
        runtime_profile
    );
    assert_contains(
        &output_text,
        "RuntimeProfile",
        &format!("{script_name} should explain the rejected runtime profile"),
    );
    if let Some(profile) = runtime_profile {
        if legacy_runtime_profile_is_rust_only(profile) {
            assert!(
                output_text.to_ascii_lowercase().contains("portable"),
                "{script_name} should redirect rust-only callers to the rs portable path\n{output_text}",
            );
        } else {
            assert_contains(
                &output_text,
                "Hybrid",
                &format!(
                    "{script_name} should reject unknown legacy profiles as explicit Hybrid-only"
                ),
            );
        }
    } else {
        assert_contains(
            &output_text,
            "Hybrid",
            &format!("{script_name} should require explicit Hybrid"),
        );
    }
    assert!(
        !record_path.exists(),
        "{script_name} must reject {:?} before invoking dotnet/cargo/winapp/ISCC; record:\n{}",
        runtime_profile,
        read_text(record_path)
    );
}

#[cfg(windows)]
fn legacy_runtime_profile_is_rust_only(profile: &str) -> bool {
    let normalized = profile.trim().replace('_', "-").to_ascii_lowercase();
    matches!(normalized.as_str(), "rust-only" | "rustonly")
}

#[cfg(windows)]
fn powershell_output_text(output: &std::process::Output) -> String {
    format!(
        "stdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    )
}

#[cfg(windows)]
fn write_fake_legacy_packaging_forbidden_tool_scripts(fake_bin: &Path) {
    fs::create_dir_all(fake_bin).expect("create fake legacy packaging tool dir");
    for tool in ["dotnet.cmd", "cargo.cmd", "winapp.cmd", "ISCC.cmd"] {
        fs::write(
            fake_bin.join(tool),
            format!(
                "@echo off\r\n\
>>\"%EASYDICT_LEGACY_PACKAGING_FORBIDDEN_TOOL_RECORD%\" echo {tool} %*\r\n\
exit /b 87\r\n"
            ),
        )
        .expect("write fake legacy packaging tool");
    }
}

#[cfg(windows)]
fn write_fake_long_doc_helper(path: &Path) {
    fs::write(
        path,
        "@echo off\r\n\
setlocal\r\n\
>>\"%EASYDICT_LONG_DOC_HELPER_RECORD%\" echo HELPER=%~f0\r\n\
>>\"%EASYDICT_LONG_DOC_HELPER_RECORD%\" echo EASYDICT_RUNTIME_PROFILE=%EASYDICT_RUNTIME_PROFILE%\r\n\
>>\"%EASYDICT_LONG_DOC_HELPER_RECORD%\" echo RUNTIME_PROFILE=%RUNTIME_PROFILE%\r\n\
>>\"%EASYDICT_LONG_DOC_HELPER_RECORD%\" echo ARGS=%*\r\n\
exit /b 0\r\n",
    )
    .expect("write fake LongDoc helper");
}

#[cfg(windows)]
fn write_fake_long_doc_helper_exe(path: &Path) {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).expect("create fake LongDoc exe parent");
    }
    let source_path = path.with_extension("rs");
    fs::write(
        &source_path,
        r#"
use std::{env, fs, io::Write as _};

fn main() {
    let record_path = env::var("EASYDICT_LONG_DOC_HELPER_RECORD").expect("record path");
    let helper = env::current_exe().expect("current exe");
    let args = env::args().skip(1).collect::<Vec<_>>().join(" ");
    let easydict_runtime_profile =
        env::var("EASYDICT_RUNTIME_PROFILE").unwrap_or_default();
    let runtime_profile = env::var("RUNTIME_PROFILE").unwrap_or_default();
    fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(record_path)
        .and_then(|mut file| {
            writeln!(file, "HELPER={}", helper.display())?;
            writeln!(
                file,
                "EASYDICT_RUNTIME_PROFILE={}",
                easydict_runtime_profile
            )?;
            writeln!(file, "RUNTIME_PROFILE={}", runtime_profile)?;
            writeln!(file, "ARGS={}", args)
        })
        .expect("append fake LongDoc helper record");
}
"#,
    )
    .expect("write fake LongDoc exe source");
    let status = std::process::Command::new("rustc")
        .arg(&source_path)
        .arg("-o")
        .arg(path)
        .status()
        .expect("compile fake LongDoc helper executable");
    assert!(
        status.success(),
        "fake LongDoc helper executable should compile"
    );
}

#[cfg(windows)]
fn write_fake_forbidden_tool_scripts(fake_bin: &Path) {
    fs::create_dir_all(fake_bin).expect("create fake forbidden tool dir");
    for tool in ["cargo.cmd", "dotnet.cmd"] {
        fs::write(
            fake_bin.join(tool),
            format!(
                "@echo off\r\n\
>>\"%EASYDICT_LONG_DOC_FORBIDDEN_TOOL_RECORD%\" echo {tool} %*\r\n\
exit /b 87\r\n"
            ),
        )
        .expect("write fake forbidden tool");
    }
}

#[cfg(windows)]
fn write_fake_long_doc_cargo_script(fake_bin: &Path) {
    fs::create_dir_all(fake_bin).expect("create fake LongDoc cargo dir");
    fs::write(
        fake_bin.join("cargo.cmd"),
        "@echo off\r\n\
setlocal\r\n\
>>\"%EASYDICT_LONG_DOC_HELPER_RECORD%\" echo CARGO=%~f0\r\n\
>>\"%EASYDICT_LONG_DOC_HELPER_RECORD%\" echo EASYDICT_RUNTIME_PROFILE=%EASYDICT_RUNTIME_PROFILE%\r\n\
>>\"%EASYDICT_LONG_DOC_HELPER_RECORD%\" echo RUNTIME_PROFILE=%RUNTIME_PROFILE%\r\n\
>>\"%EASYDICT_LONG_DOC_HELPER_RECORD%\" echo ARGS=%*\r\n\
exit /b 0\r\n",
    )
    .expect("write fake LongDoc cargo");
}

#[cfg(windows)]
fn write_fake_dotnet_forbidden_tool_script(fake_bin: &Path) {
    fs::create_dir_all(fake_bin).expect("create fake dotnet forbidden tool dir");
    fs::write(
        fake_bin.join("dotnet.cmd"),
        "@echo off\r\n\
>>\"%EASYDICT_LONG_DOC_FORBIDDEN_TOOL_RECORD%\" echo dotnet.cmd %*\r\n\
exit /b 87\r\n",
    )
    .expect("write fake dotnet forbidden tool");
}

#[cfg(windows)]
fn write_fake_retained_long_doc_entrypoint(path: &Path) {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).expect("create fake retained LongDoc entrypoint dir");
    }
    fs::write(
        path,
        "@echo off\r\n\
>>\"%EASYDICT_LONG_DOC_FORBIDDEN_TOOL_RECORD%\" echo RETAINED_HELPER=%~f0 %*\r\n\
exit /b 0\r\n",
    )
    .expect("write fake retained LongDoc entrypoint");
}

#[cfg(windows)]
fn write_stale_dotnet_payload_markers(app_dir: &Path) {
    fs::create_dir_all(app_dir.join("workers").join("longdoc"))
        .expect("create stale LongDoc worker dir");
    fs::create_dir_all(app_dir.join("workers").join("localai"))
        .expect("create stale LocalAI worker dir");
    fs::create_dir_all(app_dir.join("dotnet").join("host").join("fxr"))
        .expect("create stale dotnet runtime host dir");
    fs::create_dir_all(
        app_dir
            .join("dotnet")
            .join("shared")
            .join("Microsoft.NETCore.App"),
    )
    .expect("create stale dotnet shared runtime dir");
    fs::write(
        app_dir.join("Easydict.CompatHost.exe"),
        b"stale compat host",
    )
    .expect("write stale CompatHost marker");
    fs::write(
        app_dir
            .join("workers")
            .join("longdoc")
            .join("Easydict.Workers.LongDoc.exe"),
        b"stale longdoc worker",
    )
    .expect("write stale LongDoc worker marker");
    fs::write(app_dir.join("dotnet").join("dotnet.exe"), b"stale dotnet")
        .expect("write stale dotnet marker");
}

fn text_between<'a>(text: &'a str, start: &str, end: &str) -> &'a str {
    let after_start = text
        .split_once(start)
        .unwrap_or_else(|| panic!("missing section start: {start}"))
        .1;
    after_start
        .split_once(end)
        .unwrap_or_else(|| panic!("missing section end: {end}"))
        .0
}

fn tempfile_dir(name: &str) -> PathBuf {
    let stamp = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|duration| duration.as_nanos())
        .unwrap_or_default();
    std::env::temp_dir().join(format!("{name}-{stamp}"))
}

fn prepend_path(first: &Path, original_path: Option<&OsString>) -> OsString {
    let mut paths = vec![first.to_path_buf()];
    if let Some(original_path) = original_path {
        paths.extend(std::env::split_paths(original_path));
    }
    std::env::join_paths(paths).expect("join fake tool PATH")
}

fn write_fake_windows_ai_manifest_for_workspace(workspace: &Path) {
    let manifest_path = workspace
        .parent()
        .expect("fake workspace should have a parent")
        .join("lib")
        .join("easydict-windows-ai")
        .join("Cargo.toml");
    fs::create_dir_all(manifest_path.parent().expect("manifest parent"))
        .expect("create fake WindowsAI manifest dir");
    fs::write(
        manifest_path,
        "[package]\nname = \"easydict_windows_ai\"\nversion = \"0.0.0\"\nedition = \"2021\"\n",
    )
    .expect("write fake WindowsAI manifest");
}

#[cfg(windows)]
fn write_fake_tooling_scripts(fake_bin: &Path) {
    fs::create_dir_all(fake_bin).expect("create fake tool dir");
    let source_path = fake_bin.join("fake-tool.rs");
    fs::write(
        &source_path,
        r#"
use std::{env, fs};

fn main() {
    let exe_name = env::current_exe()
        .ok()
        .and_then(|path| path.file_stem().map(|name| name.to_string_lossy().to_string()))
        .unwrap_or_default();
    if exe_name.eq_ignore_ascii_case("rustup") {
        if let Ok(record_path) = env::var("EASYDICT_FAKE_RUSTUP_RECORD") {
            let args = env::args().skip(1).collect::<Vec<_>>().join(" ");
            use std::io::Write as _;
            fs::OpenOptions::new()
                .create(true)
                .append(true)
                .open(&record_path)
                .and_then(|mut file| {
                    writeln!(file, "TOOL=rustup")?;
                    writeln!(file, "EASYDICT_RUNTIME_PROFILE={}", env::var("EASYDICT_RUNTIME_PROFILE").unwrap_or_default())?;
                    writeln!(file, "RUNTIME_PROFILE={}", env::var("RUNTIME_PROFILE").unwrap_or_default())?;
                    writeln!(file, "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS={}", env::var("EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS").unwrap_or_default())?;
                    writeln!(file, "ARGS={}", args)
                })
                .expect("append rustup record");
        }
        return;
    }
    if matches!(
        exe_name.to_ascii_lowercase().as_str(),
        "dotnet" | "powershell" | "pwsh"
    ) {
        if let Ok(record_path) = env::var("EASYDICT_RELEASE_FORBIDDEN_TOOL_RECORD") {
            let args = env::args().skip(1).collect::<Vec<_>>().join(" ");
            use std::io::Write as _;
            fs::OpenOptions::new()
                .create(true)
                .append(true)
                .open(&record_path)
                .and_then(|mut file| writeln!(file, "{} {}", exe_name, args))
                .expect("append forbidden tool record");
        }
        std::process::exit(87);
    }
    if exe_name.eq_ignore_ascii_case("easydict_cli") {
        let record_path = env::var("EASYDICT_PACKAGED_CLI_RECORD").expect("packaged CLI record path");
        let args = env::args().skip(1).collect::<Vec<_>>().join(" ");
        use std::io::Write as _;
        fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(&record_path)
            .and_then(|mut file| {
                writeln!(file, "CLI={}", exe_name)?;
                writeln!(file, "ARGS={}", args)?;
                writeln!(file, "EASYDICT_RUNTIME_PROFILE={}", env::var("EASYDICT_RUNTIME_PROFILE").unwrap_or_default())?;
                writeln!(file, "RUNTIME_PROFILE={}", env::var("RUNTIME_PROFILE").unwrap_or_default())
            })
            .expect("append packaged CLI record");
        println!("easydict_cli packaged smoke");
        return;
    }
    if exe_name.eq_ignore_ascii_case("Easydict.Rust") {
        let record_path = env::var("EASYDICT_PACKAGED_GUI_RECORD").expect("packaged GUI record path");
        let args = env::args().skip(1).collect::<Vec<_>>().join(" ");
        use std::io::Write as _;
        fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(&record_path)
            .and_then(|mut file| {
                writeln!(file, "GUI={}", exe_name)?;
                writeln!(file, "ARGS={}", args)?;
                writeln!(file, "EASYDICT_RUNTIME_PROFILE={}", env::var("EASYDICT_RUNTIME_PROFILE").unwrap_or_default())?;
                writeln!(file, "RUNTIME_PROFILE={}", env::var("RUNTIME_PROFILE").unwrap_or_default())
            })
            .expect("append packaged GUI record");
        println!("Easydict.Rust packaged smoke");
        return;
    }

    let record_path = env::var("EASYDICT_FAKE_CARGO_RECORD").expect("record path");
    let args = env::args().skip(1).collect::<Vec<_>>().join(" ");
    use std::io::Write as _;
    fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(&record_path)
        .and_then(|mut file| {
            writeln!(file, "EASYDICT_RUNTIME_PROFILE={}", env::var("EASYDICT_RUNTIME_PROFILE").unwrap_or_default())?;
            writeln!(file, "RUNTIME_PROFILE={}", env::var("RUNTIME_PROFILE").unwrap_or_default())?;
            writeln!(file, "EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS={}", env::var("EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS").unwrap_or_default())?;
            writeln!(file, "ARGS={}", args)
        })
        .expect("append cargo record");

    let target = env::current_dir()
        .expect("current dir")
        .join("target")
        .join("x86_64-pc-windows-msvc")
        .join("release");
    fs::create_dir_all(&target).expect("create fake target dir");
    let self_path = env::current_exe().expect("current exe path");
    fs::copy(&self_path, target.join("easydict_preview_iced.exe")).expect("write preview exe");
    for exe in [
        "easydict-native-bridge.exe",
        "easydict_browser_registrar.exe",
        "easydict_cli.exe",
        "easydict_long_doc.exe",
    ] {
        fs::copy(&self_path, target.join(exe)).expect("write helper exe");
    }
}
"#,
    )
    .expect("write fake tool source");
    let cargo_exe = fake_bin.join("cargo.exe");
    let status = std::process::Command::new("rustc")
        .arg(&source_path)
        .arg("-o")
        .arg(&cargo_exe)
        .status()
        .expect("compile fake cargo executable");
    assert!(status.success(), "fake cargo executable should compile");
    fs::copy(&cargo_exe, fake_bin.join("rustup.exe")).expect("copy fake rustup executable");
}

#[cfg(windows)]
fn write_fake_release_forbidden_tool_exes(fake_bin: &Path) {
    let cargo_exe = fake_bin.join("cargo.exe");
    for tool in ["dotnet.exe", "powershell.exe", "pwsh.exe"] {
        fs::copy(&cargo_exe, fake_bin.join(tool))
            .unwrap_or_else(|error| panic!("copy fake forbidden tool {tool}: {error}"));
    }
}

#[cfg(not(windows))]
fn write_fake_tooling_scripts(fake_bin: &Path) {
    fs::create_dir_all(fake_bin).expect("create fake tool dir");
    write_executable(
        fake_bin.join("rustup"),
        "#!/bin/sh\n\
if [ -n \"$EASYDICT_FAKE_RUSTUP_RECORD\" ]; then\n\
{\n\
printf 'TOOL=rustup\\n'\n\
printf 'EASYDICT_RUNTIME_PROFILE=%s\\n' \"$EASYDICT_RUNTIME_PROFILE\"\n\
printf 'RUNTIME_PROFILE=%s\\n' \"$RUNTIME_PROFILE\"\n\
printf 'EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS=%s\\n' \"$EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS\"\n\
printf 'ARGS=%s\\n' \"$*\"\n\
} >> \"$EASYDICT_FAKE_RUSTUP_RECORD\"\n\
fi\n\
exit 0\n",
    );
    write_executable(
        fake_bin.join("cargo"),
        "#!/bin/sh\n\
{\n\
printf 'EASYDICT_RUNTIME_PROFILE=%s\\n' \"$EASYDICT_RUNTIME_PROFILE\"\n\
printf 'RUNTIME_PROFILE=%s\\n' \"$RUNTIME_PROFILE\"\n\
printf 'EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS=%s\\n' \"$EASYDICT_WINDOWS_AI_REQUIRE_WINRT_BINDINGS\"\n\
printf 'ARGS=%s\\n' \"$*\"\n\
} >> \"$EASYDICT_FAKE_CARGO_RECORD\"\n\
target=\"$PWD/target/x86_64-pc-windows-msvc/release\"\n\
mkdir -p \"$target\"\n\
printf 'fake' > \"$target/easydict_preview_iced.exe\"\n\
for f in easydict-native-bridge.exe easydict_browser_registrar.exe easydict_cli.exe easydict_long_doc.exe; do\n\
  printf 'fake' > \"$target/$f\"\n\
done\n\
exit 0\n",
    );
}

#[cfg(not(windows))]
fn write_executable(path: PathBuf, contents: &str) {
    use std::os::unix::fs::PermissionsExt;

    fs::write(&path, contents).expect("write executable script");
    let mut permissions = fs::metadata(&path)
        .expect("read executable metadata")
        .permissions();
    permissions.set_mode(0o755);
    fs::set_permissions(path, permissions).expect("chmod executable script");
}

struct EnvironmentSnapshot {
    values: Vec<(&'static str, Option<OsString>)>,
}

impl EnvironmentSnapshot {
    fn capture<const N: usize>(names: [&'static str; N]) -> Self {
        Self {
            values: names
                .into_iter()
                .map(|name| (name, std::env::var_os(name)))
                .collect(),
        }
    }

    fn original_path(&self) -> Option<&OsString> {
        self.values
            .iter()
            .find(|(name, _)| *name == "PATH")
            .and_then(|(_, value)| value.as_ref())
    }
}

impl Drop for EnvironmentSnapshot {
    fn drop(&mut self) {
        for (name, value) in self.values.iter() {
            match value {
                Some(value) => std::env::set_var(name, value),
                None => std::env::remove_var(name),
            }
        }
    }
}
