use std::path::{Path, PathBuf};

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
fn legacy_dotnet_packaging_paths_reject_rust_only_and_require_hybrid_profile() {
    let root = repo_root();
    let makefile = read_text(&root.join("dotnet/Makefile"));
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
}

#[test]
fn dotnet_runtime_extraction_shim_requires_explicit_hybrid_profile() {
    let root = repo_root();
    let script = read_text(&root.join("dotnet/scripts/Extract-DotnetRuntime.ps1"));

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
