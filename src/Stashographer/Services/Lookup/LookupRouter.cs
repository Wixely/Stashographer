using Microsoft.Extensions.Caching.Memory;

namespace Stashographer.Services.Lookup;

/// <summary>
/// Routes a code to the appropriate provider (ISBN → Open Library, otherwise Open Food
/// Facts) and caches results for a day to avoid re-hitting the public APIs on repeat scans.
/// </summary>
public class LookupRouter(
    OpenFoodFactsProvider foodFacts,
    OpenLibraryProvider openLibrary,
    IMemoryCache cache,
    ILogger<LookupRouter> logger) : ILookupRouter
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(24);

    public async Task<LookupResult> LookupAsync(string code, CancellationToken ct = default)
    {
        var normalized = CodeClassifier.Normalize(code);
        if (normalized.Length == 0)
            return LookupResult.NotFound(code, "None");

        if (cache.TryGetValue<LookupResult>(normalized, out var cached) && cached is not null)
            return cached;

        var provider = SelectProvider(normalized);
        logger.LogInformation("Routing {Code} to {Provider}", normalized, provider.Name);

        var result = await provider.LookupAsync(normalized, ct);
        if (result.Found)
            cache.Set(normalized, result, CacheFor);

        return result;
    }

    private IProductLookupProvider SelectProvider(string code) =>
        CodeClassifier.Classify(code) == CodeKind.Isbn ? openLibrary : foodFacts;
}
