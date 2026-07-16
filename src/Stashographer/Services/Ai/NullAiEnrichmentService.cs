namespace Stashographer.Services.Ai;

/// <summary>No-op implementation used when AI is not configured. Keeps callers branch-free.</summary>
public class NullAiEnrichmentService : IAiEnrichmentService
{
    public bool IsEnabled => false;

    public Task<AiSuggestion?> IdentifyFromPhotoAsync(byte[] image, string mediaType, CancellationToken ct = default)
        => Task.FromResult<AiSuggestion?>(null);

    public Task<AiSuggestion?> EnrichAsync(string name, string? kind, IReadOnlyDictionary<string, string> known, CancellationToken ct = default)
        => Task.FromResult<AiSuggestion?>(null);
}
