using MudBlazor;

namespace Stashographer;

/// <summary>
/// A curated set of icons users can pick for quick links, keyed by a short stable name that
/// is what gets persisted (not the long SVG path). Keeps stored config readable and portable.
/// </summary>
public static class IconCatalog
{
    public static readonly IReadOnlyList<(string Key, string Icon)> Options = new List<(string, string)>
    {
        ("Inventory2", Icons.Material.Filled.Inventory2),
        ("Kitchen", Icons.Material.Filled.Kitchen),
        ("MenuBook", Icons.Material.Filled.MenuBook),
        ("Dashboard", Icons.Material.Filled.Dashboard),
        ("QrCodeScanner", Icons.Material.Filled.QrCodeScanner),
        ("Handyman", Icons.Material.Filled.Handyman),
        ("Devices", Icons.Material.Filled.Devices),
        ("Album", Icons.Material.Filled.Album),
        ("Checkroom", Icons.Material.Filled.Checkroom),
        ("Category", Icons.Material.Filled.Category),
        ("Room", Icons.Material.Filled.Room),
        ("Home", Icons.Material.Filled.Home),
        ("Star", Icons.Material.Filled.Star),
        ("ShoppingCart", Icons.Material.Filled.ShoppingCart),
        ("Kitchen2", Icons.Material.Filled.Blender),
    };

    private static readonly Dictionary<string, string> Map =
        Options.ToDictionary(o => o.Key, o => o.Icon, StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolves a stored icon key to a MudBlazor icon, falling back to a generic one.</summary>
    public static string Resolve(string? key) =>
        key is not null && Map.TryGetValue(key, out var icon) ? icon : Icons.Material.Filled.Category;
}
