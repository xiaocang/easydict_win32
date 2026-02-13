using FluentAssertions;

namespace Easydict.BrowserRegistrar.Tests;

/// <summary>
/// Tests for CLI argument parsing in Program.
/// </summary>
public class ProgramTests
{
    [Fact]
    public void HasFlag_MatchesExactFlag()
    {
        var args = new[] { "install", "--chrome", "--bridge-path", "C:\\test" };

        Program.HasFlag(args, "--chrome").Should().BeTrue();
        Program.HasFlag(args, "--firefox").Should().BeFalse();
    }

    [Fact]
    public void HasFlag_IsCaseInsensitive()
    {
        var args = new[] { "install", "--Chrome" };

        Program.HasFlag(args, "--chrome").Should().BeTrue();
        Program.HasFlag(args, "--CHROME").Should().BeTrue();
    }

    [Fact]
    public void GetArgValue_ReturnsNextArg()
    {
        var args = new[] { "install", "--bridge-path", "C:\\test\\bridge.exe", "--chrome" };

        Program.GetArgValue(args, "--bridge-path").Should().Be("C:\\test\\bridge.exe");
    }

    [Fact]
    public void GetArgValue_ReturnsNullWhenMissing()
    {
        var args = new[] { "install", "--chrome" };

        Program.GetArgValue(args, "--bridge-path").Should().BeNull();
    }

    [Fact]
    public void GetArgValue_ReturnsNullWhenFlagIsLastArg()
    {
        var args = new[] { "install", "--bridge-path" };

        Program.GetArgValue(args, "--bridge-path").Should().BeNull();
    }

    [Fact]
    public void GetArgValue_IsCaseInsensitive()
    {
        var args = new[] { "install", "--Bridge-Path", "/some/path" };

        Program.GetArgValue(args, "--bridge-path").Should().Be("/some/path");
    }

    [Fact]
    public void WriteJson_ProducesValidSnakeCaseJson()
    {
        var writer = new StringWriter();
        Console.SetOut(writer);

        try
        {
            Program.WriteJson(new { success = true, bridge_path = "C:\\test" });
            var output = writer.ToString().Trim();

            output.Should().Contain("\"success\":true");
            output.Should().Contain("\"bridge_path\":\"C:\\\\test\"");
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
    }
}
