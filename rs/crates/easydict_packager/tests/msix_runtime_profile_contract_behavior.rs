use std::fs;
use std::path::{Path, PathBuf};

#[test]
fn qdc_deploy_and_install_validate_msix_with_rust_only_default_before_install() {
    let root = repo_root();
    let deploy_script = read_text(&root.join("dotnet/scripts/qdc/Deploy-ToQdc.ps1"));
    let install_script = read_text(&root.join("dotnet/scripts/qdc/Install-OnQdc.ps1"));

    assert_contains(
        &deploy_script,
        "[string]$RuntimeProfile = \"rust-only\"",
        "QDC deploy should default MSIX payload validation to rust-only",
    );
    assert_contains(
        &deploy_script,
        "$RuntimeProfile = Normalize-RuntimeProfile $RuntimeProfile",
        "QDC deploy should normalize the caller-provided runtime profile",
    );
    assert_contains(
        &deploy_script,
        "cargo build --manifest-path $cargoManifest -p easydict_msix_validate --release",
        "QDC deploy should build the Rust MSIX validator before copying install inputs",
    );
    assert_contains(
        &deploy_script,
        "@{ Local = $ValidatorPath;   Label = \"MSIX validator\" }",
        "QDC deploy should copy the validator executable to the remote staging directory",
    );
    assert_contains(
        &deploy_script,
        "$installFlags = \" -RuntimeProfile '$RuntimeProfile' -ValidatorPath '$remoteValidatorPath'\"",
        "QDC deploy should pass both runtime profile and validator path to remote install",
    );
    assert_contains(
        &deploy_script,
        "if ($Machine)   { $installFlags += \" -Machine\" }",
        "QDC deploy should forward explicit machine-scope installs to the remote installer",
    );
    assert_not_contains(
        &deploy_script,
        "[string]$RuntimeProfile = \"hybrid\"",
        "QDC deploy must not silently default to hybrid payload validation",
    );

    assert_contains(
        &install_script,
        "[string]$RuntimeProfile = \"rust-only\"",
        "QDC remote install should default runtime validation to rust-only",
    );
    assert_contains(
        &install_script,
        "[switch]$Machine",
        "QDC remote install should declare the -Machine switch forwarded by Deploy-ToQdc.ps1",
    );
    assert_contains(
        &install_script,
        "--runtime-profile",
        "QDC remote install should pass the runtime profile to the Rust validator",
    );
    assert_contains(
        &install_script,
        "--allow-unsigned",
        "QDC remote install should allow unsigned MSIX validation before signing trust is installed",
    );
    assert_contains(
        &install_script,
        "& cargo run --manifest-path $cargoManifest -p easydict_msix_validate -- @validatorArgs",
        "QDC remote install should retain a cargo-run fallback for checkout-based use",
    );
    assert_contains(
        &install_script,
        "Invoke-MsixValidator -Path $MsixPath -Profile $RuntimeProfile",
        "QDC remote install should invoke validation for the target MSIX",
    );

    assert_order(
        &install_script,
        "[1/5] Validating MSIX runtime payload",
        "Importing certificate to LocalMachine\\TrustedPeople",
        "QDC remote install should validate before importing certs",
    );
    assert_order(
        &install_script,
        "[1/5] Validating MSIX runtime payload",
        "Add-AppxPackage -Path $MsixPath",
        "QDC remote install should validate before Add-AppxPackage",
    );
}

