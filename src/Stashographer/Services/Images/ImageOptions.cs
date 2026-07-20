namespace Stashographer.Services.Images;

/// <summary>Where image binaries live on disk. Bound from the <c>Images</c> config section.</summary>
public class ImageOptions
{
    public const string SectionName = "Images";

    /// <summary>
    /// Root folder for stored originals and generated thumbnails. Relative paths are resolved
    /// against the app content root; in Docker set an absolute path on the mounted volume
    /// (e.g. <c>/data/images</c>) so photos survive container restarts.
    /// </summary>
    public string RootPath { get; set; } = "App_Data/images";

    /// <summary>Largest accepted payload (upload or URL download), in bytes.</summary>
    public long MaxUploadBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>
    /// Decompression-bomb guard: reject images whose declared width × height exceeds this
    /// before any full decode is attempted.
    /// </summary>
    public long MaxDecodedPixels { get; set; } = 40_000_000;

    /// <summary>Stored images are downscaled so their longest edge does not exceed this.</summary>
    public int MaxStoredDimension { get; set; } = 2560;
}
