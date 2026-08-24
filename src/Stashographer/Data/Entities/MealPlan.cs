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

/// <summary>An explicit inventory mutation produced when a reviewed meal is marked cooked.</summary>
public sealed class ConsumptionEvent
{
    public int Id { get; set; }
    public int? MealPlanEntryId { get; set; }
    public int? BomDefinitionId { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset ConsumedAt { get; set; }
    public DateTimeOffset? UndoneAt { get; set; }
    public List<ConsumptionLine> Lines { get; set; } = [];
}

/// <summary>The exact stock lot and quantity consumed, retained so the event can be undone.</summary>
public sealed class ConsumptionLine
{
    public int Id { get; set; }
    public int ConsumptionEventId { get; set; }
    public int? ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string? Unit { get; set; }
    public DateOnly? ExpiryDate { get; set; }
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
