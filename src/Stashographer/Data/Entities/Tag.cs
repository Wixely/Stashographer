namespace Stashographer.Data.Entities;

/// <summary>A reusable, case-insensitively unique label assigned to inventory items.</summary>
public sealed class Tag
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ItemCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
