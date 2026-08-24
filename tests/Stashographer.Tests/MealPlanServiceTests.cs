using Stashographer.Data.Entities;
using Stashographer.Services.Inventory;

namespace Stashographer.Tests;

public class MealPlanServiceTests
{
    [Fact]
    public async Task Reviewed_plan_is_inert_until_cooked_then_consumes_exact_lots_and_can_be_undone()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var boms = BomService(db, inventory);
        var meals = new MealPlanService(db.Factory, boms);
        var early = await inventory.SaveAsync(new Item
        {
            Name = "Early beans",
            ItemKindId = 1,
            Quantity = 2,
            Unit = "each",
            ExpiryDate = new DateOnly(2026, 8, 25)
        });
        var later = await inventory.SaveAsync(new Item
        {
            Name = "Later beans",
            ItemKindId = 1,
            Quantity = 3,
            Unit = "each",
            ExpiryDate = new DateOnly(2026, 9, 2)
        });
        var recipe = await RecipeAsync(boms, "Bean dinner", 3, [early.Id, later.Id]);

        var plan = await meals.SaveReviewedAsync(new MealPlanDraft
        {
            Name = "Use soon",
            StartDate = new DateOnly(2026, 8, 24),
            EndDate = new DateOnly(2026, 8, 24),
            Entries =
            [
                new MealPlanEntryDraft
                {
                    PlanDate = new DateOnly(2026, 8, 24),
                    MealSlot = "Dinner",
                    BomDefinitionId = recipe.Id,
                    OutputQuantity = 1
                }
            ]
        });

        Assert.Equal(2, (await inventory.GetAsync(early.Id))!.Quantity);
        Assert.Equal(3, (await inventory.GetAsync(later.Id))!.Quantity);

        var cooked = await meals.CookAsync(Assert.Single(plan.Entries).Id);

        Assert.Equal(0, (await inventory.GetAsync(early.Id))!.Quantity);
        Assert.Equal(2, (await inventory.GetAsync(later.Id))!.Quantity);
        Assert.Equal(2, cooked.Lines.Count);
        Assert.Equal(2, cooked.Lines.Single(line => line.ItemId == early.Id).Quantity);
        Assert.Equal(1, cooked.Lines.Single(line => line.ItemId == later.Id).Quantity);
        var loadedEntry = Assert.Single(Assert.Single(await meals.GetAllAsync()).Entries);
        Assert.Equal(MealPlanEntryStatus.Cooked, loadedEntry.Status);
        Assert.NotNull(loadedEntry.Consumption);
        await Assert.ThrowsAsync<InvalidOperationException>(() => meals.CookAsync(loadedEntry.Id));
        Assert.Equal(0, (await inventory.GetAsync(early.Id))!.Quantity);
        Assert.Equal(2, (await inventory.GetAsync(later.Id))!.Quantity);

        await meals.UndoAsync(cooked.EventId);

        Assert.Equal(2, (await inventory.GetAsync(early.Id))!.Quantity);
        Assert.Equal(3, (await inventory.GetAsync(later.Id))!.Quantity);
        loadedEntry = Assert.Single(Assert.Single(await meals.GetAllAsync()).Entries);
        Assert.Equal(MealPlanEntryStatus.Planned, loadedEntry.Status);
        Assert.Null(loadedEntry.Consumption);
        await Assert.ThrowsAsync<InvalidOperationException>(() => meals.UndoAsync(cooked.EventId));
        Assert.Equal(2, (await inventory.GetAsync(early.Id))!.Quantity);
        Assert.Equal(3, (await inventory.GetAsync(later.Id))!.Quantity);

        var cookedAgain = await meals.CookAsync(loadedEntry.Id);
        Assert.NotEqual(cooked.EventId, cookedAgain.EventId);
    }

    [Fact]
    public async Task Cook_refuses_insufficient_stock_without_changing_any_lot()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var boms = BomService(db, inventory);
        var meals = new MealPlanService(db.Factory, boms);
        var first = await inventory.SaveAsync(new Item
        {
            Name = "First tin",
            ItemKindId = 1,
            Quantity = 1
        });
        var second = await inventory.SaveAsync(new Item
        {
            Name = "Second tin",
            ItemKindId = 1,
            Quantity = 1
        });
        var recipe = await RecipeAsync(boms, "Large casserole", 3, [first.Id, second.Id]);
        var plan = await meals.SaveReviewedAsync(Draft(recipe.Id));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            meals.CookAsync(Assert.Single(plan.Entries).Id));

        Assert.Contains("not enough inventory", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, (await inventory.GetAsync(first.Id))!.Quantity);
        Assert.Equal(1, (await inventory.GetAsync(second.Id))!.Quantity);
    }

    [Fact]
    public async Task Cooked_plan_must_be_undone_before_deletion()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var boms = BomService(db, inventory);
        var meals = new MealPlanService(db.Factory, boms);
        var item = await inventory.SaveAsync(new Item
        {
            Name = "Dinner tin",
            ItemKindId = 1,
            Quantity = 1
        });
        var recipe = await RecipeAsync(boms, "Dinner", 1, [item.Id]);
        var plan = await meals.SaveReviewedAsync(Draft(recipe.Id));
        var cooked = await meals.CookAsync(Assert.Single(plan.Entries).Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => meals.DeletePlanAsync(plan.Id));

        await meals.UndoAsync(cooked.EventId);
        await meals.DeletePlanAsync(plan.Id);
        Assert.Empty(await meals.GetAllAsync());
    }

    private static BomService BomService(TestDb db, InventoryService inventory) =>
        new(db.Factory, inventory, new AttributeNameService(db.Factory));

    private static async Task<BomDefinition> RecipeAsync(
        BomService boms, string name, decimal quantity, List<int> candidates)
    {
        var recipe = await boms.SaveDefinitionAsync(new BomDefinition
        {
            Name = name,
            Kind = BomKind.Recipe,
            OutputQuantity = 1,
            OutputUnit = "serving"
        });
        await boms.SaveRequirementAsync(new BomRequirement
        {
            BomDefinitionId = recipe.Id,
            Name = "Ingredient",
            Quantity = quantity,
            MatchMode = BomMatchMode.ExplicitCandidates,
            CandidateItemIds = candidates
        });
        return recipe;
    }

    private static MealPlanDraft Draft(int recipeId) => new()
    {
        Name = "Dinner plan",
        StartDate = new DateOnly(2026, 8, 24),
        EndDate = new DateOnly(2026, 8, 24),
        Entries =
        [
            new MealPlanEntryDraft
            {
                PlanDate = new DateOnly(2026, 8, 24),
                MealSlot = "Dinner",
                BomDefinitionId = recipeId,
                OutputQuantity = 1
            }
        ]
    };
}
