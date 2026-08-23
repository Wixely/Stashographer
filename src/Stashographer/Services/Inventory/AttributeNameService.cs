using System.Text.Json;
using Dapper;
using Stashographer.Data;
using Stashographer.Data.Entities;

namespace Stashographer.Services.Inventory;

/// <summary>
/// Builds the canonical attribute vocabulary from real inventory usage and item-kind
/// suggestions, then maps harmless spelling/format variants back to those names.
/// </summary>
public class AttributeNameService(IDbConnectionFactory db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Returns canonical names ordered with kind-specific suggestions first, followed by
    /// frequently used inventory keys and suggestions from other kinds.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetCanonicalNamesAsync(
        int? kindId = null, string? kindName = null, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var kinds = (await conn.QueryAsync<KindRow>(
            "SELECT Id, Name, SuggestedAttributes FROM ItemKinds ORDER BY Id")).ToList();
        var itemJson = await conn.QueryAsync<string>(
            "SELECT AttributesJson FROM Items WHERE AttributesJson IS NOT NULL AND AttributesJson <> '{}' ");

        var preferredKind = kinds.FirstOrDefault(k => k.Id == kindId)
            ?? kinds.FirstOrDefault(k => string.Equals(k.Name, kindName, StringComparison.OrdinalIgnoreCase));
        var preferred = ParseList(preferredKind?.SuggestedAttributes);
        var usage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var json in itemJson)
        {
            foreach (var key in ParseAttributes(json).Keys)
            {
                var clean = Clean(key);
                if (clean.Length > 0 && !SpecialAttributeCatalog.IsReservedName(clean))
                    usage[clean] = usage.GetValueOrDefault(clean) + 1;
            }
        }

        var candidates = new List<Candidate>();
        candidates.AddRange(preferred.Select((name, index) =>
            new Candidate(Clean(name), true, index, usage.GetValueOrDefault(Clean(name)))));
        candidates.AddRange(usage.Select(x => new Candidate(x.Key, false, int.MaxValue, x.Value)));
        foreach (var kind in kinds)
        {
            foreach (var name in ParseList(kind.SuggestedAttributes))
                candidates.Add(new Candidate(Clean(name), false, int.MaxValue, usage.GetValueOrDefault(Clean(name))));
        }

        return candidates
            .Where(x => x.Name.Length > 0 && SemanticKey(x.Name).Length > 0
                                               && !SpecialAttributeCatalog.IsReservedName(x.Name))
            .GroupBy(x => SemanticKey(x.Name), StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(x => x.Preferred)
                .ThenByDescending(x => x.Usage)
                .ThenBy(x => x.PreferredIndex)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderByDescending(x => x.Preferred)
            .ThenBy(x => x.PreferredIndex)
            .ThenByDescending(x => x.Usage)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Name)
            .Take(100)
            .ToList();
    }

    /// <summary>
    /// Reuses a known canonical name for case, punctuation and deliberately safe spelling
    /// variants. Unknown names are preserved instead of being guessed into the wrong field.
    /// </summary>
    public static Dictionary<string, string> Canonicalize(
        IReadOnlyDictionary<string, string> attributes, IReadOnlyList<string> canonicalNames)
    {
        var byMeaning = canonicalNames
            .Select(Clean)
            .Where(x => x.Length > 0 && SemanticKey(x).Length > 0)
            .GroupBy(SemanticKey, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var exactNames = canonicalNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (rawName, rawValue) in attributes
                     .OrderByDescending(x => exactNames.Contains(Clean(x.Key))))
        {
            var name = Clean(rawName);
            var value = rawValue?.Trim();
            var meaning = SemanticKey(name);
            if (name.Length == 0 || meaning.Length == 0 || string.IsNullOrWhiteSpace(value)) continue;
            var canonical = byMeaning.GetValueOrDefault(meaning) ?? name;
            result.TryAdd(canonical, value);
        }

        return result;
    }

    public async Task<Dictionary<string, string>> CanonicalizeAsync(
        IReadOnlyDictionary<string, string> attributes, int? kindId = null,
        string? kindName = null, CancellationToken ct = default) =>
        Canonicalize(attributes, await GetCanonicalNamesAsync(kindId, kindName, ct));

    private static string Clean(string? value) =>
        string.Join(' ', (value ?? string.Empty)
            .Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string SemanticKey(string value)
    {
        var normalized = new string(value.Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant).ToArray());
        return normalized switch
        {
            "color" or "colors" or "colours" => "colour",
            "flavor" or "flavors" or "flavours" => "flavour",
            "modelno" or "modelnum" or "modelnumber" => "model",
            "serial" or "serialno" or "serialnum" or "serialnumber" => "serialnumber",
            "dimensions" => "dimension",
            "materials" => "material",
            "authors" => "author",
            "categories" => "category",
            _ => normalized
        };
    }

    private static Dictionary<string, string> ParseAttributes(string json)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    private static List<string> ParseList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(json, Json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    private sealed class KindRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string SuggestedAttributes { get; set; } = "[]";
    }

    private sealed record Candidate(string Name, bool Preferred, int PreferredIndex, int Usage);
}
