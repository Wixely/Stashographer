using Stashographer.Data.Entities;
using Stashographer.Services.Inventory;

namespace Stashographer.Tests;

public class QuickLinksTests
{
    [Fact]
    public void ToUrl_builds_page_and_filtered_inventory_targets()
    {
        Assert.Equal("dashboard", new QuickLink { Target = QuickLinkTarget.Dashboard }.ToUrl());
        Assert.Equal("scan", new QuickLink { Target = QuickLinkTarget.Scan }.ToUrl());

        Assert.Equal("inventory?include=1",
            new QuickLink { Target = QuickLinkTarget.Inventory, IncludeKindIds = new() { 1 } }.ToUrl());

        Assert.Equal("inventory?exclude=1,2",
            new QuickLink { Target = QuickLinkTarget.Inventory, ExcludeKindIds = new() { 1, 2 } }.ToUrl());

        Assert.Equal("inventory?include=3&exclude=1,2",
            new QuickLink
            {
                Target = QuickLinkTarget.Inventory,
                IncludeKindIds = new() { 3 },
                ExcludeKindIds = new() { 1, 2 }
            }.ToUrl());
    }

    [Fact]
    public async Task Seeds_five_default_quick_links()
    {
        await using var db = await TestDb.CreateAsync();
        var svc = new QuickLinksService(db.Factory);

        var links = await svc.GetAllAsync();

        Assert.Equal(5, links.Count);
        Assert.Equal(new[] { "Items", "Groceries", "Books", "Dashboard", "Scan" }, links.Select(l => l.Label));
        // "Items" excludes groceries (1) and books (2).
        var items = links.Single(l => l.Label == "Items");
        Assert.Equal(new[] { 1, 2 }, items.ExcludeKindIds);
        // "Groceries" includes groceries (1).
        Assert.Equal(new[] { 1 }, links.Single(l => l.Label == "Groceries").IncludeKindIds);
    }

    [Fact]
    public async Task Save_delete_and_reorder_roundtrip()
    {
        await using var db = await TestDb.CreateAsync();
        var svc = new QuickLinksService(db.Factory);

        var created = await svc.SaveAsync(new QuickLink
        {
            Label = "Tools", Icon = "Handyman", Target = QuickLinkTarget.Inventory, IncludeKindIds = new() { 3 }
        });
        Assert.True(created.Id > 0);
        Assert.Equal(6, (await svc.GetAllAsync()).Count);

        // Move the new last item up one and confirm order changed.
        var before = await svc.GetAllAsync();
        var last = before[^1];
        await svc.MoveAsync(last.Id, -1);
        var after = await svc.GetAllAsync();
        Assert.Equal(last.Id, after[^2].Id);

        await svc.DeleteAsync(created.Id);
        Assert.DoesNotContain(await svc.GetAllAsync(), l => l.Id == created.Id);
    }
}
