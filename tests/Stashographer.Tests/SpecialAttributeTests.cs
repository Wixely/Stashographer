using Stashographer.Data.Entities;
using Stashographer.Services.Inventory;

namespace Stashographer.Tests;

public class SpecialAttributeTests
{
    [Fact]
    public async Task Price_roundtrips_as_typed_special_attribute()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var item = new Item { Name = "Coffee", ItemKindId = 1 };
        SpecialAttributeCatalog.SetPrice(item, 6.75m, "gbp");

        await inventory.SaveAsync(item);
        var stored = await inventory.GetAsync(item.Id);

        var price = SpecialAttributeCatalog.GetPrice(stored!);
        Assert.Equal(6.75m, price!.DecimalValue);
        Assert.Equal("GBP", price.CurrencyCode);
        Assert.DoesNotContain("Price", stored!.Attributes.Keys);
    }

    [Fact]
    public async Task Recognized_price_string_is_promoted_out_of_ordinary_attributes()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var item = new Item
        {
            Name = "Labels",
            ItemKindId = 7,
            Attributes = new() { ["Unit cost"] = "£12.50", ["Colour"] = "White" }
        };

        await inventory.SaveAsync(item);

        Assert.Equal(12.50m, SpecialAttributeCatalog.GetPrice(item)!.DecimalValue);
        Assert.DoesNotContain("Unit cost", item.Attributes.Keys);
        Assert.Equal("White", item.Attributes["Colour"]);
    }

    [Fact]
    public async Task Inventory_price_sort_is_numeric_and_leaves_unpriced_items_last()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        await SavePriced(inventory, "High", 20m, "GBP");
        await SavePriced(inventory, "Low", 3m, "GBP");
        await inventory.SaveAsync(new Item { Name = "No price", ItemKindId = 7 });

        var ascending = await inventory.QueryAsync(new ItemQuery(Sort: ItemSort.PriceLowToHigh));
        var descending = await inventory.QueryAsync(new ItemQuery(Sort: ItemSort.PriceHighToLow));

        Assert.Equal(["Low", "High", "No price"], ascending.Select(x => x.Name));
        Assert.Equal(["High", "Low", "No price"], descending.Select(x => x.Name));
    }

    [Fact]
    public async Task Dashboard_value_is_quantity_times_unit_price_and_grouped_by_currency()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        await SavePriced(inventory, "GBP one", 3m, "GBP", 2);
        await SavePriced(inventory, "GBP two", 4m, "GBP", 1);
        await SavePriced(inventory, "USD one", 5m, "USD", 3);

        var metrics = (await inventory.GetDashboardAsync()).PriceMetrics;

        Assert.Equal(2, metrics.Count);
        var gbp = Assert.Single(metrics, x => x.CurrencyCode == "GBP");
        Assert.Equal(2, gbp.PricedEntries);
        Assert.Equal(10m, gbp.TotalValue);
        Assert.Equal(3m, gbp.MinimumUnitPrice);
        Assert.Equal(4m, gbp.MaximumUnitPrice);
        Assert.Equal(15m, Assert.Single(metrics, x => x.CurrencyCode == "USD").TotalValue);
    }

    [Fact]
    public void Currency_conversion_requires_explicit_rate_and_rounds_to_minor_units()
    {
        var converted = SpecialAttributeCatalog.ConvertPrice(
            new SpecialAttributeValue { DecimalValue = 10m, CurrencyCode = "GBP" }, "eur", 1.167m);

        Assert.Equal(11.67m, converted.DecimalValue);
        Assert.Equal("EUR", converted.CurrencyCode);
    }

    private static async Task SavePriced(
        InventoryService inventory, string name, decimal amount, string currency, decimal quantity = 1)
    {
        var item = new Item { Name = name, ItemKindId = 7, Quantity = quantity };
        SpecialAttributeCatalog.SetPrice(item, amount, currency);
        await inventory.SaveAsync(item);
    }
}
