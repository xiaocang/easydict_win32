using Easydict.WinUI.Views.Controls;
using FluentAssertions;
using Xunit;

namespace Easydict.WinUI.Tests.Services;

public class ResultSaveActionLayoutTests
{
    [Fact]
    public void Resolve_DefaultsToFavoriteButtonAndAnkiMenu()
    {
        var layout = ResultSaveActionLayout.Resolve(preferAnkiButton: false, ankiAvailable: true, favoriteAvailable: true);

        layout.AnkiButton.Should().BeFalse();
        layout.FavoriteButton.Should().BeTrue();
        layout.AnkiMenu.Should().BeTrue();
        layout.FavoriteMenu.Should().BeFalse();
    }

    [Fact]
    public void Resolve_PrefersAnkiButtonAndMovesFavoriteToMenu()
    {
        var layout = ResultSaveActionLayout.Resolve(preferAnkiButton: true, ankiAvailable: true, favoriteAvailable: true);

        layout.AnkiButton.Should().BeTrue();
        layout.FavoriteButton.Should().BeFalse();
        layout.AnkiMenu.Should().BeFalse();
        layout.FavoriteMenu.Should().BeTrue();
    }

    [Fact]
    public void Resolve_KeepsFavoriteWhenAnkiIsUnavailable()
    {
        var layout = ResultSaveActionLayout.Resolve(preferAnkiButton: true, ankiAvailable: false, favoriteAvailable: true);

        layout.AnkiButton.Should().BeFalse();
        layout.FavoriteButton.Should().BeTrue();
        layout.AnkiMenu.Should().BeFalse();
        layout.FavoriteMenu.Should().BeFalse();
    }
}
