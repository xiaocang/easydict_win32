using Easydict.BobPlugin.Adapter;
using Easydict.BobPlugin.Package;
using Easydict.BobPlugin.Tests.Mocks;
using Easydict.TranslationService.Models;

namespace Easydict.BobPlugin.Tests;

/// <summary>
/// One fixture plugin, wired up the way an installed plugin is: the package directory is read
/// straight from the test output, and each fixture gets its own throwaway sandbox.
/// </summary>
internal sealed class PluginFixture : IDisposable
{
    private readonly List<IDisposable> _disposables = new();

    private PluginFixture(string name, BobPluginInstanceDescriptor descriptor, StubHttpMessageHandler handler, HttpClient httpClient)
    {
        Name = name;
        Descriptor = descriptor;
        Http = handler;
        HttpClient = httpClient;
        _disposables.Add(httpClient);
        _disposables.Add(handler);
    }

    public string Name { get; }

    public BobPluginInstanceDescriptor Descriptor { get; }

    public StubHttpMessageHandler Http { get; }

    public HttpClient HttpClient { get; }

    /// <summary>Directory of the extracted fixture plugin.</summary>
    public static string DirectoryFor(string name)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "BobPlugins", name);

    /// <summary>Load a fixture's package without starting an engine.</summary>
    public static BobPluginPackage Package(string name) => BobPluginPackage.LoadFromDirectory(DirectoryFor(name));

    public static PluginFixture Create(
        string name,
        IReadOnlyDictionary<string, string>? options = null,
        ServiceExecutionPolicy? policy = null,
        int sliceTimeoutSeconds = 15,
        IReadOnlyList<string>? supportedLanguageCodes = null,
        IReadOnlyList<string>? secureOptionIds = null)
    {
        var directory = DirectoryFor(name);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"Fixture plugin '{name}' was not copied to the output directory: {directory}");
        }

        var sandbox = Path.Combine(Path.GetTempPath(), "easydict-bob-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);

        var package = BobPluginPackage.LoadFromDirectory(directory);
        var descriptor = new BobPluginInstanceDescriptor
        {
            ServiceId = BobServiceIds.Build(package.Manifest.Identifier, "test"),
            PluginIdentifier = package.Manifest.Identifier,
            Version = package.Manifest.Version,
            DisplayName = package.Manifest.Name,
            InstallDirectory = directory,
            SandboxDirectory = sandbox,
            IconPath = package.IconPath,
            OptionValues = options ?? new Dictionary<string, string>(),
            SecureOptionIds = secureOptionIds ?? [],
            SupportedLanguageCodes = supportedLanguageCodes ?? [],
            Policy = policy ?? ServiceExecutionPolicy.Conservative,
            SliceTimeoutSeconds = sliceTimeoutSeconds
        };

        var handler = new StubHttpMessageHandler();
        var httpClient = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

        return new PluginFixture(name, descriptor, handler, httpClient) { SandboxDirectory = sandbox };
    }

    /// <summary>The instance's private storage directory.</summary>
    public string SandboxDirectory { get; private init; } = string.Empty;

    /// <summary>Build the service under test; it is disposed with the fixture.</summary>
    public BobTranslationService Service()
    {
        var service = new BobTranslationService(Descriptor, HttpClient);
        _disposables.Add(service);
        return service;
    }

    /// <summary>A request with sensible defaults for these fixtures.</summary>
    public static TranslationRequest Request(
        string text = "hello",
        Language from = Language.English,
        Language to = Language.SimplifiedChinese,
        int timeoutMs = 30_000,
        string? originalText = null,
        Language? detectedFrom = null)
        => new()
        {
            Text = text,
            FromLanguage = from,
            ToLanguage = to,
            TimeoutMs = timeoutMs,
            OriginalText = originalText,
            DetectedFromLanguage = detectedFrom
        };

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            try
            {
                disposable.Dispose();
            }
            catch
            {
                // A fixture teardown failure must not mask the test's own result.
            }
        }

        try
        {
            if (Directory.Exists(SandboxDirectory))
            {
                Directory.Delete(SandboxDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
