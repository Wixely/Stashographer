using Dapper;
using Stashographer.Data;
using Stashographer.Data.Entities;

namespace Stashographer.Services.Inventory;

public sealed record ConsumptionApplied(
    int EventId,
    int MealPlanEntryId,
    string Description,
    IReadOnlyList<ConsumptionLine> Lines);

/// <summary>
/// Persists reviewed meal plans and applies explicit, reversible FEFO consumption events.
/// Merely generating or saving a plan never changes inventory quantities.
/// </summary>
public sealed class MealPlanService(IDbConnectionFactory db, BomService boms)
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
                SELECT Id, MealPlanEntryId, BomDefinitionId, Description, ConsumedAt, UndoneAt
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

    public async Task<BomAllocation?> GetAllocationAsync(
        MealPlanEntry entry, CancellationToken ct = default) =>
        entry.BomDefinitionId is { } recipeId
            ? await boms.GetAllocationAsync(recipeId, entry.OutputQuantity, ct)
            : null;

    public async Task<ConsumptionApplied> CookAsync(
        int mealPlanEntryId, CancellationToken ct = default)
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
        var allocation = await GetAllocationAsync(entry, ct)
            ?? throw new InvalidOperationException("The source recipe no longer exists.");
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
        var eventId = await write.ExecuteScalarAsync<int>("""
            INSERT INTO ConsumptionEvents
                (MealPlanEntryId, BomDefinitionId, Description, ConsumedAt)
            VALUES
                (@mealPlanEntryId, @bomDefinitionId, @description, @consumedAt);
            SELECT last_insert_rowid();
            """, new
        {
            mealPlanEntryId,
            bomDefinitionId = entry.BomDefinitionId,
            description = entry.RecipeName,
            consumedAt = now
        }, tx);
        var consumedLines = new List<ConsumptionLine>();
        foreach (var line in allocation.Lines)
        {
            var consumed = new ConsumptionLine
            {
                ConsumptionEventId = eventId,
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                Quantity = line.Quantity,
                Unit = line.Unit,
                ExpiryDate = line.ExpiryDate
            };
            consumed.Id = await write.ExecuteScalarAsync<int>("""
                INSERT INTO ConsumptionLines
                    (ConsumptionEventId, ItemId, ItemName, Quantity, Unit, ExpiryDate)
                VALUES
                    (@ConsumptionEventId, @ItemId, @ItemName, @Quantity, @Unit, @ExpiryDate);
                SELECT last_insert_rowid();
                """, consumed, tx);
            consumedLines.Add(consumed);
        }
        await write.ExecuteAsync("""
            UPDATE MealPlans SET UpdatedAt = @now WHERE Id = @mealPlanId;
            """, new { now, mealPlanId = entry.MealPlanId }, tx);
        tx.Commit();
        return new ConsumptionApplied(eventId, entry.Id, entry.RecipeName, consumedLines);
    }

    public async Task UndoAsync(int consumptionEventId, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        var consumption = await conn.QuerySingleOrDefaultAsync<ConsumptionEvent>("""
            SELECT Id, MealPlanEntryId, BomDefinitionId, Description, ConsumedAt, UndoneAt
            FROM ConsumptionEvents WHERE Id = @consumptionEventId;
            """, new { consumptionEventId }, tx)
            ?? throw new InvalidOperationException("The consumption event no longer exists.");
        if (consumption.UndoneAt is not null)
            throw new InvalidOperationException("This consumption event was already undone.");
        var lines = (await conn.QueryAsync<ConsumptionLine>("""
            SELECT Id, ConsumptionEventId, ItemId, ItemName, Quantity, Unit, ExpiryDate
            FROM ConsumptionLines WHERE ConsumptionEventId = @consumptionEventId ORDER BY Id;
            """, new { consumptionEventId }, tx)).ToList();
        if (lines.Any(line => line.ItemId is null))
            throw new InvalidOperationException(
                "An inventory lot used by this meal was deleted, so it cannot be restored automatically.");
        var now = DateTimeOffset.UtcNow;
        var claimed = await conn.ExecuteAsync("""
            UPDATE ConsumptionEvents SET UndoneAt = @now
            WHERE Id = @consumptionEventId AND UndoneAt IS NULL;
            """, new { now, consumptionEventId }, tx);
        if (claimed != 1)
            throw new InvalidOperationException("This consumption event was already undone.");
        foreach (var line in lines)
        {
            var changed = await conn.ExecuteAsync("""
                UPDATE Items SET Quantity = Quantity + @quantity, UpdatedAt = @now WHERE Id = @itemId;
                """, new { quantity = line.Quantity, now, itemId = line.ItemId }, tx);
            if (changed != 1)
                throw new InvalidOperationException(
                    $"The inventory lot “{line.ItemName}” no longer exists, so the event cannot be undone.");
        }
        if (consumption.MealPlanEntryId is { } entryId)
            await conn.ExecuteAsync("""
                UPDATE MealPlanEntries SET Status = @planned, CookedAt = NULL WHERE Id = @entryId;
                """, new { planned = (int)MealPlanEntryStatus.Planned, entryId }, tx);
        tx.Commit();
    }

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
}
