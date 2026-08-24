using Dapper;
using Stashographer.Data;
using Stashographer.Data.Entities;

namespace Stashographer.Services.Inventory;

/// <summary>
/// Persists reviewed meal plans and applies explicit, reversible FEFO consumption events.
/// Merely generating or saving a plan never changes inventory quantities.
/// </summary>
public sealed class MealPlanService(
    IDbConnectionFactory db,
    BomService boms,
    InventoryService inventory,
    ConsumptionService consumption)
{
    public async Task<List<MealPlan>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var plans = (await conn.QueryAsync<MealPlan>("""
            SELECT Id, Name, StartDate, EndDate, Notes, CreatedAt, UpdatedAt
            FROM MealPlans
            ORDER BY EndDate DESC, StartDate DESC, Id DESC;
            """)).ToList();
        if (plans.Count == 0) return plans;

        var entries = (await conn.QueryAsync<MealPlanEntry>("""
            SELECT Id, MealPlanId, PlanDate, MealSlot, BomDefinitionId, RecipeName,
                   OutputQuantity, OutputUnit, Reason, Status, CookedAt
            FROM MealPlanEntries
            WHERE MealPlanId IN @ids
            ORDER BY PlanDate, MealSlot COLLATE NOCASE, Id;
            """, new { ids = plans.Select(plan => plan.Id).ToArray() })).ToList();
        var entryIds = entries.Select(entry => entry.Id).ToArray();
        var activeEvents = entryIds.Length == 0
            ? []
            : (await conn.QueryAsync<ConsumptionEvent>("""
                SELECT Id, Kind, MealPlanEntryId, BomDefinitionId, Description, ConsumedAt, UndoneAt
                FROM ConsumptionEvents
                WHERE MealPlanEntryId IN @entryIds AND UndoneAt IS NULL;
                """, new { entryIds })).ToList();
        var eventIds = activeEvents.Select(consumption => consumption.Id).ToArray();
        var lines = eventIds.Length == 0
            ? []
            : (await conn.QueryAsync<ConsumptionLine>("""
                SELECT Id, ConsumptionEventId, ItemId, ItemName, Quantity, Unit, ExpiryDate
                FROM ConsumptionLines WHERE ConsumptionEventId IN @eventIds ORDER BY Id;
                """, new { eventIds })).ToList();
        foreach (var consumption in activeEvents)
            consumption.Lines = lines.Where(line => line.ConsumptionEventId == consumption.Id).ToList();
        foreach (var entry in entries)
            entry.Consumption = activeEvents.FirstOrDefault(consumption =>
                consumption.MealPlanEntryId == entry.Id);
        foreach (var plan in plans)
            plan.Entries = entries.Where(entry => entry.MealPlanId == plan.Id).ToList();
        return plans;
    }

    public async Task<MealPlan> SaveReviewedAsync(
        MealPlanDraft draft, CancellationToken ct = default)
    {
        PrepareDraft(draft);
        var recipes = new Dictionary<int, BomDefinition>();
        foreach (var recipeId in draft.Entries.Select(entry => entry.BomDefinitionId).Distinct())
        {
            var recipe = await boms.GetAsync(recipeId, ct)
                ?? throw new InvalidOperationException("A selected recipe no longer exists.");
            if (recipe.Kind != BomKind.Recipe)
                throw new InvalidOperationException($"“{recipe.Name}” is not a food recipe.");
            recipes[recipeId] = recipe;
        }

        var now = DateTimeOffset.UtcNow;
        using var conn = await db.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        var plan = new MealPlan
        {
            Name = draft.Name,
            StartDate = draft.StartDate,
            EndDate = draft.EndDate,
            Notes = draft.Notes,
            CreatedAt = now,
            UpdatedAt = now
        };
        plan.Id = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO MealPlans (Name, StartDate, EndDate, Notes, CreatedAt, UpdatedAt)
            VALUES (@Name, @StartDate, @EndDate, @Notes, @CreatedAt, @UpdatedAt);
            SELECT last_insert_rowid();
            """, plan, tx);
        foreach (var draftEntry in draft.Entries.OrderBy(entry => entry.PlanDate))
        {
            var recipe = recipes[draftEntry.BomDefinitionId];
            var entry = new MealPlanEntry
            {
                MealPlanId = plan.Id,
                PlanDate = draftEntry.PlanDate,
                MealSlot = draftEntry.MealSlot,
                BomDefinitionId = recipe.Id,
                RecipeName = recipe.Name,
                OutputQuantity = draftEntry.OutputQuantity,
                OutputUnit = recipe.OutputUnit,
                Reason = draftEntry.Reason,
                Status = MealPlanEntryStatus.Planned
            };
            entry.Id = await conn.ExecuteScalarAsync<int>("""
                INSERT INTO MealPlanEntries
                    (MealPlanId, PlanDate, MealSlot, BomDefinitionId, RecipeName,
                     OutputQuantity, OutputUnit, Reason, Status)
                VALUES
                    (@MealPlanId, @PlanDate, @MealSlot, @BomDefinitionId, @RecipeName,
                     @OutputQuantity, @OutputUnit, @Reason, @Status);
                SELECT last_insert_rowid();
                """, entry, tx);
            plan.Entries.Add(entry);
        }
        tx.Commit();
        return plan;
    }

    /// <summary>
    /// Projects each plan independently against one inventory snapshot. Requirements from all
    /// planned meals in a plan share a single global allocation, preventing false readiness when
    /// two meals need the same stock. The projection never writes to inventory.
    /// </summary>
    public async Task<List<MealPlanProjection>> GetProjectionsAsync(
        IReadOnlyList<MealPlan> plans, CancellationToken ct = default)
    {
        if (plans.Count == 0) return [];
        var active = (await inventory.QueryAsync(new ItemQuery(), ct))
            .Where(item => item.Quantity > 0 && !item.IsCheckedOut)
            .ToList();
        var recipeIds = plans
            .SelectMany(plan => plan.Entries)
            .Where(entry => entry.Status == MealPlanEntryStatus.Planned)
            .Select(entry => entry.BomDefinitionId)
            .OfType<int>()
            .Distinct()
            .ToList();
        var recipes = new Dictionary<int, BomDefinition>();
        foreach (var recipeId in recipeIds)
        {
            var recipe = await boms.GetAsync(recipeId, ct);
            if (recipe is not null) recipes[recipeId] = recipe;
        }

        return plans.Select(plan => Project(plan, active, recipes)).ToList();
    }

    /// <summary>Projects an editable draft so stock conflicts can be reviewed before saving.</summary>
    public async Task<MealPlanProjection> GetDraftProjectionAsync(
        MealPlanDraft draft, CancellationToken ct = default)
    {
        PrepareDraft(draft);
        var transient = new MealPlan
        {
            Name = draft.Name,
            StartDate = draft.StartDate,
            EndDate = draft.EndDate,
            Entries = draft.Entries.Select((entry, index) => new MealPlanEntry
            {
                Id = index + 1,
                PlanDate = entry.PlanDate,
                MealSlot = entry.MealSlot,
                BomDefinitionId = entry.BomDefinitionId,
                OutputQuantity = entry.OutputQuantity,
                Reason = entry.Reason,
                Status = MealPlanEntryStatus.Planned
            }).ToList()
        };
        return (await GetProjectionsAsync([transient], ct)).Single();
    }

    public async Task<BomAllocation?> GetAllocationAsync(
        MealPlanEntry entry, CancellationToken ct = default) =>
        entry.BomDefinitionId is { } recipeId
            ? await boms.GetAllocationAsync(recipeId, entry.OutputQuantity, ct)
            : null;

    public async Task<ConsumptionApplied> CookAsync(
        int mealPlanEntryId,
        bool prioritizeThisMeal = false,
        CancellationToken ct = default)
    {
        MealPlanEntry entry;
        using (var conn = await db.OpenAsync(ct))
        {
            entry = await conn.QuerySingleOrDefaultAsync<MealPlanEntry>("""
                SELECT Id, MealPlanId, PlanDate, MealSlot, BomDefinitionId, RecipeName,
                       OutputQuantity, OutputUnit, Reason, Status, CookedAt
                FROM MealPlanEntries WHERE Id = @mealPlanEntryId;
                """, new { mealPlanEntryId })
                ?? throw new InvalidOperationException("The planned meal no longer exists.");
        }
        if (entry.Status == MealPlanEntryStatus.Cooked)
            throw new InvalidOperationException("This meal was already marked cooked.");
        if (entry.BomDefinitionId is null)
            throw new InvalidOperationException("The source recipe no longer exists.");
        BomAllocation? allocation;
        if (prioritizeThisMeal)
        {
            allocation = await GetAllocationAsync(entry, ct);
        }
        else
        {
            var plan = (await GetAllAsync(ct)).SingleOrDefault(candidate => candidate.Id == entry.MealPlanId)
                ?? throw new InvalidOperationException("The meal plan no longer exists.");
            allocation = (await GetProjectionsAsync([plan], ct)).Single().Entries
                .SingleOrDefault(candidate => candidate.MealPlanEntryId == entry.Id)?.Allocation;
        }
        if (allocation is null)
            throw new InvalidOperationException("The source recipe no longer exists.");
        if (!allocation.CanMake)
        {
            var missing = string.Join(", ", allocation.Shortfalls.Select(shortfall =>
                $"{shortfall.RequirementName} ({shortfall.RequiredQuantity - shortfall.AllocatedQuantity:0.##} {shortfall.Unit} short)"));
            throw new InvalidOperationException($"There is not enough inventory to cook this meal: {missing}.");
        }

        var now = DateTimeOffset.UtcNow;
        using var write = await db.OpenAsync(ct);
        using var tx = write.BeginTransaction();
        var claimed = await write.ExecuteAsync("""
            UPDATE MealPlanEntries SET Status = @cooked, CookedAt = @now
            WHERE Id = @mealPlanEntryId AND Status = @planned;
            """, new
        {
            cooked = (int)MealPlanEntryStatus.Cooked,
            planned = (int)MealPlanEntryStatus.Planned,
            now,
            mealPlanEntryId
        }, tx);
        if (claimed != 1)
            throw new InvalidOperationException("This meal is no longer awaiting cooking.");

        foreach (var line in allocation.Lines)
        {
            var changed = await write.ExecuteAsync("""
                UPDATE Items
                SET Quantity = Quantity - @quantity, UpdatedAt = @now
                WHERE Id = @itemId AND Quantity >= @quantity;
                """, new { itemId = line.ItemId, quantity = line.Quantity, now }, tx);
            if (changed != 1)
                throw new InvalidOperationException(
                    $"Inventory changed while allocating {line.ItemName}; review the meal again.");
        }
        var consumedLines = allocation.Lines.Select(line => new ConsumptionLine
        {
            ItemId = line.ItemId,
            ItemName = line.ItemName,
            Quantity = line.Quantity,
            Unit = line.Unit,
            ExpiryDate = line.ExpiryDate
        }).ToList();
        var applied = await consumption.RecordAsync(
            write,
            tx,
            ConsumptionKind.Meal,
            entry.RecipeName,
            entry.Id,
            entry.BomDefinitionId,
            consumedLines,
            now);
        await write.ExecuteAsync("""
            UPDATE MealPlans SET UpdatedAt = @now WHERE Id = @mealPlanId;
            """, new { now, mealPlanId = entry.MealPlanId }, tx);
        tx.Commit();
        return applied;
    }

    public Task UndoAsync(int consumptionEventId, CancellationToken ct = default) =>
        consumption.UndoAsync(consumptionEventId, ct);

    public async Task DeletePlanAsync(int id, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var cooked = await conn.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM MealPlanEntries WHERE MealPlanId = @id AND Status = @cooked;
            """, new { id, cooked = (int)MealPlanEntryStatus.Cooked });
        if (cooked > 0)
            throw new InvalidOperationException(
                "Undo cooked meals before deleting this plan so its history stays understandable.");
        await conn.ExecuteAsync("DELETE FROM MealPlans WHERE Id = @id;", new { id });
    }

    private static void PrepareDraft(MealPlanDraft draft)
    {
        draft.Name = draft.Name.Trim();
        draft.Notes = Clean(draft.Notes);
        if (draft.Name.Length == 0) throw new InvalidOperationException("Enter a meal-plan name.");
        if (draft.EndDate < draft.StartDate)
            throw new InvalidOperationException("The meal-plan end date cannot precede its start.");
        if (draft.Entries.Count == 0)
            throw new InvalidOperationException("Add at least one meal before saving the plan.");
        foreach (var entry in draft.Entries)
        {
            entry.MealSlot = Clean(entry.MealSlot) ?? "Dinner";
            entry.Reason = Clean(entry.Reason);
            if (entry.PlanDate < draft.StartDate || entry.PlanDate > draft.EndDate)
                throw new InvalidOperationException("Every meal date must be inside the plan range.");
            if (entry.BomDefinitionId <= 0)
                throw new InvalidOperationException("Choose a recipe for every planned meal.");
            if (entry.OutputQuantity <= 0)
                throw new InvalidOperationException("Every planned output quantity must be greater than zero.");
        }
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static MealPlanProjection Project(
        MealPlan plan,
        IReadOnlyList<Item> inventorySnapshot,
        IReadOnlyDictionary<int, BomDefinition> recipes)
    {
        var planned = plan.Entries
            .Where(entry => entry.Status == MealPlanEntryStatus.Planned)
            .OrderBy(entry => entry.PlanDate)
            .ThenBy(entry => entry.MealSlot, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Id)
            .ToList();
        var projections = new List<MealPlanEntryProjection>();
        var requirementMap = new Dictionary<int, ProjectedRequirement>();
        var requirementCosts = new Dictionary<int, int>();
        var combined = new BomDefinition
        {
            Name = plan.Name,
            Kind = BomKind.Recipe,
            OutputQuantity = 1
        };
        var syntheticRequirementId = 0;
        for (var entryIndex = 0; entryIndex < planned.Count; entryIndex++)
        {
            var entry = planned[entryIndex];
            if (entry.BomDefinitionId is not { } recipeId
                || !recipes.TryGetValue(recipeId, out var recipe))
            {
                projections.Add(new MealPlanEntryProjection(entry.Id, null));
                continue;
            }

            var required = recipe.Requirements.Where(requirement => !requirement.IsOptional).ToList();
            if (required.Count == 0)
            {
                projections.Add(new MealPlanEntryProjection(
                    entry.Id,
                    new BomAllocation(recipe, entry.OutputQuantity, [],
                    [
                        new BomRequirementShortfall(0, "Recipe requirements", 1, 0, null)
                    ])));
                continue;
            }

            var scale = entry.OutputQuantity / recipe.OutputQuantity;
            foreach (var requirement in required)
            {
                var syntheticId = ++syntheticRequirementId;
                combined.Requirements.Add(CloneRequirement(requirement, syntheticId, requirement.Quantity * scale));
                requirementMap[syntheticId] = new ProjectedRequirement(entry, requirement);
                var priority = (long)entryIndex * (inventorySnapshot.Count + 1L);
                requirementCosts[syntheticId] = (int)Math.Min(int.MaxValue / 4, priority);
            }
        }

        var combinedAllocation = combined.Requirements.Count == 0
            ? null
            : BomService.Allocate(combined, 1, CloneInventory(inventorySnapshot), requirementCosts);
        foreach (var entry in planned.Where(entry =>
                     projections.All(projection => projection.MealPlanEntryId != entry.Id)))
        {
            var recipe = recipes[entry.BomDefinitionId!.Value];
            var syntheticIds = requirementMap
                .Where(pair => pair.Value.Entry.Id == entry.Id)
                .Select(pair => pair.Key)
                .ToHashSet();
            var lines = combinedAllocation!.Lines
                .Where(line => syntheticIds.Contains(line.RequirementId))
                .Select(line =>
                {
                    var original = requirementMap[line.RequirementId].Requirement;
                    return line with
                    {
                        RequirementId = original.Id,
                        RequirementName = original.Name
                    };
                })
                .ToList();
            var shortfalls = combinedAllocation.Shortfalls
                .Where(shortfall => syntheticIds.Contains(shortfall.RequirementId))
                .Select(shortfall =>
                {
                    var original = requirementMap[shortfall.RequirementId].Requirement;
                    return shortfall with
                    {
                        RequirementId = original.Id,
                        RequirementName = original.Name,
                        Unit = original.Unit
                    };
                })
                .ToList();
            projections.Add(new MealPlanEntryProjection(
                entry.Id,
                new BomAllocation(recipe, entry.OutputQuantity, lines, shortfalls)));
        }

        projections = projections
            .OrderBy(projection => planned.FindIndex(entry => entry.Id == projection.MealPlanEntryId))
            .ToList();
        var shopping = BuildShoppingList(planned, projections);
        return new MealPlanProjection(plan.Id, projections, shopping);
    }

    private static List<MealPlanShoppingLine> BuildShoppingList(
        IReadOnlyList<MealPlanEntry> entries,
        IReadOnlyList<MealPlanEntryProjection> projections)
    {
        var entryMap = entries.ToDictionary(entry => entry.Id);
        return projections
            .Where(projection => projection.Allocation is not null)
            .SelectMany(projection => projection.Allocation!.Shortfalls.Select(shortfall => new
            {
                Entry = entryMap[projection.MealPlanEntryId],
                RecipeName = string.IsNullOrWhiteSpace(entryMap[projection.MealPlanEntryId].RecipeName)
                    ? projection.Allocation.Definition.Name
                    : entryMap[projection.MealPlanEntryId].RecipeName,
                Shortfall = shortfall,
                Missing = shortfall.RequiredQuantity - shortfall.AllocatedQuantity
            }))
            .GroupBy(row => new
            {
                Name = InventoryService.NormalizeName(row.Shortfall.RequirementName),
                Unit = InventoryService.NormalizeName(row.Shortfall.Unit)
            })
            .Select(group => new MealPlanShoppingLine(
                group.First().Shortfall.RequirementName,
                group.Sum(row => row.Missing),
                group.First().Shortfall.Unit,
                group.GroupBy(row => row.Entry.Id)
                    .Select(need => new MealPlanShoppingNeed(
                        need.Key,
                        need.First().Entry.PlanDate,
                        need.First().RecipeName,
                        need.Sum(row => row.Missing)))
                    .OrderBy(need => need.PlanDate)
                    .ThenBy(need => need.MealPlanEntryId)
                    .ToList()))
            .OrderBy(line => line.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static BomRequirement CloneRequirement(
        BomRequirement source, int id, decimal quantity) => new()
    {
        Id = id,
        Name = source.Name,
        Quantity = quantity,
        Unit = source.Unit,
        MatchMode = source.MatchMode,
        MatchItemKindId = source.MatchItemKindId,
        MatchText = source.MatchText,
        RequiredAttributes = new Dictionary<string, string>(source.RequiredAttributes),
        CandidateItemIds = [.. source.CandidateItemIds],
        SortOrder = source.SortOrder
    };

    private static List<Item> CloneInventory(IReadOnlyList<Item> source) => source.Select(item => new Item
    {
        Id = item.Id,
        Name = item.Name,
        ItemKindId = item.ItemKindId,
        Quantity = item.Quantity,
        Unit = item.Unit,
        ExpiryDate = item.ExpiryDate,
        Attributes = new Dictionary<string, string>(item.Attributes),
        IsCheckedOut = item.IsCheckedOut
    }).ToList();

    private sealed record ProjectedRequirement(
        MealPlanEntry Entry,
        BomRequirement Requirement);
}
