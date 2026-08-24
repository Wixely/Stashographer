using System.Data;
using Dapper;
using Stashographer.Data;
using Stashographer.Data.Entities;

namespace Stashographer.Services.Inventory;

public sealed record ConsumptionHistoryQuery(
    string? Search = null,
    int? ItemId = null,
    ConsumptionKind? Kind = null,
    bool IncludeUndone = false,
    DateTimeOffset? ConsumedFrom = null,
    DateTimeOffset? ConsumedBefore = null,
    int Limit = 200);

/// <summary>Authoritative event history for explicit, reversible inventory consumption.</summary>
public sealed class ConsumptionService(IDbConnectionFactory db)
{
    public async Task<List<ConsumptionEvent>> GetHistoryAsync(
        ConsumptionHistoryQuery query, CancellationToken ct = default)
    {
        var search = Clean(query.Search);
        using var conn = await db.OpenAsync(ct);
        var parameters = new DynamicParameters();
        parameters.Add("includeUndone", query.IncludeUndone ? 1 : 0);
        parameters.Add("kind", query.Kind is { } kind ? (int)kind : null);
        parameters.Add("itemId", query.ItemId);
        parameters.Add("consumedFrom", query.ConsumedFrom);
        parameters.Add("consumedBefore", query.ConsumedBefore);
        parameters.Add("search", search);
        parameters.Add("pattern", search is null ? null : $"%{EscapeLike(search)}%");
        parameters.Add("limit", Math.Clamp(query.Limit, 1, 500));
        var events = (await conn.QueryAsync<ConsumptionEvent>("""
            SELECT e.Id, e.Kind, e.MealPlanEntryId, e.BomDefinitionId, e.Description,
                   e.ConsumedAt, e.UndoneAt, p.Name AS MealPlanName, mpe.PlanDate, mpe.MealSlot
            FROM ConsumptionEvents e
            LEFT JOIN MealPlanEntries mpe ON mpe.Id = e.MealPlanEntryId
            LEFT JOIN MealPlans p ON p.Id = mpe.MealPlanId
            WHERE (@includeUndone = 1 OR e.UndoneAt IS NULL)
              AND (@kind IS NULL OR e.Kind = @kind)
              AND (@consumedFrom IS NULL OR e.ConsumedAt >= @consumedFrom)
              AND (@consumedBefore IS NULL OR e.ConsumedAt < @consumedBefore)
              AND (@itemId IS NULL OR EXISTS (
                    SELECT 1 FROM ConsumptionLines itemLine
                    WHERE itemLine.ConsumptionEventId = e.Id AND itemLine.ItemId = @itemId))
              AND (@search IS NULL
                   OR e.Description LIKE @pattern ESCAPE '\' COLLATE NOCASE
                   OR EXISTS (
                       SELECT 1 FROM ConsumptionLines searchLine
                       WHERE searchLine.ConsumptionEventId = e.Id
                         AND searchLine.ItemName LIKE @pattern ESCAPE '\' COLLATE NOCASE))
            ORDER BY e.ConsumedAt DESC, e.Id DESC
            LIMIT @limit;
            """, parameters)).ToList();
        if (events.Count == 0) return events;

        var eventIds = events.Select(consumption => consumption.Id).ToArray();
        var lines = (await conn.QueryAsync<ConsumptionLine>("""
            SELECT Id, ConsumptionEventId, ItemId, ItemName, Quantity, Unit, ExpiryDate
            FROM ConsumptionLines
            WHERE ConsumptionEventId IN @eventIds
            ORDER BY Id;
            """, new { eventIds })).ToList();
        foreach (var consumption in events)
            consumption.Lines = lines.Where(line => line.ConsumptionEventId == consumption.Id).ToList();
        return events;
    }

    public Task<List<ConsumptionEvent>> GetForItemAsync(
        int itemId, bool includeUndone = true, int limit = 20, CancellationToken ct = default) =>
        GetHistoryAsync(new ConsumptionHistoryQuery(
            ItemId: itemId,
            IncludeUndone: includeUndone,
            Limit: limit), ct);

