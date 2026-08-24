using System.Security.Cryptography;
using Dapper;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using Stashographer.Data;
using Entities = Stashographer.Data.Entities;

namespace Stashographer.Services.Images;

/// <summary>
/// Stores image originals on disk and generates cached thumbnails on demand. Metadata lives
/// in the <c>Images</c> table; identical uploads are de-duplicated by content hash.
/// </summary>
public class ImageService
{
    private readonly IDbConnectionFactory _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ImageService> _logger;
    private readonly ImageOptions _options;
    private readonly string _originalsDir;
    private readonly string _thumbsDir;

    public ImageService(
        IDbConnectionFactory db,
        ImageOptions options,
        IHostEnvironment env,
        IHttpClientFactory httpFactory,
        ILogger<ImageService> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _logger = logger;
        _options = options;

        var root = Path.IsPathRooted(options.RootPath)
            ? options.RootPath
            : Path.Combine(env.ContentRootPath, options.RootPath);
        _originalsDir = Path.Combine(root, "originals");
        _thumbsDir = Path.Combine(root, "thumbs");
        Directory.CreateDirectory(_originalsDir);
        Directory.CreateDirectory(_thumbsDir);
    }

    private const string Columns =
        "Id, StorageKey, ContentType, OriginalName, Width, Height, ByteSize, Sha256, SourceUrl, CreatedAt";

    public async Task<Entities.Image?> GetAsync(int id, CancellationToken ct = default)
    {
        using var conn = await _db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Entities.Image>(
            $"SELECT {Columns} FROM Images WHERE Id = @id", new { id });
    }

