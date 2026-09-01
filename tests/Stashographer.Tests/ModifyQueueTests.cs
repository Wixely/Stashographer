using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Stashographer.Data.Entities;
using Stashographer.Services.Ai;
using Stashographer.Services.Config;
using Stashographer.Services.Images;
using Stashographer.Services.Intake;
using Stashographer.Services.Inventory;
using Stashographer.Services.Modify;
using Stashographer.Components.Pages;

namespace Stashographer.Tests;

public sealed class ModifyQueueTests
{
    [Fact]
    public async Task Photo_is_durable_and_ai_match_waits_for_an_explicit_action()
    {
        await using var harness = await Harness.CreateAsync();
        var existing = await harness.Inventory.SaveAsync(new Item
        {
            Name = "Cordless drill", ItemKindId = 3, Quantity = 1, ContainerId = harness.ContainerOne
        });
        harness.Ai.Identification = new VisionIdentification { Name = existing.Name, Kind = "Tool" };
        await using var photo = await PhotoAsync();

        var queued = await harness.Queue.EnqueuePhotoAsync(photo, "image/png", "drill.png");
        Assert.Equal(ModifyQueueStatus.Pending, queued.Status);
        Assert.Equal(1, (await harness.Queue.GetCountsAsync()).Open);

        await harness.Queue.ProcessAsync(queued.Id, new ModifyOptions(), aiEnabled: true);
        var ready = (await harness.Queue.GetAsync(queued.Id))!;

        Assert.True(ready.Status == ModifyQueueStatus.ReadyForReview, ready.Error);
        Assert.Equal(existing.Id, ready.MatchedItemId);
        Assert.Equal(MatchConfidence.High, ready.MatchConfidence);
        Assert.Equal(1, (await harness.Inventory.GetAsync(existing.Id))!.Quantity);
    }

    [Fact]
    public async Task Multi_object_photo_creates_focused_reminders_and_retains_original()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Ai.Identification = new VisionIdentification { Name = "Unmatched object" };
        harness.Ai.Boxes =
        [
            new DetectedBox("left", 0.05, 0.1, 0.35, 0.7),
            new DetectedBox("right", 0.55, 0.1, 0.35, 0.7)
        ];
        await using var photo = await MultiPhotoAsync();

        var queued = await harness.Queue.EnqueuePhotoAsync(photo, "image/png", "box.png", true);
        await harness.Queue.ProcessAsync(queued.Id, new ModifyOptions(), aiEnabled: true);
        var reminders = await harness.Queue.GetOpenAsync();

