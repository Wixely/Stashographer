using Dapper;
using Stashographer.Data;
using Stashographer.Data.Entities;
using Stashographer.Services.Intake;

namespace Stashographer.Services.Inventory;

/// <summary>Options controlling development/test sample data. Bound from the <c>SampleData</c> section.</summary>
public class SampleDataOptions
{
    public const string SectionName = "SampleData";

    /// <summary>When true, sample data is populated at startup (if not already present).</summary>
    public bool Enabled { get; set; }

    /// <summary>When true, existing demo inventory and workflows are wiped and re-seeded.</summary>
    public bool Reset { get; set; }
}

/// <summary>
/// Populates a realistic, wholly synthetic data set for local development and documentation.
/// Idempotent: skips if items already exist, unless <see cref="SampleDataOptions.Reset"/> is set.
/// </summary>
public class SampleDataSeeder(
    IDbConnectionFactory db,
    InventoryService inventory,
    ContainerService containers,
    CheckoutService checkouts,
    TagService tags,
    BomService boms,
    MealPlanService mealPlans,
    ConsumptionService consumption,
    IntakeQueueService intakeQueue,
    ILogger<SampleDataSeeder> logger)
{
    public async Task SeedAsync(bool reset, CancellationToken ct = default)
    {
        using (var conn = await db.OpenAsync(ct))
        {
            var existing = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Items");
            if (existing > 0 && !reset)
            {
                logger.LogInformation("Sample data: {Count} items already present, skipping", existing);
                return;
            }
            if (reset)
            {
                logger.LogInformation("Sample data: resetting demo inventory and workflows");
                await conn.ExecuteAsync("""
                    DELETE FROM BrowserUploads;
                    DELETE FROM IntakeQueueItems;
                    DELETE FROM IntakeSessions;
                    DELETE FROM ConsumptionEvents;
                    DELETE FROM MealPlans;
                    DELETE FROM BomDefinitions;
                    DELETE FROM Tags;
                    DELETE FROM Checkouts;
                    DELETE FROM Items;
                    DELETE FROM Containers;
                    """);
            }
        }

        // Containers in the built-in locations (1=Kitchen, 3=Garage, 4=Loft, 5=Study).
        var pantry = await containers.SaveContainerAsync(new Container
        {
            Name = "Pantry shelf",
            ContainerType = ContainerType.Shelf,
            LocationId = 1
        }, ct);
        var toolbox = await containers.SaveContainerAsync(new Container
        {
            Name = "Red toolbox",
            ContainerType = ContainerType.Box,
            LocationId = 3
        }, ct);
        var seasonal = await containers.SaveContainerAsync(new Container
        {
            Name = "Seasonal box",
            ContainerType = ContainerType.Box,
            LocationId = 4
        }, ct);

        var today = DateOnly.FromDateTime(DateTime.Today);

        var beansDraft = new Item
        {
            Name = "Baked Beans",
            Code = "5000157024671",
            ItemKindId = 1,
            Quantity = 4,
            Unit = "tin",
            LowStockThreshold = 2,
            ContainerId = pantry.Id,
            Attributes = new() { ["Brand"] = "Heinz", ["Category"] = "Tinned goods" }
        };
        SpecialAttributeCatalog.SetPrice(beansDraft, 1.25m, "GBP");
        SpecialAttributeCatalog.SetExpiry(beansDraft, today.AddMonths(8), ExpiryDateKind.BestBefore);
        var beans = await inventory.SaveAsync(beansDraft, ct);

        var milkDraft = new Item
        {
            Name = "Semi-skimmed Milk",
            Code = "5000000000019",
            ItemKindId = 1,
            Quantity = 2,
            Unit = "L",
            LocationId = 1,
            Attributes = new() { ["Brand"] = "Local Dairy" }
        };
        SpecialAttributeCatalog.SetPrice(milkDraft, 1.60m, "GBP");
        SpecialAttributeCatalog.SetExpiry(milkDraft, today.AddDays(3), ExpiryDateKind.UseBy);
        var milk = await inventory.SaveAsync(milkDraft, ct);

        var colaDraft = new Item
        {
            Name = "Cola (can)",
            Code = "5449000000996",
            ItemKindId = 1,
            Quantity = 6,
            Unit = "can",
            ContainerId = pantry.Id,
            Attributes = new() { ["Category"] = "Soft drinks" }
        };
        SpecialAttributeCatalog.SetPrice(colaDraft, 0.85m, "GBP");
        var cola = await inventory.SaveAsync(colaDraft, ct);

        var pasta = await inventory.SaveAsync(new Item
        {
            Name = "Penne pasta",
            ItemKindId = 1,
            Quantity = 500,
            Unit = "g",
            ContainerId = pantry.Id,
            Attributes = new() { ["Category"] = "Dried pasta" }
        }, ct);
        var tomatoes = await inventory.SaveAsync(new Item
        {
            Name = "Chopped tomatoes",
            ItemKindId = 1,
            Quantity = 2,
            Unit = "tin",
            LowStockThreshold = 2,
            ContainerId = pantry.Id,
            Attributes = new() { ["Category"] = "Tinned goods" }
        }, ct);
        var breadDraft = new Item
        {
            Name = "Wholemeal bread",
            ItemKindId = 1,
            Quantity = 10,
            Unit = "slice",
            LocationId = 1
        };
        SpecialAttributeCatalog.SetExpiry(breadDraft, today.AddDays(2), ExpiryDateKind.BestBefore);
        var bread = await inventory.SaveAsync(breadDraft, ct);
        var yogurtDraft = new Item
        {
            Name = "Natural yogurt",
            ItemKindId = 1,
            Quantity = 4,
            Unit = "pot",
            LocationId = 1
        };
        SpecialAttributeCatalog.SetExpiry(yogurtDraft, today.AddDays(1), ExpiryDateKind.UseBy);
        var yogurt = await inventory.SaveAsync(yogurtDraft, ct);
        var cheeseDraft = new Item
        {
            Name = "Cheddar cheese",
            ItemKindId = 1,
            Quantity = 150,
            Unit = "g",
            LocationId = 1
        };
        SpecialAttributeCatalog.SetExpiry(cheeseDraft, today.AddDays(6), ExpiryDateKind.BestBefore);
        var cheese = await inventory.SaveAsync(cheeseDraft, ct);

        var algorithms = await inventory.SaveAsync(new Item
        {
            Name = "Introduction to Algorithms",
            Code = "9780262033848",
            ItemKindId = 2,
            Quantity = 1,
            LocationId = 5,
            ThumbnailUrl = "https://covers.openlibrary.org/b/isbn/9780262033848-M.jpg?default=false",
            Attributes = new()
            {
                ["Author"] = "Cormen, Leiserson, Rivest, Stein",
                ["Publisher"] = "The MIT Press",
                ["Pages"] = "1292"
            }
        }, ct);
        var pragmatic = await inventory.SaveAsync(new Item
        {
            Name = "The Pragmatic Programmer",
            Code = "9780201616224",
            ItemKindId = 2,
            Quantity = 1,
            LocationId = 5,
            Attributes = new() { ["Author"] = "Hunt, Thomas" }
        }, ct);

        var drill = await inventory.SaveAsync(new Item
        {
            Name = "Cordless Drill",
            ItemKindId = 3,
            Quantity = 1,
            ContainerId = toolbox.Id,
            Attributes = new() { ["Brand"] = "DeWalt", ["Model"] = "DCD778" }
        }, ct);
        var screwdriver = await inventory.SaveAsync(new Item
        {
            Name = "Screwdriver set",
            ItemKindId = 3,
            Quantity = 1,
            ContainerId = toolbox.Id
        }, ct);
        var hdmi = await inventory.SaveAsync(new Item
        {
            Name = "HDMI Cable",
            ItemKindId = 4,
            Quantity = 3,
            Unit = "each",
            ContainerId = seasonal.Id,
            Attributes = new() { ["Length"] = "2m" }
        }, ct);
        var lights = await inventory.SaveAsync(new Item
        {
            Name = "Fairy lights",
            ItemKindId = 7,
            Quantity = 4,
            Unit = "each",
            ContainerId = seasonal.Id
        }, ct);
        var coat = await inventory.SaveAsync(new Item
        {
            Name = "Winter coat",
            ItemKindId = 6,
            Quantity = 1,
            LocationId = 4,
            Attributes = new() { ["Size"] = "L", ["Colour"] = "Navy" }
        }, ct);

        var mealPrep = await tags.SaveAsync(new Tag { Name = "Meal prep" }, ct);
        var useSoon = await tags.SaveAsync(new Tag { Name = "Use soon" }, ct);
        var favourites = await tags.SaveAsync(new Tag { Name = "Favourites" }, ct);
        var loanable = await tags.SaveAsync(new Tag { Name = "Loanable" }, ct);
        await tags.SetForItemAsync(beans.Id, [mealPrep.Id], ct);
        await tags.SetForItemAsync(pasta.Id, [mealPrep.Id], ct);
        await tags.SetForItemAsync(tomatoes.Id, [mealPrep.Id], ct);
        await tags.SetForItemAsync(bread.Id, [mealPrep.Id, useSoon.Id], ct);
        await tags.SetForItemAsync(milk.Id, [useSoon.Id], ct);
        await tags.SetForItemAsync(yogurt.Id, [useSoon.Id], ct);
        await tags.SetForItemAsync(cheese.Id, [mealPrep.Id, useSoon.Id], ct);
        await tags.SetForItemAsync(pragmatic.Id, [favourites.Id, loanable.Id], ct);
        await tags.SetForItemAsync(algorithms.Id, [loanable.Id], ct);
        await tags.SetForItemAsync(drill.Id, [loanable.Id], ct);
        await tags.SetForItemAsync(coat.Id, [favourites.Id], ct);

        var tomatoPasta = await boms.CreateWithRequirementsAsync(new BomDefinition
        {
            Name = "Tomato pasta",
            Kind = BomKind.Recipe,
            Description = "A quick pantry dinner with an optional cheese topping.",
            OutputQuantity = 2,
            OutputUnit = "servings"
        },
        [
            new BomRequirement { Name = "Penne pasta", Quantity = 200, Unit = "g", MatchItemKindId = 1, MatchText = "pasta" },
            new BomRequirement { Name = "Chopped tomatoes", Quantity = 1, Unit = "tin", MatchItemKindId = 1, MatchText = "tomatoes" },
            new BomRequirement { Name = "Cheddar cheese", Quantity = 50, Unit = "g", IsOptional = true, MatchItemKindId = 1, MatchText = "cheese" }
        ], ct);
        var beansOnToast = await boms.CreateWithRequirementsAsync(new BomDefinition
        {
            Name = "Beans on toast",
            Kind = BomKind.Recipe,
            Description = "Fast lunch using interchangeable bread and baked beans.",
            OutputQuantity = 1,
            OutputUnit = "serving"
        },
        [
            new BomRequirement { Name = "Baked beans", Quantity = 1, Unit = "tin", MatchItemKindId = 1, MatchText = "baked beans" },
            new BomRequirement { Name = "Bread", Quantity = 2, Unit = "slice", MatchItemKindId = 1, MatchText = "bread" }
        ], ct);
        await boms.CreateWithRequirementsAsync(new BomDefinition
        {
            Name = "Desk media setup",
            Kind = BomKind.Build,
            Description = "A reusable equipment checklist for a simple desk display.",
            OutputQuantity = 1,
            OutputUnit = "setup"
        },
        [
            new BomRequirement { Name = "HDMI cable", Quantity = 1, Unit = "each", MatchItemKindId = 4, MatchText = "HDMI cable" },
            new BomRequirement { Name = "Fairy lights", Quantity = 1, Unit = "each", IsOptional = true, MatchText = "fairy lights" }
        ], ct);

        var mealPlan = await mealPlans.SaveReviewedAsync(new MealPlanDraft
        {
            Name = "Demo weeknight plan",
            StartDate = today,
            EndDate = today.AddDays(3),
            Notes = "Use the soonest-dated food first and keep one easy lunch.",
            Entries =
            [
                new MealPlanEntryDraft { PlanDate = today, MealSlot = "Dinner", BomDefinitionId = tomatoPasta.Id, Reason = "Use pantry staples and the open cheese." },
                new MealPlanEntryDraft { PlanDate = today.AddDays(1), MealSlot = "Lunch", BomDefinitionId = beansOnToast.Id, Reason = "Quick lunch before the bread date." },
                new MealPlanEntryDraft { PlanDate = today.AddDays(2), MealSlot = "Dinner", BomDefinitionId = tomatoPasta.Id, Reason = "Use another tomato tin." },
                new MealPlanEntryDraft { PlanDate = today.AddDays(3), MealSlot = "Dinner", BomDefinitionId = tomatoPasta.Id, Reason = "Shows the shared stock budget across the whole plan." }
            ]
        }, ct);
        await mealPlans.CookAsync(mealPlan.Entries[0].Id, ct: ct);
        await consumption.UseItemAsync(cola.Id, 1, "Demo afternoon drink", ct);

        await intakeQueue.EnqueueDraftAsync(new Item
        {
            Name = "Camping lantern",
            Description = "Synthetic queue example awaiting item-by-item verification.",
            ItemKindId = 4,
            Quantity = 1,
            Unit = "each",
            LocationId = 3,
            Attributes = new() { ["Colour"] = "Orange", ["Power"] = "Rechargeable" }
        }, ct);

        await checkouts.CheckOutAsync(
            drill.Id, "Demo neighbour", "Sample weekend loan", today.AddDays(5), null, ct);

        logger.LogInformation(
            "Sample data seeded: {ItemCount} items, tags, recipes, a meal plan, history and queue review",
            15);
    }
}
