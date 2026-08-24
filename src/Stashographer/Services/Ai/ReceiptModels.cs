namespace Stashographer.Services.Ai;

/// <summary>A recently captured item that may correspond to a receipt line.</summary>
public sealed record ReceiptMatchCandidate(
    int QueueItemId,
    int? InventoryItemId,
    string Name,
    string? Code,
    IReadOnlyDictionary<string, string> Attributes);

/// <summary>Reviewable data extracted from a receipt image.</summary>
public sealed class ReceiptExtraction
{
    public string? Merchant { get; set; }
    public DateOnly? PurchaseDate { get; set; }
    public string? Currency { get; set; }
    public decimal? Total { get; set; }
    public List<ReceiptLineSuggestion> Lines { get; set; } = [];
}

/// <summary>One extracted receipt line and its conservative proposed item match.</summary>
public sealed class ReceiptLineSuggestion
{
    public int LineIndex { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public decimal? UnitPrice { get; set; }
    public decimal? LineTotal { get; set; }
    public int? MatchedQueueItemId { get; set; }
    public int? MatchedItemId { get; set; }
    public MatchConfidence Confidence { get; set; }

    /// <summary>Only high-confidence matches start selected; every line remains user-reviewable.</summary>
    public bool Selected { get; set; }
}
