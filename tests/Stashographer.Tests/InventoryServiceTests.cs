using Stashographer.Data.Entities;
using Stashographer.Services.Inventory;

namespace Stashographer.Tests;

public class InventoryServiceTests
{
    [Fact]
    public async Task Save_then_get_roundtrips_including_json_attributes()
    {
        await using var db = await TestDb.CreateAsync();
        var svc = new InventoryService(db.Factory);

        var saved = await svc.SaveAsync(new Item
        {
            Name = "SICP",
            Code = "9780262033848",
            ItemKindId = 2, // Book
            Quantity = 1,
            ExpiryDate = null,
            Attributes = new() { ["Author"] = "Abelson & Sussman", ["Pages"] = "657" }
        });

        Assert.True(saved.Id > 0);

        var fetched = await svc.GetAsync(saved.Id);
        Assert.NotNull(fetched);
        Assert.Equal("SICP", fetched!.Name);
        Assert.Equal("Book", fetched.Kind?.Name);
        Assert.Equal("Abelson & Sussman", fetched.Attributes["Author"]);
        Assert.Equal("657", fetched.Attributes["Pages"]);
        Assert.False(fetched.IsCheckedOut);
    }

    [Fact]
    public async Task Adjust_quantity_never_goes_below_zero()
    {
        await using var db = await TestDb.CreateAsync();
        var svc = new InventoryService(db.Factory);
        var item = await svc.SaveAsync(new Item { Name = "Beans", ItemKindId = 1, Quantity = 1 });

        await svc.AdjustQuantityAsync(item.Id, -5);

        var fetched = await svc.GetAsync(item.Id);
        Assert.Equal(0m, fetched!.Quantity);
    }

    [Fact]
    public async Task Query_filters_by_kind_and_search()
    {
        await using var db = await TestDb.CreateAsync();
        var svc = new InventoryService(db.Factory);
        await svc.SaveAsync(new Item { Name = "Baked Beans", ItemKindId = 1 });
        await svc.SaveAsync(new Item { Name = "Hammer", ItemKindId = 3 });

        var groceries = await svc.QueryAsync(new ItemQuery(KindId: 1));
        Assert.Single(groceries);
        Assert.Equal("Baked Beans", groceries[0].Name);

        var search = await svc.QueryAsync(new ItemQuery(Search: "hamm"));
        Assert.Single(search);
        Assert.Equal("Hammer", search[0].Name);
    }

    [Fact]
    public async Task Dashboard_reports_low_stock_and_expiring()
    {
        await using var db = await TestDb.CreateAsync();
        var svc = new InventoryService(db.Factory);
        await svc.SaveAsync(new Item { Name = "Milk", ItemKindId = 1, Quantity = 1, LowStockThreshold = 2 });
        await svc.SaveAsync(new Item
        {
            Name = "Yoghurt", ItemKindId = 1, Quantity = 5,
            ExpiryDate = DateOnly.FromDateTime(DateTime.Today.AddDays(2))
        });

        var summary = await svc.GetDashboardAsync();
        Assert.Equal(2, summary.TotalItems);
        Assert.Contains(summary.LowStock, i => i.Name == "Milk");
        Assert.Contains(summary.ExpiringSoon, i => i.Name == "Yoghurt");
    }

    [Fact]
    public async Task Delete_removes_item()
    {
        await using var db = await TestDb.CreateAsync();
        var svc = new InventoryService(db.Factory);
        var item = await svc.SaveAsync(new Item { Name = "Temp", ItemKindId = 7 });

        await svc.DeleteAsync(item.Id);

        Assert.Null(await svc.GetAsync(item.Id));
    }
}
