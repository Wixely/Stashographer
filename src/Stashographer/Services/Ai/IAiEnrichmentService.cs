using Stashographer.Data.Entities;

namespace Stashographer.Services.Ai;

/// <summary>An AI-produced suggestion for an item, used by the "enrich" action.</summary>
public record AiSuggestion
{
    public string? Name { get; init; }
    public string? SuggestedKind { get; init; }
    public string? Description { get; init; }
    public Dictionary<string, string> Attributes { get; init; } = new();
}

/// <summary>
/// Optional hooks into an OpenAI-protocol model. Implementations must be safe to call even
/// when AI is disabled; callers should check <see cref="IsEnabled"/> to decide whether to
/// surface AI actions in the UI.
/// </summary>
public interface IAiEnrichmentService
{
    bool IsEnabled { get; }

    /// <summary>Identifies a photographed item: name, kind, attributes, visible barcode, count.</summary>
    Task<VisionIdentification?> IdentifyItemAsync(
        byte[] image, string mediaType, IReadOnlyList<string> knownKinds,
        CancellationToken ct = default, string? intakeContext = null,
        AiRegionalContext? regionalContext = null);

    /// <summary>Detects distinct items (with normalized bounding boxes) in a multi-item photo.</summary>
    Task<List<DetectedBox>> DetectItemsAsync(byte[] image, string mediaType, CancellationToken ct = default);

    /// <summary>
    /// Decides whether the photographed item matches one of the given existing inventory
    /// candidates, comparing names, attributes and candidate thumbnails.
    /// </summary>
    Task<MatchPick?> PickMatchAsync(
        byte[] image, string mediaType, VisionIdentification identification,
        IReadOnlyList<MatchCandidate> candidates, CancellationToken ct = default);

    /// <summary>
    /// Distinguishes another view of the exact same recently captured object from another
    /// physical copy of the same product. Product identity alone is insufficient.
    /// </summary>
    Task<CaptureRelationshipPick?> ClassifyCaptureRelationshipAsync(
        byte[] image,
        string mediaType,
        VisionIdentification identification,
        IReadOnlyList<CaptureMatchCandidate> recentCaptures,
        CancellationToken ct = default);

    /// <summary>"Season" an existing item with a richer description and suggested attributes.</summary>
    Task<AiSuggestion?> EnrichAsync(
        string name, string? kind, IReadOnlyDictionary<string, string> known, CancellationToken ct = default);

    /// <summary>Drafts a reviewable recipe/build without persisting it.</summary>
    Task<AiBomSuggestion?> SuggestBomAsync(
        string request, BomKind kind, IReadOnlyList<AiBomInventoryItem> inventory,
        IReadOnlyList<string> canonicalAttributeNames, CancellationToken ct = default);

    /// <summary>Makes a minimal round-trip to the endpoint. Returns null on success, else the error.</summary>
    Task<string?> TestConnectionAsync(CancellationToken ct = default);
}
