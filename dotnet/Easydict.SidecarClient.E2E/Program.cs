using Easydict.SidecarClient;

// This E2E console app validates the JSON Lines stdio IPC contract from .NET.
// It spawns the existing Python mock sidecar service and exercises the client.

static int Main(string[] args)
{
    try
    {
        return MainAsync(args).GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex);
        return 1;
    }
}

static async Task<int> MainAsync(string[] args)
{
    var repoRoot = FindRepoRoot(Environment.CurrentDirectory);
    var scriptPath = Path.Combine(repoRoot, "sidecar_mock", "ipc_mock_service.py");
    if (!File.Exists(scriptPath))
    {
        throw new FileNotFoundException("Could not locate python mock sidecar service.", scriptPath);
    }

    var pythonCmd = Environment.GetEnvironmentVariable("EASYDICT_PYTHON");
    if (string.IsNullOrWhiteSpace(pythonCmd))
    {
        pythonCmd = "python3";
    }

    var (fileName, pyArgs) = SplitCommandLine(pythonCmd);
    var allArgs = new List<string>();
    allArgs.AddRange(pyArgs);
    allArgs.Add(scriptPath);

    var stderrLines = new List<string>();

    var options = new SidecarClientOptions
    {
        FileName = fileName,
        Arguments = allArgs,
        WorkingDirectory = repoRoot,
        Environment = new Dictionary<string, string>
        {
            // Force line-buffered behavior.
            ["PYTHONUNBUFFERED"] = "1",
        },
    };

    await using var client = new SidecarClient(options);
    client.StderrLine += line =>
    {
        lock (stderrLines)
        {
            if (stderrLines.Count < 200)
            {
                stderrLines.Add(line);
            }
        }
    };

    await client.StartAsync();

    // health
    var health = await client.CallAsync<HealthResult>("health", timeout: TimeSpan.FromSeconds(3));
    if (health.Capabilities is null || health.Capabilities.Length == 0)
    {
        throw new Exception("Health response missing capabilities.");
    }

    // translate
    var tr = await client.CallAsync<TranslateResult>(
        "translate",
        new TranslateParams { Text = "hello", ToLang = "zh" },
        timeout: TimeSpan.FromSeconds(3));
    if (tr.TranslatedText != "[zh] hello")
    {
        throw new Exception($"Unexpected translation: {tr.TranslatedText}");
    }

    // unknown method -> remote error mapping
    try
    {
        _ = await client.CallAsync<object>("__unknown__", timeout: TimeSpan.FromSeconds(3));
        throw new Exception("Expected SidecarRemoteException but call succeeded.");
    }
    catch (SidecarRemoteException ex) when (ex.Code == "method_not_found")
    {
        // Expected.
    }

    // concurrency: send 10 translate concurrently
    var tasks = Enumerable.Range(0, 10)
        .Select(async i =>
        {
            var res = await client.CallAsync<TranslateResult>(
                "translate",
                new TranslateParams { Text = $"hello-{i}", ToLang = "zh" },
                timeout: TimeSpan.FromSeconds(3));
            return (i, res);
        })
        .ToArray();

    var all = await Task.WhenAll(tasks);
    foreach (var (i, res) in all)
    {
        var expected = $"[zh] hello-{i}";
        if (res.TranslatedText != expected)
        {
            throw new Exception($"Concurrency check failed: expected {expected}, got {res.TranslatedText}");
        }
    }

    // timeout: request that delays
    try
    {
        _ = await client.CallAsync<TranslateResult>(
            "translate",
            new TranslateParams { Text = "slow", ToLang = "zh", DelayMs = 500 },
            timeout: TimeSpan.FromMilliseconds(100));
        throw new Exception("Expected SidecarTimeoutException but call succeeded.");
    }
    catch (SidecarTimeoutException)
    {
        // Expected.
    }

    // process exit: trigger a crash
    try
    {
        _ = await client.CallAsync<object>("crash", timeout: TimeSpan.FromSeconds(2));
        throw new Exception("Expected SidecarProcessExitedException but call succeeded.");
    }
    catch (SidecarProcessExitedException)
    {
        // Expected.
    }

    // Exit code 0 is success for this E2E.
    Console.WriteLine("E2E OK");
    return 0;
}

static string FindRepoRoot(string startDir)
{
    var current = new DirectoryInfo(startDir);
    for (var i = 0; i < 12 && current is not null; i++)
    {
        var candidate = Path.Combine(current.FullName, "sidecar_mock", "ipc_mock_service.py");
        if (File.Exists(candidate))
        {
            return current.FullName;
        }
        current = current.Parent;
    }
    throw new DirectoryNotFoundException("Could not locate repository root by searching for sidecar_mock/ipc_mock_service.py");
}

static (string FileName, List<string> Args) SplitCommandLine(string command)
{
    // Minimal command-line splitter to allow values like:
    //   EASYDICT_PYTHON=python3
    //   EASYDICT_PYTHON="C:\\Python\\python.exe"
    //   EASYDICT_PYTHON="python3 -u"
    var args = new List<string>();
    var sb = new System.Text.StringBuilder();
    var inQuotes = false;

    foreach (var ch in command)
    {
        if (ch == '"')
        {
            inQuotes = !inQuotes;
            continue;
        }

        if (!inQuotes && char.IsWhiteSpace(ch))
        {
            if (sb.Length > 0)
            {
                args.Add(sb.ToString());
                sb.Clear();
            }
            continue;
        }

        sb.Append(ch);
    }

    if (sb.Length > 0)
    {
        args.Add(sb.ToString());
    }

    if (args.Count == 0)
    {
        throw new ArgumentException("EASYDICT_PYTHON must not be empty.");
    }

    return (args[0], args.Skip(1).ToList());
}

sealed class HealthResult
{
    public string? Version { get; set; }
    public string? Build { get; set; }
    public string[]? Capabilities { get; set; }
}

sealed class TranslateParams
{
    public string? Text { get; set; }
    public string? FromLang { get; set; }
    public string? ToLang { get; set; }
    public int? DelayMs { get; set; }
}

sealed class TranslateResult
{
    public string? TranslatedText { get; set; }
    public string? DetectedLang { get; set; }
    public string? Engine { get; set; }
    public int TimingMs { get; set; }
}
