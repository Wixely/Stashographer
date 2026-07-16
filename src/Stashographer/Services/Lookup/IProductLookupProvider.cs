namespace Stashographer.Services.Lookup;

/// <summary>A single external metadata source keyed by a scanned code.</summary>
public interface IProductLookupProvider
{
    string Name { get; }

    Task<LookupResult> LookupAsync(string code, CancellationToken ct = default);
}

/// <summary>Picks the right provider for a code and returns a normalized result.</summary>
public interface ILookupRouter
{
    Task<LookupResult> LookupAsync(string code, CancellationToken ct = default);
}
