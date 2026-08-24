using System.ComponentModel;
using ModelContextProtocol.Server;
using Stashographer.Data.Entities;

namespace Stashographer.Services.Automation;

[McpServerToolType]
public sealed class StashographerMcpTools
{
    [McpServerTool(Name = "search_inventory", UseStructuredContent = true)]
    [Description("Searches current inventory. Use this before proposing duplicates or substitutes.")]
    public static Task<IReadOnlyList<AutomationItem>> SearchInventoryAsync(
        AutomationOperations operations,
        [Description("Optional words from item name, code, description, or tag.")] string? search = null,
        [Description("Optional exact item-kind identifier.")] int? itemKindId = null,
        [Description("Optional location identifier, including its containers.")] int? locationId = null,
        [Description("Optional exact container identifier.")] int? containerId = null,
        [Description("Maximum results from 1 to 200.")] int limit = 100,
        CancellationToken ct = default) =>
        operations.SearchInventoryAsync(search, itemKindId, locationId, containerId, limit, ct);

    [McpServerTool(Name = "get_item", UseStructuredContent = true)]
    [Description("Gets one current inventory item by identifier.")]
    public static Task<AutomationItem> GetItemAsync(
        AutomationOperations operations,
        [Description("Inventory item identifier.")] int id,
        CancellationToken ct = default) => operations.GetItemAsync(id, ct);

    [McpServerTool(Name = "list_item_kinds", UseStructuredContent = true)]
    [Description("Lists valid item kinds and their known attribute vocabulary.")]
    public static Task<List<ItemKind>> ListItemKindsAsync(
        AutomationOperations operations,
        CancellationToken ct = default) => operations.ListItemKindsAsync(ct);

    [McpServerTool(Name = "list_tags", UseStructuredContent = true)]
    [Description("Lists reusable inventory tags and the number of items assigned to each one.")]
    public static Task<List<Tag>> ListTagsAsync(
        AutomationOperations operations,
        CancellationToken ct = default) => operations.ListTagsAsync(ct);

    [McpServerTool(Name = "list_places", UseStructuredContent = true)]
    [Description("Lists valid locations and their containers for placing an intake draft.")]
    public static Task<IReadOnlyList<AutomationLocation>> ListPlacesAsync(
        AutomationOperations operations,
        CancellationToken ct = default) => operations.ListPlacesAsync(ct);

    [McpServerTool(Name = "list_intake_queue", UseStructuredContent = true)]
    [Description("Lists captures and automation drafts still awaiting processing or human review.")]
    public static Task<IReadOnlyList<AutomationQueueItem>> ListIntakeQueueAsync(
        AutomationOperations operations,
        CancellationToken ct = default) => operations.ListIntakeQueueAsync(ct);

    [McpServerTool(Name = "get_intake_item", UseStructuredContent = true)]
    [Description("Gets one intake entry and its current draft for contextual enrichment.")]
    public static Task<AutomationQueueItem> GetIntakeItemAsync(
        AutomationOperations operations,
        [Description("Intake queue identifier.")] int id,
        CancellationToken ct = default) => operations.GetIntakeItemAsync(id, ct);

    [McpServerTool(Name = "queue_barcode", UseStructuredContent = true)]
    [Description("Queues a barcode or ISBN for ordered lookup and final human review.")]
    public static Task<AutomationQueueItem> QueueBarcodeAsync(
        AutomationOperations operations,
        [Description("Barcode, ISBN, or other scanned code.")] string code,
        CancellationToken ct = default) => operations.QueueBarcodeAsync(code, ct);

    [McpServerTool(Name = "queue_item_draft", UseStructuredContent = true)]
    [Description("Proposes an item as a reviewable intake draft. This never accepts it into inventory.")]
    public static Task<AutomationQueueItem> QueueItemDraftAsync(
        AutomationOperations operations,
        [Description("Complete proposed item fields for human review.")] ItemDraftRequest item,
        CancellationToken ct = default) => operations.QueueItemDraftAsync(item, ct);

    [McpServerTool(Name = "update_intake_draft", UseStructuredContent = true)]
    [Description("Refines a pending intake draft while preserving its source image. Human acceptance is still required.")]
    public static Task<AutomationQueueItem> UpdateIntakeDraftAsync(
        AutomationOperations operations,
        [Description("Intake queue identifier.")] int id,
        [Description("Complete replacement draft fields.")] ItemDraftRequest item,
        CancellationToken ct = default) => operations.UpdateIntakeDraftAsync(id, item, ct);

    [McpServerTool(Name = "start_intake_session", UseStructuredContent = true)]
    [Description("Ends the current intake context window and starts a new session.")]
    public static Task<IntakeSession> StartIntakeSessionAsync(
        AutomationOperations operations,
        CancellationToken ct = default) => operations.StartIntakeSessionAsync(ct);

    [McpServerTool(Name = "list_consumption_history", UseStructuredContent = true)]
    [Description("Lists read-only inventory use history with exact consumed stock lots and quantities.")]
    public static Task<IReadOnlyList<AutomationConsumptionEvent>> ListConsumptionHistoryAsync(
        AutomationOperations operations,
        [Description("Optional words from the event description or consumed item name.")] string? search = null,
        [Description("Optional inventory lot identifier.")] int? itemId = null,
        [Description("Optional event source: Manual or Meal.")] ConsumptionKind? kind = null,
        [Description("Include events that were undone.")] bool includeUndone = false,
        [Description("Maximum results from 1 to 500.")] int limit = 100,
        CancellationToken ct = default) =>
        operations.ListConsumptionAsync(search, itemId, kind, includeUndone, limit: limit, ct: ct);
}