#[test]
fn qdc_machine_install_switch_uses_machine_scope_provisioning_after_validation() {
    let root = repo_root();
    let install_script = read_text(&root.join("dotnet/scripts/qdc/Install-OnQdc.ps1"));
    let install_step = text_between(
        &install_script,
        "Write-Host \"[5/5] Installing MSIX...\"",
        "if (-not $installed)",
    );

    assert_contains(
        install_step,
        "if ($Machine)",
        "QDC remote install should branch on the explicit -Machine switch",
    );
    assert_contains(
        install_step,
        "Install-ProvisionedPackageAndRegister -Path $MsixPath -Name $PackageName",
        "QDC -Machine installs should provision the package and then register it for validation",
    );
    assert_contains(
        &install_script,
        "Add-AppxProvisionedPackage -Online -PackagePath $Path -SkipLicense",
        "QDC machine-scope install should use Add-AppxProvisionedPackage",
    );
    assert_order(
        &install_script,
        "[1/5] Validating MSIX runtime payload",
        "if ($Machine)",
        "QDC remote install should validate payloads before honoring -Machine install mode",
    );
}

#[test]
fn ui_automation_msix_path_forces_rust_only_and_validates_before_upload_and_install() {
    let root = repo_root();
    let workflow = read_text(&root.join(".github/workflows/ui-automation.yml"));
    let build_job = text_between(&workflow, "  build:", "  winui-tests:");
    let winui_tests_job = text_between(&workflow, "  winui-tests:", "  ui-automation:");
    let winui_test_run_step = text_between(
        winui_tests_job,
        "      - name: Run WinUI Unit Tests",
        "      - name: Surface failed test names as annotations",
    );
    let ui_automation_job = workflow
        .split_once("  ui-automation:")
        .unwrap_or_else(|| panic!("missing UI automation job"))
        .1;
    let install_step = text_between(
        &workflow,
        "      - name: Install MSIX package (register from extracted layout)",
        "      - name: Restore UI test dependencies",
    );
    let ui_restore_step = text_between(
        ui_automation_job,
        "      - name: Restore UI test dependencies",
        "      - name: Build UI Automation Tests",
    );
    let ui_build_step = text_between(
        ui_automation_job,
        "      - name: Build UI Automation Tests",
        "      - name: Run UI Automation Tests",
    );
    let ui_test_step = text_between(
        ui_automation_job,
        "      - name: Run UI Automation Tests",
        "      - name: Surface failed UI test names as annotations",
    );

    assert_contains(
        &workflow,
        "EASYDICT_RUNTIME_PROFILE: rust-only",
        "UI automation should force the Easydict runtime profile to rust-only",
    );
    assert_contains(
        &workflow,
        "RUNTIME_PROFILE: rust-only",
        "UI automation should force the generic runtime profile to rust-only",
    );
    assert_not_contains(
        &workflow,
        "RUNTIME_PROFILE: hybrid",
        "UI automation must not default the MSIX path to hybrid",
    );

    assert_contains(
        build_job,
        "-p:RuntimeProfile=${{ env.RUNTIME_PROFILE }}",
        "UI automation dotnet restore/publish should pass the rust-only RuntimeProfile",
    );
    assert_contains(
        build_job,
        "-p:EnableInProcLongDocFallback=false",
        "UI automation dotnet restore/publish should disable the retained .NET fallback",
    );
    assert_contains(
        build_job,
        "--self-contained false",
        "UI automation rust-only diagnostic publish should not bundle the .NET runtime",
    );
    assert_not_contains(
        build_job,
        "--self-contained true",
        "UI automation rust-only diagnostic publish must not create a self-contained .NET runtime payload",
    );
    assert_contains(
        winui_tests_job,
        "-p:RuntimeProfile=${{ env.RUNTIME_PROFILE }}",
        "UI automation WinUI unit tests should use the workflow rust-only RuntimeProfile",
    );
    assert_contains(
        winui_tests_job,
        "-p:EnableInProcLongDocFallback=false",
        "UI automation WinUI unit tests should keep the retained .NET fallback disabled",
    );
    assert_contains(
        winui_tests_job,
        "-p:BuildWorkerOutputs=false",
        "UI automation WinUI unit tests should not build retained worker outputs",
    );
    for (step, label) in [
        (winui_test_run_step, "WinUI dotnet test"),
        (ui_restore_step, "UIA restore"),
        (ui_build_step, "UIA build"),
        (ui_test_step, "UIA dotnet test"),
    ] {
        assert_contains(
            step,
            "-p:RuntimeProfile=${{ env.RUNTIME_PROFILE }}",
            &format!("{label} should carry the workflow rust-only RuntimeProfile"),
        );
        assert_contains(
            step,
            "-p:EnableInProcLongDocFallback=false",
            &format!("{label} should keep retained LongDoc fallback disabled"),
        );
        assert_contains(
            step,
            "-p:BuildWorkerOutputs=false",
            &format!("{label} should keep retained worker outputs disabled"),
        );
    }
    assert_not_contains(
        winui_tests_job,
        "-p:EnableInProcLongDocFallback=true",
        "UI automation WinUI unit tests must not explicitly re-enable the retained .NET fallback",
    );
    assert_not_contains(
        ui_automation_job,
        "-p:EnableInProcLongDocFallback=true",
        "UI automation test shards must not explicitly re-enable the retained .NET fallback",
    );
    assert_contains(
        build_job,
        "cargo run --manifest-path rs/Cargo.toml -p easydict_msix_validate --",
        "UI automation build job should validate the MSIX before upload",
    );
    assert_contains(
        build_job,
        "--runtime-profile \"${{ env.RUNTIME_PROFILE }}\"",
        "UI automation build job should validate with the workflow runtime profile",
    );
    assert_contains(
        build_job,
        "--allow-unsigned",
        "UI automation MSIX validation should allow unsigned CI packages",
    );
    assert_order(
        build_job,
        "Validate MSIX (identity, MinVersion, runtime payload)",
        "Upload MSIX package",
        "UI automation should validate the MSIX before upload",
    );

    assert_contains(
        install_step,
        "cargo run --manifest-path rs/Cargo.toml -p easydict_msix_validate --",
        "UI automation install job should re-validate the downloaded MSIX before install",
    );
    assert_order(
        install_step,
        "cargo run --manifest-path rs/Cargo.toml -p easydict_msix_validate --",
        "# Remove any previous installation",
        "UI automation should validate before mutating the existing app install",
    );
    assert_order(
        install_step,
        "cargo run --manifest-path rs/Cargo.toml -p easydict_msix_validate --",
        "Expand-Archive -Path $msixPath",
        "UI automation should validate before extracting the MSIX for registration",
    );
    assert_order(
        install_step,
        "cargo run --manifest-path rs/Cargo.toml -p easydict_msix_validate --",
        "Add-AppxPackage -Register $manifestPath",
        "UI automation should validate before Add-AppxPackage",
    );
}

fn repo_root() -> PathBuf {
    let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    manifest_dir
        .parent()
        .and_then(Path::parent)
        .and_then(Path::parent)
        .unwrap_or_else(|| panic!("cannot resolve repo root from {}", manifest_dir.display()))
        .to_path_buf()
}

fn read_text(path: &Path) -> String {
    fs::read_to_string(path).unwrap_or_else(|error| panic!("read {}: {error}", path.display()))
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

fn assert_contains(haystack: &str, needle: &str, message: &str) {
    assert!(
        haystack.contains(needle),
        "{message}\nmissing marker: {needle}"
    );
}

fn assert_not_contains(haystack: &str, needle: &str, message: &str) {
    assert!(
        !haystack.contains(needle),
        "{message}\nforbidden marker: {needle}"
    );
}

fn assert_order(haystack: &str, first: &str, second: &str, message: &str) {
    let first_index = haystack
        .find(first)
        .unwrap_or_else(|| panic!("{message}\nmissing first marker: {first}"));
    let second_index = haystack
        .find(second)
        .unwrap_or_else(|| panic!("{message}\nmissing second marker: {second}"));

    assert!(
        first_index < second_index,
        "{message}\nexpected '{first}' before '{second}'"
    );
}
