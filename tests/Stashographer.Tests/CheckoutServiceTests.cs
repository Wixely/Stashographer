using Stashographer.Data.Entities;
using Stashographer.Services.Inventory;

namespace Stashographer.Tests;

public class CheckoutServiceTests
{
    [Fact]
    public async Task Checkout_then_checkin_lifecycle()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var checkouts = new CheckoutService(db.Factory);
        var item = await inventory.SaveAsync(new Item { Name = "Drill", ItemKindId = 3 });

        var record = await checkouts.CheckOutAsync(item.Id, "Sam", "lent to next door",
            DateOnly.FromDateTime(DateTime.Today.AddDays(7)), null);

        Assert.NotNull(record);
        Assert.True((await inventory.GetAsync(item.Id))!.IsCheckedOut);

        var open = await checkouts.GetOpenForItemAsync(item.Id);
        Assert.Equal("Sam", open!.CheckedOutBy);
        Assert.Equal("lent to next door", open.WhereaboutsNote);

        await checkouts.CheckInAsync(item.Id);

        Assert.False((await inventory.GetAsync(item.Id))!.IsCheckedOut);
        Assert.Null(await checkouts.GetOpenForItemAsync(item.Id));
    }

    [Fact]
    public async Task Cannot_checkout_twice_while_open()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var checkouts = new CheckoutService(db.Factory);
        var item = await inventory.SaveAsync(new Item { Name = "Ladder", ItemKindId = 3 });

        var first = await checkouts.CheckOutAsync(item.Id, "Alex", null, null, null);
        var second = await checkouts.CheckOutAsync(item.Id, "Jo", null, null, null);

        Assert.NotNull(first);
        Assert.Null(second); // already out
    }

    [Fact]
    public async Task History_retains_returned_records()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var checkouts = new CheckoutService(db.Factory);
        var item = await inventory.SaveAsync(new Item { Name = "Tent", ItemKindId = 7 });

        await checkouts.CheckOutAsync(item.Id, "Sam", null, null, null);
        await checkouts.CheckInAsync(item.Id);
        await checkouts.CheckOutAsync(item.Id, "Alex", null, null, null);

        var history = await checkouts.GetHistoryAsync(item.Id);
        Assert.Equal(2, history.Count);
    }
}
