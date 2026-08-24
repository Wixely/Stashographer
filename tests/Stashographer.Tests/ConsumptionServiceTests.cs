using Stashographer.Data.Entities;
using Stashographer.Services.Inventory;

namespace Stashographer.Tests;

public class ConsumptionServiceTests
{
    [Fact]
    public async Task Manual_use_records_exact_lot_and_undo_restores_it_once()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var consumption = new ConsumptionService(db.Factory);
        var expiry = new DateOnly(2026, 9, 12);
        var item = await inventory.SaveAsync(new Item
        {
            Name = "Tomato soup",
            ItemKindId = 1,
            Quantity = 3,
            Unit = "cans",
            ExpiryDate = expiry
        });

        var applied = await consumption.UseItemAsync(item.Id, 1, "Lunch");

        Assert.Equal(ConsumptionKind.Manual, applied.Kind);
        Assert.Null(applied.MealPlanEntryId);
        Assert.Equal(2, (await inventory.GetAsync(item.Id))!.Quantity);
        var active = Assert.Single(await consumption.GetForItemAsync(item.Id, includeUndone: false));
        Assert.Equal("Lunch", active.Description);
        Assert.True(active.CanUndo);
        var line = Assert.Single(active.Lines);
        Assert.Equal(item.Id, line.ItemId);
        Assert.Equal(1, line.Quantity);
        Assert.Equal("cans", line.Unit);
        Assert.Equal(expiry, line.ExpiryDate);

        await consumption.UndoAsync(applied.EventId);

        Assert.Equal(3, (await inventory.GetAsync(item.Id))!.Quantity);
        Assert.Empty(await consumption.GetForItemAsync(item.Id, includeUndone: false));
        var undone = Assert.Single(await consumption.GetForItemAsync(item.Id, includeUndone: true));
        Assert.NotNull(undone.UndoneAt);
        Assert.False(undone.CanUndo);
        await Assert.ThrowsAsync<InvalidOperationException>(() => consumption.UndoAsync(applied.EventId));
        Assert.Equal(3, (await inventory.GetAsync(item.Id))!.Quantity);
    }

    [Fact]
    public async Task Deleted_lot_keeps_history_but_prevents_unsafe_undo()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var consumption = new ConsumptionService(db.Factory);
        var item = await inventory.SaveAsync(new Item
        {
            Name = "Yoghurt",
            ItemKindId = 1,
            Quantity = 1,
            Unit = "pot"
        });
        var applied = await consumption.UseItemAsync(item.Id);

        await inventory.DeleteAsync(item.Id);

        var history = Assert.Single(await consumption.GetHistoryAsync(new ConsumptionHistoryQuery(
            Search: "yoghurt",
            Kind: ConsumptionKind.Manual,
            IncludeUndone: true)));
        Assert.Null(Assert.Single(history.Lines).ItemId);
        Assert.False(history.CanUndo);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            consumption.UndoAsync(applied.EventId));
        Assert.Contains("deleted", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Use_rejects_more_than_the_available_quantity_without_history()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        var consumption = new ConsumptionService(db.Factory);
        var item = await inventory.SaveAsync(new Item
        {
            Name = "Rice",
            ItemKindId = 1,
            Quantity = 0.5m,
            Unit = "kg"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => consumption.UseItemAsync(item.Id, 1));

        Assert.Equal(0.5m, (await inventory.GetAsync(item.Id))!.Quantity);
        Assert.Empty(await consumption.GetHistoryAsync(new ConsumptionHistoryQuery(IncludeUndone: true)));
    }
}
