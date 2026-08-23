namespace Stashographer.Services.Ai;

/// <summary>What the vision model determined about a photographed item.</summary>
public record VisionIdentification
{
    public string? Name { get; init; }

    /// <summary>Suggested ItemKind name (constrained to the kinds passed in the prompt).</summary>
    public string? Kind { get; init; }

    public string? Description { get; init; }

    public Dictionary<string, string> Attributes { get; init; } = new();

    /// <summary>Visible unit price, kept typed and separate from ordinary attributes.</summary>
    public decimal? PriceAmount { get; init; }

    /// <summary>Three-letter ISO currency code for <see cref="PriceAmount"/>.</summary>
    public string? PriceCurrency { get; init; }

    /// <summary>Barcode digits, only when clearly readable in the photo.</summary>
    public string? Barcode { get; init; }

    /// <summary>How many of this item are visible in the photo (increment amount).</summary>
    public int Count { get; init; } = 1;
}

/// <summary>A detected item in a multi-item photo. Coordinates are normalized 0–1, top-left origin.</summary>
public record DetectedBox(string? Label, double X, double Y, double W, double H);

public enum MatchConfidence
{
    None,
    Low,
    Medium,
    High
}

/// <summary>The model's verdict on whether the photo matches an existing inventory item.</summary>
public record MatchPick(int? MatchedItemId, MatchConfidence Confidence);

/// <summary>An existing item offered to the model for matching, with an optional thumbnail.</summary>
public record MatchCandidate(
    int ItemId,
    string Name,
    IReadOnlyDictionary<string, string> Attributes,
    byte[]? Thumbnail,
    string? ThumbnailMediaType);
