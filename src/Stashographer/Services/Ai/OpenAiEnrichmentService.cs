using System.Text.Json;
using Microsoft.Extensions.AI;
using Stashographer.Data.Entities;
using Stashographer.Services.Inventory;

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
    ILogger<OpenAiEnrichmentService> logger,
    AttributeNameService? attributeNames = null) : IAiEnrichmentService
{
    public bool IsEnabled => clients.IsConfigured;

    private static readonly ChatOptions JsonOptions = new()
    {
        // Text is the most interoperable OpenAI-protocol mode. Some local servers (including
        // Qwen Studio) reject OpenAI's legacy json_object mode while accepting JSON-schema or
        // text. Prompts still require JSON and ExtractJson defensively isolates the payload.
        ResponseFormat = ChatResponseFormat.Text,
        Temperature = 0.2f
    };

    // --- Identify -----------------------------------------------------------------

    public async Task<VisionIdentification?> IdentifyItemAsync(
        byte[] image, string mediaType, IReadOnlyList<string> knownKinds,
        CancellationToken ct = default, string? intakeContext = null,
        AiRegionalContext? regionalContext = null)
    {
        var kinds = knownKinds.Count > 0 ? string.Join(", ", knownKinds) : "Other";
        var canonicalNames = attributeNames is null
            ? Array.Empty<string>()
            : (await attributeNames.GetCanonicalNamesAsync(ct: ct)).ToArray();
        var attributeRule = AttributeRule(canonicalNames);
        var system =
            "You catalogue household inventory from photos. Reply with ONLY a JSON object shaped as: " +
            $"{{\"name\": string, \"kind\": one of [{kinds}], \"description\": string, " +
            "\"attributes\": { key: value, ... }, \"price\": {\"amount\": number, \"currency\": three-letter ISO code or null} or null, " +
            "\"expiry\": {\"rawText\": string, \"date\": \"YYYY-MM-DD\" or null, " +
            "\"type\": \"use_by\"|\"best_before\"|\"unknown\", \"confidence\": number 0..1} or null, " +
            "\"barcode\": string or null, \"count\": integer}. " +
            "name = short product/item name. barcode = digits only, and ONLY when a barcode is clearly readable, else null. " +
            "count = how many of this same item are visible (default 1). " +
            "Only return price when it is visibly printed; never estimate it. Price is the unit price and must not also appear in attributes. " +
            "Only return expiry when a use-by, best-before, or otherwise clearly expiring date is visibly printed. " +
            "Preserve its exact visible text in rawText. If the date is ambiguous, return date as null rather than guessing. " +
            "Use concise ordinary attribute keys (Brand, Model, Colour, Size). Omit fields you cannot determine. " +
            attributeRule + RegionalRule(regionalContext);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system),
            new(ChatRole.User, new List<AIContent>
            {
                new TextContent(string.IsNullOrWhiteSpace(intakeContext)
                    ? "Identify this item for a home inventory."
                    : "Identify this item for a home inventory. Earlier captures from this same intake session " +
                      "are included below as weak context. They can suggest a likely item kind or storage area, " +
                      "but do not copy their product identity or unsupported attributes.\n\n" + intakeContext),
                new DataContent(image, mediaType)
            })
        };

        var json = await CompleteJsonAsync(useVision: true, messages, ct);
        var parsed = json is null ? null : ParseIdentification(json);
        if (parsed is null || attributeNames is null) return parsed;
        var focusedNames = await attributeNames.GetCanonicalNamesAsync(kindName: parsed.Kind, ct: ct);
        return parsed with { Attributes = AttributeNameService.Canonicalize(parsed.Attributes, focusedNames) };
    }

    // --- Detect -------------------------------------------------------------------

    public async Task<List<DetectedBox>> DetectItemsAsync(byte[] image, string mediaType, CancellationToken ct = default)
    {
        const string system =
            "You locate distinct physical items in a photo for a home inventory. Reply with ONLY a JSON object: " +
            "{\"items\": [ {\"label\": string, \"box\": {\"x\": number, \"y\": number, \"w\": number, \"h\": number}} ]}. " +
            "Boxes are normalized to image dimensions (0..1), top-left origin. Return one tight box for every " +
            "separately countable physical object, including identical or adjacent copies; never group several " +
            "objects into one box. Ignore surfaces, backgrounds, people, and printed pictures of products.";

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

    // --- Exact physical capture relationship -------------------------------------

    public async Task<CaptureRelationshipPick?> ClassifyCaptureRelationshipAsync(
        byte[] image,
        string mediaType,
        VisionIdentification identification,
        IReadOnlyList<CaptureMatchCandidate> recentCaptures,
        CancellationToken ct = default)
    {
        if (recentCaptures.Count == 0) return null;
        const string system =
            "You compare consecutive household inventory photos. Decide whether the new photo is another view " +
            "of the exact same physical object captured moments ago, not merely the same product or model. " +
            "Matching name, barcode, packaging design, brand, colour, or model is not enough to prove the same " +
            "physical instance. Use viewpoint continuity and instance-specific evidence such as the same wear, " +
            "marks, serial, label placement, folds, contents, or surrounding context. Choose another_instance " +
            "when a separate copy is visible or likely. Use uncertain whenever instance identity is not supported. " +
            "Reply ONLY as JSON: {\"queueItemId\": number or null, " +
            "\"relationship\":\"same_physical\"|\"another_instance\"|\"different\"|\"uncertain\", " +
            "\"confidence\":\"high\"|\"medium\"|\"low\", " +
            "\"suggestedRole\":\"front\"|\"back\"|\"detail\"|\"label\"|\"receipt\"|\"other\", " +
            "\"reason\": string of at most 20 words}.";

        var candidateList = recentCaptures.Select(candidate => new
        {
            queueItemId = candidate.QueueItemId,
            candidate.Name,
            candidate.Attributes
        });
        var contents = new List<AIContent>
        {
            new TextContent(
                $"New photo identification: {JsonSerializer.Serialize(identification)}. " +
                $"Recent capture candidates, newest first: {JsonSerializer.Serialize(candidateList)}."),
            new TextContent("New photo:"),
            new DataContent(image, mediaType)
        };
        foreach (var candidate in recentCaptures.Take(6))
        {
            contents.Add(new TextContent($"Recent queue item {candidate.QueueItemId}:"));
            contents.Add(new DataContent(candidate.Thumbnail, candidate.ThumbnailMediaType));
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system),
            new(ChatRole.User, contents)
        };
        var json = await CompleteJsonAsync(useVision: true, messages, ct);
        return json is null ? null : ParseCaptureRelationship(json);
    }

    // --- Receipt extraction -------------------------------------------------------

    public async Task<ReceiptExtraction?> ExtractReceiptAsync(
        byte[] image,
        string mediaType,
        IReadOnlyList<ReceiptMatchCandidate> candidates,
        AiRegionalContext regionalContext,
        CancellationToken ct = default)
    {
        const string system =
            "You extract purchase evidence from a receipt for a household inventory. " +
            "Reply ONLY as JSON: {\"merchant\": string or null, \"purchaseDate\": \"YYYY-MM-DD\" or null, " +
            "\"currency\": three-letter ISO code or null, \"total\": number or null, " +
            "\"lines\": [{\"lineIndex\": integer, \"description\": string, \"quantity\": number, " +
            "\"unitPrice\": number or null, \"lineTotal\": number or null, " +
            "\"matchedQueueItemId\": integer or null, \"confidence\": \"high\"|\"medium\"|\"low\"}]}. " +
            "Extract only visible receipt data; never estimate prices, dates, merchant, or currency. " +
            "Keep one stable zero-based lineIndex per purchasable receipt line and exclude tax, payment, " +
            "subtotal, total, discount-summary, and loyalty lines. Match only to the supplied candidates. " +
            "A product name resemblance is insufficient for high confidence when several candidates are plausible. " +
            "Use null and low confidence when uncertain.";
        var compact = candidates.Select(candidate => new
        {
            queueItemId = candidate.QueueItemId,
            inventoryItemId = candidate.InventoryItemId,
            candidate.Name,
            candidate.Code,
            candidate.Attributes
        });
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system + RegionalRule(regionalContext)),
            new(ChatRole.User, new List<AIContent>
            {
                new TextContent(
                    "Extract this receipt and propose conservative matches to these items captured in the " +
                    "same intake session: " + JsonSerializer.Serialize(compact)),
                new DataContent(image, mediaType)
            })
        };
        var json = await CompleteJsonAsync(useVision: true, messages, ct);
        var extraction = json is null ? null : ParseReceipt(json);
        if (extraction is null) return null;

        var allowed = candidates.Select(candidate => candidate.QueueItemId).ToHashSet();
        foreach (var line in extraction.Lines)
        {
            if (line.MatchedQueueItemId is not { } queueId || !allowed.Contains(queueId))
            {
                line.MatchedQueueItemId = null;
                line.Confidence = MatchConfidence.None;
            }
            line.Selected = line.MatchedQueueItemId is not null
                            && line.Confidence == MatchConfidence.High;
        }
        return extraction;
    }

    // --- Enrich (text) ------------------------------------------------------------

    public async Task<AiSuggestion?> EnrichAsync(
        string name, string? kind, IReadOnlyDictionary<string, string> known, CancellationToken ct = default)
    {
        var canonicalNames = attributeNames is null
            ? Array.Empty<string>()
            : (await attributeNames.GetCanonicalNamesAsync(kindName: kind, ct: ct)).ToArray();
        var system =
            "You catalogue household inventory. Reply with ONLY a JSON object, no prose, shaped as: " +
            "{\"name\": string, \"kind\": one of [Grocery, Book, Tool, Electronics, Media, Clothing, Other], " +
            "\"description\": string, \"attributes\": { key: value, ... }}. " +
            "Use concise attribute keys (e.g. Brand, Model, Colour). Omit fields you cannot determine. " +
            AttributeRule(canonicalNames);
        var prompt =
            $"Item name: {name}\nKind: {kind ?? "unknown"}\nKnown attributes: {JsonSerializer.Serialize(known)}\n" +
            "Add a helpful description and any additional attributes you are confident about.";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system),
            new(ChatRole.User, prompt)
        };

        var json = await CompleteJsonAsync(useVision: false, messages, ct);
        var parsed = json is null ? null : ParseSuggestion(json);
        return parsed is null || attributeNames is null
            ? parsed
            : parsed with { Attributes = AttributeNameService.Canonicalize(parsed.Attributes, canonicalNames) };
    }

    // --- BOM suggestion (text) ----------------------------------------------------

    public async Task<AiBomSuggestion?> SuggestBomAsync(
        string request, BomKind kind, IReadOnlyList<AiBomInventoryItem> inventory,
        IReadOnlyList<string> canonicalAttributeNames, CancellationToken ct = default)
    {
        var system =
            "You draft household recipes and bills of materials. Reply with ONLY a JSON object: " +
            "{\"name\": string, \"description\": string, \"outputQuantity\": number, \"outputUnit\": string, " +
            "\"requirements\": [{\"name\": string, \"quantity\": number, \"unit\": string or null, " +
            "\"optional\": boolean, \"matchItemKindId\": number or null, \"matchText\": string, " +
            "\"requiredAttributes\": {key:value}}]}. " +
            "Every genuinely required ingredient or part must be present even when current inventory lacks it. " +
            "Draft generic, interchangeable requirements: matchText should name the underlying ingredient or " +
            "capability, not a brand. Add a required attribute only when it is functionally essential; never add " +
            "Brand unless the user explicitly requires that brand. Use only supplied item-kind IDs. Units are not " +
            "automatically converted, so prefer units already used by matching inventory when practical. " +
            AttributeRule(canonicalAttributeNames);
        var compactInventory = inventory.Take(200).Select(item => new
        {
            item.Id,
            item.Name,
            item.KindId,
            item.Kind,
            item.Quantity,
            item.Unit,
            item.Attributes
        });
        var prompt =
            $"Requested type: {kind}.\nUser request: {request.Trim()}\n" +
            $"Current inventory context: {JsonSerializer.Serialize(compactInventory)}\n" +
            "Create a concise, practical draft. Inventory is context for matching, not a reason to omit missing requirements.";
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system),
            new(ChatRole.User, prompt)
        };
        var json = await CompleteJsonAsync(useVision: false, messages, ct);
        return json is null ? null : ParseBomSuggestion(json, kind);
    }

    // --- Meal-plan suggestion (text) ----------------------------------------------

    public async Task<AiMealPlanSuggestion?> SuggestMealPlanAsync(
        string? request,
        DateOnly startDate,
        int days,
        IReadOnlyList<AiMealPlanRecipe> recipes,
        IReadOnlyList<AiMealPlanInventoryItem> inventory,
        AiRegionalContext regionalContext,
        CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 14);
        if (recipes.Count == 0) return null;
        var endDate = startDate.AddDays(days - 1);
        const string system =
            "You draft practical household meal plans using only saved recipes supplied by the application. " +
            "Prioritize recipes whose matching inventory expires soonest, especially overdue or near-expiry food, " +
            "without claiming food is safe when it may not be. Do not invent recipes or inventory. " +
            "Account for required ingredient quantities across the whole plan and do not budget any inventory " +
            "quantity more than once; return fewer meals when the supplied stock cannot support every day. " +
            "Reply ONLY as JSON: {\"name\": string, \"notes\": string or null, \"entries\": [" +
            "{\"date\":\"YYYY-MM-DD\", \"mealSlot\":string, \"bomDefinitionId\":integer, " +
            "\"outputQuantity\":number, \"reason\":string}]}. " +
            "Return one dinner per requested day when enough distinct ready recipes exist; recipes may repeat when needed. " +
            "The reason should briefly name the expiring ingredient that motivated the choice. " +
            "Output quantity means the recipe's output amount (usually servings), not a multiplier.";
        var prompt =
            $"Plan dates: {startDate:yyyy-MM-dd} through {endDate:yyyy-MM-dd}. " +
            $"User preferences: {(string.IsNullOrWhiteSpace(request) ? "none supplied" : request.Trim())}. " +
            $"Ready saved recipes: {JsonSerializer.Serialize(recipes)}. " +
            $"Relevant inventory, earliest expiry first: {JsonSerializer.Serialize(inventory)}.";
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system + RegionalRule(regionalContext)),
            new(ChatRole.User, prompt)
        };
        var json = await CompleteJsonAsync(useVision: false, messages, ct);
        var suggestion = json is null ? null : ParseMealPlanSuggestion(json);
        if (suggestion is null) return null;
        var recipeIds = recipes.Select(recipe => recipe.Id).ToHashSet();
        suggestion.Entries = suggestion.Entries
            .Where(entry => entry.Date >= startDate
                            && entry.Date <= endDate
                            && recipeIds.Contains(entry.BomDefinitionId)
                            && entry.OutputQuantity > 0)
            .OrderBy(entry => entry.Date)
            .ThenBy(entry => entry.MealSlot)
            .ToList();
        return suggestion.Entries.Count == 0 ? null : suggestion;
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

    private static string AttributeRule(IReadOnlyList<string> canonicalNames) =>
        canonicalNames.Count == 0
            ? string.Empty
            : "Existing canonical attribute names are: " + JsonSerializer.Serialize(canonicalNames) + ". " +
              "When one has the same meaning as an attribute you identify, use that exact name. " +
              "Create a new attribute name only when none is semantically equivalent.";

    private static string RegionalRule(AiRegionalContext? context) => context is null
        ? string.Empty
        : $" Regional context: current local date {context.CurrentDate:yyyy-MM-dd}; " +
          $"date order {context.DateOrder}; culture {context.CultureName}; time zone {context.TimeZoneId}; " +
          $"default currency {context.DefaultCurrency}. The default currency may be used by the application " +
          "when the amount is visible but the currency is not; return currency as null in that case.";

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
            var (priceAmount, priceCurrency) = GetPrice(root);
            return new VisionIdentification
            {
                Name = GetString(root, "name"),
                Kind = GetString(root, "kind"),
                Description = GetString(root, "description"),
                Barcode = NormalizeBarcode(GetString(root, "barcode")),
                Count = root.TryGetProperty("count", out var c)
                        && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) && n > 0 ? n : 1,
                Attributes = GetAttributes(root),
                PriceAmount = priceAmount,
                PriceCurrency = priceCurrency,
                Expiry = GetExpiry(root)
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
                if (!el.TryGetProperty("box", out var b) || !TryReadBox(b, out var values)) continue;
                var max = values.Max();
                var scale = max > 100 ? 1000d : max > 1 ? 100d : 1d;
                var box = new DetectedBox(GetString(el, "label"),
                    values[0] / scale, values[1] / scale, values[2] / scale, values[3] / scale);
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

    private static bool TryReadBox(JsonElement box, out double[] values)
    {
        values = new double[4];
        if (box.ValueKind == JsonValueKind.Object)
        {
            var names = new[] { "x", "y", "w", "h" };
            for (var i = 0; i < names.Length; i++)
            {
                if (!box.TryGetProperty(names[i], out var value)
                    || value.ValueKind != JsonValueKind.Number) return false;
                values[i] = value.GetDouble();
            }
            return true;
        }

        if (box.ValueKind != JsonValueKind.Array || box.GetArrayLength() != 4) return false;
        var index = 0;
        foreach (var value in box.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number) return false;
            values[index++] = value.GetDouble();
        }
        return true;
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

    internal CaptureRelationshipPick? ParseCaptureRelationship(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            int? queueItemId = root.TryGetProperty("queueItemId", out var idElement)
                               && idElement.ValueKind == JsonValueKind.Number
                               && idElement.TryGetInt32(out var id)
                ? id
                : null;
            var relationship = GetString(root, "relationship")?.Trim().ToLowerInvariant() switch
            {
                "same_physical" => CaptureRelationship.SamePhysicalItem,
                "another_instance" => CaptureRelationship.AnotherInstance,
                "different" => CaptureRelationship.DifferentItem,
                _ => CaptureRelationship.Uncertain
            };
            var confidence = GetString(root, "confidence")?.Trim().ToLowerInvariant() switch
            {
                "high" => MatchConfidence.High,
                "medium" => MatchConfidence.Medium,
                "low" => MatchConfidence.Low,
                _ => MatchConfidence.None
            };
            var role = GetString(root, "suggestedRole")?.Trim().ToLowerInvariant() switch
            {
                "front" => ItemImageRole.Front,
                "back" => ItemImageRole.Back,
                "label" => ItemImageRole.Label,
                "receipt" => ItemImageRole.Receipt,
                "other" => ItemImageRole.Other,
                _ => ItemImageRole.Detail
            };
            var reason = GetString(root, "reason")?.Trim();
            return new CaptureRelationshipPick(
                queueItemId,
                relationship,
                queueItemId is null ? MatchConfidence.None : confidence,
                role,
                string.IsNullOrWhiteSpace(reason) ? null : reason);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Could not parse capture relationship JSON");
            return null;
        }
    }

    internal ReceiptExtraction? ParseReceipt(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            DateOnly? purchaseDate = null;
            if (DateOnly.TryParseExact(GetString(root, "purchaseDate"), "yyyy-MM-dd", out var parsedDate))
                purchaseDate = parsedDate;
            var currency = GetString(root, "currency")?.Trim().ToUpperInvariant();
            if (currency is not { Length: 3 } || currency.Any(c => c is < 'A' or > 'Z'))
                currency = null;
            var extraction = new ReceiptExtraction
            {
                Merchant = GetString(root, "merchant")?.Trim(),
                PurchaseDate = purchaseDate,
                Currency = currency,
                Total = GetNonNegativeDecimal(root, "total")
            };
            if (!root.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
                return extraction;

            var fallbackIndex = 0;
            var usedIndexes = new HashSet<int>();
            foreach (var element in lines.EnumerateArray())
            {
                var description = GetString(element, "description")?.Trim();
                if (string.IsNullOrWhiteSpace(description))
                {
                    fallbackIndex++;
                    continue;
                }
                var lineIndex = element.TryGetProperty("lineIndex", out var indexElement)
                                && indexElement.ValueKind == JsonValueKind.Number
                                && indexElement.TryGetInt32(out var parsedIndex)
                                && parsedIndex >= 0
                    ? parsedIndex
                    : fallbackIndex;
                while (!usedIndexes.Add(lineIndex)) lineIndex++;
                int? matchedQueueItemId = element.TryGetProperty("matchedQueueItemId", out var matchElement)
                                               && matchElement.ValueKind == JsonValueKind.Number
                                               && matchElement.TryGetInt32(out var matchId)
                                               && matchId > 0
                    ? matchId
                    : null;
                var confidence = ParseConfidence(GetString(element, "confidence"));
                extraction.Lines.Add(new ReceiptLineSuggestion
                {
                    LineIndex = lineIndex,
                    Description = description,
                    Quantity = GetPositiveDecimal(element, "quantity") ?? 1,
                    UnitPrice = GetNonNegativeDecimal(element, "unitPrice"),
                    LineTotal = GetNonNegativeDecimal(element, "lineTotal"),
                    MatchedQueueItemId = matchedQueueItemId,
                    Confidence = matchedQueueItemId is null ? MatchConfidence.None : confidence
                });
                fallbackIndex++;
            }
            return extraction;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Could not parse receipt JSON");
            return null;
        }
    }

    internal AiBomSuggestion? ParseBomSuggestion(string json, BomKind kind)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var name = GetString(root, "name")?.Trim();
            if (string.IsNullOrWhiteSpace(name)) return null;
            var suggestion = new AiBomSuggestion
            {
                Name = name,
                Kind = kind,
                Description = GetString(root, "description"),
                OutputQuantity = GetPositiveDecimal(root, "outputQuantity") ?? 1,
                OutputUnit = GetString(root, "outputUnit")
            };
            if (!root.TryGetProperty("requirements", out var requirements)
                || requirements.ValueKind != JsonValueKind.Array) return suggestion;
            foreach (var element in requirements.EnumerateArray())
            {
                var requirementName = GetString(element, "name")?.Trim();
                if (string.IsNullOrWhiteSpace(requirementName)) continue;
                suggestion.Requirements.Add(new AiBomRequirementSuggestion
                {
                    Name = requirementName,
                    Quantity = GetPositiveDecimal(element, "quantity") ?? 1,
                    Unit = GetString(element, "unit"),
                    IsOptional = element.TryGetProperty("optional", out var optional)
                                 && optional.ValueKind is JsonValueKind.True,
                    MatchItemKindId = element.TryGetProperty("matchItemKindId", out var kindId)
                                      && kindId.ValueKind == JsonValueKind.Number
                                      && kindId.TryGetInt32(out var parsedKindId)
                        ? parsedKindId
                        : null,
                    MatchText = GetString(element, "matchText") ?? requirementName,
                    RequiredAttributes = GetAttributes(element, "requiredAttributes")
                });
            }
            return suggestion;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Could not parse BOM suggestion JSON");
            return null;
        }
    }

    internal AiMealPlanSuggestion? ParseMealPlanSuggestion(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var suggestion = new AiMealPlanSuggestion
            {
                Name = GetString(root, "name")?.Trim() ?? string.Empty,
                Notes = GetString(root, "notes")?.Trim()
            };
            if (!root.TryGetProperty("entries", out var entries)
                || entries.ValueKind != JsonValueKind.Array) return null;
            foreach (var element in entries.EnumerateArray())
            {
                if (!DateOnly.TryParseExact(GetString(element, "date"), "yyyy-MM-dd", out var date))
                    continue;
                if (!element.TryGetProperty("bomDefinitionId", out var recipeElement)
                    || recipeElement.ValueKind != JsonValueKind.Number
                    || !recipeElement.TryGetInt32(out var recipeId)
                    || recipeId <= 0) continue;
                suggestion.Entries.Add(new AiMealPlanEntrySuggestion
                {
                    Date = date,
                    MealSlot = GetString(element, "mealSlot")?.Trim() ?? "Dinner",
                    BomDefinitionId = recipeId,
                    OutputQuantity = GetPositiveDecimal(element, "outputQuantity") ?? 1,
                    Reason = GetString(element, "reason")?.Trim()
                });
            }
            return suggestion.Entries.Count == 0 ? null : suggestion;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Could not parse meal-plan suggestion JSON");
            return null;
        }
    }

    private static Dictionary<string, string> GetAttributes(
        JsonElement root, string property = "attributes")
    {
        var attributes = new Dictionary<string, string>();
        if (root.TryGetProperty(property, out var attrs) && attrs.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in attrs.EnumerateObject())
            {
                var value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
                if (!string.IsNullOrWhiteSpace(value)) attributes[p.Name] = value!;
            }
        }
        return attributes;
    }

    private static decimal? GetPositiveDecimal(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDecimal(out var parsed)
        && parsed > 0
            ? parsed
            : null;

    private static decimal? GetNonNegativeDecimal(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDecimal(out var parsed)
        && parsed >= 0
            ? parsed
            : null;

    private static MatchConfidence ParseConfidence(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "high" => MatchConfidence.High,
        "medium" => MatchConfidence.Medium,
        "low" => MatchConfidence.Low,
        _ => MatchConfidence.None
    };

    private static (decimal? Amount, string? Currency) GetPrice(JsonElement root)
    {
        if (!root.TryGetProperty("price", out var price) || price.ValueKind != JsonValueKind.Object)
            return (null, null);
        if (!price.TryGetProperty("amount", out var amountElement)
            || amountElement.ValueKind != JsonValueKind.Number
            || !amountElement.TryGetDecimal(out var amount)
            || amount < 0)
            return (null, null);
        var currency = GetString(price, "currency")?.Trim().ToUpperInvariant();
        return currency is null or { Length: 0 }
            ? (amount, null)
            : currency is { Length: 3 } && currency.All(c => c is >= 'A' and <= 'Z')
                ? (amount, currency)
                : (amount, null);
    }

    private static VisionExpiry? GetExpiry(JsonElement root)
    {
        if (!root.TryGetProperty("expiry", out var expiry) || expiry.ValueKind != JsonValueKind.Object)
            return null;
        DateOnly? date = null;
        var dateText = GetString(expiry, "date");
        if (DateOnly.TryParseExact(dateText, "yyyy-MM-dd", out var parsed)) date = parsed;
        decimal? confidence = expiry.TryGetProperty("confidence", out var confidenceElement)
                              && confidenceElement.ValueKind == JsonValueKind.Number
                              && confidenceElement.TryGetDecimal(out var value)
            ? Math.Clamp(value, 0, 1)
            : null;
        var raw = GetString(expiry, "rawText");
        return date is null && string.IsNullOrWhiteSpace(raw)
            ? null
            : new VisionExpiry
            {
                RawText = raw,
                Date = date,
                Type = GetString(expiry, "type"),
                Confidence = confidence
            };
    }

    private static string? NormalizeBarcode(string? barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return null;
        var digits = new string(barcode.Where(char.IsDigit).ToArray());
        return digits.Length >= 8 ? digits : null; // shorter than EAN-8 is likely a hallucination
    }

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

}
