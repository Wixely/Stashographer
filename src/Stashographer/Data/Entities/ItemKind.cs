namespace Stashographer.Data.Entities;

/// <summary>
/// A configurable category of thing (Grocery, Book, Tool, Electronics, …). Kinds are
/// user-extensible and carry a suggested set of attribute field names so the item editor
/// can prompt for the metadata that matters for that kind.
/// </summary>
public class ItemKind
{
    public int Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Icon key resolved via <c>IconCatalog</c> (e.g. "Kitchen", "MenuBook").</summary>
    public string? Icon { get; set; }

    /// <summary>
    /// Suggested custom-attribute field names for this kind (e.g. Book → Author, Publisher).
    /// Stored as a JSON array. Purely advisory — items may carry any attributes.
    /// </summary>
    public List<string> SuggestedAttributes { get; set; } = new();

    /// <summary>Built-in kinds are seeded and cannot be deleted from the UI.</summary>
    public bool IsSystem { get; set; }
}
