using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Stashographer.Data.Entities;
using Stashographer.Services.Images;
using Stashographer.Services.Inventory;

namespace Stashographer.Tests;

/// <summary>Sanitizing-ingest behavior: untrusted bytes/URLs never reach disk unprocessed.</summary>
public class ImageHardeningTests
{
    private sealed class StubHostEnv : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Test";
    }

    private sealed class RealHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static ImageService Create(TestDb db, string root, ImageOptions? options = null)
    {
        options ??= new ImageOptions();
        options.RootPath = root;
        return new ImageService(db.Factory, options, new StubHostEnv(), new RealHttpFactory(),
            NullLogger<ImageService>.Instance);
    }

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), $"stash_hard_{Guid.NewGuid():N}");

    private static async Task<byte[]> EncodeAsync(Image<Rgba32> img, string format)
    {
        using var ms = new MemoryStream();
        switch (format)
        {
            case "png": await img.SaveAsPngAsync(ms); break;
            case "jpeg": await img.SaveAsJpegAsync(ms); break;
            case "bmp": await img.SaveAsBmpAsync(ms); break;
            case "gif": await img.SaveAsGifAsync(ms); break;
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task Garbage_bytes_are_rejected()
    {
        await using var db = await TestDb.CreateAsync();
        var root = TempRoot();
        try
        {
            var svc = Create(db, root);
            var garbage = Encoding.UTF8.GetBytes("<html><script>alert('nope')</script></html>");
            await Assert.ThrowsAsync<InvalidDataException>(
                () => svc.SaveAsync(new MemoryStream(garbage), "image/png", "evil.png"));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "originals"))); // nothing written
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Everything_is_reencoded_to_standard_formats()
    {
        await using var db = await TestDb.CreateAsync();
        var root = TempRoot();
        try
        {
            var svc = Create(db, root);
            using var img = new Image<Rgba32>(40, 30, new Rgba32(10, 20, 30));

            // Non-JPEG sources (bmp/gif) standardize to PNG; JPEG stays JPEG.
            var fromBmp = await svc.SaveAsync(new MemoryStream(await EncodeAsync(img, "bmp")), null, "a.bmp");
            var fromGif = await svc.SaveAsync(new MemoryStream(await EncodeAsync(img, "gif")), null, "a.gif");
            var fromJpeg = await svc.SaveAsync(new MemoryStream(await EncodeAsync(img, "jpeg")), null, "a.jpg");

            Assert.Equal("image/png", fromBmp.ContentType);
            Assert.EndsWith(".png", fromBmp.StorageKey);
            Assert.Equal("image/png", fromGif.ContentType);
            Assert.Equal("image/jpeg", fromJpeg.ContentType);
            Assert.EndsWith(".jpg", fromJpeg.StorageKey);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Metadata_is_stripped_on_ingest()
    {
        await using var db = await TestDb.CreateAsync();
        var root = TempRoot();
        try
        {
            var svc = Create(db, root);
            using var img = new Image<Rgba32>(20, 20);
            var exif = new SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifProfile();
            exif.SetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Artist, "secret gps-laden metadata");
            img.Metadata.ExifProfile = exif;

            var stored = await svc.SaveAsync(new MemoryStream(await EncodeAsync(img, "jpeg")), null, "meta.jpg");

            var storedBytes = await svc.ReadOriginalBytesAsync(stored.Id);
            using var reloaded = SixLabors.ImageSharp.Image.Load(storedBytes!);
            Assert.Null(reloaded.Metadata.ExifProfile);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Camera_orientation_is_applied_before_metadata_is_stripped()
    {
        await using var db = await TestDb.CreateAsync();
        var root = TempRoot();
        try
        {
            var svc = Create(db, root);
            using var img = new Image<Rgba32>(40, 20);
            var exif = new SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifProfile();
            exif.SetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Orientation, (ushort)6);
            img.Metadata.ExifProfile = exif;

            var stored = await svc.SaveAsync(new MemoryStream(await EncodeAsync(img, "jpeg")), null, "portrait.jpg");

            Assert.Equal(20, stored.Width);
            Assert.Equal(40, stored.Height);
            var storedBytes = await svc.ReadOriginalBytesAsync(stored.Id);
            using var reloaded = SixLabors.ImageSharp.Image.Load(storedBytes!);
            Assert.Null(reloaded.Metadata.ExifProfile);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Oversized_images_are_downscaled_to_the_cap()
    {
        await using var db = await TestDb.CreateAsync();
        var root = TempRoot();
        try
        {
            var svc = Create(db, root, new ImageOptions { MaxStoredDimension = 64 });
            using var img = new Image<Rgba32>(128, 96);

            var stored = await svc.SaveAsync(new MemoryStream(await EncodeAsync(img, "png")), null, "big.png");

            Assert.Equal(64, stored.Width);  // long edge capped
            Assert.Equal(48, stored.Height); // aspect preserved
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Payloads_over_the_byte_cap_are_rejected()
    {
        await using var db = await TestDb.CreateAsync();
        var root = TempRoot();
        try
        {
            var svc = Create(db, root, new ImageOptions { MaxUploadBytes = 200 });
            using var img = new Image<Rgba32>(64, 64);
            // Pseudo-random noise so the PNG can't compress under the cap.
            for (var y = 0; y < 64; y++)
                for (var x = 0; x < 64; x++)
                    img[x, y] = new Rgba32((byte)(x * 31 + y * 17), (byte)(x * 7 ^ y * 13), (byte)(x * 3 + y * 41));
            var png = await EncodeAsync(img, "png");
            Assert.True(png.Length > 200);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => svc.SaveAsync(new MemoryStream(png), null, "big.png"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Declared_dimension_bombs_are_rejected_before_decode()
    {
        await using var db = await TestDb.CreateAsync();
        var root = TempRoot();
        try
        {
            var svc = Create(db, root, new ImageOptions { MaxDecodedPixels = 1000 }); // 1000 px budget
            using var img = new Image<Rgba32>(64, 64); // 4096 declared pixels
            var png = await EncodeAsync(img, "png");

            await Assert.ThrowsAsync<InvalidDataException>(
                () => svc.SaveAsync(new MemoryStream(png), null, "bomb.png"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Non_http_urls_are_refused()
    {
        await using var db = await TestDb.CreateAsync();
        var root = TempRoot();
        try
        {
            var svc = Create(db, root);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.SaveFromUrlAsync("file:///C:/Windows/System32/config/SAM"));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.SaveFromUrlAsync("ftp://example.com/x.png"));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.SaveFromUrlAsync("not a url"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Item_save_downloads_remote_thumbnail_and_localizes_it()
    {
        await using var db = await TestDb.CreateAsync();
        var root = TempRoot();
        try
        {
            var svc = Create(db, root);
            var inventory = new InventoryService(db.Factory, svc);

            using var img = new Image<Rgba32>(30, 30, new Rgba32(200, 100, 50));
            var png = await EncodeAsync(img, "png");
            var (port, serverTask) = ServeOnce(png, "image/png");

            var item = await inventory.SaveAsync(new Item
            {
                Name = "Remote-cover book",
                ItemKindId = 2,
                ThumbnailUrl = $"http://127.0.0.1:{port}/cover.png"
            });
            await serverTask;

            Assert.NotNull(item.ImageId);          // localized
            Assert.Null(item.ThumbnailUrl);        // no longer remote
            var persisted = await inventory.GetAsync(item.Id);
            Assert.Equal(item.ImageId, persisted!.ImageId);
            Assert.Null(persisted.ThumbnailUrl);
            Assert.NotNull(await svc.ReadOriginalBytesAsync(item.ImageId!.Value)); // on disk
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Failed_download_keeps_the_remote_url_fallback()
    {
        await using var db = await TestDb.CreateAsync();
        var root = TempRoot();
        try
        {
            var svc = Create(db, root);
            var inventory = new InventoryService(db.Factory, svc);

            var item = await inventory.SaveAsync(new Item
            {
                Name = "Unreachable cover",
                ItemKindId = 2,
                ThumbnailUrl = "http://127.0.0.1:1/nothing-here.png" // refused instantly
            });

            Assert.Null(item.ImageId);
            Assert.Equal("http://127.0.0.1:1/nothing-here.png", item.ThumbnailUrl); // graceful
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>Minimal one-shot HTTP server (raw socket — no URL ACLs needed on Windows).</summary>
    private static (int Port, Task Server) ServeOnce(byte[] body, string contentType)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var task = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var request = new byte[4096];
                await stream.ReadAtLeastAsync(request, minimumBytes: 1); // drain enough to receive the request
                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header);
                await stream.WriteAsync(body);
                await stream.FlushAsync();
            }
            finally { listener.Stop(); }
        });
        return (port, task);
    }
}
