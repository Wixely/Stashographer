namespace Stashographer.Data.Entities;

public enum BomKind
{
    Recipe = 0,
    Build = 1,
    Other = 2
}

public enum BomMatchMode
{
    Generic = 0,
    ExplicitCandidates = 1
}

/// <summary>A reusable recipe, hardware build, or other bill of materials.</summary>
public sealed class BomDefinition
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public BomKind Kind { get; set; }
    public string? Description { get; set; }
    public decimal OutputQuantity { get; set; } = 1;
    public string? OutputUnit { get; set; }
    public int RequirementCount { get; set; }
    public List<BomRequirement> Requirements { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One quantity needed by a BOM. With explicit candidates, any listed inventory entry can
/// satisfy it. Otherwise kind, text, and attribute selectors are combined deterministically.
/// Unspecified attributes such as Brand therefore do not restrict substitution.
/// </summary>
public sealed class BomRequirement
{
    public int Id { get; set; }
    public int BomDefinitionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public string? Unit { get; set; }
    public bool IsOptional { get; set; }
    public BomMatchMode MatchMode { get; set; }
    public int? MatchItemKindId { get; set; }
    public string? MatchText { get; set; }
    public Dictionary<string, string> RequiredAttributes { get; set; } = new();
    public List<int> CandidateItemIds { get; set; } = new();
    public int SortOrder { get; set; }
}

public sealed record BomRequirementAvailability(
    BomRequirement Requirement,
    IReadOnlyList<Item> MatchingItems,
    decimal AvailableQuantity,
    bool IsSatisfied);

public sealed record BomEvaluation(
    BomDefinition Definition,
    IReadOnlyList<BomRequirementAvailability> Requirements,
    bool CanMakeOne)
{
    public int SatisfiedRequiredCount => Requirements.Count(x => !x.Requirement.IsOptional && x.IsSatisfied);
    public int RequiredCount => Requirements.Count(x => !x.Requirement.IsOptional);
}