        Assert.True(reminders.Count == 2, reminders[0].Error);
        Assert.All(reminders, reminder => Assert.Equal(queued.ImageId, reminder.OriginalImageId));
        Assert.All(reminders, reminder => Assert.NotEqual(reminder.OriginalImageId, reminder.ImageId));
        Assert.Equal(2, reminders.Select(reminder => reminder.ImageId).Distinct().Count());
    }

    [Fact]
    public async Task Working_container_prioritizes_the_exact_stock_entry_but_still_requires_review()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Inventory.SaveAsync(new Item
        {
            Name = "Packing tape", ItemKindId = 7, Quantity = 1,
            ContainerId = harness.ContainerOne
        });
        var expected = await harness.Inventory.SaveAsync(new Item
        {
            Name = "Packing tape", ItemKindId = 7, Quantity = 2,
            ContainerId = harness.ContainerTwo
        });
        await harness.Queue.SetWorkingPlaceAsync(null, harness.ContainerTwo);
        harness.Ai.Identification = new VisionIdentification { Name = "Packing tape" };
        await using var photo = await PhotoAsync(51);

        var queued = await harness.Queue.EnqueuePhotoAsync(photo, "image/png", "tape.png", false);
        await harness.Queue.ProcessAsync(queued.Id, new ModifyOptions(), aiEnabled: true);
        var ready = (await harness.Queue.GetAsync(queued.Id))!;

        Assert.Equal(expected.Id, ready.MatchedItemId);
        Assert.Equal(MatchConfidence.Medium, ready.MatchConfidence);
        Assert.Contains("working container", ready.MatchReason);
        Assert.Equal(2, (await harness.Inventory.GetAsync(expected.Id))!.Quantity);
    }

    [Fact]
    public async Task Decrement_is_applied_once_and_recorded_in_use_history()
    {
        await using var harness = await Harness.CreateAsync();
        var item = await harness.Inventory.SaveAsync(new Item
        {
            Name = "Cleaning cloths", ItemKindId = 7, Quantity = 5, Unit = "each"
        });
        item = (await harness.Inventory.GetAsync(item.Id))!;
        var queued = await harness.EnqueueReadyAsync("Cleaning cloths");

        var applied = await harness.Queue.ApplyAsync(queued.Id, item.Id,
            new ModifyActionRequest(
                ModifyAction.Decrement, 2, Description: "Used while clearing the box",
                ExpectedItemUpdatedAt: item.UpdatedAt.ToString("O")));

        Assert.Equal(3, (await harness.Inventory.GetAsync(item.Id))!.Quantity);
        Assert.NotNull(applied.ConsumptionEventId);
        var history = await harness.Consumption.GetForItemAsync(item.Id);
        Assert.Equal(applied.ConsumptionEventId, Assert.Single(history).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Queue.ApplyAsync(queued.Id, item.Id,
                new ModifyActionRequest(ModifyAction.Decrement, 2)));
        Assert.Equal(3, (await harness.Inventory.GetAsync(item.Id))!.Quantity);
    }

    [Fact]
    public async Task Moving_part_of_a_quantity_creates_a_linked_stock_entry()
    {
        await using var harness = await Harness.CreateAsync();
        var item = await harness.Inventory.SaveAsync(new Item
        {
            Name = "Cable ties", ItemKindId = 7, Quantity = 10, ContainerId = harness.ContainerOne
        });
        item = (await harness.Inventory.GetAsync(item.Id))!;
        var queued = await harness.EnqueueReadyAsync(item.Name);

        var applied = await harness.Queue.ApplyAsync(queued.Id, item.Id,
            new ModifyActionRequest(
                ModifyAction.Move, 4, ContainerId: harness.ContainerTwo,
                ExpectedItemUpdatedAt: item.UpdatedAt.ToString("O")));

        Assert.NotNull(applied.CreatedItemId);
        var source = (await harness.Inventory.GetAsync(item.Id))!;
        var moved = (await harness.Inventory.GetAsync(applied.CreatedItemId!.Value))!;
        Assert.Equal(6, source.Quantity);
        Assert.Equal(harness.ContainerOne, source.ContainerId);
        Assert.Equal(4, moved.Quantity);
        Assert.Equal(harness.ContainerTwo, moved.ContainerId);
        Assert.Equal(source.CollectionKey, moved.CollectionKey);
    }

    [Fact]
    public async Task Stale_item_state_must_be_reviewed_again()
    {
        await using var harness = await Harness.CreateAsync();
        var item = await harness.Inventory.SaveAsync(new Item
        {
            Name = "Storage labels", ItemKindId = 7, Quantity = 3
        });
        var queued = await harness.EnqueueReadyAsync(item.Name);
        var selectedVersion = item.UpdatedAt.ToString("O");
        await harness.Inventory.AdjustQuantityAsync(item.Id, 1);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Queue.ApplyAsync(queued.Id, item.Id,
                new ModifyActionRequest(
                    ModifyAction.Decrement, 1, ExpectedItemUpdatedAt: selectedVersion)));

        Assert.Contains("changed after it was selected", error.Message);
        Assert.Equal(4, (await harness.Inventory.GetAsync(item.Id))!.Quantity);
    }

    [Fact]
    public async Task Dismiss_completes_the_reminder_without_changing_inventory()
    {
        await using var harness = await Harness.CreateAsync();
        var item = await harness.Inventory.SaveAsync(new Item
        {
            Name = "Spare hooks", ItemKindId = 7, Quantity = 8
        });
        var queued = await harness.EnqueueReadyAsync(item.Name);

        await harness.Queue.DismissAsync(queued.Id);

        Assert.Empty(await harness.Queue.GetOpenAsync());
        Assert.Equal(8, (await harness.Inventory.GetAsync(item.Id))!.Quantity);
    }

    [Fact]
    public async Task Browser_upload_token_cannot_duplicate_a_modify_reminder()
    {
        await using var harness = await Harness.CreateAsync();
        var token = Guid.NewGuid().ToString();
        await using var firstPhoto = await PhotoAsync(41);
        await using var retryPhoto = await PhotoAsync(42);

        var first = await harness.Queue.EnqueuePhotoFromBrowserAsync(
            firstPhoto, "image/png", "first.png", true, token);
        var retry = await harness.Queue.EnqueuePhotoFromBrowserAsync(
            retryPhoto, "image/png", "retry.png", true, token);

        Assert.Equal(first.Id, retry.Id);
        Assert.Single(await harness.Queue.GetOpenAsync());
    }

    [Fact]
    public async Task Attach_photo_keeps_quantity_unchanged()
    {
        await using var harness = await Harness.CreateAsync();
        var item = await harness.Inventory.SaveAsync(new Item
        {
            Name = "Tool case", ItemKindId = 3, Quantity = 1
        });
        item = (await harness.Inventory.GetAsync(item.Id))!;
        var queued = await harness.EnqueueReadyAsync(item.Name);

        await harness.Queue.ApplyAsync(queued.Id, item.Id,
            new ModifyActionRequest(
                ModifyAction.AttachImage, ImageRole: ItemImageRole.Detail,
                ExpectedItemUpdatedAt: item.UpdatedAt.ToString("O")));

        Assert.Equal(1, (await harness.Inventory.GetAsync(item.Id))!.Quantity);
        var image = Assert.Single(await harness.Inventory.GetImagesAsync(item.Id));
        Assert.Equal(queued.ImageId, image.ImageId);
        Assert.Equal(ItemImageRole.Detail, image.Role);
    }

    [Fact]
    public async Task Delete_requires_an_explicit_action_and_completes_the_reminder()
    {
        await using var harness = await Harness.CreateAsync();
        var item = await harness.Inventory.SaveAsync(new Item
        {
            Name = "Broken basket", ItemKindId = 7, Quantity = 1
        });
        item = (await harness.Inventory.GetAsync(item.Id))!;
        var queued = await harness.EnqueueReadyAsync(item.Name);

        await harness.Queue.ApplyAsync(queued.Id, item.Id,
            new ModifyActionRequest(
                ModifyAction.Delete,
                ExpectedItemUpdatedAt: item.UpdatedAt.ToString("O")));

        Assert.Null(await harness.Inventory.GetAsync(item.Id));
        Assert.Empty(await harness.Queue.GetOpenAsync());
    }

    [Theory]
    [InlineData(null, 0, 2, 1)]
    [InlineData(null, 1, 2, 0)]
    [InlineData("intake", 0, 2, 0)]
    [InlineData("modify", 4, 0, 1)]
    public void Queues_default_to_modify_only_when_requested_or_intake_is_empty(
        string? requested, int intake, int modify, int expected) =>
        Assert.Equal(expected, QueueTabSelection.InitialIndex(requested, intake, modify));

    private sealed class Harness : IAsyncDisposable
    {
        public required TestDb Db { get; init; }
        public required string ImageRoot { get; init; }
        public required FakeAi Ai { get; init; }
        public required InventoryService Inventory { get; init; }
        public required ConsumptionService Consumption { get; init; }
        public required ModifyQueueService Queue { get; init; }
        public required int ContainerOne { get; init; }
        public required int ContainerTwo { get; init; }

        public static async Task<Harness> CreateAsync()
        {
            var db = await TestDb.CreateAsync();
            var imageRoot = Path.Combine(Path.GetTempPath(), $"stash_modify_{Guid.NewGuid():N}");
            var images = new ImageService(db.Factory, new ImageOptions { RootPath = imageRoot },
                new StubHostEnvironment(), new StubHttpFactory(), NullLogger<ImageService>.Instance);
            var ai = new FakeAi();
            var inventory = new InventoryService(db.Factory, images);
            var containers = new ContainerService(db.Factory);
            var location = await containers.SaveLocationAsync(new Location { Name = "Test room" });
            var containerOne = await containers.SaveContainerAsync(new Container
            {
                Name = "First box", ContainerType = ContainerType.Box, LocationId = location.Id
            });
            var containerTwo = await containers.SaveContainerAsync(new Container
            {
                Name = "Second box", ContainerType = ContainerType.Box, LocationId = location.Id
            });
            var consumption = new ConsumptionService(db.Factory);
            var photoIntake = new PhotoIntakeService(
                ai, inventory, images, NullLogger<PhotoIntakeService>.Instance);
            var queue = new ModifyQueueService(
                db.Factory, images, photoIntake, inventory, consumption,
                new IntakeQueueSignal(), NullLogger<ModifyQueueService>.Instance);
            return new Harness
            {
                Db = db,
                ImageRoot = imageRoot,
                Ai = ai,
                Inventory = inventory,
                Consumption = consumption,
                Queue = queue,
                ContainerOne = containerOne.Id,
                ContainerTwo = containerTwo.Id
            };
        }

        public async Task<ModifyQueueItem> EnqueueReadyAsync(string name)
        {
            Ai.Identification = new VisionIdentification { Name = name };
            await using var photo = await PhotoAsync(81);
            var queued = await Queue.EnqueuePhotoAsync(photo, "image/png", "modify.png", false);
            await Queue.ProcessAsync(queued.Id, new ModifyOptions(), aiEnabled: true);
            return (await Queue.GetAsync(queued.Id))!;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            try { if (Directory.Exists(ImageRoot)) Directory.Delete(ImageRoot, true); } catch { }
        }
    }

    private sealed class FakeAi : IAiEnrichmentService
    {
        public bool IsEnabled => true;
        public VisionIdentification? Identification { get; set; }
        public List<DetectedBox> Boxes { get; set; } = [];

        public Task<VisionIdentification?> IdentifyItemAsync(
            byte[] image, string mediaType, IReadOnlyList<string> knownKinds,
            CancellationToken ct = default, string? intakeContext = null,
            AiRegionalContext? regionalContext = null) => Task.FromResult(Identification);

        public Task<CaptureAnalysis> AnalyzeCaptureAsync(
            byte[] image, string mediaType, CancellationToken ct = default) =>
            Task.FromResult(new CaptureAnalysis(
                CaptureContentKind.InventoryItems, MatchConfidence.High, Boxes));

        public Task<MatchPick?> PickMatchAsync(
            byte[] image, string mediaType, VisionIdentification identification,
            IReadOnlyList<MatchCandidate> candidates, CancellationToken ct = default) =>
            Task.FromResult<MatchPick?>(null);

        public Task<CaptureRelationshipPick?> ClassifyCaptureRelationshipAsync(
            byte[] image, string mediaType, VisionIdentification identification,
            IReadOnlyList<CaptureMatchCandidate> recentCaptures,
            CancellationToken ct = default) => Task.FromResult<CaptureRelationshipPick?>(null);

        public Task<ReceiptExtraction?> ExtractReceiptAsync(
            byte[] image, string mediaType, IReadOnlyList<ReceiptMatchCandidate> candidates,
            AiRegionalContext regionalContext, CancellationToken ct = default) =>
            Task.FromResult<ReceiptExtraction?>(null);

        public Task<AiSuggestion?> EnrichAsync(
            string name, string? kind, IReadOnlyDictionary<string, string> known,
            CancellationToken ct = default) => Task.FromResult<AiSuggestion?>(null);

        public Task<AiBomSuggestion?> SuggestBomAsync(
            string request, BomKind kind, IReadOnlyList<AiBomInventoryItem> inventory,
            IReadOnlyList<string> canonicalAttributeNames, CancellationToken ct = default) =>
            Task.FromResult<AiBomSuggestion?>(null);

        public Task<AiMealPlanSuggestion?> SuggestMealPlanAsync(
            string? request, DateOnly startDate, int days,
            IReadOnlyList<AiMealPlanRecipe> recipes,
            IReadOnlyList<AiMealPlanInventoryItem> inventory,
            AiRegionalContext regionalContext, CancellationToken ct = default) =>
            Task.FromResult<AiMealPlanSuggestion?>(null);

        public Task<string?> TestConnectionAsync(CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class StubHostEnvironment : IHostEnvironment
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

    private static async Task<MemoryStream> PhotoAsync(byte seed = 20)
    {
        using var image = new Image<Rgba32>(96, 64, new Rgba32(seed, 80, 120));
        var stream = new MemoryStream();
        await image.SaveAsPngAsync(stream);
        stream.Position = 0;
        return stream;
    }

    private static async Task<MemoryStream> MultiPhotoAsync()
    {
        using var image = new Image<Rgba32>(96, 64);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    row[x] = x < row.Length / 2
                        ? new Rgba32(31, 80, 120)
                        : new Rgba32(180, 45, 70);
            }
        });
        var stream = new MemoryStream();
        await image.SaveAsPngAsync(stream);
        stream.Position = 0;
        return stream;
    }
}
