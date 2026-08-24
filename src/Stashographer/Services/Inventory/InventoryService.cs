using System.Text.Json;
using Dapper;
using Stashographer.Data;
using Stashographer.Data.Entities;
using Stashographer.Services.Images;

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
    bool LooseOnly = false,
    ItemSort Sort = ItemSort.Name);

public enum ItemSort
{
    Name,
    PriceLowToHigh,
    PriceHighToLow
}

/// <summary>An item's placement (for exact move-undo).</summary>
public record ItemPlacement(int ItemId, int? LocationId, int? ContainerId);

/// <summary>The source and newly placed portion produced by a quantity split.</summary>
public record ItemSplit(Item Source, Item Created);

public record DashboardSummary(
    int TotalItems,
    decimal TotalQuantity,
    List<Item> LowStock,
    List<Item> ExpiringSoon,
    List<Item> CheckedOut,
    List<PriceMetric> PriceMetrics);

/// <summary>Active inventory grouped into non-overlapping expiry windows.</summary>
public sealed record ExpiryOverview(
    DateOnly Today,
    List<Item> Expired,
    List<Item> DueToday,
    List<Item> NextThreeDays,
    List<Item> DaysFourToSeven,
    List<Item> Later,
    List<Item> MissingFoodDate)
{
    public int DatedCount => Expired.Count + DueToday.Count + NextThreeDays.Count
                             + DaysFourToSeven.Count + Later.Count;
    public int DueWithinSevenDaysCount => DueToday.Count + NextThreeDays.Count + DaysFourToSeven.Count;
}

/// <summary>Price metrics remain separated by currency until an explicit exchange rate is supplied.</summary>
public sealed class PriceMetric
{
    public string CurrencyCode { get; set; } = string.Empty;
    public int PricedEntries { get; set; }
    public decimal TotalValue { get; set; }
    public decimal MinimumUnitPrice { get; set; }
    public decimal MaximumUnitPrice { get; set; }
}

