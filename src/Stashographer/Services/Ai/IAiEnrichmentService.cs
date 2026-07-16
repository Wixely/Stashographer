namespace Stashographer.Services.Ai;

/// <summary>An AI-produced suggestion for an item, mapped onto the same shape as a lookup.</summary>
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

    /// <summary>Identify a product from a photo when no barcode is available.</summary>
    Task<AiSuggestion?> IdentifyFromPhotoAsync(byte[] image, string mediaType, CancellationToken ct = default);

    /// <summary>"Season" an existing item with a richer description and suggested attributes.</summary>
    Task<AiSuggestion?> EnrichAsync(string name, string? kind, IReadOnlyDictionary<string, string> known, CancellationToken ct = default);
}
