namespace Stashographer.Data.Entities;

/// <summary>
/// Metadata for a stored image. The binary lives on disk under the configured image root
/// (keyed by <see cref="StorageKey"/>); thumbnails are generated on demand and cached there.
/// </summary>
public class Image
{
    public int Id { get; set; }

    /// <summary>On-disk filename of the original (a GUID plus the original extension).</summary>
    public string StorageKey { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/octet-stream";

    public string? OriginalName { get; set; }

    public int? Width { get; set; }
    public int? Height { get; set; }
    public long? ByteSize { get; set; }

    /// <summary>SHA-256 of the original bytes, used to de-duplicate identical uploads.</summary>
    public string? Sha256 { get; set; }

    /// <summary>Populated when the image was downloaded from a URL.</summary>
    public string? SourceUrl { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
