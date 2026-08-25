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
            Primary = "#2E7D5B",
            PrimaryContrastText = "#FFFFFF",
            Secondary = "#5263D7",
            SecondaryContrastText = "#FFFFFF",
            Info = "#3566A8",
            InfoContrastText = "#FFFFFF",
            Success = "#2E7D5B",
            SuccessContrastText = "#FFFFFF",
            Warning = "#A85800",
            WarningContrastText = "#FFFFFF",
            Error = "#B33B4A",
            ErrorContrastText = "#FFFFFF",
            TextPrimary = "#17221C",
            TextSecondary = "#526158",
            TextDisabled = "rgba(23,34,28,0.38)",
            ActionDefault = "#5B6860",
            AppbarBackground = "#2E7D5B",
            AppbarText = "#FFFFFF",
            Background = "#F5F8F6",
            BackgroundGray = "#EEF3F0",
            Surface = "#FFFFFF",
            DrawerBackground = "#FFFFFF",
            DrawerText = "#2C3931",
            DrawerIcon = "#66736B",
            LinesDefault = "#DCE4DF",
            LinesInputs = "#ADBAB2",
            TableLines = "#DFE6E1",
            Divider = "#DCE4DF",
            DividerLight = "rgba(23,34,28,0.08)",
            Skeleton = "#E4EAE6"
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#4CAF82",
            PrimaryContrastText = "#071B12",
            Secondary = "#9AA5FF",
            SecondaryContrastText = "#161A36",
            Info = "#7DA2E6",
            InfoContrastText = "#0A1A33",
            Success = "#4CAF82",
            SuccessContrastText = "#071B12",
            Warning = "#E5A34B",
            WarningContrastText = "#271A06",
            Error = "#EF7F8C",
            ErrorContrastText = "#2A0B10",
            TextPrimary = "#E7ECE8",
            TextSecondary = "#B4BDB7",
            TextDisabled = "rgba(231,236,232,0.38)",
            ActionDefault = "#B4BDB7",
            AppbarBackground = "#18221C",
            AppbarText = "#F4F7F5",
            Background = "#101512",
            BackgroundGray = "#151B17",
            Surface = "#1B211C",
            DrawerBackground = "#171D19",
            DrawerText = "#D8DFDA",
            DrawerIcon = "#AAB4AD",
            LinesDefault = "#354139",
            LinesInputs = "#536258",
            TableLines = "#354139",
            TableStriped = "rgba(231,236,232,0.025)",
            Divider = "#354139",
            DividerLight = "rgba(231,236,232,0.08)",
            Skeleton = "#2B342E"
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "10px"
        }
    };
}
