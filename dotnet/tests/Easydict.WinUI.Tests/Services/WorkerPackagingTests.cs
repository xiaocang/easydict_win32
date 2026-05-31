using FluentAssertions;
using Easydict.WinUI.Services.Workers;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "Configuration")]
public sealed class WorkerPackagingTests
{
    private static readonly string ProjectRoot = FindProjectRoot();

    [Fact]
    public void ReleaseWorkflow_PublishesAllDefaultEnabledWorkers()
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
        workflow.Should().Contain("Easydict.Workers.Ocr");
        workflow.Should().Contain("Easydict.CompatHost");
        workflow.Should().Contain("Publish .NET Compat Host");
        workflow.Should().Contain("Publish Rust helper executables");
        workflow.Should().Contain("Build-RustHelpers.ps1");
        workflow.Should().Contain("./publish/${{ matrix.platform }}/workers/ocr");
        workflow.Should().Contain("./publish-msix/${{ matrix.platform }}/workers/ocr");
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
    public void Makefile_PublishesAllDefaultEnabledWorkers()
    {
        var makefilePath = Path.Combine(ProjectRoot, "Makefile");
        var makefile = File.ReadAllText(makefilePath);

        makefile.Should().Contain("Easydict.Workers.LongDoc");
        makefile.Should().Contain("Easydict.Workers.LocalAi");
        makefile.Should().Contain("Easydict.Workers.Ocr");
        makefile.Should().Contain("Easydict.CompatHost");
        makefile.Should().Contain("Easydict.CompatHost.exe");
        makefile.Should().Contain("Build-RustHelpers.ps1");
        makefile.Should().Contain("easydict_browser_registrar.exe");
        makefile.Should().Contain("easydict-native-bridge.exe");
        makefile.Should().Contain("./publish/x64/workers/ocr");
        makefile.Should().Contain("./publish/arm64/workers/ocr");
        makefile.Should().Contain("./publish-msix/x64/workers/ocr");
        makefile.Should().Contain("./publish-msix/arm64/workers/ocr");
        makefile.Should().Contain("./publish-msix/x64 -p:BuildWorkerOutputs=false");
        makefile.Should().Contain("./publish-msix/arm64 -p:BuildWorkerOutputs=false");
        makefile.Should().Contain("winapp package ./publish-msix/x64");
        makefile.Should().Contain("<Identity[^>]* Version=");
        makefile.Should().Contain("Dedupe-WorkerSharedFiles.ps1");
        makefile.Should().Contain("Worker settings default");
        makefile.Should().NotContain("UseLocalAiWorker default false");
    }

    [Fact]
    public void CompatHostPackaging_PublishesBridgeBesideAppExecutable()
    {
        var makefile = File.ReadAllText(Path.Combine(ProjectRoot, "Makefile"));
        var publishScript = File.ReadAllText(Path.Combine(ProjectRoot, "scripts", "publish.ps1"));
        var packageScript = File.ReadAllText(Path.Combine(ProjectRoot, "scripts", "package-and-install.ps1"));
        var workflow = File.ReadAllText(Path.GetFullPath(Path.Combine(
            ProjectRoot,
            "..",
            ".github",
            "workflows",
            "release-publish.yml")));
        var csproj = File.ReadAllText(Path.Combine(
            ProjectRoot,
            "src",
            "Easydict.CompatHost",
            "Easydict.CompatHost.csproj"));

        makefile.Should().Contain("AppContext.BaseDirectory +");
        makefile.Should().Contain("dotnet publish src/Easydict.CompatHost/Easydict.CompatHost.csproj --configuration Release --runtime win-x64 --self-contained true --output ./publish/x64");
        makefile.Should().Contain("dotnet publish src/Easydict.CompatHost/Easydict.CompatHost.csproj --configuration Release --runtime win-x86 --self-contained true --output ./publish/x86");
        makefile.Should().Contain("dotnet publish src/Easydict.CompatHost/Easydict.CompatHost.csproj --configuration Release --runtime win-arm64 --self-contained true --output ./publish/arm64");
        makefile.Should().Contain("dotnet publish src/Easydict.CompatHost/Easydict.CompatHost.csproj --configuration Release --runtime win-x64 --self-contained true --output ./publish-msix/x64");
        makefile.Should().Contain("dotnet publish src/Easydict.CompatHost/Easydict.CompatHost.csproj --configuration Release --runtime win-x86 --self-contained true --output ./publish-msix/x86");
        makefile.Should().Contain("dotnet publish src/Easydict.CompatHost/Easydict.CompatHost.csproj --configuration Release --runtime win-arm64 --self-contained true --output ./publish-msix/arm64");
        publishScript.Should().Contain("Easydict.CompatHost.exe");
        packageScript.Should().Contain("Easydict.CompatHost/Easydict.CompatHost.csproj");
        workflow.Should().Contain("Publish .NET Compat Host (MSIX)");
        csproj.Should().Contain("<RuntimeIdentifiers>win-x64;win-x86;win-arm64</RuntimeIdentifiers>");
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

        buildScript.Should().Contain("cargo");
        buildScript.Should().Contain("--bin\", \"easydict-native-bridge");
        buildScript.Should().Contain("--bin\", \"easydict_browser_registrar");
        buildScript.Should().Contain("--bin\", \"easydict_cli");
        buildScript.Should().Contain("x86_64-pc-windows-msvc");
        buildScript.Should().Contain("i686-pc-windows-msvc");
        buildScript.Should().Contain("aarch64-pc-windows-msvc");
        buildScript.Should().Contain("easydict-native-bridge.exe");
        buildScript.Should().Contain("easydict_browser_registrar.exe");
        buildScript.Should().Contain("easydict_cli.exe");

        makefile.Should().Contain("Build-RustHelpers.ps1 -Platform x64 -Configuration Release -OutputDir ./publish/x64");
        makefile.Should().Contain("Build-RustHelpers.ps1 -Platform x86 -Configuration Release -OutputDir ./publish/x86");
        makefile.Should().Contain("Build-RustHelpers.ps1 -Platform arm64 -Configuration Release -OutputDir ./publish/arm64");
        makefile.Should().Contain("Build-RustHelpers.ps1 -Platform x64 -Configuration Release -OutputDir ./publish-msix/x64");
        makefile.Should().Contain("Build-RustHelpers.ps1 -Platform x86 -Configuration Release -OutputDir ./publish-msix/x86");
        makefile.Should().Contain("Build-RustHelpers.ps1 -Platform arm64 -Configuration Release -OutputDir ./publish-msix/arm64");

        publishScript.Should().Contain("Build-RustHelpers.ps1");
        packageScript.Should().Contain("Build-RustHelpers.ps1");
        workflow.Should().Contain("Publish Rust helper executables");
        workflow.Should().Contain("Publish Rust helper executables (MSIX)");
        arm64SmokeWorkflow.Should().Contain("Publish Rust helper executables (arm64)");
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
    public void WorkerSharedDedupeScript_MovesOnlyAllowlistedIdenticalDlls()
    {
        var scriptPath = Path.Combine(ProjectRoot, "scripts", "Dedupe-WorkerSharedFiles.ps1");
        var script = File.ReadAllText(scriptPath);

        script.Should().Contain("Microsoft.Windows.SDK.NET.dll");
        script.Should().Contain("Join-Path $PublishDir \"workers\"");
        script.Should().Contain("shared");
        script.Should().Contain("Get-FileHash");
        script.Should().Contain("Remove-Item");
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
