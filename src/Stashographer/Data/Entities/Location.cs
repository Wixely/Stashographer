namespace Stashographer.Data.Entities;

/// <summary>
/// A room or area in the house (Kitchen, Garage, Loft…). Locations may contain
/// <see cref="Container"/>s (boxes/shelves) and items may live directly in a location or
/// inside one of its containers.
/// </summary>
public class Location
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Populated by the data layer when locations are loaded with their containers.</summary>
    public List<Container> Containers { get; set; } = new();
}
