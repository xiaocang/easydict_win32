using FluentAssertions;
using Easydict.WinUI.Services.Workers;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "Configuration")]
public sealed class WorkerPackagingTests
{
    private static readonly string ProjectRoot = FindProjectRoot();

    [Fact]
    public void ReleaseWorkflow_PublishesRemainingDotnetWorkersButNotOcrWorker()
    {
        var workflowPath = Path.GetFullPath(Path.Combine(
            ProjectRoot,
            "..",
            ".github",
            "workflows",
            "release-publish.yml"));
        var workflow = File.ReadAllText(workflowPath);

        workflow.Should().Contain("Easydict.Workers.LongDoc");
        workflow.Should().Contain("Easydict.Workers.LocalAi");
        workflow.Should().NotContain("Easydict.Workers.Ocr");
        workflow.Should().NotContain("Easydict.CompatHost");
        workflow.Should().NotContain("Publish .NET Compat Host");
        workflow.Should().Contain("runtime_profile:");
        workflow.Should().Contain("RUNTIME_PROFILE: ${{ github.event.inputs.runtime_profile || 'hybrid' }}");
        workflow.Should().Contain("if: env.RUNTIME_PROFILE != 'rust-only'");
        workflow.Should().Contain("Publish Rust helper executables");
        workflow.Should().Contain("Build-RustHelpers.ps1");
        workflow.Should().Contain("./publish/${{ matrix.platform }}/workers/longdoc");
        workflow.Should().Contain("./publish/${{ matrix.platform }}/workers/localai");
        workflow.Should().NotContain("./publish/${{ matrix.platform }}/workers/ocr");
        workflow.Should().Contain("./publish-msix/${{ matrix.platform }}/workers/longdoc");
        workflow.Should().Contain("./publish-msix/${{ matrix.platform }}/workers/localai");
        workflow.Should().NotContain("./publish-msix/${{ matrix.platform }}/workers/ocr");
        workflow.Should().Contain("--output ./publish/${{ matrix.platform }}");
        workflow.Should().Contain("--output ./publish-msix/${{ matrix.platform }}");
        workflow.Should().Contain("Dedupe-WorkerSharedFiles.ps1");
    }

    [Fact]
    public void ReleaseWorkflow_EnforcesMsixBundleSizeBudget()
    {
        var workflowPath = Path.GetFullPath(Path.Combine(
            ProjectRoot,
            "..",
            ".github",
            "workflows",
            "release-publish.yml"));
        var workflow = File.ReadAllText(workflowPath);

        workflow.Should().Contain("Create MSIX Bundle");
        workflow.Should().Contain("$bundleSize = (Get-Item $bundlePath).Length");
        workflow.Should().Contain("if ($bundleSize -ge 400000000)");
        workflow.Should().Contain("MSIX bundle is over the 400 MB budget");
    }

    [Fact]
    public void ReleaseWorkflow_UsesRustBundleMinVersionValidator()
    {
        var workflowPath = Path.GetFullPath(Path.Combine(
            ProjectRoot,
            "..",
            ".github",
            "workflows",
            "release-publish.yml"));
        var workflow = File.ReadAllText(workflowPath);

        workflow.Should().NotContain("Verify bundle MinVersion");
        workflow.Should().NotContain("Expand-Archive -Path $bundlePath");
        workflow.Should().NotContain("AppxBundleManifest.xml");
        workflow.Should().Contain("cargo run --manifest-path ..\\rs\\Cargo.toml -p easydict_msix_validate --");
        workflow.Should().Contain("verify-bundle-minversion");
        workflow.Should().Contain("easydict_msix_validate");
    }

    [Fact]
    public void ReleaseWorkflow_PublishesRustPortableOnlyBesideDotnetArtifacts()
    {
        var workflowPath = Path.GetFullPath(Path.Combine(
            ProjectRoot,
            "..",
            ".github",
            "workflows",
            "release-publish.yml"));
        var workflow = File.ReadAllText(workflowPath);

        workflow.Should().Contain("publish-rs-portable:");
        workflow.Should().Contain("Build Rust Portable Package");
        workflow.Should().Contain("./scripts/Package-Portable.ps1");
        workflow.Should().Contain("easydict-rs-portable-${{ github.ref_name }}-win-${{ matrix.platform }}.zip");
        workflow.Should().Contain("rs-portable/*.zip");
        workflow.Should().Contain("easydict_packager");
        workflow.Should().Contain("zip-directory");
        workflow.Should().Contain("--exclude-extension .pdb");
        workflow.Should().NotContain("Compress-Archive");
        workflow.Should().NotContain("easydict-rs-${{ github.ref_name }}-${{ matrix.platform }}.msix");
        workflow.Should().NotContain("Easydict.Rust.msix");
    }

    [Fact]
    public void MsixValidation_UsesRustToolInsteadOfDotnetProject()
    {
        var workflowPath = Path.GetFullPath(Path.Combine(
            ProjectRoot,
            "..",
            ".github",
            "workflows",
            "release-publish.yml"));
        var workflow = File.ReadAllText(workflowPath);
        var makefile = File.ReadAllText(Path.Combine(ProjectRoot, "Makefile"));

        workflow.Should().Contain("cargo run --manifest-path ../rs/Cargo.toml -p easydict_msix_validate");
        workflow.Should().Contain("--runtime-profile \"${{ env.RUNTIME_PROFILE }}\"");
        workflow.Should().NotContain("Verify MSIX worker-only longdoc payload");
        workflow.Should().NotContain("System.IO.Compression.ZipFile");
        workflow.Should().NotContain("Add-Type -AssemblyName System.IO.Compression.FileSystem");
        makefile.Should().Contain("cargo run --manifest-path ../rs/Cargo.toml -p easydict_msix_validate");
        makefile.Should().Contain("RUNTIME_PROFILE ?= hybrid");
        makefile.Should().Contain("--runtime-profile \"$(RUNTIME_PROFILE)\"");
        workflow.Should().NotContain("dotnet run --project tools/MsixValidate");
        makefile.Should().NotContain("dotnet run --project tools/MsixValidate");
        File.Exists(Path.Combine(ProjectRoot, "tools", "MsixValidate", "MsixValidate.csproj"))
            .Should().BeFalse();
    }

