using System.IO.Compression;
using System.Text;
using Easydict.BobPlugin.Package;
using FluentAssertions;
using Xunit;

namespace Easydict.BobPlugin.Tests.Package;

public class BobPluginPackageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "easydict-bob-package-tests", Guid.NewGuid().ToString("N"));

    public BobPluginPackageTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void LoadFromDirectory_ReadsAFixturePlugin()
    {
        var package = BobPluginPackage.LoadFromDirectory(PluginFixture.DirectoryFor("dict"));

        package.Manifest.Identifier.Should().Be("test.dict");
        File.Exists(package.MainScriptPath).Should().BeTrue();
    }

    [Fact]
    public void LoadFromDirectory_FailsWithoutAManifest()
    {
        var empty = NewDirectory("empty");

        var act = () => BobPluginPackage.LoadFromDirectory(empty);

        act.Should().Throw<BobPluginException>()
            .Which.Code.Should().Be(BobPluginError.InvalidPackage);
    }

    [Fact]
    public void LoadFromDirectory_FailsWithoutMainJs()
    {
        var directory = NewDirectory("no-main");
        File.WriteAllText(Path.Combine(directory, "info.json"), """{"identifier":"x","category":"translate"}""");

        var act = () => BobPluginPackage.LoadFromDirectory(directory);

        act.Should().Throw<BobPluginException>()
            .Which.Code.Should().Be(BobPluginError.ScriptLoadFailed);
    }

    [Fact]
    public void Extract_UnpacksAnArchive()
    {
        var archive = BuildArchive("plain.bobplugin", entries: new()
        {
            ["info.json"] = """{"identifier":"com.example.x","category":"translate","version":"1.0.0","name":"X"}""",
            ["main.js"] = "function translate() {}"
        });

        var package = BobPluginPackage.Extract(archive, NewDirectory("plain-out"));

        package.Manifest.Identifier.Should().Be("com.example.x");
        File.Exists(Path.Combine(package.Directory, "main.js")).Should().BeTrue();
    }

    [Fact]
    public void Extract_ToleratesASingleWrappingFolder()
    {
        var archive = BuildArchive("wrapped.bobplugin", entries: new()
        {
            ["my-plugin/info.json"] = """{"identifier":"com.example.wrapped","category":"translate"}""",
            ["my-plugin/main.js"] = "function translate() {}"
        });

        var package = BobPluginPackage.Extract(archive, NewDirectory("wrapped-out"));

        package.Manifest.Identifier.Should().Be("com.example.wrapped");
        package.Directory.Should().EndWith("my-plugin");
    }

    [Fact]
    public void Extract_FindsAnIconEvenWhenTheManifestDoesNotDeclareOne()
    {
        var archive = BuildArchive("icon.bobplugin", entries: new()
        {
            ["info.json"] = """{"identifier":"com.example.icon","category":"translate"}""",
            ["main.js"] = "function translate() {}",
            ["icon.png"] = "not really a png"
        });

        var package = BobPluginPackage.Extract(archive, NewDirectory("icon-out"));

        package.IconPath.Should().NotBeNull();
        Path.GetFileName(package.IconPath!).Should().Be("icon.png");
    }

    [Fact]
    public void Extract_RefusesEntriesThatEscapeTheDestination()
    {
        var archive = BuildArchive("slip.bobplugin", entries: new()
        {
            ["info.json"] = """{"identifier":"com.example.slip","category":"translate"}""",
            ["main.js"] = "function translate() {}",
            ["../../escaped.txt"] = "owned"
        });
        var destination = NewDirectory("slip-out");

        var act = () => BobPluginPackage.Extract(archive, destination);

        act.Should().Throw<BobPluginException>()
            .Which.Code.Should().Be(BobPluginError.InvalidPackage);
        File.Exists(Path.Combine(_root, "escaped.txt")).Should().BeFalse();
        File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escaped.txt")).Should().BeFalse();
    }

    [Fact]
    public void Extract_RefusesTooManyEntries()
    {
        var entries = new Dictionary<string, string>
        {
            ["info.json"] = """{"identifier":"com.example.many","category":"translate"}""",
            ["main.js"] = "function translate() {}"
        };
        for (var i = 0; i < 20; i++)
        {
            entries[$"file{i}.txt"] = "x";
        }

        var archive = BuildArchive("many.bobplugin", entries);

        var act = () => BobPluginPackage.Extract(archive, NewDirectory("many-out"), new BobPackageLimits(MaxEntries: 10));

        act.Should().Throw<BobPluginException>()
            .Which.Code.Should().Be(BobPluginError.InvalidPackage);
    }

    [Fact]
    public void Extract_RefusesAnEntryOverTheSizeLimit()
    {
        var archive = BuildArchive("big.bobplugin", entries: new()
        {
            ["info.json"] = """{"identifier":"com.example.big","category":"translate"}""",
            ["main.js"] = new string('x', 5_000)
        });

        var act = () => BobPluginPackage.Extract(
            archive,
            NewDirectory("big-out"),
            new BobPackageLimits(MaxEntryUncompressedBytes: 1_000));

        act.Should().Throw<BobPluginException>()
            .Which.Code.Should().Be(BobPluginError.InvalidPackage);
    }

    [Fact]
    public void Extract_RefusesAnArchiveOverTheTotalLimit()
    {
        var entries = new Dictionary<string, string>
        {
            ["info.json"] = """{"identifier":"com.example.total","category":"translate"}""",
            ["main.js"] = "function translate() {}"
        };
        for (var i = 0; i < 10; i++)
        {
            entries[$"blob{i}.txt"] = new string('x', 500);
        }

        var act = () => BobPluginPackage.Extract(
            BuildArchive("total.bobplugin", entries),
            NewDirectory("total-out"),
            new BobPackageLimits(MaxTotalUncompressedBytes: 1_000));

        act.Should().Throw<BobPluginException>()
            .Which.Code.Should().Be(BobPluginError.InvalidPackage);
    }

    [Fact]
    public void Extract_FailsForAMissingFile()
    {
        var act = () => BobPluginPackage.Extract(Path.Combine(_root, "nope.bobplugin"), NewDirectory("missing-out"));

        act.Should().Throw<BobPluginException>()
            .Which.Code.Should().Be(BobPluginError.InvalidPackage);
    }

    [Fact]
    public void Extract_FailsForSomethingThatIsNotAnArchive()
    {
        var path = Path.Combine(_root, "garbage.bobplugin");
        File.WriteAllText(path, "this is not a zip file");

        var act = () => BobPluginPackage.Extract(path, NewDirectory("garbage-out"));

        act.Should().Throw<BobPluginException>()
            .Which.Code.Should().Be(BobPluginError.InvalidPackage);
    }

    private string NewDirectory(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Build a zip whose entry names are written verbatim, so traversal names survive.</summary>
    private string BuildArchive(string fileName, Dictionary<string, string> entries)
    {
        var path = Path.Combine(_root, fileName);
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }

        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
