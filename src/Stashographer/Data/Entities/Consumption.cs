namespace Stashographer.Data.Entities;

public enum ConsumptionKind
{
    Manual = 0,
    Meal = 1
}

/// <summary>A durable, reversible inventory consumption event.</summary>
public sealed class ConsumptionEvent
{
    public int Id { get; set; }
    public ConsumptionKind Kind { get; set; }
    public int? MealPlanEntryId { get; set; }
    public int? BomDefinitionId { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset ConsumedAt { get; set; }
    public DateTimeOffset? UndoneAt { get; set; }
    public string? MealPlanName { get; set; }
    public DateOnly? PlanDate { get; set; }
    public string? MealSlot { get; set; }
    public List<ConsumptionLine> Lines { get; set; } = [];

    public bool CanUndo => UndoneAt is null
                           && Lines.Count > 0
                           && Lines.All(line => line.ItemId is not null);
}

/// <summary>The exact stock lot and quantity consumed, retained for audit and safe undo.</summary>
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

public sealed record ConsumptionApplied(
    int EventId,
    ConsumptionKind Kind,
    int? MealPlanEntryId,
    string Description,
    IReadOnlyList<ConsumptionLine> Lines);
