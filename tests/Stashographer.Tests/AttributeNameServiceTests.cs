using Stashographer.Data.Entities;
using Stashographer.Services.Inventory;

namespace Stashographer.Tests;

public class AttributeNameServiceTests
{
    [Fact]
    public async Task Vocabulary_combines_kind_suggestions_with_existing_inventory_usage()
    {
        await using var db = await TestDb.CreateAsync();
        var inventory = new InventoryService(db.Factory);
        await inventory.SaveAsync(new Item
        {
            Name = "Amplifier",
            ItemKindId = 4,
            Attributes = new() { ["Wattage"] = "50 W", ["Color"] = "Black" }
        });

        var names = await new AttributeNameService(db.Factory)
            .GetCanonicalNamesAsync(kindId: 4);

        Assert.Contains("Wattage", names);
        Assert.Contains("Color", names);
        var ordered = names.ToList();
        Assert.True(ordered.IndexOf("Brand") < ordered.IndexOf("Wattage"));
    }

    [Fact]
    public void Canonicalize_reuses_safe_equivalents_and_preserves_unknown_names()
    {
        var canonical = AttributeNameService.Canonicalize(
            new Dictionary<string, string>
            {
                ["color"] = "Blue",
                ["Colour"] = "Green",
                ["model_number"] = "ZX-42",
                ["Ingress rating"] = "IP67"
            },
            ["Colour", "Model", "Serial number"]);

        Assert.Equal("Green", canonical["Colour"]);
        Assert.Equal("ZX-42", canonical["Model"]);
        Assert.Equal("IP67", canonical["Ingress rating"]);
    }

    [Fact]
    public async Task Inventory_save_normalizes_attribute_names_before_persistence()
    {
        await using var db = await TestDb.CreateAsync();
        var names = new AttributeNameService(db.Factory);
        var inventory = new InventoryService(db.Factory, null, names);
        var item = await inventory.SaveAsync(new Item
        {
            Name = "Jacket",
            ItemKindId = 6,
            Attributes = new() { ["Color"] = "Green" }
        });

        var saved = await inventory.GetAsync(item.Id);
        Assert.NotNull(saved);
        Assert.Equal("Green", saved!.Attributes["Colour"]);
        Assert.DoesNotContain("Color", saved.Attributes.Keys);
    }
}
