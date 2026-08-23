using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Stashographer.Data.Entities;
using Stashographer.Services.Ai;
using Stashographer.Services.Images;
using Stashographer.Services.Inventory;

namespace Stashographer.Tests;

public class PhotoIntakeTests
{
    // --- Test doubles ----------------------------------------------------------------

    private sealed class FakeAi : IAiEnrichmentService
    {
        public bool IsEnabled => true;
        public VisionIdentification? Identification { get; set; }
        public List<DetectedBox> Boxes { get; set; } = new();
        public MatchPick? Pick { get; set; }
        public int PickCalls { get; private set; }

        public Task<VisionIdentification?> IdentifyItemAsync(
            byte[] i, string m, IReadOnlyList<string> k,
            CancellationToken ct = default, string? intakeContext = null)
            => Task.FromResult(Identification);

        public Task<List<DetectedBox>> DetectItemsAsync(byte[] i, string m, CancellationToken ct = default)
            => Task.FromResult(Boxes);

        public Task<MatchPick?> PickMatchAsync(byte[] i, string m, VisionIdentification id,
            IReadOnlyList<MatchCandidate> c, CancellationToken ct = default)
        {
            PickCalls++;
            return Task.FromResult(Pick);
        }

        public Task<AiSuggestion?> EnrichAsync(string n, string? k, IReadOnlyDictionary<string, string> a, CancellationToken ct = default)
            => Task.FromResult<AiSuggestion?>(null);

