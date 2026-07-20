using Dapper;
using Stashographer.Data;
using Stashographer.Data.Entities;

namespace Stashographer.Services.Inventory;

/// <summary>CRUD and ordering for the home-screen quick links.</summary>
public class QuickLinksService(IDbConnectionFactory db)
{
    private const string Columns = "Id, Label, Icon, Target, IncludeKindIds, ExcludeKindIds, SortOrder";

    public async Task<List<QuickLink>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var links = await conn.QueryAsync<QuickLink>(
            $"SELECT {Columns} FROM QuickLinks ORDER BY SortOrder, Id");
        return links.ToList();
    }

    public async Task<QuickLink?> GetAsync(int id, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<QuickLink>(
            $"SELECT {Columns} FROM QuickLinks WHERE Id = @id", new { id });
    }

    public async Task<QuickLink> SaveAsync(QuickLink link, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        if (link.Id == 0)
        {
            if (link.SortOrder == 0)
                link.SortOrder = (await conn.ExecuteScalarAsync<int?>("SELECT MAX(SortOrder) FROM QuickLinks") ?? 0) + 1;
            link.Id = await conn.ExecuteScalarAsync<int>("""
                INSERT INTO QuickLinks (Label, Icon, Target, IncludeKindIds, ExcludeKindIds, SortOrder)
                VALUES (@Label, @Icon, @Target, @IncludeKindIds, @ExcludeKindIds, @SortOrder);
                SELECT last_insert_rowid();
                """, link);
        }
        else
        {
            await conn.ExecuteAsync("""
                UPDATE QuickLinks SET Label=@Label, Icon=@Icon, Target=@Target,
                    IncludeKindIds=@IncludeKindIds, ExcludeKindIds=@ExcludeKindIds, SortOrder=@SortOrder
                WHERE Id=@Id
                """, link);
        }
        return link;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM QuickLinks WHERE Id = @id", new { id });
    }

    /// <summary>Swaps a link's order with its neighbour in the given direction (-1 up, +1 down).</summary>
    public async Task MoveAsync(int id, int direction, CancellationToken ct = default)
    {
        var links = await GetAllAsync(ct);
        var index = links.FindIndex(l => l.Id == id);
        var target = index + Math.Sign(direction);
        if (index < 0 || target < 0 || target >= links.Count) return;

        (links[index].SortOrder, links[target].SortOrder) = (links[target].SortOrder, links[index].SortOrder);
        await SaveAsync(links[index], ct);
        await SaveAsync(links[target], ct);
    }
}
