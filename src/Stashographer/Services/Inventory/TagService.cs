using Dapper;
using Stashographer.Data;
using Stashographer.Data.Entities;

namespace Stashographer.Services.Inventory;

/// <summary>Manages reusable inventory tags and their many-to-many item assignments.</summary>
public sealed class TagService(IDbConnectionFactory db)
{
    private const int MaxNameLength = 50;

    public async Task<List<Tag>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<Tag>("""
            SELECT t.Id, t.Name, t.CreatedAt, t.UpdatedAt, COUNT(it.ItemId) AS ItemCount
            FROM Tags t
            LEFT JOIN ItemTags it ON it.TagId = t.Id
            GROUP BY t.Id
            ORDER BY t.Name COLLATE NOCASE;
            """)).ToList();
    }

    public async Task<Tag> SaveAsync(Tag tag, CancellationToken ct = default)
    {
        tag.Name = NormalizeName(tag.Name);
        using var conn = await db.OpenAsync(ct);
        var duplicate = await conn.QuerySingleOrDefaultAsync<int?>("""
            SELECT Id FROM Tags WHERE Name = @name COLLATE NOCASE AND Id <> @id;
            """, new { name = tag.Name, id = tag.Id });
        if (duplicate is not null)
            throw new InvalidOperationException($"A tag named “{tag.Name}” already exists.");

        var now = DateTimeOffset.UtcNow;
        if (tag.Id == 0)
        {
            tag.CreatedAt = now;
            tag.UpdatedAt = now;
            tag.Id = await conn.ExecuteScalarAsync<int>("""
                INSERT INTO Tags (Name, CreatedAt, UpdatedAt)
                VALUES (@Name, @CreatedAt, @UpdatedAt);
                SELECT last_insert_rowid();
                """, tag);
        }
        else
        {
            tag.UpdatedAt = now;
            var changed = await conn.ExecuteAsync("""
                UPDATE Tags SET Name = @Name, UpdatedAt = @UpdatedAt WHERE Id = @Id;
                """, tag);
            if (changed == 0) throw new InvalidOperationException("The tag no longer exists.");
        }
        return tag;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM Tags WHERE Id = @id;", new { id });
    }

    public async Task<List<Tag>> GetForItemAsync(int itemId, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<Tag>("""
            SELECT t.Id, t.Name, t.CreatedAt, t.UpdatedAt, 0 AS ItemCount
            FROM Tags t
            JOIN ItemTags it ON it.TagId = t.Id
            WHERE it.ItemId = @itemId
            ORDER BY t.Name COLLATE NOCASE;
            """, new { itemId })).ToList();
    }

    public async Task SetForItemAsync(
        int itemId, IEnumerable<int> tagIds, CancellationToken ct = default)
    {
        var selected = tagIds.Distinct().Order().ToArray();
        using var conn = await db.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        if (await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM Items WHERE Id = @itemId;", new { itemId }, tx) == 0)
            throw new InvalidOperationException("The inventory item no longer exists.");
        if (selected.Length > 0)
        {
            var valid = (await conn.QueryAsync<int>(
                "SELECT Id FROM Tags WHERE Id IN @selected;", new { selected }, tx)).ToHashSet();
            if (valid.Count != selected.Length)
                throw new InvalidOperationException("One or more selected tags no longer exist.");
        }

        await conn.ExecuteAsync("DELETE FROM ItemTags WHERE ItemId = @itemId;", new { itemId }, tx);
        foreach (var tagId in selected)
            await conn.ExecuteAsync(
                "INSERT INTO ItemTags (ItemId, TagId) VALUES (@itemId, @tagId);",
                new { itemId, tagId }, tx);
        tx.Commit();
    }

    /// <summary>Populates tags for a batch without issuing one query per item.</summary>
    public async Task PopulateAsync(IReadOnlyCollection<Item> items, CancellationToken ct = default)
    {
        if (items.Count == 0) return;
        var byId = items.ToDictionary(item => item.Id);
        foreach (var item in items) item.Tags = [];
        using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ItemTagRow>("""
            SELECT it.ItemId, t.Id AS TagId, t.Name, t.CreatedAt, t.UpdatedAt
            FROM ItemTags it
            JOIN Tags t ON t.Id = it.TagId
            WHERE it.ItemId IN @itemIds
            ORDER BY t.Name COLLATE NOCASE;
            """, new { itemIds = byId.Keys.ToArray() });
        foreach (var row in rows)
            if (byId.TryGetValue(row.ItemId, out var item))
                item.Tags.Add(new Tag
                {
                    Id = row.TagId,
                    Name = row.Name,
                    CreatedAt = row.CreatedAt,
                    UpdatedAt = row.UpdatedAt
                });
    }

    internal static string NormalizeName(string? name)
    {
        var normalized = string.Join(' ', (name ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (normalized.Length == 0) throw new InvalidOperationException("Enter a tag name.");
        if (normalized.Length > MaxNameLength)
            throw new InvalidOperationException($"Tag names can be at most {MaxNameLength} characters.");
        return normalized;
    }

    private sealed class ItemTagRow
    {
        public int ItemId { get; set; }
        public int TagId { get; set; }
        public string Name { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
