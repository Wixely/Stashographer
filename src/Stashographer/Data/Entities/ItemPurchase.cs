using Stashographer.Services.Ai;

namespace Stashographer.Data.Entities;

/// <summary>
/// Purchase evidence for an inventory lot. It records receipt provenance without changing
/// the lot's current quantity or treating a receipt line as another inventory capture.
/// </summary>
public sealed class ItemPurchase
{
    public int Id { get; set; }
    public int QueueItemId { get; set; }
    public int ReceiptLineIndex { get; set; }
    public int ItemId { get; set; }
    public int ImageId { get; set; }
    public string? Merchant { get; set; }
    public DateOnly? PurchasedOn { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public decimal? UnitPrice { get; set; }
    public string? Currency { get; set; }
    public decimal? LineTotal { get; set; }
    public MatchConfidence? Confidence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
