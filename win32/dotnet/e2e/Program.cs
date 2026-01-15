using System.Text.Json;
using Easydict.SidecarClient;
using Easydict.SidecarClient.Protocol;

/// <summary>
/// E2E test runner for SidecarClient.
/// Tests: basic requests, concurrent requests, timeout, process crash detection.
/// Exit code 0 = all tests passed, non-zero = failure.
/// </summary>

var mockServicePath = FindMockServicePath();
Console.WriteLine($"[E2E] Mock service path: {mockServicePath}");

var allPassed = true;

// Test 1: Basic health request
allPassed &= await RunTest("Basic health request", async () =>
{
    await using var client = CreateClient(mockServicePath);
    client.Start();

    var response = await client.SendRequestAsync("health");
    Assert(response.IsSuccess, "health should succeed");
    Assert(response.Result.HasValue, "health should have result");

    var result = response.Result.Value;
    Assert(result.TryGetProperty("version", out _), "result should have version");
    Assert(result.TryGetProperty("capabilities", out _), "result should have capabilities");
});

// Test 2: Basic translate request
allPassed &= await RunTest("Basic translate request", async () =>
{
    await using var client = CreateClient(mockServicePath);
    client.Start();

    var response = await client.SendRequestAsync("translate", new
    {
        text = "hello",
        toLang = "zh"
    });
    Assert(response.IsSuccess, "translate should succeed");

    var result = response.Result!.Value;
    Assert(result.TryGetProperty("translatedText", out var translated), "result should have translatedText");
    Assert(translated.GetString()!.Contains("hello"), "translatedText should contain original text");
});

// Test 3: Unknown method returns error
allPassed &= await RunTest("Unknown method returns error", async () =>
{
    await using var client = CreateClient(mockServicePath);
    client.Start();

    var response = await client.SendRequestAsync("unknown_method_xyz");
    Assert(response.IsError, "unknown method should return error");
    Assert(response.Error!.Code == IpcErrorCodes.MethodNotFound, $"error code should be method_not_found, got {response.Error.Code}");
});

// Test 4: Concurrent requests (id-based multiplexing)
allPassed &= await RunTest("Concurrent requests", async () =>
{
    await using var client = CreateClient(mockServicePath);
    client.Start();

    var tasks = new List<Task<IpcResponse>>();
    for (int i = 0; i < 10; i++)
    {
        var idx = i;
        tasks.Add(client.SendRequestAsync("translate", new
        {
            text = $"message-{idx}",
            toLang = "en"
        }));
    }

    var responses = await Task.WhenAll(tasks);
    Assert(responses.Length == 10, "should have 10 responses");
    Assert(responses.All(r => r.IsSuccess), "all responses should succeed");

    // Verify each response contains the correct message
    for (int i = 0; i < 10; i++)
    {
        var result = responses[i].Result!.Value;
        var text = result.GetProperty("translatedText").GetString()!;
        Assert(text.Contains($"message-{i}"), $"response {i} should contain message-{i}");
    }
});

// Test 5: Timeout handling
allPassed &= await RunTest("Timeout handling", async () =>
{
    await using var client = CreateClient(mockServicePath);
    client.Start();

    try
    {
        // Request with 2 second delay, but 500ms timeout
        await client.SendRequestAsync("translate", new
        {
            text = "slow",
            toLang = "en",
            delayMs = 2000
        }, timeoutMs: 500);

        Assert(false, "should have thrown SidecarTimeoutException");
    }
    catch (SidecarTimeoutException)
    {
        // Expected
    }
});

// Test 6: Process crash detection
allPassed &= await RunTest("Process crash detection", async () =>
{
    await using var client = CreateClient(mockServicePath);
    var exitedTcs = new TaskCompletionSource<int?>();
    client.OnProcessExited += code => exitedTcs.TrySetResult(code);
    client.Start();

    // First verify it's running
    var healthResponse = await client.SendRequestAsync("health");
    Assert(healthResponse.IsSuccess, "health should succeed before crash");

    // Send crash command (no response expected, process will exit)
    try
    {
        await client.SendRequestAsync("crash", timeoutMs: 1000);
    }
    catch (SidecarProcessExitedException)
    {
        // Expected - process exited before response
    }
    catch (SidecarTimeoutException)
    {
        // Also acceptable - timeout before we detect exit
    }

    // Wait for exit event
    var exitCode = await exitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Assert(exitCode == 2, $"exit code should be 2, got {exitCode}");
    Assert(!client.IsRunning, "client should not be running after crash");
});

// Test 7: Graceful shutdown
allPassed &= await RunTest("Graceful shutdown", async () =>
{
    await using var client = CreateClient(mockServicePath);
    client.Start();

    var response = await client.SendRequestAsync("shutdown");
    Assert(response.IsSuccess, "shutdown should succeed");

    // Wait a bit for process to exit
    await Task.Delay(500);
    Assert(!client.IsRunning, "client should not be running after shutdown");
});

// Test 8: stderr log collection
allPassed &= await RunTest("Stderr log collection", async () =>
{
    await using var client = CreateClient(mockServicePath);
    var logs = new List<string>();
    client.OnStderrLog += log => logs.Add(log);
    client.Start();

    await client.SendRequestAsync("health");
    await Task.Delay(200); // Give time for logs to be collected

    Assert(logs.Count > 0, "should have collected stderr logs");
    // Logs should be JSON (structured)
    Assert(logs.Any(l => l.Contains("\"level\"")), "logs should be structured JSON");
});

// Summary
Console.WriteLine();
Console.WriteLine(allPassed ? "[E2E] ✅ All tests passed!" : "[E2E] ❌ Some tests failed!");
return allPassed ? 0 : 1;

// === Helper functions ===

static string FindMockServicePath()
{
    // Look for the mock service relative to the e2e directory
    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "sidecar_mock", "ipc_mock_service.py"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "sidecar_mock", "ipc_mock_service.py"),
        Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "sidecar_mock", "ipc_mock_service.py"),
        Path.Combine(Directory.GetCurrentDirectory(), "win32", "sidecar_mock", "ipc_mock_service.py"),
    };

    foreach (var candidate in candidates)
    {
        var normalized = Path.GetFullPath(candidate);
        if (File.Exists(normalized))
            return normalized;
    }

    throw new FileNotFoundException("Could not find ipc_mock_service.py");
}

static SidecarClient CreateClient(string mockServicePath)
{
    // Use "python" on Windows, "python3" on Unix-like systems
    var pythonExe = OperatingSystem.IsWindows() ? "python" : "python3";
    return new SidecarClient(new SidecarClientOptions
    {
        ExecutablePath = pythonExe,
        Arguments = [mockServicePath],
        DefaultTimeoutMs = 10000
    });
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new Exception($"Assertion failed: {message}");
}

static async Task<bool> RunTest(string name, Func<Task> test)
{
    Console.Write($"[E2E] {name}... ");
    try
    {
        await test();
        Console.WriteLine("✅ PASS");
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ FAIL: {ex.Message}");
        return false;
    }
}

