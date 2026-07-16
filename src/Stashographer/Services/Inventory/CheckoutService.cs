using Dapper;
using Stashographer.Data;
using Stashographer.Data.Entities;

namespace Stashographer.Services.Inventory;

/// <summary>Manages item lending: check out (with a whereabouts note) and check back in.</summary>
public class CheckoutService(IDbConnectionFactory db)
{
    /// <summary>Opens a checkout for an item. Returns null if it is already out.</summary>
    public async Task<CheckoutRecord?> CheckOutAsync(
        int itemId, string checkedOutBy, string? whereabouts, DateOnly? dueDate, string? notes,
        CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var alreadyOut = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM Checkouts WHERE ItemId = @itemId AND ReturnedAt IS NULL)", new { itemId });
        if (alreadyOut) return null;

        var now = DateTimeOffset.UtcNow;
        var id = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO Checkouts (ItemId, CheckedOutBy, WhereaboutsNote, CheckedOutAt, DueDate, Notes)
            VALUES (@itemId, @checkedOutBy, @whereabouts, @at, @due, @notes);
            SELECT last_insert_rowid();
            """, new
        {
            itemId, checkedOutBy, whereabouts, notes,
            at = now.ToString("O"),
            due = dueDate?.ToString("yyyy-MM-dd")
        });

        return new CheckoutRecord
        {
            Id = id, ItemId = itemId, CheckedOutBy = checkedOutBy,
            WhereaboutsNote = whereabouts, DueDate = dueDate, Notes = notes, CheckedOutAt = now
        };
    }

    /// <summary>Closes the open checkout for an item, returning it.</summary>
    public async Task CheckInAsync(int itemId, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE Checkouts SET ReturnedAt = @at WHERE ItemId = @itemId AND ReturnedAt IS NULL",
            new { itemId, at = DateTimeOffset.UtcNow.ToString("O") });
    }

    public async Task<List<CheckoutRecord>> GetOpenAsync(CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<CheckoutRecord>("""
            SELECT c.Id, c.ItemId, i.Name AS ItemName, c.CheckedOutBy, c.WhereaboutsNote,
                   c.CheckedOutAt, c.DueDate, c.ReturnedAt, c.Notes
            FROM Checkouts c JOIN Items i ON i.Id = c.ItemId
            WHERE c.ReturnedAt IS NULL
            ORDER BY COALESCE(c.DueDate, '9999-12-31');
            """);
        return rows.ToList();
    }

    public async Task<CheckoutRecord?> GetOpenForItemAsync(int itemId, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<CheckoutRecord>("""
            SELECT Id, ItemId, CheckedOutBy, WhereaboutsNote, CheckedOutAt, DueDate, ReturnedAt, Notes
            FROM Checkouts WHERE ItemId = @itemId AND ReturnedAt IS NULL LIMIT 1;
            """, new { itemId });
    }

    public async Task<List<CheckoutRecord>> GetHistoryAsync(int itemId, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<CheckoutRecord>("""
            SELECT Id, ItemId, CheckedOutBy, WhereaboutsNote, CheckedOutAt, DueDate, ReturnedAt, Notes
            FROM Checkouts WHERE ItemId = @itemId ORDER BY CheckedOutAt DESC;
            """, new { itemId });
        return rows.ToList();
    }
}
