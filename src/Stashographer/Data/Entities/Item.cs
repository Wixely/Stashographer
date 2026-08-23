namespace Stashographer.Data.Entities;

/// <summary>
/// A single catalogued thing. A unified, kind-agnostic model: type-specific metadata lives
/// in <see cref="Attributes"/> (persisted as a JSON <c>AttributesJson</c> column) rather than
/// in per-type tables, so any manner of item — groceries, books, tools, electronics — is
/// supported. Navigation objects are populated by the data layer where needed.
/// </summary>
public class Item
{
    public int Id { get; set; }

    /// <summary>
    /// Shared opaque key for entries created by splitting one quantity across places.
    /// Null means the item has never been split.
    /// </summary>
    public string? CollectionKey { get; set; }

    /// <summary>Scanned barcode / ISBN / other identifier. Nullable — not everything scans.</summary>
    public string? Code { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public int ItemKindId { get; set; }
    public ItemKind? Kind { get; set; }

    public decimal Quantity { get; set; } = 1;

    /// <summary>Free-text unit (each, g, ml, pack…).</summary>
    public string? Unit { get; set; }

    /// <summary>Reorder threshold; when <see cref="Quantity"/> falls to/below this it is "low".</summary>
    public decimal LowStockThreshold { get; set; }

    public DateOnly? ExpiryDate { get; set; }

    /// <summary>Direct location, when the item is not inside a container.</summary>
    public int? LocationId { get; set; }
    public Location? Location { get; set; }

    /// <summary>Container the item lives in; its location is used when set.</summary>
    public int? ContainerId { get; set; }
    public Container? Container { get; set; }

    /// <summary>Remote image URL (from a lookup provider). Used as a fallback when no
    /// <see cref="ImageId"/> is set.</summary>
    public string? ThumbnailUrl { get; set; }

    /// <summary>Relative path to a locally-stored photo, if one was captured.</summary>
    public string? PhotoPath { get; set; }

    /// <summary>Primary stored image (see <c>Images</c> table), served at <c>/img/{id}</c>.</summary>
    public int? ImageId { get; set; }

    /// <summary>Flexible per-item metadata, persisted as JSON in <c>AttributesJson</c>.</summary>
    public Dictionary<string, string> Attributes { get; set; } = new();

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True while an un-returned checkout exists (computed by the data layer).</summary>
    public bool IsCheckedOut { get; set; }
}
