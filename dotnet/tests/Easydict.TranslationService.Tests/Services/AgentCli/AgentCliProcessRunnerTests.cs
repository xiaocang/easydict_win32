using Easydict.TranslationService.Services.AgentCli;
using FluentAssertions;
using Xunit;

namespace Easydict.TranslationService.Tests.Services.AgentCli;

/// <summary>
/// Tests for AgentCliProcessRunner quoting logic and process lifecycle using
/// only OS-builtin executables (cmd/powershell on Windows, sh/cat elsewhere).
/// </summary>
public class AgentCliProcessRunnerTests
{
    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("trailing\\", "\"trailing\\\\\"")]
    [InlineData("--flag", "--flag")]
    public void QuoteArgument_AppliesMsvcrtRules(string input, string expected)
    {
        AgentCliProcessRunner.QuoteArgument(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Roaming\npm\claude.cmd", true)]
    [InlineData(@"C:\tools\codex.BAT", true)]
    [InlineData(@"C:\Users\x\.local\bin\claude.exe", false)]
    [InlineData("/usr/local/bin/claude", false)]
    public void IsCmdShim_DetectsBatchShims(string path, bool expected)
    {
        AgentCliProcessRunner.IsCmdShim(path).Should().Be(expected);
    }

    [Fact]
    public void BuildCommandLine_QuotesExecutableAndArguments()
    {
        var commandLine = AgentCliProcessRunner.BuildCommandLine(
            @"C:\Program Files\claude.cmd",
            ["-p", "--tools", ""]);

        commandLine.Should().Be("\"C:\\Program Files\\claude.cmd\" -p --tools \"\"");
    }

    [Fact]
    public async Task RunLinesAsync_MissingExecutable_ThrowsProcessException()
    {
        var runner = new AgentCliProcessRunner();
        var missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "no-such-cli.exe");

        var act = async () =>
        {
            await foreach (var _ in runner.RunLinesAsync(missingPath, ["--version"], ""))
            {
            }
        };

        await act.Should().ThrowAsync<AgentCliProcessException>();
    }

    [Fact]
    public async Task RunLinesAsync_EchoesStdinLines()
    {
        var runner = new AgentCliProcessRunner();
        var (executable, arguments) = GetEchoStdinCommand();

        var lines = new List<string>();
        await foreach (var line in runner.RunLinesAsync(executable, arguments, "hello\nworld\n"))
        {
            lines.Add(line);
        }

        lines.Should().Contain("hello");
        lines.Should().Contain("world");
    }

    [Fact]
    public async Task RunLinesAsync_NonZeroExit_ThrowsWithExitCode()
    {
        var runner = new AgentCliProcessRunner();
        var (executable, arguments) = GetExitCommand(3);

        var act = async () =>
        {
            await foreach (var _ in runner.RunLinesAsync(executable, arguments, ""))
            {
            }
        };

        var ex = await act.Should().ThrowAsync<AgentCliProcessException>();
        ex.Which.ExitCode.Should().Be(3);
    }

    private static (string Executable, List<string> Arguments) GetEchoStdinCommand()
    {
        if (OperatingSystem.IsWindows())
        {
            // sort.exe passes every stdin line through to stdout.
            return (Path.Combine(Environment.SystemDirectory, "sort.exe"), []);
        }

        return ("/bin/cat", []);
    }

    private static (string Executable, List<string> Arguments) GetExitCommand(int exitCode)
    {
        if (OperatingSystem.IsWindows())
        {
            return (Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", $"exit {exitCode}"]);
        }

        return ("/bin/sh", ["-c", $"exit {exitCode}"]);
    }
}