    [Fact]
    public void MsixValidation_RustPayloadValidatorOwnsLongDocRootPayloadChecks()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(ProjectRoot, ".."));
        var validator = File.ReadAllText(Path.Combine(
            repoRoot,
            "rs",
            "crates",
            "easydict_msix_validate",
            "src",
            "lib.rs"));

        validator.Should().Contain("PackagePayloadLayoutValidator");
        validator.Should().Contain("FORBIDDEN_ROOT_LONGDOC_PAYLOADS");
        validator.Should().Contain("Easydict.DocumentExport.dll".ToLowerInvariant());
        validator.Should().Contain("MuPDF.NET.dll".ToLowerInvariant());
        validator.Should().Contain("PdfSharpCore.dll".ToLowerInvariant());
        validator.Should().Contain("UglyToad.PdfPig.dll".ToLowerInvariant());
        validator.Should().Contain("SkiaSharp.dll".ToLowerInvariant());
    }

    [Fact]
    public void SecretEncryptionTool_UsesRustCliInsteadOfDotnetProject()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(ProjectRoot, ".."));
        var workspaceManifest = File.ReadAllText(Path.Combine(repoRoot, "rs", "Cargo.toml"));
        var makefile = File.ReadAllText(Path.Combine(ProjectRoot, "Makefile"));

        workspaceManifest.Should().Contain("crates/easydict_encrypt_secret");
        makefile.Should().Contain("cargo run --manifest-path ../rs/Cargo.toml -p easydict_encrypt_secret");
        makefile.Should().NotContain("dotnet run --project tools/EncryptSecret");
        File.Exists(Path.Combine(ProjectRoot, "tools", "EncryptSecret", "EncryptSecret.csproj"))
            .Should().BeFalse();
    }

    [Fact]
    public void PdfToImagesTool_UsesRustCliInsteadOfDotnetProject()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(ProjectRoot, ".."));
        var workspaceManifest = File.ReadAllText(Path.Combine(repoRoot, "rs", "Cargo.toml"));
        var makefile = File.ReadAllText(Path.Combine(ProjectRoot, "Makefile"));
        var script = File.ReadAllText(Path.Combine(ProjectRoot, "scripts", "pdf-to-images.ps1"));

        workspaceManifest.Should().Contain("crates/easydict_pdf_to_images");
        makefile.Should().Contain("pdf-to-images.ps1");
        script.Should().Contain("& cargo @arguments");
        script.Should().Contain("-p\", \"easydict_pdf_to_images");
        script.Should().NotContain("& dotnet @arguments");
        script.Should().NotContain("tools\\PdfToImages\\PdfToImages.csproj");
        makefile.Should().NotContain("dotnet run --project tools/PdfToImages");
        File.Exists(Path.Combine(ProjectRoot, "tools", "PdfToImages", "PdfToImages.csproj"))
            .Should().BeFalse();
    }

    [Fact]
    public void RustPortablePackageScript_StaysPortableOnlyAndCoexistsWithDotnetPackage()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(ProjectRoot, ".."));
        var workspaceManifest = File.ReadAllText(Path.Combine(repoRoot, "rs", "Cargo.toml"));
        var script = File.ReadAllText(Path.Combine(
            repoRoot,
            "rs",
            "scripts",
            "Package-Portable.ps1"));

        workspaceManifest.Should().Contain("crates/easydict_packager");
        script.Should().Contain("easydict-rs-portable");
        script.Should().Contain("Easydict.Rust.exe");
        script.Should().Contain("easydict_preview_iced.exe");
        script.Should().Contain("easydict_packager");
        script.Should().Contain("zip-directory");
        script.Should().Contain("README-portable.txt");
        script.Should().Contain("does not include MSIX metadata");
        script.Should().NotContain("Easydict.WinUI.exe");
        script.Should().NotContain("winapp package");
        script.Should().NotContain(".msix");
        script.Should().NotContain("dotnet publish");
        script.Should().NotContain("Compress-Archive");
        script.Should().NotContain("Easydict.Workers.LongDoc");
        script.Should().NotContain("Easydict.Workers.LocalAi");
        script.Should().NotContain("Extract-DotnetRuntime.ps1");
    }

    [Fact]
    public void StoreListingSync_UsesRustCliInsteadOfPowershellYaml()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(ProjectRoot, ".."));
        var workspaceManifest = File.ReadAllText(Path.Combine(repoRoot, "rs", "Cargo.toml"));
        var script = File.ReadAllText(Path.Combine(
            repoRoot,
            ".winstore",
            "scripts",
            "Sync-StoreListings.ps1"));
        var workflow = File.ReadAllText(Path.Combine(
            repoRoot,
            ".github",
            "workflows",
            "store-listings.yml"));
        var validator = File.ReadAllText(Path.Combine(
            repoRoot,
            "rs",
            "crates",
            "easydict_store_listings",
            "src",
            "lib.rs"));

        workspaceManifest.Should().Contain("crates/easydict_store_listings");
        script.Should().Contain("easydict_store_listings");
        script.Should().Contain("--winstore-root");
        script.Should().NotContain("powershell-yaml");
        script.Should().NotContain("ConvertFrom-Yaml");
        script.Should().NotContain("ConvertFrom-Json");
        script.Should().NotContain("ConvertTo-Json");
        script.Should().NotContain("Install-Module");
        workflow.Should().Contain("easydict_store_listings");
        workflow.Should().Contain("summary");
        workflow.Should().NotContain("powershell-yaml");
        workflow.Should().NotContain("ConvertFrom-Yaml");
        validator.Should().Contain("serde_saphyr::from_slice");
        validator.Should().Contain("FORBIDDEN_KEYWORD_NAMES");
        validator.Should().Contain("SUPPORTED_STORE_LANGUAGES");
    }

    [Fact]
    public void DotnetRuntimeExtraction_UsesRustPackagerInsteadOfPowershellDownloadAndArchive()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(ProjectRoot, ".."));
        var workspaceManifest = File.ReadAllText(Path.Combine(repoRoot, "rs", "Cargo.toml"));
        var script = File.ReadAllText(Path.Combine(ProjectRoot, "scripts", "Extract-DotnetRuntime.ps1"));
        var packager = File.ReadAllText(Path.Combine(
            repoRoot,
            "rs",
            "crates",
            "easydict_packager",
            "src",
            "lib.rs"));

        workspaceManifest.Should().Contain("crates/easydict_packager");
        script.Should().Contain("easydict_packager");
        script.Should().Contain("extract-dotnet-runtime");
        script.Should().Contain("--rid");
        script.Should().Contain("--output-dir");
        script.Should().NotContain("Invoke-WebRequest");
        script.Should().NotContain("Expand-Archive");
        script.Should().NotContain("Remove-Item");
        script.Should().NotContain("System.IO.Path");
        packager.Should().Contain("download_and_extract_dotnet_runtime");
        packager.Should().Contain("reqwest::blocking");
        packager.Should().Contain("ZipArchive");
        packager.Should().Contain("LICENSE.txt");
        packager.Should().Contain("ThirdPartyNotices.txt");
        packager.Should().Contain("shared").And.Contain("Microsoft.NETCore.App");
        packager.Should().Contain("host").And.Contain("fxr");
    }

    [Fact]
    public void BrowserExtensionPackaging_UsesRustPackagerInsteadOfPowershellJsonAndZip()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(ProjectRoot, ".."));
        var workspaceManifest = File.ReadAllText(Path.Combine(repoRoot, "rs", "Cargo.toml"));
        var script = File.ReadAllText(Path.Combine(
            repoRoot,
            "browser-extension",
            "scripts",
            "Package-Extension.ps1"));
        var packager = File.ReadAllText(Path.Combine(
            repoRoot,
            "rs",
            "crates",
            "easydict_packager",
            "src",
            "lib.rs"));
        var packagerCli = File.ReadAllText(Path.Combine(
            repoRoot,
            "rs",
            "crates",
            "easydict_packager",
            "src",
            "main.rs"));

        workspaceManifest.Should().Contain("crates/easydict_packager");
        script.Should().Contain("easydict_packager");
        script.Should().Contain("package-browser-extension");
        script.Should().Contain("--extension-dir");
        script.Should().Contain("--target");
        script.Should().NotContain("ConvertFrom-Json");
        script.Should().NotContain("ConvertTo-Json");
        script.Should().NotContain("System.IO.Compression.ZipFile");
        script.Should().NotContain("Add-Type");
        script.Should().NotContain("Copy-Item");
        script.Should().NotContain("Remove-Item");
        packager.Should().Contain("package_browser_extension");
        packager.Should().Contain("BROWSER_EXTENSION_COMMON_FILES");
        packager.Should().Contain("serde_json::to_vec_pretty");
        packager.Should().Contain("manifest_object.remove(\"key\")");
        packager.Should().Contain("manifest.v2.json");
        packager.Should().Contain("easydict-ocr-chrome-v{version}.zip");
        packager.Should().Contain("easydict-ocr-firefox-v{version}.xpi");
        packagerCli.Should().Contain("package-browser-extension");
        packagerCli.Should().Contain("--extension-dir");
    }

    [Fact]
    public void MsixPackageInputPreparation_UsesRustToolInsteadOfPowershellXmlDom()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(ProjectRoot, ".."));
        var packageScript = File.ReadAllText(Path.Combine(ProjectRoot, "scripts", "Package-Msix.ps1"));
        var packageAndInstallScript = File.ReadAllText(Path.Combine(ProjectRoot, "scripts", "package-and-install.ps1"));
        var makefile = File.ReadAllText(Path.Combine(ProjectRoot, "Makefile"));
        var msixValidator = File.ReadAllText(Path.Combine(
            repoRoot,
            "rs",
            "crates",
            "easydict_msix_validate",
            "src",
            "lib.rs"));
        var msixValidatorCli = File.ReadAllText(Path.Combine(
            repoRoot,
            "rs",
            "crates",
            "easydict_msix_validate",
            "src",
            "main.rs"));

        packageScript.Should().Contain("prepare-package-inputs");
        packageScript.Should().Contain("easydict_msix_validate");
        packageScript.Should().Contain("--output-manifest");
        packageScript.Should().Contain("winapp package");
        packageScript.Should().Contain("Fix-MsixMinVersion.ps1");
        packageScript.Should().NotContain("[xml]");
        packageScript.Should().NotContain("System.Xml");
        packageScript.Should().NotContain("XmlWriterSettings");
        packageScript.Should().NotContain("Get-ChildItem (Join-Path $PublishDir \"Assets\")");
        packageScript.Should().NotContain("Copy-Item -Path $sourcePri");
        packageAndInstallScript.Should().Contain("Package-Msix.ps1");
        packageAndInstallScript.Should().Contain("-VerifyTargetsizeIcons");
        packageAndInstallScript.Should().NotContain("[xml]");
        packageAndInstallScript.Should().NotContain("GetTempFileName");
        packageAndInstallScript.Should().NotContain("ProcessorArchitecture=\"[^\"]*\"");
        packageAndInstallScript.Should().NotContain("Copy-Item $assemblyPri");
        makefile.Should().Contain("Package-Msix.ps1 -Platform x64");
        makefile.Should().Contain("Package-Msix.ps1 -Platform x86");
        makefile.Should().Contain("Package-Msix.ps1 -Platform arm64");
        makefile.Should().NotContain("sed -i");
        makefile.Should().NotContain("TMP_MANIFEST");
        msixValidator.Should().Contain("prepare_package_inputs");
        msixValidator.Should().Contain("REQUIRED_MSIX_ASSETS");
        msixValidator.Should().Contain("rewrite_identity_for_package");
        msixValidator.Should().Contain("ProcessorArchitecture");
        msixValidator.Should().Contain("MIN_TARGETSIZE_ICON_COUNT");
        msixValidatorCli.Should().Contain("prepare-package-inputs");
        msixValidatorCli.Should().Contain("--verify-targetsize-icons");
    }

    [Fact]
    public void UiParityAnalysis_UsesRustAnalyzerInsteadOfDotnetTool()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(ProjectRoot, ".."));
        var workspaceManifest = File.ReadAllText(Path.Combine(repoRoot, "rs", "Cargo.toml"));
        var script = File.ReadAllText(Path.Combine(ProjectRoot, "scripts", "ci", "Invoke-UiParityAnalysis.ps1"));
        var workflow = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "ui-automation.yml"));

        workspaceManifest.Should().Contain("crates/easydict_ui_parity_analyzer");
        script.Should().Contain("easydict_ui_parity_analyzer");
        script.Should().Contain("--manifest-path");
        script.Should().Contain("& cargo @selfTestArguments");
        script.Should().Contain("& cargo @arguments");
        script.Should().Contain("--self-test");
        script.Should().Contain("--manifest");
        script.Should().Contain("--score-gate");
        script.Should().Contain("--min-coverage");
        script.Should().Contain("--min-critical-coverage");
        script.Should().Contain("--fail-on-threshold");
        script.Should().Contain("--fail-on-critical-coverage-missing");
        script.Should().Contain("--require-manifest");
        script.Should().NotContain("dotnet run --project");
        script.Should().NotContain("UiParityAnalyzer.csproj");
        script.Should().NotContain("& dotnet @arguments");
        workflow.Should().Contain("Invoke-UiParityAnalysis.ps1");
        workflow.Should().Contain("-ScreenshotRoot");
        workflow.Should().Contain("-OutputDir");
        File.Exists(Path.Combine(ProjectRoot, "tools", "UiParityAnalyzer", "UiParityAnalyzer.csproj"))
            .Should().BeFalse();
    }

    [Fact]
    public void BuildTimeAppIconGeneration_UsesRustToolInsteadOfSystemDrawingScript()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(ProjectRoot, ".."));
        var workspaceManifest = File.ReadAllText(Path.Combine(repoRoot, "rs", "Cargo.toml"));
        var winuiProject = File.ReadAllText(Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.WinUI",
            "Easydict.WinUI.csproj"));

        workspaceManifest.Should().Contain("crates/easydict_icon_generator");
        winuiProject.Should().Contain("cargo run --manifest-path");
        winuiProject.Should().Contain("-p easydict_icon_generator");
        winuiProject.Should().Contain("--source-png");
        winuiProject.Should().Contain("--output-ico");
        winuiProject.Should().Contain("--output-tray-png");
        winuiProject.Should().NotContain("generate-app-icon-ico.ps1");
        winuiProject.Should().NotContain("System.Drawing");
        File.Exists(Path.Combine(ProjectRoot, "scripts", "generate-app-icon-ico.ps1"))
            .Should().BeFalse();
    }

    [Fact]
    public void AssetGenerationScripts_UseRustIconGeneratorInsteadOfSystemDrawing()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(ProjectRoot, ".."));
        var workspaceManifest = File.ReadAllText(Path.Combine(repoRoot, "rs", "Cargo.toml"));
        var scriptNames = new[]
        {
            "generate-windows-assets.ps1",
            "generate-assets-from-macos-icon.ps1",
            "convert-service-icons.ps1",
        };

        workspaceManifest.Should().Contain("crates/easydict_icon_generator");
        foreach (var scriptName in scriptNames)
        {
            var script = File.ReadAllText(Path.Combine(ProjectRoot, "scripts", scriptName));

            script.Should().Contain("cargo run --manifest-path");
            script.Should().Contain("-p easydict_icon_generator");
            script.Should().NotContain("System.Drawing");
            script.Should().NotContain("Add-Type -AssemblyName");
        }
    }

    [Fact]
    public void SidecarE2E_UsesRustTestInsteadOfDotnetConsoleProjects()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(ProjectRoot, ".."));
        var solution = File.ReadAllText(Path.Combine(ProjectRoot, "Easydict.Win32.sln"));
        var migrationList = File.ReadAllText(Path.Combine(repoRoot, "migration-list.md"));
        var rustReadme = File.ReadAllText(Path.Combine(repoRoot, "rs", "README.md"));
        var rustSidecarTest = Path.Combine(
            repoRoot,
            "rs",
            "crates",
            "easydict_app",
            "tests",
            "sidecar_ipc_e2e.rs");

        solution.Should().NotContain("E2E.SidecarClient");
        solution.Should().NotContain("e2e\\E2E.SidecarClient.csproj");
        migrationList.Should().Contain("cargo test -p easydict_app --test sidecar_ipc_e2e");
        migrationList.Should().NotContain("dotnet run --project e2e/E2E.SidecarClient.csproj");
        rustReadme.Should().Contain("sidecar_ipc_e2e");
        File.Exists(rustSidecarTest).Should().BeTrue();
        File.Exists(Path.Combine(ProjectRoot, "e2e", "E2E.SidecarClient.csproj"))
            .Should().BeFalse();
        File.Exists(Path.Combine(
            ProjectRoot,
            "Easydict.SidecarClient.E2E",
            "Easydict.SidecarClient.E2E.csproj")).Should().BeFalse();
        File.Exists(Path.Combine(
            ProjectRoot,
            "Easydict.SidecarClient",
            "Easydict.SidecarClient.csproj")).Should().BeFalse();
        File.Exists(Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.SidecarClient",
            "Easydict.SidecarClient.csproj")).Should().BeTrue();
    }

    [Fact]
    public void Makefile_PublishesRemainingDotnetWorkersButNotOcrWorker()
    {
        var makefilePath = Path.Combine(ProjectRoot, "Makefile");
        var makefile = File.ReadAllText(makefilePath);
        var winuiProject = File.ReadAllText(Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.WinUI",
            "Easydict.WinUI.csproj"));
        var solution = File.ReadAllText(Path.Combine(ProjectRoot, "Easydict.Win32.sln"));
        var dedupeScript = File.ReadAllText(Path.Combine(ProjectRoot, "scripts", "Dedupe-WorkerSharedFiles.ps1"));
        var repoRoot = Path.GetFullPath(Path.Combine(ProjectRoot, ".."));
        var msixValidator = File.ReadAllText(Path.Combine(
            repoRoot,
            "rs",
            "crates",
            "easydict_msix_validate",
            "src",
            "lib.rs"));

        makefile.Should().Contain("Easydict.Workers.LongDoc");
        makefile.Should().Contain("Easydict.Workers.LocalAi");
        makefile.Should().Contain("RUNTIME_PROFILE ?= hybrid");
        makefile.Should().Contain("-p:RuntimeProfile=$(RUNTIME_PROFILE)");
        makefile.Should().Contain("if [ \"$(RUNTIME_PROFILE)\" != \"rust-only\" ]");
        makefile.Should().Contain("Skipping retained .NET workers and bundled worker runtime for RustOnly runtime profile.");
        makefile.Should().NotContain("Easydict.Workers.Ocr");
        solution.Should().NotContain("Easydict.Workers.Ocr");
        File.Exists(Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.Workers.Ocr",
            "Easydict.Workers.Ocr.csproj")).Should().BeFalse();
        File.Exists(Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.WinUI",
            "Services",
            "Workers",
            "OcrWorkerClient.cs")).Should().BeFalse();
        winuiProject.Should().NotContain("Easydict.Workers.Ocr");
        winuiProject.Should().NotContain("OcrWorkerClient");
        winuiProject.Should().NotContain("workers\\ocr");
        dedupeScript.Should().Contain("dedupe-worker-shared");
        dedupeScript.Should().Contain("easydict_msix_validate");
        msixValidator.Should().Contain("WORKER_SHARED_DIRS");
        msixValidator.Should().Contain("const WORKER_SHARED_DIRS: &[&str] = &[\"longdoc\", \"localai\"];");
        makefile.Should().NotContain("Easydict.CompatHost");
        makefile.Should().NotContain("Easydict.CompatHost.exe");
        makefile.Should().Contain("Build-RustHelpers.ps1");
        makefile.Should().Contain("easydict_browser_registrar.exe");
        makefile.Should().Contain("BrowserHostRegistrar.exe");
        makefile.Should().Contain("easydict-native-bridge.exe");
        makefile.Should().NotContain("src/Easydict.NativeBridge/Easydict.NativeBridge.csproj");
        makefile.Should().NotContain("src/Easydict.BrowserRegistrar/Easydict.BrowserRegistrar.csproj");
        makefile.Should().Contain("./publish/x64/workers/longdoc");
        makefile.Should().Contain("./publish/x64/workers/localai");
        makefile.Should().Contain("./publish/arm64/workers/longdoc");
        makefile.Should().Contain("./publish/arm64/workers/localai");
        makefile.Should().NotContain("./publish/x64/workers/ocr");
        makefile.Should().NotContain("./publish/arm64/workers/ocr");
        makefile.Should().Contain("./publish-msix/x64/workers/longdoc");
        makefile.Should().Contain("./publish-msix/x64/workers/localai");
        makefile.Should().Contain("./publish-msix/arm64/workers/longdoc");
        makefile.Should().Contain("./publish-msix/arm64/workers/localai");
        makefile.Should().NotContain("./publish-msix/x64/workers/ocr");
        makefile.Should().NotContain("./publish-msix/arm64/workers/ocr");
        makefile.Should().Contain("./publish-msix/x64 -p:BuildWorkerOutputs=false");
        makefile.Should().Contain("./publish-msix/arm64 -p:BuildWorkerOutputs=false");
        makefile.Should().Contain("Package-Msix.ps1 -Platform x64");
        makefile.Should().NotContain("winapp package ./publish-msix/x64");
        makefile.Should().NotContain("<Identity[^>]* Version=");
        makefile.Should().Contain("Dedupe-WorkerSharedFiles.ps1");
        makefile.Should().Contain("Worker settings default");
        makefile.Should().NotContain("UseLocalAiWorker default false");
    }

    [Fact]
    public void BrowserNativeMessagingDotnetProjects_AreRemovedFromSolutionAndSource()
    {
        var solution = File.ReadAllText(Path.Combine(ProjectRoot, "Easydict.Win32.sln"));

        solution.Should().NotContain("Easydict.NativeBridge");
        solution.Should().NotContain("Easydict.BrowserRegistrar");
        solution.Should().NotContain("Easydict.BrowserRegistrar.Tests");

        File.Exists(Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.NativeBridge",
            "Easydict.NativeBridge.csproj")).Should().BeFalse();
        File.Exists(Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.BrowserRegistrar",
            "Easydict.BrowserRegistrar.csproj")).Should().BeFalse();
        File.Exists(Path.Combine(
            ProjectRoot,
            "tests",
            "Easydict.BrowserRegistrar.Tests",
            "Easydict.BrowserRegistrar.Tests.csproj")).Should().BeFalse();
    }

    [Fact]
    public void CompatHostProjectAndPackaging_AreRemovedAfterNativeMdxMigration()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(ProjectRoot, ".."));
        var solution = File.ReadAllText(Path.Combine(ProjectRoot, "Easydict.Win32.sln"));
        var testProject = File.ReadAllText(Path.Combine(
            ProjectRoot,
            "tests",
            "Easydict.WinUI.Tests",
            "Easydict.WinUI.Tests.csproj"));
        var makefile = File.ReadAllText(Path.Combine(ProjectRoot, "Makefile"));
        var publishScript = File.ReadAllText(Path.Combine(ProjectRoot, "scripts", "publish.ps1"));
        var packageScript = File.ReadAllText(Path.Combine(ProjectRoot, "scripts", "package-and-install.ps1"));
        var releaseWorkflow = File.ReadAllText(Path.Combine(
            repoRoot,
            ".github",
            "workflows",
            "release-publish.yml"));
        var arm64SmokeWorkflow = File.ReadAllText(Path.Combine(
            repoRoot,
            ".github",
            "workflows",
            "arm64-msix-smoke.yml"));

        solution.Should().NotContain("Easydict.CompatHost");
        testProject.Should().NotContain("Easydict.CompatHost");
        makefile.Should().NotContain("Easydict.CompatHost");
        publishScript.Should().NotContain("Easydict.CompatHost");
        packageScript.Should().NotContain("Easydict.CompatHost");
        releaseWorkflow.Should().NotContain("Easydict.CompatHost");
        arm64SmokeWorkflow.Should().NotContain("Easydict.CompatHost");

        File.Exists(Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.CompatHost",
            "Easydict.CompatHost.csproj")).Should().BeFalse();
        File.Exists(Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.SidecarClient",
            "Protocol",
            "CompatHostProtocol.cs")).Should().BeFalse();
    }

    [Fact]
    public void RustHelperPackaging_PublishesBridgeRegistrarAndCliBesideAppExecutable()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(ProjectRoot, ".."));
        var buildScript = File.ReadAllText(Path.Combine(ProjectRoot, "scripts", "Build-RustHelpers.ps1"));
        var makefile = File.ReadAllText(Path.Combine(ProjectRoot, "Makefile"));
        var publishScript = File.ReadAllText(Path.Combine(ProjectRoot, "scripts", "publish.ps1"));
        var packageScript = File.ReadAllText(Path.Combine(ProjectRoot, "scripts", "package-and-install.ps1"));
        var workflow = File.ReadAllText(Path.GetFullPath(Path.Combine(
            ProjectRoot,
            "..",
            ".github",
            "workflows",
            "release-publish.yml")));
        var arm64SmokeWorkflow = File.ReadAllText(Path.GetFullPath(Path.Combine(
            ProjectRoot,
            "..",
            ".github",
            "workflows",
            "arm64-msix-smoke.yml")));
        var appLib = File.ReadAllText(Path.Combine(
            repoRoot,
            "rs",
            "crates",
            "easydict_app",
            "src",
            "lib.rs"));
        var packager = File.ReadAllText(Path.Combine(
            repoRoot,
            "rs",
            "crates",
            "easydict_packager",
            "src",
            "lib.rs"));
        var packagerCli = File.ReadAllText(Path.Combine(
            repoRoot,
            "rs",
            "crates",
            "easydict_packager",
            "src",
            "main.rs"));

        buildScript.Should().Contain("cargo");
        buildScript.Should().Contain("easydict_packager");
        buildScript.Should().Contain("build-rust-helpers");
        buildScript.Should().Contain("--workspace");
        buildScript.Should().Contain("--output-dir");
        buildScript.Should().NotContain("--bin");
        buildScript.Should().NotContain("Copy-Item");
        buildScript.Should().NotContain("System.IO.Path");
        packager.Should().Contain("build_rust_helpers");
        packager.Should().Contain("RUST_HELPER_EXECUTABLES");
        packager.Should().Contain("easydict-native-bridge.exe");
        packager.Should().Contain("easydict_browser_registrar.exe");
        packager.Should().Contain("BrowserHostRegistrar.exe");
        packager.Should().Contain("easydict_cli.exe");
        packager.Should().Contain("easydict_long_doc.exe");
        packager.Should().Contain("x86_64-pc-windows-msvc");
        packager.Should().Contain("i686-pc-windows-msvc");
        packager.Should().Contain("aarch64-pc-windows-msvc");
        packagerCli.Should().Contain("build-rust-helpers");

        makefile.Should().Contain("Build-RustHelpers.ps1 -Platform x64 -Configuration Release -OutputDir ./publish/x64");
        makefile.Should().Contain("Build-RustHelpers.ps1 -Platform x86 -Configuration Release -OutputDir ./publish/x86");
        makefile.Should().Contain("Build-RustHelpers.ps1 -Platform arm64 -Configuration Release -OutputDir ./publish/arm64");
        makefile.Should().Contain("Build-RustHelpers.ps1 -Platform x64 -Configuration Release -OutputDir ./publish-msix/x64");
        makefile.Should().Contain("Build-RustHelpers.ps1 -Platform x86 -Configuration Release -OutputDir ./publish-msix/x86");
        makefile.Should().Contain("Build-RustHelpers.ps1 -Platform arm64 -Configuration Release -OutputDir ./publish-msix/arm64");

        publishScript.Should().Contain("Build-RustHelpers.ps1");
        publishScript.Should().Contain("BuildWorkerOutputs=false");
        publishScript.Should().Contain("-p:RuntimeProfile=$RuntimeProfile");
        publishScript.Should().Contain("[ValidateSet(\"Hybrid\", \"RustOnly\")]");
        publishScript.Should().Contain("Skipping retained .NET workers for RustOnly runtime profile.");
        publishScript.Should().Contain("easydict_packager");
        publishScript.Should().Contain("zip-directory");
        publishScript.Should().NotContain("Compress-Archive");
        publishScript.Should().Contain("Easydict.Workers.LongDoc");
        publishScript.Should().Contain("Easydict.Workers.LocalAi");
        publishScript.Should().NotContain("Easydict.Workers.Ocr");
        publishScript.Should().NotContain("Easydict.NativeBridge.csproj");
        publishScript.Should().NotContain("Easydict.BrowserRegistrar.csproj");
        packageScript.Should().Contain("Build-RustHelpers.ps1");
        packageScript.Should().Contain("BuildWorkerOutputs=false");
        packageScript.Should().Contain("-p:RuntimeProfile=$RuntimeProfile");
        packageScript.Should().Contain("[ValidateSet(\"Hybrid\", \"RustOnly\")]");
        packageScript.Should().Contain("Skipping retained .NET workers and bundled worker runtime for RustOnly profile.");
        packageScript.Should().Contain("easydict_msix_validate");
        packageScript.Should().Contain("--runtime-profile $validatorRuntimeProfile");
        packageScript.Should().Contain("Easydict.Workers.LongDoc");
        packageScript.Should().Contain("Easydict.Workers.LocalAi");
        packageScript.Should().NotContain("Easydict.Workers.Ocr");
        packageScript.Should().Contain("Extract-DotnetRuntime.ps1");
        packageScript.Should().NotContain("Easydict.NativeBridge.csproj");
        packageScript.Should().NotContain("Easydict.BrowserRegistrar.csproj");
        workflow.Should().Contain("Publish Rust helper executables");
        workflow.Should().Contain("Publish Rust helper executables (MSIX)");
        workflow.Should().NotContain("Easydict.NativeBridge.csproj");
        workflow.Should().NotContain("Easydict.BrowserRegistrar.csproj");
        arm64SmokeWorkflow.Should().Contain("Publish Rust helper executables (arm64)");
        arm64SmokeWorkflow.Should().Contain("runtime_profile:");
        arm64SmokeWorkflow.Should().Contain("RUNTIME_PROFILE: ${{ github.event.inputs.runtime_profile || 'hybrid' }}");
        workflow.Should().Contain("-p:RuntimeProfile=${{ env.RUNTIME_PROFILE }}");
        arm64SmokeWorkflow.Should().Contain("-p:RuntimeProfile=${{ env.RUNTIME_PROFILE }}");
        arm64SmokeWorkflow.Should().Contain("if: env.RUNTIME_PROFILE != 'rust-only'");
        arm64SmokeWorkflow.Should().Contain("easydict_msix_validate");
        arm64SmokeWorkflow.Should().Contain("--runtime-profile \"${{ env.RUNTIME_PROFILE }}\"");
        arm64SmokeWorkflow.Should().Contain("Dedupe-WorkerSharedFiles.ps1");
        arm64SmokeWorkflow.Should().Contain("-PublishDir ./publish-msix/arm64");
        arm64SmokeWorkflow.Should().NotContain("workers/ocr");
        arm64SmokeWorkflow.Should().NotContain("Easydict.NativeBridge.csproj");
        arm64SmokeWorkflow.Should().NotContain("Easydict.BrowserRegistrar.csproj");
        appLib.Should().Contain("pub const BROWSER_REGISTRAR_EXE: &str = \"easydict_browser_registrar.exe\";");
    }

    [Fact]
    public void OpenVinoNativeRuntime_IsNotPublishedWithLocalAiWorker()
    {
        var csprojPath = Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.OpenVINO",
            "Easydict.OpenVINO.csproj");
        var csproj = File.ReadAllText(csprojPath);

        csproj.Should().Contain("Intel.ML.OnnxRuntime.OpenVino");
        csproj.Should().Contain("<ExcludeAssets>runtime;native</ExcludeAssets>");
    }

    [Fact]
    public void LocalAiWorker_UsesCpuOnnxRuntimeAndExcludesOpenVinoEpNativeByDefault()
    {
        var csprojPath = Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.Workers.LocalAi",
            "Easydict.Workers.LocalAi.csproj");
        var csproj = File.ReadAllText(csprojPath);

        csproj.Should().Contain("Microsoft.ML.OnnxRuntime\" Version=\"1.21.0");
        csproj.Should().Contain("intel.ml.onnxruntime.openvino");
        csproj.Should().Contain("microsoft.windows.ai.machinelearning");
    }

    [Fact]
    public void LocalAiWorker_DoesNotInjectOpenVinoNativePathByDefault()
    {
        var spawnerPath = Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.WinUI",
            "Services",
            "Workers",
            "WorkerSpawner.cs");
        var spawner = File.ReadAllText(spawnerPath);

        spawner.Should().Contain("IsOpenVinoEpPathInjectionEnabled()");
        spawner.Should().Contain("EASYDICT_OPENVINO_RUNTIME_DIR");
        spawner.Should().NotContain("openVinoRuntimeDir + Path.PathSeparator + existingPath;\r\n        }");
    }

    [Fact]
    public void LocalAiWorker_ExposesOnlyStreamingTerminalTranslationMethod()
    {
        var programPath = Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.Workers.LocalAi",
            "Program.cs");
        var protocolPath = Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.SidecarClient",
            "Protocol",
            "LocalAiProtocol.cs");

        var program = File.ReadAllText(programPath);
        var protocol = File.ReadAllText(protocolPath);

        program.Should().NotContain("new TranslateHandler");
        program.Should().NotContain("LocalAiMethods.Translate,");
        program.Should().NotContain("LocalAiMethods.PrepareModel");
        program.Should().NotContain("LocalAiMethods.IsAvailable");
        program.Should().NotContain("LocalAiMethods.ListModels");
        program.Should().Contain("LocalAiMethods.TranslateStream");
        protocol.Should().NotContain("public const string Translate = \"translate\"");
        protocol.Should().NotContain("PrepareModel");
        protocol.Should().NotContain("IsAvailable");
        protocol.Should().NotContain("ListModels");
        protocol.Should().NotContain("LocalAiTranslateResult");
        protocol.Should().Contain("public const string TranslateStream = \"translate_stream\"");
    }

    [Fact]
    public void WorkerSharedDedupeScript_MovesOnlyAllowlistedIdenticalDlls()
    {
        var scriptPath = Path.Combine(ProjectRoot, "scripts", "Dedupe-WorkerSharedFiles.ps1");
        var script = File.ReadAllText(scriptPath);
        var repoRoot = Path.GetFullPath(Path.Combine(ProjectRoot, ".."));
        var msixValidator = File.ReadAllText(Path.Combine(
            repoRoot,
            "rs",
            "crates",
            "easydict_msix_validate",
            "src",
            "lib.rs"));
        var resolverPath = Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.SidecarClient",
            "WorkerSharedAssemblyResolver.cs");
        var resolver = File.ReadAllText(resolverPath);

        foreach (var assemblyName in new[]
        {
            "Microsoft.Windows.SDK.NET",
            "WinRT.Runtime",
            "Microsoft.Windows.UI.Xaml",
            "Microsoft.WinUI",
            "Microsoft.InteractiveExperiences.Projection",
            "Microsoft.Web.WebView2.Core.Projection",
        })
        {
            msixValidator.Should().Contain($"\"{assemblyName}.dll\"");
            resolver.Should().Contain($"\"{assemblyName}\"");
        }

        script.Should().Contain("cargo run --manifest-path $cargoManifest -p easydict_msix_validate");
        script.Should().Contain("dedupe-worker-shared");
        script.Should().NotContain("Get-FileHash");
        script.Should().NotContain("Remove-Item");
        msixValidator.Should().Contain("dedupe_worker_shared_files");
        msixValidator.Should().Contain("WORKER_SHARED_ALLOWLIST");
        msixValidator.Should().Contain("sha256_lower");
        msixValidator.Should().Contain("workers");
        msixValidator.Should().Contain("shared");
        resolver.Should().Contain("EASYDICT_WORKER_SHARED_DIR");
        resolver.Should().Contain("AssemblyLoadContext.Default.Resolving");
    }

    [Fact]
    public void ReleaseWinUIBuild_UsesWorkerOnlyLongDocPackaging()
    {
        var csprojPath = Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.WinUI",
            "Easydict.WinUI.csproj");
        var csproj = File.ReadAllText(csprojPath);

        csproj.Should().Contain("<EnableInProcLongDocFallback Condition=\"'$(EnableInProcLongDocFallback)' == '' and '$(Configuration)' == 'Release'\">false</EnableInProcLongDocFallback>");
        csproj.Should().Contain("<Compile Remove=\"Services\\LongDocumentTranslationService.cs\" />");
        csproj.Should().Contain("Services\\LongDocumentTranslationService.WorkerOnly.cs");
        csproj.Should().Contain("Condition=\"'$(EnableInProcLongDocFallback)' == 'true'\"");
    }

    [Fact]
    public void RustOnlyWinUIBuild_DisablesRetainedWorkersAndInProcLongDocFallback()
    {
        var csprojPath = Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.WinUI",
            "Easydict.WinUI.csproj");
        var csproj = File.ReadAllText(csprojPath);

        csproj.Should().Contain("<RuntimeProfile Condition=\"'$(RuntimeProfile)' == ''\">Hybrid</RuntimeProfile>");
        csproj.Should().Contain("<IsRustOnlyRuntimeProfile Condition=\"'$(RuntimeProfile)' == 'RustOnly' or '$(RuntimeProfile)' == 'rust-only' or '$(RuntimeProfile)' == 'rustonly' or '$(RuntimeProfile)' == 'rust_only'\">true</IsRustOnlyRuntimeProfile>");
        csproj.Should().Contain("<BuildWorkerOutputs Condition=\"'$(IsRustOnlyRuntimeProfile)' == 'true'\">false</BuildWorkerOutputs>");
        csproj.Should().Contain("<EnableInProcLongDocFallback Condition=\"'$(IsRustOnlyRuntimeProfile)' == 'true'\">false</EnableInProcLongDocFallback>");
    }

    [Fact]
    public void WorkerOnlyLongDocBuild_DoesNotReferenceHostMuPdfPipeline()
    {
        var csprojPath = Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.WinUI",
            "Easydict.WinUI.csproj");
        var csproj = File.ReadAllText(csprojPath);

        csproj.Should().Contain("<ProjectReference Include=\"..\\Easydict.DocumentExport\\Easydict.DocumentExport.csproj\"");
        csproj.Should().Contain("<PackageReference Include=\"MuPDF.NET\" Version=\"3.2.12\"");
        csproj.Should().Contain("<ProjectReference Include=\"..\\..\\..\\lib\\PdfPig\\src\\UglyToad.PdfPig\\UglyToad.PdfPig.csproj\"");
        csproj.Should().Contain("<PackageReference Include=\"Microsoft.ML.OnnxRuntime.Managed\" Version=\"1.21.0\"");
        csproj.Should().Contain("Condition=\"'$(EnableInProcLongDocFallback)' == 'true'\"");
    }

    [Fact]
    public void WorkerSpawner_DoesNotPinDotnetRootWhenBundledRuntimeIsMissing()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "easydict-worker-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var variables = WorkerSpawner.BuildEnvironmentVariables("longdoc", baseDir);

            variables.Should().NotContainKey("DOTNET_ROOT");
            variables.Should().NotContainKey("DOTNET_ROOT_X64");
            variables.Should().NotContainKey("DOTNET_ROOT_ARM64");
            variables.Should().ContainKey("EASYDICT_WORKER_SHARED_DIR");
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void WorkerSpawner_PinsDotnetRootWhenBundledRuntimeExists()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "easydict-worker-env-" + Guid.NewGuid().ToString("N"));
        var dotnetRoot = Path.Combine(baseDir, "dotnet");
        Directory.CreateDirectory(Path.Combine(dotnetRoot, "host", "fxr", "8.0.11"));
        Directory.CreateDirectory(Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App", "8.0.11"));
        try
        {
            var variables = WorkerSpawner.BuildEnvironmentVariables("longdoc", baseDir);

            variables["DOTNET_ROOT"].Should().Be(dotnetRoot);
            variables["DOTNET_ROOT_X64"].Should().Be(dotnetRoot);
            variables["DOTNET_ROOT_ARM64"].Should().Be(dotnetRoot);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    private static string FindProjectRoot()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var solutionPath = Path.Combine(current, "Easydict.Win32.sln");
            if (File.Exists(solutionPath))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..");
    }
}
