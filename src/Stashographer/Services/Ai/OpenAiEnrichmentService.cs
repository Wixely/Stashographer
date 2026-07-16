using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Stashographer.Services.Ai;

/// <summary>
/// AI enrichment backed by any OpenAI-protocol chat/vision model, via the provider-neutral
/// <see cref="IChatClient"/> abstraction. Asks the model for a strict JSON object and maps it
/// onto <see cref="AiSuggestion"/>.
/// </summary>
public class OpenAiEnrichmentService(IChatClient chat, ILogger<OpenAiEnrichmentService> logger)
    : IAiEnrichmentService
{
    public bool IsEnabled => true;

    private const string SystemPrompt =
        "You catalogue household inventory. Reply with ONLY a JSON object, no prose, shaped as: " +
        "{\"name\": string, \"kind\": one of [Grocery, Book, Tool, Electronics, Media, Clothing, Other], " +
        "\"description\": string, \"attributes\": { key: value, ... }}. " +
        "Use concise attribute keys (e.g. Brand, Model, Colour). Omit fields you cannot determine.";

    public async Task<AiSuggestion?> IdentifyFromPhotoAsync(byte[] image, string mediaType, CancellationToken ct = default)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, new List<AIContent>
            {
                new TextContent("Identify this item for a home inventory."),
                new DataContent(image, mediaType)
            })
        };
        return await CompleteAsync(messages, ct);
    }

    public async Task<AiSuggestion?> EnrichAsync(string name, string? kind, IReadOnlyDictionary<string, string> known, CancellationToken ct = default)
    {
        var knownJson = JsonSerializer.Serialize(known);
        var prompt =
            $"Item name: {name}\nKind: {kind ?? "unknown"}\nKnown attributes: {knownJson}\n" +
            "Add a helpful description and any additional attributes you are confident about.";
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, prompt)
        };
        return await CompleteAsync(messages, ct);
    }

    private async Task<AiSuggestion?> CompleteAsync(List<ChatMessage> messages, CancellationToken ct)
    {
        try
        {
            var response = await chat.GetResponseAsync(
                messages,
                new ChatOptions { ResponseFormat = ChatResponseFormat.Json, Temperature = 0.2f },
                ct);
            return Parse(response.Text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "AI enrichment call failed");
            return null;
        }
    }

    private AiSuggestion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Be lenient: pull the outermost JSON object in case the model wraps it in prose.
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        var json = text[start..(end + 1)];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var attributes = new Dictionary<string, string>();
            if (root.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in attrs.EnumerateObject())
                {
                    var value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
                    if (!string.IsNullOrWhiteSpace(value)) attributes[p.Name] = value!;
                }
            }
            return new AiSuggestion
            {
                Name = GetString(root, "name"),
                SuggestedKind = GetString(root, "kind"),
                Description = GetString(root, "description"),
                Attributes = attributes
            };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Could not parse AI response as JSON");
            return null;
        }
    }

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
