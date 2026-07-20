using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Stashographer.Services.Ai;

/// <summary>
/// AI features backed by any OpenAI-protocol chat/vision model via the provider-neutral
/// <see cref="IChatClient"/> abstraction. Clients come from <see cref="IAiClientProvider"/>
/// so configuration can change at runtime (Settings page) without a restart. Vision calls
/// (identify/detect/match) may use a different model than text enrichment.
/// All prompts demand strict JSON; responses are parsed leniently (outermost object).
/// </summary>
public class OpenAiEnrichmentService(
    IAiClientProvider clients,
    ILogger<OpenAiEnrichmentService> logger) : IAiEnrichmentService
{
    public bool IsEnabled => clients.IsConfigured;

    private static readonly ChatOptions JsonOptions = new()
    {
        ResponseFormat = ChatResponseFormat.Json,
        Temperature = 0.2f
    };

    // --- Identify -----------------------------------------------------------------

    public async Task<VisionIdentification?> IdentifyItemAsync(
        byte[] image, string mediaType, IReadOnlyList<string> knownKinds, CancellationToken ct = default)
    {
        var kinds = knownKinds.Count > 0 ? string.Join(", ", knownKinds) : "Other";
        var system =
            "You catalogue household inventory from photos. Reply with ONLY a JSON object shaped as: " +
            $"{{\"name\": string, \"kind\": one of [{kinds}], \"description\": string, " +
            "\"attributes\": { key: value, ... }, \"barcode\": string or null, \"count\": integer}. " +
            "name = short product/item name. barcode = digits only, and ONLY when a barcode is clearly readable, else null. " +
            "count = how many of this same item are visible (default 1). " +
            "Use concise attribute keys (Brand, Model, Colour, Size). Omit fields you cannot determine.";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system),
            new(ChatRole.User, new List<AIContent>
            {
                new TextContent("Identify this item for a home inventory."),
                new DataContent(image, mediaType)
            })
        };

        var json = await CompleteJsonAsync(useVision: true, messages, ct);
        return json is null ? null : ParseIdentification(json);
    }

    // --- Detect -------------------------------------------------------------------

    public async Task<List<DetectedBox>> DetectItemsAsync(byte[] image, string mediaType, CancellationToken ct = default)
    {
        const string system =
            "You locate distinct physical items in a photo for a home inventory. Reply with ONLY a JSON object: " +
            "{\"items\": [ {\"label\": string, \"box\": {\"x\": number, \"y\": number, \"w\": number, \"h\": number}} ]}. " +
            "Boxes are normalized to image dimensions (0..1), top-left origin. List each distinct item once; " +
            "ignore surfaces, backgrounds and people. Group identical stacked/adjacent products as one item.";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system),
            new(ChatRole.User, new List<AIContent>
            {
                new TextContent("Detect the items in this photo."),
                new DataContent(image, mediaType)
            })
        };

        var json = await CompleteJsonAsync(useVision: true, messages, ct);
        return json is null ? new List<DetectedBox>() : ParseBoxes(json);
    }

    // --- PickMatch ----------------------------------------------------------------

    public async Task<MatchPick?> PickMatchAsync(
        byte[] image, string mediaType, VisionIdentification identification,
        IReadOnlyList<MatchCandidate> candidates, CancellationToken ct = default)
    {
        const string system =
            "You decide whether a photographed item is the same product as one of the existing inventory items. " +
            "Reply with ONLY a JSON object: {\"matchedItemId\": number or null, \"confidence\": \"high\"|\"medium\"|\"low\"}. " +
            "high = clearly the same product; medium = probably the same; low or null id = not present. " +
            "Compare names, attributes and the candidate photos where provided.";

        var candidateList = candidates.Select(c => new { id = c.ItemId, name = c.Name, attributes = c.Attributes });
        var contents = new List<AIContent>
        {
            new TextContent(
                $"Photographed item was identified as: {JsonSerializer.Serialize(identification)}. " +
                $"Existing inventory candidates: {JsonSerializer.Serialize(candidateList)}. " +
                "The first image is the new photo; any further images are candidate photos, " +
                "in the same order as candidates that have one."),
            new DataContent(image, mediaType)
        };
        foreach (var c in candidates.Where(c => c.Thumbnail is not null).Take(4))
            contents.Add(new DataContent(c.Thumbnail!, c.ThumbnailMediaType ?? "image/jpeg"));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system),
            new(ChatRole.User, contents)
        };

        var json = await CompleteJsonAsync(useVision: true, messages, ct);
        return json is null ? null : ParsePick(json);
    }

    // --- Enrich (text) ------------------------------------------------------------

    public async Task<AiSuggestion?> EnrichAsync(
        string name, string? kind, IReadOnlyDictionary<string, string> known, CancellationToken ct = default)
    {
        const string system =
            "You catalogue household inventory. Reply with ONLY a JSON object, no prose, shaped as: " +
            "{\"name\": string, \"kind\": one of [Grocery, Book, Tool, Electronics, Media, Clothing, Other], " +
            "\"description\": string, \"attributes\": { key: value, ... }}. " +
            "Use concise attribute keys (e.g. Brand, Model, Colour). Omit fields you cannot determine.";
        var prompt =
            $"Item name: {name}\nKind: {kind ?? "unknown"}\nKnown attributes: {JsonSerializer.Serialize(known)}\n" +
            "Add a helpful description and any additional attributes you are confident about.";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system),
            new(ChatRole.User, prompt)
        };

        var json = await CompleteJsonAsync(useVision: false, messages, ct);
        return json is null ? null : ParseSuggestion(json);
    }

    // --- Connection test ------------------------------------------------------------

    public async Task<string?> TestConnectionAsync(CancellationToken ct = default)
    {
        var client = clients.GetChatClient();
        if (client is null) return "AI is not configured.";
        try
        {
            await client.GetResponseAsync(
                new List<ChatMessage> { new(ChatRole.User, "Reply with the single word: OK") },
                new ChatOptions { Temperature = 0 }, ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message;
        }
    }

    // --- Shared plumbing ----------------------------------------------------------

    private async Task<string?> CompleteJsonAsync(bool useVision, List<ChatMessage> messages, CancellationToken ct)
    {
        var client = useVision ? clients.GetVisionClient() : clients.GetChatClient();
        if (client is null) return null; // not configured (or disabled mid-flight)
        try
        {
            var response = await client.GetResponseAsync(messages, JsonOptions, ct);
            return ExtractJson(response.Text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "AI call failed");
            return null;
        }
    }

    /// <summary>Pulls the outermost JSON object out of a response, tolerating stray prose.</summary>
    internal static string? ExtractJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start < 0 || end <= start ? null : text[start..(end + 1)];
    }

    internal VisionIdentification? ParseIdentification(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new VisionIdentification
            {
                Name = GetString(root, "name"),
                Kind = GetString(root, "kind"),
                Description = GetString(root, "description"),
                Barcode = NormalizeBarcode(GetString(root, "barcode")),
                Count = root.TryGetProperty("count", out var c)
                        && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) && n > 0 ? n : 1,
                Attributes = GetAttributes(root)
            };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Could not parse identification JSON");
            return null;
        }
    }

    internal List<DetectedBox> ParseBoxes(string json)
    {
        var boxes = new List<DetectedBox>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return boxes;
            foreach (var el in items.EnumerateArray())
            {
                if (!el.TryGetProperty("box", out var b) || b.ValueKind != JsonValueKind.Object) continue;
                var box = new DetectedBox(
                    GetString(el, "label"),
                    GetDouble(b, "x"), GetDouble(b, "y"), GetDouble(b, "w"), GetDouble(b, "h"));
                // Discard degenerate/out-of-range boxes rather than crop garbage.
                if (box.W > 0.01 && box.H > 0.01 && box.X is >= 0 and < 1 && box.Y is >= 0 and < 1)
                    boxes.Add(box);
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Could not parse detection JSON");
        }
        return boxes;
    }

    internal MatchPick? ParsePick(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            int? id = root.TryGetProperty("matchedItemId", out var m)
                      && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var v) ? v : null;
            var confidence = GetString(root, "confidence")?.ToLowerInvariant() switch
            {
                "high" => MatchConfidence.High,
                "medium" => MatchConfidence.Medium,
                "low" => MatchConfidence.Low,
                _ => MatchConfidence.None
            };
            return new MatchPick(id, id is null ? MatchConfidence.None : confidence);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Could not parse match JSON");
            return null;
        }
    }

    internal AiSuggestion? ParseSuggestion(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new AiSuggestion
            {
                Name = GetString(root, "name"),
                SuggestedKind = GetString(root, "kind"),
                Description = GetString(root, "description"),
                Attributes = GetAttributes(root)
            };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Could not parse suggestion JSON");
            return null;
        }
    }

    private static Dictionary<string, string> GetAttributes(JsonElement root)
    {
        var attributes = new Dictionary<string, string>();
        if (root.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in attrs.EnumerateObject())
            {
                var value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
                if (!string.IsNullOrWhiteSpace(value)) attributes[p.Name] = value!;
            }
        }
        return attributes;
    }

    private static string? NormalizeBarcode(string? barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return null;
        var digits = new string(barcode.Where(char.IsDigit).ToArray());
        return digits.Length >= 8 ? digits : null; // shorter than EAN-8 is likely a hallucination
    }

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double GetDouble(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : -1;
}