    /// <summary>
    /// Saves an uploaded/captured/downloaded image, de-duplicating by content hash.
    /// Untrusted input is sanitized: size-bounded, dimension-guarded (decompression bombs),
    /// fully decoded, stripped of all metadata (EXIF/ICC/IPTC/XMP), downscaled to the
    /// configured maximum, and re-encoded into our standard formats (JPEG for JPEG sources,
    /// PNG for everything else). Only the clean re-encoded bytes ever touch disk — nothing
    /// from the original container survives except the pixels.
    /// </summary>
    public async Task<Entities.Image> SaveAsync(
        Stream content, string? contentType, string? originalName, string? sourceUrl = null,
        CancellationToken ct = default)
    {
        var bytes = await ReadBoundedAsync(content, _options.MaxUploadBytes, ct);

        // Cheap header sniff first: format + declared dimensions, no full decode yet.
        var info = SixLabors.ImageSharp.Image.Identify(bytes, out var sourceFormat);
        if (info is null || sourceFormat is null)
            throw new InvalidDataException("The data is not a recognized image.");
        if (info.Width <= 0 || info.Height <= 0
            || (long)info.Width * info.Height > _options.MaxDecodedPixels)
            throw new InvalidDataException("The image dimensions are not acceptable.");

        // Sanitize: decode → strip metadata → cap size → re-encode.
        byte[] clean;
        int width, height;
        string ext, mime;
        using (var img = SixLabors.ImageSharp.Image.Load(bytes))
        {
            // Phone cameras commonly store portrait pixels sideways and describe the intended
            // rotation in EXIF. Apply that transform before removing metadata so the sanitized
            // image keeps the orientation the user saw in their camera/gallery.
            img.Mutate(x => x.AutoOrient());

            img.Metadata.ExifProfile = null;
            img.Metadata.IccProfile = null;
            img.Metadata.IptcProfile = null;
            img.Metadata.XmpProfile = null;

            var max = _options.MaxStoredDimension;
            if (img.Width > max || img.Height > max)
                img.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new SixLabors.ImageSharp.Size(max, max),
                    Mode = ResizeMode.Max
                }));

            using var ms = new MemoryStream();
            if (string.Equals(sourceFormat.Name, "JPEG", StringComparison.OrdinalIgnoreCase))
            {
                await img.SaveAsJpegAsync(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 85 }, ct);
                (ext, mime) = ("jpg", "image/jpeg");
            }
            else
            {
                await img.SaveAsPngAsync(ms, ct); // preserves alpha for png/gif/bmp/webp sources
                (ext, mime) = ("png", "image/png");
            }
            clean = ms.ToArray();
            (width, height) = (img.Width, img.Height);
        }

        // Dedup on the sanitized bytes: the same source always re-encodes identically.
        var sha = Convert.ToHexString(SHA256.HashData(clean)).ToLowerInvariant();

        using var conn = await _db.OpenAsync(ct);
        var existing = await conn.QuerySingleOrDefaultAsync<Entities.Image>(
            $"SELECT {Columns} FROM Images WHERE Sha256 = @sha LIMIT 1", new { sha });
        if (existing is not null) return existing;

        var storageKey = $"{Guid.NewGuid():N}.{ext}";
        await File.WriteAllBytesAsync(Path.Combine(_originalsDir, storageKey), clean, ct);

        var image = new Entities.Image
        {
            StorageKey = storageKey,
            ContentType = mime,
            OriginalName = originalName,
            Width = width,
            Height = height,
            ByteSize = clean.LongLength,
            Sha256 = sha,
            SourceUrl = sourceUrl,
            CreatedAt = DateTimeOffset.UtcNow
        };
        image.Id = await conn.ExecuteScalarAsync<int>($"""
            INSERT INTO Images (StorageKey, ContentType, OriginalName, Width, Height, ByteSize, Sha256, SourceUrl, CreatedAt)
            VALUES (@StorageKey, @ContentType, @OriginalName, @Width, @Height, @ByteSize, @Sha256, @SourceUrl, @CreatedAt);
            SELECT last_insert_rowid();
            """, image);
        return image;
    }

    /// <summary>
    /// Downloads an image from a URL and stores it (sanitized like any other ingest).
    /// http/https only; size-capped before and during download; errors stay generic so
    /// nothing about a probed endpoint's response is echoed back.
    /// </summary>
    public async Task<Entities.Image> SaveFromUrlAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Only http(s) image URLs are supported.");

        var http = _httpFactory.CreateClient(nameof(ImageService));
        using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength is { } declared && declared > _options.MaxUploadBytes)
            throw new InvalidDataException("The image is too large.");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var name = Path.GetFileName(uri.AbsolutePath);
        return await SaveAsync(stream, resp.Content.Headers.ContentType?.MediaType,
            string.IsNullOrWhiteSpace(name) ? "download" : name, url, ct);
    }

    /// <summary>Reads a stream into memory, rejecting anything over the limit mid-stream.</summary>
    private static async Task<byte[]> ReadBoundedAsync(Stream content, long limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit)
                throw new InvalidDataException("The image is too large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public (Stream Stream, string ContentType)? OpenOriginal(Entities.Image image)
    {
        var path = Path.Combine(_originalsDir, image.StorageKey);
        return File.Exists(path)
            ? (File.OpenRead(path), image.ContentType)
            : null;
    }

    /// <summary>Reads an image's original bytes, or null when the file is gone.</summary>
    public async Task<byte[]?> ReadOriginalBytesAsync(int id, CancellationToken ct = default)
    {
        var image = await GetAsync(id, ct);
        if (image is null) return null;
        var path = Path.Combine(_originalsDir, image.StorageKey);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
    }

    /// <summary>
    /// Crops a normalized (0–1) box out of a stored image — with padding, clamped to the
    /// image bounds — and stores the crop as a new image (PNG, deduped like any upload).
    /// </summary>
    public async Task<Entities.Image?> CropAsync(
        int imageId, double x, double y, double w, double h, double padding = 0.08,
        double? targetAspectRatio = null,
        CancellationToken ct = default)
    {
        var source = await GetAsync(imageId, ct);
        if (source is null) return null;
        var path = Path.Combine(_originalsDir, source.StorageKey);
        if (!File.Exists(path)) return null;

        using var img = await SixLabors.ImageSharp.Image.LoadAsync(path, ct);
        var sourceWidth = img.Width;
        var sourceHeight = img.Height;

        // Expand around the detected object to the requested pixel aspect ratio. Expansion
        // preserves the whole object; the later bounds clamp tolerates objects near an edge.
        if (targetAspectRatio is > 0)
        {
            var pixelAspect = w * img.Width / Math.Max(0.0001, h * img.Height);
            if (pixelAspect < targetAspectRatio.Value)
            {
                var expandedWidth = h * img.Height * targetAspectRatio.Value / img.Width;
                x -= (expandedWidth - w) / 2;
                w = expandedWidth;
            }
            else if (pixelAspect > targetAspectRatio.Value)
            {
                var expandedHeight = w * img.Width / targetAspectRatio.Value / img.Height;
                y -= (expandedHeight - h) / 2;
                h = expandedHeight;
            }
        }

        // Pad the box outward, then clamp to the image.
        var px = x - w * padding;
        var py = y - h * padding;
        var pw = w * (1 + 2 * padding);
        var ph = h * (1 + 2 * padding);

        var rx = Math.Clamp((int)(px * img.Width), 0, img.Width - 1);
        var ry = Math.Clamp((int)(py * img.Height), 0, img.Height - 1);
        var rw = Math.Clamp((int)(pw * img.Width), 1, img.Width - rx);
        var rh = Math.Clamp((int)(ph * img.Height), 1, img.Height - ry);

        img.Mutate(c => c.Crop(new SixLabors.ImageSharp.Rectangle(rx, ry, rw, rh)));

        using var ms = new MemoryStream();
        await img.SaveAsPngAsync(ms, ct);
        ms.Position = 0;
        var crop = await SaveAsync(ms, "image/png", $"crop-of-{imageId}.png", null, ct);
        if (crop.Id != imageId)
        {
            using var conn = await _db.OpenAsync(ct);
            await conn.ExecuteAsync("""
                INSERT INTO ImageDerivations
                    (ParentImageId, ChildImageId, Kind, CropX, CropY, CropWidth, CropHeight, CreatedAt)
                VALUES
                    (@parentImageId, @childImageId, @kind, @cropX, @cropY, @cropWidth, @cropHeight, @createdAt)
                ON CONFLICT(ParentImageId, ChildImageId, Kind) DO UPDATE SET
                    CropX = excluded.CropX,
                    CropY = excluded.CropY,
                    CropWidth = excluded.CropWidth,
                    CropHeight = excluded.CropHeight;
                """, new
            {
                parentImageId = imageId,
                childImageId = crop.Id,
                kind = (int)Entities.ImageDerivationKind.Crop,
                cropX = (decimal)rx / sourceWidth,
                cropY = (decimal)ry / sourceHeight,
                cropWidth = (decimal)rw / sourceWidth,
                cropHeight = (decimal)rh / sourceHeight,
                createdAt = DateTimeOffset.UtcNow.ToString("O")
            });
        }
        return crop;
    }

    public async Task<List<Entities.ImageDerivation>> GetDerivationsAsync(
        int childImageId, CancellationToken ct = default)
    {
        using var conn = await _db.OpenAsync(ct);
        var rows = await conn.QueryAsync<DerivationRow>("""
            SELECT ParentImageId, ChildImageId, Kind, CropX, CropY, CropWidth, CropHeight, CreatedAt
            FROM ImageDerivations WHERE ChildImageId = @childImageId ORDER BY CreatedAt;
            """, new { childImageId });
        return rows.Select(row => new Entities.ImageDerivation(
            row.ParentImageId,
            row.ChildImageId,
            (Entities.ImageDerivationKind)row.Kind,
            row.CropX,
            row.CropY,
            row.CropWidth,
            row.CropHeight,
            DateTimeOffset.Parse(row.CreatedAt))).ToList();
    }

    /// <summary>Returns a thumbnail no wider than <paramref name="width"/>, generating and caching it once.</summary>
    public async Task<(byte[] Bytes, string ContentType)?> GetThumbnailAsync(
        int id, int width, CancellationToken ct = default)
    {
        var image = await GetAsync(id, ct);
        if (image is null) return null;

        var originalPath = Path.Combine(_originalsDir, image.StorageKey);
        if (!File.Exists(originalPath)) return null;

        var cacheDir = Path.Combine(_thumbsDir, width.ToString());
        var cachePath = Path.Combine(cacheDir, image.StorageKey);
        if (File.Exists(cachePath))
            return (await File.ReadAllBytesAsync(cachePath, ct), image.ContentType);

        Directory.CreateDirectory(cacheDir);
        using var img = await SixLabors.ImageSharp.Image.LoadAsync(originalPath, ct);
        var targetWidth = Math.Min(width, img.Width);
        img.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new SixLabors.ImageSharp.Size(targetWidth, 0),
            Mode = ResizeMode.Max
        }));
        await img.SaveAsync(cachePath, ct); // encoder chosen by extension → preserves format
        return (await File.ReadAllBytesAsync(cachePath, ct), image.ContentType);
    }

    /// <summary>Deletes an image, its cached thumbnails, and clears any references to it.</summary>
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var image = await GetAsync(id, ct);
        if (image is null) return;

        using var conn = await _db.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        var affectedItems = (await conn.QueryAsync<int>(
            "SELECT ItemId FROM ItemImages WHERE ImageId = @id AND IsPrimary = 1",
            new { id }, tx)).ToList();
        await conn.ExecuteAsync("UPDATE Containers SET ImageId = NULL WHERE ImageId = @id", new { id }, tx);
        await conn.ExecuteAsync("UPDATE Locations SET ImageId = NULL WHERE ImageId = @id", new { id }, tx);
        await conn.ExecuteAsync("DELETE FROM Images WHERE Id = @id", new { id }, tx);
        foreach (var itemId in affectedItems)
        {
            var replacement = await conn.QuerySingleOrDefaultAsync<int?>("""
                SELECT ImageId FROM ItemImages
                WHERE ItemId = @itemId AND Role <> @receipt
                ORDER BY SortOrder, CreatedAt LIMIT 1;
                """, new { itemId, receipt = (int)Entities.ItemImageRole.Receipt }, tx);
            if (replacement is { } imageId)
                await conn.ExecuteAsync(
                    "UPDATE ItemImages SET IsPrimary = 1 WHERE ItemId = @itemId AND ImageId = @imageId",
                    new { itemId, imageId }, tx);
            await conn.ExecuteAsync(
                "UPDATE Items SET ImageId = @imageId, UpdatedAt = @now WHERE Id = @itemId",
                new { itemId, imageId = replacement, now = DateTimeOffset.UtcNow.ToString("O") }, tx);
        }
        tx.Commit();

        TryDelete(Path.Combine(_originalsDir, image.StorageKey));
        if (Directory.Exists(_thumbsDir))
            foreach (var dir in Directory.EnumerateDirectories(_thumbsDir))
                TryDelete(Path.Combine(dir, image.StorageKey));
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException ex) { _logger.LogWarning(ex, "Could not delete image file {Path}", path); }
    }

    private sealed class DerivationRow
    {
        public int ParentImageId { get; set; }
        public int ChildImageId { get; set; }
        public int Kind { get; set; }
        public decimal? CropX { get; set; }
        public decimal? CropY { get; set; }
        public decimal? CropWidth { get; set; }
        public decimal? CropHeight { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
    }
}
