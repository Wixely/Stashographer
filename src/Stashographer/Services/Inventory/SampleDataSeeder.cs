using Dapper;
using Stashographer.Data;
using Stashographer.Data.Entities;

namespace Stashographer.Services.Inventory;

/// <summary>Options controlling development/test sample data. Bound from the <c>SampleData</c> section.</summary>
public class SampleDataOptions
{
    public const string SectionName = "SampleData";

    /// <summary>When true, sample data is populated at startup (if not already present).</summary>
    public bool Enabled { get; set; }

    /// <summary>When true, existing items/containers/checkouts are wiped and re-seeded every startup.</summary>
    public bool Reset { get; set; }
}

/// <summary>
/// Populates a realistic set of demo data (containers, items across every kind, low-stock and
/// expiring items, and an active checkout) so the UI can be run and debugged with content.
/// Idempotent: skips if items already exist, unless <see cref="SampleDataOptions.Reset"/> is set.
/// </summary>
public class SampleDataSeeder(
    IDbConnectionFactory db,
    InventoryService inventory,
    ContainerService containers,
    CheckoutService checkouts,
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
                logger.LogInformation("Sample data: resetting items, containers and checkouts");
                await conn.ExecuteAsync("DELETE FROM Checkouts; DELETE FROM Items; DELETE FROM Containers;");
            }
        }

        // Containers in the seeded locations (1=Kitchen, 3=Garage, 4=Loft, 5=Study).
        var pantry = await containers.SaveContainerAsync(new Container { Name = "Pantry shelf", ContainerType = ContainerType.Shelf, LocationId = 1 }, ct);
        var toolbox = await containers.SaveContainerAsync(new Container { Name = "Red toolbox", ContainerType = ContainerType.Box, LocationId = 3 }, ct);
        var xmas = await containers.SaveContainerAsync(new Container { Name = "Christmas box", ContainerType = ContainerType.Box, LocationId = 4 }, ct);

        var today = DateOnly.FromDateTime(DateTime.Today);

        // Groceries (kind 1) — one low on stock, one expiring soon.
        await inventory.SaveAsync(new Item
        {
            Name = "Baked Beans", Code = "5000157024671", ItemKindId = 1, Quantity = 1, Unit = "tin",
            LowStockThreshold = 2, ContainerId = pantry.Id,
            Attributes = new() { ["Brand"] = "Heinz", ["Category"] = "Tinned goods" }
        }, ct);
        await inventory.SaveAsync(new Item
        {
            Name = "Semi-skimmed Milk", Code = "5000000000019", ItemKindId = 1, Quantity = 2, Unit = "L",
            ExpiryDate = today.AddDays(3), LocationId = 1,
            Attributes = new() { ["Brand"] = "Local Dairy" }
        }, ct);
        await inventory.SaveAsync(new Item
        {
            Name = "Coca-Cola (can)", Code = "5449000000996", ItemKindId = 1, Quantity = 6, Unit = "can",
            ContainerId = pantry.Id, Attributes = new() { ["Brand"] = "Coca-Cola" }
        }, ct);

        // Books (kind 2).
        await inventory.SaveAsync(new Item
        {
            Name = "Introduction to Algorithms", Code = "9780262033848", ItemKindId = 2,
            Quantity = 1, LocationId = 5,
            ThumbnailUrl = "https://covers.openlibrary.org/b/isbn/9780262033848-M.jpg",
            Attributes = new() { ["Author"] = "Cormen, Leiserson, Rivest, Stein", ["Publisher"] = "The MIT Press", ["Pages"] = "1292" }
        }, ct);
        await inventory.SaveAsync(new Item
        {
            Name = "The Pragmatic Programmer", Code = "9780201616224", ItemKindId = 2, Quantity = 1, LocationId = 5,
            Attributes = new() { ["Author"] = "Hunt, Thomas" }
        }, ct);

        // Tools (kind 3) — one gets checked out below.
        var drill = await inventory.SaveAsync(new Item
        {
            Name = "Cordless Drill", ItemKindId = 3, Quantity = 1, ContainerId = toolbox.Id,
            Attributes = new() { ["Brand"] = "DeWalt", ["Model"] = "DCD778" }
        }, ct);
        await inventory.SaveAsync(new Item
        {
            Name = "Screwdriver set", ItemKindId = 3, Quantity = 1, ContainerId = toolbox.Id
        }, ct);

        // Electronics (kind 4), Media (5), Clothing (6), Other (7).
        await inventory.SaveAsync(new Item
        {
            Name = "HDMI Cable", ItemKindId = 4, Quantity = 3, Unit = "each", ContainerId = xmas.Id,
            Attributes = new() { ["Length"] = "2m" }
        }, ct);
        await inventory.SaveAsync(new Item { Name = "Fairy lights", ItemKindId = 7, Quantity = 4, ContainerId = xmas.Id }, ct);
        await inventory.SaveAsync(new Item
        {
            Name = "Winter coat", ItemKindId = 6, Quantity = 1, LocationId = 4,
            Attributes = new() { ["Size"] = "L", ["Colour"] = "Navy" }
        }, ct);

        // An active checkout so the dashboard / checkouts page have content.
        await checkouts.CheckOutAsync(drill.Id, "Sam next door", "borrowed for the weekend",
            today.AddDays(5), null, ct);

        logger.LogInformation("Sample data seeded");
    }
}
