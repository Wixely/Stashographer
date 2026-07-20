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
}
