namespace Stashographer.Data.Entities;

/// <summary>Free-form label for grouping items across kinds and locations (phase 2 UI).</summary>
public class Tag
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}
