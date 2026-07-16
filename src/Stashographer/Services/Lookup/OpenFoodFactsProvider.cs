using System.Text.Json;

namespace Stashographer.Services.Lookup;

/// <summary>
/// Looks up grocery barcodes via Open Food Facts (free, no API key, open data).
/// <see href="https://world.openfoodfacts.org/api/v2/product/{barcode}.json"/>
/// </summary>
public class OpenFoodFactsProvider(HttpClient http, ILogger<OpenFoodFactsProvider> logger)
    : IProductLookupProvider
{
    public string Name => "Open Food Facts";

    public async Task<LookupResult> LookupAsync(string code, CancellationToken ct = default)
    {
        var barcode = CodeClassifier.Normalize(code);
        try
        {
            using var resp = await http.GetAsync(
                $"api/v2/product/{barcode}.json?fields=product_name,brands,image_url,nutrition_grades,categories",
                ct);
            if (!resp.IsSuccessStatusCode)
                return LookupResult.NotFound(barcode, Name);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            if (!root.TryGetProperty("status", out var status) || status.GetInt32() != 1)
                return LookupResult.NotFound(barcode, Name);

            var product = root.GetProperty("product");
            var name = GetString(product, "product_name");
            if (string.IsNullOrWhiteSpace(name))
                return LookupResult.NotFound(barcode, Name);

            var attributes = new Dictionary<string, string>();
            AddIfPresent(attributes, "Brand", GetString(product, "brands"));
            AddIfPresent(attributes, "Category", GetString(product, "categories"));
            AddIfPresent(attributes, "Nutrition grade", GetString(product, "nutrition_grades")?.ToUpperInvariant());

            return new LookupResult
            {
                Found = true,
                Code = barcode,
                Name = name,
                ThumbnailUrl = GetString(product, "image_url"),
                SuggestedKind = "Grocery",
                Attributes = attributes,
                Source = Name
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Open Food Facts lookup failed for {Barcode}", barcode);
            return LookupResult.NotFound(barcode, Name);
        }
    }

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static void AddIfPresent(Dictionary<string, string> dict, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) dict[key] = value!;
    }
}
