using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Stashographer.Services.Images;

namespace Stashographer.Tests;

public class ImageServiceTests
{
    private sealed class StubHostEnv : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Test";
    }

    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static ImageService Create(TestDb db, string root) => new(
        db.Factory,
        new ImageOptions { RootPath = root },
        new StubHostEnv(),
        new StubHttpFactory(),
        NullLogger<ImageService>.Instance);

    private static async Task<byte[]> PngAsync(int width, int height)
    {
        using var img = new Image<Rgba32>(width, height);
        using var ms = new MemoryStream();
        await img.SaveAsPngAsync(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Save_stores_metadata_and_dedupes_identical_uploads()
    {
        await using var db = await TestDb.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), $"stash_img_{Guid.NewGuid():N}");
        try
        {
            var svc = Create(db, root);
            var bytes = await PngAsync(120, 60);

            var first = await svc.SaveAsync(new MemoryStream(bytes), "image/png", "a.png");
            Assert.True(first.Id > 0);
            Assert.Equal(120, first.Width);
            Assert.Equal(60, first.Height);
            Assert.Equal("image/png", first.ContentType);
            Assert.True(File.Exists(Path.Combine(root, "originals", first.StorageKey)));

            // Same content again → same record (deduped by hash), no second file.
            var second = await svc.SaveAsync(new MemoryStream(bytes), "image/png", "b.png");
            Assert.Equal(first.Id, second.Id);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Thumbnail_is_generated_capped_and_cached()
    {
        await using var db = await TestDb.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), $"stash_img_{Guid.NewGuid():N}");
        try
        {
            var svc = Create(db, root);
            var image = await svc.SaveAsync(new MemoryStream(await PngAsync(400, 200)), "image/png", "big.png");

            var thumb = await svc.GetThumbnailAsync(image.Id, 100);
            Assert.NotNull(thumb);

            using var decoded = Image.Load<Rgba32>(thumb!.Value.Bytes);
            Assert.Equal(100, decoded.Width);   // capped to requested width
            Assert.Equal(50, decoded.Height);   // aspect preserved

            // Cached on disk for reuse.
            Assert.True(File.Exists(Path.Combine(root, "thumbs", "100", image.StorageKey)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Crop_extracts_expected_region_and_stores_new_image()
    {
        await using var db = await TestDb.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), $"stash_img_{Guid.NewGuid():N}");
        try
        {
            var svc = Create(db, root);
            var source = await svc.SaveAsync(new MemoryStream(await PngAsync(100, 100)), "image/png", "src.png");

            // Center 50x50 box, no padding → exactly 50x50.
            var crop = await svc.CropAsync(source.Id, 0.25, 0.25, 0.5, 0.5, padding: 0);

            Assert.NotNull(crop);
            Assert.NotEqual(source.Id, crop!.Id);
            Assert.Equal(50, crop.Width);
            Assert.Equal(50, crop.Height);
            Assert.True(File.Exists(Path.Combine(root, "originals", crop.StorageKey)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Crop_clamps_overflowing_boxes_to_image_bounds()
    {
        await using var db = await TestDb.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), $"stash_img_{Guid.NewGuid():N}");
        try
        {
            var svc = Create(db, root);
            var source = await svc.SaveAsync(new MemoryStream(await PngAsync(100, 100)), "image/png", "src.png");

            // Box hangs off the bottom-right; padding pushes it further out.
            var crop = await svc.CropAsync(source.Id, 0.8, 0.8, 0.5, 0.5, padding: 0.1);

            Assert.NotNull(crop);
            Assert.InRange(crop!.Width!.Value, 1, 100);
            Assert.InRange(crop.Height!.Value, 1, 100);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Delete_removes_file_and_clears_item_reference()
    {
        await using var db = await TestDb.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), $"stash_img_{Guid.NewGuid():N}");
        try
        {
            var svc = Create(db, root);
            var inventory = new Stashographer.Services.Inventory.InventoryService(db.Factory);
            var image = await svc.SaveAsync(new MemoryStream(await PngAsync(80, 80)), "image/png", "i.png");
            var item = await inventory.SaveAsync(new Stashographer.Data.Entities.Item
            {
                Name = "Photographed", ItemKindId = 7, ImageId = image.Id
            });

            await svc.DeleteAsync(image.Id);

            Assert.Null(await svc.GetAsync(image.Id));
            Assert.False(File.Exists(Path.Combine(root, "originals", image.StorageKey)));
            Assert.Null((await inventory.GetAsync(item.Id))!.ImageId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
