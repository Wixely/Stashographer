namespace Stashographer.Data.Entities;

public enum ContainerType
{
    Box,
    Shelf,
    Drawer,
    Bin,
    Cupboard,
    Other
}

/// <summary>
/// A sub-location within a <see cref="Location"/> — a physical container such as a box or
/// shelf. Each container has a unique <see cref="QrSlug"/> that a printed QR code points at
/// (<c>/c/{slug}</c>), so scanning the label opens the list of what is (meant to be) inside.
/// </summary>
public class Container
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public ContainerType ContainerType { get; set; } = ContainerType.Box;

    /// <summary>Short, URL-safe, unique identifier encoded into the printed QR code.</summary>
    public string QrSlug { get; set; } = string.Empty;

    public string? Description { get; set; }

    public int LocationId { get; set; }
    public Location? Location { get; set; }

    /// <summary>Populated by the data layer when a container is loaded with its contents.</summary>
    public List<Item> Items { get; set; } = new();
}
