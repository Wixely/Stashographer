using System.Text.Json;
using Dapper;
using Stashographer.Data;
using Stashographer.Data.Entities;

namespace Stashographer.Services.Inventory;

/// <summary>Filter criteria for the inventory list.</summary>
public record ItemQuery(
    string? Search = null,
    int? KindId = null,
    int? LocationId = null,
    int? ContainerId = null);

public record DashboardSummary(
    int TotalItems,
    decimal TotalQuantity,
    List<Item> LowStock,
    List<Item> ExpiringSoon,
    List<Item> CheckedOut);

/// <summary>CRUD and query operations for inventory items, via Dapper.</summary>
public class InventoryService(IDbConnectionFactory db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Shared projection: item columns + joined display names + open-checkout flag.
    private const string SelectItem = """
        SELECT i.Id, i.Code, i.Name, i.Description, i.ItemKindId, i.Quantity, i.Unit,
               i.LowStockThreshold, i.ExpiryDate, i.LocationId, i.ContainerId, i.ThumbnailUrl,
               i.PhotoPath, i.AttributesJson, i.Notes, i.CreatedAt, i.UpdatedAt,
               k.Name AS KindName, k.Icon AS KindIcon,
               dl.Name AS DirectLocationName,
               c.Name AS ContainerName, c.LocationId AS ContainerLocationId,
               cl.Name AS ContainerLocationName,
               EXISTS (SELECT 1 FROM Checkouts co WHERE co.ItemId = i.Id AND co.ReturnedAt IS NULL) AS IsCheckedOut
        FROM Items i
        JOIN ItemKinds k ON k.Id = i.ItemKindId
        LEFT JOIN Locations dl ON dl.Id = i.LocationId
        LEFT JOIN Containers c ON c.Id = i.ContainerId
        LEFT JOIN Locations cl ON cl.Id = c.LocationId
        """;

    public async Task<List<Item>> QueryAsync(ItemQuery query, CancellationToken ct = default)
    {
        var where = new List<string>();
        if (query.KindId is not null) where.Add("i.ItemKindId = @KindId");
        if (query.ContainerId is not null) where.Add("i.ContainerId = @ContainerId");
        if (query.LocationId is not null) where.Add("(i.LocationId = @LocationId OR c.LocationId = @LocationId)");
        if (!string.IsNullOrWhiteSpace(query.Search))
            where.Add("(i.Name LIKE @Like OR i.Code LIKE @Like OR i.Description LIKE @Like)");

        var sql = SelectItem
                  + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
                  + " ORDER BY i.Name COLLATE NOCASE";

        using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ItemRow>(sql, new
        {
            query.KindId,
            query.ContainerId,
            query.LocationId,
            Like = $"%{query.Search}%"
        });
        return rows.Select(Map).ToList();
    }

    public async Task<Item?> GetAsync(int id, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ItemRow>(SelectItem + " WHERE i.Id = @id", new { id });
        return row is null ? null : Map(row);
    }

    public async Task<Item> SaveAsync(Item item, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var p = new
        {
            item.Code,
            item.Name,
            item.Description,
            item.ItemKindId,
            item.Quantity,
            item.Unit,
            item.LowStockThreshold,
            ExpiryDate = item.ExpiryDate?.ToString("yyyy-MM-dd"),
            item.LocationId,
            item.ContainerId,
            item.ThumbnailUrl,
            item.PhotoPath,
            AttributesJson = JsonSerializer.Serialize(item.Attributes, Json),
            item.Notes,
            CreatedAt = now.ToString("O"),
            UpdatedAt = now.ToString("O"),
            item.Id
        };

        if (item.Id == 0)
        {
            item.Id = await conn.ExecuteScalarAsync<int>("""
                INSERT INTO Items (Code, Name, Description, ItemKindId, Quantity, Unit, LowStockThreshold,
                                   ExpiryDate, LocationId, ContainerId, ThumbnailUrl, PhotoPath, AttributesJson,
                                   Notes, CreatedAt, UpdatedAt)
                VALUES (@Code, @Name, @Description, @ItemKindId, @Quantity, @Unit, @LowStockThreshold,
                        @ExpiryDate, @LocationId, @ContainerId, @ThumbnailUrl, @PhotoPath, @AttributesJson,
                        @Notes, @CreatedAt, @UpdatedAt);
                SELECT last_insert_rowid();
                """, p);
        }
        else
        {
            await conn.ExecuteAsync("""
                UPDATE Items SET Code=@Code, Name=@Name, Description=@Description, ItemKindId=@ItemKindId,
                    Quantity=@Quantity, Unit=@Unit, LowStockThreshold=@LowStockThreshold, ExpiryDate=@ExpiryDate,
                    LocationId=@LocationId, ContainerId=@ContainerId, ThumbnailUrl=@ThumbnailUrl, PhotoPath=@PhotoPath,
                    AttributesJson=@AttributesJson, Notes=@Notes, UpdatedAt=@UpdatedAt
                WHERE Id=@Id;
                """, p);
        }
        return item;
    }

    public async Task AdjustQuantityAsync(int id, decimal delta, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE Items SET Quantity = MAX(0, Quantity + @delta), UpdatedAt = @now WHERE Id = @id;
            """, new { id, delta, now = DateTimeOffset.UtcNow.ToString("O") });
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM Items WHERE Id = @id", new { id });
    }

    public async Task<List<ItemKind>> GetKindsAsync(CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var kinds = await conn.QueryAsync<ItemKind>(
            "SELECT Id, Name, Icon, SuggestedAttributes, IsSystem FROM ItemKinds ORDER BY Name");
        return kinds.ToList();
    }

    public async Task<DashboardSummary> GetDashboardAsync(CancellationToken ct = default)
    {
        var soon = DateOnly.FromDateTime(DateTime.Today).AddDays(7).ToString("yyyy-MM-dd");
        using var conn = await db.OpenAsync(ct);

        var total = await conn.QuerySingleAsync<int>("SELECT COUNT(*) FROM Items");
        var totalQty = await conn.ExecuteScalarAsync<decimal?>("SELECT SUM(Quantity) FROM Items") ?? 0m;

        var lowStock = (await conn.QueryAsync<ItemRow>(
            SelectItem + " WHERE i.LowStockThreshold > 0 AND i.Quantity <= i.LowStockThreshold ORDER BY i.Quantity LIMIT 20"))
            .Select(Map).ToList();

        var expiring = (await conn.QueryAsync<ItemRow>(
            SelectItem + " WHERE i.ExpiryDate IS NOT NULL AND i.ExpiryDate <= @soon ORDER BY i.ExpiryDate LIMIT 20",
            new { soon }))
            .Select(Map).ToList();

        var checkedOut = (await conn.QueryAsync<ItemRow>(
            SelectItem + " WHERE EXISTS (SELECT 1 FROM Checkouts co WHERE co.ItemId = i.Id AND co.ReturnedAt IS NULL)"
                       + " ORDER BY i.Name LIMIT 20"))
            .Select(Map).ToList();

        return new DashboardSummary(total, totalQty, lowStock, expiring, checkedOut);
    }

    private static Item Map(ItemRow r) => new()
    {
        Id = r.Id,
        Code = r.Code,
        Name = r.Name,
        Description = r.Description,
        ItemKindId = r.ItemKindId,
        Quantity = r.Quantity,
        Unit = r.Unit,
        LowStockThreshold = r.LowStockThreshold,
        ExpiryDate = string.IsNullOrEmpty(r.ExpiryDate) ? null : DateOnly.Parse(r.ExpiryDate),
        LocationId = r.LocationId,
        ContainerId = r.ContainerId,
        ThumbnailUrl = r.ThumbnailUrl,
        PhotoPath = r.PhotoPath,
        Notes = r.Notes,
        CreatedAt = DateTimeOffset.Parse(r.CreatedAt),
        UpdatedAt = DateTimeOffset.Parse(r.UpdatedAt),
        IsCheckedOut = r.IsCheckedOut,
        Attributes = string.IsNullOrWhiteSpace(r.AttributesJson)
            ? new()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(r.AttributesJson, Json) ?? new(),
        Kind = new ItemKind { Id = r.ItemKindId, Name = r.KindName ?? "", Icon = r.KindIcon },
        Location = r.LocationId is { } lid ? new Location { Id = lid, Name = r.DirectLocationName ?? "" } : null,
        Container = r.ContainerId is { } cid
            ? new Container
            {
                Id = cid,
                Name = r.ContainerName ?? "",
                LocationId = r.ContainerLocationId ?? 0,
                Location = new Location { Id = r.ContainerLocationId ?? 0, Name = r.ContainerLocationName ?? "" }
            }
            : null
    };

    /// <summary>Flat row shape for the item projection above.</summary>
    private sealed class ItemRow
    {
        public int Id { get; set; }
        public string? Code { get; set; }
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public int ItemKindId { get; set; }
        public decimal Quantity { get; set; }
        public string? Unit { get; set; }
        public decimal LowStockThreshold { get; set; }
        public string? ExpiryDate { get; set; }
        public int? LocationId { get; set; }
        public int? ContainerId { get; set; }
        public string? ThumbnailUrl { get; set; }
        public string? PhotoPath { get; set; }
        public string? AttributesJson { get; set; }
        public string? Notes { get; set; }
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public string? KindName { get; set; }
        public string? KindIcon { get; set; }
        public string? DirectLocationName { get; set; }
        public string? ContainerName { get; set; }
        public int? ContainerLocationId { get; set; }
        public string? ContainerLocationName { get; set; }
        public bool IsCheckedOut { get; set; }
    }
}
