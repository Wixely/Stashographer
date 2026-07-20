namespace Stashographer;

/// <summary>
/// Builds image <c>src</c> values consistently across the app: a stored image is served from
/// <c>/img/{id}</c> (optionally as a thumbnail via <c>?w=</c>), otherwise an optional remote
/// fallback URL (e.g. a lookup provider's cover) is used.
/// </summary>
public static class ImageUrls
{
    public static string? For(int? imageId, string? fallbackUrl = null, int? width = null)
    {
        if (imageId is { } id)
            return width is { } w ? $"/img/{id}?w={w}" : $"/img/{id}";
        return string.IsNullOrWhiteSpace(fallbackUrl) ? null : fallbackUrl;
    }

    public static bool Has(int? imageId, string? fallbackUrl = null) =>
        imageId is not null || !string.IsNullOrWhiteSpace(fallbackUrl);
}
