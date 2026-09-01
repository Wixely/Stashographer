using Stashographer.Services.Ai;

namespace Stashographer.Data.Entities;

public enum ModifyQueueStatus
{
    Pending,
    Processing,
    ReadyForReview,
    Failed,
    Applied,
    Dismissed
}

public enum ModifyAction
{
    Decrement,
    Move,
    Delete,
    AttachImage
}

/// <summary>A durable reminder to identify an existing item and explicitly modify it later.</summary>
public sealed class ModifyQueueItem
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public int OriginalImageId { get; set; }
    public int ImageId { get; set; }
    public bool IsMultiPhoto { get; set; }
    public string? BrowserUploadToken { get; set; }
    public ModifyQueueStatus Status { get; set; }
    public VisionIdentification? Identification { get; set; }
    public int? MatchedItemId { get; set; }
    public string? MatchedItemName { get; set; }
    public MatchConfidence MatchConfidence { get; set; }
    public string? MatchReason { get; set; }
    public string? MatchedItemUpdatedAt { get; set; }
    public ModifyAction? AppliedAction { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProcessingStartedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
}

public sealed record ModifySession(
    int Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int? WorkingLocationId,
    int? WorkingContainerId);

public sealed record ModifyQueueCounts(
    int Waiting,
    int Processing,
    int Ready,
    int Failed,
    int Completed)
{
    public int Open => Waiting + Processing + Ready + Failed;
}

public sealed record ModifyActionRequest(
    ModifyAction Action,
    decimal Quantity = 1,
    int? LocationId = null,
    int? ContainerId = null,
    ItemImageRole ImageRole = ItemImageRole.Detail,
    string? Description = null,
    string? ExpectedItemUpdatedAt = null);

public sealed record ModifyApplied(
    int QueueItemId,
    ModifyAction Action,
    int ItemId,
    string ItemName,
    int? CreatedItemId = null,
    int? ConsumptionEventId = null);
