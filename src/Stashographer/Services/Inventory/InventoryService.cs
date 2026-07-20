using System.Text.Json;
using Dapper;
using Stashographer.Data;
using Stashographer.Data.Entities;

namespace Stashographer.Services.Inventory;

/// <summary>
/// Filter criteria for the inventory list. Kind filtering supports multiple positive
/// (include, OR-ed) and negative (exclude) selections, e.g. "Books + Tools" or "NOT Books".
/// <see cref="LooseOnly"/> restricts to items not inside any container (used with
/// <see cref="LocationId"/> for a room's loose items).
/// </summary>
public record ItemQuery(
    string? Search = null,
    IReadOnlyList<int>? IncludeKindIds = null,
    IReadOnlyList<int>? ExcludeKindIds = null,
    int? LocationId = null,
    int? ContainerId = null,
    bool LooseOnly = false);

/// <summary>An item's placement (for exact move-undo).</summary>
public record ItemPlacement(int ItemId, int? LocationId, int? ContainerId);

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
               i.PhotoPath, i.ImageId, i.AttributesJson, i.Notes, i.CreatedAt, i.UpdatedAt,
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
        if (query.IncludeKindIds is { Count: > 0 }) where.Add("i.ItemKindId IN @IncludeKindIds");
        if (query.ExcludeKindIds is { Count: > 0 }) where.Add("i.ItemKindId NOT IN @ExcludeKindIds");
        if (query.ContainerId is not null) where.Add("i.ContainerId = @ContainerId");
        if (query.LooseOnly) where.Add("i.ContainerId IS NULL");
        if (query.LocationId is not null)
            where.Add(query.LooseOnly
                ? "i.LocationId = @LocationId"
                : "(i.LocationId = @LocationId OR c.LocationId = @LocationId)");
        if (!string.IsNullOrWhiteSpace(query.Search))
            where.Add("(i.Name LIKE @Like OR i.Code LIKE @Like OR i.Description LIKE @Like)");

        var sql = SelectItem
                  + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
                  + " ORDER BY i.Name COLLATE NOCASE";

        using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ItemRow>(sql, new
        {
            query.IncludeKindIds,
            query.ExcludeKindIds,
            query.ContainerId,
            query.LocationId,
            Like = $"%{query.Search}%"
        });
        return rows.Select(Map).ToList();
    }

    /// <summary>
    /// Finds existing items that might be the same product as an identified photo: an exact
    /// barcode match wins outright; otherwise a tokenized fuzzy match over names, scored by
    /// how many name tokens each item contains.
    /// </summary>
    public async Task<List<Item>> FindCandidatesAsync(
        string? name, string? barcode = null, int top = 8, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);

        if (!string.IsNullOrWhiteSpace(barcode))
        {
            var exact = (await conn.QueryAsync<ItemRow>(
                SelectItem + " WHERE i.Code = @barcode", new { barcode })).Select(Map).ToList();
            if (exact.Count > 0) return exact;
        }

        if (string.IsNullOrWhiteSpace(name)) return new List<Item>();

        var tokens = Tokenize(name);
        if (tokens.Count == 0) return new List<Item>();

        var clauses = new List<string>();
        var parameters = new Dapper.DynamicParameters();
        for (var i = 0; i < tokens.Count; i++)
        {
            clauses.Add($"i.Name LIKE @t{i}");
            parameters.Add($"t{i}", $"%{tokens[i]}%");
        }

        var rows = (await conn.QueryAsync<ItemRow>(
            SelectItem + " WHERE " + string.Join(" OR ", clauses), parameters)).Select(Map);

        // Score in memory: number of query tokens the item name contains.
        return rows
            .Select(i => (Item: i, Score: tokens.Count(t => i.Name.Contains(t, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(top)
            .Select(x => x.Item)
            .ToList();
    }

    /// <summary>Alphanumeric-only, lowercased form of a name for equality comparison.</summary>
    public static string NormalizeName(string? name) =>
        new(( name ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static List<string> Tokenize(string name) =>
        name.Split(new[] { ' ', ',', '-', '(', ')', '/', '.' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

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
            item.ImageId,
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
                                   ExpiryDate, LocationId, ContainerId, ThumbnailUrl, PhotoPath, ImageId, AttributesJson,
                                   Notes, CreatedAt, UpdatedAt)
                VALUES (@Code, @Name, @Description, @ItemKindId, @Quantity, @Unit, @LowStockThreshold,
                        @ExpiryDate, @LocationId, @ContainerId, @ThumbnailUrl, @PhotoPath, @ImageId, @AttributesJson,
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
                    ImageId=@ImageId, AttributesJson=@AttributesJson, Notes=@Notes, UpdatedAt=@UpdatedAt
                WHERE Id=@Id;
                """, p);
        }
        return item;
    }

    /// <summary>
    /// Moves items to a place: a container (<paramref name="containerId"/> set — stored
    /// location becomes NULL per the existing convention) or loose into a room. Returns the
    /// previous placements so the move can be undone exactly.
    /// </summary>
    public async Task<List<ItemPlacement>> MoveItemsAsync(
        IReadOnlyList<int> itemIds, int? locationId, int? containerId, CancellationToken ct = default)
    {
        if (itemIds.Count == 0) return new List<ItemPlacement>();
        using var conn = await db.OpenAsync(ct);

        var previous = (await conn.QueryAsync<(int Id, int? LocationId, int? ContainerId)>(
                "SELECT Id, LocationId, ContainerId FROM Items WHERE Id IN @itemIds",
                new { itemIds }))
            .Select(r => new ItemPlacement(r.Id, r.LocationId, r.ContainerId))
            .ToList();

        await conn.ExecuteAsync("""
            UPDATE Items SET
                ContainerId = @containerId,
                LocationId  = @locationId,
                UpdatedAt   = @now
            WHERE Id IN @itemIds;
            """, new
        {
            itemIds,
            containerId,
            locationId = containerId is null ? locationId : null,
            now = DateTimeOffset.UtcNow.ToString("O")
        });

        return previous;
    }

    /// <summary>Puts items back exactly where they were (undo for <see cref="MoveItemsAsync"/>).</summary>
    public async Task RestorePlacementsAsync(IReadOnlyList<ItemPlacement> placements, CancellationToken ct = default)
    {
        if (placements.Count == 0) return;
        using var conn = await db.OpenAsync(ct);
        foreach (var p in placements)
        {
            await conn.ExecuteAsync(
                "UPDATE Items SET LocationId = @LocationId, ContainerId = @ContainerId, UpdatedAt = @now WHERE Id = @ItemId",
                new { p.ItemId, p.LocationId, p.ContainerId, now = DateTimeOffset.UtcNow.ToString("O") });
        }
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
        ImageId = r.ImageId,
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
        public int? ImageId { get; set; }
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
