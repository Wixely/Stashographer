using Stashographer.Data.Entities;

namespace Stashographer.Services.Ai;

/// <summary>Compact inventory context supplied to the agent while drafting a BOM.</summary>
public sealed record AiBomInventoryItem(
    int Id,
    string Name,
    int KindId,
    string? Kind,
    decimal Quantity,
    string? Unit,
    IReadOnlyDictionary<string, string> Attributes);

/// <summary>An unpersisted recipe/build draft that must be reviewed before acceptance.</summary>
public sealed class AiBomSuggestion
{
    public string Name { get; set; } = string.Empty;
    public BomKind Kind { get; set; }
    public string? Description { get; set; }
    public decimal OutputQuantity { get; set; } = 1;
    public string? OutputUnit { get; set; }
    public List<AiBomRequirementSuggestion> Requirements { get; set; } = new();
}

public sealed class AiBomRequirementSuggestion
{
    public string Name { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public string? Unit { get; set; }
    public bool IsOptional { get; set; }
    public int? MatchItemKindId { get; set; }
    public string? MatchText { get; set; }
    public Dictionary<string, string> RequiredAttributes { get; set; } = new();
}
