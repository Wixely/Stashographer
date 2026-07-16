namespace Stashographer.Services.Lookup;

/// <summary>
/// Provider-agnostic result of a code lookup. Every provider maps its own response shape
/// onto this so the UI never has to know which source answered.
/// </summary>
public record LookupResult
{
    public bool Found { get; init; }

    public string? Code { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? ThumbnailUrl { get; init; }

    /// <summary>Suggested <c>ItemKind.Name</c> (e.g. "Book", "Grocery").</summary>
    public string? SuggestedKind { get; init; }

    /// <summary>Normalized metadata to pre-fill the item's attribute bag.</summary>
    public Dictionary<string, string> Attributes { get; init; } = new();

    /// <summary>Which provider produced this result (for display / diagnostics).</summary>
    public string? Source { get; init; }

    public static LookupResult NotFound(string code, string source) =>
        new() { Found = false, Code = code, Source = source };
}
