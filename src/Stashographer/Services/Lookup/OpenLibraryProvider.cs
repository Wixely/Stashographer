using System.Text.Json;

namespace Stashographer.Services.Lookup;

/// <summary>
/// Looks up books by ISBN via Open Library (free, no API key, open data).
/// <see href="https://openlibrary.org/isbn/{isbn}.json"/> — author names are resolved via
/// their referenced <c>/authors/{key}.json</c> records; covers come from covers.openlibrary.org.
/// </summary>
public class OpenLibraryProvider(HttpClient http, ILogger<OpenLibraryProvider> logger)
    : IProductLookupProvider
{
    public string Name => "Open Library";

    public async Task<LookupResult> LookupAsync(string code, CancellationToken ct = default)
    {
        var isbn = CodeClassifier.Normalize(code);
        try
        {
            using var resp = await http.GetAsync($"isbn/{isbn}.json", ct);
            if (!resp.IsSuccessStatusCode)
                return LookupResult.NotFound(isbn, Name);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var title = GetString(root, "title");
            if (string.IsNullOrWhiteSpace(title))
                return LookupResult.NotFound(isbn, Name);

            var subtitle = GetString(root, "subtitle");
            var name = string.IsNullOrWhiteSpace(subtitle) ? title : $"{title}: {subtitle}";

            var attributes = new Dictionary<string, string>();
            var authors = await ResolveAuthorsAsync(root, ct);
            if (authors.Count > 0) attributes["Author"] = string.Join(", ", authors);
            AddIfPresent(attributes, "Publisher", GetFirstOfArray(root, "publishers"));
            AddIfPresent(attributes, "Published", GetString(root, "publish_date"));
            if (root.TryGetProperty("number_of_pages", out var pages) && pages.ValueKind == JsonValueKind.Number)
                attributes["Pages"] = pages.GetInt32().ToString();

            return new LookupResult
            {
                Found = true,
                Code = isbn,
                Name = name,
                // default=false → 404 for missing covers (instead of a blank 1×1 GIF),
                // which lets the client-side broken-image fallback show a placeholder.
                ThumbnailUrl = $"https://covers.openlibrary.org/b/isbn/{isbn}-M.jpg?default=false",
                SuggestedKind = "Book",
                Attributes = attributes,
                Source = Name
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Open Library lookup failed for {Isbn}", isbn);
            return LookupResult.NotFound(isbn, Name);
        }
    }

    private async Task<List<string>> ResolveAuthorsAsync(JsonElement root, CancellationToken ct)
    {
        var names = new List<string>();
        if (!root.TryGetProperty("authors", out var authors) || authors.ValueKind != JsonValueKind.Array)
            return names;

        foreach (var a in authors.EnumerateArray())
        {
            if (!a.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String)
                continue;
            try
            {
                using var resp = await http.GetAsync($"{key.GetString()!.TrimStart('/')}.json", ct);
                if (!resp.IsSuccessStatusCode) continue;
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var authorName = GetString(doc.RootElement, "name");
                if (!string.IsNullOrWhiteSpace(authorName)) names.Add(authorName!);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Author resolution failed for {Key}", key.GetString());
            }
        }
        return names;
    }

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? GetFirstOfArray(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0
            ? v[0].GetString()
            : null;

    private static void AddIfPresent(Dictionary<string, string> dict, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) dict[key] = value!;
    }
}
