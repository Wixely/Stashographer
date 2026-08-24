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
using Stashographer.Services.Lookup;

namespace Stashographer.Tests;

public class IntakeQueueTests
{
    [Fact]
    public async Task Automation_draft_waits_in_queue_for_human_acceptance()
    {
        await using var harness = await Harness.CreateAsync();
        var draft = new Item
        {
            Name = "Agent-proposed cable",
            ItemKindId = 4,
            Quantity = 2,
            LocationId = 3
        };

        var queued = await harness.Queue.EnqueueDraftAsync(draft);
        var reloaded = await harness.Queue.GetAsync(queued.Id);

        Assert.NotNull(reloaded);
        Assert.Equal(IntakeSourceType.Manual, reloaded!.SourceType);
        Assert.Equal(IntakeQueueStatus.ReadyForReview, reloaded.Status);
        Assert.Equal(IntakeAction.CreateNew, reloaded.ProposalAction);
        Assert.Equal("Agent-proposed cable", reloaded.Draft.Name);
        Assert.Empty(await harness.Inventory.QueryAsync(new ItemQuery(Search: "Agent-proposed cable")));
    }

    [Fact]
    public async Task Barcode_capture_is_persisted_then_review_accepts_it()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Lookup.Result = new LookupResult
        {
            Found = true,
            Code = "5012345678900",
            Name = "Test Cereal",
            SuggestedKind = "Grocery",
            Attributes = new() { ["Brand"] = "Example" }
        };

        var queued = await harness.Queue.EnqueueBarcodeAsync("5012345678900");
        Assert.Equal(IntakeQueueStatus.Pending, queued.Status);
        Assert.Single(await harness.Queue.GetOpenAsync());

        await harness.Queue.ProcessAsync(queued.Id, new IntakeOptions(), aiEnabled: false);
        var ready = await harness.Queue.GetAsync(queued.Id);
        Assert.NotNull(ready);
        Assert.True(ready!.Status == IntakeQueueStatus.ReadyForReview, ready.Error);
        Assert.Equal("Test Cereal", ready.Draft.Name);
        Assert.Equal(1, ready.Draft.ItemKindId);

