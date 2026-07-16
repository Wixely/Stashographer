using MudBlazor;

namespace Stashographer.Components.Layout;

/// <summary>
/// Central theme definition. Light and dark palettes are defined here; adding further themes
/// later is a matter of adding palettes and selecting between them.
/// </summary>
public static class StashTheme
{
    public static readonly MudTheme Default = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#2E7D5B",       // "stash" green
            Secondary = "#5B6EE1",
            AppbarBackground = "#2E7D5B",
            Background = "#F7F9F8",
            Surface = "#FFFFFF"
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#4CAF82",
            Secondary = "#7C8AF0",
            AppbarBackground = "#1B2620",
            Background = "#121712",
            Surface = "#1B211C"
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "10px"
        }
    };
}
