namespace Easydict.WinUI.Views.Controls;

/// <summary>Chooses which save action is the primary result button and which stays in the overflow menu.</summary>
internal static class ResultSaveActionLayout
{
    public readonly record struct Visibility(
        bool AnkiButton,
        bool FavoriteButton,
        bool AnkiMenu,
        bool FavoriteMenu);

    public static Visibility Resolve(bool preferAnkiButton, bool ankiAvailable, bool favoriteAvailable)
    {
        var ankiPrimary = preferAnkiButton && ankiAvailable;
        return new Visibility(
            AnkiButton: ankiPrimary,
            FavoriteButton: favoriteAvailable && !ankiPrimary,
            AnkiMenu: ankiAvailable && !ankiPrimary,
            FavoriteMenu: favoriteAvailable && ankiPrimary);
    }
}
