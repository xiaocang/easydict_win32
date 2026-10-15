using Easydict.WinUI.Services;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public sealed class TrayIconCatalogTests
{
    [Theory]
    [InlineData(null, TrayIconCatalog.DefaultId)]
    [InlineData("", TrayIconCatalog.DefaultId)]
    [InlineData("  ", TrayIconCatalog.DefaultId)]
    [InlineData("not-an-icon", TrayIconCatalog.DefaultId)]
    [InlineData("default", TrayIconCatalog.DefaultId)]
    [InlineData(" DEFAULT ", TrayIconCatalog.DefaultId)]
    [InlineData("e", TrayIconCatalog.EId)]
    [InlineData("AJP", TrayIconCatalog.AjpId)]
    [InlineData(" wen ", TrayIconCatalog.WenId)]
    [InlineData("placeholder-1", TrayIconCatalog.DefaultId)]
    public void Normalize_KnownIdsStayCanonicalAndUnknownIdsUseTheCurrentIcon(string? id, string expected)
        => Assert.Equal(expected, TrayIconCatalog.Normalize(id));

    [Fact]
    public void BundledDropPathsStayStable()
    {
        Assert.Equal(
            "Assets/Branding/TrayIcons/e.ico",
            TrayIconCatalog.ExpectedFileHint(TrayIconCatalog.EId));
        Assert.Equal(
            "Assets/Branding/TrayIcons/ajp.ico",
            TrayIconCatalog.ExpectedFileHint(TrayIconCatalog.AjpId));
        Assert.Equal(
            "Assets/Branding/TrayIcons/wen.ico",
            TrayIconCatalog.ExpectedFileHint(TrayIconCatalog.WenId));
    }

    [Fact]
    public void DarkIconIsPreferredWhenBothFilesExist()
    {
        using var root = new TempIconRoot();
        var light = root.Write("Assets", "Branding", "TrayIcons", "e.ico");
        var dark = root.Write("Assets", "Branding", "TrayIcons", "e.dark.ico");

        Assert.Equal(dark, TrayIconCatalog.ResolveExistingPath(TrayIconCatalog.EId, dark: true, root.Path));
        Assert.Equal(light, TrayIconCatalog.ResolveExistingPath(TrayIconCatalog.EId, dark: false, root.Path));
        Assert.True(TrayIconCatalog.HasBundledIcon(TrayIconCatalog.EId, dark: true, root.Path));
    }

    [Fact]
    public void LightIconIsUsedOnADarkTaskbarWhenTheDarkFileIsAbsent()
    {
        using var root = new TempIconRoot();
        var light = root.Write("Assets", "Branding", "TrayIcons", "ajp.ico");

        Assert.Equal(light, TrayIconCatalog.ResolveExistingPath(TrayIconCatalog.AjpId, dark: true, root.Path));
        Assert.True(TrayIconCatalog.HasBundledIcon(TrayIconCatalog.AjpId, dark: true, root.Path));
    }

    [Fact]
    public void DarkOnlyIconIsUsedOnALightTaskbar()
    {
        using var root = new TempIconRoot();
        var dark = root.Write("Assets", "Branding", "TrayIcons", "e.dark.ico");

        Assert.Equal(dark, TrayIconCatalog.ResolveExistingPath(TrayIconCatalog.EId, dark: false, root.Path));
        Assert.True(TrayIconCatalog.HasBundledIcon(TrayIconCatalog.EId, dark: false, root.Path));
    }

    [Fact]
    public void MissingIconFallsBackToTheCurrentIcon()
    {
        using var root = new TempIconRoot();
        var current = root.Write("Assets", "Branding", "Unplated", "AppIcon.Dark.ico");

        Assert.Equal(current, TrayIconCatalog.ResolveExistingPath(TrayIconCatalog.WenId, dark: true, root.Path));
        Assert.False(TrayIconCatalog.HasBundledIcon(TrayIconCatalog.WenId, dark: true, root.Path));
        Assert.Null(TrayIconCatalog.TryGetPreviewPath(TrayIconCatalog.WenId, dark: true, root.Path));
    }

    [Fact]
    public void DefaultPreviewPrefersThe256PxArtworkOverThe32PxTrayFile()
    {
        using var root = new TempIconRoot();
        root.Write("Assets", "Branding", "Dark", "TrayIcon.png");
        root.Write("TrayIcon.png");
        var light = root.Write("Assets", "Square44x44Logo.targetsize-256.png");
        var dark = root.Write("Assets", "Branding", "Dark", "Icon-256.png");

        Assert.Equal(light, TrayIconCatalog.TryGetPreviewPath(TrayIconCatalog.DefaultId, dark: false, root.Path));
        Assert.Equal(dark, TrayIconCatalog.TryGetPreviewPath(TrayIconCatalog.DefaultId, dark: true, root.Path));
    }

    [Fact]
    public void PreviewPrefersTheBundledPngOverTheIco()
    {
        using var root = new TempIconRoot();
        root.Write("Assets", "Branding", "TrayIcons", "wen.ico");
        var png = root.Write("Assets", "Branding", "TrayIcons", "wen.png");

        Assert.Equal(png, TrayIconCatalog.TryGetPreviewPath(TrayIconCatalog.WenId, dark: false, root.Path));
    }

    private sealed class TempIconRoot : IDisposable
    {
        public TempIconRoot()
        {
            Path = Directory.CreateTempSubdirectory("easydict-tray-icons-").FullName;
        }

        public string Path { get; }

        public string Write(params string[] parts)
        {
            var full = System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, [1]);
            return full;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
