using Stashographer.Data.Entities;
using Stashographer.Services.Inventory;

namespace Stashographer.Tests;

public class CandidateSearchTests
{
    [Fact]
    public async Task Exact_barcode_match_wins_and_returns_only_that()
    {
        await using var db = await TestDb.CreateAsync();
        var svc = new InventoryService(db.Factory);
        var beans = await svc.SaveAsync(new Item { Name = "Baked Beans", Code = "5000157024671", ItemKindId = 1 });
        await svc.SaveAsync(new Item { Name = "Beans Salad", ItemKindId = 1 }); // name-similar noise

        var candidates = await svc.FindCandidatesAsync("Something Else Entirely", "5000157024671");

        Assert.Single(candidates);
        Assert.Equal(beans.Id, candidates[0].Id);
    }

    [Fact]
    public async Task Token_scoring_ranks_better_matches_first()
    {
        await using var db = await TestDb.CreateAsync();
        var svc = new InventoryService(db.Factory);
        await svc.SaveAsync(new Item { Name = "Coca-Cola (can)", ItemKindId = 1 });
        await svc.SaveAsync(new Item { Name = "Cola Bottle", ItemKindId = 1 });
        await svc.SaveAsync(new Item { Name = "Hammer", ItemKindId = 3 });

        var candidates = await svc.FindCandidatesAsync("Coca Cola can");

        Assert.True(candidates.Count >= 2);
        Assert.Equal("Coca-Cola (can)", candidates[0].Name); // matches most tokens
        Assert.DoesNotContain(candidates, c => c.Name == "Hammer");
    }

    [Fact]
    public async Task Blank_name_and_no_barcode_returns_nothing()
    {
        await using var db = await TestDb.CreateAsync();
        var svc = new InventoryService(db.Factory);
        await svc.SaveAsync(new Item { Name = "Anything", ItemKindId = 7 });

        Assert.Empty(await svc.FindCandidatesAsync(null));
        Assert.Empty(await svc.FindCandidatesAsync("  "));
    }

    [Fact]
    public void NormalizeName_strips_punctuation_and_case()
    {
        Assert.Equal(InventoryService.NormalizeName("Coca-Cola (Can)"), InventoryService.NormalizeName("coca cola can"));
        Assert.NotEqual(InventoryService.NormalizeName("Coke"), InventoryService.NormalizeName("Cola"));
    }

    [Fact]
    public async Task Result_cap_is_respected()
    {
        await using var db = await TestDb.CreateAsync();
        var svc = new InventoryService(db.Factory);
        for (var i = 0; i < 12; i++)
            await svc.SaveAsync(new Item { Name = $"Widget {i}", ItemKindId = 7 });

        var candidates = await svc.FindCandidatesAsync("Widget", top: 8);
        Assert.Equal(8, candidates.Count);
    }
}