        var applied = await harness.Queue.AcceptAsync(ready.Id, ready.Draft, null);
        Assert.Equal(IntakeAction.CreateNew, applied.Action);
        Assert.Equal("Test Cereal", (await harness.Inventory.GetAsync(applied.ItemId))!.Name);
        Assert.Empty(await harness.Queue.GetOpenAsync());
    }

    [Fact]
    public async Task Existing_barcode_is_proposed_as_increment_but_waits_for_acceptance()
    {
        await using var harness = await Harness.CreateAsync();
        var existing = await harness.Inventory.SaveAsync(new Item
        {
            Name = "Baked Beans", Code = "5000157024671", ItemKindId = 1, Quantity = 2
        });
        harness.Lookup.Result = new LookupResult
        {
            Found = true, Code = existing.Code, Name = existing.Name, SuggestedKind = "Grocery"
        };

        var queued = await harness.Queue.EnqueueBarcodeAsync(existing.Code!);
        await harness.Queue.ProcessAsync(queued.Id, new IntakeOptions(), aiEnabled: false);

        var ready = (await harness.Queue.GetAsync(queued.Id))!;
        Assert.True(ready.Status == IntakeQueueStatus.ReadyForReview, ready.Error);
        Assert.Equal(IntakeAction.IncrementExisting, ready.ProposalAction);
        Assert.Equal(existing.Id, ready.MatchedItemId);
        Assert.Equal(2, (await harness.Inventory.GetAsync(existing.Id))!.Quantity);

        await harness.Queue.AcceptAsync(ready.Id, ready.Draft, ready.MatchedItemId);
        Assert.Equal(3, (await harness.Inventory.GetAsync(existing.Id))!.Quantity);
    }

    [Fact]
    public async Task Split_barcode_requires_location_choice_instead_of_incrementing_arbitrarily()
    {
        await using var harness = await Harness.CreateAsync();
        var existing = await harness.Inventory.SaveAsync(new Item
        {
            Name = "Storage tubs", Code = "77777777", ItemKindId = 7, Quantity = 2, LocationId = 1
        });
        await harness.Inventory.SplitAsync(existing.Id, 1, 3, null);
        harness.Lookup.Result = new LookupResult
        {
            Found = true, Code = existing.Code, Name = existing.Name, SuggestedKind = "Other"
        };

        var queued = await harness.Queue.EnqueueBarcodeAsync(existing.Code!);
        await harness.Queue.ProcessAsync(queued.Id, new IntakeOptions(), aiEnabled: false);

        var ready = (await harness.Queue.GetAsync(queued.Id))!;
        Assert.Equal(IntakeAction.ChooseCandidate, ready.ProposalAction);
        Assert.Null(ready.MatchedItemId);
        Assert.All(await harness.Inventory.FindCandidatesAsync(existing.Name, existing.Code),
            candidate => Assert.Equal(1, candidate.Quantity));
    }

    [Fact]
    public async Task Photo_agent_receives_prior_session_kind_and_location_context()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Lookup.Result = new LookupResult
        {
            Found = true, Code = "1111111111111", Name = "Pasta", SuggestedKind = "Grocery"
        };
        var first = await harness.Queue.EnqueueBarcodeAsync("1111111111111");
        await harness.Queue.ProcessAsync(first.Id, new IntakeOptions(), aiEnabled: false);
        var firstReady = (await harness.Queue.GetAsync(first.Id))!;
        Assert.True(firstReady.Status == IntakeQueueStatus.ReadyForReview, firstReady.Error);
        firstReady.Draft.LocationId = 1;
        await harness.Queue.AcceptAsync(first.Id, firstReady.Draft, null);

        harness.Ai.Identification = new VisionIdentification { Name = "Tomato Sauce", Kind = "Grocery" };
        await using var photo = await PhotoAsync();
        var second = await harness.Queue.EnqueuePhotoAsync(photo, "image/png", "sauce.png");
        await harness.Queue.ProcessAsync(second.Id, new IntakeOptions(), aiEnabled: true);

        var secondReady = (await harness.Queue.GetAsync(second.Id))!;
        Assert.Equal(IntakeQueueStatus.ReadyForReview, secondReady.Status);
        Assert.Equal(1, secondReady.Draft.LocationId);
        Assert.Contains("kind Grocery", harness.Ai.LastIntakeContext);
        Assert.Contains("stored in Kitchen", harness.Ai.LastIntakeContext);
    }

    [Fact]
    public async Task Starting_new_session_resets_agent_context()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Lookup.Result = new LookupResult
        {
            Found = true, Code = "2222222222222", Name = "Hammer", SuggestedKind = "Tool"
        };
        var first = await harness.Queue.EnqueueBarcodeAsync("2222222222222");
        await harness.Queue.ProcessAsync(first.Id, new IntakeOptions(), aiEnabled: false);
        await harness.Queue.StartNewSessionAsync();

        harness.Ai.Identification = new VisionIdentification { Name = "Unknown object", Kind = "Other" };
        await using var photo = await PhotoAsync();
        var second = await harness.Queue.EnqueuePhotoAsync(photo, "image/png", "object.png");
        await harness.Queue.ProcessAsync(second.Id, new IntakeOptions(), aiEnabled: true);

        Assert.Null(harness.Ai.LastIntakeContext);
    }

    [Fact]
    public async Task Review_can_be_disabled_for_complete_high_confidence_results()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Lookup.Result = new LookupResult
        {
            Found = true, Code = "3333333333333", Name = "Quick Add", SuggestedKind = "Other"
        };
        var queued = await harness.Queue.EnqueueBarcodeAsync("3333333333333");

        await harness.Queue.ProcessAsync(queued.Id, new IntakeOptions { RequireReview = false }, aiEnabled: false);

        var completed = (await harness.Queue.GetAsync(queued.Id))!;
        Assert.Equal(IntakeQueueStatus.Accepted, completed.Status);
        Assert.NotNull(completed.AppliedItemId);
        Assert.Equal("Quick Add", (await harness.Inventory.GetAsync(completed.AppliedItemId!.Value))!.Name);
    }

    [Fact]
    public async Task Multi_item_photo_fans_out_to_individual_review_records()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Ai.Boxes = new List<DetectedBox>
        {
            new("left", 0, 0, 0.45, 1),
            new("right", 0.55, 0, 0.45, 1)
        };
        harness.Ai.Identification = new VisionIdentification { Name = "Detected item", Kind = "Other" };
        await using var photo = await PhotoAsync();
        var queued = await harness.Queue.EnqueuePhotoAsync(photo, "image/png", "several.png", multipleItems: true);

        await harness.Queue.ProcessAsync(queued.Id, new IntakeOptions(), aiEnabled: true);

        var open = await harness.Queue.GetOpenAsync();
        Assert.Equal(2, open.Count);
        Assert.All(open, x => Assert.Equal(IntakeQueueStatus.ReadyForReview, x.Status));
        Assert.All(open, x => Assert.False(x.IsMultiPhoto));
        Assert.All(open, x => Assert.Equal("Detected item", x.Draft.Name));
    }

    [Fact]
    public async Task Photo_capture_defaults_to_individual_item_splitting()
    {
        await using var harness = await Harness.CreateAsync();
        await using var photo = await PhotoAsync();

        var queued = await harness.Queue.EnqueuePhotoAsync(photo, "image/png", "several.png");

        Assert.True(queued.IsMultiPhoto);
        Assert.True((await harness.Queue.GetAsync(queued.Id))!.IsMultiPhoto);
    }

    [Fact]
    public async Task Queue_remembers_last_location_and_container_independently_per_session()
    {
        await using var harness = await Harness.CreateAsync();
        var session = await harness.Queue.GetCurrentSessionAsync();
        var first = await harness.Queue.EnqueueBarcodeAsync("4444444444444");
        await harness.Queue.AcceptAsync(first.Id, new Item
        {
            Name = "Loose item", ItemKindId = 7, LocationId = 1
        }, null);

        var box = await new ContainerService(harness.Db.Factory).SaveContainerAsync(new Container
        {
            Name = "Batch box", LocationId = 3
        });
        var second = await harness.Queue.EnqueueBarcodeAsync("5555555555555");
        await harness.Queue.AcceptAsync(second.Id, new Item
        {
            Name = "Boxed item", ItemKindId = 7, ContainerId = box.Id
        }, null);

        var remembered = await harness.Queue.GetRememberedDestinationsAsync(session.Id);
        Assert.Equal(1, remembered.LocationId);
        Assert.Equal(box.Id, remembered.ContainerId);

        var nextSession = await harness.Queue.StartNewSessionAsync();
        Assert.Equal(new RememberedDestinations(null, null),
            await harness.Queue.GetRememberedDestinationsAsync(nextSession.Id));
    }

    [Fact]
    public async Task Accepting_a_matched_item_applies_the_reviewed_destination()
    {
        await using var harness = await Harness.CreateAsync();
        var existing = await harness.Inventory.SaveAsync(new Item
        {
            Name = "Drill", Code = "6666666666666", ItemKindId = 3, LocationId = 3
        });
        var queued = await harness.Queue.EnqueueBarcodeAsync(existing.Code!);
        await harness.Queue.AcceptAsync(queued.Id, new Item
        {
            Name = existing.Name, Code = existing.Code, ItemKindId = 3, LocationId = 5
        }, existing.Id);

        var moved = await harness.Inventory.GetAsync(existing.Id);
        Assert.Equal(5, moved!.LocationId);
        Assert.Equal(2, moved.Quantity);
    }

    [Fact]
    public async Task Accepting_a_match_keeps_existing_price_and_adds_missing_expiry()
    {
        await using var harness = await Harness.CreateAsync();
        var existing = new Item { Name = "Coffee", ItemKindId = 1 };
        SpecialAttributeCatalog.SetPrice(existing, 8m, "GBP");
        await harness.Inventory.SaveAsync(existing);
        var queued = await harness.Queue.EnqueueBarcodeAsync("88888888");
        var draft = new Item { Name = existing.Name, ItemKindId = 1 };
        SpecialAttributeCatalog.SetPrice(draft, 6m, "GBP");
        SpecialAttributeCatalog.SetExpiry(draft, new DateOnly(2027, 1, 2), ExpiryDateKind.BestBefore);

        await harness.Queue.AcceptAsync(queued.Id, draft, existing.Id);
        var updated = await harness.Inventory.GetAsync(existing.Id);

        Assert.Equal(8m, SpecialAttributeCatalog.GetPrice(updated!)!.DecimalValue);
        Assert.Equal(new DateOnly(2027, 1, 2), updated!.ExpiryDate);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required TestDb Db { get; init; }
        public required string ImageRoot { get; init; }
        public required FakeAi Ai { get; init; }
        public required FakeLookup Lookup { get; init; }
        public required InventoryService Inventory { get; init; }
        public required IntakeQueueService Queue { get; init; }

        public static async Task<Harness> CreateAsync()
        {
            var db = await TestDb.CreateAsync();
            var imageRoot = Path.Combine(Path.GetTempPath(), $"stash_queue_{Guid.NewGuid():N}");
            var images = new ImageService(db.Factory, new ImageOptions { RootPath = imageRoot },
                new StubHostEnvironment(), new StubHttpFactory(), NullLogger<ImageService>.Instance);
            var ai = new FakeAi();
            var lookup = new FakeLookup();
            var inventory = new InventoryService(db.Factory);
            var photoIntake = new PhotoIntakeService(
                ai, inventory, images, NullLogger<PhotoIntakeService>.Instance);
            var queue = new IntakeQueueService(
                db.Factory, images, lookup, photoIntake, inventory, new IntakeQueueSignal(),
                NullLogger<IntakeQueueService>.Instance);
            return new Harness
            {
                Db = db,
                ImageRoot = imageRoot,
                Ai = ai,
                Lookup = lookup,
                Inventory = inventory,
                Queue = queue
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            try { if (Directory.Exists(ImageRoot)) Directory.Delete(ImageRoot, true); } catch { }
        }
    }

    private sealed class FakeLookup : ILookupRouter
    {
        public LookupResult Result { get; set; } = LookupResult.NotFound(string.Empty, "test");
        public Task<LookupResult> LookupAsync(string code, CancellationToken ct = default) => Task.FromResult(Result);
    }

    private sealed class FakeAi : IAiEnrichmentService
    {
        public bool IsEnabled => true;
        public VisionIdentification? Identification { get; set; }
        public List<DetectedBox> Boxes { get; set; } = new();
        public string? LastIntakeContext { get; private set; }

        public Task<VisionIdentification?> IdentifyItemAsync(
            byte[] image, string mediaType, IReadOnlyList<string> knownKinds,
            CancellationToken ct = default, string? intakeContext = null,
            AiRegionalContext? regionalContext = null)
        {
            LastIntakeContext = intakeContext;
            return Task.FromResult(Identification);
        }

        public Task<List<DetectedBox>> DetectItemsAsync(
            byte[] image, string mediaType, CancellationToken ct = default) =>
            Task.FromResult(Boxes);

        public Task<MatchPick?> PickMatchAsync(
            byte[] image, string mediaType, VisionIdentification identification,
            IReadOnlyList<MatchCandidate> candidates, CancellationToken ct = default) =>
            Task.FromResult<MatchPick?>(null);

        public Task<AiSuggestion?> EnrichAsync(
            string name, string? kind, IReadOnlyDictionary<string, string> known,
            CancellationToken ct = default) => Task.FromResult<AiSuggestion?>(null);

        public Task<AiBomSuggestion?> SuggestBomAsync(
            string request, BomKind kind, IReadOnlyList<AiBomInventoryItem> inventory,
            IReadOnlyList<string> canonicalAttributeNames, CancellationToken ct = default) =>
            Task.FromResult<AiBomSuggestion?>(null);

        public Task<string?> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
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

    private static async Task<MemoryStream> PhotoAsync()
    {
        using var image = new Image<Rgba32>(64, 64, new Rgba32(20, 80, 120));
        var stream = new MemoryStream();
        await image.SaveAsPngAsync(stream);
        stream.Position = 0;
        return stream;
    }
}