/// <summary>CRUD and query operations for inventory items, via Dapper.</summary>
public class InventoryService(
    IDbConnectionFactory db,
    ImageService? images = null,
    AttributeNameService? attributeNames = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Shared projection: item columns + joined display names + open-checkout flag.
    private const string SelectItem = """
        SELECT i.Id, i.CollectionKey, i.Code, i.Name, i.Description, i.ItemKindId, i.Quantity, i.Unit,
               i.LowStockThreshold, i.ExpiryDate, i.LocationId, i.ContainerId, i.ThumbnailUrl,
               i.PhotoPath, i.ImageId, i.AttributesJson, i.SpecialAttributesJson, i.Notes, i.CreatedAt, i.UpdatedAt,
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

        var orderBy = query.Sort switch
        {
            ItemSort.PriceLowToHigh =>
                "CASE WHEN json_extract(i.SpecialAttributesJson, '$.price.decimalValue') IS NULL THEN 1 ELSE 0 END, " +
                "json_extract(i.SpecialAttributesJson, '$.price.currencyCode'), " +
                "CAST(json_extract(i.SpecialAttributesJson, '$.price.decimalValue') AS NUMERIC), i.Name COLLATE NOCASE",
            ItemSort.PriceHighToLow =>
                "CASE WHEN json_extract(i.SpecialAttributesJson, '$.price.decimalValue') IS NULL THEN 1 ELSE 0 END, " +
                "json_extract(i.SpecialAttributesJson, '$.price.currencyCode'), " +
                "CAST(json_extract(i.SpecialAttributesJson, '$.price.decimalValue') AS NUMERIC) DESC, i.Name COLLATE NOCASE",
            _ => "i.Name COLLATE NOCASE"
        };
        var sql = SelectItem
                  + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
                  + " ORDER BY " + orderBy;

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

    /// <summary>
    /// True when a newly observed expiry cannot safely be merged into an existing aggregate
    /// quantity. An observed date on previously undated stock is also distinct: it describes
    /// the new units, not every older unit.
    /// </summary>
    public static bool RequiresSeparateStockLot(Item existing, Item observed) =>
        existing.Quantity > 0
        && SpecialAttributeCatalog.GetExpiry(observed)?.DateValue is { } observedDate
        && SpecialAttributeCatalog.GetExpiry(existing)?.DateValue != observedDate;

    /// <summary>Returns every independently tracked lot/place entry for the same product collection.</summary>
    public async Task<List<Item>> GetCollectionMembersAsync(int itemId, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var collectionKey = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT CollectionKey FROM Items WHERE Id = @itemId", new { itemId });
        if (string.IsNullOrWhiteSpace(collectionKey))
        {
            var row = await conn.QuerySingleOrDefaultAsync<ItemRow>(SelectItem + " WHERE i.Id = @itemId", new { itemId });
            return row is null ? new List<Item>() : [Map(row)];
        }

        var rows = await conn.QueryAsync<ItemRow>(
            SelectItem + " WHERE i.CollectionKey = @collectionKey ORDER BY i.Id", new { collectionKey });
        return rows.Select(Map).ToList();
    }

    public async Task<Item> SaveAsync(Item item, CancellationToken ct = default)
    {
        await TryIngestRemoteThumbnailAsync(item, ct);
        if (attributeNames is not null && item.Attributes.Count > 0)
            item.Attributes = await attributeNames.CanonicalizeAsync(
                item.Attributes, kindId: item.ItemKindId, ct: ct);
        SpecialAttributeCatalog.PromoteFromOrdinaryAttributes(item);
        SpecialAttributeCatalog.Normalize(item);

        using var conn = await db.OpenAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var p = new
        {
            item.CollectionKey,
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
            SpecialAttributesJson = JsonSerializer.Serialize(item.SpecialAttributes, Json),
            item.Notes,
            CreatedAt = now.ToString("O"),
            UpdatedAt = now.ToString("O"),
            item.Id
        };

        if (item.Id == 0)
        {
            item.Id = await conn.ExecuteScalarAsync<int>("""
                INSERT INTO Items (CollectionKey, Code, Name, Description, ItemKindId, Quantity, Unit, LowStockThreshold,
                                   ExpiryDate, LocationId, ContainerId, ThumbnailUrl, PhotoPath, ImageId, AttributesJson,
                                   SpecialAttributesJson, Notes, CreatedAt, UpdatedAt)
                VALUES (@CollectionKey, @Code, @Name, @Description, @ItemKindId, @Quantity, @Unit, @LowStockThreshold,
                        @ExpiryDate, @LocationId, @ContainerId, @ThumbnailUrl, @PhotoPath, @ImageId, @AttributesJson,
                        @SpecialAttributesJson, @Notes, @CreatedAt, @UpdatedAt);
                SELECT last_insert_rowid();
                """, p);
        }
        else
        {
            await conn.ExecuteAsync("""
                UPDATE Items SET CollectionKey=@CollectionKey, Code=@Code, Name=@Name, Description=@Description, ItemKindId=@ItemKindId,
                    Quantity=@Quantity, Unit=@Unit, LowStockThreshold=@LowStockThreshold, ExpiryDate=@ExpiryDate,
                    LocationId=@LocationId, ContainerId=@ContainerId, ThumbnailUrl=@ThumbnailUrl, PhotoPath=@PhotoPath,
                    ImageId=@ImageId, AttributesJson=@AttributesJson, SpecialAttributesJson=@SpecialAttributesJson,
                    Notes=@Notes, UpdatedAt=@UpdatedAt
                WHERE Id=@Id;
                """, p);
        }
        await SynchronizePrimaryImageAsync(conn, item.Id, item.ImageId, now, ct);
        return item;
    }

    /// <summary>Loads every stored view for an item in display order.</summary>
    public async Task<List<ItemImage>> GetImagesAsync(int itemId, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ItemImageRow>("""
            SELECT ii.ItemId, ii.ImageId, ii.Role, ii.IsPrimary, ii.SortOrder, ii.CreatedAt AS LinkedAt,
                   im.StorageKey, im.ContentType, im.OriginalName, im.Width, im.Height,
                   im.ByteSize, im.Sha256, im.SourceUrl, im.CreatedAt AS ImageCreatedAt
            FROM ItemImages ii
            JOIN Images im ON im.Id = ii.ImageId
            WHERE ii.ItemId = @itemId
            ORDER BY ii.IsPrimary DESC, ii.SortOrder, ii.CreatedAt;
            """, new { itemId });
        return rows.Select(MapItemImage).ToList();
    }

    /// <summary>
    /// Associates an image without changing quantity. The first image becomes primary;
    /// otherwise the requested semantic role is retained.
    /// </summary>
    public async Task<ItemImage> AttachImageAsync(
        int itemId,
        int imageId,
        ItemImageRole role = ItemImageRole.Detail,
        bool makePrimary = false,
        CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        if (await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM Items WHERE Id = @itemId", new { itemId }, tx) == 0)
            throw new InvalidOperationException("The inventory item no longer exists.");
        if (await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM Images WHERE Id = @imageId", new { imageId }, tx) == 0)
            throw new InvalidOperationException("The stored image no longer exists.");

        var hasPrimary = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM ItemImages WHERE ItemId = @itemId AND IsPrimary = 1",
            new { itemId }, tx) > 0;
        var becomesPrimary = makePrimary || (!hasPrimary && role != ItemImageRole.Receipt);
        if (becomesPrimary)
        {
            await DemotePrimaryAsync(conn, tx, itemId, imageId);
        }

        var sortOrder = await conn.ExecuteScalarAsync<int?>(
            "SELECT MAX(SortOrder) FROM ItemImages WHERE ItemId = @itemId", new { itemId }, tx) ?? -1;
        var now = DateTimeOffset.UtcNow;
        await conn.ExecuteAsync("""
            INSERT INTO ItemImages (ItemId, ImageId, Role, IsPrimary, SortOrder, CreatedAt)
            VALUES (@itemId, @imageId, @role, @isPrimary, @sortOrder, @createdAt)
            ON CONFLICT(ItemId, ImageId) DO UPDATE SET
                Role = excluded.Role,
                IsPrimary = CASE WHEN excluded.IsPrimary = 1 THEN 1 ELSE ItemImages.IsPrimary END;
            """, new
        {
            itemId,
            imageId,
            role = (int)role,
            isPrimary = becomesPrimary ? 1 : 0,
            sortOrder = sortOrder + 1,
            createdAt = now.ToString("O")
        }, tx);
        if (becomesPrimary)
            await conn.ExecuteAsync(
                "UPDATE Items SET ImageId = @imageId, UpdatedAt = @now WHERE Id = @itemId",
                new { itemId, imageId, now = now.ToString("O") }, tx);
        tx.Commit();
        return (await GetImagesAsync(itemId, ct)).Single(link => link.ImageId == imageId);
    }

    public async Task SetPrimaryImageAsync(int itemId, int imageId, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        var linked = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM ItemImages WHERE ItemId = @itemId AND ImageId = @imageId",
            new { itemId, imageId }, tx);
        if (linked == 0) throw new InvalidOperationException("Attach the image before making it primary.");
        await DemotePrimaryAsync(conn, tx, itemId, imageId);
        await conn.ExecuteAsync(
            "UPDATE ItemImages SET IsPrimary = 1 WHERE ItemId = @itemId AND ImageId = @imageId",
            new { itemId, imageId }, tx);
        await conn.ExecuteAsync(
            "UPDATE Items SET ImageId = @imageId, UpdatedAt = @now WHERE Id = @itemId",
            new { itemId, imageId, now = DateTimeOffset.UtcNow.ToString("O") }, tx);
        tx.Commit();
    }

    public async Task UpdateImageRoleAsync(
        int itemId, int imageId, ItemImageRole role, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var exists = await conn.QuerySingleOrDefaultAsync<int?>(
            "SELECT Role FROM ItemImages WHERE ItemId = @itemId AND ImageId = @imageId",
            new { itemId, imageId });
        if (exists is null) throw new InvalidOperationException("The image is not attached to this item.");
        await conn.ExecuteAsync(
            "UPDATE ItemImages SET Role = @role WHERE ItemId = @itemId AND ImageId = @imageId",
            new { itemId, imageId, role = (int)role });
    }

    public async Task DetachImageAsync(int itemId, int imageId, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        var wasPrimary = await conn.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM ItemImages
            WHERE ItemId = @itemId AND ImageId = @imageId AND IsPrimary = 1;
            """, new { itemId, imageId }, tx) > 0;
        await conn.ExecuteAsync(
            "DELETE FROM ItemImages WHERE ItemId = @itemId AND ImageId = @imageId",
            new { itemId, imageId }, tx);
        if (wasPrimary)
            await PromoteFirstRemainingAsync(conn, tx, itemId);
        tx.Commit();
    }

    /// <summary>
    /// Moves part of an item's quantity into a newly linked entry at another destination.
    /// Each entry remains a homogeneous stock holding; the source reduction and new entry
    /// are committed transactionally.
    /// </summary>
    public Task<ItemSplit> SplitAsync(
        int itemId, decimal quantity, int? locationId, int? containerId,
        CancellationToken ct = default) =>
        SplitCoreAsync(itemId, quantity, locationId, containerId, null, ExpiryDateKind.Unknown, ct);

    /// <summary>
    /// Separates part of an aggregate quantity into a same-product stock lot with its own
    /// expiry. The new lot stays in the current place and the original expiry is untouched.
    /// </summary>
    public Task<ItemSplit> SplitLotAsync(
        int itemId, decimal quantity, DateOnly expiryDate,
        ExpiryDateKind expiryKind = ExpiryDateKind.Unknown,
        CancellationToken ct = default) =>
        SplitCoreAsync(itemId, quantity, null, null, expiryDate, expiryKind, ct);

    private async Task<ItemSplit> SplitCoreAsync(
        int itemId, decimal quantity, int? locationId, int? containerId,
        DateOnly? lotExpiryDate, ExpiryDateKind expiryKind, CancellationToken ct)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Split quantity must be greater than zero.");
        if (lotExpiryDate is null && locationId is null && containerId is null)
            throw new InvalidOperationException("Choose a destination for the split quantity.");
        if (locationId is not null && containerId is not null)
            throw new InvalidOperationException("Choose either a location or a container, not both.");

        using var conn = await db.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        var source = await conn.QuerySingleOrDefaultAsync<SplitSource>("""
            SELECT i.Id, i.Quantity, i.LocationId, i.ContainerId, i.CollectionKey,
                   i.ExpiryDate, i.SpecialAttributesJson,
                   EXISTS (SELECT 1 FROM Checkouts c WHERE c.ItemId = i.Id AND c.ReturnedAt IS NULL) AS IsCheckedOut
            FROM Items i WHERE i.Id = @itemId;
            """, new { itemId }, tx);
        if (source is null) throw new InvalidOperationException("The item no longer exists.");
        if (source.IsCheckedOut) throw new InvalidOperationException("Check the item in before splitting its quantity.");
        if (quantity >= source.Quantity)
            throw new InvalidOperationException("The split quantity must leave some quantity in the current place.");

        if (lotExpiryDate is { } newExpiryDate)
        {
            var sourceExpiry = string.IsNullOrWhiteSpace(source.ExpiryDate)
                ? (DateOnly?)null
                : DateOnly.Parse(source.ExpiryDate);
            if (sourceExpiry == newExpiryDate)
                throw new InvalidOperationException("Choose an expiry date different from the current stock entry.");
            locationId = source.LocationId;
            containerId = source.ContainerId;
        }
        else if (containerId is { } targetContainerId)
        {
            var exists = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM Containers WHERE Id = @targetContainerId",
                new { targetContainerId }, tx);
            if (exists == 0) throw new InvalidOperationException("The destination container no longer exists.");
            locationId = null;
        }
        else if (locationId is { } targetLocationId)
        {
            var exists = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM Locations WHERE Id = @targetLocationId",
                new { targetLocationId }, tx);
            if (exists == 0) throw new InvalidOperationException("The destination location no longer exists.");
        }

        if (lotExpiryDate is null && source.LocationId == locationId && source.ContainerId == containerId)
            throw new InvalidOperationException("Choose a different place for the split quantity.");

        var collectionKey = source.CollectionKey ?? Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToString("O");
        var changed = await conn.ExecuteAsync("""
            UPDATE Items SET Quantity = Quantity - @quantity, CollectionKey = @collectionKey, UpdatedAt = @now
            WHERE Id = @itemId AND Quantity > @quantity;
            """, new { itemId, quantity, collectionKey, now }, tx);
        if (changed != 1)
            throw new InvalidOperationException("The item quantity changed before it could be split. Reload and try again.");

        var createdId = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO Items
                (CollectionKey, Code, Name, Description, ItemKindId, Quantity, Unit, LowStockThreshold,
                 ExpiryDate, LocationId, ContainerId, ThumbnailUrl, PhotoPath, ImageId, AttributesJson,
                 SpecialAttributesJson, Notes, CreatedAt, UpdatedAt)
            SELECT @collectionKey, Code, Name, Description, ItemKindId, @quantity, Unit, LowStockThreshold,
                   ExpiryDate, @locationId, @containerId, ThumbnailUrl, PhotoPath, ImageId, AttributesJson,
                   SpecialAttributesJson, Notes, @now, @now
            FROM Items WHERE Id = @itemId;
            SELECT last_insert_rowid();
            """, new { itemId, quantity, collectionKey, locationId, containerId, now }, tx);
        if (lotExpiryDate is { } expiryDate)
        {
            var specialAttributes = DeserializeSpecialAttributes(source.SpecialAttributesJson);
            var lot = new Item { SpecialAttributes = specialAttributes };
            SpecialAttributeCatalog.SetExpiry(lot, expiryDate, expiryKind,
                new SpecialAttributeEvidence { Source = "user" });
            await conn.ExecuteAsync("""
                UPDATE Items SET ExpiryDate = @expiryDate, SpecialAttributesJson = @specialAttributes
                WHERE Id = @createdId;
                """, new
            {
                createdId,
                expiryDate = expiryDate.ToString("yyyy-MM-dd"),
                specialAttributes = JsonSerializer.Serialize(lot.SpecialAttributes, Json)
            }, tx);
        }
        await conn.ExecuteAsync("""
            INSERT INTO ItemImages (ItemId, ImageId, Role, IsPrimary, SortOrder, CreatedAt)
            SELECT @createdId, ImageId, Role, IsPrimary, SortOrder, @now
            FROM ItemImages WHERE ItemId = @itemId;
            """, new { itemId, createdId, now }, tx);
        tx.Commit();

        var remaining = await GetAsync(itemId, ct)
            ?? throw new InvalidOperationException("The source item disappeared after splitting.");
        var created = await GetAsync(createdId, ct)
            ?? throw new InvalidOperationException("The split item disappeared after creation.");
        return new ItemSplit(remaining, created);
    }

    /// <summary>
    /// Adds newly acquired units as a linked stock lot when their observed expiry differs
    /// from the matched product entry. Shared product metadata is copied, while lot-specific
    /// special attributes, placement, and the new evidence image remain independent.
    /// </summary>
    public async Task<Item> CreateStockLotAsync(
        int matchedItemId, Item observed, CancellationToken ct = default)
    {
        if (observed.Quantity <= 0)
            throw new InvalidOperationException("Stock-lot quantity must be greater than zero.");
        if (attributeNames is not null && observed.Attributes.Count > 0)
            observed.Attributes = await attributeNames.CanonicalizeAsync(
                observed.Attributes, kindId: observed.ItemKindId, ct: ct);
        SpecialAttributeCatalog.PromoteFromOrdinaryAttributes(observed);
        SpecialAttributeCatalog.Normalize(observed);

        using var conn = await db.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        var row = await conn.QuerySingleOrDefaultAsync<ItemRow>(
            SelectItem + " WHERE i.Id = @matchedItemId", new { matchedItemId }, tx);
        if (row is null) throw new InvalidOperationException("The matched inventory item no longer exists.");
        var source = Map(row);
        if (!RequiresSeparateStockLot(source, observed))
            throw new InvalidOperationException("The observed expiry does not require a separate stock lot.");

        var collectionKey = source.CollectionKey ?? Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var lot = new Item
        {
            CollectionKey = collectionKey,
            Code = source.Code ?? observed.Code,
            Name = source.Name,
            Description = source.Description ?? observed.Description,
            ItemKindId = source.ItemKindId,
            Quantity = observed.Quantity,
            Unit = source.Unit ?? observed.Unit,
            LowStockThreshold = source.LowStockThreshold,
            LocationId = observed.ContainerId is null ? observed.LocationId ?? source.LocationId : null,
            ContainerId = observed.ContainerId ?? (observed.LocationId is null ? source.ContainerId : null),
            ThumbnailUrl = source.ThumbnailUrl ?? observed.ThumbnailUrl,
            PhotoPath = source.PhotoPath ?? observed.PhotoPath,
            ImageId = observed.ImageId ?? source.ImageId,
            Attributes = new(source.Attributes),
            SpecialAttributes = new(source.SpecialAttributes),
            Notes = observed.Notes ?? source.Notes,
            CreatedAt = now,
            UpdatedAt = now
        };
        foreach (var (name, value) in observed.Attributes)
            if (!lot.Attributes.Keys.Any(existing =>
                    string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
                lot.Attributes[name] = value;
        foreach (var (key, value) in observed.SpecialAttributes)
            lot.SpecialAttributes[key] = value;
        SpecialAttributeCatalog.Normalize(lot);

        await conn.ExecuteAsync(
            "UPDATE Items SET CollectionKey = @collectionKey, UpdatedAt = @now WHERE Id = @matchedItemId",
            new { matchedItemId, collectionKey, now = now.ToString("O") }, tx);
        var createdId = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO Items
                (CollectionKey, Code, Name, Description, ItemKindId, Quantity, Unit, LowStockThreshold,
                 ExpiryDate, LocationId, ContainerId, ThumbnailUrl, PhotoPath, ImageId, AttributesJson,
                 SpecialAttributesJson, Notes, CreatedAt, UpdatedAt)
            VALUES
                (@CollectionKey, @Code, @Name, @Description, @ItemKindId, @Quantity, @Unit, @LowStockThreshold,
                 @ExpiryDate, @LocationId, @ContainerId, @ThumbnailUrl, @PhotoPath, @ImageId, @AttributesJson,
                 @SpecialAttributesJson, @Notes, @CreatedAt, @UpdatedAt);
            SELECT last_insert_rowid();
            """, new
        {
            lot.CollectionKey,
            lot.Code,
            lot.Name,
            lot.Description,
            lot.ItemKindId,
            lot.Quantity,
            lot.Unit,
            lot.LowStockThreshold,
            ExpiryDate = lot.ExpiryDate?.ToString("yyyy-MM-dd"),
            lot.LocationId,
            lot.ContainerId,
            lot.ThumbnailUrl,
            lot.PhotoPath,
            lot.ImageId,
            AttributesJson = JsonSerializer.Serialize(lot.Attributes, Json),
            SpecialAttributesJson = JsonSerializer.Serialize(lot.SpecialAttributes, Json),
            lot.Notes,
            CreatedAt = now.ToString("O"),
            UpdatedAt = now.ToString("O")
        }, tx);

        var hasNewPrimaryImage = observed.ImageId is { } observedImageId
                                 && observedImageId != source.ImageId;
        await conn.ExecuteAsync("""
            INSERT INTO ItemImages (ItemId, ImageId, Role, IsPrimary, SortOrder, CreatedAt)
            SELECT @createdId, ImageId, Role,
                   CASE WHEN @demote = 1 THEN 0 ELSE IsPrimary END,
                   SortOrder, @now
            FROM ItemImages WHERE ItemId = @matchedItemId;
            """, new
        {
            createdId,
            matchedItemId,
            demote = hasNewPrimaryImage ? 1 : 0,
            now = now.ToString("O")
        }, tx);
        if (hasNewPrimaryImage)
        {
            await conn.ExecuteAsync("""
                INSERT INTO ItemImages (ItemId, ImageId, Role, IsPrimary, SortOrder, CreatedAt)
                VALUES (@createdId, @imageId, @role, 1,
                        COALESCE((SELECT MAX(SortOrder) + 1 FROM ItemImages WHERE ItemId = @createdId), 0), @now)
                ON CONFLICT(ItemId, ImageId) DO UPDATE SET IsPrimary = 1;
                """, new
            {
                createdId,
                imageId = observed.ImageId,
                role = (int)ItemImageRole.Other,
                now = now.ToString("O")
            }, tx);
        }
        tx.Commit();
        return await GetAsync(createdId, ct)
               ?? throw new InvalidOperationException("The stock lot disappeared after creation.");
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

    /// <summary>
    /// Remote lookup covers are not meant to stay remote: when an item is saved carrying a
    /// remote <see cref="Item.ThumbnailUrl"/> and no stored image, download it through the
    /// sanitizing ingest (verified, re-encoded, deduped) and reference the local copy
    /// instead. Failures (offline, dead URL, not-an-image) keep the remote URL so the UI
    /// still has its fallback — ingestion retries on the next save.
    /// </summary>
    private async Task TryIngestRemoteThumbnailAsync(Item item, CancellationToken ct)
    {
        if (images is null || item.ImageId is not null) return;
        var url = item.ThumbnailUrl;
        if (string.IsNullOrWhiteSpace(url)
            || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var stored = await images.SaveFromUrlAsync(url!, ct);
            item.ImageId = stored.Id;
            item.ThumbnailUrl = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberate: keep the remote URL as the display fallback.
            _ = ex;
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
        using var tx = conn.BeginTransaction();
        var collectionKey = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT CollectionKey FROM Items WHERE Id = @id", new { id }, tx);
        await conn.ExecuteAsync("DELETE FROM Items WHERE Id = @id", new { id }, tx);
        if (!string.IsNullOrWhiteSpace(collectionKey))
        {
            // A one-entry product no longer needs a collection marker.
            await conn.ExecuteAsync("""
                UPDATE Items SET CollectionKey = NULL
                WHERE CollectionKey = @collectionKey
                  AND (SELECT COUNT(*) FROM Items WHERE CollectionKey = @collectionKey) = 1;
                """, new { collectionKey }, tx);
        }
        tx.Commit();
    }

    private static async Task SynchronizePrimaryImageAsync(
        System.Data.IDbConnection conn,
        int itemId,
        int? imageId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        _ = ct;
        if (imageId is null)
        {
            await conn.ExecuteAsync(
                "DELETE FROM ItemImages WHERE ItemId = @itemId AND IsPrimary = 1",
                new { itemId });
            return;
        }

        await conn.ExecuteAsync("""
            UPDATE ItemImages SET IsPrimary = 0
            WHERE ItemId = @itemId AND IsPrimary = 1 AND ImageId <> @imageId;
            """, new
        {
            itemId,
            imageId,
        });
        await conn.ExecuteAsync("""
            INSERT INTO ItemImages (ItemId, ImageId, Role, IsPrimary, SortOrder, CreatedAt)
            VALUES (@itemId, @imageId, @role, 1, 0, @createdAt)
            ON CONFLICT(ItemId, ImageId) DO UPDATE SET IsPrimary = 1;
            """, new
        {
            itemId,
            imageId,
            role = (int)ItemImageRole.Other,
            createdAt = now.ToString("O")
        });
    }

    private static Task DemotePrimaryAsync(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        int itemId,
        int exceptImageId) => conn.ExecuteAsync("""
            UPDATE ItemImages SET IsPrimary = 0
            WHERE ItemId = @itemId AND IsPrimary = 1 AND ImageId <> @exceptImageId;
            """, new { itemId, exceptImageId }, tx);

    private static async Task PromoteFirstRemainingAsync(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        int itemId)
    {
        var replacement = await conn.QuerySingleOrDefaultAsync<int?>("""
            SELECT ImageId FROM ItemImages
            WHERE ItemId = @itemId AND Role <> @receipt
            ORDER BY SortOrder, CreatedAt LIMIT 1;
            """, new { itemId, receipt = (int)ItemImageRole.Receipt }, tx);
        if (replacement is { } imageId)
        {
            await conn.ExecuteAsync(
                "UPDATE ItemImages SET IsPrimary = 1 WHERE ItemId = @itemId AND ImageId = @imageId",
                new { itemId, imageId }, tx);
        }
        await conn.ExecuteAsync(
            "UPDATE Items SET ImageId = @imageId, UpdatedAt = @now WHERE Id = @itemId",
            new { itemId, imageId = replacement, now = DateTimeOffset.UtcNow.ToString("O") }, tx);
    }

    private static ItemImage MapItemImage(ItemImageRow row) => new()
    {
        ItemId = row.ItemId,
        ImageId = row.ImageId,
        Role = (ItemImageRole)row.Role,
        IsPrimary = row.IsPrimary,
        SortOrder = row.SortOrder,
        CreatedAt = DateTimeOffset.Parse(row.LinkedAt),
        Image = new Image
        {
            Id = row.ImageId,
            StorageKey = row.StorageKey,
            ContentType = row.ContentType,
            OriginalName = row.OriginalName,
            Width = row.Width,
            Height = row.Height,
            ByteSize = row.ByteSize,
            Sha256 = row.Sha256,
            SourceUrl = row.SourceUrl,
            CreatedAt = DateTimeOffset.Parse(row.ImageCreatedAt)
        }
    };

    public async Task<List<ItemKind>> GetKindsAsync(CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var kinds = await conn.QueryAsync<ItemKind>(
            "SELECT Id, Name, Icon, SuggestedAttributes, IsSystem FROM ItemKinds ORDER BY Name");
        return kinds.ToList();
    }

    public Task<DashboardSummary> GetDashboardAsync(CancellationToken ct = default) =>
        GetDashboardAsync(DateOnly.FromDateTime(DateTime.Today), ct);

    public async Task<DashboardSummary> GetDashboardAsync(DateOnly today, CancellationToken ct = default)
    {
        var soon = today.AddDays(7).ToString("yyyy-MM-dd");
        using var conn = await db.OpenAsync(ct);

        var total = await conn.QuerySingleAsync<int>("""
            SELECT COUNT(DISTINCT COALESCE(CollectionKey, 'item:' || Id)) FROM Items;
            """);
        var totalQty = await conn.ExecuteScalarAsync<decimal?>("SELECT SUM(Quantity) FROM Items") ?? 0m;

        var lowGroups = (await conn.QueryAsync<LowStockGroup>("""
            SELECT MIN(Id) AS RepresentativeId, SUM(Quantity) AS TotalQuantity
            FROM Items
            GROUP BY COALESCE(CollectionKey, 'item:' || Id)
            HAVING MAX(LowStockThreshold) > 0 AND SUM(Quantity) <= MAX(LowStockThreshold)
            ORDER BY TotalQuantity LIMIT 20;
            """)).ToList();
        var lowStock = lowGroups.Count == 0
            ? []
            : (await conn.QueryAsync<ItemRow>(SelectItem + " " + """
                 WHERE i.Id IN (
                     SELECT MIN(Id) FROM Items
                     GROUP BY COALESCE(CollectionKey, 'item:' || Id)
                     HAVING MAX(LowStockThreshold) > 0
                        AND SUM(Quantity) <= MAX(LowStockThreshold)
                 )
                 """))
                .Select(Map)
                .ToList();
        foreach (var item in lowStock)
            item.Quantity = lowGroups.Single(group => group.RepresentativeId == item.Id).TotalQuantity;

        var expiring = (await conn.QueryAsync<ItemRow>(
            SelectItem + " WHERE i.ExpiryDate IS NOT NULL AND i.ExpiryDate <= @soon ORDER BY i.ExpiryDate LIMIT 20",
            new { soon }))
            .Select(Map).ToList();

        var checkedOut = (await conn.QueryAsync<ItemRow>(
            SelectItem + " WHERE EXISTS (SELECT 1 FROM Checkouts co WHERE co.ItemId = i.Id AND co.ReturnedAt IS NULL)"
                       + " ORDER BY i.Name LIMIT 20"))
            .Select(Map).ToList();

        var priceMetrics = (await conn.QueryAsync<PriceMetric>("""
            SELECT json_extract(SpecialAttributesJson, '$.price.currencyCode') AS CurrencyCode,
                   COUNT(*) AS PricedEntries,
                   SUM(Quantity * CAST(json_extract(SpecialAttributesJson, '$.price.decimalValue') AS NUMERIC)) AS TotalValue,
                   MIN(CAST(json_extract(SpecialAttributesJson, '$.price.decimalValue') AS NUMERIC)) AS MinimumUnitPrice,
                   MAX(CAST(json_extract(SpecialAttributesJson, '$.price.decimalValue') AS NUMERIC)) AS MaximumUnitPrice
            FROM Items
            WHERE json_extract(SpecialAttributesJson, '$.price.decimalValue') IS NOT NULL
            GROUP BY json_extract(SpecialAttributesJson, '$.price.currencyCode')
            ORDER BY CurrencyCode;
            """)).ToList();

        return new DashboardSummary(total, totalQty, lowStock, expiring, checkedOut, priceMetrics);
    }

    /// <summary>
    /// Returns positive-quantity food grouped by expiry urgency. When requested, dated
    /// non-food items are included too; missing-date reminders always remain food-only.
    /// </summary>
    public async Task<ExpiryOverview> GetExpiryOverviewAsync(
        DateOnly today, bool includeNonFood = false, CancellationToken ct = default)
    {
        const int groceryKindId = 1;
        var scope = includeNonFood
            ? "(i.ItemKindId = @groceryKindId OR i.ExpiryDate IS NOT NULL)"
            : "i.ItemKindId = @groceryKindId";
        using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ItemRow>(
            SelectItem + $" WHERE i.Quantity > 0 AND {scope}" +
            " ORDER BY CASE WHEN i.ExpiryDate IS NULL THEN 1 ELSE 0 END, i.ExpiryDate, i.Name COLLATE NOCASE",
            new { groceryKindId });
        var items = rows.Select(Map).ToList();
        var dayThree = today.AddDays(3);
        var daySeven = today.AddDays(7);

        return new ExpiryOverview(
            today,
            items.Where(item => item.ExpiryDate < today).ToList(),
            items.Where(item => item.ExpiryDate == today).ToList(),
            items.Where(item => item.ExpiryDate > today && item.ExpiryDate <= dayThree).ToList(),
            items.Where(item => item.ExpiryDate > dayThree && item.ExpiryDate <= daySeven).ToList(),
            items.Where(item => item.ExpiryDate > daySeven).ToList(),
            items.Where(item => item.ItemKindId == groceryKindId && item.ExpiryDate is null).ToList());
    }

    private static Item Map(ItemRow r) => new()
    {
        Id = r.Id,
        CollectionKey = r.CollectionKey,
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
        SpecialAttributes = DeserializeSpecialAttributes(r.SpecialAttributesJson),
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

    private static Dictionary<string, SpecialAttributeValue> DeserializeSpecialAttributes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, SpecialAttributeValue>>(json, Json) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    /// <summary>Flat row shape for the item projection above.</summary>
    private sealed class ItemRow
    {
        public int Id { get; set; }
        public string? CollectionKey { get; set; }
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
        public string? SpecialAttributesJson { get; set; }
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

    private sealed class ItemImageRow
    {
        public int ItemId { get; set; }
        public int ImageId { get; set; }
        public int Role { get; set; }
        public bool IsPrimary { get; set; }
        public int SortOrder { get; set; }
        public string LinkedAt { get; set; } = string.Empty;
        public string StorageKey { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public string? OriginalName { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public long? ByteSize { get; set; }
        public string? Sha256 { get; set; }
        public string? SourceUrl { get; set; }
        public string ImageCreatedAt { get; set; } = string.Empty;
    }

    private sealed class LowStockGroup
    {
        public int RepresentativeId { get; set; }
        public decimal TotalQuantity { get; set; }
    }

    private sealed class SplitSource
    {
        public int Id { get; set; }
        public decimal Quantity { get; set; }
        public int? LocationId { get; set; }
        public int? ContainerId { get; set; }
        public string? CollectionKey { get; set; }
        public string? ExpiryDate { get; set; }
        public string? SpecialAttributesJson { get; set; }
        public bool IsCheckedOut { get; set; }
    }
}
