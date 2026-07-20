using Dapper;
using QRCoder;
using Stashographer.Data;
using Stashographer.Data.Entities;

namespace Stashographer.Services.Inventory;

/// <summary>Explorer badge counts: loose items per room and items per container.</summary>
public record PlaceCounts(
    IReadOnlyDictionary<int, int> LooseByLocation,
    IReadOnlyDictionary<int, int> ItemsByContainer);

/// <summary>
/// Manages storage locations and their containers, and generates the printable QR codes that
/// address a container at <c>/c/{slug}</c>. QR PNGs are produced with QRCoder's
/// <see cref="PngByteQRCode"/>, which has no native/System.Drawing dependency, so it works in
/// a Linux Docker container.
/// </summary>
public class ContainerService(IDbConnectionFactory db)
{
    public async Task<List<Location>> GetLocationsAsync(CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var locations = (await conn.QueryAsync<Location>(
            "SELECT Id, Name, Description, ImageId FROM Locations ORDER BY Name COLLATE NOCASE")).ToList();
        var containers = (await conn.QueryAsync<Container>(
            "SELECT Id, Name, ContainerType, QrSlug, Description, LocationId, ImageId FROM Containers ORDER BY Name COLLATE NOCASE"))
            .ToList();

        var byLocation = containers.ToLookup(c => c.LocationId);
        foreach (var l in locations)
            l.Containers = byLocation[l.Id].ToList();
        return locations;
    }

    public async Task<Location> SaveLocationAsync(Location location, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        if (location.Id == 0)
        {
            location.Id = await conn.ExecuteScalarAsync<int>("""
                INSERT INTO Locations (Name, Description, ImageId) VALUES (@Name, @Description, @ImageId);
                SELECT last_insert_rowid();
                """, location);
        }
        else
        {
            await conn.ExecuteAsync(
                "UPDATE Locations SET Name=@Name, Description=@Description, ImageId=@ImageId WHERE Id=@Id", location);
        }
        return location;
    }

    public async Task<Container> SaveContainerAsync(Container container, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(container.QrSlug))
            container.QrSlug = GenerateSlug();

        using var conn = await db.OpenAsync(ct);
        if (container.Id == 0)
        {
            container.Id = await conn.ExecuteScalarAsync<int>("""
                INSERT INTO Containers (Name, ContainerType, QrSlug, Description, LocationId, ImageId)
                VALUES (@Name, @ContainerType, @QrSlug, @Description, @LocationId, @ImageId);
                SELECT last_insert_rowid();
                """, container);
        }
        else
        {
            await conn.ExecuteAsync("""
                UPDATE Containers SET Name=@Name, ContainerType=@ContainerType, QrSlug=@QrSlug,
                    Description=@Description, LocationId=@LocationId, ImageId=@ImageId WHERE Id=@Id
                """, container);
        }
        return container;
    }

    public async Task<Container?> GetContainerBySlugAsync(string slug, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var container = await conn.QuerySingleOrDefaultAsync<Container>("""
            SELECT Id, Name, ContainerType, QrSlug, Description, LocationId, ImageId
            FROM Containers WHERE QrSlug = @slug
            """, new { slug });
        if (container is null) return null;

        container.Location = await conn.QuerySingleOrDefaultAsync<Location>(
            "SELECT Id, Name, Description, ImageId FROM Locations WHERE Id = @id", new { id = container.LocationId });

        var items = await conn.QueryAsync<Item>("""
            SELECT i.Id, i.Name, i.Quantity, i.Unit, i.ItemKindId, i.ImageId, i.ThumbnailUrl,
                   EXISTS (SELECT 1 FROM Checkouts co WHERE co.ItemId = i.Id AND co.ReturnedAt IS NULL) AS IsCheckedOut
            FROM Items i WHERE i.ContainerId = @id ORDER BY i.Name COLLATE NOCASE
            """, new { id = container.Id });
        container.Items = items.ToList();
        return container;
    }

    /// <summary>Moves a container (and implicitly its contents) to another room.</summary>
    public async Task MoveContainerAsync(int containerId, int locationId, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE Containers SET LocationId = @locationId WHERE Id = @containerId",
            new { containerId, locationId });
    }

    /// <summary>Counts for explorer cards/badges: loose items per room, items per container.</summary>
    public async Task<PlaceCounts> GetPlaceCountsAsync(CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var loose = (await conn.QueryAsync<(int LocationId, int Count)>("""
            SELECT LocationId, COUNT(*) AS Count FROM Items
            WHERE LocationId IS NOT NULL AND ContainerId IS NULL GROUP BY LocationId
            """)).ToDictionary(r => r.LocationId, r => r.Count);
        var inContainers = (await conn.QueryAsync<(int ContainerId, int Count)>("""
            SELECT ContainerId, COUNT(*) AS Count FROM Items
            WHERE ContainerId IS NOT NULL GROUP BY ContainerId
            """)).ToDictionary(r => r.ContainerId, r => r.Count);
        return new PlaceCounts(loose, inContainers);
    }

    /// <summary>Short, URL-safe, collision-resistant slug for a container QR.</summary>
    public static string GenerateSlug() => Guid.NewGuid().ToString("N")[..10];

    /// <summary>Returns a PNG QR code encoding the given payload (typically the container URL).</summary>
    public static byte[] GenerateQrPng(string payload, int pixelsPerModule = 20)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        var qr = new PngByteQRCode(data);
        return qr.GetGraphic(pixelsPerModule);
    }

    /// <summary>Data URI form for embedding a QR directly in an &lt;img&gt; tag.</summary>
    public static string GenerateQrDataUri(string payload, int pixelsPerModule = 20) =>
        $"data:image/png;base64,{Convert.ToBase64String(GenerateQrPng(payload, pixelsPerModule))}";
}
