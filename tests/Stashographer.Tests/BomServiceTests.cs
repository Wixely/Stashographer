using Stashographer.Data.Entities;
using Stashographer.Services.Inventory;

namespace Stashographer.Tests;

public class BomServiceTests
{
    private static BomService Service(TestDb db, InventoryService inventory) =>
        new(db.Factory, inventory, new AttributeNameService(db.Factory));

    [Fact]
    public async Task Definition_and_requirements_roundtrip_with_candidates_and_attributes()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var service = Service(db, inventory);
        var milk = await inventory.SaveAsync(new Item { Name = "Whole milk", ItemKindId = 1 });
        var recipe = await service.SaveDefinitionAsync(new BomDefinition
        {
            Name = "Pancakes",
            Kind = BomKind.Recipe,
            OutputQuantity = 8,
            OutputUnit = "pancakes"
        });
        await service.SaveRequirementAsync(new BomRequirement
        {
            BomDefinitionId = recipe.Id,
            Name = "Milk",
            Quantity = 200,
            Unit = "ml",
            MatchItemKindId = 1,
            MatchText = "milk",
            RequiredAttributes = new() { ["Brand"] = "Example" },
            MatchMode = BomMatchMode.ExplicitCandidates,
            CandidateItemIds = [milk.Id]
        });

        var loaded = await service.GetAsync(recipe.Id);

