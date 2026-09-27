using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Stashographer.Data.Entities;

namespace Stashographer.Services.Automation;

[McpServerToolType]
public sealed class StashographerMcpTools
{
    [McpServerTool(Name = "search_inventory", UseStructuredContent = true)]
    [Description("Searches inventory, hiding not-in-stock records unless requested. Use this before proposing duplicates or substitutes.")]
    public static Task<IReadOnlyList<AutomationItem>> SearchInventoryAsync(
        AutomationOperations operations,
        [Description("Optional words from item name, code, description, or tag.")] string? search = null,
        [Description("Optional exact item-kind identifier.")] int? itemKindId = null,
        [Description("Optional location identifier, including its containers.")] int? locationId = null,
        [Description("Optional exact container identifier.")] int? containerId = null,
        [Description("Include retained items currently marked not in stock.")] bool includeOutOfStock = false,
        [Description("Maximum results from 1 to 200.")] int limit = 100,
        CancellationToken ct = default) =>
        operations.SearchInventoryAsync(
            search, itemKindId, locationId, containerId, includeOutOfStock, limit, ct);

    [McpServerTool(Name = "get_item", UseStructuredContent = true)]
    [Description("Gets one retained inventory item by identifier, including its current stock state.")]
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

    [McpServerTool(Name = "list_intake_history", UseStructuredContent = true)]
    [Description("Lists recent capture groups with every derived result, including entries still awaiting review.")]
    public static Task<IReadOnlyList<AutomationCaptureHistory>> ListIntakeHistoryAsync(
        AutomationOperations operations,
        [Description("Maximum capture groups from 1 to 100.")] int limit = 25,
        CancellationToken ct = default) => operations.ListIntakeHistoryAsync(limit, ct);

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

    [McpServerTool(Name = "retry_intake_item", UseStructuredContent = true)]
    [Description("Returns one failed intake entry to processing. Human review remains required.")]
    public static Task<AutomationQueueItem> RetryIntakeItemAsync(
        AutomationOperations operations,
        [Description("Failed intake queue identifier.")] int id,
        CancellationToken ct = default) => operations.RetryIntakeAsync(id, ct);

    [McpServerTool(Name = "rerun_intake_capture", UseStructuredContent = true)]
    [Description("Reruns a completed capture from its untouched original as a new reviewable capture group.")]
    public static Task<AutomationQueueItem> RerunIntakeCaptureAsync(
        AutomationOperations operations,
        [Description("Completed capture-group identifier from intake history.")] int captureGroupId,
        CancellationToken ct = default) => operations.RerunIntakeCaptureAsync(captureGroupId, ct);

    [McpServerTool(Name = "get_intake_undo_preview", UseStructuredContent = true)]
    [Description("Describes whether and how an accepted intake result could be undone. This does not apply the undo.")]
    public static Task<AutomationUndoPreview> GetIntakeUndoPreviewAsync(
        AutomationOperations operations,
        [Description("Accepted intake queue identifier.")] int id,
        CancellationToken ct = default) => operations.GetIntakeUndoPreviewAsync(id, ct);

    [McpServerTool(Name = "start_intake_session", UseStructuredContent = true)]
    [Description("Ends the current intake context window and starts a new session.")]
    public static Task<IntakeSession> StartIntakeSessionAsync(
        AutomationOperations operations,
        CancellationToken ct = default) => operations.StartIntakeSessionAsync(ct);

    [McpServerTool(Name = "get_working_place", UseStructuredContent = true)]
    [Description("Gets the current working location or container used to prioritize photo matching.")]
    public static Task<ModifyWorkingPlace?> GetWorkingPlaceAsync(
        AutomationOperations operations,
        CancellationToken ct = default) => operations.GetWorkingPlaceAsync(ct);

    [McpServerTool(Name = "set_working_place", UseStructuredContent = true)]
    [Description("Sets the current working location or container. Supply exactly one identifier.")]
    public static Task<ModifyWorkingPlace?> SetWorkingPlaceAsync(
        AutomationOperations operations,
        [Description("Location identifier, when working without a specific container.")] int? locationId = null,
        [Description("Container identifier; its parent location is inferred.")] int? containerId = null,
        CancellationToken ct = default) => operations.SetWorkingPlaceAsync(locationId, containerId, ct);

    [McpServerTool(Name = "clear_working_place", UseStructuredContent = true)]
    [Description("Clears the current working location and container context.")]
    public static Task<ModifyWorkingPlace?> ClearWorkingPlaceAsync(
        AutomationOperations operations,
        CancellationToken ct = default) => operations.SetWorkingPlaceAsync(null, null, ct);

    [McpServerTool(Name = "list_modify_queue", UseStructuredContent = true)]
    [Description("Lists photo reminders awaiting processing or human review in the Modify queue.")]
    public static Task<IReadOnlyList<AutomationModifyQueueItem>> ListModifyQueueAsync(
        AutomationOperations operations,
        CancellationToken ct = default) => operations.ListModifyQueueAsync(ct);

    [McpServerTool(Name = "get_modify_queue_item", UseStructuredContent = true)]
    [Description("Gets one Modify queue entry, its AI identification, candidate match, and failure details.")]
    public static Task<AutomationModifyQueueItem> GetModifyQueueItemAsync(
        AutomationOperations operations,
        [Description("Modify queue identifier.")] int id,
        CancellationToken ct = default) => operations.GetModifyQueueItemAsync(id, ct);

    [McpServerTool(Name = "list_processing_logs", UseStructuredContent = true)]
    [Description("Lists bounded, temporary intake, AI, image, and Modify processing logs from the current process.")]
    public static IReadOnlyList<AutomationProcessingLog> ListProcessingLogs(
        AutomationOperations operations,
        [Description("Minimum log severity.")] LogLevel minimumLevel = LogLevel.Warning,
        [Description("Maximum entries from 1 to 250.")] int limit = 100,
        [Description("Include exception details, which can contain local operational context.")] bool includeException = false) =>
        operations.ListProcessingLogs(minimumLevel, limit, includeException);

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
