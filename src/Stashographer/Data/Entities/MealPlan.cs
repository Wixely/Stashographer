namespace Stashographer.Data.Entities;

public enum MealPlanEntryStatus
{
    Planned = 0,
    Cooked = 1
}

/// <summary>A persisted, user-reviewed set of recipe intentions.</summary>
public sealed class MealPlan
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string? Notes { get; set; }
    public List<MealPlanEntry> Entries { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A recipe scheduled for a date; recipe display fields are snapshotted for history.</summary>
public sealed class MealPlanEntry
{
    public int Id { get; set; }
    public int MealPlanId { get; set; }
    public DateOnly PlanDate { get; set; }
    public string MealSlot { get; set; } = "Dinner";
    public int? BomDefinitionId { get; set; }
    public string RecipeName { get; set; } = string.Empty;
    public decimal OutputQuantity { get; set; } = 1;
    public string? OutputUnit { get; set; }
    public string? Reason { get; set; }
    public MealPlanEntryStatus Status { get; set; }
    public DateTimeOffset? CookedAt { get; set; }
    public ConsumptionEvent? Consumption { get; set; }
}

/// <summary>Editable plan data that is not persisted until the user accepts it.</summary>
public sealed class MealPlanDraft
{
    public string Name { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string? Notes { get; set; }
    public List<MealPlanEntryDraft> Entries { get; set; } = [];
}

public sealed class MealPlanEntryDraft
{
    public DateOnly PlanDate { get; set; }
    public string MealSlot { get; set; } = "Dinner";
    public int BomDefinitionId { get; set; }
    public decimal OutputQuantity { get; set; } = 1;
    public string? Reason { get; set; }
}

/// <summary>A deterministic, non-reserving stock projection for every planned meal.</summary>
public sealed record MealPlanProjection(
    int MealPlanId,
    IReadOnlyList<MealPlanEntryProjection> Entries,
    IReadOnlyList<MealPlanShoppingLine> ShoppingList)
{
    public bool CanSupplyAll => Entries.All(entry => entry.Allocation?.CanMake == true);
    public int UnavailableRecipeCount => Entries.Count(entry => entry.Allocation is null);
}

public sealed record MealPlanEntryProjection(
    int MealPlanEntryId,
    BomAllocation? Allocation);

/// <summary>An aggregated ingredient gap, with its contributing meals retained for review.</summary>
public sealed record MealPlanShoppingLine(
    string Name,
    decimal Quantity,
    string? Unit,
    IReadOnlyList<MealPlanShoppingNeed> Needs);

public sealed record MealPlanShoppingNeed(
    int MealPlanEntryId,
    DateOnly PlanDate,
    string RecipeName,
    decimal Quantity);