        public Task<string?> TestConnectionAsync(CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

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

    private sealed class Harness : IAsyncDisposable
    {
        public required TestDb Db { get; init; }
        public required string ImageRoot { get; init; }
        public required FakeAi Ai { get; init; }
        public required InventoryService Inventory { get; init; }
        public required ImageService Images { get; init; }
        public required PhotoIntakeService Intake { get; init; }

        public static async Task<Harness> CreateAsync()
        {
            var db = await TestDb.CreateAsync();
            var root = Path.Combine(Path.GetTempPath(), $"stash_intake_{Guid.NewGuid():N}");
            var ai = new FakeAi();
            var inventory = new InventoryService(db.Factory);
            var images = new ImageService(db.Factory, new ImageOptions { RootPath = root },
                new StubHostEnv(), new StubHttpFactory(), NullLogger<ImageService>.Instance);
            var intake = new PhotoIntakeService(ai, inventory, images, NullLogger<PhotoIntakeService>.Instance);
            return new Harness { Db = db, ImageRoot = root, Ai = ai, Inventory = inventory, Images = images, Intake = intake };
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            try { if (Directory.Exists(ImageRoot)) Directory.Delete(ImageRoot, true); } catch { }
        }
    }

    private static async Task<MemoryStream> PhotoAsync(int w = 64, int h = 64, byte seed = 0)
    {
        using var img = new Image<Rgba32>(w, h, new Rgba32(seed, 128, 64));
        var ms = new MemoryStream();
        await img.SaveAsPngAsync(ms);
        ms.Position = 0;
        return ms;
    }

    private static async Task<MemoryStream> SplitPhotoAsync()
    {
        using var img = new Image<Rgba32>(200, 100, new Rgba32(220, 30, 30));
        for (var y = 0; y < img.Height; y++)
        for (var x = img.Width / 2; x < img.Width; x++)
            img[x, y] = new Rgba32(30, 30, 220);
        var ms = new MemoryStream();
        await img.SaveAsPngAsync(ms);
        ms.Position = 0;
        return ms;
    }

    // --- Single-item pipeline -------------------------------------------------------

    [Fact]
    public async Task Barcode_match_short_circuits_to_high_confidence_increment()
    {
        await using var h = await Harness.CreateAsync();
        var existing = await h.Inventory.SaveAsync(new Item { Name = "Baked Beans", Code = "5000157024671", ItemKindId = 1, Quantity = 2 });
        h.Ai.Identification = new VisionIdentification { Name = "Beans tin", Barcode = "5000157024671" };

        var result = await h.Intake.ProcessSingleAsync(await PhotoAsync(), "image/png");

        Assert.Equal(IntakeAction.IncrementExisting, result.Proposal.Action);
        Assert.Equal(existing.Id, result.Proposal.MatchedItemId);
        Assert.Equal(0, h.Ai.PickCalls); // model never consulted for the match
    }

    [Fact]
    public async Task Barcode_match_to_split_collection_requires_user_to_choose_a_place()
    {
        await using var h = await Harness.CreateAsync();
        var existing = await h.Inventory.SaveAsync(new Item
        {
            Name = "Baked Beans", Code = "5000157024671", ItemKindId = 1, Quantity = 2, LocationId = 1
        });
        await h.Inventory.SplitAsync(existing.Id, 1, 3, null);
        h.Ai.Identification = new VisionIdentification { Name = "Beans tin", Barcode = existing.Code };

        var result = await h.Intake.ProcessSingleAsync(await PhotoAsync(), "image/png");

        Assert.Equal(IntakeAction.ChooseCandidate, result.Proposal.Action);
        Assert.Null(result.Proposal.MatchedItemId);
        Assert.Equal(0, h.Ai.PickCalls);
    }

    [Fact]
    public async Task Exact_name_match_is_high_confidence_without_model_call()
    {
        await using var h = await Harness.CreateAsync();
        var existing = await h.Inventory.SaveAsync(new Item { Name = "Hammer", ItemKindId = 3, Quantity = 1 });
        h.Ai.Identification = new VisionIdentification { Name = "hammer" }; // case differs

        var result = await h.Intake.ProcessSingleAsync(await PhotoAsync(), "image/png");

        Assert.Equal(IntakeAction.IncrementExisting, result.Proposal.Action);
        Assert.Equal(existing.Id, result.Proposal.MatchedItemId);
        Assert.Equal(0, h.Ai.PickCalls);
    }

    [Fact]
    public async Task Ambiguous_candidates_ask_model_medium_becomes_picker()
    {
        await using var h = await Harness.CreateAsync();
        await h.Inventory.SaveAsync(new Item { Name = "Coke Can", ItemKindId = 1 });
        var bottle = await h.Inventory.SaveAsync(new Item { Name = "Coke Bottle", ItemKindId = 1 });
        h.Ai.Identification = new VisionIdentification { Name = "Coke Zero" };
        h.Ai.Pick = new MatchPick(bottle.Id, MatchConfidence.Medium);

        var result = await h.Intake.ProcessSingleAsync(await PhotoAsync(), "image/png");

        Assert.Equal(1, h.Ai.PickCalls);
        Assert.Equal(IntakeAction.ChooseCandidate, result.Proposal.Action);
        Assert.Equal(bottle.Id, result.Proposal.MatchedItemId);
        Assert.True(result.Candidates.Count >= 2);
    }

    [Fact]
    public async Task No_candidates_proposes_create_and_apply_creates_item_with_photo()
    {
        await using var h = await Harness.CreateAsync();
        h.Ai.Identification = new VisionIdentification
        {
            Name = "Cordless Screwdriver",
            Kind = "Tool",
            Description = "Compact electric screwdriver",
            Attributes = new() { ["Brand"] = "Bosch" }
        };

        var result = await h.Intake.ProcessSingleAsync(await PhotoAsync(), "image/png");
        Assert.Equal(IntakeAction.CreateNew, result.Proposal.Action);

        var applied = await h.Intake.ApplyAsync(result);
        Assert.Equal(IntakeAction.CreateNew, applied.Action);

        var created = await h.Inventory.GetAsync(applied.ItemId);
        Assert.NotNull(created);
        Assert.Equal("Cordless Screwdriver", created!.Name);
        Assert.Equal("Tool", created.Kind?.Name);
        Assert.Equal("Bosch", created.Attributes["Brand"]);
        Assert.Equal(result.ImageId, created.ImageId); // photo attached
    }

    [Fact]
    public async Task Single_item_photo_is_focus_cropped_from_ai_bounds_before_identification()
    {
        await using var h = await Harness.CreateAsync();
        h.Ai.Boxes = [new DetectedBox("item", 0.1, 0.2, 0.3, 0.6)];
        h.Ai.Identification = new VisionIdentification { Name = "Focused item", Kind = "Other" };

        var result = await h.Intake.ProcessSingleAsync(await SplitPhotoAsync(), "image/png");

        var crop = await h.Images.GetAsync(result.ImageId);
        Assert.NotNull(crop);
        Assert.InRange(crop!.Width!.Value, 60, 80);
        Assert.InRange(crop.Height!.Value, 60, 80);
        Assert.Equal(result.ImageId, result.Proposal.Draft.ImageId);
    }

    [Fact]
    public async Task Count_drives_increment_amount_and_undo_reverses_it()
    {
        await using var h = await Harness.CreateAsync();
        var existing = await h.Inventory.SaveAsync(new Item { Name = "Cola", Code = "5449000000996", ItemKindId = 1, Quantity = 2 });
        h.Ai.Identification = new VisionIdentification { Name = "Cola", Barcode = "5449000000996", Count = 3 };

        var result = await h.Intake.ProcessSingleAsync(await PhotoAsync(), "image/png");
        Assert.Equal(3, result.Proposal.IncrementBy);

        var applied = await h.Intake.ApplyAsync(result);
        Assert.Equal(5, (await h.Inventory.GetAsync(existing.Id))!.Quantity);

        await h.Intake.UndoAsync(applied);
        Assert.Equal(2, (await h.Inventory.GetAsync(existing.Id))!.Quantity);
    }

    [Fact]
    public async Task Low_or_no_pick_falls_back_to_create()
    {
        await using var h = await Harness.CreateAsync();
        await h.Inventory.SaveAsync(new Item { Name = "Coke Can", ItemKindId = 1 });
        await h.Inventory.SaveAsync(new Item { Name = "Coke Bottle", ItemKindId = 1 });
        h.Ai.Identification = new VisionIdentification { Name = "Coke Glass" };
        h.Ai.Pick = new MatchPick(null, MatchConfidence.Low);

        var result = await h.Intake.ProcessSingleAsync(await PhotoAsync(), "image/png");
        Assert.Equal(IntakeAction.CreateNew, result.Proposal.Action);
    }

    // --- Multi-item pipeline --------------------------------------------------------

    [Fact]
    public async Task Multi_detects_crops_and_processes_each_box()
    {
        await using var h = await Harness.CreateAsync();
        h.Ai.Boxes = new List<DetectedBox>
        {
            new("left thing", 0.05, 0.2, 0.3, 0.6),
            new("right thing", 0.65, 0.2, 0.3, 0.6)
        };
        h.Ai.Identification = new VisionIdentification { Name = "Mystery Gadget", Kind = "Other" };

        var results = await h.Intake.ProcessMultiAsync(await SplitPhotoAsync(), "image/png");

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(IntakeAction.CreateNew, r.Proposal.Action));
        Assert.Equal(2, results.Select(x => x.ImageId).Distinct().Count());
        foreach (var r in results)
        {
            var crop = await h.Images.GetAsync(r.ImageId);
            Assert.NotNull(crop);
            Assert.InRange(crop!.Width!.Value, 60, 80);
            Assert.InRange(crop.Height!.Value, 60, 80);
        }
    }

    [Fact]
    public void Multi_box_preparation_keeps_adjacent_objects_but_removes_detector_duplicates()
    {
        var boxes = PhotoIntakeService.PrepareDetectedBoxes([
            new("right copy", 0.55, 0.1, 0.35, 0.7),
            new("left copy duplicate", 0.101, 0.101, 0.349, 0.699),
            new("left copy", 0.1, 0.1, 0.35, 0.7)
        ]);

        Assert.Equal(2, boxes.Count);
        Assert.Equal("left copy", boxes[0].Label);
        Assert.Equal("right copy", boxes[1].Label);
    }

    [Fact]
    public async Task Multi_with_no_detections_falls_back_to_single_flow()
    {
        await using var h = await Harness.CreateAsync();
        h.Ai.Boxes = new List<DetectedBox>();
        h.Ai.Identification = new VisionIdentification { Name = "Lone Item" };

        var results = await h.Intake.ProcessMultiAsync(await PhotoAsync(), "image/png");

        Assert.Single(results);
        Assert.Equal("Lone Item", results[0].Proposal.Draft.Name);
    }
}
