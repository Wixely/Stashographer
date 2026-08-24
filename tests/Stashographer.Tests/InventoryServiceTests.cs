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
    public async Task Fractional_quantity_after_arithmetic_roundtrips_across_inventory_views()
    {
        await using var db = await TestDb.CreateAsync();
        var svc = new InventoryService(db.Factory);
        var today = new DateOnly(2026, 8, 24);
        var item = await svc.SaveAsync(new Item
        {
            Name = "Chopped tomatoes",
            ItemKindId = 1,
            Quantity = 2,
            Unit = "tin",
            LowStockThreshold = 2,
            ExpiryDate = today.AddDays(2)
        });

        await svc.AdjustQuantityAsync(item.Id, -0.5m);

        Assert.Equal(1.5m, (await svc.GetAsync(item.Id))!.Quantity);
        Assert.Equal(1.5m, Assert.Single(await svc.QueryAsync(new ItemQuery())).Quantity);
        var dashboard = await svc.GetDashboardAsync(today);
        Assert.Equal(1.5m, dashboard.TotalQuantity);
        Assert.Equal(1.5m, Assert.Single(dashboard.LowStock).Quantity);
        Assert.Equal(1.5m, Assert.Single(
            (await svc.GetExpiryOverviewAsync(today)).NextThreeDays).Quantity);
    }

    [Fact]
    public async Task Query_filters_by_kind_and_search()
    {
        await using var db = await TestDb.CreateAsync();
        var svc = new InventoryService(db.Factory);
        await svc.SaveAsync(new Item { Name = "Baked Beans", ItemKindId = 1 });   // Grocery
        await svc.SaveAsync(new Item { Name = "Hammer", ItemKindId = 3 });         // Tool
        await svc.SaveAsync(new Item { Name = "Novel", ItemKindId = 2 });          // Book

        // Single positive kind filter.
        var groceries = await svc.QueryAsync(new ItemQuery(IncludeKindIds: new[] { 1 }));
        Assert.Single(groceries);
        Assert.Equal("Baked Beans", groceries[0].Name);

        var search = await svc.QueryAsync(new ItemQuery(Search: "hamm"));
        Assert.Single(search);
        Assert.Equal("Hammer", search[0].Name);
    }

    [Fact]
    public async Task Query_supports_multiple_include_kinds_or_semantics()
    {
        await using var db = await TestDb.CreateAsync();
        var svc = new InventoryService(db.Factory);
        await svc.SaveAsync(new Item { Name = "Baked Beans", ItemKindId = 1 });   // Grocery
        await svc.SaveAsync(new Item { Name = "Hammer", ItemKindId = 3 });         // Tool
        await svc.SaveAsync(new Item { Name = "Novel", ItemKindId = 2 });          // Book

        // "Books + Tools" → both, not the grocery.
        var result = await svc.QueryAsync(new ItemQuery(IncludeKindIds: new[] { 2, 3 }));
        var names = result.Select(i => i.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "Hammer", "Novel" }, names);
    }

    [Fact]
    public async Task Query_supports_exclude_kinds()
    {
        await using var db = await TestDb.CreateAsync();
        var svc = new InventoryService(db.Factory);
        await svc.SaveAsync(new Item { Name = "Baked Beans", ItemKindId = 1 });   // Grocery
        await svc.SaveAsync(new Item { Name = "Hammer", ItemKindId = 3 });         // Tool
        await svc.SaveAsync(new Item { Name = "Novel", ItemKindId = 2 });          // Book

        // "NOT Books" → everything except the book.
        var result = await svc.QueryAsync(new ItemQuery(ExcludeKindIds: new[] { 2 }));
        Assert.DoesNotContain(result, i => i.Name == "Novel");
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task Dashboard_reports_low_stock_and_expiring()
    {
        await using var db = await TestDb.CreateAsync();
        var svc = new InventoryService(db.Factory);
        await svc.SaveAsync(new Item { Name = "Milk", ItemKindId = 1, Quantity = 1, LowStockThreshold = 2 });
        await svc.SaveAsync(new Item
        {
            Name = "Yoghurt",
            ItemKindId = 1,
            Quantity = 5,
            ExpiryDate = DateOnly.FromDateTime(DateTime.Today.AddDays(2))
        });

        var summary = await svc.GetDashboardAsync();
        Assert.Equal(2, summary.TotalItems);
        Assert.Contains(summary.LowStock, i => i.Name == "Milk");
        Assert.Contains(summary.ExpiringSoon, i => i.Name == "Yoghurt");
    }

    [Fact]
    public async Task Dashboard_expiry_window_can_use_configured_regional_today()
    {
        await using var db = await TestDb.CreateAsync();
        var svc = new InventoryService(db.Factory);
        var today = new DateOnly(2030, 1, 10);
        await svc.SaveAsync(new Item { Name = "Boundary", ItemKindId = 1, ExpiryDate = today.AddDays(7) });
        await svc.SaveAsync(new Item { Name = "Outside", ItemKindId = 1, ExpiryDate = today.AddDays(8) });

        var summary = await svc.GetDashboardAsync(today);

        Assert.Contains(summary.ExpiringSoon, item => item.Name == "Boundary");
        Assert.DoesNotContain(summary.ExpiringSoon, item => item.Name == "Outside");
    }

    [Fact]
    public async Task Expiry_overview_groups_active_food_into_non_overlapping_windows()
    {
        await using var db = await TestDb.CreateAsync();
        var svc = new InventoryService(db.Factory);
        var today = new DateOnly(2026, 8, 24);
        await svc.SaveAsync(new Item { Name = "Expired", ItemKindId = 1, ExpiryDate = today.AddDays(-1) });
        await svc.SaveAsync(new Item { Name = "Today", ItemKindId = 1, ExpiryDate = today });
        await svc.SaveAsync(new Item { Name = "Soon", ItemKindId = 1, ExpiryDate = today.AddDays(3) });
        await svc.SaveAsync(new Item { Name = "This week", ItemKindId = 1, ExpiryDate = today.AddDays(4) });
        await svc.SaveAsync(new Item { Name = "Later", ItemKindId = 1, ExpiryDate = today.AddDays(8) });
        await svc.SaveAsync(new Item { Name = "Missing", ItemKindId = 1 });
        await svc.SaveAsync(new Item
        {
            Name = "Empty expired",
            ItemKindId = 1,
            Quantity = 0,
            ExpiryDate = today.AddDays(-2)
        });
        await svc.SaveAsync(new Item { Name = "Dated tool", ItemKindId = 3, ExpiryDate = today.AddDays(2) });

        var overview = await svc.GetExpiryOverviewAsync(today);

        Assert.Equal("Expired", Assert.Single(overview.Expired).Name);
        Assert.Equal("Today", Assert.Single(overview.DueToday).Name);
        Assert.Equal("Soon", Assert.Single(overview.NextThreeDays).Name);
        Assert.Equal("This week", Assert.Single(overview.DaysFourToSeven).Name);
        Assert.Equal("Later", Assert.Single(overview.Later).Name);
        Assert.Equal("Missing", Assert.Single(overview.MissingFoodDate).Name);
        Assert.Equal(5, overview.DatedCount);
        Assert.Equal(3, overview.DueWithinSevenDaysCount);
        Assert.DoesNotContain(overview.Expired, item => item.Name == "Empty expired");
    }

    [Fact]
    public async Task Expiry_overview_can_include_dated_non_food_without_missing_non_food_noise()
    {
        await using var db = await TestDb.CreateAsync();
        var svc = new InventoryService(db.Factory);
        var today = new DateOnly(2026, 8, 24);
        await svc.SaveAsync(new Item { Name = "Dated battery", ItemKindId = 4, ExpiryDate = today.AddDays(2) });
        await svc.SaveAsync(new Item { Name = "Undated battery", ItemKindId = 4 });
        await svc.SaveAsync(new Item { Name = "Undated food", ItemKindId = 1 });

        var overview = await svc.GetExpiryOverviewAsync(today, includeNonFood: true);

        Assert.Equal("Dated battery", Assert.Single(overview.NextThreeDays).Name);
        Assert.Equal("Undated food", Assert.Single(overview.MissingFoodDate).Name);
        Assert.DoesNotContain(overview.MissingFoodDate, item => item.Name == "Undated battery");
    }

    [Fact]
    public async Task Kind_icons_are_short_iconcatalog_keys_after_migrations()
    {
        await using var db = await TestDb.CreateAsync();
        var svc = new InventoryService(db.Factory);

        var kinds = await svc.GetKindsAsync();

        Assert.All(kinds, k =>
        {
            Assert.False(string.IsNullOrWhiteSpace(k.Icon));
            Assert.DoesNotContain(".", k.Icon); // 0006 strips 'Icons.Material.Filled.'
        });
        Assert.Equal("MenuBook", kinds.Single(k => k.Name == "Book").Icon);
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
