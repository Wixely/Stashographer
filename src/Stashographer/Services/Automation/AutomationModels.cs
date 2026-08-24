using Stashographer.Data.Entities;
using Stashographer.Services.Ai;

namespace Stashographer.Services.Automation;

public sealed record AutomationItem(
    int Id,
    string? CollectionKey,
    string? Code,
    string Name,
    string? Description,
    int ItemKindId,
    string? ItemKind,
    decimal Quantity,
    string? Unit,
    decimal LowStockThreshold,
    DateOnly? ExpiryDate,
    int? LocationId,
    string? Location,
    int? ContainerId,
    string? Container,
    int? ImageId,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyDictionary<string, SpecialAttributeValue> SpecialAttributes,
    string? Notes,
    bool IsCheckedOut,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AutomationContainer(
    int Id,
    string Name,
    ContainerType Type,
    string QrSlug);

public sealed record AutomationLocation(
    int Id,
    string Name,
    string? Description,
    IReadOnlyList<AutomationContainer> Containers);

public sealed record AutomationQueueItem(
    int Id,
    int SessionId,
    IntakeSourceType SourceType,
    string? SourceCode,
    int? ImageId,
    bool IsMultiPhoto,
    IntakeQueueStatus Status,
    AutomationItem Draft,
    ReceiptExtraction? Receipt,
    string? ProposalAction,
    int? MatchedItemId,
    string? MatchedItemName,
    int? MatchedQueueItemId,
    string? CaptureRelationship,
    string? RelationshipConfidence,
    string? RelationshipReason,
    ItemImageRole? SuggestedImageRole,
    decimal IncrementBy,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ProcessedAt);

public sealed record AutomationConsumptionLine(
    int Id,
    int? ItemId,
    string ItemName,
    decimal Quantity,
    string? Unit,
    DateOnly? ExpiryDate);

public sealed record AutomationConsumptionEvent(
    int Id,
    ConsumptionKind Kind,
    string Description,
    DateTimeOffset ConsumedAt,
    DateTimeOffset? UndoneAt,
    int? MealPlanEntryId,
    int? BomDefinitionId,
    string? MealPlanName,
    DateOnly? PlanDate,
    string? MealSlot,
    IReadOnlyList<AutomationConsumptionLine> Lines);

/// <summary>A proposed item that is always queued for final human review.</summary>
public sealed class ItemDraftRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string? Description { get; set; }
    public int ItemKindId { get; set; } = 7;
    public decimal Quantity { get; set; } = 1;
    public string? Unit { get; set; }
    public decimal LowStockThreshold { get; set; }
    public int? LocationId { get; set; }
    public int? ContainerId { get; set; }
    public Dictionary<string, string> Attributes { get; set; } = new();
    public string? Notes { get; set; }
    public decimal? PriceAmount { get; set; }
    public string? PriceCurrency { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public ExpiryDateKind ExpiryKind { get; set; }
}

public sealed record BarcodeIntakeRequest(string Code);

public sealed record AutomationIdentity(
    string Name,
    string Version,
    IReadOnlyList<string> Endpoints,
    string ReviewPolicy);
