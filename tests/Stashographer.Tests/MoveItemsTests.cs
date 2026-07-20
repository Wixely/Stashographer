using Stashographer.Data.Entities;
using Stashographer.Services.Inventory;

namespace Stashographer.Tests;

public class MoveItemsTests
{
    // Seeded rooms: 1=Kitchen, 3=Garage. Seeded containers: none — create as needed.

    [Fact]
    public async Task Move_into_container_clears_stored_location()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var containers = new ContainerService(db.Factory);
        var box = await containers.SaveContainerAsync(new Container { Name = "Box", LocationId = 3 });
        var item = await inventory.SaveAsync(new Item { Name = "Drill", ItemKindId = 3, LocationId = 1 });

        var previous = await inventory.MoveItemsAsync(new[] { item.Id }, null, box.Id);

        var moved = await inventory.GetAsync(item.Id);
        Assert.Equal(box.Id, moved!.ContainerId);
        Assert.Null(moved.LocationId); // convention: container implies room via the container
        Assert.Equal("Garage", moved.Container?.Location?.Name);

        Assert.Single(previous);
        Assert.Equal(new ItemPlacement(item.Id, 1, null), previous[0]);
    }

    [Fact]
    public async Task Move_out_of_container_into_room_clears_container()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var containers = new ContainerService(db.Factory);
        var box = await containers.SaveContainerAsync(new Container { Name = "Box", LocationId = 3 });
        var item = await inventory.SaveAsync(new Item { Name = "Rope", ItemKindId = 7, ContainerId = box.Id });

        await inventory.MoveItemsAsync(new[] { item.Id }, 1, null);

        var moved = await inventory.GetAsync(item.Id);
        Assert.Null(moved!.ContainerId);
        Assert.Equal(1, moved.LocationId); // loose in Kitchen
    }

    [Fact]
    public async Task Restore_puts_multiple_items_back_exactly()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var containers = new ContainerService(db.Factory);
        var box = await containers.SaveContainerAsync(new Container { Name = "Box", LocationId = 3 });
        var loose = await inventory.SaveAsync(new Item { Name = "Loose thing", ItemKindId = 7, LocationId = 1 });
        var boxed = await inventory.SaveAsync(new Item { Name = "Boxed thing", ItemKindId = 7, ContainerId = box.Id });

        // Move both loose into Garage, then undo.
        var previous = await inventory.MoveItemsAsync(new[] { loose.Id, boxed.Id }, 3, null);
        await inventory.RestorePlacementsAsync(previous);

        var restoredLoose = await inventory.GetAsync(loose.Id);
        var restoredBoxed = await inventory.GetAsync(boxed.Id);
        Assert.Equal(1, restoredLoose!.LocationId);
        Assert.Null(restoredLoose.ContainerId);
        Assert.Equal(box.Id, restoredBoxed!.ContainerId);
        Assert.Null(restoredBoxed.LocationId);
    }

    [Fact]
    public async Task LooseOnly_query_excludes_container_items()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var containers = new ContainerService(db.Factory);
        var box = await containers.SaveContainerAsync(new Container { Name = "Box", LocationId = 3 });
        await inventory.SaveAsync(new Item { Name = "Loose in garage", ItemKindId = 7, LocationId = 3 });
        await inventory.SaveAsync(new Item { Name = "In the box", ItemKindId = 7, ContainerId = box.Id });

        var loose = await inventory.QueryAsync(new ItemQuery(LocationId: 3, LooseOnly: true));
        var all = await inventory.QueryAsync(new ItemQuery(LocationId: 3));

        Assert.Single(loose);
        Assert.Equal("Loose in garage", loose[0].Name);
        Assert.Equal(2, all.Count); // without the flag the box's item still counts as in-room
    }

    [Fact]
    public async Task Move_container_carries_items_to_the_new_room()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var containers = new ContainerService(db.Factory);
        var box = await containers.SaveContainerAsync(new Container { Name = "Box", LocationId = 3 });
        var item = await inventory.SaveAsync(new Item { Name = "Tinsel", ItemKindId = 7, ContainerId = box.Id });

        await containers.MoveContainerAsync(box.Id, 1); // Garage → Kitchen

        var moved = await inventory.GetAsync(item.Id);
        Assert.Equal("Kitchen", moved!.Container?.Location?.Name);
        Assert.Contains(moved.Id,
            (await inventory.QueryAsync(new ItemQuery(LocationId: 1))).Select(i => i.Id));
    }

    [Fact]
    public async Task Place_counts_report_loose_and_container_totals()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var containers = new ContainerService(db.Factory);
        var box = await containers.SaveContainerAsync(new Container { Name = "Box", LocationId = 3 });
        await inventory.SaveAsync(new Item { Name = "A", ItemKindId = 7, LocationId = 3 });
        await inventory.SaveAsync(new Item { Name = "B", ItemKindId = 7, LocationId = 3 });
        await inventory.SaveAsync(new Item { Name = "C", ItemKindId = 7, ContainerId = box.Id });

        var counts = await containers.GetPlaceCountsAsync();

        Assert.Equal(2, counts.LooseByLocation.GetValueOrDefault(3));
        Assert.Equal(1, counts.ItemsByContainer.GetValueOrDefault(box.Id));
    }
}