        Assert.NotNull(loaded);
        Assert.Equal(BomKind.Recipe, loaded!.Kind);
        Assert.Equal(8, loaded.OutputQuantity);
        var requirement = Assert.Single(loaded.Requirements);
        Assert.Equal(200, requirement.Quantity);
        Assert.Equal("Example", requirement.RequiredAttributes["Brand"]);
        Assert.Equal(BomMatchMode.ExplicitCandidates, requirement.MatchMode);
        Assert.Equal([milk.Id], requirement.CandidateItemIds);
    }

    [Fact]
    public async Task Generic_requirement_matches_kind_text_and_attributes_without_requiring_brand()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var service = Service(db, inventory);
        await inventory.SaveAsync(new Item
        {
            Name = "Corsair Vengeance",
            ItemKindId = 4,
            Quantity = 1,
            Attributes = new() { ["Component type"] = "Memory", ["Brand"] = "Corsair" }
        });
        await inventory.SaveAsync(new Item
        {
            Name = "Kingston Fury",
            ItemKindId = 4,
            Quantity = 1,
            Attributes = new() { ["Component type"] = "Memory", ["Brand"] = "Kingston" }
        });
        await inventory.SaveAsync(new Item
        {
            Name = "Graphics card",
            ItemKindId = 4,
            Quantity = 1,
            Attributes = new() { ["Component type"] = "GPU" }
        });
        var build = await service.SaveDefinitionAsync(new BomDefinition { Name = "Workstation", Kind = BomKind.Build });
        await service.SaveRequirementAsync(new BomRequirement
        {
            BomDefinitionId = build.Id,
            Name = "RAM sticks",
            Quantity = 2,
            Unit = "each",
            MatchItemKindId = 4,
            MatchText = "memory",
            RequiredAttributes = new() { ["Component type"] = "Memory" }
        });

        var evaluation = await service.EvaluateAsync(build.Id);

        Assert.NotNull(evaluation);
        var availability = Assert.Single(evaluation!.Requirements);
        Assert.Equal(2, availability.MatchingItems.Count);
        Assert.DoesNotContain(availability.MatchingItems, item => item.Name == "Graphics card");
        Assert.True(availability.IsSatisfied);
        Assert.True(evaluation.CanMakeOne);
    }

    [Fact]
    public async Task Explicit_mode_does_not_become_generic_when_its_last_candidate_is_deleted()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var service = Service(db, inventory);
        var allowed = await inventory.SaveAsync(new Item { Name = "Allowed milk", ItemKindId = 1 });
        await inventory.SaveAsync(new Item { Name = "Other milk", ItemKindId = 1 });
        var recipe = await service.SaveDefinitionAsync(new BomDefinition { Name = "Drink", Kind = BomKind.Recipe });
        await service.SaveRequirementAsync(new BomRequirement
        {
            BomDefinitionId = recipe.Id,
            Name = "Milk",
            MatchText = "milk",
            MatchMode = BomMatchMode.ExplicitCandidates,
            CandidateItemIds = [allowed.Id]
        });

        await inventory.DeleteAsync(allowed.Id);
        var evaluation = await service.EvaluateAsync(recipe.Id);

        var availability = Assert.Single(evaluation!.Requirements);
        Assert.Equal(BomMatchMode.ExplicitCandidates, availability.Requirement.MatchMode);
        Assert.Empty(availability.Requirement.CandidateItemIds);
        Assert.Empty(availability.MatchingItems);
        Assert.False(evaluation.CanMakeOne);
    }

    [Fact]
    public async Task Allocation_does_not_double_count_one_item_across_interchangeable_requirements()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var service = Service(db, inventory);
        var a = await inventory.SaveAsync(new Item { Name = "A", ItemKindId = 7, Quantity = 1 });
        var b = await inventory.SaveAsync(new Item { Name = "B", ItemKindId = 7, Quantity = 1 });
        var build = await service.SaveDefinitionAsync(new BomDefinition { Name = "Allocation test", Kind = BomKind.Build });
        await service.SaveRequirementAsync(new BomRequirement
        {
            BomDefinitionId = build.Id,
            Name = "Flexible",
            MatchMode = BomMatchMode.ExplicitCandidates,
            CandidateItemIds = [a.Id, b.Id]
        });
        var fixedRequirement = await service.SaveRequirementAsync(new BomRequirement
        {
            BomDefinitionId = build.Id,
            Name = "Only A",
            MatchMode = BomMatchMode.ExplicitCandidates,
            CandidateItemIds = [a.Id]
        });

        Assert.True((await service.EvaluateAsync(build.Id))!.CanMakeOne);

        fixedRequirement.Quantity = 2;
        await service.SaveRequirementAsync(fixedRequirement);
        var unavailable = await service.EvaluateAsync(build.Id);
        Assert.False(unavailable!.CanMakeOne);
        Assert.False(unavailable.Requirements.Single(x => x.Requirement.Id == fixedRequirement.Id).IsSatisfied);
    }

    [Fact]
    public async Task Exact_allocation_uses_earliest_expiry_without_blocking_a_valid_substitution()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var service = Service(db, inventory);
        var earlyOnly = await inventory.SaveAsync(new Item
        {
            Name = "Early yoghurt",
            ItemKindId = 1,
            Quantity = 1,
            ExpiryDate = new DateOnly(2026, 8, 25)
        });
        var flexibleOnly = await inventory.SaveAsync(new Item
        {
            Name = "Later yoghurt",
            ItemKindId = 1,
            Quantity = 1,
            ExpiryDate = new DateOnly(2026, 8, 30)
        });
        var recipe = await service.SaveDefinitionAsync(new BomDefinition
        {
            Name = "Two-part snack",
            Kind = BomKind.Recipe,
            OutputQuantity = 1,
            OutputUnit = "serving"
        });
        var flexible = await service.SaveRequirementAsync(new BomRequirement
        {
            BomDefinitionId = recipe.Id,
            Name = "Either yoghurt",
            MatchMode = BomMatchMode.ExplicitCandidates,
            CandidateItemIds = [earlyOnly.Id, flexibleOnly.Id]
        });
        var fixedRequirement = await service.SaveRequirementAsync(new BomRequirement
        {
            BomDefinitionId = recipe.Id,
            Name = "Specific yoghurt",
            MatchMode = BomMatchMode.ExplicitCandidates,
            CandidateItemIds = [earlyOnly.Id]
        });

        var allocation = await service.GetAllocationAsync(recipe.Id, 1);

        Assert.NotNull(allocation);
        Assert.True(allocation!.CanMake);
        Assert.Empty(allocation.Shortfalls);
        Assert.Contains(allocation.Lines, line =>
            line.RequirementId == fixedRequirement.Id && line.ItemId == earlyOnly.Id);
        Assert.Contains(allocation.Lines, line =>
            line.RequirementId == flexible.Id && line.ItemId == flexibleOnly.Id);
    }

    [Fact]
    public async Task Exact_allocation_scales_recipe_and_uses_earliest_expiry_first()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var service = Service(db, inventory);
        var early = await inventory.SaveAsync(new Item
        {
            Name = "Early tomatoes",
            ItemKindId = 1,
            Quantity = 2,
            Unit = "each",
            ExpiryDate = new DateOnly(2026, 8, 25)
        });
        var later = await inventory.SaveAsync(new Item
        {
            Name = "Later tomatoes",
            ItemKindId = 1,
            Quantity = 5,
            Unit = "each",
            ExpiryDate = new DateOnly(2026, 9, 1)
        });
        var recipe = await service.SaveDefinitionAsync(new BomDefinition
        {
            Name = "Tomato salad",
            Kind = BomKind.Recipe,
            OutputQuantity = 2,
            OutputUnit = "servings"
        });
        await service.SaveRequirementAsync(new BomRequirement
        {
            BomDefinitionId = recipe.Id,
            Name = "Tomatoes",
            Quantity = 2,
            Unit = "each",
            MatchMode = BomMatchMode.ExplicitCandidates,
            CandidateItemIds = [early.Id, later.Id]
        });

        var allocation = await service.GetAllocationAsync(recipe.Id, 4);

        Assert.NotNull(allocation);
        Assert.True(allocation!.CanMake);
        Assert.Equal(2, allocation.Lines.Count);
        Assert.Equal(2, allocation.Lines.Single(line => line.ItemId == early.Id).Quantity);
        Assert.Equal(2, allocation.Lines.Single(line => line.ItemId == later.Id).Quantity);
    }

    [Fact]
    public async Task Unit_matching_is_conservative_but_treats_blank_inventory_unit_as_each()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var service = Service(db, inventory);
        var item = await inventory.SaveAsync(new Item { Name = "Screw", ItemKindId = 3, Quantity = 4 });

        Assert.True(BomService.Matches(new BomRequirement
        {
            Name = "Screw",
            Unit = "pieces",
            MatchMode = BomMatchMode.ExplicitCandidates,
            CandidateItemIds = [item.Id]
        }, item));
        Assert.False(BomService.Matches(new BomRequirement
        {
            Name = "Screw",
            Unit = "g",
            MatchMode = BomMatchMode.ExplicitCandidates,
            CandidateItemIds = [item.Id]
        }, item));
    }

    [Fact]
    public async Task Reviewed_draft_is_created_with_all_requirements_in_one_operation()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var service = Service(db, inventory);

        var definition = await service.CreateWithRequirementsAsync(new BomDefinition
        {
            Name = "Vegetable soup",
            Kind = BomKind.Recipe,
            OutputQuantity = 4,
            OutputUnit = "servings"
        },
        [
            new BomRequirement { Name = "Carrots", Quantity = 3, Unit = "each" },
            new BomRequirement { Name = "Stock", Quantity = 500, Unit = "ml" }
        ]);

        var loaded = await service.GetAsync(definition.Id);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Requirements.Count);
        Assert.Equal([1, 2], loaded.Requirements.Select(requirement => requirement.SortOrder));
        Assert.All(loaded.Requirements, requirement => Assert.Equal(definition.Id, requirement.BomDefinitionId));
    }

    [Fact]
    public async Task Reviewed_draft_rolls_back_definition_when_a_requirement_cannot_be_saved()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var service = Service(db, inventory);

        await Assert.ThrowsAnyAsync<Exception>(() => service.CreateWithRequirementsAsync(
            new BomDefinition { Name = "Invalid draft", Kind = BomKind.Build },
            [new BomRequirement
            {
                Name = "Missing candidate",
                MatchMode = BomMatchMode.ExplicitCandidates,
                CandidateItemIds = [int.MaxValue]
            }]));

        Assert.Empty(await service.GetAllAsync());
    }
}
