using Stashographer.Services.Ai;

namespace Stashographer.Data.Entities;

public enum IntakeSourceType
{
    Barcode,
    Photo,
    Manual,
    Receipt
}

public enum IntakeQueueStatus
{
    Pending,
    Processing,
    ReadyForReview,
    Failed,
    Accepted,
    Rejected
}

/// <summary>A capture persisted in the intake queue before enrichment begins.</summary>
public class IntakeQueueItem
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public IntakeSourceType SourceType { get; set; }
    /// <summary>True when a person explicitly chose the source type, so AI must not change it.</summary>
    public bool SourceTypeOverride { get; set; }
    public string? SourceCode { get; set; }
    public int CaptureQuantity { get; set; } = 1;
    public DateTimeOffset? LiveCaptureHoldUntil { get; set; }
    public string? BrowserUploadToken { get; set; }
    public int? ImageId { get; set; }
    public bool IsMultiPhoto { get; set; }
    public IntakeQueueStatus Status { get; set; }
    public Item Draft { get; set; } = new() { Name = string.Empty, ItemKindId = 7 };
    public ReceiptExtraction? Receipt { get; set; }
    public IntakeAction? ProposalAction { get; set; }
    public int? MatchedItemId { get; set; }
    public string? MatchedItemName { get; set; }
    public int? MatchedQueueItemId { get; set; }
    public CaptureRelationship? CaptureRelationship { get; set; }
    public MatchConfidence? RelationshipConfidence { get; set; }
    public string? RelationshipReason { get; set; }
    public ItemImageRole? SuggestedImageRole { get; set; }
    public decimal IncrementBy { get; set; } = 1;
    public int? AppliedItemId { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProcessingStartedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
}

public record IntakeSession(int Id, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);
