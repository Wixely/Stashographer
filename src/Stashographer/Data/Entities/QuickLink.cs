namespace Stashographer.Data.Entities;

public enum QuickLinkTarget
{
    Dashboard = 0,
    Scan = 1,
    Inventory = 2
}

/// <summary>How the inventory presents itself: data-dense table or image-forward cover grid.</summary>
public enum InventoryView
{
    List = 0,
    Gallery = 1
}

/// <summary>
/// A configurable large button on the home launcher. Targets either a built-in page
/// (Dashboard, Scan) or a pre-filtered Inventory view (include/exclude item kinds).
/// </summary>
public class QuickLink
{
    public int Id { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>Icon key resolved via <c>IconCatalog</c> (e.g. "Kitchen").</summary>
    public string? Icon { get; set; }

    public QuickLinkTarget Target { get; set; } = QuickLinkTarget.Inventory;

    /// <summary>Kinds to include (OR-ed) when the target is Inventory.</summary>
    public List<int> IncludeKindIds { get; set; } = new();

    /// <summary>Kinds to exclude when the target is Inventory.</summary>
    public List<int> ExcludeKindIds { get; set; } = new();

    /// <summary>Which inventory view this link opens (List table or Gallery cover grid).</summary>
    public InventoryView ViewMode { get; set; } = InventoryView.List;

    public int SortOrder { get; set; }

    /// <summary>The route this link navigates to, including any filter query string.</summary>
    public string ToUrl() => Target switch
    {
        QuickLinkTarget.Dashboard => "dashboard",
        QuickLinkTarget.Scan => "scan",
        QuickLinkTarget.Inventory => BuildInventoryUrl(),
        _ => ""
    };

    private string BuildInventoryUrl()
    {
        var parts = new List<string>();
        if (IncludeKindIds.Count > 0) parts.Add("include=" + string.Join(",", IncludeKindIds));
        if (ExcludeKindIds.Count > 0) parts.Add("exclude=" + string.Join(",", ExcludeKindIds));
        if (ViewMode == InventoryView.Gallery) parts.Add("view=gallery");
        return "inventory" + (parts.Count > 0 ? "?" + string.Join("&", parts) : "");
    }
}
