namespace Stashographer.Data.Entities;

/// <summary>How an image contributes to an item's catalogue record.</summary>
public enum ItemImageRole
{
    Front,
    Back,
    Detail,
    Label,
    Receipt,
    Other
}

/// <summary>
/// An ordered item/image association. The same image may belong to several items, while an
/// item can have front, back, detail, receipt, and other views without affecting quantity.
/// </summary>
public sealed class ItemImage
{
    public int ItemId { get; set; }
    public int ImageId { get; set; }
    public ItemImageRole Role { get; set; }
    public bool IsPrimary { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Image Image { get; set; } = new();
}

public enum ImageDerivationKind
{
    Crop
}

/// <summary>Provenance for a generated image, including its retained source region.</summary>
public sealed record ImageDerivation(
    int ParentImageId,
    int ChildImageId,
    ImageDerivationKind Kind,
    decimal? CropX,
    decimal? CropY,
    decimal? CropWidth,
    decimal? CropHeight,
    DateTimeOffset CreatedAt);
