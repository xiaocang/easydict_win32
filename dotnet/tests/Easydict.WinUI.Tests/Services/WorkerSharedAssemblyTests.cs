using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Easydict.SidecarClient;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

[Trait("Category", "Configuration")]
public sealed class WorkerSharedAssemblyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "easydict-shared-" + Guid.NewGuid().ToString("N"));
    private const string Dll = "Microsoft.Windows.SDK.NET.dll";

    public WorkerSharedAssemblyTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("Microsoft.Windows.SDK.NET.dll")]
    [InlineData("WinRT.Runtime.dll")]
    [InlineData("Microsoft.Windows.UI.Xaml.dll")]
    [InlineData("Microsoft.WinUI.dll")]
    [InlineData("Microsoft.InteractiveExperiences.Projection.dll")]
    [InlineData("Microsoft.Web.WebView2.Core.Projection.dll")]
    public async Task Dedupe_ReusesIdenticalHostEvenWithOnlyOneWorker(string name)
    {
        Write(name, "same");
        Write("workers/ocr/" + name, "same");
        await DedupeAsync();
        File.ReadAllText(PathAt(name)).Should().Be("same");
        File.Exists(PathAt("workers/ocr/" + name)).Should().BeFalse();
        File.Exists(PathAt("workers/shared/" + name)).Should().BeFalse();
        await DedupeAsync();
        File.ReadAllText(PathAt(name)).Should().Be("same");
    }

    [Fact]
    public async Task Dedupe_KeepsLocalAiWinRtBootstrapBeforeModuleInitialization()
    {
        const string name = "WinRT.Runtime.dll";
        Write(name, "runtime");
        Write("workers/localai/" + name, "runtime");
        Write("workers/ocr/" + name, "runtime");
        await DedupeAsync();
        File.ReadAllText(PathAt("workers/localai/" + name)).Should().Be("runtime");
        File.Exists(PathAt("workers/ocr/" + name)).Should().BeFalse();
    }

    [Fact]
    public async Task Dedupe_RestoresLocalAiBootstrapFromEarlierSharedLayout()
    {
        const string name = "WinRT.Runtime.dll";
        Write(name, "runtime");
        Write("workers/shared/" + name, "runtime");
        Directory.CreateDirectory(PathAt("workers/localai"));
        await DedupeAsync();
        File.ReadAllText(PathAt("workers/localai/" + name)).Should().Be("runtime");
        File.Exists(PathAt("workers/shared/" + name)).Should().BeFalse();
        await DedupeAsync();
        File.ReadAllText(PathAt("workers/localai/" + name)).Should().Be("runtime");
    }

    [Fact]
    public async Task Dedupe_RemovesEarlierSharedOutputWhenHostIsIdentical()
    {
        Write(Dll, "same");
        Write("workers/shared/" + Dll, "same");
        Directory.CreateDirectory(PathAt("workers/ocr"));
        await DedupeAsync();
        File.Exists(PathAt("workers/shared/" + Dll)).Should().BeFalse();
        File.ReadAllText(PathAt(Dll)).Should().Be("same");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dedupe_PreservesWorkerSharingWhenHostIsMissingOrDifferent(bool differentHost)
    {
        if (differentHost) Write(Dll, "host");
        Write("workers/longdoc/" + Dll, "worker");
        Write("workers/ocr/" + Dll, "worker");
        await DedupeAsync();
        File.ReadAllText(PathAt("workers/shared/" + Dll)).Should().Be("worker");
        File.Exists(PathAt("workers/longdoc/" + Dll)).Should().BeFalse();
        File.Exists(PathAt("workers/ocr/" + Dll)).Should().BeFalse();
        // Publishing a worker again must not overwrite or delete the shared copy.
        Write("workers/ocr/" + Dll, "worker");
        await DedupeAsync();
        File.ReadAllText(PathAt("workers/shared/" + Dll)).Should().Be("worker");
        File.Exists(PathAt("workers/ocr/" + Dll)).Should().BeFalse();
        if (differentHost) File.ReadAllText(PathAt(Dll)).Should().Be("host");
    }

    [Fact]
    public async Task Dedupe_PreservesConflictingVersionsAndNonAllowlistedFiles()
    {
        Write(Dll, "version-a");
        Write("workers/longdoc/" + Dll, "version-a");
        Write("workers/ocr/" + Dll, "version-b");
        Write("workers/shared/" + Dll, "version-c");
        Write("onnxruntime.dll", "native");
        Write("workers/ocr/onnxruntime.dll", "native");
        var before = Directory.GetFiles(_directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllText);
        await DedupeAsync();
        foreach (var (path, content) in before) File.ReadAllText(path).Should().Be(content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolver_LoadsHostFallbackButPrefersWorkerSharedAssembly(bool sharedExists)
    {
        var context = VerifyResolverLoad(sharedExists);
        // Unload is asynchronous; release Windows DLL mappings before fixture cleanup.
        for (var attempt = 0; context.IsAlive && attempt < 10; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        context.IsAlive.Should().BeFalse();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference VerifyResolverLoad(bool sharedExists)
    {
        var shared = PathAt("workers/shared");
        Directory.CreateDirectory(shared);
        var source = Path.Combine(AppContext.BaseDirectory, Dll);
        File.Copy(source, PathAt(Dll));
        if (sharedExists) File.Copy(source, Path.Combine(shared, Dll));
        var context = new AssemblyLoadContext("worker-shared-test", isCollectible: true);
        try
        {
            var assembly = WorkerSharedAssemblyResolver.ResolveFromDirectories(
                context, AssemblyName.GetAssemblyName(source), shared, _directory);
            assembly.Should().NotBeNull();
            assembly!.Location.Should().Be(sharedExists ? Path.Combine(shared, Dll) : PathAt(Dll));
        }
        finally
        {
            context.Unload();
        }
        return new WeakReference(context);
    }

    [Fact]
    public void Resolver_IgnoresMissingAndNonAllowlistedAssemblies()
    {
        Write("NotAllowed.dll", "must not be loaded");
        WorkerSharedAssemblyResolver.ResolveFromDirectories(AssemblyLoadContext.Default,
            new AssemblyName("NotAllowed"), _directory, _directory).Should().BeNull();
        WorkerSharedAssemblyResolver.ResolveFromDirectories(AssemblyLoadContext.Default,
            new AssemblyName("Microsoft.Windows.SDK.NET"), _directory, _directory).Should().BeNull();
    }

    private async Task DedupeAsync()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Easydict.Win32.sln")))
            directory = directory.Parent;
        directory.Should().NotBeNull();
        var start = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-File",
                     Path.Combine(directory!.FullName, "scripts", "Dedupe-WorkerSharedFiles.ps1"),
                     "-PublishDir", _directory })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        process.ExitCode.Should().Be(0, "{0}\n{1}", await output, await error);
    }

    private string PathAt(string path) => Path.Combine(_directory, path.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string path, string contents)
    {
        var destination = PathAt(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, contents);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
