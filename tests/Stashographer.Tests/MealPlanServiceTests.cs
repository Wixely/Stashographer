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
        var meals = new MealPlanService(db.Factory, boms, inventory, new ConsumptionService(db.Factory));
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

        Assert.Equal(ConsumptionKind.Meal, cooked.Kind);
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
        var meals = new MealPlanService(db.Factory, boms, inventory, new ConsumptionService(db.Factory));
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
        var meals = new MealPlanService(db.Factory, boms, inventory, new ConsumptionService(db.Factory));
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

    [Fact]
    public async Task Whole_plan_projection_reassigns_interchangeable_stock_to_avoid_a_false_conflict()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var boms = BomService(db, inventory);
        var meals = new MealPlanService(db.Factory, boms, inventory, new ConsumptionService(db.Factory));
        var earlySpecific = await inventory.SaveAsync(new Item
        {
            Name = "Specific yoghurt",
            ItemKindId = 1,
            Quantity = 1,
            ExpiryDate = new DateOnly(2026, 8, 25)
        });
        var laterFlexible = await inventory.SaveAsync(new Item
        {
            Name = "Flexible yoghurt",
            ItemKindId = 1,
            Quantity = 1,
            ExpiryDate = new DateOnly(2026, 8, 30)
        });
        var flexibleRecipe = await RecipeAsync(
            boms, "Flexible snack", 1, [earlySpecific.Id, laterFlexible.Id]);
        var specificRecipe = await RecipeAsync(
            boms, "Specific snack", 1, [earlySpecific.Id]);
        var plan = await meals.SaveReviewedAsync(new MealPlanDraft
        {
            Name = "Two snacks",
            StartDate = new DateOnly(2026, 8, 24),
            EndDate = new DateOnly(2026, 8, 25),
            Entries =
            [
                Entry(new DateOnly(2026, 8, 24), flexibleRecipe.Id),
                Entry(new DateOnly(2026, 8, 25), specificRecipe.Id)
            ]
        });

        var projection = Assert.Single(await meals.GetProjectionsAsync([plan]));

        Assert.True(projection.CanSupplyAll);
        Assert.Empty(projection.ShoppingList);
        var flexible = projection.Entries.Single(entry =>
            entry.MealPlanEntryId == plan.Entries[0].Id).Allocation!;
        var specific = projection.Entries.Single(entry =>
            entry.MealPlanEntryId == plan.Entries[1].Id).Allocation!;
        Assert.Equal(laterFlexible.Id, Assert.Single(flexible.Lines).ItemId);
        Assert.Equal(earlySpecific.Id, Assert.Single(specific.Lines).ItemId);
        Assert.Equal(1, (await inventory.GetAsync(earlySpecific.Id))!.Quantity);
        Assert.Equal(1, (await inventory.GetAsync(laterFlexible.Id))!.Quantity);

        await meals.CookAsync(plan.Entries[0].Id);
        Assert.Equal(1, (await inventory.GetAsync(earlySpecific.Id))!.Quantity);
        Assert.Equal(0, (await inventory.GetAsync(laterFlexible.Id))!.Quantity);
        await meals.CookAsync(plan.Entries[1].Id);
        Assert.Equal(0, (await inventory.GetAsync(earlySpecific.Id))!.Quantity);
    }

    [Fact]
    public async Task Whole_plan_projection_prioritizes_earlier_meals_and_aggregates_true_shopping_gap()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var boms = BomService(db, inventory);
        var meals = new MealPlanService(db.Factory, boms, inventory, new ConsumptionService(db.Factory));
        var beans = await inventory.SaveAsync(new Item
        {
            Name = "Beans",
            ItemKindId = 1,
            Quantity = 2,
            Unit = "each"
        });
        var recipe = await RecipeAsync(boms, "Bean dinner", 2, [beans.Id], "each", "Beans");
        var draft = new MealPlanDraft
        {
            Name = "Three dinners",
            StartDate = new DateOnly(2026, 8, 24),
            EndDate = new DateOnly(2026, 8, 26),
            Entries =
            [
                Entry(new DateOnly(2026, 8, 24), recipe.Id),
                Entry(new DateOnly(2026, 8, 25), recipe.Id),
                Entry(new DateOnly(2026, 8, 26), recipe.Id)
            ]
        };

        var draftProjection = await meals.GetDraftProjectionAsync(draft);

        Assert.False(draftProjection.CanSupplyAll);
        Assert.True(draftProjection.Entries[0].Allocation!.CanMake);
        Assert.False(draftProjection.Entries[1].Allocation!.CanMake);
        Assert.False(draftProjection.Entries[2].Allocation!.CanMake);
        var shopping = Assert.Single(draftProjection.ShoppingList);
        Assert.Equal("Beans", shopping.Name);
        Assert.Equal("each", shopping.Unit);
        Assert.Equal(4, shopping.Quantity);
        Assert.Equal(2, shopping.Needs.Count);
        Assert.All(shopping.Needs, need => Assert.Equal("Bean dinner", need.RecipeName));
        Assert.Equal(2, (await inventory.GetAsync(beans.Id))!.Quantity);

        var plan = await meals.SaveReviewedAsync(draft);
        await Assert.ThrowsAsync<InvalidOperationException>(() => meals.CookAsync(plan.Entries[1].Id));
        Assert.Equal(2, (await inventory.GetAsync(beans.Id))!.Quantity);
        await meals.CookAsync(plan.Entries[1].Id, prioritizeThisMeal: true);
        Assert.Equal(0, (await inventory.GetAsync(beans.Id))!.Quantity);
    }

    private static BomService BomService(TestDb db, InventoryService inventory) =>
        new(db.Factory, inventory, new AttributeNameService(db.Factory));

    private static async Task<BomDefinition> RecipeAsync(
        BomService boms,
        string name,
        decimal quantity,
        List<int> candidates,
        string? unit = null,
        string requirementName = "Ingredient")
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
            Name = requirementName,
            Quantity = quantity,
            Unit = unit,
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

    private static MealPlanEntryDraft Entry(DateOnly date, int recipeId) => new()
    {
        PlanDate = date,
        MealSlot = "Dinner",
        BomDefinitionId = recipeId,
        OutputQuantity = 1
    };
}
