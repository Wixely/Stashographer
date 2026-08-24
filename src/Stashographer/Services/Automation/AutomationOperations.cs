using Stashographer.Data.Entities;
using Stashographer.Services.Config;
using Stashographer.Services.Intake;
using Stashographer.Services.Inventory;

namespace Stashographer.Services.Automation;

/// <summary>
/// Authoritative application operations shared by HTTP and MCP. Automation may propose
/// intake drafts, but only the interactive review workflow can accept them into inventory.
/// </summary>
public sealed class AutomationOperations(
    InventoryService inventory,
    ContainerService places,
    IntakeQueueService intake,
    SettingsService settings,
    ConsumptionService consumption)
{
    public async Task<IReadOnlyList<AutomationItem>> SearchInventoryAsync(
        string? search = null,
        int? itemKindId = null,
        int? locationId = null,
        int? containerId = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        var items = await inventory.QueryAsync(new ItemQuery(
            Search: Clean(search),
            IncludeKindIds: itemKindId is null ? null : [itemKindId.Value],
            LocationId: locationId,
            ContainerId: containerId), ct);
        return items.Take(Math.Clamp(limit, 1, 200)).Select(ToAutomationItem).ToList();
    }

    public async Task<AutomationItem> GetItemAsync(int id, CancellationToken ct = default)
    {
        var item = await inventory.GetAsync(id, ct)
            ?? throw new KeyNotFoundException("The inventory item does not exist.");
        return ToAutomationItem(item);
    }

    public Task<List<ItemKind>> ListItemKindsAsync(CancellationToken ct = default) =>
        inventory.GetKindsAsync(ct);

    public async Task<IReadOnlyList<AutomationLocation>> ListPlacesAsync(CancellationToken ct = default) =>
        (await places.GetLocationsAsync(ct)).Select(location => new AutomationLocation(
            location.Id,
            location.Name,
            location.Description,
            location.Containers.Select(container => new AutomationContainer(
                container.Id,
                container.Name,
                container.ContainerType,
                container.QrSlug)).ToList())).ToList();

    public async Task<IReadOnlyList<AutomationQueueItem>> ListIntakeQueueAsync(
        CancellationToken ct = default) =>
        (await intake.GetOpenAsync(ct)).Select(ToAutomationQueueItem).ToList();

    public async Task<AutomationQueueItem> GetIntakeItemAsync(int id, CancellationToken ct = default)
    {
        var queued = await intake.GetAsync(id, ct)
            ?? throw new KeyNotFoundException("The intake item does not exist.");
        return ToAutomationQueueItem(queued);
    }

    public async Task<AutomationQueueItem> QueueBarcodeAsync(
        string code,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("A barcode or other scanned code is required.", nameof(code));
        return ToAutomationQueueItem(await intake.EnqueueBarcodeAsync(code, ct));
    }

    public async Task<AutomationQueueItem> QueuePhotoAsync(
        Stream content,
        string mediaType,
        string? originalName,
        bool multipleItems = true,
        CancellationToken ct = default) =>
        ToAutomationQueueItem(await intake.EnqueuePhotoAsync(
            content, mediaType, originalName, multipleItems, ct));

    public async Task<AutomationQueueItem> QueueReceiptAsync(
        Stream content,
        string mediaType,
        string? originalName,
        CancellationToken ct = default) =>
        ToAutomationQueueItem(await intake.EnqueueReceiptAsync(
            content, mediaType, originalName, ct));

    public async Task<AutomationQueueItem> QueueItemDraftAsync(
        ItemDraftRequest request,
        CancellationToken ct = default)
    {
        var draft = await BuildDraftAsync(request, null, ct);
        return ToAutomationQueueItem(await intake.EnqueueDraftAsync(draft, ct));
    }

    public async Task<AutomationQueueItem> UpdateIntakeDraftAsync(
        int id,
        ItemDraftRequest request,
        CancellationToken ct = default)
    {
        var queued = await intake.GetAsync(id, ct)
            ?? throw new KeyNotFoundException("The intake item does not exist.");
        if (queued.Status is IntakeQueueStatus.Accepted or IntakeQueueStatus.Rejected)
            throw new InvalidOperationException("The intake item has already been reviewed.");

        var draft = await BuildDraftAsync(request, queued.Draft, ct);
        await intake.UpdateDraftAsync(id, draft, ct);
        return ToAutomationQueueItem((await intake.GetAsync(id, ct))!);
    }

    public Task<IntakeSession> StartIntakeSessionAsync(CancellationToken ct = default) =>
        intake.StartNewSessionAsync(ct);

    public async Task<IReadOnlyList<AutomationConsumptionEvent>> ListConsumptionAsync(
        string? search = null,
        int? itemId = null,
        ConsumptionKind? kind = null,
        bool includeUndone = false,
        DateTimeOffset? consumedFrom = null,
        DateTimeOffset? consumedBefore = null,
        int limit = 100,
        CancellationToken ct = default) =>
        (await consumption.GetHistoryAsync(new ConsumptionHistoryQuery(
            Search: search,
            ItemId: itemId,
            Kind: kind,
            IncludeUndone: includeUndone,
            ConsumedFrom: consumedFrom,
            ConsumedBefore: consumedBefore,
            Limit: limit), ct)).Select(ToAutomationConsumptionEvent).ToList();

    private async Task<Item> BuildDraftAsync(
        ItemDraftRequest request,
        Item? existing,
        CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0) throw new ArgumentException("An item name is required.", nameof(request));
        if (request.Quantity <= 0)
            throw new ArgumentException("Quantity must be greater than zero.", nameof(request));
        if (request.LowStockThreshold < 0)
            throw new ArgumentException("Low-stock threshold cannot be negative.", nameof(request));
        if (request.LocationId is not null && request.ContainerId is not null)
            throw new ArgumentException("Choose either a location or a container, not both.", nameof(request));

        var kinds = await inventory.GetKindsAsync(ct);
        if (kinds.All(kind => kind.Id != request.ItemKindId))
            throw new ArgumentException("The item kind does not exist.", nameof(request));
        var locations = await places.GetLocationsAsync(ct);
        if (request.LocationId is { } locationId && locations.All(location => location.Id != locationId))
            throw new ArgumentException("The location does not exist.", nameof(request));
        if (request.ContainerId is { } containerId
            && locations.SelectMany(location => location.Containers).All(container => container.Id != containerId))
            throw new ArgumentException("The container does not exist.", nameof(request));

        var draft = new Item
        {
            Name = name,
            Code = Clean(request.Code),
            Description = Clean(request.Description),
            ItemKindId = request.ItemKindId,
            Quantity = request.Quantity,
            Unit = Clean(request.Unit),
            LowStockThreshold = request.LowStockThreshold,
            LocationId = request.LocationId,
            ContainerId = request.ContainerId,
            Attributes = (request.Attributes ?? new())
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase),
            Notes = Clean(request.Notes),
            ImageId = existing?.ImageId,
            ThumbnailUrl = existing?.ThumbnailUrl,
            PhotoPath = existing?.PhotoPath,
            ExpiryDate = existing?.ExpiryDate,
            SpecialAttributes = existing is null ? new() : new(existing.SpecialAttributes)
        };

        if (request.PriceAmount is { } amount)
        {
            var currency = Clean(request.PriceCurrency) ?? await settings.GetDefaultCurrencyAsync(ct);
            SpecialAttributeCatalog.SetPrice(draft, amount, currency, new SpecialAttributeEvidence
            {
                Source = "automation"
            });
        }
        if (request.ExpiryDate is { } expiry)
        {
            SpecialAttributeCatalog.SetExpiry(draft, expiry, request.ExpiryKind, new SpecialAttributeEvidence
            {
                Source = "automation"
            });
        }
        return draft;
    }

    private static AutomationQueueItem ToAutomationQueueItem(IntakeQueueItem item) => new(
        item.Id,
        item.SessionId,
        item.SourceType,
        item.SourceCode,
        item.ImageId,
        item.IsMultiPhoto,
        item.Status,
        ToAutomationItem(item.Draft),
        item.Receipt,
        item.ProposalAction?.ToString(),
        item.MatchedItemId,
        item.MatchedItemName,
        item.MatchedQueueItemId,
        item.CaptureRelationship?.ToString(),
        item.RelationshipConfidence?.ToString(),
        item.RelationshipReason,
        item.SuggestedImageRole,
        item.IncrementBy,
        item.Error,
        item.CreatedAt,
        item.ProcessedAt);

    private static AutomationItem ToAutomationItem(Item item) => new(
        item.Id,
        item.CollectionKey,
        item.Code,
        item.Name,
        item.Description,
        item.ItemKindId,
        item.Kind?.Name,
        item.Quantity,
        item.Unit,
        item.LowStockThreshold,
        item.ExpiryDate,
        item.LocationId,
        item.Location?.Name,
        item.ContainerId,
        item.Container?.Name,
        item.ImageId,
        item.Attributes,
        item.SpecialAttributes,
        item.Notes,
        item.IsCheckedOut,
        item.CreatedAt,
        item.UpdatedAt);

    private static AutomationConsumptionEvent ToAutomationConsumptionEvent(ConsumptionEvent consumption) => new(
        consumption.Id,
        consumption.Kind,
        consumption.Description,
        consumption.ConsumedAt,
        consumption.UndoneAt,
        consumption.MealPlanEntryId,
        consumption.BomDefinitionId,
        consumption.MealPlanName,
        consumption.PlanDate,
        consumption.MealSlot,
        consumption.Lines.Select(line => new AutomationConsumptionLine(
            line.Id,
            line.ItemId,
            line.ItemName,
            line.Quantity,
            line.Unit,
            line.ExpiryDate)).ToList());

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
