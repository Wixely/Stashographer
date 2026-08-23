using Stashographer.Data.Entities;
using Stashographer.Services.Inventory;

namespace Stashographer.Tests;

public class SplitItemTests
{
    [Fact]
    public async Task Split_creates_linked_entry_with_independent_quantity_and_place()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var containers = new ContainerService(db.Factory);
        var box = await containers.SaveContainerAsync(new Container { Name = "Overflow box", LocationId = 3 });
        var item = await inventory.SaveAsync(new Item
        {
            Name = "AA batteries",
            Code = "12345678",
            ItemKindId = 4,
            Quantity = 6,
            Unit = "each",
            LocationId = 1,
            Attributes = new() { ["Brand"] = "Example" }
        });

        var split = await inventory.SplitAsync(item.Id, 2, null, box.Id);

        Assert.Equal(4, split.Source.Quantity);
        Assert.Equal(1, split.Source.LocationId);
        Assert.Equal(2, split.Created.Quantity);
        Assert.Equal(box.Id, split.Created.ContainerId);
        Assert.NotNull(split.Source.CollectionKey);
        Assert.Equal(split.Source.CollectionKey, split.Created.CollectionKey);
        Assert.Equal(item.Code, split.Created.Code);
        Assert.Equal("Example", split.Created.Attributes["Brand"]);

        var members = await inventory.GetCollectionMembersAsync(item.Id);
        Assert.Equal(2, members.Count);
        Assert.Equal(6, members.Sum(x => x.Quantity));
        Assert.Single(await inventory.QueryAsync(new ItemQuery(LocationId: 1)));
        Assert.Single(await inventory.QueryAsync(new ItemQuery(ContainerId: box.Id)));
    }

    [Fact]
    public async Task Repeated_splits_join_the_same_collection()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var item = await inventory.SaveAsync(new Item
        {
            Name = "Labels", ItemKindId = 7, Quantity = 5, LocationId = 1
        });

        await inventory.SplitAsync(item.Id, 1, 3, null);
        await inventory.SplitAsync(item.Id, 1, 5, null);

        var members = await inventory.GetCollectionMembersAsync(item.Id);
        Assert.Equal(3, members.Count);
        Assert.Single(members.Select(x => x.CollectionKey).Distinct());
        Assert.Equal(5, members.Sum(x => x.Quantity));
        Assert.Equal([1, 3, 5], members.Select(x => x.LocationId!.Value).Order().ToArray());
    }

    [Fact]
    public async Task Invalid_split_leaves_source_unchanged()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var item = await inventory.SaveAsync(new Item
        {
            Name = "Tape", ItemKindId = 7, Quantity = 2, LocationId = 1
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            inventory.SplitAsync(item.Id, 2, 3, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            inventory.SplitAsync(item.Id, 1, 1, null));

        var unchanged = await inventory.GetAsync(item.Id);
        Assert.Equal(2, unchanged!.Quantity);
        Assert.Null(unchanged.CollectionKey);
        Assert.Single(await inventory.GetCollectionMembersAsync(item.Id));
    }

    [Fact]
    public async Task Deleting_one_of_two_parts_collapses_the_remaining_collection_marker()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var item = await inventory.SaveAsync(new Item
        {
            Name = "Cable ties", ItemKindId = 7, Quantity = 2, LocationId = 1
        });
        var split = await inventory.SplitAsync(item.Id, 1, 3, null);

        await inventory.DeleteAsync(split.Created.Id);

        var remaining = await inventory.GetAsync(item.Id);
        Assert.NotNull(remaining);
        Assert.Null(remaining!.CollectionKey);
        Assert.Single(await inventory.GetCollectionMembersAsync(item.Id));
    }
}
