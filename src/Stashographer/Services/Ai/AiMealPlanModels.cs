namespace Stashographer.Services.Ai;

public sealed record AiMealPlanRecipe(
    int Id,
    string Name,
    string? Description,
    decimal OutputQuantity,
    string? OutputUnit,
    IReadOnlyList<AiMealPlanIngredient> Ingredients);

public sealed record AiMealPlanIngredient(
    string Name,
    decimal Quantity,
    string? Unit,
    bool IsOptional,
    IReadOnlyList<int> MatchingItemIds);

public sealed record AiMealPlanInventoryItem(
    int Id,
    string Name,
    decimal Quantity,
    string? Unit,
    DateOnly? ExpiryDate);

/// <summary>An unpersisted AI plan that must be reviewed before it becomes a meal plan.</summary>
public sealed class AiMealPlanSuggestion
{
    public string Name { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public List<AiMealPlanEntrySuggestion> Entries { get; set; } = [];
}

public sealed class AiMealPlanEntrySuggestion
{
    public DateOnly Date { get; set; }
    public string MealSlot { get; set; } = "Dinner";
    public int BomDefinitionId { get; set; }
    public decimal OutputQuantity { get; set; } = 1;
    public string? Reason { get; set; }
}
