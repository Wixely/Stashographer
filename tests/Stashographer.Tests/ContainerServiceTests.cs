using Stashographer.Data.Entities;
using Stashographer.Services.Inventory;

namespace Stashographer.Tests;

public class ContainerServiceTests
{
    [Fact]
    public void Qr_png_has_valid_signature()
    {
        var png = ContainerService.GenerateQrPng("https://example/c/abc123");
        // PNG magic number.
        Assert.True(png.Length > 8);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
    }

    [Fact]
    public void Slug_is_short_and_unique()
    {
        var slugs = Enumerable.Range(0, 100).Select(_ => ContainerService.GenerateSlug()).ToList();
        Assert.All(slugs, s => Assert.Equal(10, s.Length));
        Assert.Equal(slugs.Count, slugs.Distinct().Count());
    }

    [Fact]
    public async Task Container_roundtrips_and_lists_its_items_by_slug()
    {
        await using var db = await TestDb.CreateAsync();
        var containers = new ContainerService(db.Factory);
        var inventory = new InventoryService(db.Factory);

        // Location 3 = Garage (seeded).
        var container = await containers.SaveContainerAsync(new Container
        {
            Name = "Xmas box", ContainerType = ContainerType.Box, LocationId = 3
        });
        Assert.False(string.IsNullOrWhiteSpace(container.QrSlug));

        await inventory.SaveAsync(new Item { Name = "Fairy lights", ItemKindId = 7, ContainerId = container.Id });

        var loaded = await containers.GetContainerBySlugAsync(container.QrSlug);
        Assert.NotNull(loaded);
        Assert.Equal("Garage", loaded!.Location?.Name);
        Assert.Single(loaded.Items);
        Assert.Equal("Fairy lights", loaded.Items[0].Name);
    }

    [Fact]
    public async Task Locations_include_their_containers()
    {
        await using var db = await TestDb.CreateAsync();
        var containers = new ContainerService(db.Factory);
        await containers.SaveContainerAsync(new Container { Name = "Bin A", LocationId = 3 });

        var locations = await containers.GetLocationsAsync();
        var garage = locations.Single(l => l.Name == "Garage");
        Assert.Contains(garage.Containers, c => c.Name == "Bin A");
    }
}