    public async Task<ConsumptionApplied> UseItemAsync(
        int itemId,
        decimal quantity = 1,
        string? description = null,
        CancellationToken ct = default)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Consumed quantity must be greater than zero.");
        using var conn = await db.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        var item = await conn.QuerySingleOrDefaultAsync<Item>("""
            SELECT Id, Name, Quantity, Unit, ExpiryDate
            FROM Items WHERE Id = @itemId;
            """, new { itemId }, tx)
            ?? throw new KeyNotFoundException("The inventory item does not exist.");
        var now = DateTimeOffset.UtcNow;
        var changed = await conn.ExecuteAsync("""
            UPDATE Items SET Quantity = Quantity - @quantity, UpdatedAt = @now
            WHERE Id = @itemId AND Quantity >= @quantity;
            """, new { itemId, quantity, now }, tx);
        if (changed != 1)
            throw new InvalidOperationException($"Only {item.Quantity:0.##} {item.Unit} remains in this stock lot.");

        var lines = new List<ConsumptionLine>
        {
            new()
            {
                ItemId = item.Id,
                ItemName = item.Name,
                Quantity = quantity,
                Unit = item.Unit,
                ExpiryDate = item.ExpiryDate
            }
        };
        var applied = await RecordAsync(
            conn,
            tx,
            ConsumptionKind.Manual,
            Clean(description) ?? $"Used {item.Name}",
            mealPlanEntryId: null,
            bomDefinitionId: null,
            lines,
            now);
        tx.Commit();
        return applied;
    }

    public async Task UndoAsync(int consumptionEventId, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        var consumption = await conn.QuerySingleOrDefaultAsync<ConsumptionEvent>("""
            SELECT Id, Kind, MealPlanEntryId, BomDefinitionId, Description, ConsumedAt, UndoneAt
            FROM ConsumptionEvents WHERE Id = @consumptionEventId;
            """, new { consumptionEventId }, tx)
            ?? throw new KeyNotFoundException("The consumption event does not exist.");
        if (consumption.UndoneAt is not null)
            throw new InvalidOperationException("This consumption event was already undone.");
        var lines = (await conn.QueryAsync<ConsumptionLine>("""
            SELECT Id, ConsumptionEventId, ItemId, ItemName, Quantity, Unit, ExpiryDate
            FROM ConsumptionLines WHERE ConsumptionEventId = @consumptionEventId ORDER BY Id;
            """, new { consumptionEventId }, tx)).ToList();
        if (lines.Count == 0)
            throw new InvalidOperationException("This event has no stock lines to restore.");
        if (lines.Any(line => line.ItemId is null))
            throw new InvalidOperationException(
                "An inventory lot used by this event was deleted, so it cannot be restored automatically.");

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
        {
            await conn.ExecuteAsync("""
                UPDATE MealPlanEntries SET Status = @planned, CookedAt = NULL WHERE Id = @entryId;
                """, new { planned = (int)MealPlanEntryStatus.Planned, entryId }, tx);
            await conn.ExecuteAsync("""
                UPDATE MealPlans SET UpdatedAt = @now
                WHERE Id = (SELECT MealPlanId FROM MealPlanEntries WHERE Id = @entryId);
                """, new { now, entryId }, tx);
        }
        tx.Commit();
    }

    internal async Task<ConsumptionApplied> RecordAsync(
        IDbConnection conn,
        IDbTransaction tx,
        ConsumptionKind kind,
        string description,
        int? mealPlanEntryId,
        int? bomDefinitionId,
        IReadOnlyList<ConsumptionLine> lines,
        DateTimeOffset consumedAt)
    {
        if (lines.Count == 0 || lines.Any(line => line.Quantity <= 0))
            throw new InvalidOperationException("A consumption event requires positive stock lines.");
        var eventId = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO ConsumptionEvents
                (Kind, MealPlanEntryId, BomDefinitionId, Description, ConsumedAt)
            VALUES
                (@kind, @mealPlanEntryId, @bomDefinitionId, @description, @consumedAt);
            SELECT last_insert_rowid();
            """, new
        {
            kind = (int)kind,
            mealPlanEntryId,
            bomDefinitionId,
            description,
            consumedAt
        }, tx);
        foreach (var line in lines)
        {
            line.ConsumptionEventId = eventId;
            line.Id = await conn.ExecuteScalarAsync<int>("""
                INSERT INTO ConsumptionLines
                    (ConsumptionEventId, ItemId, ItemName, Quantity, Unit, ExpiryDate)
                VALUES
                    (@ConsumptionEventId, @ItemId, @ItemName, @Quantity, @Unit, @ExpiryDate);
                SELECT last_insert_rowid();
                """, line, tx);
        }
        return new ConsumptionApplied(eventId, kind, mealPlanEntryId, description, lines);
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
