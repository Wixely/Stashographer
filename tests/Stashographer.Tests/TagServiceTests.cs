using Stashographer.Data.Entities;
using Stashographer.Services.Inventory;

namespace Stashographer.Tests;

public class TagServiceTests
{
    [Fact]
    public async Task Tags_are_unique_normalized_assignable_renameable_and_deletable()
    {
        await using var db = await TestDb.CreateAsync();
        var tags = new TagService(db.Factory);
        var inventory = new InventoryService(db.Factory, tagService: tags);
        var seasonal = await tags.SaveAsync(new Tag { Name = "  Seasonal   storage " });
        var item = await inventory.SaveAsync(new Item { Name = "Fairy lights", ItemKindId = 7 });

        await tags.SetForItemAsync(item.Id, [seasonal.Id, seasonal.Id]);
        seasonal.Name = "Christmas";
        await tags.SaveAsync(seasonal);

        var stored = await inventory.GetAsync(item.Id);
        var listed = Assert.Single(await tags.GetAllAsync());
        Assert.Equal("Christmas", Assert.Single(stored!.Tags).Name);
        Assert.Equal(1, listed.ItemCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tags.SaveAsync(new Tag { Name = "christmas" }));

        await tags.DeleteAsync(seasonal.Id);
        Assert.Empty((await inventory.GetAsync(item.Id))!.Tags);
        Assert.NotNull(await inventory.GetAsync(item.Id));
    }

    [Fact]
    public async Task Inventory_tag_filters_require_all_includes_and_reject_any_exclude()
    {
        await using var db = await TestDb.CreateAsync();
        var tags = new TagService(db.Factory);
        var inventory = new InventoryService(db.Factory, tagService: tags);
        var red = await tags.SaveAsync(new Tag { Name = "Red" });
        var sale = await tags.SaveAsync(new Tag { Name = "Sale" });
        var archived = await tags.SaveAsync(new Tag { Name = "Archived" });
        var both = await inventory.SaveAsync(new Item { Name = "Both", ItemKindId = 7 });
        var redOnly = await inventory.SaveAsync(new Item { Name = "Red only", ItemKindId = 7 });
        var oldSale = await inventory.SaveAsync(new Item { Name = "Old sale", ItemKindId = 7 });
        await tags.SetForItemAsync(both.Id, [red.Id, sale.Id]);
        await tags.SetForItemAsync(redOnly.Id, [red.Id]);
        await tags.SetForItemAsync(oldSale.Id, [sale.Id, archived.Id]);

        var included = await inventory.QueryAsync(new ItemQuery(IncludeTagIds: [red.Id, sale.Id]));
        var notArchived = await inventory.QueryAsync(new ItemQuery(ExcludeTagIds: [archived.Id]));
        var searched = await inventory.QueryAsync(new ItemQuery(Search: "sale"));

        Assert.Equal("Both", Assert.Single(included).Name);
        Assert.Equal(["Both", "Red only"], notArchived.Select(item => item.Name).Order());
        Assert.Equal(["Both", "Old sale"], searched.Select(item => item.Name).Order());
        Assert.Equal(["Red", "Sale"], included[0].Tags.Select(tag => tag.Name).Order());
    }

    [Fact]
    public async Task Tags_follow_place_splits_and_new_expiry_lots()
    {
        await using var db = await TestDb.CreateAsync();
        var tags = new TagService(db.Factory);
        var inventory = new InventoryService(db.Factory, tagService: tags);
        var camping = await tags.SaveAsync(new Tag { Name = "Camping" });
        var splitSource = await inventory.SaveAsync(new Item
        {
            Name = "Water bottle", ItemKindId = 7, Quantity = 2, LocationId = 1
        });
        await tags.SetForItemAsync(splitSource.Id, [camping.Id]);

        var split = await inventory.SplitAsync(splitSource.Id, 1, 3, null);
        Assert.Equal("Camping", Assert.Single(split.Source.Tags).Name);
        Assert.Equal("Camping", Assert.Single(split.Created.Tags).Name);

        var chilled = await tags.SaveAsync(new Tag { Name = "Chilled" });
        var milk = new Item
        {
            Name = "Milk", ItemKindId = 1, Quantity = 1, ExpiryDate = new DateOnly(2026, 9, 1)
        };
        SpecialAttributeCatalog.SetExpiry(milk, milk.ExpiryDate, ExpiryDateKind.UseBy);
        await inventory.SaveAsync(milk);
        await tags.SetForItemAsync(milk.Id, [chilled.Id]);
        var observed = new Item
        {
            Name = milk.Name, ItemKindId = 1, Quantity = 1, ExpiryDate = new DateOnly(2026, 9, 8)
        };
        SpecialAttributeCatalog.SetExpiry(observed, observed.ExpiryDate, ExpiryDateKind.UseBy);

        var lot = await inventory.CreateStockLotAsync(milk.Id, observed);
        Assert.Equal("Chilled", Assert.Single(lot.Tags).Name);
    }
}
