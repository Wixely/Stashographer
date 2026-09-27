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
        Assert.Equal("Xmas box", loaded!.Name);
        Assert.Equal("Garage", loaded.Location?.Name);
        Assert.Single(loaded.Items);
        Assert.Equal("Fairy lights", loaded.Items[0].Name);
    }

    [Fact]
    public async Task Room_and_container_names_preserve_words_and_normalize_extra_whitespace()
    {
        await using var db = await TestDb.CreateAsync();
        var containers = new ContainerService(db.Factory);

        var room = await containers.SaveLocationAsync(new Location { Name = "  Utility   room  " });
        var container = await containers.SaveContainerAsync(new Container
        {
            Name = "  Under   stairs box  ",
            LocationId = room.Id
        });

        var savedRoom = (await containers.GetLocationsAsync()).Single(location => location.Id == room.Id);
        Assert.Equal("Utility room", savedRoom.Name);
        Assert.Equal("Under stairs box", savedRoom.Containers.Single(x => x.Id == container.Id).Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Place_names_require_at_least_one_non_whitespace_character(string? name)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => ContainerService.NormalizePlaceName(name, "container"));

        Assert.Equal("Enter a name for the container.", error.Message);
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
